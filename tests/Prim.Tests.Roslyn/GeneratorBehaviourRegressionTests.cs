using System.Linq;
using Xunit;

namespace Prim.Tests.Roslyn
{
    /// <summary>
    /// Regression tests for generated code that compiled but behaved wrongly (review
    /// items A1-A7). Each snippet is compiled with the generator, the generated method is
    /// run with a small instruction budget so it suspends at many yield points (not just
    /// the first), and every resume must still produce the plain method's result.
    /// </summary>
    public class GeneratorBehaviourRegressionTests
    {
        private const string Usings = @"
using System;
using System.Collections.Generic;
using Prim.Core;
";

        /// <summary>
        /// Compiles the snippet and checks the generated method's result: suspended at
        /// the first yield site and resumed, and run in budget slices so it suspends at
        /// many sites. With <paramref name="json"/> the sliced run is repeated with a
        /// JSON round-trip of the state at every suspension (only for snippets whose
        /// hoisted locals are serializable). Returns the budget-sliced outcome.
        /// </summary>
        private static GeneratorHarness.Outcome<T> AssertSameResult<T>(string source, T expected, int budget = 20, bool json = false, params string[] methods)
        {
            var go = GeneratorHarness.CompileEntry<T>(source, methods);
            var unsliced = GeneratorHarness.RunToCompletion(go, int.MaxValue);
            Assert.Equal(expected, unsliced.Value);
            Assert.Equal(0, unsliced.Suspensions);
            Assert.Equal(expected, GeneratorHarness.SuspendAtFirstYieldThenResume(go));
            var sliced = GeneratorHarness.RunToCompletion(go, budget);
            Assert.Equal(expected, sliced.Value);
            if (json)
            {
                var serialized = GeneratorHarness.RunToCompletion(go, budget, serialize: true);
                Assert.Equal(expected, serialized.Value);
                Assert.Equal(sliced.Suspensions, serialized.Suspensions);
            }
            return sliced;
        }

        // ---- A1: brace-less embedded statements ----

        private const string BracelessIf = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int M(bool flag)
        {
            int t = 0;
            int n = 30;
            if (flag) while (n > 0) { t += n; n--; }
            return t;
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(FLAG); }
}";

        [Fact]
        public void A1_BracelessIfAroundLoop_FalseConditionSkipsLoop()
        {
            // Before the fix the loop's yield call became the if body and the loop ran
            // unconditionally, returning 465.
            AssertSameResult(BracelessIf.Replace("FLAG", "false"), 0, methods: "M");
        }

        [Fact]
        public void A1_BracelessIfAroundLoop_TrueConditionRunsLoop()
        {
            var outcome = AssertSameResult(BracelessIf.Replace("FLAG", "true"), 465, json: true, methods: "M");
            Assert.True(outcome.Suspensions > 1);
        }

        [Fact]
        public void A1_BracelessIfAroundTry_FalseConditionSkipsTry()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int M(bool c)
        {
            int t = 1;
            if (c) try { for (int i = 0; i < 10; i++) t += i; } catch (InvalidOperationException) { t = -1; }
            return t;
        }

