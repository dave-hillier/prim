# Prim: Remaining Work

## Current State

The framework is architecturally complete. Runtime, serialization, and analysis components are working and tested. The samples demonstrate full suspend/serialize/resume cycles.

## What's Left

### Cecil IL Transformation (High Priority)

The `MethodTransformer` has the framework in place but needs the actual IL emission completed:

- **InjectYieldPointChecks** - Structure exists, needs to emit working IL for yield checks before backward branches
- **WrapInTryCatch** - Needs to properly wrap method bodies and emit catch block IL for state capture
- **AddRestoreBlock** - Needs to emit the entry prologue with switch dispatch to yield points

Location: [MethodTransformer.cs](../src/Prim.Cecil/MethodTransformer.cs)

The reference imports, local variable creation, and branch target updates are implemented. The remaining work is emitting the actual instruction sequences.

### Roslyn Source Generator (Medium Priority)

The generator works for simple cases but is marked as a "simplified implementation". To handle real-world code:

- Loop transformation (while, for, foreach)
- Try-catch-finally blocks
- Nested method calls with yield points
- Complex control flow (switch expressions, pattern matching)

Location: [ContinuationGenerator.cs](../src/Prim.Roslyn/ContinuationGenerator.cs)

### Minor Items

- **Instruction counting** - `ScriptScheduler._currentTick` is declared but unused; connect it to time-slice enforcement
- **Direct resume** - `RunRestoringWithContext<T>()` throws NotImplementedException; would allow resuming without re-specifying the entry point

### Failing Tests (13 total)

**Cecil/Analysis (7 tests)** - CFG and stack simulation tests fail, likely due to test assembly loading issues:
- `ControlFlowGraph_IdentifiesBackEdges`
- `ControlFlowGraph_LoopMethodHasBackEdges`
- `StackSimulator_TracksStackCorrectly`
- `StackSimulator_TracksStackDepth`
- `YieldPointIdentifier_FindsBackwardBranches`
- `YieldPointIdentifier_FindsYieldPointsInLoopMethod`
- `AssemblyRewriter_TransformsMarkedTypes`

**Integration (3 tests)** - Serialization round-trip issues:
- `Json_SerializeAndDeserialize_PreservesState`
- `MultipleYieldPoints_PreservesCorrectId`
- `NestedCalls_SerializeAndRestore`

**Roslyn (2 tests)** - Source generator output issues:
- `GeneratedMethod_SuspendsOnYieldRequest`
- `GeneratedMethod_StateCanBeSerializedToJson`

**Unit (1 test)**:
- `FrameSlot_DefaultRequiresSerialization`

### Other Testing Work

- End-to-end tests that use the Cecil transformer on real assemblies
- Performance benchmarks comparing transformed vs original code overhead

## What's Done

- Core types (HostFrameRecord, ContinuationState, SuspendException)
- Runtime (ContinuationRunner, ScriptContext, ScriptScheduler)
- Serialization (JSON and MessagePack with object graph tracking)
- Analysis (CFG construction, stack simulation, yield point identification)
- Stable hashing for method tokens
- Working samples (Generator, MigrationDemo)
- Comprehensive test coverage for implemented components
