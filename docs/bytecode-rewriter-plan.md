# .NET Bytecode Rewriter for Cooperative Threading and Serialization

## Project Overview

A recreation of the bytecode rewriting system originally built for Second Life's Mono integration. This system transforms .NET assemblies to add cooperative multithreading (yield points) and full execution state serialization (stack, locals, program counter), enabling transparent migration of running programs.

**Target:** .NET Standard 2.0 (compatible with .NET 6/7/8/9 and modern Mono)

**Core Library:** Mono.Cecil for bytecode manipulation

---

## Architecture: Separate Components

The system consists of five independent components that can be developed and tested in isolation:

```
┌─────────────────────────────────────────────────────────────────────┐
│                        AssemblyRewriter                             │
│  (Orchestrates the transformation pipeline)                         │
└─────────────────────────────────────────────────────────────────────┘
         │              │                │              │
         ▼              ▼                ▼              ▼
┌──────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐
│ Analyzer     │ │ YieldPoint  │ │ Serializer  │ │ Sandbox     │
│              │ │ Injector    │ │ Generator   │ │ Enforcer    │
│ - CFG build  │ │             │ │             │ │             │
│ - Stack sim  │ │ - Counters  │ │ - Save code │ │ - Restrict  │
│ - Local map  │ │ - Hooks     │ │ - Restore   │ │   opcodes   │
└──────────────┘ └─────────────┘ └─────────────┘ └─────────────┘
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

3. **Local Variable Analysis**
   - Map local variables and their types
   - Track liveness to optimize serialization
   - Handle compiler-generated locals

### Data Structures

```csharp
public class MethodAnalysis
{
    public List<BasicBlock> Blocks { get; }
    public Dictionary<int, StackState> StackAtOffset { get; }
    public Dictionary<int, LocalInfo[]> LiveLocalsAtOffset { get; }
    public List<int> LoopBackEdges { get; }
    public List<int> YieldPointCandidates { get; }
}

public class BasicBlock
{
    public int StartOffset { get; }
    public int EndOffset { get; }
    public List<Instruction> Instructions { get; }
    public List<BasicBlock> Successors { get; }
    public List<BasicBlock> Predecessors { get; }
    public bool IsLoopHeader { get; }
}

public class StackState
{
    public int Depth { get; }
    public TypeReference[] Types { get; }
}
```

### Implementation Notes

- Use Cecil's `MethodBody.Instructions` for raw bytecode
- Build CFG by scanning for branch instructions and their targets
- Stack simulation must handle all CIL opcodes (use ECMA-335 spec)
- Consider using existing libraries like `Mono.Cecil.Cil.ILProcessor` helpers

---

## Component 2: Yield Point Injector

**Purpose:** Insert cooperative yield points that allow the runtime to preempt execution.

### Yield Point Strategy

From your original design, yield points should be inserted at:

1. **Loop back-edges** - Prevents infinite loops from blocking
2. **Method entry** - Allows preemption on any call
3. **After expensive operations** - Optional, instruction counting
4. **Before/after calls** - Captures clean stack states

### Instruction Counting Approach

```csharp
// Injected at yield points:
// 1. Decrement instruction counter
// 2. Check if <= 0
// 3. If so, call yield handler

ldsfld int32 ScriptContext::instructionBudget
ldc.i4 <cost>
sub
dup
stsfld int32 ScriptContext::instructionBudget
ldc.i4.0
bgt.s CONTINUE
call void ScriptContext::CheckYield()
CONTINUE:
```

### Yield Point Interface

```csharp
public interface IYieldHandler
{
    /// <summary>
    /// Called at yield points. Returns true to suspend execution.
    /// </summary>
    bool ShouldYield();

    /// <summary>
    /// Called when suspending. Receives serialized state.
    /// </summary>
    void OnSuspend(ExecutionState state);
}
```

### Implementation Considerations

- Yield points must have well-defined stack states (preferably empty or simple)
- Use a state machine index to track which yield point we're at
- Each yield point gets a unique ID for restore targeting

---

## Component 3: State Serializer Generator

**Purpose:** Generate code that captures and serializes execution state (stack, locals, PC).

### State Structure

```csharp
public class ExecutionState
{
    /// <summary>
    /// Per-method frame, forms a linked list for call stack
    /// </summary>
    public StackFrame[] CallStack { get; set; }

    /// <summary>
    /// Heap-allocated objects referenced by the script
    /// </summary>
    public byte[] HeapSnapshot { get; set; }
}

