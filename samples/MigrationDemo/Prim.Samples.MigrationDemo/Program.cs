using Prim.Core;
using Prim.Runtime;
using Prim.Serialization;

namespace Prim.Samples.MigrationDemo;

/// <summary>
/// Demonstrates state migration - suspending a computation, serializing it to a file,
/// and resuming it later (potentially in a different process).
///
/// This is inspired by Second Life's script migration capability where scripts
/// can be suspended on one server and resumed on another.
///
/// Usage:
///   dotnet run           - Runs full demo (suspend, serialize, deserialize, resume)
///   dotnet run --suspend - Suspends and saves state to file
///   dotnet run --resume  - Loads state from file and resumes
/// </summary>
internal class Program
{
    private const string StateFileName = "computation_state.json";

    static void Main(string[] args)
    {
        Console.WriteLine("=== Continuation Framework: Migration Demo ===\n");

        if (args.Length > 0 && args[0] == "--resume")
        {
            ResumeComputation();
        }
        else if (args.Length > 0 && args[0] == "--suspend")
        {
            SuspendComputation();
        }
        else
        {
            RunFullDemo();
        }
    }

    /// <summary>
    /// Runs the full demo: suspend, save to file, load from file, resume.
    /// This simulates process migration within a single execution.
    /// </summary>
    static void RunFullDemo()
    {
        Console.WriteLine("Running full migration demo...\n");

        var stateFile = Path.Combine(Path.GetTempPath(), StateFileName);
        var serializer = new JsonContinuationSerializer();
        var runner = new ContinuationRunner { Serializer = serializer };
        var computation = new PrimeFinder();

        // Phase 1: Start and run until we decide to suspend
        Console.WriteLine("Phase 1: Starting prime computation...");
        var result = runner.Run(() => computation.FindPrimes(1000));

        int iterations = 0;
        const int suspendAfter = 50;

        while (!result.IsCompleted && iterations < suspendAfter)
        {
            var suspended = (ContinuationResult<int>.Suspended)result;
            if (iterations % 10 == 0)
            {
                Console.WriteLine($"   Found prime #{iterations + 1}: {suspended.YieldedValue}");
            }
            iterations++;
            result = runner.Resume(suspended.State, null, () => computation.FindPrimes(1000));
        }

        // Phase 2: Suspend and serialize to file
        if (!result.IsCompleted)
        {
            var suspended = (ContinuationResult<int>.Suspended)result;
            Console.WriteLine($"\n   Suspending after {iterations} primes (last: {suspended.YieldedValue})");

            var json = serializer.SerializeToString(suspended.State);
            File.WriteAllText(stateFile, json);
            Console.WriteLine($"   State saved to: {stateFile}");
            Console.WriteLine($"   State size: {json.Length} chars");
            Console.WriteLine($"   Computation state: found={computation.Found}, candidate={computation.Candidate}");
        }

        // Phase 3: Load state from file (simulating new process)
        Console.WriteLine("\nPhase 2: Loading state from file (simulating process restart)...");

        if (!File.Exists(stateFile))
        {
            Console.WriteLine("   No saved state found!");
            return;
        }

        var loadedJson = File.ReadAllText(stateFile);
        var loadedState = serializer.DeserializeFromString(loadedJson);
        Console.WriteLine("   State loaded successfully");

        // Create new computation instance and restore its state
        var newComputation = new PrimeFinder();
        if (loadedState.StackHead?.Slots?.Length >= 2)
        {
            newComputation.Found = Convert.ToInt32(loadedState.StackHead.Slots[0]);
            newComputation.Candidate = Convert.ToInt32(loadedState.StackHead.Slots[1]);
            Console.WriteLine($"   Restored state: found={newComputation.Found}, candidate={newComputation.Candidate}");
        }

        // Phase 4: Resume computation
        Console.WriteLine("\nPhase 3: Resuming computation...");
        result = runner.Resume(loadedState, null, () => newComputation.FindPrimes(1000));

        int resumedIterations = 0;
        const int runMore = 50;

        while (!result.IsCompleted && resumedIterations < runMore)
        {
            var suspended = (ContinuationResult<int>.Suspended)result;
            if (resumedIterations % 10 == 0)
            {
                Console.WriteLine($"   Resumed: prime #{iterations + resumedIterations + 1}: {suspended.YieldedValue}");
            }
            resumedIterations++;
            result = runner.Resume(suspended.State, null, () => newComputation.FindPrimes(1000));
        }

        Console.WriteLine($"\n   Completed {resumedIterations} more iterations after resume");
        Console.WriteLine($"   Total primes found: {iterations + resumedIterations}");

        // Cleanup
        if (File.Exists(stateFile))
        {
            File.Delete(stateFile);
        }

        Console.WriteLine("\n=== Migration Demo Complete ===");
    }

