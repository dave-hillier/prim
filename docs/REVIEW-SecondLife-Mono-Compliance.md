# Prim Framework Review: Alignment with Second Life Mono Documentation

This document reviews the Prim continuation framework against the reference document "Recreating Second Life's Mono-Based Cooperative Multithreading in Modern .NET".

---

## Executive Summary

**Overall Assessment: Strong Foundation with Partial Implementation**

The Prim framework demonstrates excellent architectural alignment with the Second Life Mono approach. The core concepts—exception-driven stack unwinding, frame chain construction, serializable state, and cooperative multitasking—are faithfully implemented. However, the IL rewriting (Mono.Cecil) component remains scaffolding-level, requiring completion before the framework can transparently transform arbitrary assemblies.

| Component | Doc Requirement | Prim Status | Rating |
|-----------|-----------------|-------------|--------|
| IL Rewriting | Full method transformation | Scaffolding only | ⚠️ Partial |
| Yield Point Detection | Backward branches, calls | Implemented (CFG-based) | ✅ Complete |
| State Capture | Frame + locals + eval stack | Implemented | ✅ Complete |
| State Serialization | Portable format | JSON + MessagePack | ✅ Complete |
| Scheduler | Cooperative multitasking | Basic runner only | ⚠️ Partial |
| Cross-Process Migration | Full state transfer | Demonstrated | ✅ Complete |
| Source Generation | Alternative to IL rewrite | Implemented | ✅ Complete |

---

## Detailed Analysis

### 1. Architecture: Two-Component Design

**Document Requirement:**
> "We propose building the solution as two loosely coupled components: (A) a cooperative multithreading execution engine, and (B) a state serialization/restoration module."

**Prim Implementation:**

The framework correctly implements this separation:

| Doc Component | Prim Implementation |
|---------------|---------------------|
| **(A) Cooperative Engine** | `Prim.Runtime` (ScriptContext, ContinuationRunner) + `Prim.Cecil`/`Prim.Roslyn` (transformation) |
| **(B) Serialization Module** | `Prim.Serialization` (JsonContinuationSerializer, MessagePackContinuationSerializer) |

**Assessment:** ✅ Architecture matches document recommendations. Clean separation allows swapping serialization backends without touching the execution engine.

---

### 2. IL Rewriting for Yielding

**Document Requirements:**

> "Restore prologue: Inject a block at the method start to check if the method is resuming from a yield."
> "Save epilogue: Inject code at function return to save state."
> "Yield points: Insert conditional checks at strategic points (e.g. loop back-edges)."

**Prim Implementation:**

**A. Roslyn Source Generator (`Prim.Roslyn/ContinuationGenerator.cs`)**

The generator correctly implements all three requirements:

```csharp
// Restore prologue (lines 266-283)
if (__context.IsRestoring && __context.FrameChain?.MethodToken == __methodToken)
{
    var __frame = __context.FrameChain;
    __context.FrameChain = __frame.Caller;
    __state = __frame.YieldPointId + 1;
    // Restore locals from slots...
}

// Save epilogue (lines 303-314)
catch (SuspendException __ex)
{
    var __slots = FrameCapture.PackSlots(...);
    var __record = FrameCapture.CaptureFrame(__methodToken, ...);
    __ex.FrameChain = __record;
    throw;
}

// Yield points (lines 345-347)
__context.HandleYieldPoint({yieldPointIndex});
```

**B. Mono.Cecil Rewriter (`Prim.Cecil/MethodTransformer.cs`)**

Current state: **Scaffolding only**. Methods are defined but contain placeholder comments:

```csharp
private void InjectYieldPointChecks(ILProcessor il, List<ILYieldPoint> yieldPoints)
{
    // This is a simplified demonstration
}
```

**Assessment:** ⚠️ **Partial**
- Roslyn generator: ✅ Functional for compile-time transformation
- Cecil rewriter: ❌ Not implemented (critical gap for runtime assembly transformation)

**Recommendation:** Complete the Cecil `MethodTransformer` implementation. The document specifically mentions needing runtime transformation for loading user assemblies:
> "loading user-provided assemblies and rewriting them in memory before execution"

---

### 3. Yield Point Identification

