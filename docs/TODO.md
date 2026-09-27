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
- Yield points inside `try`/`catch`/`finally` regions are skipped.
- Methods with byref/pointer locals or `ref`/`out` parameters are skipped
  (reported in `SkippedMethods`).

Location: [MethodTransformer.cs](../src/Prim.Cecil/MethodTransformer.cs)

### Serialization

- MessagePack does not preserve reference identity between slots (the skipped test
  in `SerializationTests`). JSON does.
- JSON turns a `DateTimeOffset` slot into a `DateTime` and loses its offset:
  Json.NET parses the date string into a `DateTime` before `SlotCodec.Coerce` sees
  it, and `Coerce` only converts strings to `DateTimeOffset`.

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
- Roslyn generator (replay model):
  - Loops (while, for, foreach, do-while)
  - Yield points inside `try` and `catch` blocks, with `SuspendException` filtered out of user catch clauses
  - Nested calls between `[Continuable]` methods
  - Diagnostics `PRIM001`–`PRIM003` for unsupported members, shapes and regions
- Samples (`Generator`, `MigrationDemo`) and BenchmarkDotNet benchmarks
