# Revisiting Continuations: Serializable Stack State on .NET

*In 2007-2008 I worked on embedding Mono into Second Life. User scripts had to be migratable: we needed to suspend a running program, capture its complete state, and resume it elsewhere. I am now rebuilding that system, and this post compares the original design with what has been published since.*

## The Original Problem

In Second Life, scripts were actor-like programs attached to objects. When an object moved from one simulator to another, its scripts had to move too. Migration had to be transparent: the script could not be required to know it had moved. It resumed where it left off, mid-execution.

The legacy LSL VM could do this because it controlled the whole execution model. We wanted to run on Mono for performance, and the CLR does not expose its stack. There is no API that returns the current call stack in a form you can serialize.

Our solution was bytecode rewriting. We took the compiled CIL and transformed it, injecting code that could capture local variables and the program counter at specific yield points, then restore that state on resume. It worked, but it took a lot of detailed reasoning about stack states and branch targets.

## What's Changed

Several current systems implement continuations. They differ in where the mechanism lives and in whether the captured state can be serialized.

**WebAssembly's stack switching proposal** (WasmFX) adds native continuation instructions to the bytecode. Instead of rewriting after compilation, you get `suspend` and `resume` as instructions. Suspension goes through typed control tags: a tag declares the type of the value passed out on suspend and the type expected back on resume, and validation checks that handlers and suspension points agree. The continuations are references inside one engine and cannot be serialized.

**GraalVM's Truffle framework** does not modify bytecode. Truffle languages are written as AST interpreters. The framework provides "safepoints" where execution can be interrupted, and frames can be "materialized" from a virtual (stack-resident) representation to a heap-allocated one that can be captured.

