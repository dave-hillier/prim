# Prim

A .NET continuation framework enabling suspend/resume of program execution with serializable state.

## Why Serializable Continuations?

Most runtimes support some form of suspension (coroutines, async/await, green threads), but the captured state typically exists only in memory. **Serializable continuations** let you persist that state to disk or transmit it to another machine.

This enables patterns that are otherwise difficult:

- **Transparent migration** - Move running computations between servers without the code knowing it moved
- **Durable execution** - Checkpoint long-running work and resume after crashes, without requiring deterministic replay
- **Cooperative multithreading for untrusted code** - Run many scripts on one thread with guaranteed yield points

Prim achieves this on stock .NET runtimes through program transformation. No runtime modifications required.

## Background

The techniques in Prim were originally developed for Second Life's Mono integration (2007-2008), where user scripts needed to migrate seamlessly between simulator processes. A script counting to a million shouldn't restart from zero just because its object crossed a region boundary.

For the full technical details, design rationale, and comparison with related systems (WasmFX, Espresso, Project Loom), see the [whitepaper](docs/whitepaper.md).

## What Prim Does

- **Suspends** execution at yield points and captures the entire call stack
- **Serializes** the captured state to JSON or MessagePack
- **Resumes** execution from saved state, even in a different process
- **Migrates** running computations across processes or machines

## Status

