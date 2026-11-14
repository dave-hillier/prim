# Prim: Remaining Work

## Current State

The runtime (`Prim.Core`, `Prim.Runtime`) and serialization are working. Both
automatic transformers are experimental: the Roslyn source generator works end to
end for supported method shapes, and the Cecil IL rewriter produces verifiable IL
but is not yet hooked into a build.

`dotnet test Prim.sln` passes, with one test skipped (MessagePack reference
identity, below).

## What's Left

### Roslyn Source Generator

The generator uses a replay model: on resume the body re-runs from the top over
restored locals, so side effects before a yield point repeat. Remaining gaps:

- Continuable calls inside an `if`/loop condition or a `switch` do not get their
  own yield point; they rely on the whole statement being replayed.
- Unsupported shapes (reported as `PRIM002`): async, iterator, generic and
  expression-bodied methods, and methods on generic types.
- There is no explicit yield statement: suspension happens only at loop headers
  and continuable calls. Adding one needs replay-aware emission so that a resumed
  method does not suspend again at the same point.

Location: [ContinuationGenerator.cs](../src/Prim.Roslyn/ContinuationGenerator.cs)

### Cecil IL Rewriter

- No MSBuild task or CLI; `AssemblyRewriter.Transform` must be called from code.
- Yield points inside catch handlers, `finally`/`fault` blocks, exception filters
  and `lock` bodies are skipped (reported in `SkippedYieldPoints`). Only the
  runtime can enter a handler, so there is nowhere to resume. Yield points inside
  `try` blocks are supported.
- A suspension that comes back through a call that is not a resume point cannot be
  resumed, so the frame throws `InvalidOperationException` as it suspends. This
  happens with a delegate call, a call to a method that was not transformed (one
  without `[Continuable]`, or one that was skipped), or a call to a transformed
  method inside a catch handler or `finally`. Making delegate calls resume points
  would let a delegate reach a transformed method. Only calls whose target is known
  to be, or may dispatch to, a transformed method are resume points today. A user
  `catch (Exception)` in a caller sees this exception like any other.
- Yield points where an evaluation-stack value cannot be typed are skipped
  (reported in `SkippedYieldPoints`). Example: two classes meet at a join and
  their nearest common base class is defined in another assembly.
  `StackSimulator` does not import such base classes. It also gives up on generic
  instances, arrays and interfaces at a join. Importing them and merging
  interfaces would let these yield points be placed.
- A boxed value (`box`) is typed `object` on the evaluation stack. If it is
  spilled at a yield point and then used as an interface, the output still runs
  but does not pass `ilverify`.
- Instance methods on structs are skipped: `this` points into the caller's
  storage, which the frame cannot capture. `this` is never captured for classes
  either; it is passed in again on resume.
- Suspending inside a `try`/`finally` defers the `finally` until the method
  really leaves the block after resuming. That keeps a `using` resource alive (and
  in the captured state) across the suspension, so it must survive serialization
  if the state is serialized.
- Methods with byref/pointer locals or `ref`/`out` parameters are skipped
  (reported in `SkippedMethods`).
- CI does not run `dotnet tool restore`. The ilverify tests now restore the tool
  themselves and fail if it cannot be run. Before this they passed without
  verifying anything whenever the tool was not restored.

Location: [MethodTransformer.cs](../src/Prim.Cecil/MethodTransformer.cs)

### Serialization

- MessagePack does not preserve reference identity between slots (the skipped test
  in `SerializationTests`). JSON does.
- A `DateTimeOffset` passed as the yielded value (`ContinuationState.YieldedValue`)
  comes back from JSON as a `DateTime` without its offset. Unlike slots, the
  yielded value carries no type name, so fixing it needs a wire-format change.

## What's Done

- Core types (`HostFrameRecord`, `ContinuationState`, `SuspendException`, `ContinuationResult`, `Continuation<T>`)
- Runtime (`ContinuationRunner`, `ScriptContext`, `FrameCapture`)
- Budget-based preemption (`ScriptContext.HandleYieldPointWithBudget`, `ResetBudget`)
- Direct resume without re-supplying the entry point (`EntryPointRegistry`)
- Serialization: JSON (with shared-reference preservation) and MessagePack, typed slot envelopes, parser depth limits
- Validation of deserialized state (`ContinuationValidator`: method tokens, yield point IDs, slot counts and types, stack depth, type allow-list)
- JSON `$type` checked against an allow-list before any object is constructed (`SlotSerializationBinder`)
- Stable hashing for method tokens
- IL analysis (CFG construction, stack simulation, yield point identification)
- Cecil rewriter: back-edge yield checks, optional instruction counting, resume at calls to other transformed methods, output checked with `ilverify`
  - Yield points inside `try` blocks, including nested ones: a nested dispatch at each block's first instruction, `SuspendException` filtered out of user catch clauses and filters, and `finally` blocks skipped while suspending
- Roslyn generator (replay model):
  - Loops (while, for, foreach, do-while)
  - Yield points inside `try` and `catch` blocks, with `SuspendException` filtered out of user catch clauses
  - Nested calls between `[Continuable]` methods
  - Diagnostics `PRIM001`–`PRIM003` for unsupported members, shapes and regions
- Samples (`Generator`, `MigrationDemo`) and BenchmarkDotNet benchmarks
