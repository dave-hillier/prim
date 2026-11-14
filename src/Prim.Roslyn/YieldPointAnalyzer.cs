// Written without nullable annotations. Prim.Tests.Roslyn compiles this file
// directly with nullable enabled, so state the context explicitly.
#nullable disable

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Prim.Roslyn
{
    /// <summary>
    /// Information about a yield point in the code.
    /// </summary>
    public class YieldPointInfo
    {
        public int Id { get; set; }
        public Location Location { get; set; }
        public YieldPointKind Kind { get; set; }

        /// <summary>
        /// Human-readable description of the yield point.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Nesting depth of try blocks at this yield point.
        /// Used to track which finally blocks need to execute on suspension.
        /// </summary>
        public int TryNestingDepth { get; set; }

        /// <summary>
        /// IDs of finally blocks that need to execute if suspending at this point.
        /// </summary>
        public List<int> EnclosingFinallyBlocks { get; set; } = new List<int>();

        /// <summary>
        /// For NestedMethodCall yield points, the name of the method being called.
        /// </summary>
        public string CalledMethodName { get; set; }

        /// <summary>
        /// For NestedMethodCall yield points, the syntax node of the invocation.
        /// </summary>
        public InvocationExpressionSyntax InvocationSyntax { get; set; }
    }

    /// <summary>
    /// Information about a try-catch-finally block.
    /// </summary>
    public class TryBlockInfo
    {
        public int Id { get; set; }
        public Location Location { get; set; }
        public bool HasFinally { get; set; }
        public bool HasCatch { get; set; }
        public List<CatchClauseInfo> CatchClauses { get; set; } = new List<CatchClauseInfo>();
        public List<YieldPointInfo> YieldPointsInTry { get; set; } = new List<YieldPointInfo>();
        public List<YieldPointInfo> YieldPointsInFinally { get; set; } = new List<YieldPointInfo>();
    }

    /// <summary>
    /// Information about a catch clause.
    /// </summary>
    public class CatchClauseInfo
    {
        public string ExceptionType { get; set; }
        public string VariableName { get; set; }
        public List<YieldPointInfo> YieldPoints { get; set; } = new List<YieldPointInfo>();
    }

    /// <summary>
    /// A yield point found in a context the suspend/replay model cannot honour
    /// (finally / lock / catch filter). Reported as a hard diagnostic (#24).
    /// </summary>
    public class IllegalYieldContext
    {
        public Location Location { get; set; }
        public ForbiddenRegionKind Region { get; set; }

        /// <summary>Human-readable region name for the diagnostic message.</summary>
        public string RegionDescription => Region switch
        {
            ForbiddenRegionKind.Finally => "a finally block",
            ForbiddenRegionKind.Lock => "a lock statement",
            ForbiddenRegionKind.CatchFilter => "a catch filter (when clause)",
            _ => "an unsupported region"
        };
    }

    /// <summary>
    /// A region a yield point may not appear in.
    /// </summary>
    public enum ForbiddenRegionKind
    {
        None,
        Finally,
        Lock,
        CatchFilter
    }

    /// <summary>
    /// The kind of yield point.
    /// </summary>
    public enum YieldPointKind
    {
        /// <summary>
        /// A backward jump (loop back-edge).
        /// </summary>
        LoopBackEdge,

        /// <summary>
        /// A call to another continuable method that may yield.
        /// </summary>
        NestedMethodCall,

        /// <summary>
        /// Call to an already-suffixed *_Continuable method, reported in standalone mode
        /// (no continuable name set supplied).
        /// </summary>
        ContinuableCall
    }

    /// <summary>
    /// Analyzes C# syntax to find yield points.
    ///
    /// SINGLE SOURCE OF TRUTH (#22): the set of yield points produced here is exactly
    /// the set the emitter (<see cref="ContinuationGenerator"/>) turns into
    /// HandleYieldPoint / nested-call sites, walked in the SAME syntax order. The
    /// emitter does NOT keep an independent counter: it consumes the IDs assigned here
    /// (in emission order) so the ID a frame is captured with is the same ID used to
    /// resume and to describe the method in diagnostics/comments.
    ///
    /// A yield point is emitted for:
    ///   - each loop (while / for / foreach / do), checked at the start of every
    ///     iteration -> LoopBackEdge
    ///   - each non-structural statement that directly contains a call to a known
    ///     continuable method (statement granularity, including sub-expression calls,
    ///     but not calls inside lambdas, local functions or nested statements)
    ///     -> NestedMethodCall
    ///
    /// Continuable calls are recognised by a predicate (the generator passes one that
    /// resolves the invoked symbol) or by a set of ORIGINAL method names. With neither,
    /// the analyzer falls back to recognising already-suffixed *_Continuable names,
    /// which keeps the standalone analyzer unit tests meaningful.
    /// </summary>
    public class YieldPointAnalyzer
    {
        private readonly HashSet<string> _continuableMethodNames;
        private readonly System.Func<InvocationExpressionSyntax, bool> _isContinuableCall;

        public YieldPointAnalyzer()
        {
        }

        public YieldPointAnalyzer(HashSet<string> continuableMethodNames)
        {
            _continuableMethodNames = continuableMethodNames;
        }

        /// <summary>
        /// Creates an analyzer that decides which invocations are continuable calls with
        /// <paramref name="isContinuableCall"/>. The generator passes a predicate that
        /// resolves the invoked symbol through the semantic model.
        /// </summary>
        public YieldPointAnalyzer(System.Func<InvocationExpressionSyntax, bool> isContinuableCall)
        {
            _isContinuableCall = isContinuableCall;
        }

        private bool HasContinuableSet =>
            _isContinuableCall != null || (_continuableMethodNames != null && _continuableMethodNames.Count > 0);

        private bool MatchesContinuable(InvocationExpressionSyntax invocation)
        {
            if (_isContinuableCall != null) return _isContinuableCall(invocation);
            return _continuableMethodNames != null && _continuableMethodNames.Contains(GetSimpleMethodName(invocation));
        }

        /// <summary>
        /// Finds all yield points in a method.
        /// </summary>
        public List<YieldPointInfo> FindYieldPoints(MethodDeclarationSyntax method)
        {
            return FindYieldPointsAndTryBlocks(method).YieldPoints;
        }

        /// <summary>
        /// Finds all try-catch-finally blocks in a method along with yield points.
        /// </summary>
        public (List<YieldPointInfo> YieldPoints, List<TryBlockInfo> TryBlocks) FindYieldPointsAndTryBlocks(MethodDeclarationSyntax method)
        {
            var yieldPoints = new List<YieldPointInfo>();
            var tryBlocks = new List<TryBlockInfo>();
            var visitor = new YieldPointVisitor(yieldPoints, tryBlocks, _continuableMethodNames, _isContinuableCall);
            visitor.Visit(method.Body);
            return (yieldPoints, tryBlocks);
        }

        /// <summary>
        /// Produces the CANONICAL ordered list of yield points the emitter will turn into
        /// HandleYieldPoint / nested-call sites, in emission order, with stable IDs (the
        /// list index). This is the single source of truth (#22): the emitter consumes the
        /// returned objects (drawing their <see cref="YieldPointInfo.Id"/> as it emits each
        /// site) instead of running an independent counter, so the captured frame's
        /// YieldPointId, the resume dispatch, and the "{Count} yield point(s)" comment all
        /// agree.
        ///
        /// The traversal mirrors <c>ContinuationGenerator.MethodEmitter.EmitStatement</c>
        /// and uses the same helpers:
        ///   - a loop (while/for/foreach/do) contributes one LoopBackEdge point (checked
        ///     at the start of every iteration), THEN its body is walked;
        ///   - a non-structural statement for which
        ///     <see cref="FindStatementContinuableCall"/> finds a call contributes exactly
        ///     one NestedMethodCall point;
        ///   - structural statements (<see cref="IsStructuralStatement"/>: block, if, try,
        ///     using, switch, checked, unsafe, labeled, loops) recurse into their child
        ///     statements in source order.
        /// </summary>
        public List<YieldPointInfo> PlanEmittedYieldPoints(MethodDeclarationSyntax method)
        {
            var plan = new List<YieldPointInfo>();
            if (method.Body == null) return plan;
            PlanStatements(method.Body.Statements, plan);
            return plan;
        }

        private void PlanStatements(SyntaxList<StatementSyntax> statements, List<YieldPointInfo> plan)
        {
            foreach (var statement in statements)
            {
                PlanStatement(statement, plan);
            }
        }

        private void PlanStatement(StatementSyntax statement, List<YieldPointInfo> plan)
        {
            // Loop: one yield point, checked at the start of every iteration (emitter:
            // EmitLoop takes the id before walking the body).
            if (IsLoopStatement(statement))
            {
                plan.Add(NewPlanPoint(statement.GetFirstToken().GetLocation(), YieldPointKind.LoopBackEdge, plan));
            }

            // Statement-level continuable call (emitter: EmitStatement). Structural
            // statements recurse into their children instead.
            if (!IsStructuralStatement(statement))
            {
                var call = FindStatementContinuableCall(statement);
                if (call != null)
                {
                    var point = NewPlanPoint(call.GetLocation(), YieldPointKind.NestedMethodCall, plan);
                    point.CalledMethodName = GetSimpleMethodName(call);
                    point.InvocationSyntax = call;
                    plan.Add(point);
                }
                return;
            }

            switch (statement)
            {
                case BlockSyntax block:
                    PlanStatements(block.Statements, plan);
                    break;
                case TryStatementSyntax tryStatement:
                    PlanStatements(tryStatement.Block.Statements, plan);
                    foreach (var c in tryStatement.Catches)
                        PlanStatements(c.Block.Statements, plan);
                    if (tryStatement.Finally != null)
                        PlanStatements(tryStatement.Finally.Block.Statements, plan);
                    break;
                case UsingStatementSyntax usingStatement:
                    PlanStatement(usingStatement.Statement, plan);
                    break;
                case IfStatementSyntax ifStatement:
                    PlanStatement(ifStatement.Statement, plan);
                    if (ifStatement.Else != null)
                        PlanStatement(ifStatement.Else.Statement, plan);
                    break;
                case SwitchStatementSyntax switchStatement:
                    foreach (var section in switchStatement.Sections)
                        PlanStatements(section.Statements, plan);
                    break;
                case CheckedStatementSyntax checkedStatement:
                    PlanStatements(checkedStatement.Block.Statements, plan);
                    break;
                case UnsafeStatementSyntax unsafeStatement:
                    PlanStatements(unsafeStatement.Block.Statements, plan);
                    break;
                case LabeledStatementSyntax labeled:
                    PlanStatement(labeled.Statement, plan);
                    break;
                case WhileStatementSyntax whileStatement:
                    PlanStatement(whileStatement.Statement, plan);
                    break;
                case DoStatementSyntax doStatement:
                    PlanStatement(doStatement.Statement, plan);
                    break;
                case ForStatementSyntax forStatement:
                    PlanStatement(forStatement.Statement, plan);
                    break;
                case CommonForEachStatementSyntax foreachStatement:
                    PlanStatement(foreachStatement.Statement, plan);
                    break;
            }
        }

        /// <summary>
        /// Statements the emitter walks itself (rather than copying verbatim): their child
        /// statements are emitted one by one, so yield sites inside them are planned.
        /// Everything else (expression statements, declarations, lock, fixed, local
        /// functions, ...) is copied as a unit.
        /// </summary>
        public static bool IsStructuralStatement(StatementSyntax statement)
        {
            return statement is BlockSyntax || statement is TryStatementSyntax ||
                   statement is IfStatementSyntax || statement is UsingStatementSyntax ||
                   statement is SwitchStatementSyntax || statement is CheckedStatementSyntax ||
                   statement is UnsafeStatementSyntax || statement is LabeledStatementSyntax ||
                   IsLoopStatement(statement);
        }

        /// <summary>
        /// True for while / do / for / foreach (including deconstructing foreach).
        /// </summary>
        public static bool IsLoopStatement(SyntaxNode node)
        {
            return node is WhileStatementSyntax || node is ForStatementSyntax ||
                   node is CommonForEachStatementSyntax || node is DoStatementSyntax;
        }

        /// <summary>
        /// True for a lambda, anonymous method or local function: code inside runs when
        /// the delegate is invoked, not as part of the enclosing statement.
        /// </summary>
        public static bool IsFunctionBoundary(SyntaxNode node)
        {
            return node is AnonymousFunctionExpressionSyntax || node is LocalFunctionStatementSyntax;
        }

        /// <summary>
        /// True if every statement enclosing <paramref name="statement"/> up to
        /// <paramref name="body"/> is structural, i.e. the emitter reaches
        /// <paramref name="statement"/> by walking rather than by copying something that
        /// contains it.
        /// </summary>
        public static bool IsInWalkedPosition(StatementSyntax statement, SyntaxNode body)
        {
            if (statement == body) return true;
            for (var p = statement.Parent; p != null; p = p.Parent)
            {
                if (p == body) return true;
                if (IsFunctionBoundary(p)) return false;
                if (p is StatementSyntax s && !IsStructuralStatement(s)) return false;
                if (p is ExpressionSyntax) return false;
            }
            return false;
        }

        /// <summary>
        /// The first continuable call that belongs to <paramref name="statement"/> itself:
        /// not inside a lambda, local function or nested statement.
        /// </summary>
        public InvocationExpressionSyntax FindStatementContinuableCall(StatementSyntax statement)
        {
            if (!HasContinuableSet || statement is LocalFunctionStatementSyntax)
                return null;

            var nodes = statement.DescendantNodes(n =>
                n == statement || !(n is StatementSyntax || IsFunctionBoundary(n)));
            foreach (var invocation in nodes.OfType<InvocationExpressionSyntax>())
            {
                if (IsInsideFunction(invocation, statement)) continue;
                if (MatchesContinuable(invocation))
                    return invocation;
            }
            return null;
        }

        private static bool IsInsideFunction(SyntaxNode node, SyntaxNode stopAt)
        {
            for (var p = node.Parent; p != null && p != stopAt; p = p.Parent)
            {
                if (IsFunctionBoundary(p)) return true;
            }
            return false;
        }

        /// <summary>
        /// The invoked method's simple name (<c>M</c> for <c>M(...)</c>, <c>x.M(...)</c>
        /// and <c>x?.M(...)</c>), or an empty string.
        /// </summary>
        public static string GetSimpleMethodName(InvocationExpressionSyntax invocation)
        {
            return invocation.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
                _ => ""
            };
        }

        private static YieldPointInfo NewPlanPoint(Location location, YieldPointKind kind, List<YieldPointInfo> plan)
        {
            return new YieldPointInfo
            {
                Id = plan.Count,
                Location = location,
                Kind = kind
            };
        }

        /// <summary>
        /// Finds every yield point (loop header or continuable call) that is lexically inside a context the suspend/replay model
        /// cannot honour: a <c>finally</c> block, a <c>lock</c> statement body, or a
        /// <c>catch</c> exception filter (<c>when (...)</c>). Per whitepaper §10.2 these
        /// are hard errors — suspending out of a finally/lock/filter would abandon a
        /// CLR-managed region (the finally would not complete, the monitor would not be
        /// released, the filter cannot be re-entered) — so the emitter must diagnose and
        /// skip such methods rather than emit broken output (#24).
        /// </summary>
        public List<IllegalYieldContext> FindIllegalYieldContexts(MethodDeclarationSyntax method)
        {
            var results = new List<IllegalYieldContext>();
            if (method.Body == null) return results;

            foreach (var node in method.Body.DescendantNodes())
            {
                Location location = null;
                if (IsLoopStatement(node))
                {
                    location = node.GetFirstToken().GetLocation();
                }
                else if (node is InvocationExpressionSyntax invocation && IsYieldingInvocation(invocation))
                {
                    location = invocation.GetLocation();
                }

                if (location == null) continue;

                // Code inside a lambda or local function runs when the delegate is
                // invoked; it is not a yield point of this method.
                if (IsInsideFunction(node, method.Body)) continue;

                var kind = ClassifyEnclosingForbiddenRegion(node, method.Body);
                if (kind != ForbiddenRegionKind.None)
                {
                    results.Add(new IllegalYieldContext { Location = location, Region = kind });
                }
            }

            return results;
        }

        private bool IsYieldingInvocation(InvocationExpressionSyntax invocation)
        {
            if (_isContinuableCall != null) return _isContinuableCall(invocation);

            var simpleName = GetSimpleMethodName(invocation);

            if (_continuableMethodNames != null && _continuableMethodNames.Contains(simpleName))
                return true;

            // Standalone fallback (no name set supplied): already-suffixed calls.
            return _continuableMethodNames == null && simpleName.EndsWith("_Continuable");
        }

        /// <summary>
        /// Walks the ancestor chain of <paramref name="node"/> (stopping at the method
        /// body) and reports the innermost forbidden region it sits inside, if any.
        /// </summary>
        private static ForbiddenRegionKind ClassifyEnclosingForbiddenRegion(SyntaxNode node, SyntaxNode stopAt)
        {
            for (var current = node; current != null && current != stopAt.Parent; current = current.Parent)
            {
                var parent = current.Parent;
                if (parent == null) continue;

                // Inside a finally block.
                if (parent is FinallyClauseSyntax)
                    return ForbiddenRegionKind.Finally;

                // Inside a lock statement body (but not the lock's own expression).
                if (parent is LockStatementSyntax lockStmt && current == lockStmt.Statement)
                    return ForbiddenRegionKind.Lock;

                // Inside a catch filter clause: when (...).
                if (parent is CatchFilterClauseSyntax)
                    return ForbiddenRegionKind.CatchFilter;

                if (current == stopAt) break;
            }

            return ForbiddenRegionKind.None;
        }

        private class YieldPointVisitor : CSharpSyntaxWalker
        {
            private readonly List<YieldPointInfo> _yieldPoints;
            private readonly List<TryBlockInfo> _tryBlocks;
            private readonly HashSet<string> _continuableMethodNames;
            private readonly System.Func<InvocationExpressionSyntax, bool> _isContinuableCall;
            private int _nextYieldPointId = 0;
            private int _nextTryBlockId = 0;

            // Stack to track current try block nesting
            private readonly Stack<TryBlockInfo> _tryStack = new Stack<TryBlockInfo>();

            // Flag to track if we're currently in a finally block
            private bool _inFinallyBlock = false;
            private TryBlockInfo _currentTryForFinally = null;

            // Flag to track if we're currently in a catch block
            private bool _inCatchBlock = false;
            private CatchClauseInfo _currentCatchClause = null;

            public YieldPointVisitor(
                List<YieldPointInfo> yieldPoints,
                List<TryBlockInfo> tryBlocks,
                HashSet<string> continuableMethodNames = null,
                System.Func<InvocationExpressionSyntax, bool> isContinuableCall = null)
            {
                _yieldPoints = yieldPoints;
                _tryBlocks = tryBlocks;
                _continuableMethodNames = continuableMethodNames;
                _isContinuableCall = isContinuableCall;
            }

            private YieldPointInfo AddYieldPoint(Location location, YieldPointKind kind)
            {
                var yieldPoint = new YieldPointInfo
                {
                    Id = _nextYieldPointId++,
                    Location = location,
                    Kind = kind,
                    TryNestingDepth = _tryStack.Count
                };

                // Record enclosing finally blocks (from innermost to outermost)
                foreach (var tryBlock in _tryStack)
                {
                    if (tryBlock.HasFinally)
                    {
                        yieldPoint.EnclosingFinallyBlocks.Add(tryBlock.Id);
                    }
                }

                _yieldPoints.Add(yieldPoint);

                // Also add to the current try/catch/finally tracking
                if (_inFinallyBlock && _currentTryForFinally != null)
                {
                    _currentTryForFinally.YieldPointsInFinally.Add(yieldPoint);
                }
                else if (_inCatchBlock && _currentCatchClause != null)
                {
                    _currentCatchClause.YieldPoints.Add(yieldPoint);
                }
                else if (_tryStack.Count > 0)
                {
                    _tryStack.Peek().YieldPointsInTry.Add(yieldPoint);
                }

                return yieldPoint;
            }

            public override void VisitTryStatement(TryStatementSyntax node)
            {
                var tryBlock = new TryBlockInfo
                {
                    Id = _nextTryBlockId++,
                    Location = node.TryKeyword.GetLocation(),
                    HasFinally = node.Finally != null,
                    HasCatch = node.Catches.Count > 0
                };
                _tryBlocks.Add(tryBlock);
                _tryStack.Push(tryBlock);

                // Visit the try block
                Visit(node.Block);

                // Visit catch clauses
                foreach (var catchClause in node.Catches)
                {
                    var catchInfo = new CatchClauseInfo
                    {
                        ExceptionType = catchClause.Declaration?.Type.ToString() ?? "Exception",
                        VariableName = catchClause.Declaration?.Identifier.Text
                    };
                    tryBlock.CatchClauses.Add(catchInfo);

                    _inCatchBlock = true;
                    _currentCatchClause = catchInfo;
                    Visit(catchClause.Block);
                    _inCatchBlock = false;
                    _currentCatchClause = null;
                }

                // Visit finally block
                if (node.Finally != null)
                {
                    _inFinallyBlock = true;
                    _currentTryForFinally = tryBlock;
                    Visit(node.Finally.Block);
                    _inFinallyBlock = false;
                    _currentTryForFinally = null;
                }

                _tryStack.Pop();
            }

            public override void VisitWhileStatement(WhileStatementSyntax node)
            {
                // Yield point at the loop condition (back-edge)
                AddYieldPoint(node.WhileKeyword.GetLocation(), YieldPointKind.LoopBackEdge);
                base.VisitWhileStatement(node);
            }

            public override void VisitDoStatement(DoStatementSyntax node)
            {
                // Yield point at the while condition (back-edge)
                AddYieldPoint(node.WhileKeyword.GetLocation(), YieldPointKind.LoopBackEdge);
                base.VisitDoStatement(node);
            }

            public override void VisitForStatement(ForStatementSyntax node)
            {
                // Yield point at the for keyword (back-edge)
                AddYieldPoint(node.ForKeyword.GetLocation(), YieldPointKind.LoopBackEdge);
                base.VisitForStatement(node);
            }

            public override void VisitForEachStatement(ForEachStatementSyntax node)
            {
                // Yield point at the foreach keyword (back-edge)
                AddYieldPoint(node.ForEachKeyword.GetLocation(), YieldPointKind.LoopBackEdge);
                base.VisitForEachStatement(node);
            }

            public override void VisitGotoStatement(GotoStatementSyntax node)
            {
                // Yield point at backward gotos would require label analysis
                // For now, add a yield point at all gotos
                AddYieldPoint(node.GotoKeyword.GetLocation(), YieldPointKind.LoopBackEdge);
                base.VisitGotoStatement(node);
            }

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                var methodName = GetMethodName(node);
                var simpleMethodName = GetSimpleMethodName(node);

                // Generator mode (v1 replay model): the emitter supplies the set of
                // continuable method names by their ORIGINAL name (it rewrites them to
                // *_Continuable afterwards), so when the set is present we match on it
                // and emit a NestedMethodCall the replay emitter understands.
                var isContinuable = _isContinuableCall != null
                    ? _isContinuableCall(node)
                    : _continuableMethodNames != null && _continuableMethodNames.Contains(simpleMethodName);
                if (isContinuable)
                {
                    var yieldPoint = AddYieldPoint(node.GetLocation(), YieldPointKind.NestedMethodCall);
                    yieldPoint.CalledMethodName = simpleMethodName;
                    yieldPoint.InvocationSyntax = node;
                    yieldPoint.Description = $"Continuable call: {methodName}";
                }
                // Standalone analyzer mode: no name set supplied, so recognise the
                // already-suffixed *_Continuable form and report it as ContinuableCall.
                else if (_continuableMethodNames == null && _isContinuableCall == null && methodName.EndsWith("_Continuable"))
                {
                    var yieldPoint = AddYieldPoint(node.GetLocation(), YieldPointKind.ContinuableCall);
                    yieldPoint.CalledMethodName = simpleMethodName;
                    yieldPoint.InvocationSyntax = node;
                    yieldPoint.Description = $"Continuable call: {methodName}";
                }

                base.VisitInvocationExpression(node);
            }

            public override void VisitSwitchStatement(SwitchStatementSyntax node)
            {
                // Track switch statements for completeness (they may contain loops)
                base.VisitSwitchStatement(node);
            }

            public override void VisitSwitchExpression(SwitchExpressionSyntax node)
            {
                // Track switch expressions for completeness
                base.VisitSwitchExpression(node);
            }

            private static string GetMethodName(InvocationExpressionSyntax invocation)
            {
                return invocation.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    _ => ""
                };
            }

            private static string GetFullMethodName(InvocationExpressionSyntax invocation)
            {
                return invocation.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    MemberAccessExpressionSyntax ma => ma.ToString(),
                    _ => ""
                };
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
        }

        /// <summary>
        /// Checks if a method body contains any yield points.
        /// </summary>
        public bool HasYieldPoints(MethodDeclarationSyntax method)
        {
            return FindYieldPoints(method).Count > 0;
        }

        /// <summary>
        /// Gets a summary of yield points for documentation.
        /// </summary>
        public string GetYieldPointSummary(MethodDeclarationSyntax method)
        {
            var yieldPoints = FindYieldPoints(method);
            if (yieldPoints.Count == 0) return "No yield points";

            var loopCount = yieldPoints.Count(yp => yp.Kind == YieldPointKind.LoopBackEdge);
            var callCount = yieldPoints.Count(yp => yp.Kind == YieldPointKind.ContinuableCall);

            var parts = new List<string>();
            if (loopCount > 0) parts.Add($"{loopCount} loop(s)");
            if (callCount > 0) parts.Add($"{callCount} continuable call(s)");

            return string.Join(", ", parts);
        }
    }
}
