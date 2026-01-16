# Serializable Continuations via Bytecode Transformation

**Dave Hillier**

January 2026

---

## Abstract

We present techniques for implementing serializable continuations on JIT-compiled managed runtimes that do not expose their call stacks. The core contribution is a bytecode transformation that injects state capture and restoration logic, using exception-based unwinding to traverse the stack during suspension. Unlike interpreter-based approaches (Espresso/Truffle) that can introspect frames directly, or native continuation instructions (WasmFX) that provide runtime support, our approach works on stock runtimes without modification.

We describe the transformation in detail, including the exception-based capture pattern, yield point placement strategies for guaranteed preemption of untrusted code, and a security model for validating serialized continuation state before resumption. The techniques were originally developed for Second Life's Mono integration (2007-2008) and ran in production for years; this paper documents the approach and relates it to parallel developments in the field.

---

## 1. Introduction

The ability to suspend a computation, capture its state, and later resume—potentially on a different machine—has applications in migration, fault tolerance, and sandboxed execution. While continuations are well-understood theoretically [1, 2], practical implementations on mainstream runtimes remain challenging.

Most continuation implementations produce in-memory representations: opaque references that exist only within a single process. WebAssembly's stack switching proposal (WasmFX) [3] and Project Loom's virtual threads [4] both fall into this category. For migration or persistence, the continuation must be *serializable*—convertible to bytes that can be stored or transmitted.

GraalVM's Espresso [5] achieves serializable continuations for Java, but relies on Truffle's interpreter infrastructure to access frame state. This paper addresses a harder problem: achieving the same capability on a JIT-compiled runtime where the stack is opaque and frames cannot be introspected.

The key insight is that while we cannot *read* the stack, we can *transform* the code to make stack state explicit. By injecting capture and restore logic at compile time or post-compilation, we create programs that can externalize their own execution state on demand.

### 1.1 Contributions

1. **Exception-based stack traversal for capture**: A pattern where suspension throws an exception, and each frame's catch block captures its state into a linked list before rethrowing. This achieves lazy capture (only pay cost when suspending) with localized logic (each method handles its own state).

2. **Bytecode transformation for JIT-compiled runtimes**: Detailed transformation that injects yield checks, capture catch blocks, and restore entry blocks into compiled code, enabling serializable continuations without runtime modification.

3. **Yield point placement for guaranteed preemption**: A systematic strategy (backward jumps + instruction counting + external calls) ensuring untrusted code cannot monopolize execution.

4. **Security model for untrusted serialized state**: Validation of deserialized continuations including method token verification, yield point bounds checking, slot count consistency, and type compatibility.

### 1.2 Context

These techniques were developed for Linden Lab's Second Life (2007-2008), where user scripts needed to migrate transparently between simulator processes. The system ran in production handling millions of script executions daily. Concurrently and independently, Oracle Labs developed similar techniques for the JVM [6]. This parallel evolution suggests the patterns described here are fundamental rather than incidental.

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

We want a more surgical approach: transform only what's necessary to enable capture, preserve normal calling conventions, and work at the bytecode level without compiler cooperation.

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
  - Caller: link to the next frame outward
