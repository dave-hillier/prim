# Prim

Prim suspends and resumes .NET program execution and can serialize the suspended state.

## Why Serializable Continuations?

Most runtimes support some form of suspension (coroutines, async/await, green threads), but the suspended state exists only in memory. A serializable continuation can be written to disk or sent to another machine. Uses:

- **Migration**: move a running computation to another server. The code is not aware that it moved.
- **Durable execution**: checkpoint long-running work and resume after a crash, without an effect log.
- **Cooperative multithreading for untrusted code**: run many scripts on one thread, with yield points the scripts cannot avoid.

Prim does this by transforming the program. It runs on unmodified .NET runtimes.

## Background

Prim reimplements the approach used by Second Life's Mono script engine (in production from 2008), where user scripts had to migrate between simulator processes mid-execution. A script counting to a million must not restart from zero because its object crossed a region boundary. Second Life's rewriter saved each frame and returned; Prim unwinds with an exception instead. Both designs follow Java mobile-agent systems published from 1998 onward.

The [whitepaper](docs/whitepaper.md) describes the design and compares it with related systems (WasmFX, Espresso, Project Loom). [docs/research](docs/research/README.md) holds the prior-art survey and its source notes.

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
| `Prim.Cecil` IL rewriter | Experimental. Output passes `ilverify` in tests; no MSBuild integration yet. Yield points inside `try` blocks are supported; those inside `catch`, `finally`, filters and `lock` bodies are skipped. |

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
└── docs/                    # Whitepaper, TODO, blog/ (draft post), research/ (prior-art survey and notes)
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

For a `partial` type (class, struct, record or record struct, including nested types whose containing types are all partial) the generator adds `SumTo_Continuable(int n)` next to the original, with the same accessibility and `static` modifier. For a non-partial top-level class it emits a static `SummerContinuations.SumTo_Continuable(Summer instance, int n)` instead (public if both the class and the method are public, otherwise internal). That copy lives in another class, so the body may only use members that are accessible from outside the type; otherwise the method is rejected with `PRIM004`. Each loop is checked at the start of every iteration, and each statement that calls another `[Continuable]` method of the same type is a yield point too. The generated code captures locals, so the state can move between processes. It is compiled with the `using` directives of the file the method came from.

The method token in each captured frame is `StableHash.GenerateMethodToken(typeName, methodName, parameterTypes...)`, where `typeName` is the namespace plus containing types (`Ns.Outer.Inner`) and each parameter type is its resolved C# name (`int`, `string`, `System.Collections.Generic.List<int>`), prefixed with `ref `, `out ` or `in ` for by-reference parameters. Writing `Int32` or `int` gives the same token.

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

- **Replay.** On resume, locals are restored and the method body runs again *from the top*; it does not jump to the yield point. Code before a yield point runs again, so any side effect there (writing a field, printing, sending a message) happens again, and a `finally` runs both when the method suspends and when it finally leaves the block. Put side effects that must not repeat after the yield point. See [whitepaper §6.3](docs/whitepaper.md).
- **Replay must be deterministic.** Every yield site (loop iteration or continuable call) is counted, and the counts are saved with the state. On resume, yield checks neither suspend nor spend budget until the replay reaches the site where the run suspended, so a resumed method always gets past that point. If the replayed code takes a different path (it reads the clock, a random number, or state that changed), the replay may not reach that site; checks then stay off until the outermost continuable call that was running at the suspension returns.
- **Outside a runner** (calling `X_Continuable` directly, with no `ContinuationRunner`), the generated code uses a private context with an unlimited budget, so it never suspends.
- **Reused thread context.** `ContinuationRunner.Run` reuses a thread's existing `ScriptContext` (for example one created by `ScriptContext.EnsureCurrent()`). If such a run suspends, the saved site count may be stale, and the first resume can end its replay at the wrong place. The result is still correct, but that resume may run unpreempted or suspend one extra time. `Resume` always uses a fresh context, so later resumes are exact.
- **Locals.** A local is hoisted (captured in a slot) when it is declared by a declaration statement in a block, branch, loop body, `try`/`catch`/`finally`, `switch` section, `checked`/`unsafe` block or label, or is an unfiltered catch variable. These locals are never hoisted and keep their declaration: locals captured by a lambda or local function (each closure keeps its own variable), `const`, `ref` and `using` locals, locals of anonymous, ref-like (`Span<T>`) or pointer types, pattern and `out` variables, and anything declared inside a lambda, local function, `lock` or `fixed` body. They are not in the captured state, but replay recomputes them.
- **Continuable calls.** Only calls that resolve to a generated `[Continuable]` method of the same type are rewritten to `_Continuable`. Calls inside lambdas and local functions, and inside `lock` or `fixed` bodies, call the original method and cannot suspend. A call in an `if`, loop, `switch`, `using` or `foreach` header does not get its own yield point. It resumes because the statement is replayed. Frame records are matched to calls by method token. With several calls to the same method (in one statement, in a loop or in a header), a record may be restored into the wrong call, but replay overwrites restored values before reading them, so results do not change.
- **Diagnostics.** In each case no code is generated for the member:
  - `PRIM001`: `[Continuable]` on something that is not a method (including types and local functions).
  - `PRIM002`: async, iterator, generic, expression-bodied, body-less or ref-returning methods; methods on generic or file-local types; nested types whose containing types are not all partial; non-partial structs.
  - `PRIM003` (error): a yield point inside `finally`, `lock` or a catch filter.
  - `PRIM004`: the body uses something the copy cannot reproduce: a primary constructor parameter, a preprocessor directive other than `#region`, `#pragma warning` or `#nullable`, or, for a non-partial class, `base`, a member that is not accessible from outside the type, a `?.` call to a continuable method, or the name `instance`.