    /// <summary>
    /// Runs just the suspend phase.
    /// Use: dotnet run -- --suspend
    /// Then: dotnet run -- --resume
    /// </summary>
    static void SuspendComputation()
    {
        var stateFile = Path.Combine(Directory.GetCurrentDirectory(), StateFileName);
        var serializer = new JsonContinuationSerializer();
        var runner = new ContinuationRunner { Serializer = serializer };
        var computation = new PrimeFinder();

        Console.WriteLine("Starting computation (will suspend after 100 primes)...\n");

        var result = runner.Run(() => computation.FindPrimes(500));
        int iterations = 0;
        const int suspendAfter = 100;

        while (!result.IsCompleted && iterations < suspendAfter)
        {
            var suspended = (ContinuationResult<int>.Suspended)result;
            Console.WriteLine($"Prime #{iterations + 1}: {suspended.YieldedValue}");
            iterations++;
            result = runner.Resume(suspended.State, null, () => computation.FindPrimes(500));
        }

        if (!result.IsCompleted)
        {
            var suspended = (ContinuationResult<int>.Suspended)result;
            var json = serializer.SerializeToString(suspended.State);

            // Also save the computation state
            var fullState = new SavedState
            {
                ContinuationJson = json,
                Found = computation.Found,
                Candidate = computation.Candidate
            };
            var fullJson = System.Text.Json.JsonSerializer.Serialize(fullState);
            File.WriteAllText(stateFile, fullJson);

            Console.WriteLine($"\nSuspended! State saved to: {stateFile}");
            Console.WriteLine($"State size: {fullJson.Length} chars");
            Console.WriteLine($"Computation: found={computation.Found}, candidate={computation.Candidate}");
            Console.WriteLine("\nRun with '--resume' to continue the computation.");
        }
        else
        {
            Console.WriteLine("Computation completed before suspension point.");
        }
    }

    /// <summary>
    /// Runs just the resume phase.
    /// Use: dotnet run -- --suspend (first)
    /// Then: dotnet run -- --resume
    /// </summary>
    static void ResumeComputation()
    {
        var stateFile = Path.Combine(Directory.GetCurrentDirectory(), StateFileName);

        if (!File.Exists(stateFile))
        {
            Console.WriteLine($"No saved state found at: {stateFile}");
            Console.WriteLine("Run with '--suspend' first to create a saved state.");
            return;
        }

        var serializer = new JsonContinuationSerializer();
        var runner = new ContinuationRunner { Serializer = serializer };

        Console.WriteLine($"Loading state from: {stateFile}...\n");

        var fullJson = File.ReadAllText(stateFile);
        var fullState = System.Text.Json.JsonSerializer.Deserialize<SavedState>(fullJson);

        if (fullState == null)
        {
            Console.WriteLine("Failed to deserialize state.");
            return;
        }

        var state = serializer.DeserializeFromString(fullState.ContinuationJson);
        var computation = new PrimeFinder
        {
            Found = fullState.Found,
            Candidate = fullState.Candidate
        };

        Console.WriteLine($"State loaded! found={computation.Found}, candidate={computation.Candidate}\n");
        Console.WriteLine("Resuming computation...\n");

        var result = runner.Resume(state, null, () => computation.FindPrimes(500));

        int iterations = 0;
        while (!result.IsCompleted && iterations < 100)
        {
            var suspended = (ContinuationResult<int>.Suspended)result;
            Console.WriteLine($"Prime (resumed) #{fullState.Found + iterations + 1}: {suspended.YieldedValue}");
            iterations++;
            result = runner.Resume(suspended.State, null, () => computation.FindPrimes(500));
        }

        if (result.IsCompleted)
        {
            var completed = (ContinuationResult<int>.Completed)result;
            Console.WriteLine($"\nComputation completed! Total primes: {completed.Value}");
        }
        else
        {
            Console.WriteLine($"\n... stopped after {iterations} more primes for demo");
        }

        // Cleanup
        File.Delete(stateFile);
        Console.WriteLine($"\nState file deleted.");
    }

    /// <summary>
    /// Saved state including both continuation state and computation state.
    /// </summary>
    class SavedState
    {
        public string ContinuationJson { get; set; } = "";
        public int Found { get; set; }
        public int Candidate { get; set; }
    }
}

/// <summary>
/// A prime number finder that demonstrates state capture and migration.
///
/// The computation state is tracked in instance fields, which allows
/// the state to be serialized and restored across process boundaries.
/// </summary>
public class PrimeFinder
{
    /// <summary>Number of primes found so far.</summary>
    public int Found { get; set; }

    /// <summary>Current candidate being tested.</summary>
    public int Candidate { get; set; } = 2;

    /// <summary>
    /// Finds prime numbers, yielding each one found.
    ///
    /// State is tracked in instance fields (Found, Candidate) which can be
    /// serialized and restored for process migration.
    /// </summary>
    public int FindPrimes(int count)
    {
        ScriptContext context = ScriptContext.EnsureCurrent();
        const int methodToken = 54321;

        try
        {
            while (Found < count)
            {
                if (IsPrime(Candidate))
                {
                    Found++;
                    int prime = Candidate;
                    Candidate++;

                    // Yield the found prime
                    context.RequestYield();
                    context.HandleYieldPoint(0, prime);
                }
                else
                {
                    Candidate++;
                }
            }

            return Found;
        }
        catch (SuspendException ex)
        {
            // Capture state for serialization
            var slots = FrameCapture.PackSlots(Found, Candidate);
            var record = FrameCapture.CaptureFrame(methodToken, ex.YieldPointId, slots, ex.FrameChain);
            ex.FrameChain = record;
            throw;
        }
    }

    private static bool IsPrime(int n)
    {
        if (n < 2) return false;
        if (n == 2) return true;
        if (n % 2 == 0) return false;

        int limit = (int)Math.Sqrt(n);
        for (int i = 3; i <= limit; i += 2)
        {
            if (n % i == 0) return false;
        }
        return true;
    }
}