```

The complete continuation state is the head of this chain. Serialization walks the chain and encodes each record; deserialization rebuilds it.

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

The `PackSlots` call serializes the method's locals (and any spilled evaluation stack items) into an array. The catch block prepends this frame's record to the chain being carried by the exception, then rethrows to continue unwinding.

### 3.3 Avoiding Catch-All Interference

User code may have its own catch blocks. A naive `catch (Exception)` would intercept `SuspendException`. Two solutions:

1. **Special exception type**: Make `SuspendException` not derive from `Exception` in the user-visible hierarchy (CLR allows this with some tricks)

2. **Rethrow filtering**: Transform user catch blocks to check for `SuspendException` and rethrow immediately

The original Second Life implementation used approach #1 with a custom exception base.

---

## 4. Bytecode Transformation

The transformation injects three structures into each method:

1. **Yield point checks**: Test whether suspension is requested
2. **Capture catch block**: Catch `SuspendException` and record frame state
3. **Restore entry block**: On resumption, restore state and jump to the right location

### 4.1 Yield Point Injection

At each yield point, inject a check:

```csharp
if (context.YieldRequested)
{
    context.HandleYieldPoint(yieldPointId);
}
```

`HandleYieldPoint` examines the request, and if suspension should proceed, throws `SuspendException` with the yield point ID. The ID is a small integer identifying this specific yield point within the method.

In CIL, this becomes roughly:

```
ldsfld     ScriptContext.Current
ldfld      YieldRequested
brfalse.s  CONTINUE
ldc.i4     <yieldPointId>
call       ScriptContext.HandleYieldPoint
CONTINUE:
```

The overhead during normal execution is a field load and conditional branch—typically a few nanoseconds.

### 4.2 Restore Entry Block

At method entry, check if we're restoring rather than starting fresh:

```csharp
if (context.IsRestoring && context.FrameChain?.MethodToken == 0x06000123)
{
    var frame = context.FrameChain;
    context.FrameChain = frame.Caller;

    UnpackSlots(frame.Slots, out local0, out local1, out local2);

    if (context.FrameChain == null)
        context.IsRestoring = false;

    switch (frame.YieldPointId)
    {
        case 0: goto YIELD_0;
        case 1: goto YIELD_1;
        case 2: goto YIELD_2;
    }
}
// Normal entry continues here
```

The dispatch table jumps to the appropriate yield point label. Locals have been restored, so execution continues as if it had never been suspended.

### 4.3 Analysis Requirements

The transformation requires static analysis of each method:

**Control flow graph**: Identify basic blocks and branch targets. Yield points are placed at loop back-edges (branches to earlier addresses) and before/after calls.

**Stack simulation**: Track the evaluation stack depth and types at each instruction. If a yield point has non-empty stack, inject spill code to save stack items to temporaries before the yield check.

**Liveness analysis** (optional): Determine which locals are live at each yield point. Dead locals need not be captured, reducing serialized state size.

### 4.4 Handling the Evaluation Stack

CIL is stack-based. At a yield point, there may be values on the evaluation stack—intermediate results of expressions. These must be captured too.

If analysis determines stack depth > 0 at a yield point, the transformation inserts spill code:

```
// Before yield point, stack has: [value1, value2]
stloc      $spill1    // Pop to temporary
stloc      $spill0    // Pop to temporary
// ... yield check ...
// After YIELD_N label (on restore):
ldloc      $spill0    // Restore stack
ldloc      $spill1
// Continue with stack: [value1, value2]
```

The spill temporaries are captured/restored like any other local.

---

## 5. Yield Point Placement

Where to place yield points involves tradeoffs between responsiveness, overhead, and—for untrusted code—security.

### 5.1 The Preemption Guarantee

For sandboxed execution of untrusted code, we need a guarantee: *no execution path can proceed indefinitely without hitting a yield point*. Otherwise, malicious code could monopolize the executor.

Three categories cover all cases:

1. **Backward jumps**: Every loop involves a backward branch. Placing a yield point at each back-edge catches all loops.

2. **Instruction counting**: Straight-line code without loops could still be arbitrarily long (unrolled, generated, etc.). Decrement a counter at yield points; when it reaches zero, force a yield.

3. **External calls**: Calls to runtime APIs could block or take unbounded time. Yield points before/after calls let the scheduler intervene.

The combination guarantees bounded execution between yield points.

### 5.2 Instruction Counting

The obvious approach for preemption is safepoint polling: check a flag set by an external timer or scheduler thread. However, this requires crossing the managed-unmanaged boundary—the timer callback runs in native code and must communicate with managed code. In Second Life's deployment, this boundary crossing proved expensive enough that an alternative was needed.

Instruction counting moves the preemption decision entirely into managed code:

```csharp
context.InstructionBudget -= COST;
if (context.InstructionBudget <= 0)
{
    context.HandleYieldPoint(yieldPointId);
}
```

COST is the estimated cost of instructions since the last check. The budget is set by the scheduler before each execution slice; when exhausted, the script yields.

This approach has several advantages. No timer thread or native callback is needed—preemption is entirely cooperative and stays in managed code. The cost is predictable: a decrement and comparison at each yield point. And it provides natural fairness accounting: scripts that do more work consume more budget.

The tradeoff is that instruction counts are approximate. Different instructions have different real costs, and JIT optimization makes static estimates even less accurate. But for the goal of preventing runaway scripts, approximate fairness is sufficient.

### 5.3 Safepoint Polling

The minimal yield check is a single flag test:

```csharp
if (context.YieldRequested)
{
    context.HandleYieldPoint(yieldPointId);
}
```

The scheduler sets `YieldRequested` from another thread (or timer callback). The flag must be volatile or use appropriate memory barriers. This is the same mechanism JVMs use for safepoint polling [9].

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
2. Set `context.FrameChain = state.StackHead`
3. Call the entry point method (`main`)

Each method's restore block:
- Checks if it's the next frame to restore
- Pops its record from the chain
- Restores its locals
- Either calls the next method (if more frames) or jumps to its yield point (if innermost)

```
main's restore block:
  - Restore main's locals
  - Call foo()  // foo's restore block will handle the rest

