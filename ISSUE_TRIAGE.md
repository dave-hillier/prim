# Prim issue triage & remediation plan

_Generated from a 32-agent read-only verification of every open issue against current `main`. No code was changed._

## Close without work (stale / filed against non-existent code)

- **#23** — Status stale-already-fixed. Exception filters `when (!(x is SuspendException))` were added to all generated catch clauses in commit bf39e7a (ContinuationGenerator.cs:701-707). SuspendException now propagates to the rewriter's capture handler. No work required; close it.
- **#46** — Status stale-already-fixed and rootCauseConfirmed=false. The Floyd's-algorithm code the issue describes never existed in any commit. GetRootFrame (ContinuationRunner.cs:208-217) is a simple correct linear walk of the Caller chain. Filed against non-existent code; close it.

## Corrections — issues whose text misstates current code

These are *partially* valid; update the issue body before working them so the fix targets the real defect:

- **#15** (Process: source generator is never wired as an analyzer, so it never runs): The issue partially exists but with a twist. File:/Users/davehillier/repos/prim/tests/Prim.Tests.Roslyn/Prim.Tests.Roslyn.csproj:27 shows: `OutputItemType="Analyzer" ReferenceOutputAssembly="true"` which is incorrect. It should be `ReferenceOutputAssembly="false"` to match the correct pattern in samples/Generator/Prim.Samples.Generator/Prim.Samples.Generator.csproj:17-19 and samples/MigrationDemo/
- **#29** (Roslyn: using-statement locals are added after analysis, never declared, corrupting slot indices): ContinuationGenerator.cs:GetLocalVariables (lines 834-858) only collects LocalDeclarationStatementSyntax. No handling exists for UsingStatementSyntax or UsingDeclarationSyntax, meaning using-statement resource variables are NOT collected during the initial locals pass (lines 282-285 where all locals are declared). When a using statement is encountered in GenerateStatement (lines 437-519), there's 
- **#39** (Serialization: JSON does not preserve object-graph identity for slot payloads): File: /Users/davehillier/repos/prim/src/Prim.Serialization/JsonContinuationSerializer.cs, lines 19-20 show TypeNameHandling.Auto with PreserveReferencesHandling.Objects (NOT TypeNameHandling.None as issue claims). Issue description is factually inaccurate about current code state. However, no test validates that shared user-defined objects in multiple slots are reconstructed with preserved referen
- **#41** (Security: MessagePack typeless resolver is a deserialization-gadget surface; whitelist checked post-construction): MessagePackContinuationSerializer.cs:20 uses ContractlessStandardResolver (not typeless as claimed); no RestrictedObjectFormatter/RestrictedObjectArrayFormatter exist (lines :282, :333 referenced in issue do not exist). However, ContinuationStateDto:106 has `object YieldedValue` and HostFrameRecordDto:125 has `object[] Slots` which can deserialize to any type via MessagePack. Validation in Continu
- **#45** (Serialization: ObjectGraphTracker is dead code and internally inconsistent): ObjectGraphTracker.cs is a public class (line 12) that is never referenced by JsonContinuationSerializer.cs or MessagePackContinuationSerializer.cs. JsonContinuationSerializer uses JSON.NET's PreserveReferencesHandling.Objects (line 20 of JsonContinuationSerializer.cs). MessagePackContinuationSerializer uses ContractlessStandardResolver (line 20 of MessagePackContinuationSerializer.cs). ObjectGrap

## Recommended batches (each later batch builds on the earlier)

### Batch 1: Quick wins and stale-issue cleanup (parallel)
Issues: #15, #18, #23, #45, #46, #36, #37

Independently fixable trivial/small items plus closing the two stale issues. 23 and 46 are pure closes (no code). 15 corrects analyzer wiring. 18 and 45 delete dead code (ScriptScheduler, ObjectGraphTracker), shrinking surface before the harder pipeline work. 36 and 37 are localized Cecil/Analysis hardening with no shared rearchitecture. None of these touch the resume/spilling internals, so they can land in parallel without conflict.

### Batch 2: Make the tests honest (test-credibility gate)
Issues: #16, #17, #19