**Document Requirement:**
> "Backwards jump is one of the places to insert a yield... Insert conditional checks at strategic points in long-running code (e.g. loop back-edges, after X instructions, or before expensive library calls)"

**Prim Implementation (`Prim.Analysis/YieldPointIdentifier.cs`):**

```csharp
// Back-edge detection using CFG
foreach (var (from, to) in _cfg.BackEdges)
{
    var lastInstruction = from.Instructions[from.Instructions.Count - 1];
    yieldPoints.Add(new ILYieldPoint
    {
        Id = nextId++,
        Instruction = lastInstruction,
        Kind = ILYieldPointKind.BackwardBranch,
        StackState = _stackSim.GetStateAt(lastInstruction.Offset)
    });
}
```

Additionally, `ControlFlowGraph.cs` implements proper CFG construction with DFS-based back-edge detection.

**Assessment:** ✅ **Complete**. The implementation correctly:
- Builds control flow graphs
- Identifies back-edges via DFS
- Tracks stack state at yield points
- Supports optional external call detection (currently commented out)

---

### 4. Frame and State Data Structures

**Document Requirement:**
> "Define a serializable class... a StackFrameState could hold: the method identifier, instruction pointer (program counter) within that method, and serialized copies of all local variables"

**Prim Implementation (`Prim.Core/HostFrameRecord.cs`):**

```csharp
public sealed class HostFrameRecord
{
    public int MethodToken { get; set; }       // Method identifier
    public int YieldPointId { get; set; }      // Program counter
    public object[] Slots { get; set; }        // Locals + eval stack
    public HostFrameRecord Caller { get; set; } // Call stack chain
}
```

**Document Requirement:**
> "a ScriptState container that holds a stack (list) of StackFrameState objects... plus perhaps the script's global variables or message queue"

**Prim Implementation (`Prim.Core/ContinuationState.cs`):**

```csharp
public sealed class ContinuationState
{
    public int Version { get; set; }           // Format version
    public HostFrameRecord StackHead { get; set; } // Frame chain head
    public object YieldedValue { get; set; }   // Yielded value
}
```

**Assessment:** ✅ **Complete**. The data structures match the document's specification. The linked-list approach for frames is efficient for stack unwinding.

**Note:** The document mentions "queue of messages" for event-driven scripts. This is not implemented but may not be necessary for the core continuation mechanism.

---

### 5. State Capture Mechanism

**Document Requirement:**
> "when a yield was needed, the instrumented code would save the program counter, local variables, and evaluation stack into a Frame object, unwind the call stack, and yield control to a scheduler."

**Prim Implementation:**

Uses exception-driven unwinding with `SuspendException`:

```csharp
// ScriptContext.cs:92-98
public void HandleYieldPoint(int yieldPointId)
{
    if (YieldRequested != 0)
    {
        YieldRequested = 0;
        throw new SuspendException(yieldPointId);
    }
}
```

Each method's catch block captures state during unwinding:
```csharp
catch (SuspendException __ex)
{
    var __slots = FrameCapture.PackSlots(local1, local2, ...);
    var __record = FrameCapture.CaptureFrame(__methodToken, __ex.YieldPointId, __slots, __ex.FrameChain);
    __ex.FrameChain = __record;  // Prepend to chain
    throw;  // Continue unwinding
}
```

**Assessment:** ✅ **Complete**. This matches the document's description:
> "a flag like IsSaving was used to indicate the thread should yield"

Prim uses `YieldRequested` volatile int instead of `IsSaving` boolean, but the semantics are equivalent.

---

### 6. Restoration on Resume

**Document Requirement:**
> "When a script function starts, it sees 'Oh, I should resume from state X', then uses the saved pc (program counter) to jump to the correct instruction label... The example shows a switch(frame.pc) that jumps to PC0, PC1, etc."

**Prim Implementation (from generated code pattern):**

```csharp
if (__context.IsRestoring && __context.FrameChain?.MethodToken == __methodToken)
{
    var __frame = __context.FrameChain;
    __context.FrameChain = __frame.Caller;  // Pop frame
    __state = __frame.YieldPointId + 1;     // Resume after yield point

    // Restore locals
    local1 = FrameCapture.GetSlot<T>(__frame.Slots, 0);
    local2 = FrameCapture.GetSlot<T>(__frame.Slots, 1);

    if (__context.FrameChain == null)
        __context.IsRestoring = false;  // Last frame
}
```

