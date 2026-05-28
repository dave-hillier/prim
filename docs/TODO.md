# Prim: Remaining Work

## Current State

The framework is architecturally complete. Runtime, serialization, analysis, and security components are working and all tests pass (256 total).

## What's Left

### Roslyn Source Generator (Low Priority)

The generator handles most common cases. Remaining edge cases:

- Complex control flow (switch expressions, pattern matching)

Location: [ContinuationGenerator.cs](../src/Prim.Roslyn/ContinuationGenerator.cs)

## What's Done

- Core types (HostFrameRecord, ContinuationState, SuspendException)
- Runtime (ContinuationRunner, ScriptContext)
- Budget-based preemption (ScriptContext.HandleYieldPointWithBudget / RequestYield)
- Serialization (JSON and MessagePack with object graph tracking)
- Analysis (CFG construction, stack simulation, yield point identification)
- Cecil IL transformation with E2E tests
- Roslyn source generator with:
  - Loop transformation (while, for, foreach, do-while)
  - Try-catch-finally blocks (including nested and finally with yield points)
  - Proper SuspendException filtering in catch clauses
  - Nested continuable method calls (calls between [Continuable] methods are transformed)
- Instruction counting for preemptive scheduling (budget-based yield enforcement)
- Security validation for deserialized state (method tokens, yield points, slot types, type whitelist)
- Direct resume without entry point (EntryPointRegistry maps method tokens to delegates)
- Performance benchmarks (transform overhead, suspension/resume, serialization, validation)
- Stable hashing for method tokens
- Working samples (Generator, MigrationDemo)
- Comprehensive test coverage (256 tests passing)