**Espresso** (GraalVM's Java-on-Truffle implementation) has a Continuation API with which you can suspend, serialize to bytes, and resume on a different JVM. That is the capability we built for Second Life, implemented in the interpreter instead of by bytecode rewriting.

The Second Life rewriter was not the first of its kind, and we knew that at the time. It was influenced by two Java mobile-agent systems from 2000, Brakes and JavaGoX, which captured thread state by rewriting bytecode. Related work goes back to 1998 (see [the prior-art survey](../research/prior-art-report.md)). In 2009, Stadler, Wimmer, Würthinger, Mössenböck and Rose published "Lazy Continuations for Java Virtual Machines". That work modifies the HotSpot VM to copy stack frames, so it is a different approach from rewriting the program, and it does not serialize the result.

Many of these systems capture by exception-based unwinding, among them Espresso and, before it, JavaGo (1999) and Pettyjohn et al. on .NET (2005). Instead of injecting save logic at every yield point, you throw a special exception when you want to suspend. As the stack unwinds, each frame's catch block captures its locals into a heap object. These objects form a linked list representing the captured call stack. The capture code runs only when you suspend.

## Design Decisions for the Recreation

These are the main decisions for the new implementation.

### Safepoint Polling vs Instruction Counting

The Second Life system used both. A timer ran outside the scripts to decide when preemption should happen. But we also had instruction counting at yield points - not primarily for scheduling, but to avoid expensive managed/unmanaged boundary crossings. Checking a counter in managed code is cheap. Crossing into native code to check a timer on every loop iteration is not.

The timer controlled policy (when should this script yield?), the counter controlled mechanism (check in managed code, only cross the boundary when needed).

Truffle's safepoints work on the same principle: poll a flag in managed code and avoid expensive transitions. The flag check is a single volatile read, which the JIT handles well.

The new implementation supports both. Which one to use depends on how the host controls scheduling. Polling alone is simpler. Counting gives more precise accounting. Using both avoids the boundary-crossing cost.

### Exception-Based Capture over Explicit Save Blocks

The original design injected explicit save logic at each yield point. When the "should I suspend?" check returned true, you'd execute a block that serialized locals and pushed them onto a frame list.

With exceptions, only one thing is injected at each yield point: the check and throw. The capture logic lives in a catch block at the method level. This means less injected code per yield point, and the capture path only executes when you're actually suspending.

It does mean you need to be careful about exception handlers in the original code. If the user has a catch-all, you need to make sure your `SuspendException` escapes it. The transformation has to rewrite those handlers so that they ignore it.

### Frame Descriptors

We did this in Second Life. A rewriter that generates serialization code knows the frame shape. You're emitting the bytes for each local, so you know what locals exist and what types they have. Truffle has a `FrameDescriptor` class for this. Any system that generates serialization code has the same information.

Liveness analysis is one thing to add. If a local is dead at a yield point - it won't be read again before being overwritten - you don't need to serialize it. I don't recall if we did this originally. It's an optimisation that reduces state size, which matters if you're shipping state over the network for migration.

### State Validation Depends on Your Trust Model

The Espresso documentation warns that deserializing an attacker-supplied continuation "will allow a complete takeover of the JVM." That applies when you accept arbitrary bytecode and arbitrary serialized state. Second Life accepted neither.

In Second Life, users didn't upload bytecode - they submitted LSL source code. We controlled the compiler. Even though it was open source, the compilation happened on our servers. This meant we knew exactly what bytecode could exist: only what our compiler could generate. The serialized state could only contain values that our compiler's output could produce.

The attack surface was therefore not arbitrary .NET. It was whatever LSL can express, compiled by our toolchain, which is much smaller. If your compiler doesn't emit `calli` or pointer operations, they can't appear in the bytecode. If your type system doesn't allow certain object graphs, they can't appear in serialized state.

We also used a whitelist of allowed instructions. We controlled the compiler, but we still verified that the bytecode contained only the instructions we expected. If something unexpected appeared, reject it. This catches compiler bugs as well as injected bytecode.

The new implementation has to choose a trust model. There are three:

1. **Controlled compiler** (Second Life model): Only accept bytecode from your own compiler. State validation can be minimal because you trust the provenance.

2. **Arbitrary bytecode, trusted state**: Accept any .NET assembly, but only resume continuations you created. Need bytecode validation but not state validation.

3. **Arbitrary bytecode, untrusted state**: Validate everything: bytecode, serialized state, object types in slots. This is the case Espresso's warning covers.

The right choice depends on the use case. For a Second Life-style system, the controlled compiler approach is simpler and sufficient.

## What This Enables

The original use case was script migration. The same mechanism can move long-running computations between machines for load balancing or fault tolerance. It has other uses:

**Durable execution.** Systems like Temporal and Restate record the results of side effects in a log and resume by replaying user code against it, which requires that code to be deterministic. A serialized continuation stores the locals directly, so the code does not have to be deterministic.

**Speculative execution.** Espresso's documentation suggests this: when a computation blocks waiting for a slow RPC, you could suspend, then speculatively resume with multiple guesses for the result. Whichever branch gets the real result continues; the others are discarded.

**Time-travel debugging.** Capture the continuation at points during execution, let the developer "step back" by restoring an earlier state.

## Script Unloading No Longer Needs AppDomains

One challenge in the original Second Life implementation was script unloading. At the time, you couldn't destroy assemblies once loaded into the CLR. When a script had to be removed, because its object was deleted or the script was replaced, we could not unload it.

The solution was to suspend the script's actor and let it sit. But you'd accumulate dead scripts over time, wasting memory. I ended up implementing something akin to generational garbage collection using AppDomains: scripts would be loaded into AppDomains by "generation", and when a generation had enough dead scripts, we'd migrate the live ones to a new generation and unload the entire old AppDomain.

This is no longer necessary. Since .NET Core 3.0, `AssemblyLoadContext` supports collectible assemblies. You create a collectible context, load scripts into it, and when you want to unload, call `Unload()`. The GC will collect the assemblies once all references are released. This replaces the AppDomain generations.

One restriction applies: if anything outside the context holds a reference to types inside it, unloading does not complete. The host API has to be designed so that such references are not kept.

## The Work Ahead

I'm planning two implementations that share the same core concepts:

**Roslyn-based** (compile-time): A source generator that transforms C# code during compilation. This is useful for demonstration and debugging, because you can read and step through the generated code. Limited to C# code you're compiling yourself.

**Cecil-based** (post-compile): Bytecode rewriting using Mono.Cecil. It can transform any .NET assembly regardless of source language. More complex to implement and harder to debug, but more generally applicable.

The original Second Life implementation used RAIL (Runtime Assembly Instrumentation Library), a bytecode manipulation library from the University of Coimbra that predated Cecil becoming the standard. It works the same way: load the assembly, modify the IL, save it back. Cecil is its current equivalent.

I'm not trying to support every possible .NET program. Some constraints are acceptable, such as not yielding inside a `finally` block.

Most of the remaining work is in the details: keeping branch targets correct after injecting code, handling generics, and preserving debug information where possible.

---

*This is part of a series on recreating the Second Life Mono continuation system. Previous posts covered [User Scripting in Second Life](https://davehillier.net/2015/03/10/user-scripting-in-second-life/) and [Second Life Mono Internals](https://davehillier.net/2015/03/11/second-life-mono-internals/).*
