using Prim.Core;
using Prim.Runtime;
using Prim.Serialization;

namespace Prim.Samples.Generator;

/// <summary>
/// Demonstrates the continuation framework's state capture and restoration.
///
/// This sample shows how the framework captures and restores state when a
/// SuspendException is thrown. The key concepts:
///
/// 1. ScriptContext.HandleYieldPoint() throws SuspendException if yield was requested
/// 2. The catch block captures local variables into a HostFrameRecord
/// 3. On resume, the state is restored and the method can continue
///
/// Note: For true automatic state machine transformation (like C# async/await),
/// a full source generator or Cecil bytecode rewriter would transform loops
/// into explicit state machines. This demo shows the underlying mechanism.
/// </summary>
internal class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== Continuation Framework: Demo ===\n");

        // Example 1: Simple counter with explicit state tracking
        Console.WriteLine("1. Counter with State Capture:");
        RunCounterDemo();

        // Example 2: Serialization round-trip
        Console.WriteLine("\n2. Serialization Demo:");
        RunSerializationDemo();

        Console.WriteLine("\n=== Done ===");
    }

    /// <summary>
    /// Demonstrates the fundamental yield/resume cycle.
    /// </summary>
    static void RunCounterDemo()
    {
        var runner = new ContinuationRunner();
        var counter = new Counter();

        // Start the counter - it will run until the first yield point
        var result = runner.Run(() => counter.CountTo(5));

        int iterations = 0;
        while (!result.IsCompleted && iterations < 10)
        {
            var suspended = (ContinuationResult<int>.Suspended)result;
            Console.WriteLine($"   Yielded value: {suspended.YieldedValue}, current count: {counter.Current}");
            iterations++;

            // Resume - the counter state is preserved
            result = runner.Resume(suspended.State, null, () => counter.CountTo(5));
        }

        if (result.IsCompleted)
        {
            var completed = (ContinuationResult<int>.Completed)result;
            Console.WriteLine($"   Completed with final value: {completed.Value}");
        }
        else
        {
            Console.WriteLine($"   Stopped after {iterations} iterations");
        }
    }

    /// <summary>
    /// Demonstrates serializing state and resuming from it.
    /// </summary>
    static void RunSerializationDemo()
    {
        var serializer = new JsonContinuationSerializer();
        var runner = new ContinuationRunner { Serializer = serializer };
        var counter = new Counter();

        // Start and run a few iterations
        var result = runner.Run(() => counter.CountTo(10));

        for (int i = 0; i < 3 && !result.IsCompleted; i++)
        {
            var suspended = (ContinuationResult<int>.Suspended)result;
            Console.WriteLine($"   Before serialize - count: {counter.Current}, yielded: {suspended.YieldedValue}");
            result = runner.Resume(suspended.State, null, () => counter.CountTo(10));
        }

        if (!result.IsCompleted)
        {
            var suspended = (ContinuationResult<int>.Suspended)result;
            Console.WriteLine($"   Suspending at count: {counter.Current}");

            // Serialize the state
            var json = serializer.SerializeToString(suspended.State);
            Console.WriteLine($"   Serialized state: {json.Length} chars");

            // Simulate process restart - create new objects
            var newSerializer = new JsonContinuationSerializer();
            var newRunner = new ContinuationRunner { Serializer = newSerializer };
            var newCounter = new Counter();

            // Deserialize and restore the counter state from the serialized state
            var restoredState = newSerializer.DeserializeFromString(json);

            // We need to manually restore the counter's state from the frame
            // In a real scenario, this would be handled by generated code
            if (restoredState.StackHead?.Slots?.Length > 0)
            {
                newCounter.Current = Convert.ToInt32(restoredState.StackHead.Slots[0]);
                Console.WriteLine($"   Restored counter state: {newCounter.Current}");
            }

            // Resume with the new counter (state restored)
            result = newRunner.Resume(restoredState, null, () => newCounter.CountTo(10));

            for (int i = 0; i < 3 && !result.IsCompleted; i++)
            {
                suspended = (ContinuationResult<int>.Suspended)result;
                Console.WriteLine($"   After restore - count: {newCounter.Current}, yielded: {suspended.YieldedValue}");
                result = newRunner.Resume(suspended.State, null, () => newCounter.CountTo(10));
            }
        }
    }
}

/// <summary>
/// A simple counter that demonstrates state capture.
/// The counter maintains its state in instance fields, and the
/// continuation framework captures the local variables at yield points.
/// </summary>
public class Counter
{
    /// <summary>
    /// Current count value. This is preserved across yield/resume cycles.
    /// </summary>
    public int Current { get; set; }

    /// <summary>
    /// Counts from Current to target, yielding at each step.
    ///
    /// This method demonstrates manual state machine pattern:
    /// - State is tracked in instance fields (Current)
    /// - Each iteration requests a yield and handles the yield point
    /// - The catch block captures Current for serialization
    /// </summary>
    public int CountTo(int target)
    {
        ScriptContext context = ScriptContext.EnsureCurrent();
        const int methodToken = 12345;

        try
        {
            while (Current < target)
            {
                Current++;

                // Request a yield - on next HandleYieldPoint, will throw SuspendException
                context.RequestYield();

                // This throws SuspendException if yield was requested
                // The yieldPointId (0) identifies this yield point
                // The second parameter is the yielded value
                context.HandleYieldPoint(0, Current);
            }

            return Current;
        }
        catch (SuspendException ex)
        {
            // Capture the current state for later restoration
            // In generated code, this would capture all local variables
            var slots = FrameCapture.PackSlots(Current);
            var record = FrameCapture.CaptureFrame(methodToken, ex.YieldPointId, slots, ex.FrameChain);
            ex.FrameChain = record;
            throw;
        }
    }
}