Before trusting any pipeline fix, the tests must actually exercise generated/rewritten code. 16 makes the Roslyn tests call the *_Continuable generated methods through a real suspend/serialize/resume cycle (depends on the analyzer wiring from issue 15). 17 adds CLR-load-and-invoke (and/or ilverify) for the Cecil output so IL correctness is verifiable. 19 corrects the README so the documented capability matches reality. These establish the verification harness that every later batch relies on to prove correctness.

### Batch 3: Cecil stack-analysis foundation (StackSimulator + offset integrity) — **must be ONE coherent change, not parallel patches**
Issues: #33, #35

33 (CFG-aware StackSimulator with correct push/pop modeling and exception-handler init) is the dataflow foundation that eval-stack spilling, MaxStackSize recompute, and byref handling all need; it explicitly dependsOn 35. 35 fixes stale instruction offsets after insertions, which the cost/ordering logic 33 consumes. Doing these first gives the Cecil rearchitecture a trustworthy stack model. Keep as a coherent change since 35 underpins 33's correctness.

### Batch 4: Cecil resume/spilling rearchitecture (single coherent change) — **must be ONE coherent change, not parallel patches**
Issues: #30, #31, #32, #34, #37, #38

These all rewrite the same resume/capture mechanism in MethodTransformer (WrapInTryCatch, InjectYieldPointChecks, AddRestoreBlock) and conflict if patched separately. 31 fixes branching from outside into the try region (invalid IL); 32 fixes the resume target (.Next); 30 adds eval-stack spill/restore consuming the StackState from batch 3; 38 extends capture/restore to ref/out/byref/pinned locals and parameters; 34 recomputes MaxStackSize (now feasible with corrected simulator) and sets InitLocals; 37 (if not already done in batch 1) supplies the order-independent slot accounting this restructure needs. Treat the resume-block redesign as one atomic change validated by the batch-2 CLR-execution/ilverify tests. (37 listed in batch 1 as a quick win; if deferred, fold it here.)

### Batch 5: Roslyn signature/locals fidelity rearchitecture (shared semantic-model rewrite) — **must be ONE coherent change, not parallel patches**
Issues: #25, #26, #27, #28, #29

25/26/27/28/29 are flagged sharedRewriteWith each other and 25/26 are independentlyFixable=false. They all require threading Compilation/SemanticModel through ContinuationGenerator's signature and local-hoisting pipeline: 25 (var/parameter/return types via semantic model), 26 (async/generic/iterator/property signature preservation + diagnostics), 27 (dedup/rename hoisted locals from nested scopes), 28 (foreach back-edge resume semantics), 29 (using-statement/declaration resource locals). Done as one coherent pass over GetLocalVariables + GenerateTransformedMethod so the hoisting/type model is consistent; piecemeal patches would repeatedly re-touch the same traversal and signature code.

### Batch 6: Roslyn resume control-flow + yield-point ID unification rearchitecture — **must be ONE coherent change, not parallel patches**
Issues: #20, #21, #22

