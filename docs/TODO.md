# Prim: Remaining Work

## Current State

The framework is architecturally complete. Runtime, serialization, and analysis components are working and all tests pass (174 total).

## What's Left

### Cecil IL Transformation (High Priority)

The `MethodTransformer` has the structure in place but needs testing with real assemblies:

- **InjectYieldPointChecks** - Implemented, emits IL for yield checks before backward branches
- **WrapInTryCatch** - Implemented, wraps method bodies with catch block for state capture
- **AddRestoreBlock** - Implemented, emits entry prologue with switch dispatch to yield points

Location: [MethodTransformer.cs](../src/Prim.Cecil/MethodTransformer.cs)

The IL emission is complete but untested against real-world assemblies. Needs end-to-end integration tests.

### Roslyn Source Generator (Medium Priority)

The generator works for simple cases but is marked as a "simplified implementation". To handle real-world code:

- Loop transformation (while, for, foreach) - partially done
- Try-catch-finally blocks
- Nested method calls with yield points
- Complex control flow (switch expressions, pattern matching)

Location: [ContinuationGenerator.cs](../src/Prim.Roslyn/ContinuationGenerator.cs)

### Minor Items

- **Direct resume** - `RunRestoringWithContext<T>()` throws NotImplementedException; would allow resuming without re-specifying the entry point

### Testing Work Needed

- End-to-end tests that use the Cecil transformer on real assemblies
- Performance benchmarks comparing transformed vs original code overhead
- Tests for the Roslyn generator with complex control flow

## What's Done

- Core types (HostFrameRecord, ContinuationState, SuspendException)
- Runtime (ContinuationRunner, ScriptContext, ScriptScheduler)
- Serialization (JSON and MessagePack with object graph tracking)
- Analysis (CFG construction, stack simulation, yield point identification)
- Cecil IL transformation with E2E tests
- Roslyn source generator (works for simple cases)
- Instruction counting for preemptive scheduling (budget-based yield enforcement)
- Stable hashing for method tokens
- Working samples (Generator, MigrationDemo)
- Comprehensive test coverage (193 tests passing)
