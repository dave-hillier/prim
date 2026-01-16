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
        ExplicitYield
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
            var visitor = new YieldPointVisitor(yieldPoints);
            visitor.Visit(method.Body);
            return yieldPoints;
        }

        private class YieldPointVisitor : CSharpSyntaxWalker
        {
            private readonly List<YieldPointInfo> _yieldPoints;
            private int _nextId = 0;

            public YieldPointVisitor(List<YieldPointInfo> yieldPoints)
            {
                _yieldPoints = yieldPoints;
            }

            public override void VisitWhileStatement(WhileStatementSyntax node)
            {
                // Yield point at the loop condition (back-edge)
                _yieldPoints.Add(new YieldPointInfo
                {
                    Id = _nextId++,
                    Location = node.WhileKeyword.GetLocation(),
                    Kind = YieldPointKind.LoopBackEdge
                });
                base.VisitWhileStatement(node);
            }

            public override void VisitDoStatement(DoStatementSyntax node)
            {
                // Yield point at the while condition (back-edge)
                _yieldPoints.Add(new YieldPointInfo
                {
                    Id = _nextId++,
                    Location = node.WhileKeyword.GetLocation(),
                    Kind = YieldPointKind.LoopBackEdge
                });
                base.VisitDoStatement(node);
            }

            public override void VisitForStatement(ForStatementSyntax node)
            {
                // Yield point at the for keyword (back-edge)
                _yieldPoints.Add(new YieldPointInfo
                {
                    Id = _nextId++,
                    Location = node.ForKeyword.GetLocation(),
                    Kind = YieldPointKind.LoopBackEdge
                });
                base.VisitForStatement(node);
            }

            public override void VisitForEachStatement(ForEachStatementSyntax node)
            {
                // Yield point at the foreach keyword (back-edge)
                _yieldPoints.Add(new YieldPointInfo
                {
                    Id = _nextId++,
                    Location = node.ForEachKeyword.GetLocation(),
                    Kind = YieldPointKind.LoopBackEdge
                });
                base.VisitForEachStatement(node);
            }

            public override void VisitGotoStatement(GotoStatementSyntax node)
            {
                // Yield point at backward gotos would require label analysis
                // For now, add a yield point at all gotos
                _yieldPoints.Add(new YieldPointInfo
                {
                    Id = _nextId++,
                    Location = node.GotoKeyword.GetLocation(),
                    Kind = YieldPointKind.LoopBackEdge
                });
                base.VisitGotoStatement(node);
            }

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                // Check if this is a call to Suspend.Yield or similar
                var methodName = GetMethodName(node);
                if (methodName == "Yield" || methodName == "CheckYield" ||
                    methodName.EndsWith(".Yield") || methodName.EndsWith(".CheckYield"))
                {
                    _yieldPoints.Add(new YieldPointInfo
                    {
                        Id = _nextId++,
                        Location = node.GetLocation(),
                        Kind = YieldPointKind.ExplicitYield
                    });
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
        }
    }
}