foo's restore block:
  - Restore foo's locals
  - Call bar()  // bar's restore block will handle the rest

bar's restore block:
  - Restore bar's locals
  - FrameChain is now null, so clear IsRestoring
  - Jump to YIELD_2
  - Continue execution...
```

The recursive calling rebuilds the stack naturally.

### 6.2 Return Value Handling

When the innermost frame eventually returns, it returns through the restored call stack normally. The continuation runner receives the final result just as if the computation had never been suspended.

If suspension happens again, the same capture process occurs, producing a new continuation state.

---

## 7. Serialization

The frame chain is a heap data structure that can be serialized straightforwardly.

### 7.1 Structure

```
ContinuationState:
  - Version: format version for compatibility
  - StackHead: first HostFrameRecord

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

### 7.3 Format Choices

The original system used a custom binary format for compactness. Modern alternatives:

- **MessagePack**: Compact binary, good library support
- **JSON**: Human-readable, useful for debugging
- **Protocol Buffers**: Schema-based, good for versioning

The choice is engineering, not fundamental. The key requirement is preserving the object graph structure.

---

## 8. Security Model

Deserializing untrusted continuation state is dangerous. As Espresso's documentation warns: "Deserializing a continuation supplied by an attacker will allow a complete takeover" [5].

### 8.1 Attack Surface

A malicious serialized continuation could:

- **Jump to arbitrary code locations**: Invalid yield point IDs could transfer control anywhere
- **Forge local variable values**: Inject values that violate invariants the code assumes
- **Instantiate forbidden types**: Bypass sandbox restrictions by deserializing restricted objects

### 8.2 Validation Requirements

Before resuming a deserialized continuation:

**Method token validation**: Each frame's method token must correspond to a real method in the permitted assembly set.

**Yield point bounds**: The yield point ID must be within the valid range for that method (0 to N-1 where N is the number of injected yield points).

**Slot count consistency**: The slots array length must match the expected count for that yield point, as recorded in the method's frame descriptor.

**Type compatibility**: Each slot value must be type-compatible with what that slot holds at that yield point. A slot expecting `int` cannot contain a `string`.

**Object type whitelist**: Reference types in slots must be from an allowed set. No deserializing `System.Diagnostics.Process` in a sandbox.

### 8.3 Trust Levels

Different deployment scenarios need different validation:

**Controlled compiler** (Second Life model): The host compiles all source code. Only host-generated bytecode exists; only host-serialized continuations exist. Minimal validation needed.

**Arbitrary bytecode, trusted state**: Accept third-party assemblies but only resume self-created continuations. Validate bytecode; trust state.

**Arbitrary bytecode, untrusted state**: Accept everything from untrusted sources. Full validation of both bytecode and state.

---

## 9. Relation to Other Systems

### 9.1 Espresso (GraalVM)

Espresso implements Java on Truffle, an interpreter framework. Truffle provides frame materialization—converting stack frames to heap objects—as a built-in capability [10]. Espresso's continuation API builds on this.