public class StackFrame
{
    public int MethodToken { get; set; }
    public int YieldPointId { get; set; }
    public byte[] Locals { get; set; }
    public byte[] EvaluationStack { get; set; }
}
```

### Save Block Generation

At each yield point, inject code to save state:

```
// Pseudocode for generated save block
if (shouldSuspend)
{
    var frame = new StackFrame();
    frame.MethodToken = <token>;
    frame.YieldPointId = <id>;

    // Save locals
    frame.Locals = SerializeLocals(local0, local1, ...);

    // Save evaluation stack (items currently on stack)
    frame.EvaluationStack = SerializeStack(stackItem0, stackItem1, ...);

    // Push frame to call stack
    context.PushFrame(frame);

    // Return special sentinel or throw SuspendException
    throw new SuspendException();
}
```

### Serialization Strategy

For value types and primitives:
- Use `BinaryWriter` / `BinaryReader` or `MemoryPack`/`MessagePack`

For reference types:
- Require types to be serializable (attribute or interface)
- Use graph serialization to handle object references
- Consider using `System.Text.Json` for simplicity or custom binary format for performance

### Key Challenges

1. **Stack items at yield points** - Must save any values on the evaluation stack
2. **Reference identity** - Must preserve object graph structure
3. **Closures and delegates** - Captured variables in lambdas
4. **Exception handlers** - Active try/catch/finally state

---

## Component 4: State Restorer Generator

**Purpose:** Generate code that restores execution state and jumps to the correct yield point.

### Restore Block Structure

At method entry, inject restore logic:

```csharp
// Pseudocode for generated restore block
if (context.IsRestoring)
{
    var frame = context.PopFrame();

    // Restore locals
    DeserializeLocals(frame.Locals, out local0, out local1, ...);

    // Restore evaluation stack
    var stackItems = DeserializeStack(frame.EvaluationStack);

    // Push stack items (in reverse order)
    foreach (var item in stackItems.Reverse())
        Push(item);

    // Jump to yield point
    switch (frame.YieldPointId)
    {
        case 0: goto YIELD_POINT_0;
        case 1: goto YIELD_POINT_1;
        // ...
    }
}
```

### CIL Implementation

The restore block uses:
- `switch` opcode for efficient dispatch
- `br` to jump to yield point labels
- Careful stack manipulation to restore evaluation stack state

```
// CIL pseudocode
ldarg.0  // this or context
call bool Context::get_IsRestoring()
brfalse NORMAL_ENTRY

// Pop frame, restore locals
ldarg.0
call StackFrame Context::PopFrame()
// ... deserialize locals ...

// Switch to yield point
ldloc frameYieldPointId
switch (YIELD_0, YIELD_1, YIELD_2, ...)

NORMAL_ENTRY:
// Original method code starts here