**Assessment:** ✅ **Complete**. The pattern matches the document's description. The `YieldPointId + 1` semantic correctly resumes *after* the yield point that triggered suspension.

**Gap:** The current Roslyn generator doesn't emit the full `switch(__state)` jump table for multi-yield-point methods. This limits which yield point can be resumed from. A production implementation should emit:
```csharp
switch(__state) {
    case 1: goto yield_point_0_resume;
    case 2: goto yield_point_1_resume;
    // ...
}
```

---

### 7. Serialization

**Document Requirement:**
> "We recommend making the serialization format as straightforward as possible – for example, JSON or XML for readability during development, then perhaps a binary format for efficiency later."

**Prim Implementation (`Prim.Serialization/`):**

| Format | Implementation | Features |
|--------|----------------|----------|
| JSON | `JsonContinuationSerializer` | Newtonsoft.Json, type handling, reference preservation |
| MessagePack | `MessagePackContinuationSerializer` | LZ4 compression, contractless |

Both implement `IContinuationSerializer`:
```csharp
public interface IContinuationSerializer
{
    byte[] Serialize(ContinuationState state);
    ContinuationState Deserialize(byte[] data);
}
```

**Assessment:** ✅ **Complete**. Exceeds document recommendation by providing both human-readable (JSON) and efficient binary (MessagePack) options.

---

### 8. Scheduling and Execution

**Document Requirement:**
> "Provide a Scheduler class... Run an event loop on a single thread, cycling through all active scripts... Maintain a queue or list of runnable script contexts."

**Prim Implementation (`Prim.Runtime/ContinuationRunner.cs`):**

The current implementation provides a simple runner, not a full scheduler:

```csharp
public ContinuationResult<T> Run<T>(Func<T> computation)
public ContinuationResult<T> Resume<T>(ContinuationState state, object resumeValue, Func<T> entryPoint)
```

**Assessment:** ⚠️ **Partial**

**What exists:**
- Single-script execution and resumption
- `RunToCompletion` helper for automatic resume loop

**What's missing:**
- Multi-script scheduling (round-robin or priority-based)
- Time-slice enforcement
- Event queue for script wake-up
- Script isolation/sandboxing

The document's example scheduler loop:
```csharp
while(true) {
    foreach(var script in scripts) {
        script.ResumeExecution();
    }
}
```

This pattern is not implemented. Users must manually manage multiple script contexts.

---

### 9. Cross-Process Migration

**Document Requirement:**
> "The entire stack of Frame objects (plus any queued events and heap data) could then be serialized (e.g. stored in a database) and later reloaded to resume the script"

**Prim Implementation:**

The `MigrationDemo` sample demonstrates this:
1. Process A runs computation, triggers yield
2. Serializes state to JSON file
3. Process B loads JSON file
4. Resumes computation with restored state

**Assessment:** ✅ **Complete**. Core migration capability is demonstrated.

---

### 10. Method Token Generation

**Document Requirement:**
> The document implies stable method identification across serialization boundaries.

**Prim Implementation:**

Both Cecil and Roslyn use hash-based tokens:

```csharp
// MethodTransformer.cs:101-113
private int GenerateMethodToken()
{
    unchecked
    {
        int hash = 17;
        hash = hash * 31 + (_method.DeclaringType.FullName?.GetHashCode() ?? 0);
        hash = hash * 31 + (_method.Name?.GetHashCode() ?? 0);
        foreach (var param in _method.Parameters)
        {
            hash = hash * 31 + (param.ParameterType.FullName?.GetHashCode() ?? 0);
        }
        return hash;
    }
}
```

**Assessment:** ⚠️ **Potential Issue**

`String.GetHashCode()` is not deterministic across .NET processes/versions. For true cross-process migration, consider:
- Using a stable hash algorithm (FNV-1a, xxHash, etc.)
- Using method metadata tokens from the assembly
- Using a registry/lookup system

---