The key difference: Espresso operates *within* an interpreter that controls execution. Our approach operates *on* code that will be JIT-compiled by a runtime we don't control. We must anticipate and inject everything at transformation time.

Espresso's HostFrameRecord chain is structurally similar to ours. This is convergent design—both solve the same problem (representing captured stack state) and arrive at similar solutions.

### 9.2 WasmFX

WebAssembly's stack switching proposal [3] adds continuation primitives to the bytecode:

- `cont.new`: Create a continuation from a function
- `suspend`: Yield control with a typed tag
- `resume`: Resume a continuation with a handler

WasmFX continuations are first-class but opaque—runtime references, not serializable data. This makes WasmFX suitable for in-process coroutines and effect handlers, but not for migration or persistence.

WasmFX's typed tags (declaring what's yielded and what's expected back) are more structured than our approach. A Prim-style system could adopt similar typing for its API.

### 9.3 Asyncify

Asyncify [11] transforms WebAssembly modules to support async operations via a technique similar to ours: injecting save/restore logic at potential suspension points. Measurements show roughly 30% code size overhead and 30% runtime slowdown [12].

Asyncify captures state to WASM linear memory, not a serializable format. It's designed for async interop (calling JS promises from WASM), not persistence.

### 9.4 Project Loom

Loom [4] adds virtual threads to Java. Internally, virtual threads use continuations—when a virtual thread blocks, its continuation is captured and the carrier thread runs other work.

However, Loom's continuations are internal implementation details, not a public API, and they're not serializable. Loom solves efficient concurrency, not state persistence.

---

## 10. Discussion

### 10.1 Overhead

The transformation adds:

- **Code size**: Yield checks, capture catch blocks, restore entry blocks. Roughly 20-40% increase depending on method complexity and yield point density.

- **Normal execution**: Yield point checks (flag test + branch). Nanoseconds per check. For tight loops this can be significant; for typical application code it's negligible.

- **Suspension**: Exception throw, catch blocks execute, frame records allocated. Microseconds to low milliseconds depending on stack depth.

- **Restoration**: Method calls down the chain, local restoration, dispatch jumps. Similar to suspension cost.

For the Second Life use case (scripts running mixed workloads, suspending occasionally for migration or preemption), the overhead was acceptable. For tight numerical loops requiring maximum performance, it might not be.

### 10.2 Limitations

**No yield inside finally**: The CLR requires finally blocks to complete. Suspending mid-finally would violate invariants. Yield points inside finally blocks must be forbidden.

**Exception handlers complicate things**: User try/catch blocks interact with the capture mechanism. The transformation must ensure `SuspendException` escapes user handlers.

**Debugger interaction**: Transformed code differs from source. Breakpoints, stepping, and variable inspection may behave unexpectedly. Preserving debug symbols requires extra work.

**Closures and captured variables**: Lambdas that capture locals create compiler-generated classes. These must be handled—either by serializing the closure objects or by special-casing the transformation.

### 10.3 Implementation Approaches

Two implementation strategies, each with tradeoffs:

**Source generator (Roslyn)**: Transform C# source during compilation. Transformed code is visible and debuggable. Limited to C#; only works on code you compile.

**Bytecode rewriter (Cecil)**: Transform compiled assemblies. Language-agnostic; can transform third-party code. Harder to debug; more complex implementation.

The original Second Life system used bytecode rewriting (with RAIL, a precursor to Cecil). A modern implementation might offer both.

---

## 11. Conclusion

Serializable continuations on JIT-compiled managed runtimes are achievable through bytecode transformation. The core techniques—exception-based stack traversal, yield point injection, restore dispatch tables—are straightforward once understood, but getting the details right requires careful handling of stack simulation, exception handler interaction, and security validation.

The independent development of similar techniques at Oracle Labs [6], the structural similarity to Espresso's design [5], and the viability demonstrated in Second Life's production deployment suggest these patterns are fundamental solutions to the continuation capture problem on opaque-stack runtimes.

The serialization capability remains a differentiator. WasmFX, Loom, OCaml effects, and other modern continuation systems produce in-memory representations. When persistence or migration is required, bytecode transformation remains a practical approach.

---

