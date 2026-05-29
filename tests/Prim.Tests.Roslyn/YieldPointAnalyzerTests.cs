using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Prim.Roslyn;
using System.Linq;
using Xunit;

namespace Prim.Tests.Roslyn
{
    public class YieldPointAnalyzerTests
    {
        private readonly YieldPointAnalyzer _analyzer = new YieldPointAnalyzer();

        #region While Loop Tests

        [Fact]
        public void FindYieldPoints_WhileLoop_ReturnsOneYieldPoint()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    while (true) { }
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Single(yieldPoints);
            Assert.Equal(YieldPointKind.LoopBackEdge, yieldPoints[0].Kind);
        }

        [Fact]
        public void FindYieldPoints_NestedWhileLoops_ReturnsTwoYieldPoints()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    while (true)
                    {
                        while (true) { }
                    }
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Equal(2, yieldPoints.Count);
            Assert.All(yieldPoints, yp => Assert.Equal(YieldPointKind.LoopBackEdge, yp.Kind));
        }

        #endregion

        #region For Loop Tests

        [Fact]
        public void FindYieldPoints_ForLoop_ReturnsOneYieldPoint()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    for (int i = 0; i < 10; i++) { }
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Single(yieldPoints);
            Assert.Equal(YieldPointKind.LoopBackEdge, yieldPoints[0].Kind);
        }

        [Fact]
        public void FindYieldPoints_MultipleForLoops_ReturnsCorrectCount()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    for (int i = 0; i < 10; i++) { }
                    for (int j = 0; j < 5; j++) { }
                    for (int k = 0; k < 3; k++) { }
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Equal(3, yieldPoints.Count);
        }

        #endregion

        #region ForEach Loop Tests

        [Fact]
        public void FindYieldPoints_ForEachLoop_ReturnsOneYieldPoint()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    foreach (var item in new int[] { 1, 2, 3 }) { }
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Single(yieldPoints);
            Assert.Equal(YieldPointKind.LoopBackEdge, yieldPoints[0].Kind);
        }

        #endregion

        #region Do-While Loop Tests

        [Fact]
        public void FindYieldPoints_DoWhileLoop_ReturnsOneYieldPoint()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    do { } while (true);
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Single(yieldPoints);
            Assert.Equal(YieldPointKind.LoopBackEdge, yieldPoints[0].Kind);
        }

        #endregion

        #region Goto Statement Tests

        [Fact]
        public void FindYieldPoints_GotoStatement_ReturnsYieldPoint()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    start:
                    goto start;
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Single(yieldPoints);
            Assert.Equal(YieldPointKind.LoopBackEdge, yieldPoints[0].Kind);
        }

        #endregion

        #region Explicit Yield Tests

        [Fact]
        public void FindYieldPoints_ExplicitYieldCall_ReturnsExplicitYieldPoint()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    Yield();
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Single(yieldPoints);
            Assert.Equal(YieldPointKind.ExplicitYield, yieldPoints[0].Kind);
        }

        [Fact]
        public void FindYieldPoints_CheckYieldCall_ReturnsExplicitYieldPoint()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    CheckYield();
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Single(yieldPoints);
            Assert.Equal(YieldPointKind.ExplicitYield, yieldPoints[0].Kind);
        }

        [Fact]
        public void FindYieldPoints_QualifiedYieldCall_ReturnsExplicitYieldPoint()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    Context.Yield();
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Single(yieldPoints);
            Assert.Equal(YieldPointKind.ExplicitYield, yieldPoints[0].Kind);
        }

        #endregion

        #region No Yield Points Tests

        [Fact]
        public void FindYieldPoints_NoLoops_ReturnsEmpty()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    var x = 1;
                    var y = 2;
                    var z = x + y;
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Empty(yieldPoints);
        }

        [Fact]
        public void FindYieldPoints_EmptyMethod_ReturnsEmpty()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Empty(yieldPoints);
        }

        [Fact]
        public void FindYieldPoints_IfStatement_ReturnsEmpty()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    if (true) { }
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Empty(yieldPoints);
        }

        #endregion

        #region Mixed Tests

        [Fact]
        public void FindYieldPoints_MixedLoopsAndYields_ReturnsAll()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        Yield();
                    }
                    while (true) { }
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Equal(3, yieldPoints.Count);
            Assert.Contains(yieldPoints, yp => yp.Kind == YieldPointKind.ExplicitYield);
            Assert.Equal(2, yieldPoints.Count(yp => yp.Kind == YieldPointKind.LoopBackEdge));
        }

        [Fact]
        public void FindYieldPoints_AssignsSequentialIds()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    for (int i = 0; i < 10; i++) { }
                    while (true) { }
                    foreach (var x in new int[0]) { }
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Equal(3, yieldPoints.Count);
            Assert.Equal(0, yieldPoints[0].Id);
            Assert.Equal(1, yieldPoints[1].Id);
            Assert.Equal(2, yieldPoints[2].Id);
        }

        [Fact]
        public void FindYieldPoints_YieldPointsHaveLocation()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    while (true) { }
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Single(yieldPoints);
            Assert.NotNull(yieldPoints[0].Location);
        }

        #endregion

        #region Complex Scenarios

        [Fact]
        public void FindYieldPoints_ComplexMethod_ReturnsCorrectCount()
        {
            var method = ParseMethod(@"
                public int ComplexMethod()
                {
                    int sum = 0;
                    for (int i = 0; i < 10; i++)
                    {
                        if (i % 2 == 0)
                        {
                            while (sum < 100)
                            {
                                sum++;
                                if (sum > 50)
                                {
                                    Yield();
                                }
                            }
                        }
                        else
                        {
                            foreach (var j in new[] { 1, 2, 3 })
                            {
                                sum += j;
                            }
                        }
                    }
                    return sum;
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            // for loop, while loop, Yield() call, foreach loop = 4 yield points
            Assert.Equal(4, yieldPoints.Count);
        }

        [Fact]
        public void FindYieldPoints_DoWhileInsideFor_ReturnsTwoYieldPoints()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    for (int i = 0; i < 5; i++)
                    {
                        do
                        {
                            i++;
                        } while (i < 3);
                    }
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Equal(2, yieldPoints.Count);
        }

        #endregion

        #region YieldPointInfo Tests

        [Fact]
        public void YieldPointInfo_PropertiesAreSettable()
        {
            var info = new YieldPointInfo
            {
                Id = 42,
                Kind = YieldPointKind.LoopBackEdge,
                Description = "Test description"
            };

            Assert.Equal(42, info.Id);
            Assert.Equal(YieldPointKind.LoopBackEdge, info.Kind);
            Assert.Equal("Test description", info.Description);
        }

        [Fact]
        public void YieldPointKind_AllValuesAreDefined()
        {
            Assert.True(System.Enum.IsDefined(typeof(YieldPointKind), YieldPointKind.LoopBackEdge));
            Assert.True(System.Enum.IsDefined(typeof(YieldPointKind), YieldPointKind.MethodExit));
            Assert.True(System.Enum.IsDefined(typeof(YieldPointKind), YieldPointKind.ExplicitYield));
            Assert.True(System.Enum.IsDefined(typeof(YieldPointKind), YieldPointKind.ContinuableCall));
            Assert.True(System.Enum.IsDefined(typeof(YieldPointKind), YieldPointKind.AwaitExpression));
        }

        #endregion

        #region Continuable Call Tests

        [Fact]
        public void FindYieldPoints_ContinuableMethodCall_ReturnsContinuableCallYieldPoint()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    SomeMethod_Continuable();
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Single(yieldPoints);
            Assert.Equal(YieldPointKind.ContinuableCall, yieldPoints[0].Kind);
            Assert.Contains("Continuable call", yieldPoints[0].Description);
        }

        [Fact]
        public void FindYieldPoints_QualifiedContinuableCall_ReturnsContinuableCallYieldPoint()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    instance.DoWork_Continuable();
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Single(yieldPoints);
            Assert.Equal(YieldPointKind.ContinuableCall, yieldPoints[0].Kind);
        }

        #endregion

        #region Await Expression Tests

        [Fact]
        public void FindYieldPoints_AwaitExpression_ReturnsAwaitYieldPoint()
        {
            var method = ParseMethod(@"
                public async void TestMethod()
                {
                    await Task.Delay(100);
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Single(yieldPoints);
            Assert.Equal(YieldPointKind.AwaitExpression, yieldPoints[0].Kind);
            Assert.Contains("Await", yieldPoints[0].Description);
        }

        [Fact]
        public void FindYieldPoints_MultipleAwaits_ReturnsAllAwaitYieldPoints()
        {
            var method = ParseMethod(@"
                public async void TestMethod()
                {
                    await Task.Delay(100);
                    await Task.Delay(200);
                    await Task.Delay(300);
                }");

            var yieldPoints = _analyzer.FindYieldPoints(method);

            Assert.Equal(3, yieldPoints.Count);
            Assert.All(yieldPoints, yp => Assert.Equal(YieldPointKind.AwaitExpression, yp.Kind));
        }

        #endregion

        #region Helper Method Tests

        [Fact]
        public void HasYieldPoints_MethodWithLoop_ReturnsTrue()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    while (true) { }
                }");

            Assert.True(_analyzer.HasYieldPoints(method));
        }

        [Fact]
        public void HasYieldPoints_MethodWithoutLoop_ReturnsFalse()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    var x = 1;
                }");

            Assert.False(_analyzer.HasYieldPoints(method));
        }

        [Fact]
        public void GetYieldPointSummary_NoYieldPoints_ReturnsNoYieldPoints()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    var x = 1;
                }");

            var summary = _analyzer.GetYieldPointSummary(method);

            Assert.Equal("No yield points", summary);
        }

        [Fact]
        public void GetYieldPointSummary_WithLoops_ReturnsLoopCount()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    for (int i = 0; i < 10; i++) { }
                    while (true) { }
                }");

            var summary = _analyzer.GetYieldPointSummary(method);

            Assert.Contains("2 loop(s)", summary);
        }

        [Fact]
        public void GetYieldPointSummary_WithMixedYieldPoints_ReturnsAll()
        {
            var method = ParseMethod(@"
                public void TestMethod()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        Yield();
                    }
                }");

            var summary = _analyzer.GetYieldPointSummary(method);

            Assert.Contains("1 loop(s)", summary);
            Assert.Contains("1 explicit yield(s)", summary);
        }

        #endregion

        #region Helper Methods

        private static MethodDeclarationSyntax ParseMethod(string methodCode)
        {
            var code = $@"
                class TestClass
                {{
                    {methodCode}
                }}";

            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetCompilationUnitRoot();
            var method = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .First();

            return method;
        }

        #endregion
    }
}