YIELD_0:
// ... restore stack state for yield point 0 ...
// ... continue execution ...
```

### Implementation Challenges

1. **Label management** - Must track original instruction offsets and map to new positions
2. **Branch rewriting** - Original branches must point to new locations
3. **Exception handlers** - Handler offsets must be updated
4. **Verification** - Generated code must pass CLR verification

---

## Component 5: Sandbox Enforcer

**Purpose:** Restrict what untrusted code can do.

### Restrictions to Enforce

1. **Disallowed opcodes:**
   - `calli` (indirect calls)
   - `jmp` (tail jump)
   - `localloc` (stack allocation)
   - Pointer operations (`ldind.*`, `stind.*`, `cpblk`, `initblk`)

2. **Restricted types:**
   - Block `System.Reflection` namespace
   - Block `System.IO` (or whitelist specific APIs)
   - Block `System.Net`
   - Block `System.Threading` (we provide our own model)

3. **Restricted operations:**
   - No P/Invoke
   - No `unsafe` code
   - No finalizers (destructor abuse)

### Implementation Approach

```csharp
public class SandboxValidator
{
    public ValidationResult Validate(AssemblyDefinition assembly)
    {
        var errors = new List<ValidationError>();

        foreach (var type in assembly.MainModule.Types)
        foreach (var method in type.Methods)
        foreach (var instruction in method.Body.Instructions)
        {
            if (IsDisallowedOpcode(instruction.OpCode))
                errors.Add(new ValidationError(method, instruction, "Disallowed opcode"));

            if (IsRestrictedTypeAccess(instruction))
                errors.Add(new ValidationError(method, instruction, "Restricted type"));
        }

        return new ValidationResult(errors);
    }
}
```

### Whitelist vs Blacklist

Prefer whitelist approach:
- Define allowed types/methods explicitly
- Safer against new .NET additions
- More work upfront but more secure

---

## Development Phases

### Phase 1: Foundation (Weeks 1-2)

- [ ] Set up project structure with .NET Standard 2.0 library
- [ ] Add Mono.Cecil dependency
- [ ] Implement basic assembly loading/saving
- [ ] Build CFG construction for simple methods
- [ ] Write stack simulation for basic opcodes

**Deliverable:** Can analyze a simple method and output CFG + stack states

### Phase 2: Yield Points (Weeks 3-4)

- [ ] Implement yield point identification (loop back-edges)
- [ ] Generate instruction counting code
- [ ] Inject yield check calls
- [ ] Create test harness with mock yield handler

**Deliverable:** Can transform assembly to include yield points, verify with test runner

### Phase 3: Serialization (Weeks 5-7)

- [ ] Design serialization format for primitives and value types
- [ ] Implement local variable serialization
- [ ] Implement evaluation stack serialization
- [ ] Handle reference types with graph serialization
- [ ] Generate save blocks at yield points

**Deliverable:** Can save execution state at yield points

### Phase 4: Restoration (Weeks 8-10)

- [ ] Implement restore block injection at method entry
- [ ] Generate switch dispatch for yield points
- [ ] Handle evaluation stack restoration
- [ ] Rewrite branch targets and exception handlers
- [ ] Verify generated code passes CLR verification

**Deliverable:** Can resume from serialized state

### Phase 5: Sandbox (Weeks 11-12)

- [ ] Implement opcode validator
- [ ] Implement type/method whitelist
- [ ] Add configurable restriction profiles
- [ ] Integration testing with malicious code samples

**Deliverable:** Can reject unsafe code

### Phase 6: Integration & Polish (Weeks 13-14)

- [ ] End-to-end integration tests
- [ ] Performance benchmarking
- [ ] Documentation
- [ ] Example applications

---

## Testing Strategy

### Unit Tests

- CFG construction for various control flow patterns
- Stack simulation correctness
- Serialization round-trips for all types
- Individual opcode handling

### Integration Tests

- Simple programs: loops, conditionals, method calls
- Complex scenarios: recursion, exceptions, closures
- Migration tests: save state, modify state, restore, verify behavior
- Stress tests: deep call stacks, large object graphs

### Verification Tests

- PEVerify on transformed assemblies
- Runtime execution comparison (original vs transformed)
- Memory leak detection on serialization

---

## Key References

### Your Original Work
- [Second Life Mono Internals](https://davehillier.net/2015/03/11/second-life-mono-internals/) - Your blog post

### Technical Resources
- [ECMA-335 Standard](https://www.ecma-international.org/publications-and-standards/standards/ecma-335/) - CLI specification
- [Mono.Cecil](https://www.mono-project.com/docs/tools+libraries/libraries/Mono.Cecil/) - Bytecode manipulation library
- [Cecil GitHub](https://github.com/jbevain/cecil) - Source code

### Similar Projects
- [SquidDev Coroutines](https://squiddev.cc/2023/03/29/coroutines-and-bytecode.html) - Coroutine implementation via bytecode
- [SLua](https://github.com/secondlife/slua) - Second Life's newer Lua implementation with similar goals

---

## Suggested Project Structure

```
BytecodeRewriter/
├── src/
│   ├── BytecodeRewriter.Core/           # Shared types and interfaces
│   │   ├── ExecutionState.cs
│   │   ├── IYieldHandler.cs
│   │   └── ISandboxProfile.cs
│   │
│   ├── BytecodeRewriter.Analyzer/       # CFG and stack analysis
│   │   ├── ControlFlowGraph.cs
│   │   ├── StackSimulator.cs
│   │   ├── LocalVariableAnalyzer.cs
│   │   └── YieldPointIdentifier.cs
│   │
│   ├── BytecodeRewriter.Transform/      # The actual rewriter
│   │   ├── AssemblyRewriter.cs
│   │   ├── YieldPointInjector.cs
│   │   ├── SerializerGenerator.cs
│   │   └── RestorerGenerator.cs
│   │
│   ├── BytecodeRewriter.Sandbox/        # Security validation
│   │   ├── SandboxValidator.cs
│   │   └── Profiles/
│   │       └── StrictProfile.cs
│   │
│   └── BytecodeRewriter.Runtime/        # Runtime support library
│       ├── ScriptContext.cs
│       ├── StateSerializer.cs
│       └── SuspendException.cs
│
├── tests/
│   ├── BytecodeRewriter.Tests.Unit/
│   ├── BytecodeRewriter.Tests.Integration/
│   └── BytecodeRewriter.Tests.Samples/  # Sample scripts to transform
│
└── samples/
    └── SimpleHost/                       # Example host application
```

---

## Questions to Resolve During Implementation

1. **Serialization format:** Binary (fast, compact) vs JSON (debuggable)?
2. **Yield point granularity:** Every instruction vs strategic points only?
3. **Object graph handling:** Full graph serialization vs requiring explicit `[Serializable]`?
4. **Async/await compatibility:** Support modern async patterns or restrict to sync only?
5. **Generic method handling:** Full generics support or restrict to closed types?
6. **Exception state:** Serialize active exception handlers or restrict yield points?

---

## Next Steps

1. Create the solution structure
2. Start with the Analyzer component - it's foundational
3. Build a simple test case (a counting loop) as the first target
4. Iterate through each component with that test case

Ready to begin implementation when you are!
