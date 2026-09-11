# Prim: Remaining Work

## Current State

Prim has a working core architecture for serializable continuations over
participating transformed/manual frames. Runtime, serialization, validation,
analysis, Roslyn generation, Cecil rewriting, samples, benchmarks, and
end-to-end tests are present.

The project is not yet at the stronger goal of transparent exact-resume
semantics for the full intended transformed-code subset. In particular, the
Roslyn generator currently uses replay as a progress-enabling compromise rather
than as the desired final semantic model.

## Product Goal

For code that has been preprocessed or transformed by Prim, suspension should
capture enough state to serialize, move, and resume the computation elsewhere
with the same observable behavior, within a clearly documented supported subset.
Code outside that subset should be rejected with clear diagnostics rather than
being partially transformed.

## What's Left

### Exact Resume Semantics

- Make exact resume the production path for transformed code.
- Decide whether Roslyn should grow exact state-machine dispatch or remain a
  readable/reference implementation while Cecil carries the production path.
- Add tests for side effects before and after yield points, so replay-only
  behavior cannot be mistaken for transparent resume.

### Broader C# Support

- Support async/await continuable methods.
- Support iterator methods and richer generator-style control flow.
- Support generic methods and methods on generic types.
- Support closures, lambdas, captured variables, and compiler-generated display
  classes where their state can be serialized safely.
- Expand coverage for switch expressions, pattern matching, and more complex
  control-flow shapes.

### Tooling And Metadata

- Generate a manifest of method tokens, yield point IDs, slot layouts, supported
  transform version, and entry points.
- Use that manifest to configure validation and direct resume automatically.
- Ensure CI restores local dotnet tools before running tests.

### Serialization Contract

- Decide whether MessagePack should preserve cross-slot reference identity like
  JSON, or document MessagePack as value-like-state serialization.
- Document supported slot types, identity behavior, version compatibility, and
  migration failure modes.

## What's Done

- Core types (HostFrameRecord, ContinuationState, SuspendException)
- Runtime (ContinuationRunner, ScriptContext)
- Budget-based preemption (ScriptContext.HandleYieldPointWithBudget / RequestYield)
- Serialization (JSON and MessagePack typed slot envelopes)
- Analysis (CFG construction, stack simulation, yield point identification)
- Cecil IL transformation with IL verification and execute/resume tests
- Roslyn source generator with selected end-to-end suspend/serialize/resume tests
- Loop transformation coverage (while, for, foreach, do-while)
- Try/catch/finally handling with forbidden-region diagnostics
- SuspendException filtering in generated catch clauses
- Nested continuable method calls for selected forms
- Instruction counting for preemptive scheduling
- Security validation for deserialized state (method tokens, yield points, slot
  types, type whitelist, bounded stack/array depth)
- Direct resume support via EntryPointRegistry
- Performance benchmarks
- Stable hashing for method tokens, with collision detection at descriptor
  registration
- Working samples (Generator, MigrationDemo)