| Component | Status |
|-----------|--------|
| `Prim.Core`, `Prim.Runtime` | Working. Run, suspend, resume, budget-based preemption, direct resume via `EntryPointRegistry`. |
| `Prim.Serialization` | Working. JSON preserves shared references between slots; MessagePack preserves slot types but not shared references. See [Security](#security) before loading untrusted state. |
| `Prim.Roslyn` source generator | Experimental, works end to end for supported method shapes. Uses a *replay* model (see below). |
| `Prim.Cecil` IL rewriter | Experimental. Output passes `ilverify` in tests; no MSBuild integration yet, and yield points inside `try`/`catch`/`finally` are not supported. |

## Project Structure

```
Prim/
├── src/
│   ├── Prim.Core/           # Core types (HostFrameRecord, ContinuationState, ContinuationValidator, ...)
│   ├── Prim.Runtime/        # ScriptContext, ContinuationRunner, FrameCapture, EntryPointRegistry
│   ├── Prim.Serialization/  # JSON and MessagePack serializers
│   ├── Prim.Roslyn/         # Source generator for [Continuable] methods (experimental)
│   ├── Prim.Analysis/       # IL analysis (CFG, stack simulation, yield point identification)
│   └── Prim.Cecil/          # Bytecode rewriting with Mono.Cecil (experimental)
├── tests/
│   ├── Prim.Tests.Unit/
│   ├── Prim.Tests.Integration/
│   ├── Prim.Tests.Roslyn/
│   └── Prim.Tests.Cecil/
├── samples/
│   ├── Generator/           # Yield/resume demonstration (manual pattern)
│   └── MigrationDemo/       # Suspend to a file, resume in another process
├── benchmarks/              # BenchmarkDotNet suite
└── docs/                    # Whitepaper, TODO, blog/ (draft post), research/ (prior art, WasmFX notes)
```

## Quick Start

There are two ways to make a method continuable:

1. **Manual pattern** — write the suspend/capture code yourself. Nothing is generated, so it is the easiest way to see the mechanism.
2. **`[Continuable]` source generator** — mark a method and let `Prim.Roslyn` generate a `*_Continuable` version of it that saves and restores its locals.

### Basic Yield/Resume (Manual Pattern)

State that must survive a yield lives in instance fields (`Current`), and the `catch` block packs that state into a frame record before re-throwing.

```csharp
using Prim.Core;
using Prim.Runtime;

public class Counter
{
    public int Current { get; set; }

    public int CountTo(int target)
    {
        var context = ScriptContext.EnsureCurrent();
        const int methodToken = 12345;

        try
        {
            while (Current < target)
            {
                Current++;
                context.RequestYield();
                context.HandleYieldPoint(0, Current);
            }
            return Current;
        }
        catch (SuspendException ex)
        {
            var slots = FrameCapture.PackSlots(Current);
            var record = FrameCapture.CaptureFrame(methodToken, ex.YieldPointId, slots, ex.FrameChain);
            ex.FrameChain = record;
            throw;
        }
    }
}

// Usage
var runner = new ContinuationRunner();
var counter = new Counter();
var result = runner.Run(() => counter.CountTo(5));

while (result is ContinuationResult<int>.Suspended suspended)
{
    Console.WriteLine($"Yielded: {suspended.YieldedValue}");
    result = runner.Resume(suspended.State, null, () => counter.CountTo(5));
}

Console.WriteLine($"Completed: {((ContinuationResult<int>.Completed)result).Value}");
```

In this pattern nothing reads the captured slots back automatically: resume works because `counter` still holds `Current`. To resume in another process you restore the fields from `state.StackHead.Slots` yourself, as `samples/Generator` does.

### Generated Continuations (`[Continuable]`)

Reference the generator as an analyzer and mark methods with `[Continuable]`:

```xml
<ProjectReference Include="path/to/Prim.Roslyn.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

```csharp
public partial class Summer
{
    [Continuable]
    public int SumTo(int n)
    {
        int sum = 0;
        for (int i = 1; i <= n; i++)
        {
            sum += i;
        }
        return sum;
    }
}
```

For a `partial` class the generator adds an instance method `SumTo_Continuable(int n)`. For a non-partial class it emits a static `SummerContinuations.SumTo_Continuable(Summer instance, int n)` instead. Loop headers and calls between `[Continuable]` methods become yield points, and the generated code captures and restores locals, so the state can move between processes.

### Serialization and Migration

```csharp
using Prim.Serialization;

var runner = new ContinuationRunner();
var result = runner.Run(() =>
{
    ScriptContext.Current.RequestYield();
    return new Summer().SumTo_Continuable(10);
});
var suspended = (ContinuationResult<int>.Suspended)result;

var serializer = new JsonContinuationSerializer();
File.WriteAllText("state.json", serializer.SerializeToString(suspended.State));

// Later, in another process...
var state = serializer.DeserializeFromString(File.ReadAllText("state.json"));
var resumed = new ContinuationRunner().Resume(state, null, () => new Summer().SumTo_Continuable(10));
// resumed is Completed(55)
```

To resume without passing the entry point again, register it by method token and resume a `Continuation<T>`:

```csharp
var runner = new ContinuationRunner { EntryPoints = new EntryPointRegistry() };
runner.EntryPoints.Register(state.StackHead.MethodToken, () => new Summer().SumTo_Continuable(10));
var resumed = runner.Resume(new Continuation<int>(state));
```

### Limitations of the Source Generator

- **Replay.** On resume, locals are restored and the method body runs again *from the top*; it does not jump to the yield point. Code before a yield point runs again, so any side effect there (writing a field, printing, sending a message) happens again. Put side effects that must not repeat after the yield point. See [whitepaper §6.3](docs/whitepaper.md).
- **Diagnostics.** `PRIM001`: `[Continuable]` on a non-method member. `PRIM002`: async, iterator, generic, expression-bodied or body-less methods, and methods on generic types. `PRIM003` (error): a yield point inside `finally`, `lock` or a catch filter. In each case no code is generated for the member.
- A continuable call inside an `if`/loop condition or a `switch` does not get its own yield point; it only resumes correctly because the whole statement is replayed.

### IL Rewriting (`Prim.Cecil`, experimental)

`AssemblyRewriter` rewrites a compiled assembly: `new AssemblyRewriter(options).Transform(inputPath, outputPath)`. It adds yield checks at loop back-edges (with optional instruction counting via `HandleYieldPointWithBudget`) and resume points at calls to other transformed methods. It does not use replay: it spills the evaluation stack and jumps back to the yield point. It is not yet hooked into the build, it skips yield points inside protected regions, and it skips methods with byref/pointer locals or `ref`/`out` parameters (listed in `SkippedMethods`).

## Core Concepts

### HostFrameRecord
A linked list node representing a captured stack frame. Contains the method token, yield point ID, captured slots, and a `Caller` link. The chain head (`ContinuationState.StackHead`) is the outermost frame.

### ScriptContext
Thread-local context. `RequestYield()` sets the yield flag; `HandleYieldPoint()` throws `SuspendException` if it is set. `HandleYieldPointWithBudget()` also decrements `InstructionBudget` and suspends when it runs out, which is how untrusted code is preempted without a timer. The budget is not synchronised, so a context should only be used from one thread at a time.

### SuspendException
Unwinds the stack when suspending. Each catch block captures its frame and re-throws, building the frame chain.

### ContinuationRunner
An instance class. `Run` executes a computation and returns `Completed` or `Suspended`. `Resume(state, resumeValue, entryPoint)` restarts from a state. `Resume(continuation)` looks up the entry point in `EntryPoints`. If `Validator` is set, state is validated before every resume.

## Security

Deserialized state is checked twice:

1. **While deserializing.** A slot value may only have a type the serializer allows. For JSON, `SlotSerializationBinder` checks each `$type` before the object is constructed and throws `JsonSerializationException` for anything else. A type is allowed if any of these permit it (arrays and nullables follow their element type):
   - the `SlotTypeResolver` built-ins, or a resolver registered with `AddResolver`;
   - the serializer's `ContinuationTypeRegistry` (by default primitives, `string`, `decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`, and enums);
   - the serializer's `Validator` allow-list.

   To allow your own types, use any of the three, for example:

   ```csharp
   var serializer = new JsonContinuationSerializer(ContinuationTypeRegistry.Default.With(typeof(MyState)));
   ```

   If you pass your own `JsonSerializerSettings` with a `SerializationBinder` already set, that binder is used instead, and what it allows is up to you. `MessagePackContinuationSerializer` uses the contractless resolver, which ignores type names in the payload; custom reference types are rebuilt only when the `Validator` allows them. Do not give it options with a typeless resolver for untrusted input.

2. **Before resuming.** `ContinuationValidator` checks known method tokens (via registered `FrameDescriptor`s), valid yield point IDs, slot counts and types, maximum stack depth, and an allow-list of types. Set it on `ContinuationRunner.Validator` (or the serializer's `Validator`) when state comes from anywhere you do not trust.

## Building

```bash
dotnet build Prim.sln
dotnet test Prim.sln
```

## Running Samples

```bash
dotnet run --project samples/Generator/Prim.Samples.Generator
dotnet run --project samples/MigrationDemo/Prim.Samples.MigrationDemo
```

## Target Framework

Libraries target .NET Standard 2.0. Tests, samples and benchmarks target .NET 8.

## License

MIT
