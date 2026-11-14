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

- Continuable calls in an `if`/loop/`switch`/`using`/`foreach` header do not get
  their own yield point; they rely on the statement being replayed. Frame records
  are matched to calls by method token only, so with several calls to one method
  a record can be restored into the wrong call. Restored values are always
  overwritten by the replay, so this does not change results, but per-call
  matching would be needed before resume stops relying on replay.
- Only calls to `[Continuable]` methods of the same type are rewritten; calls to
  another type's continuable methods run the original method.
- Replay needs deterministic pre-yield code. If the replay does not reach the
  saved (entry, site) position, yield checks stay off until the outermost
  continuable call returns.
- `ContinuationRunner.Run` reuses a thread's existing `ScriptContext`. After a run
  on a reused context, the first resume may end its replay at the wrong place
  (correct result, possibly unpreempted). Fixing it needs a run boundary the
  generated code can see, for example the runner always creating a fresh
  context or resetting a counter on the context.
- Unsupported shapes (reported as `PRIM002`): async, iterator, generic,
  expression-bodied and ref-returning methods, methods on generic or file-local
  types, nested types in non-partial types, and non-partial structs. Bodies the
  copy cannot reproduce are reported as `PRIM004`. For a non-partial class that
  means anything not accessible from outside the type.
- There is no explicit yield statement: suspension happens only at loop
  iterations and continuable calls. The replay counter would let one resume
  without suspending again at the same point, but it has not been added.
- The incremental pipeline matches `[Continuable]` with
  `ForAttributeWithMetadataName`, but still combines with the whole compilation
  (emission needs semantic models of each type), so any edit regenerates.

Location: [ContinuationGenerator.cs](../src/Prim.Roslyn/ContinuationGenerator.cs)

### Cecil IL Rewriter

- No MSBuild task or CLI; `AssemblyRewriter.Transform` must be called from code.
- Yield points inside catch handlers, `finally`/`fault` blocks, exception filters
  and `lock` bodies are skipped (reported in `SkippedYieldPoints`). Only the
  runtime can enter a handler, so there is nowhere to resume. Yield points inside
  `try` blocks are supported.
- A call to another transformed method inside a catch handler or `finally` gets
  no resume point. If the callee suspends there, the caller's frame records its
  last yield point instead, so the resume goes to the wrong place.
- Suspending inside a `try`/`finally` defers the `finally` until the method
  really leaves the block after resuming. That keeps a `using` resource alive (and
  in the captured state) across the suspension, so it must survive serialization
  if the state is serialized.
- Methods with byref/pointer locals or `ref`/`out` parameters are skipped
  (reported in `SkippedMethods`).

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
  - Loops (while, for, foreach, do-while), checked at the start of every iteration; a replay counter keeps a resumed method from suspending before it passes the saved point
  - Yield points inside `try` and `catch` blocks, with `SuspendException` filtered out of user catch clauses (user `when` filters kept)
  - Nested calls between `[Continuable]` methods, resolved by symbol
  - Partial classes, structs, records and nested types; non-partial classes through a static wrapper; the source file's `using` directives
  - Diagnostics `PRIM001`–`PRIM004` for unsupported members, shapes, regions and bodies
- Samples (`Generator`, `MigrationDemo`) and BenchmarkDotNet benchmarks
