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
        /// Method exit point.
        /// </summary>
        MethodExit,

        /// <summary>
        /// Explicit yield call.
        /// </summary>
        ExplicitYield,

        /// <summary>
        /// A call to another continuable method that may yield.
        /// </summary>
        NestedMethodCall,

        /// <summary>
        /// Call to an already-suffixed *_Continuable method, reported in standalone mode
        /// (no continuable name set supplied).
        /// </summary>
        ContinuableCall,

        /// <summary>
        /// Await expression (for async methods that need continuation support).
        /// </summary>
        AwaitExpression
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
    ///   - each loop header (while / for / foreach / do)  -> LoopBackEdge
    ///   - each explicit Yield() / CheckYield() call       -> ExplicitYield
    ///   - each statement that contains a call to a known continuable method
    ///     (statement granularity, including sub-expression calls) -> NestedMethodCall
    ///
    /// Pass the set of continuable method names so nested calls are recognised by their
    /// ORIGINAL name (the emitter rewrites them to *_Continuable afterwards). When the
    /// set is null/empty the analyzer falls back to recognising already-suffixed
    /// *_Continuable names, which keeps the standalone analyzer unit tests meaningful.
    /// </summary>
    public class YieldPointAnalyzer
    {
        private readonly HashSet<string> _continuableMethodNames;

        public YieldPointAnalyzer()
        {
        }

        public YieldPointAnalyzer(HashSet<string> continuableMethodNames)
        {
            _continuableMethodNames = continuableMethodNames;
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
            var visitor = new YieldPointVisitor(yieldPoints, tryBlocks, _continuableMethodNames);
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
        /// The traversal mirrors <c>ContinuationGenerator.GenerateStatement</c> exactly:
        ///   - a loop statement (while/for/foreach/do) contributes one LoopBackEdge point
        ///     at its header, THEN its body is walked;
        ///   - a non-control-flow statement that contains a call to a known continuable
        ///     method contributes exactly one NestedMethodCall point (statement
        ///     granularity, including sub-expression calls);
        ///   - if/try/using/block recurse into their children in source order.
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
            // Loop header yield point (emitter: GenerateStatement, before recursing).
            var isLoop = statement is WhileStatementSyntax || statement is ForStatementSyntax ||
                         statement is ForEachStatementSyntax || statement is DoStatementSyntax;
            if (isLoop)
            {
                plan.Add(NewPlanPoint(statement.GetFirstToken().GetLocation(), YieldPointKind.LoopBackEdge, plan));
            }

            // Statement-level continuable call (emitter: FindContinuableMethodCall +
            // GenerateNestedMethodCallStatement). Excluded for control-flow statements,
            // which recurse into their children instead.
            if (!IsControlFlowStatement(statement))
            {
                var call = FindStatementContinuableCall(statement);
                if (call != null)
                {
                    var point = NewPlanPoint(call.GetLocation(), YieldPointKind.NestedMethodCall, plan);
                    point.CalledMethodName = GetSimpleMethodName(call);
                    point.InvocationSyntax = call;
                    plan.Add(point);
                    return; // emitter returns after handling the nested call statement
                }
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
                    if (usingStatement.Statement is BlockSyntax usingBlock)
                        PlanStatements(usingBlock.Statements, plan);
                    else
                        PlanStatement(usingStatement.Statement, plan);
                    break;
                case IfStatementSyntax ifStatement:
                    PlanBranch(ifStatement.Statement, plan);
                    if (ifStatement.Else != null)
                        PlanBranch(ifStatement.Else.Statement, plan);
                    break;
                case WhileStatementSyntax whileStatement:
                    PlanBranch(whileStatement.Statement, plan);
                    break;
                case ForStatementSyntax forStatement:
                    PlanBranch(forStatement.Statement, plan);
                    break;
                case ForEachStatementSyntax foreachStatement:
                    PlanBranch(foreachStatement.Statement, plan);
                    break;
                case DoStatementSyntax doStatement:
                    PlanBranch(doStatement.Statement, plan);
                    break;
            }
        }

        private void PlanBranch(StatementSyntax statement, List<YieldPointInfo> plan)
        {
            if (statement is BlockSyntax block)
                PlanStatements(block.Statements, plan);
            else
                PlanStatement(statement, plan);
        }

        private static bool IsControlFlowStatement(StatementSyntax statement)
        {
            return statement is BlockSyntax || statement is TryStatementSyntax ||
                   statement is IfStatementSyntax || statement is WhileStatementSyntax ||
                   statement is ForStatementSyntax || statement is ForEachStatementSyntax ||
                   statement is DoStatementSyntax || statement is UsingStatementSyntax;
        }

        private InvocationExpressionSyntax FindStatementContinuableCall(StatementSyntax statement)
        {
            if (_continuableMethodNames == null || _continuableMethodNames.Count == 0)
                return null;

            foreach (var invocation in statement.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (_continuableMethodNames.Contains(GetSimpleMethodName(invocation)))
                    return invocation;
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
        /// Finds every yield point (loop header, explicit Yield()/CheckYield(), or
        /// continuable call) that is lexically inside a context the suspend/replay model
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

                var kind = ClassifyEnclosingForbiddenRegion(node, method.Body);
                if (kind != ForbiddenRegionKind.None)
                {
                    results.Add(new IllegalYieldContext { Location = location, Region = kind });
                }
            }

            return results;
        }

        private static bool IsLoopStatement(SyntaxNode node)
        {
            return node is WhileStatementSyntax || node is ForStatementSyntax ||
                   node is ForEachStatementSyntax || node is DoStatementSyntax;
        }

        private bool IsYieldingInvocation(InvocationExpressionSyntax invocation)
        {
            var simpleName = GetSimpleMethodName(invocation);
            var fullName = invocation.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax ma => ma.ToString(),
                _ => ""
            };

            if (fullName == "Yield" || fullName == "CheckYield" ||
                fullName.EndsWith(".Yield") || fullName.EndsWith(".CheckYield"))
            {
                return true;
            }

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

            public YieldPointVisitor(List<YieldPointInfo> yieldPoints, List<TryBlockInfo> tryBlocks, HashSet<string> continuableMethodNames = null)
            {
                _yieldPoints = yieldPoints;
                _tryBlocks = tryBlocks;
                _continuableMethodNames = continuableMethodNames;
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

                // Check if this is a call to Suspend.Yield or similar
                if (methodName == "Yield" || methodName == "CheckYield" ||
                    methodName.EndsWith(".Yield") || methodName.EndsWith(".CheckYield"))
                {
                    var explicitPoint = AddYieldPoint(node.GetLocation(), YieldPointKind.ExplicitYield);
                    explicitPoint.Description = $"Explicit yield: {methodName}";
                }
                // Generator mode (v1 replay model): the emitter supplies the set of
                // continuable method names by their ORIGINAL name (it rewrites them to
                // *_Continuable afterwards), so when the set is present we match on it
                // and emit a NestedMethodCall the replay emitter understands.
                else if (_continuableMethodNames != null && _continuableMethodNames.Contains(simpleMethodName))
                {
                    var yieldPoint = AddYieldPoint(node.GetLocation(), YieldPointKind.NestedMethodCall);
                    yieldPoint.CalledMethodName = simpleMethodName;
                    yieldPoint.InvocationSyntax = node;
                    yieldPoint.Description = $"Continuable call: {methodName}";
                }
                // Standalone analyzer mode: no name set supplied, so recognise the
                // already-suffixed *_Continuable form and report it as ContinuableCall.
                else if (_continuableMethodNames == null && methodName.EndsWith("_Continuable"))
                {
                    var yieldPoint = AddYieldPoint(node.GetLocation(), YieldPointKind.ContinuableCall);
                    yieldPoint.CalledMethodName = simpleMethodName;
                    yieldPoint.InvocationSyntax = node;
                    yieldPoint.Description = $"Continuable call: {methodName}";
                }

                base.VisitInvocationExpression(node);
            }

            public override void VisitAwaitExpression(AwaitExpressionSyntax node)
            {
                // Await expressions are potential yield points in async-continuable methods
                var awaitPoint = AddYieldPoint(node.GetLocation(), YieldPointKind.AwaitExpression);
                awaitPoint.Description = $"Await: {node.Expression}";
                base.VisitAwaitExpression(node);
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
            var explicitCount = yieldPoints.Count(yp => yp.Kind == YieldPointKind.ExplicitYield);
            var callCount = yieldPoints.Count(yp => yp.Kind == YieldPointKind.ContinuableCall);
            var awaitCount = yieldPoints.Count(yp => yp.Kind == YieldPointKind.AwaitExpression);

            var parts = new List<string>();
            if (loopCount > 0) parts.Add($"{loopCount} loop(s)");
            if (explicitCount > 0) parts.Add($"{explicitCount} explicit yield(s)");
            if (callCount > 0) parts.Add($"{callCount} continuable call(s)");
            if (awaitCount > 0) parts.Add($"{awaitCount} await(s)");

            return string.Join(", ", parts);
        }
    }
}