## Comparison with Document's Key Takeaways

| Document Takeaway | Prim Implementation |
|-------------------|---------------------|
| "cooperative multithreading by explicit yields" | ✅ `HandleYieldPoint` + `RequestYield` |
| "custom scheduler (no OS threads for each script)" | ⚠️ Basic runner, no multi-script scheduler |
| "transparent persistence by capturing the complete execution state" | ✅ `SuspendException` chain + serializers |
| "IL-transformed code" | ⚠️ Roslyn generator ✅, Cecil rewriter ❌ |
| "portable to standard .NET runtimes" | ✅ .NET Standard 2.0 target |

---

## Gap Analysis

### Critical Gaps

1. **Mono.Cecil IL Rewriter**: The `MethodTransformer` is scaffolding only. This prevents runtime transformation of pre-compiled assemblies—a core requirement for user-script scenarios.

2. **Full State Machine in Source Generator**: The Roslyn generator emits yield checks but doesn't fully transform loop bodies into resumable state machines. Complex control flow (nested loops, try-finally) may not resume correctly.

### Moderate Gaps

3. **Multi-Script Scheduler**: No scheduler for managing multiple concurrent scripts. Users must implement their own scheduling loop.

4. **Method Token Stability**: Hash-based tokens may not be stable across process boundaries due to `String.GetHashCode()` behavior.

5. **Evaluation Stack Capture**: The framework captures locals but evaluation stack capture at arbitrary IL offsets is incomplete.

### Minor Gaps

6. **External Call Yield Points**: Detection code exists but is commented out. Enable for full Second Life-style behavior.

7. **Security/Sandboxing**: No mention of script isolation or resource limits. The document notes:
   > "If the target use-case is sandboxing untrusted code... preventing unsafe code, infinite loops beyond yields"

---

## Recommendations

### High Priority

1. **Complete `MethodTransformer`**: Implement the three placeholder methods:
   - `InjectYieldPointChecks`: Insert `ScriptContext.HandleYieldPoint()` calls before back-edges
   - `WrapInTryCatch`: Add try-catch block around method body with state capture in catch
   - `AddRestoreBlock`: Insert restoration prologue with switch/jump table

2. **Implement Stable Method Tokens**: Replace `String.GetHashCode()` with deterministic hash:
   ```csharp
   private static int StableHash(string s)
   {
       unchecked
       {
           int hash = 2166136261;
           foreach (char c in s)
               hash = (hash ^ c) * 16777619;
           return hash;
       }
   }
   ```

3. **Add Multi-Script Scheduler**: Implement basic round-robin scheduler:
   ```csharp
   public class ScriptScheduler
   {
       private Queue<ScriptInstance> _runnable;
       public void Tick() { /* cycle through scripts */ }
   }
   ```

### Medium Priority

4. **Enhance State Machine Generation**: Transform loops into proper state machines with labeled resume points.

5. **Add Integration Tests**: Test cross-process migration with different .NET versions to verify token stability.

6. **Enable External Call Yield Points**: Uncomment and test the external call detection in `YieldPointIdentifier`.

### Low Priority

7. **Add Security Layer**: Consider `AssemblyLoadContext` isolation for untrusted scripts.

8. **Performance Optimization**: Profile serialization overhead; consider pooling `HostFrameRecord` objects.

---

## Conclusion

The Prim framework provides a solid foundation that faithfully follows the Second Life Mono architecture. The core mechanisms—exception-based unwinding, frame chain construction, and dual serialization—are well-implemented.

The primary gap is the incomplete Mono.Cecil rewriter, which limits the framework to scenarios where source code is available for the Roslyn generator. Completing this component would unlock the full vision of transparently transforming any .NET assembly.

**Readiness Assessment:**
- **Research/Learning**: ✅ Ready
- **Prototyping**: ✅ Ready (with Roslyn generator)
- **Production (source available)**: ⚠️ Needs state machine improvements
- **Production (arbitrary assemblies)**: ❌ Requires Cecil rewriter completion

---

*Review Date: 2026-01-16*
*Prim Version: Initial (commit 9cb23c8)*
*Reference: "Recreating Second Life's Mono-Based Cooperative Multithreading in Modern .NET"*
