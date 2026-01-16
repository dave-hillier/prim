# .NET Continuation Framework: Cooperative Threading and Serialization

## Project Overview

A recreation of the bytecode rewriting system originally built for Second Life's Mono integration (2007-2008). This system transforms .NET code to add cooperative multithreading (yield points) and full execution state serialization (stack, locals, program counter), enabling transparent migration of running programs.

**Target:** .NET Standard 2.0 (compatible with .NET 6/7/8/9 and modern Mono)

---

## Two Implementation Approaches

This project will have two implementations sharing the same core concepts:

### Roslyn-Based (Compile-Time)

A **source generator** that transforms C# code during compilation.

**Advantages:**
- Generated code is visible and debuggable
- Step through transformations, understand what's happening
- Better error messages tied to source locations
- Natural fit for "controlled compiler" security model

**Limitations:**
- C# only (not F#, VB, or other .NET languages)
- Only works on code you're compiling yourself
- Can't transform third-party assemblies

**Use case:** Demonstration, debugging, learning, and systems where you control the source (like Second Life's LSL compiler).

### Cecil-Based (Post-Compile)

**Bytecode rewriting** using Mono.Cecil - load assembly, modify IL, save it back.

**Advantages:**
- Language-agnostic - works on any .NET assembly
- Can transform third-party code
- Production-ready for "transform anything" scenarios

**Limitations:**
- More complex - dealing with raw IL, branch offsets, exception handlers
- Harder to debug transformed code
- Debug symbols require extra work to preserve

**Use case:** Production systems, multi-language support, transforming existing assemblies.

**Historical note:** The original Second Life implementation used RAIL (Runtime Assembly Instrumentation Library), a bytecode manipulation library from the University of Coimbra that predated Cecil becoming the standard. Same approach - load assembly, modify IL, execute or save. Cecil is the modern equivalent.

---

## State of the Art Context

The Second Life continuation system (2007-2008) and much of this research developed in parallel. The Oracle Labs team published "Lazy Continuations for Java Virtual Machines" in 2009 - different runtimes, different motivations (production virtual world vs JVM research), but converging on similar ideas. Neither cited the other; it was parallel evolution.

| System | Approach | Relationship to Our Work |
|--------|----------|--------------------------|
| **WasmFX** | Native continuation instructions | Tags as typed suspension protocols - cleaner API design |
| **GraalVM Truffle** | Frame materialization + safepoints | Parallel evolution - similar hybrid for yield control |
| **Espresso Continuations** | Exception-based unwinding + HFR chain | Same goal - serializable continuations |
| **Temporal/Restate** | Event sourcing + deterministic replay | Different approach - requires deterministic code |
| **Project Loom** | JVM-level virtual threads | Continuations possible but not yet serializable |

**Our unique value:** Serializable continuations on stock .NET runtime, without runtime modifications. The Second Life system proved this works at production scale for years.

---

## Architecture: Separate Components

```
┌─────────────────────────────────────────────────────────────────────┐
│                        AssemblyRewriter                             │
│  (Orchestrates the transformation pipeline)                         │
└─────────────────────────────────────────────────────────────────────┘
         │              │                │              │
         ▼              ▼                ▼              ▼
┌──────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐
│ Analyzer     │ │ YieldPoint  │ │ State       │ │ Sandbox     │
│              │ │ Injector    │ │ Manager     │ │ Enforcer    │
│ - CFG build  │ │             │ │             │ │             │
│ - Stack sim  │ │ - Safepoint │ │ - Frame     │ │ - Bytecode  │
│ - Frame desc │ │   polling   │ │   capture   │ │   validation│
│ - Live vars  │ │ - Tags      │ │ - Serialize │ │ - State     │
└──────────────┘ └─────────────┘ │ - Restore   │ │   validation│
                                 └─────────────┘ └─────────────┘
```

---

## Component 1: Assembly Analyzer

**Purpose:** Analyze CIL bytecode to understand control flow, stack state, and local variable usage.

### Key Responsibilities

1. **Control Flow Graph (CFG) Construction**
   - Identify basic blocks
   - Map branch targets and exception handlers
   - Detect loops (back-edges) for yield point placement

2. **Stack Simulation**
   - Track stack depth at each instruction
   - Determine stack types at yield points
   - Handle exception handler entry points

3. **Frame Descriptor Generation**
   - Pre-compute the "shape" of each method's frame
   - Slot count, types, and indices for all locals
   - Enables efficient serialization without runtime reflection
   - *(Note: This was part of the original Second Life design - when generating serialization code, you inherently know the frame shape. Truffle later formalised this as `FrameDescriptor`, but the concept is inherent to any system that generates serialization code.)*

4. **Liveness Analysis**
   - Track which locals are live at each yield point
   - Dead locals don't need to be serialized
   - Reduces state size significantly

### Data Structures

```csharp
/// <summary>
/// Pre-computed description of a method's frame layout.
/// Inherent to any system generating serialization code - you know the
/// frame shape because you're emitting the code. Truffle later formalised
/// this as FrameDescriptor.
/// </summary>
public class FrameDescriptor
{
    public int MethodToken { get; }
    public FrameSlot[] Slots { get; }
    public int[] YieldPointIds { get; }

    // For each yield point, which slots are live
    public BitArray[] LiveSlotsAtYieldPoint { get; }
}

public class FrameSlot
{
    public int Index { get; }
    public string Name { get; }  // Debug info if available
    public SlotKind Kind { get; }  // Local, Argument, EvalStack
    public TypeReference Type { get; }
    public bool RequiresSerialization { get; }  // false for constants
}

public class MethodAnalysis
{
    public FrameDescriptor FrameDescriptor { get; }
    public List<BasicBlock> Blocks { get; }
    public Dictionary<int, StackState> StackAtOffset { get; }
    public List<YieldPointInfo> YieldPoints { get; }
}

public class YieldPointInfo
{
    public int Id { get; }
    public int OriginalOffset { get; }
    public StackState StackState { get; }
    public BitArray LiveLocals { get; }
    public YieldPointKind Kind { get; }  // LoopBackEdge, MethodExit, Explicit
}
```

### Implementation Notes

- Use Cecil's `MethodBody.Instructions` for raw bytecode
- Build CFG by scanning for branch instructions and their targets
- Stack simulation must handle all CIL opcodes (use ECMA-335 spec)
- Frame descriptors are computed once at transform time, not runtime

---

## Component 2: Yield Point Injector

**Purpose:** Insert cooperative yield points that allow the runtime to preempt execution.

### Yield Point Strategy

In the original Second Life implementation, yield points were inserted at three locations to guarantee untrusted code could never monopolise execution:

1. **Every backward jump** - Catches all loops (while, for, do-while, goto back)
2. **Instruction counter checks** - Catches long straight-line code without loops
3. **All external calls** - Every whitelisted runtime API call was a potential suspend point

This triple coverage meant a script *could not* do unbounded work - every execution path would either loop (backward jump), exhaust its instruction budget (counter), or call the runtime (external call).

**Suspension was invisible to scripts.** The microthread injector handled all the mechanics. Scripts were actors but didn't have to know that - from the script's perspective, execution simply continued. This was critical for the transparent migration use case: the script shouldn't need to know it had moved.

### Timer + Counter Hybrid

The **instruction counter** wasn't primarily for scheduling - it was to avoid expensive managed/unmanaged boundary crossings. Checking a counter in managed code is cheap. Crossing into native code to check a timer on every loop iteration is not.

The combination worked like this:
- **Timer** (external): Runs outside the scripts, decides *when* preemption should happen
- **Counter** (injected): Checked in managed code, triggers cross-boundary call only when needed

This hybrid approach is conceptually similar to what Truffle later formalised as "safepoint polling" - though both systems were developed around the same time (Second Life 2007-2008, Oracle Labs paper 2009).

### Safepoint Polling vs Instruction Counting

The original design can be simplified for trusted-code scenarios:

```csharp
// Old approach: instruction counting
ldsfld int32 ScriptContext::instructionBudget
ldc.i4 <cost>
sub
dup
stsfld int32 ScriptContext::instructionBudget
ldc.i4.0
bgt.s CONTINUE
call void ScriptContext::CheckYield()
CONTINUE:

// New approach: safepoint polling (simpler, JIT-friendly)
ldsfld int32 ScriptContext::yieldRequested  // volatile read
brfalse.s CONTINUE
call void ScriptContext::HandleYieldPoint(int yieldPointId)
CONTINUE:
```

**Advantages of polling:**
- Single memory read vs arithmetic + write
- JIT can potentially hoist/combine polls
- Cleaner separation: host sets flag, guest checks it
- Matches Truffle's approach (proven at scale)

**The host controls timing:**
```csharp
public class ScriptContext
{
    // Volatile: visible across threads
    public volatile int yieldRequested;

    // Called by host (e.g., on timer, or quota exceeded)
    public void RequestYield() => yieldRequested = 1;

    // Called by injected code at yield points
    public void HandleYieldPoint(int yieldPointId)
    {
        if (yieldRequested != 0)
        {
            yieldRequested = 0;
            throw new SuspendException(yieldPointId);
        }
    }
}
```

### Typed Suspension Tags *(New - inspired by WasmFX)*

For more structured control flow, support explicit suspension with typed tags:

```csharp
/// <summary>
/// A suspension tag defines the protocol for a suspension point.
/// Inspired by WasmFX's typed control tags.
/// </summary>
public class SuspensionTag<TOut, TIn>
{
    public string Name { get; }
}

// Usage in transformed code (conceptual):
public static class Suspend
{
    // Suspend with payload, receive value on resume
    public static TIn Yield<TOut, TIn>(SuspensionTag<TOut, TIn> tag, TOut value);
}

// Example: Generator pattern
public static readonly SuspensionTag<int, Unit> GeneratorYield = new("yield");

// In transformed code:
Suspend.Yield(GeneratorYield, currentValue);  // Suspends, returns Unit on resume
```

This enables:
- Type-safe communication between suspend/resume
- Different handlers for different suspension reasons
- Clear API for language implementers

---

## Component 3: State Manager (Serializer + Restorer)

**Purpose:** Capture, serialize, deserialize, and restore execution state.

### Frame Capture *(Updated - inspired by Espresso)*

Use **exception-based unwinding** with **HostFrameRecord chain**:

```csharp
/// <summary>
/// Captured state of a single stack frame.
/// Inspired by Espresso's HostFrameRecord.
/// </summary>
public class HostFrameRecord
{
    public int MethodToken { get; set; }
    public int YieldPointId { get; set; }
    public object[] Slots { get; set; }  // Locals + eval stack
    public HostFrameRecord Caller { get; set; }  // Linked list
}

/// <summary>
/// Complete captured execution state.
/// </summary>
public class ContinuationState
{
    public HostFrameRecord StackHead { get; set; }
    public int Version { get; set; }  // For compatibility checking
}
```

### Capture Flow (Exception-Based)

```
Normal execution:
    main() → foo() → bar() → [yield point hit, yieldRequested=1]

Unwinding:
    bar(): HandleYieldPoint(3) throws SuspendException
           catch block captures locals → creates HFR for bar
           rethrows

    foo(): catches SuspendException
           captures locals → creates HFR for foo
           links foo.HFR → bar.HFR
           rethrows

    main(): catches SuspendException
            captures locals → creates HFR for main
            links main.HFR → foo.HFR
            returns ContinuationState with StackHead = main.HFR
```

### Generated Catch Block

Each transformed method gets a catch block for `SuspendException`:

```csharp
// Injected at method level
try
{
    // ... original method body with yield points ...
}
catch (SuspendException ex)
{
    // Capture this frame
    var record = new HostFrameRecord
    {
        MethodToken = /* this method's token */,
        YieldPointId = ex.YieldPointId,
        Slots = CaptureSlots(/* locals and stack items */)
    };

    // Link to caller's record (if any)
    record.Caller = ex.FrameChain;
    ex.FrameChain = record;

    throw;  // Continue unwinding
}
```

### Serialization Strategy

**Two-phase serialization** (inspired by Espresso):

1. **Materialization**: Convert live execution to `ContinuationState` (heap objects)
2. **Serialization**: Convert `ContinuationState` to bytes

```csharp
public interface IContinuationSerializer
{
    byte[] Serialize(ContinuationState state);
    ContinuationState Deserialize(byte[] data);
}

// Default: Use MessagePack for speed + compactness
public class MessagePackSerializer : IContinuationSerializer { ... }

// Alternative: JSON for debugging
public class JsonSerializer : IContinuationSerializer { ... }
```

**Object graph handling:**
- Reference types in slots use object graph serialization
- Track object identity to preserve reference equality
- Circular references handled via reference tracking

### Restore Flow

```csharp
public class ContinuationRunner
{
    public object Resume(ContinuationState state, object resumeValue)
    {
        var context = new ScriptContext
        {
            IsRestoring = true,
            FrameChain = state.StackHead,
            ResumeValue = resumeValue
        };

        // Call the entry point method
        // Restore blocks will handle re-winding the stack
        return InvokeEntryPoint(context);
    }
}
```

### Restore Block (at method entry)

```csharp
// Injected at method entry
if (context.IsRestoring && context.FrameChain?.MethodToken == /* this method */)
{
    var frame = context.FrameChain;
    context.FrameChain = frame.Caller;  // Pop frame

    // Restore locals from frame.Slots
    RestoreSlots(frame.Slots, out local0, out local1, ...);

    // If this is the innermost frame, we're done restoring
    if (context.FrameChain == null)
    {
        context.IsRestoring = false;
    }

    // Jump to yield point
    switch (frame.YieldPointId)
    {
        case 0: goto YIELD_0;
        case 1: goto YIELD_1;
        // ...
    }
}
// Normal entry continues here
```

---

## Component 4: Sandbox Enforcer

**Purpose:** Restrict what untrusted code can do. Now includes **state validation**.

### Bytecode Validation (Pre-Transform)

```csharp
public class BytecodeValidator
{
    public ValidationResult Validate(AssemblyDefinition assembly)
    {
        var errors = new List<ValidationError>();

        foreach (var method in assembly.AllMethods())
        {
            ValidateOpcodes(method, errors);
            ValidateTypeReferences(method, errors);
            ValidateMethodCalls(method, errors);
        }

        return new ValidationResult(errors);
    }

    // Disallowed opcodes
    private static readonly HashSet<OpCode> DisallowedOpcodes = new()
    {
        OpCodes.Calli,      // Indirect calls
        OpCodes.Jmp,        // Tail jump
        OpCodes.Localloc,   // Stack allocation
        // Pointer operations...
    };
}
```

### State Validation *(New - critical security)*

**From Espresso docs:** *"Deserializing a continuation supplied by an attacker will allow a complete takeover."*

Before resuming a deserialized continuation, validate:

```csharp
public class StateValidator
{
    public ValidationResult ValidateState(ContinuationState state, AssemblyDefinition assembly)
    {
        var errors = new List<ValidationError>();

        var frame = state.StackHead;
        while (frame != null)
        {
            // 1. Method token must exist in the assembly
            var method = assembly.FindMethod(frame.MethodToken);
            if (method == null)
            {
                errors.Add(new ValidationError($"Unknown method token: {frame.MethodToken}"));
                break;
            }

            // 2. Yield point ID must be valid for this method
            var descriptor = GetFrameDescriptor(method);
            if (frame.YieldPointId < 0 || frame.YieldPointId >= descriptor.YieldPointIds.Length)
            {
                errors.Add(new ValidationError($"Invalid yield point: {frame.YieldPointId}"));
            }

            // 3. Slot count must match expected
            var expectedSlots = CountLiveSlots(descriptor, frame.YieldPointId);
            if (frame.Slots.Length != expectedSlots)
            {
                errors.Add(new ValidationError($"Slot count mismatch: {frame.Slots.Length} vs {expectedSlots}"));
            }

            // 4. Slot types must be compatible
            ValidateSlotTypes(frame, descriptor, errors);

            // 5. Reference types must be in allowed set
            ValidateObjectTypes(frame.Slots, errors);

            frame = frame.Caller;
        }

        return new ValidationResult(errors);
    }
}
```

### Security Levels

```csharp
public enum SecurityLevel
{
    /// <summary>
    /// Only resume self-created continuations (in-memory).
    /// No deserialization attack surface.
    /// </summary>
    TrustedOnly,

    /// <summary>
    /// Resume serialized continuations with full validation.
    /// Safe for persistence/restart scenarios.
    /// </summary>
    ValidatedSerialization,

    /// <summary>
    /// Resume serialized continuations from untrusted sources.
    /// Additional: cryptographic signature verification.
    /// </summary>
    SignedSerialization
}
```

---

## API Design *(New section - inspired by WasmFX)*

### Core Types

```csharp
namespace ContinuationFramework
{
    /// <summary>
    /// A suspended computation that can be resumed.
    /// Inspired by WasmFX's continuation reference type.
    /// </summary>
    public sealed class Continuation<T>
    {
        internal ContinuationState State { get; }

        /// <summary>
        /// Resume the continuation, passing a value to the suspension point.
        /// Returns the final result or throws if suspended again.
        /// </summary>
        public T Resume(object value);

        /// <summary>
        /// Serialize the continuation to bytes for storage/migration.
        /// </summary>
        public byte[] Serialize();

        /// <summary>
        /// Deserialize and validate a continuation.
        /// </summary>
        public static Continuation<T> Deserialize(byte[] data, ValidationOptions options);
    }

    /// <summary>
    /// Result of running a continuable computation.
    /// </summary>
    public abstract class ContinuationResult<T>
    {
        public sealed class Completed : ContinuationResult<T>
        {
            public T Value { get; }
        }

        public sealed class Suspended : ContinuationResult<T>
        {
            public object YieldedValue { get; }
            public Continuation<T> Continuation { get; }
        }
    }

    /// <summary>
    /// Entry point for running transformed code.
    /// </summary>
    public class ContinuationRunner
    {
        public ContinuationResult<T> Run<T>(Action entryPoint);
        public ContinuationResult<T> Run<T>(Continuation<T> continuation, object resumeValue);
    }
}
```

### Usage Example

```csharp
// 1. Transform the assembly (build time or load time)
var rewriter = new AssemblyRewriter();
var transformed = rewriter.Transform(originalAssembly);

// 2. Run with continuation support
var runner = new ContinuationRunner(transformed);
var result = runner.Run<int>(() => MyScript.Main());

switch (result)
{
    case ContinuationResult<int>.Completed c:
        Console.WriteLine($"Completed with: {c.Value}");
        break;

    case ContinuationResult<int>.Suspended s:
        Console.WriteLine($"Suspended with: {s.YieldedValue}");

        // Save for later
        var bytes = s.Continuation.Serialize();
        File.WriteAllBytes("state.bin", bytes);

        // ... later, or on another machine ...

        var restored = Continuation<int>.Deserialize(
            File.ReadAllBytes("state.bin"),
            ValidationOptions.Full);
        var finalResult = runner.Run(restored, resumeValue: 42);
        break;
}
```

---

## Development Phases

Development will start with the Roslyn implementation for clarity and debugging, then port to Cecil for production use.

### Phase 1: Core Types and Runtime (Week 1)

- [ ] Set up solution structure
- [ ] Implement core types: `Continuation<T>`, `ContinuationResult`, `ContinuationState`, `HostFrameRecord`
- [ ] Implement `SuspendException`
- [ ] Implement `ScriptContext` (yield flag, frame chain, restore state)
- [ ] Implement `ContinuationRunner` (basic structure)

**Deliverable:** Core abstractions compile and have tests

### Phase 2: Roslyn - Yield Point Injection (Weeks 2-3)

- [ ] Create source generator project
- [ ] Identify yield points in C# syntax (loop back-edges, method exits)
- [ ] Generate safepoint polling code
- [ ] Transform simple methods with yield checks
- [ ] Test: simple loops can be interrupted

**Deliverable:** Roslyn generator produces code that responds to yield requests

### Phase 3: Roslyn - State Capture (Weeks 4-5)

- [ ] Generate catch blocks for `SuspendException`
- [ ] Generate code to capture locals into `HostFrameRecord`
- [ ] Build frame chain during unwinding
- [ ] Test: can capture state at any yield point

**Deliverable:** Can suspend and see captured state in debugger

### Phase 4: Roslyn - Restoration (Weeks 6-7)

- [ ] Generate restore blocks at method entry
- [ ] Generate switch dispatch to yield points
- [ ] Handle evaluation stack restoration (if needed)
- [ ] Test: can resume from captured state (in-memory)

**Deliverable:** Full suspend/resume cycle works with Roslyn implementation

### Phase 5: Serialization (Week 8)

- [ ] Implement `IContinuationSerializer` with MessagePack
- [ ] Handle object graph serialization (reference tracking)
- [ ] Add JSON serializer for debugging
- [ ] Test: round-trip serialization preserves state

**Deliverable:** Can serialize/deserialize continuation state

### Phase 6: Cecil - Port to Bytecode Rewriting (Weeks 9-11)

- [ ] Add Mono.Cecil dependency
- [ ] Implement CFG construction from IL
- [ ] Implement stack simulation
- [ ] Port yield point injection to IL
- [ ] Port catch block injection to IL
- [ ] Port restore block injection to IL
- [ ] Implement branch fixup after code injection
- [ ] Test: Cecil output matches Roslyn output behavior

**Deliverable:** Cecil implementation passes same tests as Roslyn

### Phase 7: Security and Validation (Week 12)

- [ ] Implement `BytecodeValidator` (instruction whitelist)
- [ ] Implement `StateValidator` (validate deserialized state)
- [ ] Add security levels
- [ ] Test with adversarial inputs

**Deliverable:** Safe to run untrusted code and resume untrusted state

### Phase 8: Polish (Weeks 13-14)

- [ ] End-to-end integration tests (both implementations)
- [ ] Performance benchmarking (Roslyn vs Cecil vs baseline)
- [ ] API documentation
- [ ] Example applications
- [ ] Migration demo (save on machine A, resume on machine B)

---

## Testing Strategy

### Unit Tests

- CFG construction for all control flow patterns
- Stack simulation correctness (all opcodes)
- FrameDescriptor generation accuracy
- Serialization round-trips
- StateValidator catches bad input

### Integration Tests

- Simple programs: loops, conditionals, method calls
- Complex: recursion, exceptions, closures, generics
- Suspend at every yield point, verify state
- Resume and verify execution continues correctly
- Cross-version: serialize with v1, deserialize with v2

### Security Tests

- Malformed method tokens
- Out-of-range yield point IDs
- Wrong slot counts/types
- Injected malicious objects
- Stack overflow via deep frame chains

### Performance Tests

- Overhead of yield point polling
- Serialization size vs original memory
- Serialization/deserialization speed
- Resume latency
- Compare to: Asyncify, native async/await overhead

---

## Test Scenarios

Comprehensive scenarios organised by category. Each scenario should have tests that verify both the Roslyn and Cecil implementations produce identical behavior.

### Basic Suspension & Resume

1. **Empty loop** - `while(true) {}` suspends and resumes
2. **Counter loop** - `for(i=0; i<N; i++)` suspends mid-count, resumes and completes
3. **Method call chain** - `A → B → C`, suspend in C, resume correctly
4. **Recursive function** - Factorial/Fibonacci with suspend at arbitrary depth
5. **Multiple suspend points** - Function that suspends multiple times before completing

### State Preservation

6. **Local variables (primitives)** - int, long, float, double, bool preserved across suspend
7. **Local variables (structs)** - Custom structs and DateTime preserved
8. **Local variables (references)** - Objects, arrays, strings preserved with identity
9. **Method arguments** - All argument types preserved
10. **Evaluation stack items** - Pending computations `a + [suspend] + b`
11. **Nested scopes** - Variables in inner blocks preserved
12. **Object graph** - Circular references maintain identity after resume

### Control Flow

13. **If/else branches** - Suspend in then-branch, resume correctly
14. **Switch statements** - Suspend in case body, resume to correct case
15. **Nested loops** - Suspend in inner loop, resume at correct iteration
16. **Break/continue** - Suspend before break, resume and break correctly
17. **Early return** - Suspend before conditional return, resume and return
18. **Ternary expressions** - Suspend in ternary operand

### Exception Handling

19. **Try block suspend** - Suspend in try, resume in try
20. **Catch block suspend** - Suspend in catch handler, resume in handler
21. **Finally block suspend** - Suspend in finally, resume and complete finally
22. **Nested try/catch** - Suspend in inner try, resume correctly
23. **Exception after resume** - Resume, then throw, verify handler executes
24. **Exception before suspend** - Throw, catch, suspend in handler, resume

### Serialization Round-Trip

25. **In-memory round-trip** - Serialize → deserialize → resume (same process)
26. **Cross-process** - Serialize → write to file → read in new process → resume
27. **Cross-machine** - Serialize → transfer → resume on different machine
28. **Large state** - Many locals, deep call stack, large objects
29. **Binary compatibility** - Serialize with v1, deserialize with v1.1
30. **Format validation** - Corrupted bytes detected and rejected

### Edge Cases

31. **Suspend at method entry** - First instruction is a yield point
32. **Suspend at method exit** - Just before return
33. **Zero iterations** - Loop that never executes body, but has yield check
34. **Very deep stack** - 1000+ frames deep
35. **Very wide frame** - Method with 100+ locals
36. **Generic methods** - Suspend in `Method<T>` with various T
37. **Closures/lambdas** - Captured variables preserved across suspend

### Multi-Suspend Scenarios

38. **Generator pattern** - Multiple `yield return` equivalents
39. **Cooperative multitasking** - Multiple scripts taking turns
40. **Checkpoint/restore** - Suspend at checkpoints, restore from any

### External Call Boundaries

41. **Suspend on runtime call** - Call whitelisted API, suspend during
42. **Resume with return value** - Runtime call returns value, used after resume
43. **Interleaved calls** - Script → runtime → script → suspend

### Negative Cases (Should Fail Gracefully)

44. **Invalid yield point ID** - Reject with clear error
45. **Wrong method token** - Reject with clear error
46. **Type mismatch in slot** - Reject with clear error
47. **Tampered serialization** - Detect and reject
48. **Missing method** - Assembly changed, method gone, clear error

---

## Project Structure

```
ContinuationFramework/
├── src/
│   │
│   │   # ===== SHARED (used by both implementations) =====
│   │
│   ├── ContinuationFramework.Core/
│   │   ├── Continuation.cs
│   │   ├── ContinuationResult.cs
│   │   ├── ContinuationState.cs
│   │   ├── HostFrameRecord.cs
│   │   ├── FrameDescriptor.cs
│   │   └── SuspendException.cs
│   │
│   ├── ContinuationFramework.Runtime/
│   │   ├── ScriptContext.cs
│   │   ├── ContinuationRunner.cs
│   │   └── FrameCapture.cs
│   │
│   ├── ContinuationFramework.Serialization/
│   │   ├── IContinuationSerializer.cs
│   │   ├── MessagePackSerializer.cs
│   │   ├── JsonSerializer.cs
│   │   └── ObjectGraphTracker.cs
│   │
│   ├── ContinuationFramework.Security/
│   │   ├── BytecodeValidator.cs
│   │   ├── StateValidator.cs
│   │   ├── SecurityLevel.cs
│   │   └── SignatureVerifier.cs
│   │
│   │   # ===== ROSLYN IMPLEMENTATION =====
│   │
│   ├── ContinuationFramework.Roslyn/
│   │   ├── ContinuationGenerator.cs        # ISourceGenerator entry point
│   │   ├── SyntaxAnalyzer.cs               # Find yield points in C# syntax
│   │   ├── MethodRewriter.cs               # CSharpSyntaxRewriter for transform
│   │   ├── CatchBlockGenerator.cs          # Generate catch blocks
│   │   ├── RestoreBlockGenerator.cs        # Generate restore logic
│   │   └── DiagnosticDescriptors.cs        # Compiler warnings/errors
│   │
│   │   # ===== CECIL IMPLEMENTATION =====
│   │
│   ├── ContinuationFramework.Cecil/
│   │   ├── AssemblyRewriter.cs             # Entry point
│   │   ├── MethodTransformer.cs            # IL transformation
│   │   ├── YieldPointInjector.cs           # Inject yield checks
│   │   ├── CatchBlockInjector.cs           # Inject catch blocks
│   │   ├── RestoreBlockInjector.cs         # Inject restore logic
│   │   └── BranchFixup.cs                  # Fix branch offsets after injection
│   │
│   └── ContinuationFramework.Analysis/     # Shared analysis (used by Cecil)
│       ├── ControlFlowGraph.cs
│       ├── StackSimulator.cs
│       ├── FrameDescriptorBuilder.cs
│       ├── LivenessAnalyzer.cs
│       └── YieldPointIdentifier.cs
│
├── tests/
│   ├── ContinuationFramework.Tests.Unit/
│   ├── ContinuationFramework.Tests.Integration/
│   ├── ContinuationFramework.Tests.Security/
│   ├── ContinuationFramework.Tests.Roslyn/     # Source generator tests
│   ├── ContinuationFramework.Tests.Cecil/      # IL rewriting tests
│   └── ContinuationFramework.Tests.Samples/    # Sample code to transform
│
└── samples/
    ├── Generator/           # Python-style generator
    ├── AsyncSimulator/      # Simulated async/await
    ├── MigrationDemo/       # Cross-machine state transfer
    └── Sandbox/             # Untrusted code runner
```

---

## Key References

### Your Original Work
- [Second Life Mono Internals](https://davehillier.net/2015/03/11/second-life-mono-internals/)

### Modern Implementations
- [WasmFX Explainer](https://wasmfx.dev/specs/explainer/) - Typed continuations design
- [Espresso Continuation API](https://www.graalvm.org/latest/reference-manual/espresso/continuations/) - Serializable continuations on JVM
- [Truffle Safepoints](https://www.graalvm.org/latest/graalvm-as-a-platform/language-implementation-framework/Safepoint/) - Cooperative yield design
- [Truffle Bytecode DSL](https://github.com/oracle/graal/blob/master/truffle/docs/bytecode_dsl/UserGuide.md) - Continuation + serialization support

### Technical Resources
- [ECMA-335](https://www.ecma-international.org/publications-and-standards/standards/ecma-335/) - CLI specification
- [Mono.Cecil](https://www.mono-project.com/docs/tools+libraries/libraries/Mono.Cecil/) - Bytecode manipulation (modern)
- [RAIL](https://www.researchgate.net/publication/221001208_RAIL_code_instrumentation_for_NET) - Runtime Assembly Instrumentation Library (used in original Second Life implementation)
- [Asyncify](https://kripken.github.io/blog/wasm/2019/07/16/asyncify.html) - Similar bytecode rewriting approach for WASM

### Academic
- [Continuing WebAssembly with Effect Handlers (OOPSLA 2023)](https://dl.acm.org/doi/10.1145/3622814)
- [Wasm/k: Delimited Continuations for WebAssembly](https://arxiv.org/abs/2010.01723)
- [Delimited Continuations](https://okmij.org/ftp/continuations/index.html) - Theory background

---

## Hosting: AssemblyLoadContext and Script Lifecycle

A significant challenge in the original Second Life implementation was **script unloading**. At the time, you couldn't destroy assemblies once loaded into the CLR. The workaround was a generational approach using AppDomains: scripts were loaded by "generation," and when a generation accumulated enough dead scripts, live ones were migrated out and the entire AppDomain was unloaded.

### Modern Solution: Collectible AssemblyLoadContext

Since .NET Core 3.0, `AssemblyLoadContext` supports **collectible assemblies**:

```csharp
// Create a collectible context
var context = new AssemblyLoadContext("ScriptContext", isCollectible: true);

// Load transformed script assembly
var assembly = context.LoadFromAssemblyPath(transformedPath);

// ... run scripts ...

// When done: unload the context
context.Unload();

// GC will collect once all references are released
```

This eliminates the AppDomain generational hack entirely.

### Context Granularity Options

The right granularity depends on the use case:

| Strategy | Memory | Isolation | Complexity |
|----------|--------|-----------|------------|
| One context per script | High | Maximum | Simple lifecycle |
| One context per object | Medium | Per-object | Natural for Second Life model |
| One context per group | Low | Grouped | Batch lifecycle management |
| Shared context | Lowest | Minimal | Manual tracking |

**For a Second Life-style system**, one context per object makes sense: each in-world object could have multiple scripts (assemblies), and they all share the same lifecycle - when the object is deleted, everything goes.

```csharp
public class ScriptHost
{
    // One context per in-world object
    private readonly Dictionary<ObjectId, AssemblyLoadContext> _contexts = new();

    public void LoadScript(ObjectId objectId, byte[] transformedAssembly)
    {
        if (!_contexts.TryGetValue(objectId, out var context))
        {
            context = new AssemblyLoadContext($"Object-{objectId}", isCollectible: true);
            _contexts[objectId] = context;
        }

        // Load into object's context
        using var stream = new MemoryStream(transformedAssembly);
        var assembly = context.LoadFromStream(stream);
        // ... instantiate and run ...
    }

    public void DeleteObject(ObjectId objectId)
    {
        if (_contexts.TryGetValue(objectId, out var context))
        {
            _contexts.Remove(objectId);
            context.Unload();  // GC will clean up
        }
    }
}
```

### Continuations and Context Lifecycle

Continuation serialization is **orthogonal** to context management:

1. **Suspend:** Capture continuation state, serialize to bytes
2. **Unload:** Can unload the AssemblyLoadContext (optional)
3. **Reload:** Create new context, load transformed assembly
4. **Resume:** Deserialize continuation, continue execution

This enables scenarios like:
- **Cold migration:** Suspend, unload, transfer bytes to new machine, load there, resume
- **Persistence:** Suspend, serialize, shutdown, restart later, deserialize, resume
- **Memory reclamation:** Suspend idle scripts, unload contexts, reload on demand

### Caveats

- **Reference tracking:** If anything outside the context holds a reference to types inside it, unloading won't complete. API design must ensure clean boundaries.
- **Type sharing:** Types used in serialization (like `ContinuationState`) should be in a shared assembly that outlives individual script contexts.
- **Debugging:** Collectible assemblies have some debugging limitations - symbol loading may be affected.

---

## Open Questions

1. **Generic methods:** Full open generics support or require closed instantiations?
2. **Exception handlers:** Allow yield inside try/catch/finally or restrict?
3. **Async/await interop:** Can we transform async methods, or must they be avoided?
4. **Debugging:** Can we preserve/generate debug symbols for Cecil-transformed code?
5. **Liveness analysis:** Uncertain if Second Life did this originally. Worth the complexity? Reduces state size for network migration.
6. **Yield point granularity:** Already answered - triple coverage: backward jumps + instruction counter + external calls.

---

## Summary: Original vs Recreation

| Area | Second Life (2007-2008) | This Recreation |
|------|------------------------|-----------------|
| **Library** | RAIL | Roslyn (compile-time) + Cecil (post-compile) |
| **Yield points** | Backward jumps + counter + external calls | Same triple coverage |
| **Yield control** | Timer + instruction counter | Same hybrid approach (or simplified polling) |
| **State capture** | Explicit save blocks | Exception-based unwinding (exploring) |
| **Frame storage** | Generated per-method | Linked HostFrameRecord chain |
| **Suspension visibility** | Invisible to scripts | Same - actors don't know they're actors |
| **Security model** | Controlled compiler + instruction whitelist | Same, with optional state validation |
| **Serialization** | Custom binary | MessagePack / JSON |

**Key insight:** The core ideas from Second Life remain valid and worked at scale in production. Modern implementations (Espresso, Truffle, WasmFX) have converged on similar approaches - parallel evolution rather than one inspiring the other. The recreation benefits from better tooling (Roslyn, Cecil) and can reference these parallel implementations for API design.

---

Ready to begin implementation!
