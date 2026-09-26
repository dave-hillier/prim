# Serializable Continuations via Bytecode Transformation

**Dave Hillier**

January 2026 (revised September 2026)

---

## Abstract

We present techniques for implementing serializable continuations on JIT-compiled managed runtimes that do not expose their call stacks. The core contribution is a bytecode transformation that injects state capture and restoration logic, using exception-based unwinding to traverse the stack during suspension. Unlike interpreter-based approaches (Espresso/Truffle) that can materialize frames directly, or native continuation instructions (WasmFX) that provide runtime support, our approach works on stock runtimes without modification.

We describe the transformation in detail, including the exception-based capture pattern, how to structure the restore dispatch so the rewritten code remains verifiable, yield point placement strategies for guaranteed preemption of untrusted code, and a security model for validating serialized continuation state before resumption. We also describe a simpler source-level variant based on replay, and its tradeoffs. The techniques were originally developed for Second Life's Mono integration (2007-2008) and ran in production for years; this paper documents the approach, reports on Prim, an open-source reimplementation for modern .NET, and relates the work to parallel developments in the field.

---

## 1. Introduction

The ability to suspend a computation, capture its state, and later resume—potentially on a different machine—has applications in migration, fault tolerance [22], and sandboxed execution. While continuations are well-understood theoretically [1, 2, 13], practical implementations on mainstream runtimes remain challenging.

Most continuation implementations produce in-memory representations: opaque references that exist only within a single process. WebAssembly's stack switching proposal (WasmFX) [3] and Project Loom's virtual threads [4] both fall into this category. For migration or persistence, the continuation must be *serializable*—convertible to bytes that can be stored or transmitted.

GraalVM's Espresso [5] achieves serializable continuations for Java, but relies on Truffle's ability to materialize interpreter frames as heap objects. This paper addresses a different setting: achieving the same capability on a runtime whose JIT-compiled frames cannot be introspected, and which we cannot modify.

The key insight is that while we cannot *read* the stack, we can *transform* the code to make stack state explicit. By injecting capture and restore logic at compile time or post-compilation, we create programs that can externalize their own execution state on demand.

### 1.1 Contributions

1. **Exception-based stack traversal for capture**: A pattern where suspension throws an exception, and each frame's catch block captures its state into a linked list before rethrowing. This achieves lazy capture (only pay cost when suspending) with localized logic (each method handles its own state).

2. **Verifiable bytecode transformation for JIT-compiled runtimes**: A transformation that injects yield checks, capture catch blocks, and restore dispatch into compiled code, structured so that the output passes IL verification, enabling serializable continuations without runtime modification.

3. **Yield point placement for guaranteed preemption**: A systematic strategy (backward jumps + instruction counting + external calls) ensuring untrusted code cannot monopolize execution.

4. **Security model for untrusted serialized state**: Validation of deserialized continuations including method identity verification, yield point bounds checking, slot count consistency, and type compatibility.

### 1.2 Context

These techniques were developed for Linden Lab's Second Life (2007-2008) [14], where user scripts—actor-like programs [21] attached to objects in the virtual world—needed to migrate transparently between simulator processes. The system ran in production for years, executing scripts for the whole grid. Around the same time and independently, Stadler et al. at JKU Linz and Sun Microsystems developed lazy continuations for the JVM [6]. This parallel evolution suggests the patterns described here are fundamental rather than incidental.

---

## 2. Problem Statement

### 2.1 The Opaque Stack Problem

Managed runtimes like the CLR and JVM do not expose their call stacks programmatically. You cannot ask "what are the current local variables in the caller's frame?" The stack is an implementation detail managed by the runtime and JIT compiler.

This is problematic for continuation capture. A continuation must include:

- The program counter (where to resume)
- Local variables for each frame
- The evaluation stack (pending intermediate values)
- The call chain (which methods to re-enter on resume)

Interpreter-based systems (Truffle, many scripting languages) have direct access to this information because they manage execution explicitly. JIT-compiled code on the CLR or JVM does not.

### 2.2 Why Not CPS or State Machines?

Two standard approaches exist for adding continuations to languages without native support:

