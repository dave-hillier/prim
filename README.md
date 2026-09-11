# Prim

A .NET continuation framework enabling suspend/resume of program execution with serializable state.

## Why Serializable Continuations?

Most runtimes support some form of suspension (coroutines, async/await, green threads), but the captured state typically exists only in memory. **Serializable continuations** let you persist that state to disk or transmit it to another machine.

This enables patterns that are otherwise difficult:

- **Transparent migration** - Move running computations between servers without the code knowing it moved
- **Durable execution** - Checkpoint long-running work and resume after crashes, without requiring deterministic replay
- **Cooperative multithreading for untrusted code** - Run many scripts on one thread with guaranteed yield points

Prim achieves this on stock .NET runtimes through explicit frame capture and
program transformation. No runtime modifications required.

## Background

The techniques in Prim were originally developed for Second Life's Mono integration (2007-2008), where user scripts needed to migrate seamlessly between simulator processes. A script counting to a million shouldn't restart from zero just because its object crossed a region boundary.

For the full technical details, design rationale, and comparison with related systems (WasmFX, Espresso, Project Loom), see the [whitepaper](docs/whitepaper.md).

## What Prim Does

- **Suspends** execution at yield points and captures participating stack frames
- **Serializes** the captured state to JSON or MessagePack
- **Resumes** execution from saved state, including after a process restart
- **Migrates** supported running computations across processes or machines

## Project Structure

```
Prim/
├── src/
│   ├── Prim.Core/           # Core types (HostFrameRecord, ContinuationState, etc.)
│   ├── Prim.Runtime/        # Execution context and runner
│   ├── Prim.Serialization/  # JSON and MessagePack serializers
│   ├── Prim.Roslyn/         # Source generator for [Continuable] methods
│   ├── Prim.Analysis/       # IL analysis (CFG, stack simulation)
│   └── Prim.Cecil/          # Bytecode rewriting with Mono.Cecil
├── tests/
│   ├── Prim.Tests.Unit/
│   ├── Prim.Tests.Integration/
│   ├── Prim.Tests.Roslyn/
│   └── Prim.Tests.Cecil/
└── samples/
    ├── Generator/           # Yield/resume demonstration
    └── MigrationDemo/       # Cross-process state migration
```

## Quick Start