## References

[1] Reynolds, J. C. (1993). "The Discoveries of Continuations." *Lisp and Symbolic Computation*, 6(3-4), 233–248.

[2] Appel, A. W. (1992). *Compiling with Continuations*. Cambridge University Press.

[3] WebAssembly Community Group. (2024). "Stack Switching Proposal." https://github.com/WebAssembly/stack-switching

[4] Pressler, R. (2024). "JEP 444: Virtual Threads." OpenJDK. https://openjdk.org/jeps/444

[5] GraalVM Team. (2024). "Espresso: Java on Truffle – Continuation API." https://www.graalvm.org/reference-manual/espresso/continuations/

[6] Stadler, L., Wimmer, C., Würthinger, T., Mössenböck, H., Rose, J. (2009). "Lazy Continuations for Java Virtual Machines." In *Proceedings of PPPJ 2009*, ACM, 143–152.

[7] Flanagan, C., Sabry, A., Duba, B. F., Felleisen, M. (1993). "The Essence of Compiling with Continuations." In *Proceedings of PLDI 1993*, ACM, 237–247.

[8] Bierman, G., Russo, C., Mainland, G., Meijer, E., Torgersen, M. (2012). "Pause 'n' Play: Formalizing Asynchronous C#." In *Proceedings of ECOOP 2012*, Springer, 233–257.

[9] Agesen, O., Garthwaite, A., Knippel, J., et al. (2000). "An Efficient Meta-Lock for Implementing Ubiquitous Synchronization." In *Proceedings of OOPSLA 2000*, ACM, 207–222.

[10] Würthinger, T., Wimmer, C., Wöß, A., et al. (2017). "Practical Partial Evaluation for High-Performance Dynamic Language Runtimes." In *Proceedings of PLDI 2017*, ACM, 662–676.

[11] Zakai, A. (2019). "Asyncify: Turn Synchronous to Asynchronous." https://kripken.github.io/blog/wasm/2019/07/16/asyncify.html

[12] Jangda, A., Powers, B., Berger, E. D., Guha, A. (2019). "Not So Fast: Analyzing the Performance of WebAssembly vs. Native Code." In *Proceedings of USENIX ATC 2019*, 107–120.

[13] ECMA International. (2012). "ECMA-335: Common Language Infrastructure (CLI)." 6th Edition.

[14] Danvy, O., Filinski, A. (1990). "Abstracting Control." In *Proceedings of LISP and Functional Programming*, ACM, 151–160.

[15] Hillier, D. (2015). "Second Life Mono Internals." https://davehillier.net/2015/03/11/second-life-mono-internals/

[16] Aho, A. V., Lam, M. S., Sethi, R., Ullman, J. D. (2006). *Compilers: Principles, Techniques, and Tools* (2nd ed.). Addison-Wesley.

[17] Muchnick, S. S. (1997). *Advanced Compiler Design and Implementation*. Morgan Kaufmann.

[18] Cousot, P., Cousot, R. (1977). "Abstract Interpretation: A Unified Lattice Model for Static Analysis of Programs." In *Proceedings of POPL 1977*, ACM, 238–252.

[19] Evain, J. (2024). "Mono.Cecil: A Library to Generate and Inspect CIL Code." https://github.com/jbevain/cecil

[20] Lindahl, S., Rossberg, A., Titzer, B., et al. (2023). "Continuing WebAssembly with Effect Handlers." In *Proceedings of OOPSLA 2023*, ACM, Article 234.

[21] Sivaramakrishnan, K. C., Dolan, S., White, L., et al. (2021). "Retrofitting Effect Handlers onto OCaml." In *Proceedings of PLDI 2021*, ACM, 206–221.

[22] Hewitt, C., Bishop, P., Steiger, R. (1973). "A Universal Modular ACTOR Formalism for Artificial Intelligence." In *Proceedings of IJCAI 1973*, 235–245.

[23] Elnozahy, E. N., Alvisi, L., Wang, Y., Johnson, D. B. (2002). "A Survey of Rollback-Recovery Protocols in Message-Passing Systems." *ACM Computing Surveys*, 34(3), 375–408.