### IL Rewriting (`Prim.Cecil`, experimental)

`AssemblyRewriter` rewrites a compiled assembly: `new AssemblyRewriter(options).Transform(inputPath, outputPath)`. It adds yield checks at loop back-edges (with optional instruction counting via `HandleYieldPointWithBudget`) and resume points at calls to other transformed methods. It does not use replay: it spills the evaluation stack and jumps back to the yield point. It is not yet hooked into the build.

- **Which methods.** A method is rewritten if it has `[Continuable]` itself, in any type: nested, without the attribute, or a struct (static methods only). `[Continuable]` on a class neither is needed nor selects its methods.
- **What is saved.** Locals, the arguments the method assigns or takes the address of, and the live evaluation stack. Generic methods and methods on generic types work. `this` is not saved: whoever resumes passes it in again.
- **Resume points.** A call is a resume point if its target is a transformed method, or is a virtual or interface method that a transformed method overrides or implements. If a suspension comes back through any other call (a delegate, a method that was not transformed, a call inside a `catch` or `finally`), the frame throws `InvalidOperationException` as it suspends. It does not save a frame that would resume in the wrong place.
- **Skipped methods** (listed in `SkippedMethods`, left unchanged): methods without a body, instance methods on structs, methods with byref/pointer locals or `ref`/`out` parameters, and methods with a managed pointer on the evaluation stack at a yield point. Calls to them are not resume points.
- **Skipped yield points** (listed in `SkippedYieldPoints`): yield points where a value on the evaluation stack cannot be given a type. Examples are a method pointer, or a mix of two classes whose nearest common base class is defined in another assembly. The rest of the method is still rewritten.

Yield points inside `try` blocks, including nested ones, are supported. On resume the method enters each enclosing `try` block at its first instruction, where a nested dispatch jumps on to the next block or to the yield point. The method's own handlers do not see a suspension: a `catch` that could catch `SuspendException` (`catch (Exception)`, a bare `catch`) becomes a filter that rejects it, user `when` filters reject it before running, and a `finally` does not run while the method suspends. The `finally` runs once, when the method really leaves the `try` block after resuming, as if it had never suspended. (The source generator differs: under replay its `finally` runs on every suspension.) Yield points inside a `catch` handler, a `finally` block, a filter or a `lock` body are skipped and listed in `SkippedYieldPoints`, because only the runtime can enter a handler, so there is nowhere to resume.

## Core Concepts

### HostFrameRecord
A linked list node representing a captured stack frame. Contains the method token, yield point ID, captured slots, and a `Caller` link. The chain head (`ContinuationState.StackHead`) is the outermost frame.

### ScriptContext
Thread-local context. `RequestYield()` sets the yield flag; `HandleYieldPoint()` throws `SuspendException` if it is set. `HandleYieldPointWithBudget()` also decrements `InstructionBudget` and suspends when it runs out, which is how untrusted code is preempted without a timer. The budget is not synchronised, so a context should only be used from one thread at a time.

### SuspendException
Unwinds the stack when suspending. Each catch block captures its frame and re-throws, building the frame chain.

### ContinuationRunner
An instance class. `Run` executes a computation and returns `Completed` or `Suspended`. `Resume(state, resumeValue, entryPoint)` restarts from a state. `Resume(continuation)` looks up the entry point in `EntryPoints`. If `Validator` is set, state is validated before every resume. Set `InstructionBudget` (nullable `int`) to choose the budget of the contexts the runner creates in `Run` and `Resume`; left null, the default budget applies.

## Security

Deserialized state is checked twice:

1. **While deserializing.** A slot value may only have a type the serializer allows. For JSON, `SlotSerializationBinder` checks each `$type` before the object is constructed and throws `JsonSerializationException` for anything else. There is one type allow-list, owned by `ContinuationValidator`. A type is allowed if any of these permit it (arrays and nullables follow their element type):
   - the validator's built-in types: primitives, `string`, `decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`, and enums. These are allowed even with no `Validator` set;
   - types registered on the serializer's `Validator`;
   - the `SlotTypeResolver` built-ins, or a resolver registered with `AddResolver` (JSON only).

   To allow your own types, register them on a validator. `ValidationOptions.Lenient` skips frame descriptor checks, and on this allow-list it still checks types. A Lenient validator set on `ContinuationRunner.Validator` skips slot type checks entirely, so use it there only for state you trust:

   ```csharp
   var validator = new ContinuationValidator(ValidationOptions.Lenient);
   validator.RegisterAllowedType(typeof(MyState));
   var serializer = new JsonContinuationSerializer { Validator = validator };
   ```

   `ContinuationTypeRegistry` and the serializer constructors that take one are obsolete. They still work, and wrap a validator.

   If you pass your own `JsonSerializerSettings` with a `SerializationBinder` already set, that binder is used instead, and what it allows is up to you. `MessagePackContinuationSerializer` uses the contractless resolver, which ignores type names in the payload; custom reference types are rebuilt only when the `Validator` allows them. Do not give it options with a typeless resolver for untrusted input.

2. **Before resuming.** `ContinuationValidator` checks known method tokens (via registered `FrameDescriptor`s), valid yield point IDs, slot counts and types, maximum stack depth, and an allow-list of types. Set it on `ContinuationRunner.Validator` (or the serializer's `Validator`) when state comes from anywhere you do not trust. The validator checks each frame independently; it does not verify that the frames form a valid call chain.

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