> **Two paths, different maturity.** The smallest, most explicit way to make a
> method continuable is the *manual pattern* shown below: track state in instance
> fields and hand-write a `catch (SuspendException)` block that captures the
> frame. This path is exercised by the tests and the `Generator` sample.
>
> Automatic transformation — where you annotate a method with `[Continuable]` and
> let the Roslyn source generator or the Mono.Cecil bytecode rewriter generate the
> state machine for you — is wired up for selected cases and covered by end-to-end
> tests. It is still experimental and intentionally constrained, so read
> [Automatic Transformation](#automatic-transformation) before relying on it.

### Basic Yield/Resume (Manual Pattern)

The example below writes the suspend/capture logic by hand. State that must survive
a yield lives in instance fields (`Current`), and the `catch` block packs that state
into a frame record before re-throwing.

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
var counter = new Counter();
var runner = new ContinuationRunner();
var result = runner.Run(() => counter.CountTo(5));

while (result is ContinuationResult<int>.Suspended suspended)
{
    Console.WriteLine($"Yielded at: {counter.Current}");
    result = runner.Resume(
        suspended.State,
        resumeValue: null,
        entryPoint: () => counter.CountTo(5));
}

Console.WriteLine($"Completed: {((ContinuationResult<int>.Completed)result).Value}");
```

### Serialization and Migration

```csharp
using Prim.Runtime;
using Prim.Serialization;

var runner = new ContinuationRunner();
var serializer = new JsonContinuationSerializer();

// Serialize state
string json = serializer.SerializeToString(suspended.State);
File.WriteAllText("state.json", json);

// Later, in another process...
json = File.ReadAllText("state.json");
var state = serializer.DeserializeFromString(json);

var restoredCounter = new Counter();
if (state.StackHead?.Slots?.Length > 0)
{
    restoredCounter.Current = FrameCapture.GetSlot<int>(state.StackHead.Slots, 0);
}

var result = runner.Resume(
    state,
    resumeValue: null,
    entryPoint: () => restoredCounter.CountTo(5));
```

For direct resume without passing an entry point each time, configure
`ContinuationRunner.EntryPoints` with an `EntryPointRegistry`.

### Automatic Transformation

The longer-term ergonomic goal is to remove the manual boilerplate above. Instead
of writing the `try`/`catch (SuspendException)` block yourself, mark a method with
`[Continuable]` and let the framework generate exact suspend/capture/resume code
for transformed programs - much like the C# compiler does for `async`/`await`.

Two implementations of this transformation exist in the tree:

- **`Prim.Roslyn`** — a Roslyn source generator that rewrites `[Continuable]`
  methods at compile time.
- **`Prim.Cecil`** — a Mono.Cecil rewriter that transforms the compiled IL.

Both paths are experimental, but they are no longer just sketches. The Roslyn
tests invoke generated `*_Continuable` methods through real
suspend/serialize/resume cycles, and the Cecil tests verify and execute rewritten
IL.

Current important constraints:

- Roslyn currently uses a **replay** resume model as an implementation compromise:
  on resume, the generated method restores hoisted locals and re-runs the method
  body from the top. This is not the target semantic model for Prim. Observable
  side effects before a yield point can run again, so replay-based methods must
  be written with that limitation in mind.
- Roslyn currently diagnoses and skips async methods, iterators, generic methods,
  methods on generic types, and expression-bodied methods.
- Yield points inside `finally`, `lock`, or catch-filter regions are rejected.
- Cecil skips methods with state that cannot be boxed/round-tripped safely, such
  as byref, pointer, pinned, `ref`, or `out` state.
- MessagePack serialization does not preserve cross-slot reference identity for
  shared object instances. JSON does preserve this for supported reference types.

Roadmap goals:

- Make exact resume semantics the production path for transformed code.
- Support async/await continuable methods.
- Support iterator methods and richer generator-style control flow.
- Support generic methods and methods on generic types.
- Support closures, lambdas, captured variables, and compiler-generated display
  classes where their state can be serialized safely.
- Expand coverage for switch expressions, pattern matching, and more complex C#
  control-flow shapes.
- Generate a manifest of method tokens, yield point IDs, slot layouts, and entry
  points so validation and direct resume can be configured automatically.
- Decide and document a uniform object-graph identity contract across JSON and
  MessagePack.

## Core Concepts

### HostFrameRecord
A linked list node representing a captured stack frame. Contains the method
token, yield point ID, and captured slot values.

### ScriptContext
Thread-local context managing yield requests, restore state, and instruction
budgeting. Call `RequestYield()` to signal suspension, and `HandleYieldPoint()`
or `HandleYieldPointWithBudget()` at yield points to check and throw
`SuspendException`.

### SuspendException
Special exception used for stack unwinding during suspension. Each generated or
manual catch block captures its frame state and re-throws, building the frame
chain.

### ContinuationRunner
Entry point for running and resuming continuable computations. Handles the
`SuspendException` and packages results as `Completed` or `Suspended`.

## Building

```bash
dotnet tool restore
dotnet build Prim.sln
dotnet test Prim.sln
```

`dotnet tool restore` installs the pinned `dotnet-ilverify` tool used by the Cecil
verification tests.

## Running Samples

```bash
dotnet run --project samples/Generator/Prim.Samples.Generator/Prim.Samples.Generator.csproj
dotnet run --project samples/MigrationDemo/Prim.Samples.MigrationDemo/Prim.Samples.MigrationDemo.csproj
```

## Target Framework

Library projects target .NET Standard 2.0. Tests and samples target .NET 8.

## License

MIT
