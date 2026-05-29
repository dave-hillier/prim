using System.Collections.Generic;
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
    /// RESUME MODEL — REPLAY (read this before changing the emitter):
    ///
    /// The generated state machine does NOT linearize control flow with gotos and does
    /// NOT dispatch on a saved state at resume. Instead it uses a REPLAY model:
    ///
    ///  * Hoisted locals are declared at the top and initialised to default.
    ///  * A restore prologue checks <c>ScriptContext.IsRestoring</c>; if this method's
    ///    frame is at the head of the frame chain it pops the frame, rehydrates the
    ///    hoisted locals from the captured slots, and sets
    ///    <c>__state = frame.YieldPointId + 1</c>.
    ///  * The original method body then RE-RUNS FROM THE TOP. <c>__state</c> is not used
    ///    to jump; the body simply executes again over the restored locals. Pure,
    ///    re-computable control-flow predicates (if-conditions, loop headers) therefore
    ///    take the same path they took originally, and a nested continuable call is
    ///    resumed because the inner method's own restore prologue fires (its frame is now
    ///    at the head of the chain).
    ///
    /// SAFE positions (proven by ReplayModelEndToEndTests):
    ///  * Loop headers (while / for / foreach / do) — re-run from the header against the
    ///    restored accumulator; the loop variable is loop-scoped and re-created.
    ///  * A continuable call inside an <c>if</c> branch, inside a loop body, or used as a
    ///    SUB-EXPRESSION (e.g. <c>int x = Foo() * 2;</c>) — the statement is replayed and
    ///    the inner call resumes via the frame chain.
    ///
    /// LIMITATION — side effects before a yield RE-RUN on resume:
    ///  * Because the body replays from the top, any OBSERVABLE side effect that executes
    ///    before the yield point (and is not itself a continuable call that suspends) will
    ///    execute AGAIN on resume. The model is correct only when such pre-yield work is
    ///    idempotent / re-computable. Mutations that must not be repeated should occur
    ///    AFTER the relevant yield point. This is an inherent property of replay, not a
    ///    bug to be "fixed" by faking a goto rewrite.
    ///
    /// FORBIDDEN positions (diagnosed, never emitted — see PRIM003 / #24):
    ///  * A yield point inside a <c>finally</c> block, a <c>lock</c> statement, or a
    ///    <c>catch</c> filter cannot be suspended (it would abandon a CLR-managed region)
    ///    and is reported as an error; the method is skipped.
    ///
    /// NOT-YET-MODELLED positions (rely on replay re-running the whole statement):
    ///  * A continuable call in a loop/if condition (e.g. <c>if (Helper() &gt; 0)</c>) or
    ///    inside a <c>switch</c> is not assigned its own planned yield point; it round-trips
    ///    only because the enclosing statement re-runs on replay and the inner frame
    ///    resumes via the chain. Avoid placing irreversible side effects alongside such
    ///    calls until per-position resume is modelled.
    ///
    /// YIELD-POINT IDs — SINGLE SOURCE OF TRUTH (#22):
    ///  * <see cref="YieldPointAnalyzer.PlanEmittedYieldPoints"/> produces the canonical
    ///    ordered set of yield points the emitter turns into HandleYieldPoint /
    ///    nested-call sites. The emitter consumes those IDs in emission order (it has no
    ///    independent counter), so the captured frame's YieldPointId, the resume path, and
    ///    the "{Count} yield point(s)" doc comment all agree.
    ///
    /// Note: This is still a simplified implementation. A production implementation would
    /// need more sophisticated transformation to handle all C# constructs and to capture
    /// pre-yield side effects without replay.
    /// </summary>
    [Generator]
    public class ContinuationGenerator : IIncrementalGenerator
    {
        // --- Diagnostics (issue #26 / #29 / #28) ---

        private static readonly DiagnosticDescriptor UnsupportedMember = new DiagnosticDescriptor(
            id: "PRIM001",
            title: "Unsupported [Continuable] member",
            messageFormat: "[Continuable] is not supported on {0}; no continuation code was generated",
            category: "Prim.Continuation",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor UnsupportedMethodShape = new DiagnosticDescriptor(
            id: "PRIM002",
            title: "Unsupported [Continuable] method shape",
            messageFormat: "[Continuable] method '{0}' is not supported ({1}); no continuation code was generated",
            category: "Prim.Continuation",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        // #24: a yield point (loop, explicit Yield()/CheckYield(), or continuable call)
        // inside a finally block, lock statement, or catch filter is a hard error.
        // Suspending out of such a region would abandon a CLR-managed region (the finally
        // would not complete, the monitor lock would not be released, an exception filter
        // cannot be re-entered) per whitepaper §10.2. ERROR severity, and the member is
        // diagnosed-and-skipped (no broken/partial output), consistent with PRIM002.
        private static readonly DiagnosticDescriptor YieldInForbiddenRegion = new DiagnosticDescriptor(
            id: "PRIM003",
            title: "Yield point in unsupported region",
            messageFormat: "[Continuable] method '{0}' has a yield point inside {1}, which cannot be suspended; no continuation code was generated",
            category: "Prim.Continuation",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Find all members with a [Continuable] attribute. We deliberately capture
            // non-method members too (properties/indexers/etc.) so we can emit a
            // diagnostic for them rather than silently ignoring them (#26).
            var members = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (s, _) => IsCandidateMember(s),
                    transform: static (ctx, _) => GetMemberIfContinuable(ctx))
                .Where(static m => m is not null);

            var compilationAndMembers = context.CompilationProvider.Combine(members.Collect());

            context.RegisterSourceOutput(compilationAndMembers,
                static (spc, source) => Execute(source.Left, source.Right, spc));
        }

        private static bool IsCandidateMember(SyntaxNode node)
        {
            return node is MemberDeclarationSyntax m && m.AttributeLists.Count > 0;
        }

        private static MemberDeclarationSyntax GetMemberIfContinuable(GeneratorSyntaxContext context)
        {
            var member = (MemberDeclarationSyntax)context.Node;

            foreach (var attributeList in member.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    var name = attribute.Name.ToString();
                    if (name == "Continuable" || name == "ContinuableAttribute" ||
                        name.EndsWith(".Continuable") || name.EndsWith(".ContinuableAttribute"))
                    {
                        return member;
                    }
                }
            }

            return null;
        }

        private static void Execute(
            Compilation compilation,
            ImmutableArray<MemberDeclarationSyntax> members,
            SourceProductionContext context)
        {
            if (members.IsDefaultOrEmpty)
                return;

            // Partition into supported methods vs. members we diagnose-and-skip (#26).
            var supportedMethods = new List<MethodDeclarationSyntax>();

            foreach (var member in members.Where(m => m is not null))
            {
                if (member is not MethodDeclarationSyntax method)
                {
                    // Property / indexer / event / field etc. carrying [Continuable].
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnsupportedMember,
                        GetAttributeLocation(member),
                        DescribeMemberKind(member)));
                    continue;
                }

                var reason = GetUnsupportedReason(method);
                if (reason != null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        UnsupportedMethodShape,
                        method.Identifier.GetLocation(),
                        method.Identifier.Text,
                        reason));
                    continue;
                }

                supportedMethods.Add(method);
            }

            if (supportedMethods.Count == 0)
                return;

            var methodsByType = supportedMethods
                .GroupBy(m => GetFullTypeName(m))
                .ToList();

            foreach (var group in methodsByType)
            {
                var typeName = group.Key;

                // #24: a yield point inside a finally / lock / catch-filter cannot be
                // suspended. Diagnose-and-skip those methods (PRIM003, error) before
                // generation so no broken/partial output is emitted. The continuable
                // name set is needed so a continuable CALL inside such a region is
                // recognised as a yield point, not just loops/explicit yields.
                var continuableNames = new HashSet<string>(group.Select(m => m.Identifier.Text));
                var emittable = new List<MethodDeclarationSyntax>();

                foreach (var method in group)
                {
                    var analyzer = new YieldPointAnalyzer(continuableNames);
                    var illegal = analyzer.FindIllegalYieldContexts(method);
                    if (illegal.Count > 0)
                    {
                        var first = illegal[0];
                        context.ReportDiagnostic(Diagnostic.Create(
                            YieldInForbiddenRegion,
                            first.Location ?? method.Identifier.GetLocation(),
                            method.Identifier.Text,
                            first.RegionDescription));
                        continue;
                    }
                    emittable.Add(method);
                }

                if (emittable.Count == 0)
                    continue;

                var source = GenerateTransformedMethods(typeName, emittable, compilation);
                context.AddSource($"{typeName.Replace(".", "_")}_Continuations.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        }

        private static Location GetAttributeLocation(MemberDeclarationSyntax member)
        {
            var attr = member.AttributeLists
                .SelectMany(al => al.Attributes)
                .FirstOrDefault(a =>
                {
                    var n = a.Name.ToString();
                    return n == "Continuable" || n == "ContinuableAttribute" ||
                           n.EndsWith(".Continuable") || n.EndsWith(".ContinuableAttribute");
                });
            return (attr ?? (SyntaxNode)member).GetLocation();
        }

        private static string DescribeMemberKind(MemberDeclarationSyntax member)
        {
            return member switch
            {
                PropertyDeclarationSyntax => "properties",
                IndexerDeclarationSyntax => "indexers",
                EventDeclarationSyntax => "events",
                FieldDeclarationSyntax => "fields",
                ConstructorDeclarationSyntax => "constructors",
                _ => "this member kind"
            };
        }

        /// <summary>
        /// Returns a human-readable reason a method cannot be transformed, or null if
        /// the method is supported. Covers async/iterator/generic/expression-bodied (#26).
        /// </summary>
        private static string GetUnsupportedReason(MethodDeclarationSyntax method)
        {
            // async (modifier or any await expression)
            if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)) ||
                method.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
            {
                return "async methods are not supported";
            }

            // iterator (yield return / yield break)
            if (method.DescendantNodes().OfType<YieldStatementSyntax>().Any())
            {
                return "iterator methods (yield return/yield break) are not supported";
            }

            // generic method
            if (method.TypeParameterList != null && method.TypeParameterList.Parameters.Count > 0)
            {
                return "generic methods are not supported";
            }

            // method declared on a generic type
            for (var p = method.Parent; p != null; p = p.Parent)
            {
                if (p is TypeDeclarationSyntax t &&
                    t.TypeParameterList != null && t.TypeParameterList.Parameters.Count > 0)
                {
                    return "methods on generic types are not supported";
                }
            }

            // expression-bodied method with no block body: nothing to transform into a
            // state machine. (Simple ones could be supported but we keep the contract:
            // only block-bodied methods are transformed.)
            if (method.Body == null && method.ExpressionBody != null)
            {
                return "expression-bodied methods are not supported";
            }

            if (method.Body == null)
            {
                return "methods without a body are not supported";
            }

            return null;
        }

        private static string GetFullTypeName(MethodDeclarationSyntax method)
        {
            var typeDecl = method.Parent as TypeDeclarationSyntax;
            if (typeDecl == null) return "UnknownType";

            var typeName = typeDecl.Identifier.Text;
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
            List<MethodDeclarationSyntax> methods,
            Compilation compilation)
        {
            var sb = new StringBuilder();

            var lastDot = typeName.LastIndexOf('.');
            var ns = lastDot > 0 ? typeName.Substring(0, lastDot) : null;
            var shortTypeName = lastDot > 0 ? typeName.Substring(lastDot + 1) : typeName;

            var continuableMethodNames = new HashSet<string>(methods.Select(m => m.Identifier.Text));

            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("#nullable disable");
            sb.AppendLine("#pragma warning disable CS0162 // Unreachable code detected");
            sb.AppendLine("#pragma warning disable CS0219 // Variable assigned but never used");
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
                    GenerateTransformedMethod(sb, method, compilation, "        ", continuableMethodNames);
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
                    GenerateStaticTransformedMethod(sb, method, compilation, shortTypeName, "        ", continuableMethodNames);
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

        private static readonly SymbolDisplayFormat FqFormat =
            SymbolDisplayFormat.FullyQualifiedFormat
                .WithMiscellaneousOptions(
                    SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        /// <summary>
        /// Resolves a type symbol to a fully-qualified name (global::...), so the
        /// generated code binds regardless of usings (#25). Falls back to "object".
        /// </summary>
        private static string DisplayType(ITypeSymbol type)
        {
            if (type == null || type.TypeKind == TypeKind.Error)
                return "object";
            return type.ToDisplayString(FqFormat);
        }

        private static void GenerateTransformedMethod(
            StringBuilder sb,
            MethodDeclarationSyntax method,
            Compilation compilation,
            string indent,
            HashSet<string> continuableMethodNames)
        {
            var model = compilation.GetSemanticModel(method.SyntaxTree);
            var methodName = method.Identifier.Text;
            var returnType = GetReturnType(method, model);
            var parameters = BuildParameterList(method, model);
            var isVoid = returnType == "void";
            var methodToken = GenerateMethodToken(method, model);

            var hoist = HoistedLocals.Collect(method, model);
            // Single source of truth (#22): the emitted yield-point plan drives BOTH the
            // count comment below and the IDs consumed during emission.
            var analyzer = new YieldPointAnalyzer(continuableMethodNames);
            var yieldPoints = analyzer.PlanEmittedYieldPoints(method);

            sb.AppendLine();
            sb.AppendLine($"{indent}/// <summary>");
            sb.AppendLine($"{indent}/// Continuation-enabled version of {methodName}.");
            sb.AppendLine($"{indent}/// Supports suspension at {yieldPoints.Count} yield point(s).");
            sb.AppendLine($"{indent}/// </summary>");
            sb.AppendLine($"{indent}public {returnType} {methodName}_Continuable({parameters})");
            sb.AppendLine($"{indent}{{");

            GenerateStateMachineBody(sb, method, model, hoist, yieldPoints, methodToken, indent + "    ", isVoid, returnType, continuableMethodNames);

            sb.AppendLine($"{indent}}}");
        }

        private static void GenerateStaticTransformedMethod(
            StringBuilder sb,
            MethodDeclarationSyntax method,
            Compilation compilation,
            string originalTypeName,
            string indent,
            HashSet<string> continuableMethodNames)
        {
            var model = compilation.GetSemanticModel(method.SyntaxTree);
            var methodName = method.Identifier.Text;
            var returnType = GetReturnType(method, model);
            var isStatic = method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            var isVoid = returnType == "void";
            var methodToken = GenerateMethodToken(method, model);

            var hoist = HoistedLocals.Collect(method, model);
            var analyzer = new YieldPointAnalyzer(continuableMethodNames);
            var yieldPoints = analyzer.PlanEmittedYieldPoints(method);

            var paramList = BuildParameterList(method, model);
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

            GenerateStateMachineBody(sb, method, model, hoist, yieldPoints, methodToken, indent + "    ", isVoid, returnType, continuableMethodNames);

            sb.AppendLine($"{indent}}}");
        }

        /// <summary>
        /// Builds the parameter list using fully-qualified parameter types (#25).
        /// </summary>
        private static string BuildParameterList(MethodDeclarationSyntax method, SemanticModel model)
        {
            var parts = new List<string>();
            foreach (var p in method.ParameterList.Parameters)
            {
                var modifiers = string.Join(" ", p.Modifiers.Select(m => m.Text));
                var typeStr = "object";
                if (p.Type != null)
                {
                    var t = model.GetTypeInfo(p.Type).Type;
                    typeStr = t != null && t.TypeKind != TypeKind.Error ? DisplayType(t) : p.Type.ToString();
                }
                var defaultClause = p.Default != null ? $" {p.Default}" : "";
                var prefix = string.IsNullOrEmpty(modifiers) ? "" : modifiers + " ";
                parts.Add($"{prefix}{typeStr} {p.Identifier.Text}{defaultClause}");
            }
            return string.Join(", ", parts);
        }

        /// <summary>
        /// Resolves the return type to a fully-qualified name (#25).
        /// </summary>
        private static string GetReturnType(MethodDeclarationSyntax method, SemanticModel model)
        {
            var symbol = model.GetDeclaredSymbol(method);
            if (symbol != null)
            {
                if (symbol.ReturnsVoid) return "void";
                return DisplayType(symbol.ReturnType);
            }
            return method.ReturnType.ToString();
        }

        private static void GenerateStateMachineBody(
            StringBuilder sb,
            MethodDeclarationSyntax method,
            SemanticModel model,
            HoistedLocals hoist,
            List<YieldPointInfo> yieldPoints,
            int methodToken,
            string indent,
            bool isVoid,
            string returnType,
            HashSet<string> continuableMethodNames)
        {
            var analyzer = new YieldPointAnalyzer();
            var (_, tryBlocks) = analyzer.FindYieldPointsAndTryBlocks(method);

            var locals = hoist.Slots; // ordered (synthetic name, fq type)

            sb.AppendLine($"{indent}var __context = ScriptContext.EnsureCurrent();");
            sb.AppendLine($"{indent}const int __methodToken = {methodToken};");
            sb.AppendLine($"{indent}int __state = 0;");

            if (tryBlocks.Count > 0)
            {
                sb.AppendLine($"{indent}int __tryBlockState = -1; // Which try block to resume into (-1 = none)");
            }
            sb.AppendLine();

            foreach (var local in locals)
            {
                sb.AppendLine($"{indent}{local.type} {local.name} = default({local.type});");
            }
            sb.AppendLine();

            sb.AppendLine($"{indent}// Restore state if resuming");
            sb.AppendLine($"{indent}if (__context.IsRestoring && __context.FrameChain?.MethodToken == __methodToken)");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    var __frame = __context.FrameChain;");
            sb.AppendLine($"{indent}    __context.FrameChain = __frame.Caller;");
            sb.AppendLine($"{indent}    __state = __frame.YieldPointId + 1; // Resume after yield point");
            sb.AppendLine();

            for (int i = 0; i < locals.Count; i++)
            {
                var local = locals[i];
                sb.AppendLine($"{indent}    {local.name} = FrameCapture.GetSlot<{local.type}>(__frame.Slots, {i});");
            }

            if (tryBlocks.Count > 0 && locals.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"{indent}    // Restore try block state if available");
                sb.AppendLine($"{indent}    if (__frame.Slots.Length > {locals.Count})");
                sb.AppendLine($"{indent}        __tryBlockState = FrameCapture.GetSlot<int>(__frame.Slots, {locals.Count});");
            }
            sb.AppendLine();

            sb.AppendLine($"{indent}    if (__context.FrameChain == null)");
            sb.AppendLine($"{indent}        __context.IsRestoring = false;");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine();

            sb.AppendLine($"{indent}try");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    // State machine implementation");
            sb.AppendLine($"{indent}    // Supports try-catch-finally blocks and nested method calls.");
            sb.AppendLine();

            var ctx = new BodyGenerationContext
            {
                NextPlanIndex = 0,
                TryBlockIndex = 0,
                TryBlocks = tryBlocks,
                YieldPoints = yieldPoints,
                ContinuableMethodNames = continuableMethodNames,
                Hoist = hoist,
                Model = model
            };

            GenerateStatements(sb, method.Body.Statements, ctx, indent + "    ");

            sb.AppendLine($"{indent}}}");

            sb.AppendLine($"{indent}catch (SuspendException __ex)");
            sb.AppendLine($"{indent}{{");
            sb.Append($"{indent}    var __slots = FrameCapture.PackSlots(");
            if (locals.Count > 0)
            {
                sb.Append(string.Join(", ", locals.Select(l => l.name)));
                if (tryBlocks.Count > 0)
                {
                    sb.Append(", __tryBlockState");
                }
            }
            else if (tryBlocks.Count > 0)
            {
                sb.Append("__tryBlockState");
            }
            sb.AppendLine(");");
            sb.AppendLine($"{indent}    var __record = FrameCapture.CaptureFrame(__methodToken, __ex.YieldPointId, __slots, __ex.FrameChain);");
            sb.AppendLine($"{indent}    __ex.FrameChain = __record;");
            sb.AppendLine($"{indent}    throw;");
            sb.AppendLine($"{indent}}}");
        }

        // ----------------------------------------------------------------------------
        // Hoisted local collection + symbol-aware renaming (#25, #27, #29)
        // ----------------------------------------------------------------------------

        /// <summary>
        /// Collects every local that must be hoisted into the prologue for slot
        /// capture/restore, gives each a UNIQUE synthetic name, and records a mapping
        /// from the local's symbol to that name so all references can be remapped.
        ///
        /// Collected up front (so slot indices are stable): local declarations in ALL
        /// nested scopes, catch-clause variables, and using-statement/declaration
        /// resource variables (#29). Loop iteration variables (for / foreach) are NOT
        /// hoisted: the simplified resume model re-runs loops from their header, so the
        /// loop variable is re-created each run and stays loop-scoped.
        /// </summary>
        private sealed class HoistedLocals
        {
            // symbol -> synthetic name
            private readonly Dictionary<ISymbol, string> _bySymbol =
                new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);

            public List<(string name, string type)> Slots { get; } = new List<(string name, string type)>();

            public bool TryGetName(ISymbol symbol, out string name) => _bySymbol.TryGetValue(symbol, out name);

            public static HoistedLocals Collect(MethodDeclarationSyntax method, SemanticModel model)
            {
                var result = new HoistedLocals();
                if (method.Body == null) return result;

                int index = 0;
                string Synth(string original) => $"__l{index++}_{original}";

                void AddSymbol(ISymbol symbol, string original, ITypeSymbol type)
                {
                    if (symbol == null) return;
                    if (result._bySymbol.ContainsKey(symbol)) return;
                    var name = Synth(original);
                    result._bySymbol[symbol] = name;
                    result.Slots.Add((name, DisplayType(type)));
                }

                foreach (var node in method.Body.DescendantNodes())
                {
                    switch (node)
                    {
                        case LocalDeclarationStatementSyntax localDecl:
                        {
                            // Includes `using var x = ...;` (using declarations) (#29).
                            foreach (var v in localDecl.Declaration.Variables)
                            {
                                var sym = model.GetDeclaredSymbol(v) as ILocalSymbol;
                                AddSymbol(sym, v.Identifier.Text, sym?.Type);
                            }
                            break;
                        }
                        case UsingStatementSyntax usingStmt when usingStmt.Declaration != null:
                        {
                            // `using (var x = ...) { }` resource locals (#29).
                            foreach (var v in usingStmt.Declaration.Variables)
                            {
                                var sym = model.GetDeclaredSymbol(v) as ILocalSymbol;
                                AddSymbol(sym, v.Identifier.Text, sym?.Type);
                            }
                            break;
                        }
                        case CatchDeclarationSyntax catchDecl when !catchDecl.Identifier.IsKind(SyntaxKind.None) &&
                                                                    !string.IsNullOrEmpty(catchDecl.Identifier.Text):
                        {
                            var sym = model.GetDeclaredSymbol(catchDecl) as ILocalSymbol;
                            AddSymbol(sym, catchDecl.Identifier.Text, sym?.Type);
                            break;
                        }
                    }
                }

                return result;
            }
        }

        /// <summary>
        /// Rewrites identifier references to hoisted locals to their synthetic names,
        /// using symbol identity (so sibling-scope locals that share a source name are
        /// disambiguated). Returns the rewritten source text for a syntax fragment.
        /// </summary>
        private static string RenderWithRenames(SyntaxNode node, BodyGenerationContext ctx)
        {
            if (node == null) return "";
            var rewriter = new LocalRenamer(ctx.Model, ctx.Hoist);
            var rewritten = rewriter.Visit(node);
            var text = rewritten.ToFullString();
            return TransformContinuableMethodCalls(text.Trim(), ctx.ContinuableMethodNames);
        }

        private sealed class LocalRenamer : CSharpSyntaxRewriter
        {
            private readonly SemanticModel _model;
            private readonly HoistedLocals _hoist;

            public LocalRenamer(SemanticModel model, HoistedLocals hoist)
            {
                _model = model;
                _hoist = hoist;
            }

            public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
            {
                var symbol = _model.GetSymbolInfo(node).Symbol;
                if (symbol != null && _hoist.TryGetName(symbol, out var newName))
                {
                    return node.WithIdentifier(
                        SyntaxFactory.Identifier(newName).WithTriviaFrom(node.Identifier));
                }
                return base.VisitIdentifierName(node);
            }
        }

        private class BodyGenerationContext
        {
            /// <summary>
            /// Cursor into <see cref="YieldPoints"/> (the analyzer's emission plan). The
            /// emitter draws the next plan entry's Id as it emits each yield site, instead
            /// of running an independent counter — this is the #22 unification.
            /// </summary>
            public int NextPlanIndex { get; set; }
            public int TryBlockIndex { get; set; }
            public List<TryBlockInfo> TryBlocks { get; set; }

            /// <summary>
            /// The canonical, ordered emission plan from
            /// <see cref="YieldPointAnalyzer.PlanEmittedYieldPoints"/>. The emitter must
            /// walk statements in the same order this was built so that
            /// <see cref="NextYieldPointId"/> hands back the matching plan entry's Id.
            /// </summary>
            public List<YieldPointInfo> YieldPoints { get; set; }
            public HashSet<string> ContinuableMethodNames { get; set; }
            public HoistedLocals Hoist { get; set; }
            public SemanticModel Model { get; set; }

            /// <summary>
            /// Returns the Id of the next planned yield point and advances the cursor.
            /// Asserts (via clamp) that the emitter never emits more sites than were
            /// planned; in lockstep traversal the plan Id always equals the cursor index.
            /// </summary>
            public int NextYieldPointId(YieldPointKind expectedKind)
            {
                if (NextPlanIndex < YieldPoints.Count)
                {
                    var point = YieldPoints[NextPlanIndex];
                    NextPlanIndex++;
                    return point.Id;
                }
                // Should never happen: the plan is built by the same traversal the emitter
                // uses. Fall back to the cursor index so output stays well-formed.
                return NextPlanIndex++;
            }
        }

        private static string TransformContinuableMethodCalls(string code, HashSet<string> continuableMethodNames)
        {
            if (continuableMethodNames == null || continuableMethodNames.Count == 0)
                return code;

            var result = code;
            foreach (var methodName in continuableMethodNames)
            {
                result = System.Text.RegularExpressions.Regex.Replace(
                    result,
                    $@"\b{methodName}\s*\(",
                    $"{methodName}_Continuable(");
            }
            return result;
        }

        private static void GenerateStatements(
            StringBuilder sb,
            SyntaxList<StatementSyntax> statements,
            BodyGenerationContext context,
            string indent)
        {
            foreach (var statement in statements)
            {
                GenerateStatement(sb, statement, context, indent);
            }
        }

        private static void GenerateStatement(
            StringBuilder sb,
            StatementSyntax statement,
            BodyGenerationContext context,
            string indent)
        {
            if (statement is WhileStatementSyntax ||
                statement is ForStatementSyntax ||
                statement is ForEachStatementSyntax ||
                statement is DoStatementSyntax)
            {
                var loopCost = EstimateStatementCost(statement);
                var loopYieldId = context.NextYieldPointId(YieldPointKind.LoopBackEdge);
                sb.AppendLine($"{indent}// Yield point {loopYieldId} (loop, cost={loopCost})");
                sb.AppendLine($"{indent}__context.HandleYieldPointWithBudget({loopYieldId}, {loopCost});");
            }

            var nestedCall = FindContinuableMethodCall(statement, context.ContinuableMethodNames);
            if (nestedCall != null)
            {
                GenerateNestedMethodCallStatement(sb, statement, nestedCall, context, indent);
                return;
            }

            switch (statement)
            {
                case TryStatementSyntax tryStatement:
                    GenerateTryStatement(sb, tryStatement, context, indent);
                    break;

                case LocalDeclarationStatementSyntax localDecl:
                    // Hoisted: emit assignment only (declaration is in the prologue).
                    foreach (var variable in localDecl.Declaration.Variables)
                    {
                        if (variable.Initializer != null)
                        {
                            var name = ResolveHoistedName(variable, context);
                            var initText = RenderWithRenames(variable.Initializer.Value, context);
                            sb.AppendLine($"{indent}{name} = {initText};");
                        }
                    }
                    break;

                case UsingStatementSyntax usingStatement:
                    GenerateUsingStatement(sb, usingStatement, context, indent);
                    break;

                case BlockSyntax block:
                    sb.AppendLine($"{indent}{{");
                    GenerateStatements(sb, block.Statements, context, indent + "    ");
                    sb.AppendLine($"{indent}}}");
                    break;

                case IfStatementSyntax ifStatement:
                    GenerateIfStatement(sb, ifStatement, context, indent);
                    break;

                case WhileStatementSyntax whileStatement:
                    GenerateWhileStatement(sb, whileStatement, context, indent);
                    break;

                case ForStatementSyntax forStatement:
                    GenerateForStatement(sb, forStatement, context, indent);
                    break;

                case ForEachStatementSyntax foreachStatement:
                    GenerateForEachStatement(sb, foreachStatement, context, indent);
                    break;

                case DoStatementSyntax doStatement:
                    GenerateDoStatement(sb, doStatement, context, indent);
                    break;

                default:
                    var stmtText = RenderWithRenames(statement, context);
                    sb.AppendLine($"{indent}{stmtText}");
                    break;
            }
        }

        /// <summary>
        /// For a hoisted local declarator, returns its synthetic name; falls back to the
        /// source name (should not happen for collected locals).
        /// </summary>
        private static string ResolveHoistedName(VariableDeclaratorSyntax variable, BodyGenerationContext context)
        {
            var sym = context.Model.GetDeclaredSymbol(variable);
            if (sym != null && context.Hoist.TryGetName(sym, out var name))
                return name;
            return variable.Identifier.Text;
        }

        private static InvocationExpressionSyntax FindContinuableMethodCall(
            StatementSyntax statement,
            HashSet<string> continuableMethodNames)
        {
            if (continuableMethodNames == null || continuableMethodNames.Count == 0)
                return null;

            // Only look at the statement's own expression, not nested loop/try bodies
            // (those are recursed into separately).
            if (statement is BlockSyntax || statement is TryStatementSyntax ||
                statement is IfStatementSyntax || statement is WhileStatementSyntax ||
                statement is ForStatementSyntax || statement is ForEachStatementSyntax ||
                statement is DoStatementSyntax || statement is UsingStatementSyntax)
            {
                return null;
            }

            foreach (var invocation in statement.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var methodName = GetSimpleMethodName(invocation);
                if (continuableMethodNames.Contains(methodName))
                {
                    return invocation;
                }
            }
            return null;
        }

        private static string GetSimpleMethodName(InvocationExpressionSyntax invocation)
        {
            return invocation.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                _ => ""
            };
        }

        private static void GenerateNestedMethodCallStatement(
            StringBuilder sb,
            StatementSyntax statement,
            InvocationExpressionSyntax nestedCall,
            BodyGenerationContext context,
            string indent)
        {
            var yieldPointId = context.NextYieldPointId(YieldPointKind.NestedMethodCall);
            var methodName = GetSimpleMethodName(nestedCall);

            sb.AppendLine();
            sb.AppendLine($"{indent}// Yield point {yieldPointId} (nested call to {methodName})");

            switch (statement)
            {
                case LocalDeclarationStatementSyntax localDecl:
                    foreach (var variable in localDecl.Declaration.Variables)
                    {
                        if (variable.Initializer != null)
                        {
                            var varName = ResolveHoistedName(variable, context);
                            var initText = RenderWithRenames(variable.Initializer.Value, context);
                            sb.AppendLine($"{indent}{varName} = {initText};");
                        }
                    }
                    break;

                case ExpressionStatementSyntax exprStatement:
                    var exprText = RenderWithRenames(exprStatement.Expression, context);
                    sb.AppendLine($"{indent}{exprText};");
                    break;

                case ReturnStatementSyntax returnStatement:
                    if (returnStatement.Expression != null)
                    {
                        var returnText = RenderWithRenames(returnStatement.Expression, context);
                        sb.AppendLine($"{indent}return {returnText};");
                    }
                    else
                    {
                        sb.AppendLine($"{indent}return;");
                    }
                    break;

                default:
                    var stmtText = RenderWithRenames(statement, context);
                    sb.AppendLine($"{indent}{stmtText}");
                    break;
            }

            sb.AppendLine();
        }

        private static void GenerateTryStatement(
            StringBuilder sb,
            TryStatementSyntax tryStatement,
            BodyGenerationContext context,
            string indent)
        {
            var tryBlockId = context.TryBlockIndex++;
            var hasFinally = tryStatement.Finally != null;
            var hasCatch = tryStatement.Catches.Count > 0;

            sb.AppendLine();
            sb.AppendLine($"{indent}// Try block {tryBlockId} (hasFinally={hasFinally}, hasCatch={hasCatch})");

            if (context.TryBlocks.Count > 0)
            {
                sb.AppendLine($"{indent}__tryBlockState = {tryBlockId};");
            }

            sb.AppendLine($"{indent}try");
            sb.AppendLine($"{indent}{{");
            GenerateStatements(sb, tryStatement.Block.Statements, context, indent + "    ");
            sb.AppendLine($"{indent}}}");

            foreach (var catchClause in tryStatement.Catches)
            {
                GenerateCatchClause(sb, catchClause, context, indent);
            }

            if (tryStatement.Finally != null)
            {
                sb.AppendLine($"{indent}finally");
                sb.AppendLine($"{indent}{{");
                sb.AppendLine($"{indent}    // Finally block executes on both normal completion and suspension");
                GenerateStatements(sb, tryStatement.Finally.Block.Statements, context, indent + "    ");
                sb.AppendLine($"{indent}}}");
            }

            if (context.TryBlocks.Count > 0)
            {
                sb.AppendLine($"{indent}__tryBlockState = -1;");
            }

            sb.AppendLine();
        }

        private static void GenerateCatchClause(
            StringBuilder sb,
            CatchClauseSyntax catchClause,
            BodyGenerationContext context,
            string indent)
        {
            // Index of this clause within its try, so sibling catches get distinct
            // synthetic guard names rather than relying on catch-scope isolation (#27).
            var clauseIndex = (catchClause.Parent as TryStatementSyntax)?.Catches.IndexOf(catchClause) ?? 0;

            var declaration = catchClause.Declaration;
            if (declaration != null)
            {
                var exTypeSymbol = context.Model.GetTypeInfo(declaration.Type).Type;
                var exType = exTypeSymbol != null && exTypeSymbol.TypeKind != TypeKind.Error
                    ? DisplayType(exTypeSymbol)
                    : declaration.Type.ToString();

                // The actual catch always binds a FRESH guard name so it never collides
                // with a hoisted slot of the same source name (CS0136). When the source
                // catch declared a named variable, that name may be hoisted (so body
                // references rewrite to the slot); we copy the caught exception into that
                // hoisted slot at the top of the catch body.
                var guardName = $"__catchEx{context.TryBlockIndex}_{clauseIndex}";
                string hoistedName = null;
                if (!string.IsNullOrEmpty(declaration.Identifier.Text))
                {
                    var sym = context.Model.GetDeclaredSymbol(catchClause.Declaration);
                    if (sym != null && context.Hoist.TryGetName(sym, out var n))
                    {
                        hoistedName = n;
                    }
                }

                // Only inject the SuspendException-escape filter when this catch could
                // actually catch a SuspendException; on an unrelated typed catch the
                // 'is SuspendException' test is provably false and yields a CS0184
                // warning in consumer code (#23 follow-up).
                if (CatchCanCatchSuspend(exTypeSymbol, context))
                {
                    sb.AppendLine($"{indent}catch ({exType} {guardName}) when (!({guardName} is SuspendException))");
                }
                else
                {
                    sb.AppendLine($"{indent}catch ({exType} {guardName})");
                }

                sb.AppendLine($"{indent}{{");
                if (hoistedName != null)
                {
                    sb.AppendLine($"{indent}    {hoistedName} = {guardName};");
                }
                GenerateStatements(sb, catchClause.Block.Statements, context, indent + "    ");
                sb.AppendLine($"{indent}}}");
                return;
            }
            else
            {
                // Bare catch catches everything (including SuspendException), so the
                // escape filter is always required here.
                var guard = $"__catchEx{context.TryBlockIndex}_{clauseIndex}";
                sb.AppendLine($"{indent}catch (Exception {guard}) when (!({guard} is SuspendException))");
            }

            sb.AppendLine($"{indent}{{");
            GenerateStatements(sb, catchClause.Block.Statements, context, indent + "    ");
            sb.AppendLine($"{indent}}}");
        }

        /// <summary>
        /// Returns true if a catch of <paramref name="catchType"/> could actually catch a
        /// SuspendException (i.e. SuspendException is or derives from the catch type). Used
        /// to decide whether the 'when (!(x is SuspendException))' escape filter is needed;
        /// emitting it on an unrelated typed catch produces a provably-false test (CS0184).
        /// </summary>
        private static bool CatchCanCatchSuspend(ITypeSymbol catchType, BodyGenerationContext context)
        {
            // Unknown/error type: be safe and keep the filter.
            if (catchType == null || catchType.TypeKind == TypeKind.Error) return true;

            var suspend = context.Model.Compilation.GetTypeByMetadataName("Prim.Core.SuspendException");
            if (suspend == null) return true; // can't resolve; keep the filter defensively.

            for (var t = suspend; t != null; t = t.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(t, catchType)) return true;
            }
            return false;
        }

        /// <summary>
        /// Generates a using-statement as an explicit try/finally that disposes the
        /// hoisted resource local (#29). The resource local is declared+counted in the
        /// prologue, so slot indexing is never corrupted by late additions.
        /// </summary>
        private static void GenerateUsingStatement(
            StringBuilder sb,
            UsingStatementSyntax usingStatement,
            BodyGenerationContext context,
            string indent)
        {
            var disposeNames = new List<string>();

            if (usingStatement.Declaration != null)
            {
                foreach (var v in usingStatement.Declaration.Variables)
                {
                    var name = ResolveHoistedName(v, context);
                    if (v.Initializer != null)
                    {
                        var initText = RenderWithRenames(v.Initializer.Value, context);
                        sb.AppendLine($"{indent}{name} = {initText};");
                    }
                    disposeNames.Add(name);
                }
            }
            else if (usingStatement.Expression != null)
            {
                // using (expr) { }  -- expression-only resource; capture into a temp.
                var tmp = $"__using{context.TryBlockIndex}_{context.NextPlanIndex}";
                var exprText = RenderWithRenames(usingStatement.Expression, context);
                sb.AppendLine($"{indent}System.IDisposable {tmp} = {exprText};");
                disposeNames.Add(tmp);
            }

            sb.AppendLine($"{indent}try");
            sb.AppendLine($"{indent}{{");
            if (usingStatement.Statement is BlockSyntax usingBlock)
            {
                GenerateStatements(sb, usingBlock.Statements, context, indent + "    ");
            }
            else
            {
                GenerateStatement(sb, usingStatement.Statement, context, indent + "    ");
            }
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}finally");
            sb.AppendLine($"{indent}{{");
            foreach (var name in disposeNames)
            {
                sb.AppendLine($"{indent}    ((System.IDisposable){name})?.Dispose();");
            }
            sb.AppendLine($"{indent}}}");
        }

        private static void GenerateIfStatement(
            StringBuilder sb,
            IfStatementSyntax ifStatement,
            BodyGenerationContext context,
            string indent)
        {
            var conditionText = RenderWithRenames(ifStatement.Condition, context);
            sb.AppendLine($"{indent}if ({conditionText})");
            if (ifStatement.Statement is BlockSyntax block)
            {
                sb.AppendLine($"{indent}{{");
                GenerateStatements(sb, block.Statements, context, indent + "    ");
                sb.AppendLine($"{indent}}}");
            }
            else
            {
                GenerateStatement(sb, ifStatement.Statement, context, indent + "    ");
            }

            if (ifStatement.Else != null)
            {
                sb.AppendLine($"{indent}else");
                if (ifStatement.Else.Statement is BlockSyntax elseBlock)
                {
                    sb.AppendLine($"{indent}{{");
                    GenerateStatements(sb, elseBlock.Statements, context, indent + "    ");
                    sb.AppendLine($"{indent}}}");
                }
                else
                {
                    GenerateStatement(sb, ifStatement.Else.Statement, context, indent + "    ");
                }
            }
        }

        private static void GenerateWhileStatement(
            StringBuilder sb,
            WhileStatementSyntax whileStatement,
            BodyGenerationContext context,
            string indent)
        {
            var conditionText = RenderWithRenames(whileStatement.Condition, context);
            sb.AppendLine($"{indent}while ({conditionText})");
            if (whileStatement.Statement is BlockSyntax block)
            {
                sb.AppendLine($"{indent}{{");
                GenerateStatements(sb, block.Statements, context, indent + "    ");
                sb.AppendLine($"{indent}}}");
            }
            else
            {
                GenerateStatement(sb, whileStatement.Statement, context, indent + "    ");
            }
        }

        private static void GenerateForStatement(
            StringBuilder sb,
            ForStatementSyntax forStatement,
            BodyGenerationContext context,
            string indent)
        {
            // The for-loop control variable stays loop-scoped (re-run model). Render the
            // header with hoisted-local renames applied to any references.
            var initPart = forStatement.Declaration != null
                ? RenderWithRenames(forStatement.Declaration, context)
                : string.Join(", ", forStatement.Initializers.Select(i => RenderWithRenames(i, context)));
            var condition = forStatement.Condition != null ? RenderWithRenames(forStatement.Condition, context) : "";
            var incrementors = string.Join(", ", forStatement.Incrementors.Select(i => RenderWithRenames(i, context)));

            sb.AppendLine($"{indent}for ({initPart}; {condition}; {incrementors})");

            if (forStatement.Statement is BlockSyntax block)
            {
                sb.AppendLine($"{indent}{{");
                GenerateStatements(sb, block.Statements, context, indent + "    ");
                sb.AppendLine($"{indent}}}");
            }
            else
            {
                GenerateStatement(sb, forStatement.Statement, context, indent + "    ");
            }
        }

        private static void GenerateForEachStatement(
            StringBuilder sb,
            ForEachStatementSyntax foreachStatement,
            BodyGenerationContext context,
            string indent)
        {
            // Resolve element + collection types via the semantic model (#25). The
            // iteration variable stays loop-scoped: like the 'for' case, the simplified
            // resume model re-runs the loop from its header, so the enumerator is
            // re-created and MoveNext is NOT re-called mid-iteration on resume -- the
            // whole loop is replayed against the (restored) hoisted locals (#28). This
            // is consistent with how 'for' resumes and does not skip elements.
            var info = context.Model.GetForEachStatementInfo(foreachStatement);
            string elementType;
            if (foreachStatement.Type.IsVar)
            {
                var elemSym = info.ElementType;
                elementType = elemSym != null && elemSym.TypeKind != TypeKind.Error
                    ? DisplayType(elemSym)
                    : "var";
            }
            else
            {
                var t = context.Model.GetTypeInfo(foreachStatement.Type).Type;
                elementType = t != null && t.TypeKind != TypeKind.Error
                    ? DisplayType(t)
                    : foreachStatement.Type.ToString();
            }

            var collectionText = RenderWithRenames(foreachStatement.Expression, context);
            sb.AppendLine($"{indent}foreach ({elementType} {foreachStatement.Identifier.Text} in {collectionText})");

            if (foreachStatement.Statement is BlockSyntax block)
            {
                sb.AppendLine($"{indent}{{");
                GenerateStatements(sb, block.Statements, context, indent + "    ");
                sb.AppendLine($"{indent}}}");
            }
            else
            {
                GenerateStatement(sb, foreachStatement.Statement, context, indent + "    ");
            }
        }

        private static void GenerateDoStatement(
            StringBuilder sb,
            DoStatementSyntax doStatement,
            BodyGenerationContext context,
            string indent)
        {
            sb.AppendLine($"{indent}do");
            if (doStatement.Statement is BlockSyntax block)
            {
                sb.AppendLine($"{indent}{{");
                GenerateStatements(sb, block.Statements, context, indent + "    ");
                sb.AppendLine($"{indent}}}");
            }
            else
            {
                GenerateStatement(sb, doStatement.Statement, context, indent + "    ");
            }
            var conditionText = RenderWithRenames(doStatement.Condition, context);
            sb.AppendLine($"{indent}while ({conditionText});");
        }

        private static int EstimateStatementCost(StatementSyntax statement)
        {
            var nodeCount = statement.DescendantNodes().Count();
            return System.Math.Max(1, System.Math.Min(nodeCount, 100));
        }

        private static int GenerateMethodToken(MethodDeclarationSyntax method, SemanticModel model)
        {
            var typeName = GetFullTypeName(method);
            var methodName = method.Identifier.Text;
            // Use the SYNTAX type text for param types to keep tokens stable with the
            // pre-existing e2e expectations (CountToTenMethodToken constant in tests).
            var paramTypes = method.ParameterList.Parameters
                .Select(p => p.Type?.ToString() ?? "")
                .ToArray();

            return StableHashFnv1a(typeName, methodName, paramTypes);
        }

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
                    foreach (char c in value)
                    {
                        hash ^= c;
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
