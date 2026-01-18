using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Prim.Core;
using Prim.Runtime;

namespace Prim.Benchmarks;

/// <summary>
/// Benchmarks comparing original code execution vs code with continuation support.
/// Measures the overhead of yield point checks and context management.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class TransformOverheadBenchmarks
{
    private ContinuationRunner _runner = null!;

    [GlobalSetup]
    public void Setup()
    {
        _runner = new ContinuationRunner();
    }

    #region Loop Overhead Benchmarks

    /// <summary>
    /// Baseline: Simple loop without any continuation support.
    /// </summary>
    [Benchmark(Baseline = true)]
    public int LoopBaseline()
    {
        int sum = 0;
        for (int i = 0; i < 1000; i++)
        {
            sum += i;
        }
        return sum;
    }

    /// <summary>
    /// Loop with yield point check (no actual yield).
    /// Measures overhead of checking the yield flag.
    /// </summary>
    [Benchmark]
    public int LoopWithYieldCheck()
    {
        var context = ScriptContext.EnsureCurrent();
        int sum = 0;
        for (int i = 0; i < 1000; i++)
        {
            // Simulates what transformed code does at each yield point
            context.HandleYieldPoint(0);
            sum += i;
        }
        return sum;
    }

    /// <summary>
    /// Loop with budget-based yield check (no actual yield).
    /// Measures overhead of instruction counting.
    /// </summary>
    [Benchmark]
    public int LoopWithBudgetCheck()
    {
        var context = ScriptContext.EnsureCurrent();
        context.ResetBudget(10000); // High budget so we don't yield
        int sum = 0;
        for (int i = 0; i < 1000; i++)
        {
            context.HandleYieldPointWithBudget(0, 1);
            sum += i;
        }
        return sum;
    }

    /// <summary>
    /// Loop running through ContinuationRunner.
    /// Measures full overhead including context setup.
    /// </summary>
    [Benchmark]
    public int LoopWithRunner()
    {
        var result = _runner.Run(() =>
        {
            int sum = 0;
            for (int i = 0; i < 1000; i++)
            {
                sum += i;
            }
            return sum;
        });
        return ((ContinuationResult<int>.Completed)result).Value;
    }

    #endregion

    #region Nested Call Overhead Benchmarks

    /// <summary>
    /// Baseline: Nested method calls without continuation support.
    /// </summary>
    [Benchmark]
    public int NestedCallsBaseline()
    {
        return Level1Baseline(10);
    }

    private int Level1Baseline(int n)
    {
        if (n <= 0) return 0;
        return n + Level2Baseline(n - 1);
    }

    private int Level2Baseline(int n)
    {
        if (n <= 0) return 0;
        return n + Level3Baseline(n - 1);
    }

    private int Level3Baseline(int n)
    {
        if (n <= 0) return 0;
        return n + Level1Baseline(n - 1);
    }

    /// <summary>
    /// Nested calls with yield checks simulating transformed code.
    /// </summary>
    [Benchmark]
    public int NestedCallsWithYieldCheck()
    {
        var context = ScriptContext.EnsureCurrent();
        return Level1WithCheck(context, 10);
    }

    private int Level1WithCheck(ScriptContext context, int n)
    {
        context.HandleYieldPoint(0);
        if (n <= 0) return 0;
        return n + Level2WithCheck(context, n - 1);
    }

    private int Level2WithCheck(ScriptContext context, int n)
    {
        context.HandleYieldPoint(1);
        if (n <= 0) return 0;
        return n + Level3WithCheck(context, n - 1);
    }

    private int Level3WithCheck(ScriptContext context, int n)
    {
        context.HandleYieldPoint(2);
        if (n <= 0) return 0;
        return n + Level1WithCheck(context, n - 1);
    }

    #endregion

    #region Computation Overhead Benchmarks

    /// <summary>
    /// Compute-heavy baseline (Fibonacci).
    /// </summary>
    [Benchmark]
    public int FibonacciBaseline()
    {
        return FibBaseline(25);
    }

    private int FibBaseline(int n)
    {
        if (n <= 1) return n;
        return FibBaseline(n - 1) + FibBaseline(n - 2);
    }

    /// <summary>
    /// Fibonacci with yield checks at each call.
    /// </summary>
    [Benchmark]
    public int FibonacciWithYieldCheck()
    {
        var context = ScriptContext.EnsureCurrent();
        return FibWithCheck(context, 25);
    }

    private int FibWithCheck(ScriptContext context, int n)
    {
        context.HandleYieldPoint(0);
        if (n <= 1) return n;
        return FibWithCheck(context, n - 1) + FibWithCheck(context, n - 2);
    }

    #endregion
}
