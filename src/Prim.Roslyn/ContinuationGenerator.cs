using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Prim.Roslyn
{
    /// <summary>
    /// Source generator that transforms methods marked with [Continuable]
    /// to support suspension, state capture, and resumption.
    ///
    /// This generator transforms the method body into a state machine where:
    /// - Each yield point becomes a distinct state
    /// - Local variables are hoisted to persist across suspension
    /// - Control flow (loops, try-catch, switch) is converted to state transitions
    ///
    /// Supported constructs:
    /// - Loops: while, for, foreach, do-while
    /// - Exception handling: try-catch-finally (with limitations on yield in finally)
    /// - Switch statements and pattern matching
    /// - Nested method calls to other [Continuable] methods
    /// - Lock and using statements (with warnings for yield points)
    /// </summary>
    [Generator]
    public class ContinuationGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Find all methods with [Continuable] attribute
            var methodDeclarations = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (s, _) => IsCandidateMethod(s),
                    transform: static (ctx, _) => GetMethodIfContinuable(ctx))
                .Where(static m => m is not null);

            // Combine with compilation
            var compilationAndMethods = context.CompilationProvider.Combine(methodDeclarations.Collect());

            // Generate source
            context.RegisterSourceOutput(compilationAndMethods,
                static (spc, source) => Execute(source.Left, source.Right, spc));
        }

        private static bool IsCandidateMethod(SyntaxNode node)
        {
            return node is MethodDeclarationSyntax m && m.AttributeLists.Count > 0;
        }

        private static MethodDeclarationSyntax GetMethodIfContinuable(GeneratorSyntaxContext context)
        {
            var methodSyntax = (MethodDeclarationSyntax)context.Node;

            foreach (var attributeList in methodSyntax.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    var name = attribute.Name.ToString();
                    if (name == "Continuable" || name == "ContinuableAttribute" ||
                        name.EndsWith(".Continuable") || name.EndsWith(".ContinuableAttribute"))
                    {
                        return methodSyntax;
                    }
                }
            }

            return null;
        }

        private static void Execute(
            Compilation compilation,
            ImmutableArray<MethodDeclarationSyntax> methods,
            SourceProductionContext context)
        {
            if (methods.IsDefaultOrEmpty)
                return;

            var methodsByType = methods
                .Where(m => m is not null)
                .GroupBy(m => GetFullTypeName(m))
                .ToList();

            foreach (var group in methodsByType)
            {
                var typeName = group.Key;
                var source = GenerateTransformedMethods(typeName, group.ToList(), compilation);
                context.AddSource($"{typeName.Replace(".", "_")}_Continuations.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        }

        private static string GetFullTypeName(MethodDeclarationSyntax method)
        {
            var typeDecl = method.Parent as TypeDeclarationSyntax;
            if (typeDecl == null) return "UnknownType";

            // Walk all parent type declarations to handle nested types
            var typeNames = new System.Collections.Generic.List<string>();
            var current = typeDecl;
            while (current != null)
            {
                typeNames.Insert(0, current.Identifier.Text);
                current = current.Parent as TypeDeclarationSyntax;
            }
            var typeName = string.Join(".", typeNames);

            var ns = GetNamespace(typeDecl);

            return string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
        }

        private static string GetNamespace(SyntaxNode node)
        {
            var ns = node.Ancestors()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .FirstOrDefault();

            return ns?.Name.ToString() ?? "";
        }

        private static string GenerateTransformedMethods(
            string typeName,
            System.Collections.Generic.List<MethodDeclarationSyntax> methods,
            Compilation compilation)
        {
            var sb = new StringBuilder();

            var lastDot = typeName.LastIndexOf('.');
            var ns = lastDot > 0 ? typeName.Substring(0, lastDot) : null;
            var shortTypeName = lastDot > 0 ? typeName.Substring(lastDot + 1) : typeName;

            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("// This file was generated by Prim.Roslyn ContinuationGenerator");
            sb.AppendLine("// Supports: loops, try-catch-finally, switch, pattern matching, nested calls");
            sb.AppendLine("#nullable disable");
            sb.AppendLine("#pragma warning disable CS0162 // Unreachable code detected");
            sb.AppendLine("#pragma warning disable CS0164 // This label has not been referenced");
            sb.AppendLine("#pragma warning disable CS8321 // Local function is declared but never used");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine("using Prim.Core;");
            sb.AppendLine("using Prim.Runtime;");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(ns))
            {
                sb.AppendLine($"namespace {ns}");
                sb.AppendLine("{");
            }

            var firstMethod = methods.FirstOrDefault();
            var typeDecl = firstMethod?.Parent as TypeDeclarationSyntax;
            var isPartial = typeDecl?.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)) ?? false;

            if (isPartial)
            {
                var modifiers = GetTypeModifiers(typeDecl);
                sb.AppendLine($"    {modifiers}partial class {shortTypeName}");
                sb.AppendLine("    {");

                foreach (var method in methods)
                {
                    GenerateTransformedMethod(sb, method, compilation, "        ");
                }

                sb.AppendLine("    }");
            }
            else
            {
                sb.AppendLine($"    /// <summary>");
                sb.AppendLine($"    /// Continuation-enabled versions of {shortTypeName} methods.");
                sb.AppendLine($"    /// </summary>");
                sb.AppendLine($"    public static class {shortTypeName}Continuations");
                sb.AppendLine("    {");

                foreach (var method in methods)
                {
                    GenerateStaticTransformedMethod(sb, method, shortTypeName, compilation, "        ");
                }

                sb.AppendLine("    }");
            }

            if (!string.IsNullOrEmpty(ns))
            {
                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        private static string GetTypeModifiers(TypeDeclarationSyntax typeDecl)
        {
            var sb = new StringBuilder();
            foreach (var mod in typeDecl.Modifiers)
            {
                if (mod.IsKind(SyntaxKind.PartialKeyword)) continue;
                sb.Append(mod.Text);
                sb.Append(' ');
            }
            return sb.ToString();
        }

        private static void GenerateTransformedMethod(StringBuilder sb, MethodDeclarationSyntax method, Compilation compilation, string indent)
        {
            var methodName = method.Identifier.Text;
            var returnType = method.ReturnType.ToString();
            var parameters = method.ParameterList.ToString();
            var isVoid = returnType == "void";
            var methodToken = GenerateMethodToken(method);

            var analyzer = new YieldPointAnalyzer();
            var yieldPoints = analyzer.FindYieldPoints(method);

            sb.AppendLine();
            sb.AppendLine($"{indent}/// <summary>");
            sb.AppendLine($"{indent}/// Continuation-enabled version of {methodName}.");
            sb.AppendLine($"{indent}/// Supports suspension at {yieldPoints.Count} yield point(s).");
            sb.AppendLine($"{indent}/// </summary>");

            // Copy method modifiers except async
            var modifiers = method.Modifiers
                .Where(m => !m.IsKind(SyntaxKind.AsyncKeyword))
                .Select(m => m.Text);
            var modString = string.Join(" ", modifiers);
            if (!string.IsNullOrEmpty(modString)) modString += " ";

            sb.AppendLine($"{indent}{modString}{returnType} {methodName}_Continuable{parameters}");
            sb.AppendLine($"{indent}{{");

            // Use the StateMachineRewriter for complex transformation
            var semanticModel = compilation.GetSemanticModel(method.SyntaxTree);
            var rewriter = new StateMachineRewriter(method, semanticModel, methodToken);
            var body = rewriter.GenerateStateMachineBody(returnType, isVoid);

            // Indent the generated body
            foreach (var line in body.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    sb.AppendLine($"{indent}    {line.TrimEnd()}");
                }
                else
                {
                    sb.AppendLine();
                }
            }

            sb.AppendLine($"{indent}}}");
        }

        private static void GenerateStaticTransformedMethod(StringBuilder sb, MethodDeclarationSyntax method, string originalTypeName, Compilation compilation, string indent)
        {
            var methodName = method.Identifier.Text;
            var returnType = method.ReturnType.ToString();
            var isStatic = method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            var isVoid = returnType == "void";
            var methodToken = GenerateMethodToken(method);

            var analyzer = new YieldPointAnalyzer();
            var yieldPoints = analyzer.FindYieldPoints(method);

            var paramList = method.ParameterList.Parameters.ToString();
            if (!isStatic)
            {
                paramList = string.IsNullOrWhiteSpace(paramList)
                    ? $"{originalTypeName} instance"
                    : $"{originalTypeName} instance, {paramList}";
            }

            sb.AppendLine();
            sb.AppendLine($"{indent}/// <summary>");
            sb.AppendLine($"{indent}/// Continuation-enabled wrapper for {originalTypeName}.{methodName}.");
            sb.AppendLine($"{indent}/// Supports suspension at {yieldPoints.Count} yield point(s).");
            sb.AppendLine($"{indent}/// </summary>");
            sb.AppendLine($"{indent}public static {returnType} {methodName}_Continuable({paramList})");
            sb.AppendLine($"{indent}{{");

            // Use the StateMachineRewriter for complex transformation
            var semanticModel = compilation.GetSemanticModel(method.SyntaxTree);
            var rewriter = new StateMachineRewriter(method, semanticModel, methodToken);
            var body = rewriter.GenerateStateMachineBody(returnType, isVoid);

            // Indent the generated body
            foreach (var line in body.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    sb.AppendLine($"{indent}    {line.TrimEnd()}");
                }
                else
                {
                    sb.AppendLine();
                }
            }

            sb.AppendLine($"{indent}}}");
        }

        private static int GenerateMethodToken(MethodDeclarationSyntax method)
        {
            var typeName = GetFullTypeName(method);
            var methodName = method.Identifier.Text;
            var paramTypes = method.ParameterList.Parameters
                .Select(p => p.Type?.ToString() ?? "")
                .ToArray();

            return StableHashFnv1a(typeName, methodName, paramTypes);
        }

        /// <summary>
        /// Computes a stable FNV-1a hash for method identification.
        /// This must match StableHash.GenerateMethodToken in Prim.Core.
        /// </summary>
        private static int StableHashFnv1a(string typeName, string methodName, string[] paramTypes)
        {
            unchecked
            {
                const uint fnvPrime = 16777619;
                const uint fnvOffsetBasis = 2166136261;

                int ComputeStringHash(string value)
                {
                    if (value == null) return 0;
                    uint hash = fnvOffsetBasis;
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
                    foreach (byte b in bytes)
                    {
                        hash ^= b;
                        hash *= fnvPrime;
                    }
                    return (int)hash;
                }

                int Combine(params int[] hashes)
                {
                    int hash = 17;
                    foreach (var h in hashes)
                    {
                        hash = ((hash << 5) + hash) ^ h;
                    }
                    return hash;
                }

                var typeHash = ComputeStringHash(typeName);
                var methodHash = ComputeStringHash(methodName);

                if (paramTypes == null || paramTypes.Length == 0)
                {
                    return Combine(typeHash, methodHash);
                }

                var hashes = new int[paramTypes.Length + 2];
                hashes[0] = typeHash;
                hashes[1] = methodHash;
                for (int i = 0; i < paramTypes.Length; i++)
                {
                    hashes[i + 2] = ComputeStringHash(paramTypes[i]);
                }

                return Combine(hashes);
            }
        }
    }
}
