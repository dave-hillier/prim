using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Prim.Core;
using Prim.Roslyn;
using Xunit;

namespace Prim.Tests.Roslyn
{
    /// <summary>
    /// #22: there must be a SINGLE source of truth for yield-point IDs. The analyzer's
    /// emission plan (<see cref="YieldPointAnalyzer.PlanEmittedYieldPoints"/>) is what the
    /// emitter consumes, so the count of emitted yield sites, the IDs they are emitted
    /// with, and the "{Count} yield point(s)" doc comment must all agree with the plan.
    ///
    /// These tests run the real generator and parse its output, so they fail if the
    /// emitter ever drifts back to an independent counter.
    /// </summary>
    public class YieldPointIdConsistencyTests
    {
        private static GeneratorDriverRunResult Run(string source)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source);
            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.IEnumerable<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ContinuableAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Prim.Runtime.ScriptContext).Assembly.Location),
                MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
                MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("netstandard").Location),
            };
            var compilation = CSharpCompilation.Create(
                "YieldPointIdConsistencyTestAssembly",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(new ContinuationGenerator());
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
            return driver.GetRunResult();
        }

        private static string GeneratedText(GeneratorDriverRunResult result)
        {
            return result.GeneratedTrees.Single().ToString();
        }

        private const string Prologue = @"
using Prim.Core;
namespace TestNs { public partial class Sample {";
        private const string Epilogue = "} }";

        /// <summary>
        /// Asserts that the emitted yield-point IDs are exactly 0..N-1 (one per emitted
        /// site, contiguous, no gaps or duplicates) and that the doc comment count agrees.
        /// </summary>
        private static void AssertIdsConsistent(string method, int expectedCount)
        {
            var result = Run(Prologue + method + Epilogue);
            var fullText = GeneratedText(result);

            // Scope the analysis to the LAST emitted method (always 'M' in these tests):
            // helper methods may emit their own yield-point comments earlier in the file,
            // so we slice from the final "Supports suspension at" doc comment onward.
            var lastDoc = fullText.LastIndexOf("Supports suspension at", System.StringComparison.Ordinal);
            Assert.True(lastDoc >= 0, "Expected a yield-point count doc comment.");
            var text = fullText.Substring(lastDoc);

            // Doc comment count.
            var countMatch = Regex.Match(text, @"Supports suspension at (\d+) yield point\(s\)\.");
            Assert.True(countMatch.Success, "Expected a yield-point count doc comment.");
            Assert.Equal(expectedCount, int.Parse(countMatch.Groups[1].Value));

            // Every emitted yield site carries a "// Yield point N (...)" comment. Collect
            // the IDs in emission order.
            var ids = Regex.Matches(text, @"// Yield point (\d+) \(")
                .Select(m => int.Parse(m.Groups[1].Value))
                .ToList();

            Assert.Equal(expectedCount, ids.Count);
            // Contiguous 0..N-1 in emission order: single authority, no independent counter.
            Assert.Equal(Enumerable.Range(0, expectedCount).ToList(), ids);

            // Each HandleYieldPointWithBudget(id, ...) call uses an id that is in range.
            foreach (Match m in Regex.Matches(text, @"HandleYieldPointWithBudget\((\d+),"))
            {
                var id = int.Parse(m.Groups[1].Value);
                Assert.InRange(id, 0, expectedCount - 1);
            }
        }

        [Fact]
        public void SingleLoop_HasOneConsistentYieldPoint()
        {
            AssertIdsConsistent(@"
        [Continuable]
        public int M()
        {
            int sum = 0;
            for (int i = 0; i < 3; i++) { sum += i; }
            return sum;
        }", expectedCount: 1);
        }

        [Fact]
        public void LoopPlusNestedCall_HasTwoConsistentYieldPointsInEmissionOrder()
        {
            // A loop (id 0) whose body contains a continuable call (id 1). This is the
            // representative "loop + nested-call mix" the unification must get right: the
            // analyzer used to count the nested call (sub-expression) differently from the
            // emitter, producing a count that disagreed with the emitted IDs.
            AssertIdsConsistent(@"
        [Continuable]
        public int Helper()
        {
            int s = 0;
            for (int i = 0; i < 2; i++) { s += i; }
            return s;
        }

        [Continuable]
        public int M()
        {
            int total = 0;
            for (int i = 0; i < 3; i++)
            {
                total += Helper();
            }
            return total;
        }", expectedCount: 2);
        }

        [Fact]
        public void SubExpressionNestedCall_IsCountedExactlyOnce()
        {
            // #21: a continuable call used as a SUB-EXPRESSION (int x = Helper() * 2;) is a
            // single yield point and the count agrees with the emitted id.
            AssertIdsConsistent(@"
        [Continuable]
        public int Helper()
        {
            int s = 0;
            for (int i = 0; i < 2; i++) { s += i; }
            return s;
        }

        [Continuable]
        public int M()
        {
            int x = Helper() * 2;
            return x;
        }", expectedCount: 1);
        }

        [Fact]
        public void GotoIsNotAnEmittedYieldPoint_ButBroadAnalyzerStillSeesIt()
        {
            // A method with a goto. The EMITTER does not emit a HandleYieldPoint for a
            // goto (the replay model does not transform gotos), so the emission plan has
            // zero points and the doc comment says 0 — consistent with what is emitted.
            // The broad analyzer (used for diagnostics/other analysis) still records the
            // goto, which is fine: the plan, not the broad walk, is the emission authority.
            var src = Prologue + @"
        [Continuable]
        public int M()
        {
            int x = 0;
        start:
            x++;
            if (x < 3) goto start;
            return x;
        }" + Epilogue;

            var result = Run(src);
            var text = GeneratedText(result);

            var countMatch = Regex.Match(text, @"Supports suspension at (\d+) yield point\(s\)\.");
            Assert.True(countMatch.Success);
            Assert.Equal(0, int.Parse(countMatch.Groups[1].Value));
            Assert.DoesNotContain("// Yield point", text);

            // Ground-truth: the broad analyzer DOES count the goto, proving the plan is a
            // deliberately narrower, emission-faithful set rather than the broad walk.
            var method = CSharpSyntaxTree.ParseText(src).GetRoot()
                .DescendantNodes().OfType<MethodDeclarationSyntax>().First();
            var broad = new YieldPointAnalyzer().FindYieldPoints(method);
            Assert.Single(broad);
            var plan = new YieldPointAnalyzer().PlanEmittedYieldPoints(method);
            Assert.Empty(plan);
        }
    }
}