These are the core Roslyn pipeline criticals and are interdependent: 20 (resume goto cannot jump into nested C# scopes) forces a change to the dispatch/resume model; 21 (eval-stack/sub-expression yield points, sharedRewriteWith 22) requires StackSimulator-driven spilling integrated into generation; 22 (yield-point ID divergence between analyzer and rewriter) demands a single source of truth for IDs and emitted FrameDescriptors. The resume model, the set of yield points, and their IDs must be designed together, so this is one rearchitecture, sequenced after the signature/locals foundation (batch 5) so generated code already compiles before the resume mechanism is reworked.

### Batch 7: Serialization correctness (identity, type fidelity, DoS)
Issues: #39, #40, #42

Serialization fixes that the security-validator overhaul depends on. 40 (JSON vs MessagePack type-fidelity divergence causing validator false negatives) and 39 (reference-identity preservation, leveraging or replacing the now-deleted tracker) define the on-the-wire contract; 42 (bounded/iterative Caller-chain (de)serialization to stop stack-overflow DoS) hardens both serializers. Doing type fidelity (40) before the validator hardening (batch 8) ensures the validator's exact-type checks operate on faithful data.

### Batch 8: Security trust-anchor and validator hardening
Issues: #41, #43, #44

Final security layer, built on the corrected serialization contract from batch 7. 41 (pre-construction whitelist resolver for MessagePack to close the deserialization-gadget surface), 43 (validator bypasses: exact slot count, validate all slots, assembly-qualified type identity, bounded recursion, and actually invoke the validator from Deserialize/Resume), 44 (replace weak 32-bit FNV token with a signature manifest / wider hash validated at resume). These share the validator and descriptor structures and depend on type fidelity (40) and bounded deserialization (42) already being correct.

## Critical path

The deepest chain runs through the Cecil and Roslyn pipelines. Foundational and highest-leverage: (1) test credibility (16/17/19) — until tests actually execute generated and rewritten code, every \"fix\" to either pipeline is unverifiable, so this gates all downstream confidence; and (2) the Cecil StackSimulator rewrite (33, which dependsOn 35) — it is the dataflow substrate that eval-stack spilling (30), MaxStackSize recompute (34), byref handling (38) on the Cecil side, and the eval-stack spilling for sub-expression yields (21) on the Roslyn side all consume.

Two distinct rearchitectures must each be a single coherent change rather than competing patches: the Cecil resume/capture block (30/31/32/34/37/38 — all rewrite WrapInTryCatch/InjectYieldPointChecks/AddRestoreBlock), and the Roslyn pipeline split across signature/locals fidelity (25/26/27/28/29, all sharedRewriteWith) then resume control-flow + yield-point-ID unification (20/21/22). Riskiest items: 20 (nested-scope resume requires rearchitecting the dispatch model), 22 (changing the ID scheme can silently invalidate frame validation), 33 (CFG-aware simulation may surface latent stack mismatches and break code assuming all stack types are object), and 43/41 (stricter validation and a whitelist resolver are breaking changes that can reject previously-accepted continuations). The serialization/security batches (7, 8) sit at the end because validator hardening depends on a faithful, bounded serialization contract.

## Sequencing notes

Issue 37 appears in both batch 1 (as an independently-fixable quick win) and batch 4 (because the Cecil resume rearchitecture needs order-independent slot accounting). Land it in batch 1 if convenient; otherwise fold it into batch 4 — do not patch it twice.

Issue 32 is a one-line correct fix but lives in AddRestoreBlock, the same method being rewritten in batch 4. If batch 4 is imminent, do 32 there to avoid merge churn; it is listed as a quick win only because it is trivially correct and low-risk in isolation.

Issue 45 (delete ObjectGraphTracker) interacts with issue 39: 39's fix proposes possibly using ObjectGraphTracker for reference tracking. Recommendation — delete the tracker (45) and have 39 validate/repair reference identity through the live serializers (JSON PreserveReferencesHandling, MessagePack), since the tracker is dead and inconsistent. Sequence 45 with batch 7 in mind, or just delete now and build 39 on the real serializers.

Issues 39, 41, 45 are partially-confirmed: their descriptions misstate the current code (e.g., 39/41 claim TypeNameHandling.None/typeless resolver, but the code uses Auto+PreserveReferences / ContractlessStandardResolver). The residual real work (missing reference-identity test for 39; pre-construction whitelist for 41; dead-code removal for 45) is still valid — update the issue text to match reality before starting so the fix targets the actual defect.

The shared 'mustBeOneChange' batches (3, 4, 5, 6) should each be developed on their own branch with the batch-2 execution/ilverify tests as the acceptance gate. Within those batches, resist splitting into parallel PRs — they touch the same methods and will conflict.

34's MaxStackSize recompute is only reliable once 33's corrected simulator exists; the InitLocals half of 34 is safe to do anytime. They are kept together in batch 4 but the InitLocals part could be pulled into batch 1 if desired.

## Per-issue detail

| # | Subsystem | Status | Effort | Depends | SharedRewrite | Fix approach |
|---|-----------|--------|--------|---------|---------------|--------------|
| 15 | Roslyn | partially-confirmed | trivial | - | - | Change line 27 in /Users/davehillier/repos/prim/tests/Prim.Tests.Roslyn/Prim.Tests.Roslyn.csproj from `ReferenceOutputAssembly="true"` to `ReferenceOutputAssembly="false"`. This single-line change aligns the test project |
| 16 | Tests | confirmed | small | - | - | Add three new test methods (or extend existing ones) that explicitly call the generated _Continuable variants (CountToTen_Continuable(), WhileCounter_Continuable()) instead of the hand-written methods. Implement a full s |
| 17 | Tests | confirmed | medium | - | - | Add a new test that: (1) loads the rewritten assembly into the CLR via Assembly.LoadFrom() or Assembly.Load(bytes); (2) uses reflection (Type.GetMethod, MethodInfo.Invoke) to execute the rewritten loop method; (3) verifi |
| 18 | Runtime | confirmed | trivial | - | - | Delete src/Prim.Runtime/ScriptScheduler.cs and tests/Prim.Tests.Unit/ScriptSchedulerTests.cs entirely. The preemption semantics described in the whitepaper §5 (instruction counting and safepoint polling) are correctly im |
| 19 | Docs | confirmed | small | - | - | Edit README.md to clarify: (1) Move the Quick Start example to a "Manual Pattern (Currently Supported)" section to reflect the only proven working path; (2) Add a new "Automatic Transformation (Work in Progress)" section |
| 20 | Cecil | confirmed | large | - | - | The switch-based dispatch model requires all jump targets to be in a single flat scope. Two viable paths: (1) Flatten control flow by converting all structured blocks (if/try/loop) into a state machine at IL generation t |
| 21 | Roslyn | confirmed | large | - | 22 | Transform sub-expression yields during Roslyn code generation: (1) detect when a Yield() invocation is NOT at statement level; (2) extract it into a temporary variable before the yield check; (3) use StackSimulator to de |
| 22 | Roslyn | confirmed | large | - | - | Establish a single source of truth for yield-point ID assignment: (1) Move ID assignment logic to a shared utility class that both Roslyn/ContinuationGenerator and Cecil/MethodTransformer use; (2) Both pipelines must cou |
| 23 | Roslyn | stale-already-fixed | trivial | - | - | No fix needed - issue is already resolved. The fix was implemented in commit bf39e7a which added exception filters to all generated catch clauses. These filters explicitly exclude SuspendException from being caught by us |
| 24 | Roslyn | confirmed | medium | - | - | Add a bool flag to BodyGenerationContext (line 411) to track _inFinallyBlock. Update GenerateTryStatement (line 630) to set context._inFinallyBlock=true before processing finally statements, reset to false after. In Gene |
| 25 | Roslyn | confirmed | medium | - | 26,27,28,29 | Thread the Compilation parameter through GenerateTransformedMethods, GenerateTransformedMethod, and GetLocalVariables. Use SemanticModel from compilation to resolve var types via SemanticModel.GetTypeInfo(VariableDeclara |
| 26 | Roslyn | confirmed | medium | - | 25,27,28,29 | Enhance ContinuationGenerator to preserve method signatures: (1) Extend GenerateTransformedMethod and GenerateStaticTransformedMethod to extract and emit async modifier and generic type-parameter list with constraints fr |
| 27 | Roslyn | confirmed | medium | - | - | Modify GetLocalVariables to recursively walk all nested blocks (BlockSyntax, IfStatement, WhileStatement, ForStatement, TryStatement, CatchClause, etc.) using DescendantNodes() or custom traversal, collecting ALL LocalDe |
| 28 | Analysis | confirmed | medium | - | - | Distinguish foreach state-machine loops from generic backward branches in YieldPointIdentifier by analyzing IL pattern: foreach typically has a getEnumerator call, a branch to MoveNext, and a conditional branch. Place th |
| 29 | Roslyn | partially-confirmed | small | - | - | Extend GetLocalVariables (lines 834-858) to recursively collect UsingStatementSyntax and UsingDeclarationSyntax resource variables. Then add a case in GenerateStatement (after line 483) to handle UsingStatementSyntax: tr |
| 30 | Cecil | confirmed | medium | - | - | Modify InjectYieldPointChecks() to check yp.StackState.Depth before each yield point. If depth > 0, insert spill instructions (stloc temporaries in reverse order) before the yield check. Create spill temps as new locals  |
| 31 | Cecil | confirmed | medium | - | - | The switch statement and its jump targets must both reside within the same protection boundary. Two approaches: (1) Move the switch into the try region by placing it after `skipRestoreLabel` and adjusting try boundaries, |
| 32 | Cecil | confirmed | small | - | - | Change line 631 from jumpTargets[i] = yieldPoints[i].Instruction to jumpTargets[i] = yieldPoints[i].Instruction.Next. This directs the switch statement to resume at the instruction immediately after the branch, rather th |
| 33 | Cecil | confirmed | large | 35 | 30,34,38 | Replace the naive linear Simulate() with a proper worklist algorithm: (1) initialize entry block with empty stack, queue it; (2) for each block from worklist, compute successor stack states by simulating instructions; (3 |
| 34 | Cecil | confirmed | small | - | - | Two fixes: (1) After line 81 OptimizeMacros(), explicitly recompute MaxStackSize by iterating through instructions and tracking stack depth, or use Cecil's built-in stack depth calculation. (2) In AddLocal method (line 1 |
| 35 | Cecil | confirmed | medium | - | - | Move the CalculateInstructionCosts call to occur AFTER all yield point check injections, or alternatively refactor to track instruction identity instead of offsets. A safer approach: calculate costs using instruction ind |
| 36 | Analysis | confirmed | small | - | - | Add a private helper method to YieldPointIdentifier that checks if an instruction offset falls within any exception handler region (try block, handler block, or filter block). Call this helper from FindYieldPoints() to f |
| 37 | Cecil | confirmed | small | - | - | Replace hard-coded constants with explicit tracking of synthetic local indices. Track each synthetic local (e.g., contextLocalIndex, frameLocalIndex, stateLocalIndex, exLocalIndex, recordLocalIndex) as VariableDefinition |
| 38 | Cecil | confirmed | medium | - | - | Add explicit checks for ByReferenceType (via `local.VariableType is ByReferenceType`) before attempting to box locals; reject or explicitly skip byref/pinned/pointer locals with a validation error during transform. Also  |
| 39 | Serialization | partially-confirmed | medium | - | - | First, add integration test that creates a custom type, puts one instance in two different slots (or YieldedValue + Slot), serializes, deserializes, and verifies ReferenceEquals(restored[0], restored[1]) == true. If test |
| 40 | Serialization | confirmed | medium | - | - | Add explicit type information to both serializers. Two approaches: (1) Modify DTOs to include a parallel type-name array alongside object[] Slots (e.g., add `string[] SlotTypeNames` to JsonHostFrameRecordDto and HostFram |
| 41 | Serialization | partially-confirmed | medium | - | - | Create a custom MessagePack resolver that whitelist-checks types BEFORE instantiation (by wrapping or replacing ContractlessStandardResolver with a RestrictedResolver that consults ContinuationValidator's allowed types w |
| 42 | Serialization | confirmed | medium | - | - | Convert all four recursive methods to iterative stack-based traversal (one DTO conversion per frame using a Queue or Stack). Alternatively, add a depth counter parameter to each recursive method with an early throw if de |
| 43 | Core-Security | confirmed | large | - | - | 1) Replace lower-bound slot check at ContinuationValidator.cs:244 with exact match (actualSlotCount == expectedSlotCount). 2) Validate ALL slots against declared types (not just live slots); remove liveSlots guard at lin |
| 44 | Core-Security | confirmed | medium | - | - | Migrate from 32-bit FNV-1a to a collision-resistant identifier. Options: (1) Widen to 64-bit hash (uint64) by combining two FNV passes or using a different algorithm like MurmurHash3; (2) Append a manifest: store the ful |
| 45 | Serialization | partially-confirmed | small | - | - | Remove ObjectGraphTracker class entirely from ObjectGraphTracker.cs (lines 12-101) and delete the unused test cases in SerializationTests.cs (lines 94-127). The class provides no value—both active serializers (JSON.NET w |
| 46 | Runtime | stale-already-fixed | trivial | - | - | No fix needed. The problematic code referenced in the issue (Floyd's tortoise/hare with slow/fast cursors) does not exist in the current codebase. The current GetRootFrame implementation is straightforward and correct fo |
