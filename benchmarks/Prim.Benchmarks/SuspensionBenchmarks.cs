using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Prim.Core;
using Prim.Runtime;

namespace Prim.Benchmarks;

/// <summary>
/// Benchmarks measuring suspension and resumption overhead.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class SuspensionBenchmarks
{
    private ContinuationRunner _runner = null!;
    private ContinuationState _shallowState = null!;
    private ContinuationState _deepState = null!;
    private EntryPointRegistry _registry = null!;

    [GlobalSetup]
    public void Setup()
    {
        _registry = new EntryPointRegistry();
        _runner = new ContinuationRunner { EntryPoints = _registry };

        // Create a shallow state (1 frame)
        var shallowFrame = new HostFrameRecord(
            methodToken: 100,
            yieldPointId: 0,
            slots: new object[] { 1, 2, 3, "test", 42.0 },
            caller: null);
        _shallowState = new ContinuationState(shallowFrame);

        // Create a deep state (10 frames)
        HostFrameRecord? frame = null;
        for (int i = 0; i < 10; i++)
        {
            frame = new HostFrameRecord(
                methodToken: 100 + i,
                yieldPointId: i % 3,
                slots: new object[] { i, $"frame{i}", (double)i },
                caller: frame);
        }
        _deepState = new ContinuationState(frame);

        // Register a simple entry point
        _registry.Register(100, () => 42);
    }

    #region Suspension Benchmarks

    /// <summary>
    /// Measures the cost of throwing and catching SuspendException.
    /// </summary>
    [Benchmark]
    public ContinuationState SuspendException_ThrowAndCatch()
    {
        try
        {
            throw new SuspendException(0);
        }
        catch (SuspendException ex)
        {
            return ex.BuildContinuationState();
        }
    }

    /// <summary>
    /// Measures the cost of suspending via ContinuationRunner.
    /// </summary>
    [Benchmark]
    public ContinuationState SuspendViaRunner()
    {
        var result = _runner.Run<int>(() =>
        {
            var context = ScriptContext.Current!;
            context.RequestYield();
            context.HandleYieldPoint(0);
            return 42;
        });

        return ((ContinuationResult<int>.Suspended)result).State;
    }

    /// <summary>
    /// Measures the cost of a full suspend with frame capture.
    /// </summary>
    [Benchmark]
    public ContinuationState SuspendWithFrameCapture()
    {
        var result = _runner.Run<int>(() =>
        {
            var context = ScriptContext.Current!;
            try
            {
                context.RequestYield();
                context.HandleYieldPoint(0);
                return 42;
            }
            catch (SuspendException ex)
            {
                var slots = FrameCapture.PackSlots(1, 2, 3, "test", 42.0);
                ex.FrameChain = FrameCapture.CaptureFrame(100, 0, slots, ex.FrameChain);
                throw;
            }
        });

        return ((ContinuationResult<int>.Suspended)result).State;
    }

    #endregion

    #region Resume Benchmarks

    /// <summary>
    /// Measures the cost of creating a ScriptContext for restoration.
    /// </summary>
    [Benchmark]
    public ScriptContext CreateRestorationContext_Shallow()
    {
        return new ScriptContext(_shallowState, null);
    }

    /// <summary>
    /// Measures the cost of creating a ScriptContext for deep stack restoration.
    /// </summary>
    [Benchmark]
    public ScriptContext CreateRestorationContext_Deep()
    {
        return new ScriptContext(_deepState, null);
    }

    /// <summary>
    /// Measures resumption with direct entry point.
    /// </summary>
    [Benchmark]
    public int ResumeWithEntryPoint()
    {
        var result = _runner.Resume(_shallowState, null, () => 42);
        return ((ContinuationResult<int>.Completed)result).Value;
    }

    #endregion

    #region Full Cycle Benchmarks

    /// <summary>
    /// Measures a complete suspend-resume cycle.
    /// </summary>
    [Benchmark]
    public int FullSuspendResumeCycle()
    {
        var suspended = false;
        ContinuationState? state = null;

        var result = _runner.Run<int>(() =>
        {
            var context = ScriptContext.Current!;

            if (context.IsRestoring)
            {
                // Resuming - return result
                return 42;
            }

            // First run - suspend
            context.RequestYield();
            context.HandleYieldPoint(0);
            return 0; // Never reached
        });

        if (result.IsSuspended)
        {
            state = ((ContinuationResult<int>.Suspended)result).State;
            suspended = true;
        }

        if (suspended && state != null)
        {
            // Resume
            result = _runner.Resume<int>(state, null, () =>
            {
                return 42;
            });
        }

        return ((ContinuationResult<int>.Completed)result).Value;
    }

    /// <summary>
    /// Measures multiple suspend-resume cycles (simulates cooperative scheduling).
    /// </summary>
    [Benchmark]
    [Arguments(5)]
    [Arguments(10)]
    public int MultipleSuspendResumeCycles(int cycles)
    {
        var iteration = 0;
        ContinuationState? state = null;

        Func<int> computation = () =>
        {
            var context = ScriptContext.Current!;
            var localIteration = iteration;

            if (context.IsRestoring && context.FrameChain != null)
            {
                localIteration = FrameCapture.GetSlot<int>(context.FrameChain.Slots, 0);
                context.FrameChain = context.FrameChain.Caller;
                context.IsRestoring = context.FrameChain != null;
            }

            while (localIteration < cycles)
            {
                localIteration++;
                try
                {
                    context.RequestYield();
                    context.HandleYieldPoint(0);
                }
                catch (SuspendException ex)
                {
                    ex.FrameChain = FrameCapture.CaptureFrame(
                        200, 0,
                        FrameCapture.PackSlots(localIteration),
                        ex.FrameChain);
                    throw;
                }
            }

            return localIteration;
        };

        var result = _runner.Run(computation);

        while (result.IsSuspended)
        {
            state = ((ContinuationResult<int>.Suspended)result).State;
            result = _runner.Resume(state, null, computation);
        }

        return ((ContinuationResult<int>.Completed)result).Value;
    }

    #endregion
}
