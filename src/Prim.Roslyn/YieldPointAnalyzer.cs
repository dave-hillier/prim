using System.Collections.Generic;
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
        NestedMethodCall
    }

    /// <summary>
    /// Analyzes C# syntax to find yield points.
    /// </summary>
    public class YieldPointAnalyzer
    {
        /// <summary>
        /// Finds all yield points in a method.
        /// </summary>
        public List<YieldPointInfo> FindYieldPoints(MethodDeclarationSyntax method)
        {
            var yieldPoints = new List<YieldPointInfo>();
            var tryBlocks = new List<TryBlockInfo>();
            var visitor = new YieldPointVisitor(yieldPoints, tryBlocks);
            visitor.Visit(method.Body);
            return yieldPoints;
        }

        /// <summary>
        /// Finds all try-catch-finally blocks in a method along with yield points.
        /// </summary>
        public (List<YieldPointInfo> YieldPoints, List<TryBlockInfo> TryBlocks) FindYieldPointsAndTryBlocks(MethodDeclarationSyntax method)
        {
            var yieldPoints = new List<YieldPointInfo>();
            var tryBlocks = new List<TryBlockInfo>();
            var visitor = new YieldPointVisitor(yieldPoints, tryBlocks);
            visitor.Visit(method.Body);
            return (yieldPoints, tryBlocks);
        }

        private class YieldPointVisitor : CSharpSyntaxWalker
        {
            private readonly List<YieldPointInfo> _yieldPoints;
            private readonly List<TryBlockInfo> _tryBlocks;
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

            public YieldPointVisitor(List<YieldPointInfo> yieldPoints, List<TryBlockInfo> tryBlocks)
            {
                _yieldPoints = yieldPoints;
                _tryBlocks = tryBlocks;
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
                    AddYieldPoint(node.GetLocation(), YieldPointKind.ExplicitYield);
                }
                // Check if this is a call to a continuable method (ending with _Continuable)
                else if (simpleMethodName.EndsWith("_Continuable"))
                {
                    var yieldPoint = AddYieldPoint(node.GetLocation(), YieldPointKind.NestedMethodCall);
                    yieldPoint.CalledMethodName = simpleMethodName;
                    yieldPoint.InvocationSyntax = node;
                }

                base.VisitInvocationExpression(node);
            }

            private static string GetMethodName(InvocationExpressionSyntax invocation)
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
    }
}