**Continuation-Passing Style (CPS)** transforms every function to take an explicit continuation argument [7]. This makes continuation capture trivial but changes calling conventions globally, complicating interop and often causing significant code bloat.

**State machine transformation**, used by C#'s async/await [8], converts methods into classes where locals become fields and the PC becomes a state variable. This works well for compiler-integrated patterns but requires language-level support and is typically restricted to specific constructs (async methods).

We want a more surgical approach: transform only what's necessary to enable capture, preserve normal calling conventions, and work at the bytecode level without compiler cooperation. (As §4.2 shows, the restore dispatch ends up borrowing the *shape* of a state machine within each method, but locals stay on the stack and calling conventions are unchanged.)

---

## 3. Exception-Based Stack Traversal

The core technique is using exceptions to traverse the stack during suspension. When we want to capture:

1. Throw a special `SuspendException` from the innermost frame
2. Each frame has a catch block that captures its state, then rethrows
3. The exception carries a growing chain of frame records
4. At the stack base, we catch the exception and extract the complete state

This has several desirable properties:

**Lazy capture**: We only execute capture logic when actually suspending. Normal execution just checks a flag at yield points.

**Localized logic**: Each method's catch block knows only about its own locals. No global coordinator needs to understand all possible frame shapes.

**Correct unwinding order**: The exception mechanism naturally visits frames from innermost to outermost, building the chain in the right order for later restoration.

### 3.1 The Frame Record Chain

Each captured frame becomes a record in a linked list:

```
HostFrameRecord:
  - MethodToken: identifies which method
  - YieldPointId: where within the method (for dispatch on restore)
  - Slots: array of captured values (locals + eval stack items)
  - Caller: link to the next record in the chain
```

The complete continuation state is the head of this chain. Serialization walks the chain and encodes each record; deserialization rebuilds it.

Because each frame *prepends* its record as the exception passes outward (§3.2), the head of the finished chain is the outermost frame, and each record links to the frame it called. Despite its name, `Caller` therefore points inward, toward the innermost frame. This is exactly the order restoration needs (§6.1), so no reversal is required.

### 3.2 Capture Catch Block

Each transformed method gets a try/catch wrapping its body:

```csharp
try
{
    // ... original method body with yield points ...
}
catch (SuspendException ex)
{
    var record = new HostFrameRecord
    {
        MethodToken = 0x06000123,  // This method's token
        YieldPointId = ex.YieldPointId,
        Slots = PackSlots(local0, local1, local2)
    };
    record.Caller = ex.FrameChain;
    ex.FrameChain = record;
    throw;
}
```

The `PackSlots` call boxes the method's locals (and any spilled evaluation stack items) into an array. The catch block prepends this frame's record to the chain being carried by the exception, then rethrows to continue unwinding.

Note that `ex.YieldPointId` is only correct for the frame that threw. Every outer frame suspended at a *call* to the inner method, and must record the yield point for that call site instead (§4.5).

### 3.3 Avoiding Catch-All Interference

User code may have its own exception handlers. A `catch (Exception)` would intercept `SuspendException`, and a `finally` block would run during unwinding as if the method were exiting. Options:

