// Written without nullable annotations. Prim.Tests.Roslyn compiles this file
// directly with nullable enabled, so state the context explicitly.
#nullable disable

using System;
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
    /// The generated method does NOT linearize control flow with gotos and does NOT
    /// jump to the yield point on resume. It uses a REPLAY model:
    ///
    ///  * Hoisted locals are declared at the top and initialised to default.
    ///  * A restore prologue checks <c>ScriptContext.IsRestoring</c>; if this method's
    ///    frame is at the head of the frame chain it pops the frame, rehydrates the
    ///    hoisted locals from the captured slots and reads back the replay counter.
    ///  * The original method body then RE-RUNS FROM THE TOP over the restored locals.
    ///    Code before the yield point runs again, so it must be deterministic and free
    ///    of side effects that must not repeat.
    ///
    /// REPLAY COUNTER. Loops are checked at the start of every iteration, so a resumed
    /// method that simply replayed would spend its budget re-running the iterations it
    /// already did and suspend again at (or before) the same place, forever. To prevent
    /// that, every emitted yield site (each loop iteration check and each statement
    /// with a nested continuable call) counts itself on a per-ScriptContext counter
    /// (the generated <c>Prim.Generated.ContinuationReplay_*</c> helper), which also
    /// counts outermost generated frames entered. Both counts are captured as the last
    /// two slots of each frame. On resume, yield checks neither suspend nor spend budget
    /// until the replay reaches the same (entry, site) pair; there the replay ends
    /// (IsRestoring is cleared) and execution continues normally. Because replay is
    /// deterministic, that is exactly the site the run suspended at, whichever frame
    /// record each call consumed on the way, so every resume gets past its suspension
    /// point with a full budget. Outside a runner the generated code uses a private
    /// context with an unlimited budget, so direct calls never suspend and never leave
    /// a thread context behind for a later run to reuse.
    ///
    /// FORBIDDEN positions (diagnosed, never emitted — PRIM003):
    ///  * A yield point inside a <c>finally</c> block, a <c>lock</c> statement, or a
    ///    <c>catch</c> filter.
    ///
    /// NOT-YET-MODELLED positions:
    ///  * A continuable call in an if/loop/switch/using/foreach header (e.g.
    ///    <c>if (Helper() &gt; 0)</c>) is rewritten but is not a yield site of its own;
    ///    it resumes because the enclosing statement replays. Frame records are matched
    ///    to calls by method token only, so with several calls to the same method a
    ///    record can be restored into the wrong invocation; restored values are always
    ///    overwritten by the replay before they are read, so this does not change
    ///    results.
    ///
    /// YIELD-POINT IDs — SINGLE SOURCE OF TRUTH (#22):
    ///  * <see cref="YieldPointAnalyzer.PlanEmittedYieldPoints"/> produces the canonical
    ///    ordered set of yield points. The emitter consumes those IDs in emission order
    ///    (it has no independent counter), and both use the same predicate to decide
    ///    which statements hold a nested continuable call.
    /// </summary>
    [Generator]
    public class ContinuationGenerator : IIncrementalGenerator
    {
        private const string ContinuableAttributeMetadataName = "Prim.Core.ContinuableAttribute";

        // --- Diagnostics ---

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

        // A yield point (loop or continuable call) inside a finally block, lock
        // statement, or catch filter is a hard error: suspending out of such a region
        // would abandon a CLR-managed region (whitepaper §10.2).
        private static readonly DiagnosticDescriptor YieldInForbiddenRegion = new DiagnosticDescriptor(
            id: "PRIM003",
            title: "Yield point in unsupported region",
            messageFormat: "[Continuable] method '{0}' has a yield point inside {1}, which cannot be suspended; no continuation code was generated",
            category: "Prim.Continuation",
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        // The body uses something the generated copy cannot reproduce (for example a
        // private member when the type is not partial, so the copy lives in another
        // class). Reported instead of emitting code that does not compile.
        private static readonly DiagnosticDescriptor UnsupportedBody = new DiagnosticDescriptor(
            id: "PRIM004",
            title: "Unsupported construct in [Continuable] method body",
            messageFormat: "[Continuable] method '{0}' cannot be transformed: {1}; no continuation code was generated",
            category: "Prim.Continuation",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Members carrying Prim.Core.ContinuableAttribute, matched by symbol rather
            // than by attribute text. Non-method members are kept so they can be
            // diagnosed (PRIM001) rather than silently ignored.
            var members = context.SyntaxProvider
                .ForAttributeWithMetadataName(
                    ContinuableAttributeMetadataName,
                    predicate: static (node, _) =>
                        node is MemberDeclarationSyntax ||
                        node is VariableDeclaratorSyntax ||
                        node is LocalFunctionStatementSyntax,
                    transform: static (ctx, _) => ctx.TargetNode);

            // Emission needs semantic models for the whole type (continuable callees,
            // accessibility, hoisted types), so the members are combined with the
            // compilation.
            var compilationAndMembers = context.CompilationProvider.Combine(members.Collect());

            context.RegisterSourceOutput(compilationAndMembers,
                static (spc, source) => Execute(source.Left, source.Right, spc));
        }

        private sealed class Candidate
        {
            public MethodDeclarationSyntax Syntax;
            public IMethodSymbol Symbol;
            public SemanticModel Model;
        }

        private static void Execute(
            Compilation compilation,
            ImmutableArray<SyntaxNode> nodes,
            SourceProductionContext context)
        {
            if (nodes.IsDefaultOrEmpty)
                return;

            var candidates = new List<Candidate>();
            var seen = new HashSet<SyntaxNode>();

            foreach (var node in nodes)
            {
                if (node == null) continue;

                if (node is MethodDeclarationSyntax method)
                {
                    var model = compilation.GetSemanticModel(method.SyntaxTree);
                    var symbol = model.GetDeclaredSymbol(method);

                    // [Continuable] on the defining part of a partial method: use the part
                    // with the body.
                    if (method.Body == null && method.ExpressionBody == null &&
                        symbol?.PartialImplementationPart != null &&
                        symbol.PartialImplementationPart.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                            is MethodDeclarationSyntax impl)
                    {
                        method = impl;
                        model = compilation.GetSemanticModel(method.SyntaxTree);
                        symbol = model.GetDeclaredSymbol(method);
                    }

                    if (!seen.Add(method)) continue;

                    var reason = GetUnsupportedReason(method, symbol);
                    if (reason != null)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            UnsupportedMethodShape,
                            method.Identifier.GetLocation(),
                            method.Identifier.Text,
                            reason));
                        continue;
                    }

                    candidates.Add(new Candidate { Syntax = method, Symbol = symbol, Model = model });
                    continue;
                }

                var member = node is VariableDeclaratorSyntax v
                    ? (SyntaxNode)v.FirstAncestorOrSelf<MemberDeclarationSyntax>() ?? v
                    : node;
                if (!seen.Add(member)) continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedMember,
                    GetAttributeLocation(member),
                    DescribeMemberKind(member)));
            }

            if (candidates.Count == 0)
                return;

            var usedHintNames = new HashSet<string>();
            var replayType = "global::Prim.Generated." + GetReplayTypeName(compilation);

            foreach (var typeGroup in candidates.GroupBy(c => c.Symbol.ContainingType, SymbolEqualityComparer.Default))
            {
                var type = (INamedTypeSymbol)typeGroup.Key;
                var methods = typeGroup.ToList();
                var isPartial = IsPartialType(methods[0].Syntax);

                // Body checks that do not depend on which callees are continuable.
                foreach (var c in methods.ToList())
                {
                    var bodyReason = GetUnsupportedBodyReason(c, compilation, isPartial);
                    if (bodyReason != null)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            UnsupportedBody,
                            c.Syntax.Identifier.GetLocation(),
                            c.Syntax.Identifier.Text,
                            bodyReason));
                        methods.Remove(c);
                    }
                }

                // PRIM003: a yield point inside finally / lock / catch filter. Whether a
                // call is a yield point depends on which methods are generated, so remove
                // rejected methods until the set is stable (a call to a method that is not
                // generated is an ordinary call and must not be rewritten).
                bool removed;
                do
                {
                    removed = false;
                    var continuable = new HashSet<IMethodSymbol>(
                        methods.Select(m => m.Symbol), SymbolEqualityComparer.Default);

                    foreach (var c in methods.ToList())
                    {
                        var analyzer = CreateAnalyzer(c.Model, continuable);
                        var illegal = analyzer.FindIllegalYieldContexts(c.Syntax);
                        if (illegal.Count == 0) continue;

                        var first = illegal[0];
                        context.ReportDiagnostic(Diagnostic.Create(
                            YieldInForbiddenRegion,
                            first.Location ?? c.Syntax.Identifier.GetLocation(),
                            c.Syntax.Identifier.Text,
                            first.RegionDescription));
                        methods.Remove(c);
                        removed = true;
                    }
                } while (removed && methods.Count > 0);

                if (methods.Count == 0)
                    continue;

                var finalSet = new HashSet<IMethodSymbol>(
                    methods.Select(m => m.Symbol), SymbolEqualityComparer.Default);
                var fullTypeName = GetFullTypeName(type);

                // One file per partial declaration, so each copy is compiled with the
                // using directives and namespace blocks of the file it came from.
                foreach (var part in methods.GroupBy(m => m.Syntax.Parent))
                {
                    var source = GenerateFile(compilation, type, fullTypeName, part.ToList(), finalSet, isPartial, replayType);

                    var baseHint = fullTypeName.Replace('.', '_') + "_Continuations";
                    var hint = baseHint;
                    for (int n = 2; !usedHintNames.Add(hint); n++)
                        hint = baseHint + "_" + n;

                    context.AddSource(hint + ".g.cs", SourceText.From(source, Encoding.UTF8));
                }
            }

            if (usedHintNames.Count > 0)
            {
                context.AddSource("Prim_ContinuationReplay.g.cs", SourceText.From(GenerateReplayHelper(GetReplayTypeName(compilation)), Encoding.UTF8));
            }
        }

        /// <summary>
        /// Name of the per-compilation replay helper. It includes the assembly name so
        /// two assemblies that both use the generator (and see each other's internals)
        /// do not declare the same internal type.
        /// </summary>
        private static string GetReplayTypeName(Compilation compilation)
        {
            var name = compilation.AssemblyName ?? "Assembly";
            var sb = new StringBuilder("ContinuationReplay_");
            foreach (var ch in name)
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            return sb.ToString();
        }

        /// <summary>
        /// The replay counter shared by all generated frames running on one
        /// ScriptContext. Every yield site (loop iteration check or nested continuable
        /// call) calls <c>Site</c>. The count, relative to the outermost generated frame,
        /// is captured in every frame's last slot. On resume the outermost frame starts a
        /// replay with that count as the target: until the replay reaches the same site,
        /// yield checks neither suspend nor spend budget; at that site the replay ends
        /// (IsRestoring is cleared) and execution continues normally. Replay is
        /// deterministic, so the target site is exactly the one the run suspended at,
        /// whichever frame record each call consumed on the way.
        /// </summary>
        private static string GenerateReplayHelper(string typeName)
        {
            return $@"// <auto-generated />
#nullable disable

namespace Prim.Generated
{{
    /// <summary>
    /// Replay counter used by [Continuable] methods generated in this assembly.
    /// </summary>
    internal sealed class {typeName}
    {{
        private static readonly global::System.Runtime.CompilerServices.ConditionalWeakTable<global::Prim.Runtime.ScriptContext, {typeName}> States =
            new global::System.Runtime.CompilerServices.ConditionalWeakTable<global::Prim.Runtime.ScriptContext, {typeName}>();

        private int _depth;
        private long _entries;
        private long _sites;
        private long _targetEntry;
        private long _targetSites;
        private bool _targetKnown;
        private bool _replaying;

        /// <summary>Outermost generated frames entered on this context so far.</summary>
        public long Entries => _entries;

        /// <summary>Yield sites passed since the current outermost generated frame was entered.</summary>
        public long Sites => _sites;

        /// <summary>
        /// The context to run on: the runner's context, or, outside a runner, a private
        /// one with an unlimited budget (nothing could catch a suspension there).
        /// </summary>
        public static global::Prim.Runtime.ScriptContext CurrentContext()
        {{
            var current = global::Prim.Runtime.ScriptContext.Current;
            if (current != null) return current;
            var context = new global::Prim.Runtime.ScriptContext();
            context.InstructionBudget = int.MaxValue;
            return context;
        }}

        public static {typeName} Enter(global::Prim.Runtime.ScriptContext context)
        {{
            var state = States.GetValue(context, _ => new {typeName}());
            if (state._depth++ == 0)
            {{
                state._entries++;
                state._sites = 0;
                if (!state._targetKnown && context.IsRestoring)
                {{
                    state.ReadTarget(context);
                }}
                if (state._replaying &&
                    (state._entries > state._targetEntry ||
                     (state._entries == state._targetEntry && state._targetSites == 0)))
                {{
                    state.End(context);
                }}
            }}
            return state;
        }}

        public void Exit(global::Prim.Runtime.ScriptContext context)
        {{
            if (--_depth == 0 && _replaying && _entries >= _targetEntry)
            {{
                // The replay never reached the suspended site (the replayed code was
                // not deterministic); stop replaying rather than leak the state.
                End(context);
            }}
        }}

        /// <summary>
        /// Reads the suspension point from the outermost captured frame: its last two
        /// slots hold the entry and site counts at the time of the suspension.
        /// </summary>
        private void ReadTarget(global::Prim.Runtime.ScriptContext context)
        {{
            _targetKnown = true;
            var slots = context.FrameChain?.Slots;
            if (slots == null || slots.Length < 2 ||
                !TryGetCount(slots[slots.Length - 2], out _targetEntry) ||
                !TryGetCount(slots[slots.Length - 1], out _targetSites))
            {{
                return;
            }}
            _replaying = true;
        }}

        private static bool TryGetCount(object value, out long count)
        {{
            switch (value)
            {{
                case long l: count = l; return true;
                case int i: count = i; return true;
                default: count = 0; return false;
            }}
        }}

        /// <summary>
        /// Counts a yield site. Returns true if the site may suspend; false while
        /// replaying, including at the site the run suspended at (where the replay ends).
        /// </summary>
        public bool Site(global::Prim.Runtime.ScriptContext context)
        {{
            _sites++;
            if (!_replaying) return true;
            if (_entries < _targetEntry || _sites < _targetSites) return false;
            End(context);
            return false;
        }}

        private void End(global::Prim.Runtime.ScriptContext context)
        {{
            _replaying = false;
            context.IsRestoring = false;
            context.FrameChain = null;
        }}
    }}
}}
";
        }

        private static YieldPointAnalyzer CreateAnalyzer(SemanticModel model, HashSet<IMethodSymbol> continuable)
        {
            return new YieldPointAnalyzer(inv => ResolveContinuableTarget(inv, model, continuable) != null);
        }

        /// <summary>
        /// Returns the [Continuable] method this invocation calls if it is one of the
        /// methods being generated for the same type, else null. Resolved through the
        /// semantic model, so overloads, other receivers' methods with the same name and
        /// text inside strings are not matched.
        /// </summary>
        private static IMethodSymbol ResolveContinuableTarget(
            InvocationExpressionSyntax invocation,
            SemanticModel model,
            HashSet<IMethodSymbol> continuable)
        {
            if (continuable == null || continuable.Count == 0) return null;
            if (invocation.SyntaxTree != model.SyntaxTree) return null;
            var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (symbol == null) return null;
            var definition = symbol.ReducedFrom ?? symbol;
            return continuable.Contains(definition.OriginalDefinition) ? symbol : null;
        }

        private static Location GetAttributeLocation(SyntaxNode member)
        {
            SyntaxList<AttributeListSyntax> lists = default;
            if (member is MemberDeclarationSyntax m) lists = m.AttributeLists;
            else if (member is LocalFunctionStatementSyntax lf) lists = lf.AttributeLists;

            var attr = lists
                .SelectMany(al => al.Attributes)
                .FirstOrDefault(a =>
                {
                    var n = a.Name.ToString();
                    return n == "Continuable" || n == "ContinuableAttribute" ||
                           n.EndsWith(".Continuable") || n.EndsWith(".ContinuableAttribute");
                });
            return (attr ?? member).GetLocation();
        }

        private static string DescribeMemberKind(SyntaxNode member)
        {
            return member switch
            {
                PropertyDeclarationSyntax => "properties",
                IndexerDeclarationSyntax => "indexers",
                EventDeclarationSyntax => "events",
                EventFieldDeclarationSyntax => "events",
                FieldDeclarationSyntax => "fields",
                ConstructorDeclarationSyntax => "constructors",
                BaseTypeDeclarationSyntax => "types (mark the individual methods instead)",
                LocalFunctionStatementSyntax => "local functions",
                _ => "this member kind"
            };
        }

        // ----------------------------------------------------------------------------
        // Shape checks (PRIM002) and body checks (PRIM004)
        // ----------------------------------------------------------------------------

        /// <summary>
        /// Returns a human-readable reason a method cannot be transformed, or null if
        /// the method's shape is supported.
        /// </summary>
        private static string GetUnsupportedReason(MethodDeclarationSyntax method, IMethodSymbol symbol)
        {
            if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)) ||
                method.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
            {
                return "async methods are not supported";
            }

            if (method.DescendantNodes().OfType<YieldStatementSyntax>().Any())
            {
                return "iterator methods (yield return/yield break) are not supported";
            }

            if (method.TypeParameterList != null && method.TypeParameterList.Parameters.Count > 0)
            {
                return "generic methods are not supported";
            }

            for (var p = method.Parent; p != null; p = p.Parent)
            {
                if (p is TypeDeclarationSyntax t &&
                    t.TypeParameterList != null && t.TypeParameterList.Parameters.Count > 0)
                {
                    return "methods on generic types are not supported";
                }
            }

            if (method.Body == null && method.ExpressionBody != null)
            {
                return "expression-bodied methods are not supported";
            }

            if (method.Body == null)
            {
                return "methods without a body are not supported";
            }

            if (method.ReturnType is RefTypeSyntax)
            {
                return "ref-returning methods are not supported";
            }

            if (symbol == null || symbol.ContainingType == null)
            {
                return "the containing type could not be resolved";
            }

            if (!(method.Parent is TypeDeclarationSyntax ownType))
            {
                return "the containing type could not be resolved";
            }

            var chain = method.Ancestors().OfType<TypeDeclarationSyntax>().ToList();
            if (chain.Any(t => t.Modifiers.Any(m => m.Text == "file")))
            {
                return "methods on file-local types are not supported";
            }

            var ownPartial = IsPartial(ownType);
            if (ownPartial)
            {
                var nonPartialOuter = chain.Skip(1).FirstOrDefault(t => !IsPartial(t));
                if (nonPartialOuter != null)
                {
                    return $"the containing type '{nonPartialOuter.Identifier.Text}' must also be partial";
                }
            }
            else
            {
                if (chain.Count > 1)
                {
                    return "methods on nested types are only supported when the type and all its containing types are partial";
                }

                if (symbol.ContainingType.IsValueType)
                {
                    return "methods on structs are only supported when the struct is partial";
                }
            }

            return null;
        }

        private static bool IsPartial(TypeDeclarationSyntax type) =>
            type.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));

        private static bool IsPartialType(MethodDeclarationSyntax method) =>
            method.Parent is TypeDeclarationSyntax t && IsPartial(t);

        /// <summary>
        /// Checks the body for things the generated copy cannot reproduce. Returns a
        /// reason (PRIM004) or null.
        /// </summary>
        private static string GetUnsupportedBodyReason(Candidate c, Compilation compilation, bool isPartial)
        {
            var method = c.Syntax;
            var model = c.Model;

            foreach (var directive in method.Body.DescendantTrivia(descendIntoTrivia: true).Where(t => t.IsDirective))
            {
                var kind = directive.Kind();
                if (kind != SyntaxKind.RegionDirectiveTrivia &&
                    kind != SyntaxKind.EndRegionDirectiveTrivia &&
                    kind != SyntaxKind.PragmaWarningDirectiveTrivia &&
                    kind != SyntaxKind.NullableDirectiveTrivia)
                {
                    return "preprocessor directives other than #region, #pragma warning and #nullable inside the body are not supported";
                }
            }

            foreach (var node in method.DescendantNodes())
            {
                if (node is IdentifierNameSyntax || node is GenericNameSyntax)
                {
                    var symbol = model.GetSymbolInfo(node).Symbol;
                    if (symbol is IParameterSymbol p &&
                        p.ContainingSymbol is IMethodSymbol pm &&
                        pm.MethodKind == MethodKind.Constructor)
                    {
                        return $"it uses primary constructor parameter '{p.Name}'";
                    }
                }
            }

            if (isPartial)
                return null;

            // Non-partial type: the copy is a static method on a separate static class,
            // so it can only use what is accessible from outside the type.
            var assembly = compilation.Assembly;

            if (method.ParameterList.Parameters.Any(p => p.Identifier.ValueText == "instance") ||
                method.Body.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == "instance"))
            {
                return "the name 'instance' is used in the method, and the generated wrapper for a non-partial type takes the instance under that name (make the type partial)";
            }

            foreach (var t in c.Symbol.Parameters.Select(p => p.Type).Concat(new[] { c.Symbol.ReturnType }))
            {
                if (!compilation.IsSymbolAccessibleWithin(t, assembly))
                    return $"its signature uses '{t.Name}', which is not accessible outside '{c.Symbol.ContainingType.Name}' (make the type partial)";
            }

            foreach (var node in method.DescendantNodes())
            {
                if (node is BaseExpressionSyntax)
                {
                    return "it uses 'base', which is not available outside the type (make the type partial)";
                }

                if (node is IdentifierNameSyntax || node is GenericNameSyntax)
                {
                    var symbol = model.GetSymbolInfo(node).Symbol;
                    if (symbol == null) continue;
                    if (symbol is ILocalSymbol || symbol is IParameterSymbol || symbol is ILabelSymbol ||
                        symbol is IRangeVariableSymbol || symbol is INamespaceSymbol || symbol is IDiscardSymbol ||
                        symbol is ITypeParameterSymbol)
                    {
                        continue;
                    }

                    if (!compilation.IsSymbolAccessibleWithin(symbol, assembly))
                    {
                        return $"it uses '{symbol.Name}', which is not accessible outside '{c.Symbol.ContainingType.Name}' (make the type partial)";
                    }
                }

                if (node is InvocationExpressionSyntax inv && inv.Expression is MemberBindingExpressionSyntax)
                {
                    if (model.GetSymbolInfo(inv).Symbol is IMethodSymbol target &&
                        target.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == ContinuableAttributeMetadataName))
                    {
                        return "a [Continuable] method is called with '?.', which the wrapper for a non-partial type cannot rewrite (make the type partial)";
                    }
                }
            }

            return null;
        }

        // ----------------------------------------------------------------------------
        // Names and tokens
        // ----------------------------------------------------------------------------

        /// <summary>
        /// Namespace plus containing types, dot-separated (for example
        /// <c>Ns.Outer.Inner</c>). Used for the method token and the hint name.
        /// </summary>
        private static string GetFullTypeName(INamedTypeSymbol type)
        {
            var parts = new List<string>();
            for (var t = type; t != null; t = t.ContainingType)
                parts.Insert(0, t.Name);
            var ns = type.ContainingNamespace;
            if (ns != null && !ns.IsGlobalNamespace)
                parts.Insert(0, ns.ToDisplayString());
            return string.Join(".", parts);
        }

        private static readonly SymbolDisplayFormat FqFormat =
            SymbolDisplayFormat.FullyQualifiedFormat
                .WithMiscellaneousOptions(
                    SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        /// <summary>
        /// Parameter type names hashed into the method token: C# keywords for special
        /// types (<c>int</c>, <c>string</c>), otherwise namespace-qualified without
        /// <c>global::</c>; nullable reference annotations are ignored. So <c>int</c> and
        /// <c>Int32</c> give the same token.
        /// </summary>
        private static readonly SymbolDisplayFormat TokenTypeFormat = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        /// <summary>
        /// Resolves a type symbol to a fully-qualified name (global::...), so the
        /// generated code binds regardless of usings. Falls back to "object".
        /// </summary>
        private static string DisplayType(ITypeSymbol type)
        {
            if (type == null || type.TypeKind == TypeKind.Error)
                return "object";
            return type.ToDisplayString(FqFormat);
        }

        /// <summary>
        /// The method token: <c>StableHash.GenerateMethodToken(fullTypeName, methodName,
        /// parameterTypes...)</c>, where each parameter type is formatted with
        /// <see cref="TokenTypeFormat"/> and prefixed with <c>ref </c>/<c>out </c>/<c>in </c>
        /// for by-reference parameters. Reimplemented here because the generator cannot
        /// reference Prim.Core; the hash is the same FNV-1a over UTF-8 bytes.
        /// </summary>
        private static int GenerateMethodToken(INamedTypeSymbol type, IMethodSymbol method)
        {
            var paramTypes = method.Parameters
                .Select(p => RefKindPrefix(p.RefKind) + p.Type.ToDisplayString(TokenTypeFormat))
                .ToArray();
            return StableHashFnv1a(GetFullTypeName(type), method.Name, paramTypes);
        }

        private static string RefKindPrefix(RefKind kind)
        {
            switch (kind)
            {
                case RefKind.Ref: return "ref ";
                case RefKind.Out: return "out ";
                case RefKind.In: return "in ";
                default: return "";
            }
        }

        private static int StableHashFnv1a(string typeName, string methodName, string[] paramTypes)
        {
            unchecked
            {
                const uint fnvPrime = 16777619;
                const uint fnvOffsetBasis = 2166136261;

                int ComputeStringHash(string value)
                {
                    uint hash = fnvOffsetBasis;
                    foreach (byte b in Encoding.UTF8.GetBytes(value ?? ""))
                    {
                        hash ^= b;
                        hash *= fnvPrime;
                    }
                    return (int)hash;
                }

                int hashResult = 17;
                void Mix(int h) => hashResult = ((hashResult << 5) + hashResult) ^ h;

                Mix(ComputeStringHash(typeName));
                Mix(ComputeStringHash(methodName));
                foreach (var p in paramTypes)
                    Mix(ComputeStringHash(p));
                return hashResult;
            }
        }

        // ----------------------------------------------------------------------------
        // File layout: usings, namespaces, containing types
        // ----------------------------------------------------------------------------

        private static string GenerateFile(
            Compilation compilation,
            INamedTypeSymbol type,
            string fullTypeName,
            List<Candidate> methods,
            HashSet<IMethodSymbol> continuable,
            bool isPartial,
            string replayType)
        {
            var sb = new StringBuilder();
            var typeDecl = (TypeDeclarationSyntax)methods[0].Syntax.Parent;

            sb.AppendLine("// <auto-generated />");
            // Copied signatures and locals keep the user's nullable annotations, so
            // annotations are enabled; the rewritten body is not flow-analysed, so
            // nullable warnings stay off.
            sb.AppendLine("#nullable enable annotations");
            sb.AppendLine("#nullable disable warnings");
            sb.AppendLine("#pragma warning disable CS0162 // Unreachable code detected");
            sb.AppendLine("#pragma warning disable CS0219 // Variable assigned but never used");
            sb.AppendLine("#pragma warning disable CS8123 // Tuple element name ignored (explicit names keep inferred names stable)");
            sb.AppendLine();

            // The user's extern aliases and using directives, so copied code binds the
            // same way it does in the original file. Global usings already apply.
            var root = typeDecl.SyntaxTree.GetRoot() as CompilationUnitSyntax;
            if (root != null)
            {
                AppendUsings(sb, root.Externs, root.Usings, "");
            }
            sb.AppendLine();

            var indent = "";
            var namespaces = typeDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().ToList();
            foreach (var ns in namespaces)
            {
                sb.AppendLine($"{indent}namespace {ns.Name}");
                sb.AppendLine($"{indent}{{");
                indent += "    ";
                AppendUsings(sb, ns.Externs, ns.Usings, indent);
            }

            if (isPartial)
            {
                var typeChain = typeDecl.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse().ToList();
                foreach (var t in typeChain)
                {
                    sb.AppendLine($"{indent}{GetTypeModifiers(t)}partial {GetTypeKeyword(t)} {t.Identifier.Text}");
                    sb.AppendLine($"{indent}{{");
                    indent += "    ";
                }

                foreach (var method in methods)
                {
                    var emitter = new MethodEmitter(compilation, type, method, continuable, replayType, nonPartial: false, null);
                    emitter.Emit(sb, indent);
                }

                foreach (var _ in typeChain)
                {
                    indent = indent.Substring(4);
                    sb.AppendLine($"{indent}}}");
                }
            }
            else
            {
                var shortName = type.Name;
                var accessibility = type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
                var continuationsType = !type.ContainingNamespace.IsGlobalNamespace
                    ?$"global::{type.ContainingNamespace.ToDisplayString()}.{shortName}Continuations"
                    : $"global::{shortName}Continuations";

                sb.AppendLine($"{indent}/// <summary>");
                sb.AppendLine($"{indent}/// Continuation-enabled versions of {shortName} methods.");
                sb.AppendLine($"{indent}/// </summary>");
                sb.AppendLine($"{indent}{accessibility} static partial class {shortName}Continuations");
                sb.AppendLine($"{indent}{{");

                foreach (var method in methods)
                {
                    var emitter = new MethodEmitter(compilation, type, method, continuable, replayType, nonPartial: true, continuationsType);
                    emitter.Emit(sb, indent + "    ");
                }

                sb.AppendLine($"{indent}}}");
            }

            foreach (var _ in namespaces)
            {
                indent = indent.Substring(4);
                sb.AppendLine($"{indent}}}");
            }

            return sb.ToString();
        }

        private static void AppendUsings(
            StringBuilder sb,
            SyntaxList<ExternAliasDirectiveSyntax> externs,
            SyntaxList<UsingDirectiveSyntax> usings,
            string indent)
        {
            foreach (var e in externs)
                sb.AppendLine($"{indent}{e.WithoutTrivia().ToString()}");
            foreach (var u in usings)
            {
                if (u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)) continue;
                sb.AppendLine($"{indent}{u.WithoutTrivia().ToString()}");
            }
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

        private static string GetTypeKeyword(TypeDeclarationSyntax typeDecl)
        {
            if (typeDecl is RecordDeclarationSyntax record)
            {
                return record.ClassOrStructKeyword.IsKind(SyntaxKind.None) || record.ClassOrStructKeyword.IsMissing
                    ? "record"
                    : "record " + record.ClassOrStructKeyword.Text;
            }
            return typeDecl.Keyword.Text;
        }

        // ----------------------------------------------------------------------------
        // Per-method emission
        // ----------------------------------------------------------------------------

        /// <summary>
        /// Emits one *_Continuable method. The body is emitted first (into its own
        /// buffer) because locals are hoisted as their declarations are reached; the
        /// prologue that declares the hoisted slots is written afterwards.
        ///
        /// Hoisting rules: a local is hoisted (declared at the top, its declaration
        /// turned into an assignment, and captured in a slot) only if it is declared by
        /// a local declaration statement in a statement list the emitter walks itself,
        /// or is a catch variable without a filter, AND it is not const / ref / a using
        /// declaration, is not captured by a lambda or local function (hoisting would
        /// make every closure share one variable), and its type can be named and boxed
        /// (no anonymous, ref-like or pointer types). Every other local stays declared
        /// where it is, under its own name. Restored slot values are never read before
        /// replay reassigns them (definite assignment), so leaving a local unhoisted
        /// does not affect resume.
        /// </summary>
        private sealed class MethodEmitter
        {
            private readonly Compilation _compilation;
            private readonly INamedTypeSymbol _type;
            private readonly MethodDeclarationSyntax _method;
            private readonly IMethodSymbol _symbol;
            private readonly SemanticModel _model;
            private readonly HashSet<IMethodSymbol> _continuable;
            private readonly YieldPointAnalyzer _analyzer;
            private readonly List<YieldPointInfo> _plan;
            private readonly HashSet<ISymbol> _captured = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            private readonly Dictionary<ISymbol, string> _hoisted = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
            private readonly List<(string name, string type)> _slots = new List<(string name, string type)>();
            private readonly StringBuilder _body = new StringBuilder();
            private readonly ISymbol _accessWithin;
            private readonly string _replayType;
            private int _planCursor;
            private int _guardCounter;

            public bool NonPartial { get; }
            public string ContinuationsType { get; }
            public string InstanceName => "instance";
            public SemanticModel Model => _model;
            public INamedTypeSymbol Type => _type;

            public MethodEmitter(
                Compilation compilation,
                INamedTypeSymbol type,
                Candidate candidate,
                HashSet<IMethodSymbol> continuable,
                string replayType,
                bool nonPartial,
                string continuationsType)
            {
                _replayType = replayType;
                _compilation = compilation;
                _type = type;
                _method = candidate.Syntax;
                _symbol = candidate.Symbol;
                _model = candidate.Model;
                _continuable = continuable;
                NonPartial = nonPartial;
                ContinuationsType = continuationsType;
                _accessWithin = nonPartial ? (ISymbol)compilation.Assembly : type;
                _analyzer = CreateAnalyzer(_model, continuable);
                _plan = _analyzer.PlanEmittedYieldPoints(_method);

                // Locals referenced inside a lambda or local function are captured.
                foreach (var fn in _method.Body.DescendantNodes().Where(YieldPointAnalyzer.IsFunctionBoundary))
                {
                    foreach (var id in fn.DescendantNodes().OfType<IdentifierNameSyntax>())
                    {
                        if (_model.GetSymbolInfo(id).Symbol is ILocalSymbol local)
                            _captured.Add(local);
                    }
                }
            }

            public bool TryGetHoistedName(ISymbol symbol, out string name) => _hoisted.TryGetValue(symbol, out name);

            /// <summary>
            /// The [Continuable] target if this invocation is rewritten to call the
            /// *_Continuable twin: it resolves to a generated method of this type, is not
            /// inside a lambda or local function, and every statement around it is one
            /// the emitter walks itself (so no user try/catch in copied code can swallow
            /// the SuspendException).
            /// </summary>
            public IMethodSymbol GetRewrittenTarget(InvocationExpressionSyntax invocation)
            {
                var target = ResolveContinuableTarget(invocation, _model, _continuable);
                if (target == null) return null;

                StatementSyntax statement = null;
                for (SyntaxNode n = invocation.Parent; n != null; n = n.Parent)
                {
                    if (YieldPointAnalyzer.IsFunctionBoundary(n)) return null;
                    if (n is StatementSyntax s) { statement = s; break; }
                }

                if (statement == null || statement is LocalFunctionStatementSyntax) return null;
                return YieldPointAnalyzer.IsInWalkedPosition(statement, _method.Body) ? target : null;
            }

            public void Emit(StringBuilder sb, string indent)
            {
                var methodName = _method.Identifier.Text;
                var returnType = _symbol.ReturnsVoid ? "void" : DisplayType(_symbol.ReturnType);
                var parameters = BuildParameterList();
                var token = GenerateMethodToken(_type, _symbol);

                // Emit the body first so the hoisted slots are known.
                var inner = indent + "        ";
                EmitStatements(_method.Body.Statements, inner);

                sb.AppendLine();
                sb.AppendLine($"{indent}/// <summary>");
                sb.AppendLine(NonPartial
                    ? $"{indent}/// Continuation-enabled wrapper for {_type.Name}.{methodName}."
                    : $"{indent}/// Continuation-enabled version of {methodName}.");
                sb.AppendLine($"{indent}/// Supports suspension at {_plan.Count} yield point(s).");
                sb.AppendLine($"{indent}/// </summary>");
                sb.AppendLine($"{indent}{BuildMethodModifiers()}{returnType} {methodName}_Continuable({parameters})");
                sb.AppendLine($"{indent}{{");

                var i1 = indent + "    ";
                sb.AppendLine($"{i1}var __context = {_replayType}.CurrentContext();");
                sb.AppendLine($"{i1}const int __methodToken = {token};");
                sb.AppendLine($"{i1}var __replay = {_replayType}.Enter(__context);");
                foreach (var slot in _slots)
                {
                    sb.AppendLine($"{i1}{slot.type} {slot.name} = default({slot.type});");
                }
                sb.AppendLine();

                sb.AppendLine($"{i1}try");
                sb.AppendLine($"{i1}{{");
                var i2 = i1 + "    ";
                sb.AppendLine($"{i2}// Restore state if resuming. The body then replays from the top, with");
                sb.AppendLine($"{i2}// yield checks off until the replay reaches the site it suspended at.");
                sb.AppendLine($"{i2}if (__context.IsRestoring && __context.FrameChain?.MethodToken == __methodToken)");
                sb.AppendLine($"{i2}{{");
                sb.AppendLine($"{i2}    var __frame = __context.FrameChain;");
                sb.AppendLine($"{i2}    __context.FrameChain = __frame.Caller;");
                for (int i = 0; i < _slots.Count; i++)
                {
                    sb.AppendLine($"{i2}    {_slots[i].name} = global::Prim.Runtime.FrameCapture.GetSlot<{_slots[i].type}>(__frame.Slots, {i});");
                }
                sb.AppendLine($"{i2}}}");
                sb.AppendLine();
                sb.Append(_body);
                sb.AppendLine($"{i1}}}");
                sb.AppendLine($"{i1}catch (global::Prim.Core.SuspendException __ex)");
                sb.AppendLine($"{i1}{{");
                // The last two slots locate the suspension for the replay (see the
                // generated ContinuationReplay_* helper).
                var slotNames = _slots.Select(s => s.name).Concat(new[] { "__replay.Entries", "__replay.Sites" });
                sb.AppendLine($"{i1}    var __slots = global::Prim.Runtime.FrameCapture.PackSlots({string.Join(", ", slotNames)});");
                sb.AppendLine($"{i1}    var __record = global::Prim.Runtime.FrameCapture.CaptureFrame(__methodToken, __ex.YieldPointId, __slots, __ex.FrameChain);");
                sb.AppendLine($"{i1}    __ex.FrameChain = __record;");
                sb.AppendLine($"{i1}    throw;");
                sb.AppendLine($"{i1}}}");
                sb.AppendLine($"{i1}finally");
                sb.AppendLine($"{i1}{{");
                sb.AppendLine($"{i1}    __replay.Exit(__context);");
                sb.AppendLine($"{i1}}}");
                sb.AppendLine($"{indent}}}");
            }

            private string BuildMethodModifiers()
            {
                var sb = new StringBuilder();
                if (NonPartial)
                {
                    var isPublic = _type.DeclaredAccessibility == Accessibility.Public &&
                                   _symbol.DeclaredAccessibility == Accessibility.Public;
                    sb.Append(isPublic ? "public static " : "internal static ");
                    if (_method.Modifiers.Any(m => m.IsKind(SyntaxKind.UnsafeKeyword)) ||
                        ((TypeDeclarationSyntax)_method.Parent).Modifiers.Any(m => m.IsKind(SyntaxKind.UnsafeKeyword)))
                    {
                        sb.Append("unsafe ");
                    }
                    return sb.ToString();
                }

                foreach (var mod in _method.Modifiers)
                {
                    switch (mod.Kind())
                    {
                        case SyntaxKind.PublicKeyword:
                        case SyntaxKind.PrivateKeyword:
                        case SyntaxKind.ProtectedKeyword:
                        case SyntaxKind.InternalKeyword:
                        case SyntaxKind.StaticKeyword:
                        case SyntaxKind.UnsafeKeyword:
                        case SyntaxKind.ReadOnlyKeyword:
                            sb.Append(mod.Text).Append(' ');
                            break;
                    }
                }
                return sb.ToString();
            }

            private string BuildParameterList()
            {
                var parts = new List<string>();
                if (NonPartial && !_symbol.IsStatic)
                {
                    parts.Add($"{DisplayType(_type)} {InstanceName}");
                }

                foreach (var p in _method.ParameterList.Parameters)
                {
                    var modifiers = string.Join(" ", p.Modifiers.Select(m => m.Text));
                    var symbol = _model.GetDeclaredSymbol(p);
                    var typeStr = symbol != null && symbol.Type.TypeKind != TypeKind.Error
                        ? DisplayType(symbol.Type)
                        : p.Type?.ToString() ?? "object";
                    var defaultClause = p.Default != null ? $" = {Render(p.Default.Value)}" : "";
                    var prefix = string.IsNullOrEmpty(modifiers) ? "" : modifiers + " ";
                    parts.Add($"{prefix}{typeStr} {p.Identifier.Text}{defaultClause}");
                }
                return string.Join(", ", parts);
            }

            // ------------------------------------------------------------------------
            // Statements
            // ------------------------------------------------------------------------

            private void Line(string indent, string text) => _body.Append(indent).AppendLine(text);

            private int NextYieldPointId()
            {
                // The plan is built by the same traversal (and the same nested-call
                // predicate) as this emitter, so the cursor always matches.
                if (_planCursor < _plan.Count)
                    return _plan[_planCursor++].Id;
                return _planCursor++;
            }

            private void EmitStatements(SyntaxList<StatementSyntax> statements, string indent)
            {
                foreach (var statement in statements)
                    EmitStatement(statement, indent);
            }

            /// <summary>
            /// Emits an embedded statement (the body of an if/else/loop/using/label).
            /// Always braced: a statement can expand to several (yield-site bookkeeping
            /// plus the statement), and a brace-less body must keep all of them.
            /// </summary>
            private void EmitEmbedded(StatementSyntax statement, string indent)
            {
                if (statement is BlockSyntax)
                {
                    EmitStatement(statement, indent);
                    return;
                }
                Line(indent, "{");
                EmitStatement(statement, indent + "    ");
                Line(indent, "}");
            }

            private void EmitStatement(StatementSyntax statement, string indent)
            {
                if (YieldPointAnalyzer.IsLoopStatement(statement))
                {
                    EmitLoop(statement, indent);
                    return;
                }

                if (!YieldPointAnalyzer.IsStructuralStatement(statement))
                {
                    var call = _analyzer.FindStatementContinuableCall(statement);
                    if (call != null)
                    {
                        var id = NextYieldPointId();
                        Line(indent, $"// Yield point {id} (nested call to {YieldPointAnalyzer.GetSimpleMethodName(call)})");
                        Line(indent, "__replay.Site(__context);");
                    }
                }

                switch (statement)
                {
                    case BlockSyntax block:
                        Line(indent, "{");
                        EmitStatements(block.Statements, indent + "    ");
                        Line(indent, "}");
                        break;

                    case IfStatementSyntax ifStatement:
                        EmitIf(ifStatement, indent);
                        break;

                    case TryStatementSyntax tryStatement:
                        EmitTry(tryStatement, indent);
                        break;

                    case UsingStatementSyntax usingStatement:
                    {
                        var header = usingStatement.Declaration != null
                            ? Render(usingStatement.Declaration)
                            : Render(usingStatement.Expression);
                        Line(indent, $"using ({header})");
                        EmitEmbedded(usingStatement.Statement, indent);
                        break;
                    }

                    case SwitchStatementSyntax switchStatement:
                        EmitSwitch(switchStatement, indent);
                        break;

                    case CheckedStatementSyntax checkedStatement:
                        Line(indent, checkedStatement.Keyword.Text);
                        EmitStatement(checkedStatement.Block, indent);
                        break;

                    case UnsafeStatementSyntax unsafeStatement:
                        Line(indent, "unsafe");
                        EmitStatement(unsafeStatement.Block, indent);
                        break;

                    case LabeledStatementSyntax labeled:
                        Line(indent, $"{labeled.Identifier.Text}:");
                        EmitEmbedded(labeled.Statement, indent);
                        break;

                    case LocalDeclarationStatementSyntax localDecl:
                        EmitLocalDeclaration(localDecl, indent);
                        break;

                    default:
                        Line(indent, Render(statement));
                        break;
                }
            }

            private void EmitIf(IfStatementSyntax ifStatement, string indent)
            {
                Line(indent, $"if ({Render(ifStatement.Condition)})");
                EmitEmbedded(ifStatement.Statement, indent);

                if (ifStatement.Else != null)
                {
                    Line(indent, "else");
                    if (ifStatement.Else.Statement is IfStatementSyntax elseIf)
                        EmitStatement(elseIf, indent);
                    else
                        EmitEmbedded(ifStatement.Else.Statement, indent);
                }
            }

            /// <summary>
            /// A loop gets one yield point, checked at the start of EVERY iteration (so an
            /// infinite loop is still preempted by the instruction budget). While the frame
            /// replays the check only counts; at the iteration where the frame suspended it
            /// releases instead of checking.
            /// </summary>
            private void EmitLoop(StatementSyntax loop, string indent)
            {
                var id = NextYieldPointId();
                StatementSyntax body;

                switch (loop)
                {
                    case WhileStatementSyntax w:
                        Line(indent, $"while ({Render(w.Condition)})");
                        body = w.Statement;
                        break;

                    case DoStatementSyntax d:
                        Line(indent, "do");
                        body = d.Statement;
                        break;

                    case ForStatementSyntax f:
                    {
                        var init = f.Declaration != null
                            ? Render(f.Declaration)
                            : string.Join(", ", f.Initializers.Select(Render));
                        var condition = f.Condition != null ? Render(f.Condition) : "";
                        var incrementors = string.Join(", ", f.Incrementors.Select(Render));
                        Line(indent, $"for ({init}; {condition}; {incrementors})");
                        body = f.Statement;
                        break;
                    }

                    case ForEachStatementSyntax fe:
                        Line(indent, $"foreach ({Render(fe.Type)} {fe.Identifier.Text} in {Render(fe.Expression)})");
                        body = fe.Statement;
                        break;

                    case ForEachVariableStatementSyntax fv:
                        Line(indent, $"foreach ({Render(fv.Variable)} in {Render(fv.Expression)})");
                        body = fv.Statement;
                        break;

                    default:
                        Line(indent, Render(loop));
                        return;
                }

                var cost = EstimateCost(body);
                Line(indent, "{");
                var inner = indent + "    ";
                Line(inner, $"// Yield point {id} (loop iteration, cost={cost})");
                Line(inner, $"if (__replay.Site(__context)) __context.HandleYieldPointWithBudget({id}, {cost});");
                if (body is BlockSyntax block)
                    EmitStatements(block.Statements, inner);
                else
                    EmitStatement(body, inner);

                if (loop is DoStatementSyntax doLoop)
                    Line(indent, $"}} while ({Render(doLoop.Condition)});");
                else
                    Line(indent, "}");
            }

            private static int EstimateCost(StatementSyntax body)
            {
                var nodeCount = body.DescendantNodesAndSelf().Count();
                return Math.Max(1, Math.Min(nodeCount, 100));
            }

            private void EmitTry(TryStatementSyntax tryStatement, string indent)
            {
                Line(indent, "try");
                EmitStatement(tryStatement.Block, indent);

                foreach (var catchClause in tryStatement.Catches)
                    EmitCatch(catchClause, indent);

                if (tryStatement.Finally != null)
                {
                    Line(indent, "finally");
                    Line(indent, "{");
                    Line(indent + "    ", "// Under replay a finally runs on suspension as well as on normal exit.");
                    EmitStatements(tryStatement.Finally.Block.Statements, indent + "    ");
                    Line(indent, "}");
                }
            }

            /// <summary>
            /// Emits a catch clause so that it never catches the SuspendException that
            /// unwinds a suspension: a catch that could catch it gets a
            /// <c>!(e is SuspendException)</c> filter, combined with the user's own
            /// <c>when</c> filter if there is one.
            /// </summary>
            private void EmitCatch(CatchClauseSyntax catchClause, string indent)
            {
                var declaration = catchClause.Declaration;
                var userFilter = catchClause.Filter != null ? Render(catchClause.Filter.FilterExpression) : null;
                var conditions = new List<string>();
                string header;
                string hoistedAssignment = null;

                if (declaration == null)
                {
                    var guard = $"__catchEx{_guardCounter++}";
                    conditions.Add($"!({guard} is global::Prim.Core.SuspendException)");
                    header = $"catch (global::System.Exception {guard})";
                }
                else
                {
                    var exSymbol = _model.GetTypeInfo(declaration.Type).Type;
                    var exType = exSymbol != null && exSymbol.TypeKind != TypeKind.Error
                        ? DisplayType(exSymbol)
                        : Render(declaration.Type);
                    var canCatchSuspend = CatchCanCatchSuspend(exSymbol);
                    var named = !string.IsNullOrEmpty(declaration.Identifier.ValueText);

                    string varName = null;
                    if (named)
                    {
                        var local = _model.GetDeclaredSymbol(declaration) as ILocalSymbol;
                        // With a filter the variable is read before the body runs, so it
                        // keeps its own name instead of being hoisted.
                        if (catchClause.Filter == null && CanHoist(local))
                        {
                            var hoistedName = Hoist(local);
                            varName = $"__catchEx{_guardCounter++}";
                            hoistedAssignment = $"{hoistedName} = {varName};";
                        }
                        else
                        {
                            varName = declaration.Identifier.Text;
                        }
                    }
                    else if (canCatchSuspend)
                    {
                        varName = $"__catchEx{_guardCounter++}";
                    }

                    if (canCatchSuspend)
                        conditions.Add($"!({varName} is global::Prim.Core.SuspendException)");

                    header = varName != null ? $"catch ({exType} {varName})" : $"catch ({exType})";
                }

                if (userFilter != null)
                    conditions.Add($"({userFilter})");

                if (conditions.Count > 0)
                    header += $" when ({string.Join(" && ", conditions)})";

                Line(indent, header);
                Line(indent, "{");
                if (hoistedAssignment != null)
                    Line(indent + "    ", hoistedAssignment);
                EmitStatements(catchClause.Block.Statements, indent + "    ");
                Line(indent, "}");
            }

            /// <summary>
            /// True if a catch of <paramref name="catchType"/> could catch a
            /// SuspendException. Emitting the escape filter on an unrelated typed catch
            /// would be a provably-false test (CS0184).
            /// </summary>
            private bool CatchCanCatchSuspend(ITypeSymbol catchType)
            {
                if (catchType == null || catchType.TypeKind == TypeKind.Error) return true;

                var suspend = _compilation.GetTypeByMetadataName("Prim.Core.SuspendException");
                if (suspend == null) return true;

                for (var t = suspend; t != null; t = t.BaseType)
                {
                    if (SymbolEqualityComparer.Default.Equals(t, catchType)) return true;
                }
                return false;
            }

            private void EmitSwitch(SwitchStatementSyntax switchStatement, string indent)
            {
                var expression = Render(switchStatement.Expression);
                Line(indent, switchStatement.OpenParenToken.IsMissing || switchStatement.OpenParenToken.IsKind(SyntaxKind.None)
                    ? $"switch {expression}"
                    : $"switch ({expression})");
                Line(indent, "{");
                foreach (var section in switchStatement.Sections)
                {
                    foreach (var label in section.Labels)
                        Line(indent + "    ", Render(label));
                    EmitStatements(section.Statements, indent + "        ");
                }
                Line(indent, "}");
            }

            private void EmitLocalDeclaration(LocalDeclarationStatementSyntax localDecl, string indent)
            {
                var isUsing = localDecl.UsingKeyword.IsKind(SyntaxKind.UsingKeyword);
                var isConst = localDecl.IsConst;

                var hoistable = new List<(VariableDeclaratorSyntax v, ILocalSymbol symbol)>();
                var anyHoisted = false;
                foreach (var v in localDecl.Declaration.Variables)
                {
                    var symbol = _model.GetDeclaredSymbol(v) as ILocalSymbol;
                    var ok = !isUsing && !isConst && CanHoist(symbol);
                    hoistable.Add((v, ok ? symbol : null));
                    anyHoisted |= ok;
                }

                if (!anyHoisted)
                {
                    // Keep the declaration as written (const, using, ref, captured by a
                    // closure, or a type that cannot be hoisted).
                    Line(indent, Render(localDecl));
                    return;
                }

                foreach (var (v, symbol) in hoistable)
                {
                    if (symbol != null)
                    {
                        var name = Hoist(symbol);
                        if (v.Initializer != null)
                        {
                            var value = v.Initializer.Value is InitializerExpressionSyntax arrayInit
                                ? $"new {DisplayType(symbol.Type)} {Render(arrayInit)}"
                                : Render(v.Initializer.Value);
                            Line(indent, $"{name} = {value};");
                        }
                    }
                    else
                    {
                        // Mixed declaration (only possible with an explicit type).
                        Line(indent, $"{Render(localDecl.Declaration.Type)} {Render(v)};");
                    }
                }
            }

            private bool CanHoist(ILocalSymbol local)
            {
                if (local == null) return false;
                if (local.IsConst || local.RefKind != RefKind.None || local.IsFixed) return false;
                if (_captured.Contains(local)) return false;
                return IsSlotType(local.Type);
            }

            private bool IsSlotType(ITypeSymbol type)
            {
                if (type == null || type.TypeKind == TypeKind.Error) return false;
                if (type.IsRefLikeType) return false;
                if (type.TypeKind == TypeKind.Pointer || type.TypeKind == TypeKind.FunctionPointer) return false;
                if (ContainsUnnameableType(type)) return false;
                return _compilation.IsSymbolAccessibleWithin(type, _accessWithin);
            }

            private static bool ContainsUnnameableType(ITypeSymbol type)
            {
                switch (type)
                {
                    case IArrayTypeSymbol array:
                        return ContainsUnnameableType(array.ElementType);
                    case IPointerTypeSymbol _:
                        return true;
                    case INamedTypeSymbol named:
                        if (named.IsAnonymousType) return true;
                        if (named.IsTupleType)
                            return named.TupleElements.Any(e => ContainsUnnameableType(e.Type));
                        return named.TypeArguments.Any(ContainsUnnameableType);
                    default:
                        return false;
                }
            }

            private string Hoist(ILocalSymbol local)
            {
                if (_hoisted.TryGetValue(local, out var existing)) return existing;
                var name = $"__l{_slots.Count}_{local.Name}";
                _hoisted[local] = name;
                _slots.Add((name, DisplayType(local.Type)));
                return name;
            }

            // ------------------------------------------------------------------------
            // Expression rendering
            // ------------------------------------------------------------------------

            /// <summary>
            /// Renders a fragment of the original method with hoisted-local renames,
            /// continuable-call rewrites and (for a non-partial type) instance-member
            /// qualification applied.
            /// </summary>
            public string Render(SyntaxNode node)
            {
                if (node == null) return "";
                var rewritten = new BodyRewriter(this).Visit(node);
                return rewritten.ToFullString().Trim();
            }
        }

        /// <summary>
        /// Syntax-and-symbol based rewriter applied to every copied fragment. It never
        /// works on text, so overloads, other receivers and string contents are left
        /// alone.
        /// </summary>
        private sealed class BodyRewriter : CSharpSyntaxRewriter
        {
            private readonly MethodEmitter _emitter;
            private SemanticModel Model => _emitter.Model;

            public BodyRewriter(MethodEmitter emitter)
            {
                _emitter = emitter;
            }

            public override SyntaxTrivia VisitTrivia(SyntaxTrivia trivia)
            {
                // Only #region / #pragma warning / #nullable can reach here (others are
                // rejected); dropping them keeps the copied fragments balanced.
                if (trivia.IsDirective) return default;
                return base.VisitTrivia(trivia);
            }

            public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
            {
                var symbol = Model.GetSymbolInfo(node).Symbol;
                if (symbol is ILocalSymbol && _emitter.TryGetHoistedName(symbol, out var newName))
                {
                    return node.WithIdentifier(
                        SyntaxFactory.Identifier(newName).WithTriviaFrom(node.Identifier));
                }

                if (_emitter.NonPartial && symbol != null && IsImplicitMemberReference(node))
                {
                    var qualified = QualifyMember(node, symbol);
                    if (qualified != null) return qualified;
                }

                return base.VisitIdentifierName(node);
            }

            public override SyntaxNode VisitGenericName(GenericNameSyntax node)
            {
                var updated = (GenericNameSyntax)base.VisitGenericName(node);
                if (_emitter.NonPartial && IsImplicitMemberReference(node))
                {
                    var symbol = Model.GetSymbolInfo(node).Symbol;
                    if (symbol != null)
                    {
                        var qualified = QualifyMember(updated, symbol);
                        if (qualified != null) return qualified;
                    }
                }
                return updated;
            }

            public override SyntaxNode VisitThisExpression(ThisExpressionSyntax node)
            {
                if (_emitter.NonPartial)
                {
                    return SyntaxFactory.IdentifierName(_emitter.InstanceName).WithTriviaFrom(node);
                }
                return base.VisitThisExpression(node);
            }

            /// <summary>
            /// For a non-partial type the copy lives in another class: a simple name that
            /// binds to a member of the type (or a base type) is qualified with the
            /// instance or with the declaring type.
            /// </summary>
            private ExpressionSyntax QualifyMember(SimpleNameSyntax name, ISymbol symbol)
            {
                if (symbol is ILocalSymbol || symbol is IParameterSymbol || symbol is INamespaceSymbol ||
                    symbol is ILabelSymbol || symbol is IRangeVariableSymbol || symbol is ITypeParameterSymbol)
                {
                    return null;
                }

                var declaringType = symbol.ContainingType;
                if (declaringType == null || !IsInTypeHierarchy(declaringType)) return null;

                var stripped = name.WithoutTrivia();
                ExpressionSyntax result;
                if (symbol is INamedTypeSymbol || symbol.IsStatic || symbol.Kind == SymbolKind.Field && ((IFieldSymbol)symbol).IsConst)
                {
                    result = SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ParseExpression(DisplayType(declaringType)),
                        stripped);
                }
                else
                {
                    result = SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(_emitter.InstanceName),
                        stripped);
                }
                return result.WithTriviaFrom(name);
            }

            private bool IsInTypeHierarchy(INamedTypeSymbol declaringType)
            {
                for (var t = _emitter.Type; t != null; t = t.BaseType)
                {
                    if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, declaringType.OriginalDefinition))
                        return true;
                }
                return false;
            }

            private static bool IsImplicitMemberReference(SimpleNameSyntax node)
            {
                var parent = node.Parent;
                switch (parent)
                {
                    case MemberAccessExpressionSyntax ma when ma.Name == node:
                        return false;
                    case QualifiedNameSyntax qn when qn.Right == node:
                        return false;
                    case MemberBindingExpressionSyntax _:
                        return false;
                    case NameColonSyntax _:
                    case NameEqualsSyntax _:
                        return false;
                    case AliasQualifiedNameSyntax _:
                        return false;
                    case AssignmentExpressionSyntax assignment
                        when assignment.Left == node && assignment.Parent is InitializerExpressionSyntax init &&
                             (init.IsKind(SyntaxKind.ObjectInitializerExpression) ||
                              init.IsKind(SyntaxKind.WithInitializerExpression)):
                        return false;
                }
                return true;
            }

            public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                // nameof(local) must keep the source name, not the hoisted one.
                if (node.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == "nameof")
                {
                    var constant = Model.GetConstantValue(node);
                    if (constant.HasValue && constant.Value is string text)
                    {
                        return SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal(text))
                            .WithTriviaFrom(node);
                    }
                }

                var target = _emitter.GetRewrittenTarget(node);
                var updated = (InvocationExpressionSyntax)base.VisitInvocationExpression(node);
                if (target == null) return updated;

                var newName = SyntaxFactory.IdentifierName(target.Name + "_Continuable");

                if (!_emitter.NonPartial)
                {
                    switch (updated.Expression)
                    {
                        case IdentifierNameSyntax simple:
                            return updated.WithExpression(newName.WithTriviaFrom(simple));
                        case MemberAccessExpressionSyntax ma:
                            return updated.WithExpression(ma.WithName(newName.WithTriviaFrom(ma.Name)));
                        case MemberBindingExpressionSyntax mb:
                            return updated.WithExpression(mb.WithName(newName.WithTriviaFrom(mb.Name)));
                        default:
                            return updated;
                    }
                }

                // Non-partial: Type.M(args) / recv.M(args) / M(args) become
                // TContinuations.M_Continuable([recv,] args). The implicit-this case was
                // already qualified to instance.M by VisitIdentifierName.
                ExpressionSyntax receiver = null;
                var definition = target.ReducedFrom ?? target;
                var isStaticCall = definition.IsStatic && target.ReducedFrom == null;
                if (!isStaticCall && updated.Expression is MemberAccessExpressionSyntax access)
                {
                    receiver = access.Expression.WithoutTrivia();
                }
                else if (!isStaticCall)
                {
                    receiver = SyntaxFactory.IdentifierName(_emitter.InstanceName);
                }

                var callee = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParseExpression(_emitter.ContinuationsType),
                    newName);

                var args = updated.ArgumentList.Arguments;
                if (receiver != null)
                {
                    var all = new[] { SyntaxFactory.Argument(receiver) }.Concat(args).ToList();
                    args = SyntaxFactory.SeparatedList(
                        all,
                        Enumerable.Repeat(
                            SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space),
                            all.Count - 1));
                }

                return SyntaxFactory.InvocationExpression(
                        callee,
                        updated.ArgumentList.WithArguments(args))
                    .WithTriviaFrom(updated);
            }

            public override SyntaxNode VisitAnonymousObjectMemberDeclarator(AnonymousObjectMemberDeclaratorSyntax node)
            {
                // new { total } infers the member name from the expression; keep the
                // source name when the expression is renamed.
                var property = node.NameEquals == null ? Model.GetDeclaredSymbol(node) : null;
                var updated = (AnonymousObjectMemberDeclaratorSyntax)base.VisitAnonymousObjectMemberDeclarator(node);
                if (property != null)
                {
                    updated = updated.WithNameEquals(
                        SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName(property.Name)));
                }
                return updated;
            }

            public override SyntaxNode VisitArgument(ArgumentSyntax node)
            {
                // (total, x) infers tuple element names from the expressions; keep the
                // source names for renamed locals.
                string inferred = null;
                if (node.NameColon == null && node.Parent is TupleExpressionSyntax tuple &&
                    node.Expression is IdentifierNameSyntax id &&
                    !IsDeconstructionTarget(tuple))
                {
                    var symbol = Model.GetSymbolInfo(id).Symbol;
                    if (symbol is ILocalSymbol && _emitter.TryGetHoistedName(symbol, out _))
                        inferred = id.Identifier.ValueText;
                }

                var updated = (ArgumentSyntax)base.VisitArgument(node);
                if (inferred != null)
                {
                    updated = updated.WithNameColon(SyntaxFactory.NameColon(inferred));
                }
                return updated;
            }

            private static bool IsDeconstructionTarget(TupleExpressionSyntax tuple)
            {
                SyntaxNode current = tuple;
                while (current.Parent is ArgumentSyntax || current.Parent is TupleExpressionSyntax)
                    current = current.Parent;
                return (current.Parent is AssignmentExpressionSyntax assignment && assignment.Left == current) ||
                       current.Parent is ForEachVariableStatementSyntax;
            }
        }
    }
}