        [Continuable]
        public int Both() { int a = M(false); int b = M(true); return a * 1000 + b; }
    }
    public static class Entry { public static int Go() => new C().Both_Continuable(); }
}";
            AssertSameResult(src, 1 * 1000 + 46, methods: "M");
        }

        [Fact]
        public void A1_NestedBracelessForeach_CompilesAndSums()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int M()
        {
            int t = 0;
            var a = new[] { new[] { 1, 2 }, new[] { 3, 4, 5 } };
            foreach (var r in a) foreach (var v in r) t += v;
            return t;
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(); }
}";
            var outcome = AssertSameResult(src, 15, budget: 6, methods: "M");
            Assert.True(outcome.Suspensions > 1);
        }

        [Fact]
        public void A1_BracelessElseAndLabel_KeepTheirBodies()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int Helper(int n) { int s = 0; for (int i = 1; i <= n; i++) s += i; return s; }

        [Continuable]
        public int M(int k)
        {
            int t = 0;
            if (k > 100) t = -1;
            else if (k > 5) t += Helper(k);
            else t += Helper(1);
            return t;
        }

        [Continuable]
        public int Both() { int a = M(10); int b = M(2); return a * 10 + b; }
    }
    public static class Entry { public static int Go() => new C().Both_Continuable(); }
}";
            AssertSameResult(src, 55 * 10 + 1, budget: 8, json: true, methods: new[] { "M", "Helper" });
        }

        // ---- A2: locals declared inside statements copied verbatim ----

        [Fact]
        public void A2_LocalInSwitchSection_IsReadCorrectly()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int M(int m)
        {
            int t = 0;
            for (int i = 0; i < 3; i++)
            {
                switch (m)
                {
                    case 1:
                        int y = 5;
                        t += y;
                        break;
                    case 2:
                        y = 7;
                        t += y;
                        break;
                    default:
                        t = -1;
                        break;
                }
            }
            return t;
        }

        [Continuable]
        public int Both() { int a = M(1); int b = M(2); return a * 100 + b; }
    }
    public static class Entry { public static int Go() => new C().Both_Continuable(); }
}";
            AssertSameResult(src, 15 * 100 + 21, budget: 10, json: true, methods: "M");
        }

        [Fact]
        public void A2_LocalsInLambdaLocalFunctionLockAndDeconstruction()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        private readonly object _gate = new object();

        [Continuable]
        public int M()
        {
            int t = 0;
            Func<int, int> twice = x => { int d = x * 2; return d; };
            int Add(int a) { int extra = 1; return a + extra; }
            lock (_gate) { int inLock = 3; t += inLock; }
            var pairs = new List<(int, int)> { (1, 2), (3, 4) };
            foreach (var (a, b) in pairs) { int s = a + b; t += s; }
            for (int i = 0; i < 3; i++) t += twice(i) + Add(i);
            return t;
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(); }
}";
            // 3 + (3 + 7) + sum(i*2 + i + 1 for i in 0..2) = 13 + 12 = 25
            AssertSameResult(src, 25, budget: 10, methods: "M");
        }

        [Fact]
        public void A2_OutVarsAndPatternVariables_KeepTheirNames()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int M(object o)
        {
            int t = 0;
            if (!int.TryParse(""12"", out var parsed)) return -1;
            t += parsed;
            if (o is int boxed) t += boxed;
            for (int i = 0; i < 4; i++) { if (o is int again && again > i) t++; }
            return t;
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(3); }
}";
            AssertSameResult(src, 12 + 3 + 3, budget: 5, methods: "M");
        }

        // ---- A3: catch filters ----

        [Fact]
        public void A3_UserCatchFiltersAreKept()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int M(string message)
        {
            int t = 0;
            for (int i = 0; i < 6; i++) t++;
            try
            {
                for (int i = 0; i < 3; i++) t++;
                throw new InvalidOperationException(message);
            }
            catch (InvalidOperationException e) when (e.Message.Length > 3)
            {
                t += 100;
            }
            catch (Exception) when (t > 5)
            {
                t += 1000;
            }
            return t;
        }

        [Continuable]
        public int Both() { int a = M(""long message""); int b = M(""no""); return a * 10000 + b; }
    }
    public static class Entry { public static int Go() => new C().Both_Continuable(); }
}";
            AssertSameResult(src, 109 * 10000 + 1009, budget: 6, json: true, methods: "M");
        }

        [Fact]
        public void A3_FilterThatRejects_LetsExceptionPropagate()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int M()
        {
            int t = 0;
            try
            {
                try { for (int i = 0; i < 3; i++) t++; throw new InvalidOperationException(""x""); }
                catch when (t > 50) { t = -1; }
            }
            catch (InvalidOperationException) { t += 10; }
            return t;
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(); }
}";
            AssertSameResult(src, 13, budget: 4, methods: "M");
        }

        // ---- A4: using declarations ----

        [Fact]
        public void A4_UsingDeclaration_DisposesAtEndOfScope()
        {
            var src = Usings + @"
namespace Snippet
{
    public sealed class Tracker : IDisposable
    {
        public List<string> Log;
        public string Name;
        public void Dispose() => Log.Add(""dispose "" + Name);
    }

    public partial class C
    {
        [Continuable]
        public string M()
        {
            var log = new List<string>();
            {
                using var a = new Tracker { Log = log, Name = ""a"" };
                using (new Tracker { Log = log, Name = ""b"" })
                {
                    for (int i = 0; i < 3; i++) log.Add(""work"" + i);
                }
                using (new Tracker { Log = log, Name = ""c"" }) { log.Add(""c-body""); }
                log.Add(""end of block"");
            }
            return string.Join("","", log);
        }
    }
    public static class Entry { public static string Go() => new C().M_Continuable(); }
}";
            AssertSameResult(src, "work0,work1,work2,dispose b,c-body,dispose c,end of block,dispose a", budget: 6, methods: "M");
        }

        // ---- A5: nameof and inferred member names ----

        [Fact]
        public void A5_NameofAndInferredNames_UseSourceNames()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public string M()
        {
            int total = 0;
            for (int i = 0; i < 4; i++) total += i;
            var anon = new { total };
            var tuple = (total, count: 4);
            return nameof(total) + "":"" + anon.total + "":"" + tuple.total + "":"" + tuple.count;
        }
    }
    public static class Entry { public static string Go() => new C().M_Continuable(); }
}";
            AssertSameResult(src, "total:6:6:4", budget: 5, methods: "M");
        }

        // ---- A6: closures over loop-body locals ----

        [Fact]
        public void A6_LambdasCaptureAFreshLocalPerIteration()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int M()
        {
            var fs = new List<Func<int>>();
            var xs = new[] { 1, 2, 3, 4 };
            foreach (var x in xs)
            {
                int c = x * 10;
                fs.Add(() => c);
            }
            int sum = 0;
            foreach (var f in fs) sum += f();
            return sum;
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(); }
}";
            // Shared hoisted variable would give 4 * 40 = 160.
            AssertSameResult(src, 100, budget: 8, methods: "M");
        }

        // ---- A7: per-iteration loop checks ----

        [Fact]
        public void A7_LongLoopIsPreemptedEveryBudgetAndStillCompletes()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int M()
        {
            int n = 0;
            while (true)
            {
                n++;
                if (n == 5000) break;
            }
            return n;
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(); }
}";
            var go = GeneratorHarness.CompileEntry<int>(src, "M");
            var outcome = GeneratorHarness.RunToCompletion(go, budget: 1000);
            Assert.Equal(5000, outcome.Value);
            // The loop runs ~5000 iterations of cost ~11, so it must be preempted many
            // times; before the fix the check ran once before the loop and never fired.
            Assert.True(outcome.Suspensions >= 10, $"Only {outcome.Suspensions} suspensions.");
            Assert.All(outcome.InnermostYieldPoints, id => Assert.Equal(0, id));
        }

        [Fact]
        public void A7_SuspendsInLaterLoopsAndInsideNestedCalls()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int Inner(int n)
        {
            int s = 0;
            for (int i = 1; i <= n; i++) s += i;
            return s;
        }

        [Continuable]
        public int M()
        {
            int total = 0;
            for (int i = 0; i < 5; i++) total += i;
            for (int i = 1; i <= 6; i++)
            {
                total += Inner(i);
            }
            int j = 0;
            do { total += j; j++; } while (j < 4);
            return total;
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(); }
}";
            // 10 + (1+3+6+10+15+21) + 6 = 72
            var outcome = AssertSameResult(src, 72, budget: 12, json: true, methods: new[] { "M", "Inner" });
            Assert.True(outcome.Suspensions > 3);
            Assert.Contains(outcome.Depths, d => d == 2);
            Assert.Contains(outcome.InnermostYieldPoints, id => id != 0);
        }

        [Fact]
        public void A7_RecursiveContinuableCalls_ResumeTheRightFrame()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int Fib(int n)
        {
            if (n < 2) return n;
            int a = Fib(n - 1);
            int b = Fib(n - 2);
            int spin = 0;
            for (int i = 0; i < 3; i++) spin++;
            return a + b + spin - 3;
        }
    }
    public static class Entry { public static int Go() => new C().Fib_Continuable(10); }
}";
            var outcome = AssertSameResult(src, 55, budget: 25, json: true, methods: "Fib");
            Assert.True(outcome.Depths.Max() > 2);
        }

        [Fact]
        public void A7_ContinuableCallsInConditions_ResumeWithoutTheirOwnYieldSite()
        {
            // Calls in if/while/switch/using headers are rewritten but are not yield sites
            // of their own: the frame hands its inner frames back at the previous site
            // and the call in the header picks them up.
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int Helper(int n) { int s = 0; for (int i = 1; i <= n; i++) s += i; return s; }

        [Continuable]
        public int M()
        {
            int t = 0;
            if (Helper(6) > 20) t += 100;
            int k = 0;
            while (Helper(k) < 10) { k++; }
            switch (Helper(3))
            {
                case 6: t += 1; break;
                default: t -= 1000; break;
            }
            return t + k;
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(); }
}";
            var outcome = AssertSameResult(src, 105, budget: 6, json: true, methods: new[] { "M", "Helper" });
            Assert.True(outcome.Suspensions > 3);
        }

        [Fact]
        public void A7_SeveralCallsToOneMethodInOneStatement_StillMakeProgress()
        {
            // One yield site covers both calls, and frame records are matched to calls by
            // method token, so the first call may restore the second call's record. The
            // run-wide replay counter still ends the replay at the exact site the run
            // suspended at, so every resume makes progress.
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int Helper(int n) { int s = 0; for (int i = 1; i <= n; i++) s += i; return s; }

        [Continuable]
        public int M()
        {
            int x = Helper(8) + Helper(9) * 2;
            return x;
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(); }
}";
            var outcome = AssertSameResult(src, 36 + 90, budget: 9, json: true, methods: new[] { "M", "Helper" });
            Assert.True(outcome.Suspensions > 3);
        }

        [Fact]
        public void A7_EntryPointCallingSeveralGeneratedMethods_MakesProgress()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int Sum(int n) { int s = 0; for (int i = 1; i <= n; i++) s += i; return s; }
    }
    public partial class D
    {
        [Continuable]
        public int Count(int n) { int c = 0; while (c < n) c++; return c; }
    }
    public static class Entry { public static int Go() => new C().Sum_Continuable(10) + new D().Count_Continuable(12) + new C().Sum_Continuable(3); }
}";
            var outcome = AssertSameResult(src, 55 + 12 + 6, budget: 7, json: true, methods: new[] { "Sum", "Count" });
            Assert.True(outcome.Suspensions > 3);
        }

        /// <summary>
        /// A thread whose ScriptContext persists (ScriptContext.EnsureCurrent) makes
        /// ContinuationRunner.Run reuse it, so the entry count carries over from earlier
        /// runs. Only the first resume is affected (Resume always uses a fresh context):
        /// the result stays correct and the run still completes.
        /// </summary>
        [Fact]
        public void A7_ReusedThreadContext_FirstResumeStaysCorrectAndCompletes()
        {
            var src = Usings + @"
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int Sum(int n) { int s = 0; for (int i = 1; i <= n; i++) s += i; return s; }
    }
    public static class Entry { public static int Go() => new C().Sum_Continuable(4) + new C().Sum_Continuable(30) + new C().Sum_Continuable(2); }
}";
            var go = GeneratorHarness.CompileEntry<int>(src, "Sum");
            var expected = 10 + 465 + 3;
            var outcome = GeneratorHarness.RunToCompletion(go, budget: 9, prepareThread: () =>
            {
                // Leave a persistent context with stale counts on this thread.
                Prim.Runtime.ScriptContext.EnsureCurrent();
                var runner = new Prim.Runtime.ContinuationRunner();
                var first = runner.Run(() =>
                {
                    Prim.Runtime.ScriptContext.Current!.ResetBudget(int.MaxValue);
                    return go();
                });
                Assert.True(first.IsCompleted);
            });
            Assert.Equal(expected, outcome.Value);
        }
    }
}