1. **Throw a non-`Exception` object**: CIL permits throwing any object, not just `System.Exception` subclasses. This does not reliably escape user handlers, however. The runtime wraps such objects in `RuntimeWrappedException` for assemblies marked `WrapNonExceptionThrows` (the C# compiler's default), so `catch (Exception)` still sees them, and a bare `catch` catches everything.

2. **Rethrow filtering**: Transform user catch clauses so they ignore `SuspendException`. In C#, this is an exception filter: `catch (Exception e) when (!(e is SuspendException))`. Filters run before unwinding, so the user's handler never executes and the exception continues outward untouched.

3. **Forbid yield points inside protected regions**: If no yield point is placed inside a `try`, `catch`, `filter` or `finally` region, the method's own handlers can never observe its own suspension.

Prim's source generator uses option 2. Its bytecode rewriter uses option 3, which is simpler but more restrictive (§10.2). Neither fully solves the `finally` problem for *callers*: if a transformed method suspends while its caller is inside a `try`/`finally`, the caller's `finally` runs during unwinding. LSL has no exception handling, so the original Second Life implementation never had to deal with any of this.

---

## 4. Bytecode Transformation

The transformation injects three structures into each method:

1. **Yield point checks**: Test whether suspension is requested
2. **Capture catch block**: Catch `SuspendException` and record frame state
3. **Restore prologue and dispatch**: On resumption, restore state and jump to the right location

### 4.1 Yield Point Injection

At each yield point, inject a check:

```csharp
if (context.YieldRequested)
{
    context.HandleYieldPoint(yieldPointId);
}
```

`HandleYieldPoint` throws `SuspendException` carrying the yield point ID. The ID is a small integer identifying this specific yield point within the method.

Ideally the flag test is inlined, so the fast path is a field load and a conditional branch. Prim currently emits the simpler form below and moves the flag test inside `HandleYieldPoint`, relying on the JIT to inline the call:

```
call       ScriptContext::EnsureCurrent()      // thread-static lookup
ldc.i4     <yieldPointId>
callvirt   ScriptContext::HandleYieldPoint(int32)
```

The context is held in a thread-static field, so each check also pays for a thread-static access. Hoisting that lookup into a local at method entry is a straightforward optimization.

### 4.2 Restore Prologue and Dispatch

At method entry, check whether we are restoring rather than starting fresh. The naive design puts the whole restore block, including a `switch` that jumps to each yield point label, before the `try`:

```csharp
// NOT VALID: the gotos branch from outside the try into it
if (context.IsRestoring && context.FrameChain?.MethodToken == 0x06000123)
{
    // ... restore locals ...
    switch (frame.YieldPointId)
    {
        case 0: goto YIELD_0;   // YIELD_0 is inside the try below
        case 1: goto YIELD_1;
    }
}
try { ... YIELD_0: ... YIELD_1: ... }
catch (SuspendException ex) { ... }
```

This does not work. ECMA-335 only allows a protected region to be entered at its first instruction [12]; a branch from outside into the middle of a `try` is invalid IL. (C# forbids the equivalent `goto` for the same reason.) The fix is to split the restore logic in two, following the shape the C# compiler uses for async and iterator state machines:

```csharp
// Prologue: OUTSIDE the try. Restores state and records where to resume.
var context = ScriptContext.EnsureCurrent();
int state = 0;
if (context.IsRestoring && context.FrameChain?.MethodToken == 0x06000123)
{
    var frame = context.FrameChain;
    context.FrameChain = frame.Caller;
    UnpackSlots(frame.Slots, out local0, out local1, out local2);
    if (context.FrameChain == null)
        context.IsRestoring = false;
    state = frame.YieldPointId + 1;
}

try
{
    // Dispatch: the FIRST instruction of the try, so every jump is intra-region.
    switch (state)
    {
        case 1: goto RESUME_0;
        case 2: goto RESUME_1;
    }
    // ... original body; state == 0 falls through to a normal entry ...
}
catch (SuspendException ex) { /* capture, as in §3.2 */ }
```

Entering the `try` at its first instruction is always legal, and every branch from the dispatch `switch` to a resume label stays within the same region.

The same rule constrains the rest of the rewrite. A `ret` is not allowed inside a protected region either, so each original `ret` becomes a store to a return-value local followed by `leave` to a single return placed after the handler.

### 4.3 Analysis Requirements

The transformation requires static analysis of each method [15, 16]:

**Control flow graph**: Identify basic blocks, branch targets, and exception handler regions. Yield points are placed at loop back-edges (branches to earlier addresses) and, optionally, at calls.

**Stack simulation**: Track the evaluation stack depth and types at each instruction. This is a forward dataflow analysis over the CFG—an abstract interpretation [17] of the IL—computed with a worklist so that stack states merge correctly at join points and handler entries seed the stack with the caught exception. A single linear pass over the instruction list is not enough: it gives wrong answers after unconditional branches and at merge points.

**Liveness analysis** (optional): Determine which locals are live at each yield point. Dead locals need not be captured, reducing serialized state size.

The analysis must track instructions by identity, not by offset. Offsets become stale as soon as code is inserted.

### 4.4 Handling the Evaluation Stack

CIL is stack-based. At a yield point, there may be values on the evaluation stack—intermediate results of expressions. These must be captured too. CIL also requires the evaluation stack to be empty when control enters a protected region or a handler, so the stack must be drained before the yield check can throw.

If analysis determines stack depth > 0 at a yield point, the transformation inserts spill code:

```
// Before yield point, stack has: [value1, value2]
stloc      $spill1    // Pop to temporary
stloc      $spill0    // Pop to temporary
// ... yield check (may throw with an empty stack) ...
RESUME_N:             // Restore dispatch lands here
ldloc      $spill0    // Reload the stack
ldloc      $spill1
// Continue with stack: [value1, value2]
```

The spill temporaries are captured and restored like any other local. Their types come from the stack simulation. Managed pointers (`ref` locals, byref stack items) and pinned locals cannot be boxed into a slot, so a yield point where one would be live must be rejected.

### 4.5 Where Resumption Lands

The resume label sits *after* the yield check and *before* the instruction the yield point guards. On resume, execution therefore re-executes that instruction exactly once:

- For a **loop back-edge**, the branch is taken and the loop continues.
- For a **call** to another transformed method, the call is made again. The callee finds its own frame at the head of the chain, restores itself, and continues from its own yield point (§6.1).

This is also why each frame must record the ID of its own yield point, not the ID carried in the exception (§3.2). An outer frame suspended at a call site; that call site is what its dispatch must jump back to.

---

## 5. Yield Point Placement

Where to place yield points involves tradeoffs between responsiveness, overhead, and—for untrusted code—security.

### 5.1 The Preemption Guarantee

For sandboxed execution of untrusted code, we need a guarantee: *no execution path can proceed indefinitely without hitting a yield point*. Otherwise, malicious code could monopolize the executor.

Three categories cover all cases:

1. **Backward jumps**: Every loop involves a backward branch. Placing a yield point at each back-edge catches all loops.

2. **Instruction counting**: Straight-line code without loops could still be arbitrarily long (unrolled, generated, etc.). Decrement a counter at yield points; when it reaches zero, force a yield.

3. **External calls**: Calls to runtime APIs could block or take unbounded time. Yield points before/after calls let the scheduler intervene.

Together these bound the work between yield points. Recursion must also be accounted for: a method that calls itself without looping never takes a backward branch, so either calls must be yield points or the host must bound stack depth.

### 5.2 Instruction Counting

The obvious approach for preemption is safepoint polling: check a flag set by an external timer or scheduler thread. However, in Second Life's deployment the timer lived outside the scripts, in native code, and consulting it on every loop iteration meant crossing the managed-unmanaged boundary. That crossing was too expensive to pay at every yield point.

Second Life therefore used both a timer and an instruction counter. The timer set policy: it decided when a script's time slice was over. The counter handled mechanism: it was a cheap check in managed code at each yield point, so the boundary was only crossed when the counter ran out:

```csharp
context.InstructionBudget -= COST;
if (context.InstructionBudget <= 0)
{
    context.HandleYieldPoint(yieldPointId);
}
```

COST is the estimated cost of instructions since the last check. The budget is set by the scheduler before each execution slice; when exhausted, the script yields.

The counter keeps the per-yield-point check entirely in managed code, while the timer remains the authority on scheduling. The cost is predictable: a decrement and comparison at each yield point. The counter also provides natural fairness accounting: scripts that do more work consume more budget.

A host that doesn't face the boundary-crossing cost can use either mechanism alone. Pure polling (§5.3) is simpler, and pure counting gives more precise accounting.

The tradeoff is that instruction counts are approximate. Different instructions have different real costs, and JIT optimization makes static estimates even less accurate. Prim, for instance, charges each yield point the number of IL instructions between it and the previous yield point in program order, which ignores which path was actually taken. But for the goal of preventing runaway scripts, approximate fairness is sufficient.

### 5.3 Safepoint Polling

The minimal yield check is a single flag test:

```csharp
if (context.YieldRequested)
{
    context.HandleYieldPoint(yieldPointId);
}
```

The scheduler sets `YieldRequested` from another thread (or timer callback). The flag must be volatile or use appropriate memory barriers. This is the same mechanism JVMs use to bring threads to a safepoint for garbage collection [9].

---

## 6. Restoration and Stack Rebuilding

Resuming a continuation requires rebuilding the call stack and jumping to the right point in each frame.

### 6.1 The Restoration Process

Given a `ContinuationState` with a frame chain:

```
main -> foo -> bar -> [suspended at yield point 2]
```

Restoration proceeds:

1. Set `context.IsRestoring = true`
2. Set `context.FrameChain = state.StackHead`, which is the outermost frame (`main`)
3. Call the entry point method (`main`)

Restoration consumes frames outermost-first, which is the order the chain was left in by capture (§3.1).

Each method's restore prologue:
- Checks if it's the next frame to restore
- Pops its record from the chain
- Restores its locals
- Dispatches to its resume label (§4.2). For every frame but the innermost, that label is at a call site, so the call is re-executed and the callee restores itself.

```
main's prologue:
  - Restore main's locals
  - Dispatch to the call site of foo(); call foo()

foo's prologue:
  - Restore foo's locals
  - Dispatch to the call site of bar(); call bar()

bar's prologue:
  - Restore bar's locals
  - FrameChain is now empty, so clear IsRestoring
  - Dispatch to RESUME_2
  - Continue execution...
```

The re-executed calls rebuild the stack naturally. The entry point can be supplied by the host, or looked up from the method token of the outermost frame through a registry of entry points.

### 6.2 Return Value Handling

When the innermost frame eventually returns, it returns through the restored call stack normally. The continuation runner receives the final result just as if the computation had never been suspended.

If suspension happens again, the same capture process occurs, producing a new continuation state.

### 6.3 A Source-Level Alternative: Replay

A source-to-source transformation (for example a Roslyn source generator) can't emit the arbitrary jumps of §4.2. C# does not allow `goto` into a nested block, so a resume label inside a loop or an `if` cannot be reached from a dispatch `switch` at the top of the method. The fully general answer is to flatten the method into a state machine, as the C# compiler does for `async`.

Prim's source generator takes a simpler route: **replay**. All locals are hoisted to the top of the method. The restore prologue rehydrates them, and then the *original body runs again from the top*. Loop headers and `if` conditions are re-evaluated over the restored locals, so they take the same path as before, and a nested call resumes because the callee's own prologue finds its frame at the head of the chain.

Replay is correct only when the work performed before the yield point is idempotent. Any observable side effect before the yield—writing to a field, printing, sending a message—happens again on resume. That makes replay a pragmatic choice for demonstrations and for code written with the rule in mind, but not a substitute for the bytecode transformation.

---

## 7. Serialization

The frame chain is a heap data structure that can be serialized straightforwardly.

### 7.1 Structure

```
ContinuationState:
  - Version: format version for compatibility
  - StackHead: first HostFrameRecord
  - YieldedValue: the value passed out at the suspension point, if any

HostFrameRecord:
  - MethodToken: int32
  - YieldPointId: int32
  - Slots: object[]
  - Caller: HostFrameRecord (or null)
```

### 7.2 Object Graph Handling

Slots can contain reference types. Serialization must handle:

- **Circular references**: Object A references B which references A
- **Shared references**: Two slots point to the same object

Standard approaches (reference tracking with integer IDs) work. On serialization, assign each object an ID when first seen; write the ID for subsequent references. On deserialization, maintain an ID-to-object map to reconstruct sharing.

Preserving reference identity is important for correctness—programs may depend on `ReferenceEquals` checks.

### 7.3 Type Fidelity

Slots are typed `object`, so the serializer must record each value's concrete type. General-purpose formats lose it: JSON has only one number type, so an `int` comes back as a `long` and a `float` as a `double`, and a `Guid` or `DateTime` comes back as a string. A restored method that unboxes a slot as `int` then fails. Prim wraps every slot in an envelope that carries a canonical type name, and coerces the value back to exactly that type on deserialization. This also gives the validator (§8) exact types to check.

### 7.4 Format Choices

The original system used a custom binary format for compactness. Modern alternatives:

- **MessagePack**: Compact binary, good library support
- **JSON**: Human-readable, useful for debugging
- **Protocol Buffers**: Schema-based, good for versioning

The choice is engineering, not fundamental. The key requirements are preserving the object graph structure and the exact slot types.

The caller chain is recursive, so a naive serializer recurses once per frame. Deserialization must bound the depth, or a maliciously deep chain overflows the stack before validation can reject it.

---

## 8. Security Model

Deserializing untrusted continuation state is dangerous. As Espresso's documentation warns: "Deserializing a continuation supplied by an attacker will allow a complete takeover" [5].

### 8.1 Attack Surface

A malicious serialized continuation could:

- **Jump to arbitrary code locations**: Invalid yield point IDs could transfer control anywhere
- **Forge local variable values**: Inject values that violate invariants the code assumes
- **Instantiate forbidden types**: Bypass sandbox restrictions by deserializing restricted objects
- **Exhaust resources**: Supply deeply nested frame chains or huge slot arrays

### 8.2 Validation Requirements

Before resuming a deserialized continuation:

**Method identity**: Each frame's method token must correspond to a real method in the permitted assembly set. A compact token such as a 32-bit hash can collide, so the host should keep the full method signature (declaring type, name, parameter types) for each registered token and reject a registration whose token is already taken by a different signature.

**Yield point bounds**: The yield point ID must be within the valid range for that method (0 to N-1 where N is the number of injected yield points).

**Slot count consistency**: The slots array length must exactly match the expected count for that yield point, as recorded in the method's frame descriptor.

**Type compatibility**: Each slot value must be type-compatible with what that slot holds at that yield point. A slot expecting `int` cannot contain a `string`.

**Object type whitelist**: Reference types in slots must be from an allowed set, matched by assembly-qualified name so that a type in another assembly cannot impersonate an allowed one by sharing its full name. No deserializing `System.Diagnostics.Process` in a sandbox.

**Resource limits**: Stack depth and slot counts must be bounded.

Validation after deserialization is not sufficient on its own. By the time a validator runs, the deserializer has already constructed every object in the graph, and constructors or setters can have side effects. The type whitelist must also be enforced *during* deserialization, before an object is instantiated.

### 8.3 Trust Levels

Different deployment scenarios need different validation:

**Controlled compiler** (Second Life model): The host compiles all source code. Only host-generated bytecode exists; only host-serialized continuations exist. Minimal validation needed.

**Arbitrary bytecode, trusted state**: Accept third-party assemblies but only resume self-created continuations. Validate bytecode; trust state.

**Arbitrary bytecode, untrusted state**: Accept everything from untrusted sources. Full validation of both bytecode and state.

---

## 9. Relation to Other Systems

### 9.1 Espresso (GraalVM)

Espresso implements Java on Truffle, an interpreter framework. Truffle provides frame materialization—converting stack frames to heap objects—as a built-in capability [10]. Espresso's continuation API builds on this.

The key difference: Espresso operates *within* an interpreter framework that controls execution. Our approach operates *on* code that will be JIT-compiled by a runtime we don't control. We must anticipate and inject everything at transformation time.

Espresso's `HostFrameRecord` chain is structurally similar to ours. This is convergent design—both solve the same problem (representing captured stack state) and arrive at similar solutions.

### 9.2 WasmFX

WebAssembly's stack switching proposal [3, 19] adds continuation primitives to the bytecode:

- `cont.new`: Create a continuation from a function
- `suspend`: Yield control with a typed tag
- `resume`: Resume a continuation with a handler

WasmFX continuations are first-class but opaque—runtime references, not serializable data. This makes WasmFX suitable for in-process coroutines and effect handlers, but not for migration or persistence.

WasmFX's typed tags (declaring what's yielded and what's expected back) are more structured than our approach. A Prim-style system could adopt similar typing for its API.

### 9.3 Asyncify

Asyncify [11] transforms WebAssembly modules to support async operations via a technique similar to ours: injecting save/restore logic at potential suspension points. The instrumentation increases both code size and run time, and Asyncify lets users restrict it to the functions that can actually unwind to keep that overhead down [11].

Asyncify captures state to WASM linear memory, not a serializable format. It's designed for async interop (calling JS promises from WASM), not persistence.

### 9.4 Project Loom

Loom [4] adds virtual threads to Java. Internally, virtual threads use continuations—when a virtual thread blocks, its continuation is captured and the carrier thread runs other work.

However, Loom's continuations are internal implementation details, not a public API, and they're not serializable. Loom solves efficient concurrency, not state persistence.

---

## 10. Discussion

### 10.1 Overhead

The transformation adds:

- **Code size**: Yield checks, a capture catch block, a restore prologue and dispatch, plus spill code at yield points with a non-empty stack. The increase grows with yield point density and with the number of locals, since the capture block and prologue each touch every captured local.

- **Normal execution**: Yield point checks (flag test + branch). Nanoseconds per check. For tight loops this can be significant; for typical application code it's negligible.

- **Suspension**: Exception throw, catch blocks execute, frame records allocated. Cost grows linearly with stack depth.

- **Restoration**: Method calls down the chain, local restoration, dispatch jumps. Similar to suspension cost.

For the Second Life use case (scripts running mixed workloads, suspending occasionally for migration or preemption), the overhead was acceptable. For tight numerical loops requiring maximum performance, it might not be. Prim includes benchmarks for transformation overhead, suspension and resumption, serialization and validation; we have not yet published figures from them.

### 10.2 Limitations

**No yield inside `finally`, `lock` or filters**: The CLR requires finally blocks to complete, and a filter cannot be left by an exception. Suspending in any of these would violate the runtime's invariants [12]. A `lock` statement compiles to a `try`/`finally`, so it inherits the restriction. Prim's bytecode rewriter is more conservative still: it places no yield points inside any protected region, including `try` bodies. That means a long-running loop inside a `try` block is not preemptible.

**Exception handlers complicate things**: User try/catch blocks interact with the capture mechanism (§3.3). The transformation must ensure `SuspendException` escapes user handlers, and callers' `finally` blocks still run during unwinding.

**Debugger interaction**: Transformed code differs from source. Breakpoints, stepping, and variable inspection may behave unexpectedly. Preserving debug symbols requires extra work.

**Closures and captured variables**: Lambdas that capture locals create compiler-generated classes. These must be handled—either by serializing the closure objects or by special-casing the transformation.

**Byrefs and pinned locals**: Managed pointers cannot be boxed or serialized (§4.4). Methods with `ref` locals live across a yield point cannot be transformed.

### 10.3 Implementation Approaches

Two implementation strategies, each with tradeoffs:

**Source generator (Roslyn)**: Transform C# source during compilation. Transformed code is visible and debuggable. Limited to C#; only works on code you compile, and C#'s scoping rules push it toward either a full state-machine rewrite or replay (§6.3).

**Bytecode rewriter (Cecil [18])**: Transform compiled assemblies. Language-agnostic; can transform third-party code. Harder to debug; more complex implementation.

The original Second Life system used bytecode rewriting (with RAIL, a precursor to Cecil). Prim implements both.

### 10.4 Status of Prim

Prim is an open-source reimplementation of these techniques for .NET, and its two transformers are experimental. The bytecode rewriter implements §4 as described: CFG-based stack simulation, spilling, the verifiable prologue/dispatch split, and optional instruction counting. Its output is checked with `ilverify` and executed in tests. It does not yet place yield points inside protected regions, and it does not yet support suspension across a chain of rewritten methods: every frame's catch block records the yield point ID carried by the exception, which is only correct for the innermost frame (§3.2, §4.5). The source generator uses replay (§6.3), handles nested continuable calls, and filters `SuspendException` out of user catch clauses. JSON serialization preserves reference identity; MessagePack preserves slot types but not shared references.

---

## 11. Conclusion

Serializable continuations on JIT-compiled managed runtimes are achievable through bytecode transformation. The core techniques—exception-based stack traversal, yield point injection, and restore dispatch—are straightforward once understood. Getting the details right, however, requires careful handling of stack simulation, protected-region rules, exception handler interaction, type fidelity, and security validation.

The independent development of similar techniques by Stadler et al. [6], the structural similarity to Espresso's design [5], and the viability demonstrated in Second Life's production deployment suggest these patterns are fundamental solutions to the continuation capture problem on opaque-stack runtimes.

The serialization capability remains a differentiator. WasmFX, Loom, OCaml's effect handlers [20], and other modern continuation systems produce in-memory representations. When persistence or migration is required, bytecode transformation remains a practical approach.

---

## References

[1] Reynolds, J. C. (1993). "The Discoveries of Continuations." *Lisp and Symbolic Computation*, 6(3-4), 233–248.

[2] Appel, A. W. (1992). *Compiling with Continuations*. Cambridge University Press.

[3] WebAssembly Community Group. (2024). "Stack Switching Proposal." https://github.com/WebAssembly/stack-switching

[4] Pressler, R., Bateman, A. (2023). "JEP 444: Virtual Threads." OpenJDK. https://openjdk.org/jeps/444

[5] GraalVM Team. (2024). "Espresso: Java on Truffle – Continuation API." https://www.graalvm.org/reference-manual/espresso/continuations/

[6] Stadler, L., Wimmer, C., Würthinger, T., Mössenböck, H., Rose, J. (2009). "Lazy Continuations for Java Virtual Machines." In *Proceedings of PPPJ 2009*, ACM, 143–152.

[7] Flanagan, C., Sabry, A., Duba, B. F., Felleisen, M. (1993). "The Essence of Compiling with Continuations." In *Proceedings of PLDI 1993*, ACM, 237–247.

[8] Bierman, G., Russo, C., Mainland, G., Meijer, E., Torgersen, M. (2012). "Pause 'n' Play: Formalizing Asynchronous C#." In *Proceedings of ECOOP 2012*, Springer, 233–257.

[9] Agesen, O. (1998). "GC Points in a Threaded Environment." Technical Report SMLI TR-98-70, Sun Microsystems Laboratories.

[10] Würthinger, T., Wimmer, C., Wöß, A., et al. (2017). "Practical Partial Evaluation for High-Performance Dynamic Language Runtimes." In *Proceedings of PLDI 2017*, ACM, 662–676.

[11] Zakai, A. (2019). "Asyncify: Turn Synchronous to Asynchronous." https://kripken.github.io/blog/wasm/2019/07/16/asyncify.html

[12] ECMA International. (2012). "ECMA-335: Common Language Infrastructure (CLI)." 6th Edition.

[13] Danvy, O., Filinski, A. (1990). "Abstracting Control." In *Proceedings of LISP and Functional Programming*, ACM, 151–160.

[14] Hillier, D. (2015). "Second Life Mono Internals." https://davehillier.net/2015/03/11/second-life-mono-internals/

[15] Aho, A. V., Lam, M. S., Sethi, R., Ullman, J. D. (2006). *Compilers: Principles, Techniques, and Tools* (2nd ed.). Addison-Wesley.

[16] Muchnick, S. S. (1997). *Advanced Compiler Design and Implementation*. Morgan Kaufmann.

[17] Cousot, P., Cousot, R. (1977). "Abstract Interpretation: A Unified Lattice Model for Static Analysis of Programs." In *Proceedings of POPL 1977*, ACM, 238–252.

[18] Evain, J. (2024). "Mono.Cecil: A Library to Generate and Inspect CIL Code." https://github.com/jbevain/cecil

[19] Phipps-Costin, L., Rossberg, A., Guha, A., Leijen, D., Hillerström, D., Sivaramakrishnan, K. C., Pretnar, M., Lindley, S. (2023). "Continuing WebAssembly with Effect Handlers." In *Proceedings of OOPSLA 2023*, ACM.

[20] Sivaramakrishnan, K. C., Dolan, S., White, L., et al. (2021). "Retrofitting Effect Handlers onto OCaml." In *Proceedings of PLDI 2021*, ACM, 206–221.

[21] Hewitt, C., Bishop, P., Steiger, R. (1973). "A Universal Modular ACTOR Formalism for Artificial Intelligence." In *Proceedings of IJCAI 1973*, 235–245.

[22] Elnozahy, E. N., Alvisi, L., Wang, Y., Johnson, D. B. (2002). "A Survey of Rollback-Recovery Protocols in Message-Passing Systems." *ACM Computing Surveys*, 34(3), 375–408.
