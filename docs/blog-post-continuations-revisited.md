# Revisiting Continuations: Serializable Stack State on .NET

*A long time ago, I worked on embedding Mono into Second Life. The core challenge was making user scripts migratable - you needed to be able to suspend a running program, capture its complete state, and resume it elsewhere. Recently I've been thinking about recreating that system, and it's been interesting to see how the field has evolved.*

## The Original Problem

In Second Life, scripts were actor-like programs attached to objects. When an object moved from one simulator to another, its scripts had to move too. The tricky part was transparent migration - the script shouldn't need to know it had moved. It should just resume exactly where it left off, mid-execution.

The legacy LSL VM handled this by having complete control over the execution model. But we wanted to run on Mono for performance, and the CLR doesn't expose its stack. You can't just ask "what's on the call stack right now?" and get something you can serialize.

Our solution was bytecode rewriting. We took the compiled CIL and transformed it, injecting code that could capture local variables and the program counter at specific yield points, then restore that state on resume. It worked, but it was fiddly - lots of careful reasoning about stack states and branch targets.

## What's Changed

Looking at this problem again, I wanted to see what the state of the art looks like. The answer is: the ideas are now mainstream, but with some interesting variations.

**WebAssembly's stack switching proposal** (WasmFX) adds native continuation instructions to the bytecode. Instead of rewriting after compilation, you get `suspend` and `resume` as first-class operations. The clever bit is typed control tags - when you suspend, you declare what type of value you're yielding and what type you expect back. The type system ensures handlers and suspension points agree on the protocol.

**GraalVM's Truffle framework** takes a different approach. Rather than modifying bytecode, Truffle languages are written as AST interpreters. The framework provides "safepoints" where execution can be interrupted, and frames can be "materialized" from a virtual (stack-resident) representation to a heap-allocated one that can be captured.

**Espresso** (GraalVM's Java-on-Truffle implementation) goes further - it has a Continuation API where you can suspend, serialize to bytes, and resume on a completely different JVM. This is essentially what we built for Second Life, but implemented at the interpreter level rather than through bytecode rewriting.

Interestingly, the timelines overlap. I was working on the Second Life continuation system in 2007-2008. The same Oracle Labs team (Stadler, Wimmer, Würthinger) published "Lazy Continuations for Java Virtual Machines" in 2009. We were solving the same problem at the same time - different runtimes, different motivations (production virtual world vs JVM research), but converging on similar ideas. They don't cite Second Life, and I wasn't aware of their work at the time. Parallel evolution.

The pattern I keep seeing is exception-based unwinding for capture. Instead of injecting save logic at every yield point, you throw a special exception when you want to suspend. As the stack unwinds, each frame's catch block captures its locals into a heap object. These objects form a linked list representing the captured call stack. It's elegant - you only pay the capture cost when you actually suspend.

## Design Decisions for the Recreation

Given all this, here's how I'm thinking about the new implementation:

### Safepoint Polling vs Instruction Counting

The Second Life system actually used both. A timer ran outside the scripts to decide when preemption should happen. But we also had instruction counting at yield points - not primarily for scheduling, but to avoid expensive managed/unmanaged boundary crossings. Checking a counter in managed code is cheap. Crossing into native code to check a timer on every loop iteration is not.

The combination made sense: the timer controlled policy (when should this script yield?), the counter controlled mechanism (check in managed code, only cross the boundary when needed).

Truffle's safepoint approach is similar in spirit - poll a flag in managed code, avoid expensive transitions. The flag check is a single volatile read, which the JIT handles well.

For the recreation, I'll likely do something similar. The exact mechanism depends on how the host wants to control scheduling. Pure polling is simpler. Counting gives you more precise accounting. The hybrid approach handles the boundary-crossing cost. Different use cases want different tradeoffs.

### Exception-Based Capture over Explicit Save Blocks

The original design injected explicit save logic at each yield point. When the "should I suspend?" check returned true, you'd execute a block that serialized locals and pushed them onto a frame list.

The exception approach is more elegant. You only inject one thing at yield points: the check and throw. The capture logic lives in a catch block at the method level. This means less injected code per yield point, and the capture path only executes when you're actually suspending.

It does mean you need to be careful about exception handlers in the original code. If the user has a catch-all, you need to make sure your `SuspendException` escapes it. This is solvable but requires some care during transformation.

### Frame Descriptors

This isn't new - we did this in Second Life. When you're generating serialization code, you obviously know the frame shape. You're emitting the bytes for each local, so you know what locals exist and what types they have. Truffle has a formal `FrameDescriptor` abstraction, but the concept is inherent to any system that generates serialization code.

What might be worth revisiting is liveness analysis. If a local is dead at a yield point - it won't be read again before being overwritten - you don't need to serialize it. I don't recall if we did this originally. It's an optimisation that reduces state size, which matters if you're shipping state over the network for migration.

### State Validation Depends on Your Trust Model

The Espresso documentation warns that deserializing an attacker-supplied continuation "will allow a complete takeover of the JVM." That's true if you accept arbitrary bytecode and arbitrary serialized state. But that wasn't the Second Life model.

In Second Life, users didn't upload bytecode - they submitted LSL source code. We controlled the compiler. Even though it was open source, the compilation happened on our servers. This meant we knew exactly what bytecode could exist: only what our compiler could generate. The serialized state could only contain values that our compiler's output could produce.

This is a crucial distinction. The attack surface wasn't "arbitrary .NET" - it was "whatever LSL can express, compiled by our toolchain." That's a much smaller surface. If your compiler doesn't emit `calli` or pointer operations, they can't appear in the bytecode. If your type system doesn't allow certain object graphs, they can't appear in serialized state.

We also used a whitelist of allowed instructions. Even though we controlled the compiler, defence in depth made sense - verify that the bytecode only contains instructions we expected. If something unexpected appeared, reject it. This catches bugs in the compiler as well as any hypothetical attack that somehow injected bytecode.

For the recreation, I need to decide what trust model I'm building for. Options:

1. **Controlled compiler** (Second Life model): Only accept bytecode from your own compiler. State validation can be minimal because you trust the provenance.

2. **Arbitrary bytecode, trusted state**: Accept any .NET assembly, but only resume continuations you created. Need bytecode validation but not state validation.

3. **Arbitrary bytecode, untrusted state**: The paranoid model. Validate everything - bytecode, serialized state, object types in slots. This is what Espresso warns about.

The right choice depends on the use case. For a Second Life-style system, the controlled compiler approach is simpler and sufficient.

## What This Enables

The original use case was script migration. That's still interesting - you could build a system where long-running computations move between machines for load balancing or fault tolerance.

But there are other applications:

**Durable execution** is the trendy term. Systems like Temporal and Restate solve this by replaying from an event log. That works, but requires your code to be deterministic. Serializable continuations are more general - you can capture arbitrary state, including non-deterministic results.

**Speculative execution** is interesting. Espresso mentions this: when a computation blocks waiting for a slow RPC, you could suspend, then speculatively resume with multiple guesses for the result. Whichever branch gets the real result continues; the others are discarded.

**Time-travel debugging** is another. Capture the continuation at points during execution, let the developer "step back" by restoring an earlier state.

## A Problem That's Now Solved: Script Unloading

One challenge in the original Second Life implementation was script unloading. At the time, you couldn't destroy assemblies once loaded into the CLR. When a script needed to be removed - object deleted, script replaced, whatever - we couldn't actually unload it.

The solution was to suspend the script's actor and let it sit. But you'd accumulate dead scripts over time, wasting memory. I ended up implementing something akin to generational garbage collection using AppDomains: scripts would be loaded into AppDomains by "generation", and when a generation had enough dead scripts, we'd migrate the live ones to a new generation and unload the entire old AppDomain.

This is no longer necessary. Since .NET Core 3.0, `AssemblyLoadContext` supports collectible assemblies. You create a collectible context, load scripts into it, and when you want to unload, call `Unload()`. The GC will collect the assemblies once all references are released. Much cleaner than the AppDomain generation hack.

The main caveat is reference tracking - if anything outside the context holds a reference to types inside it, unloading won't complete. But that's manageable with careful API design.

## The Work Ahead

I'm planning two implementations that share the same core concepts:

**Roslyn-based** (compile-time): A source generator that transforms C# code during compilation. This is useful for demonstration and debugging - you can see exactly what code gets generated, step through it, understand what's happening. Limited to C# code you're compiling yourself.

**Cecil-based** (post-compile): Bytecode rewriting using Mono.Cecil. This is the production path - it can transform any .NET assembly regardless of source language. More complex to implement and harder to debug, but more generally applicable.

The original Second Life implementation used RAIL (Runtime Assembly Instrumentation Library), a bytecode manipulation library from the University of Coimbra that predated Cecil becoming the standard. Same approach - load assembly, modify IL, save it back. Cecil is the modern equivalent.

I'm not trying to support every possible .NET program. Constraints are acceptable - maybe you can't yield inside a finally block, maybe certain constructs need to be avoided. The goal is something useful, not something universal.

The interesting engineering is in the details: getting branch offsets right after injecting code, handling generics, preserving debug information if possible. The concepts are proven - we did this fifteen years ago, and the industry has validated the approach since. The implementation is where the work lives.

More to come as I make progress.

---

*This is part of a series on recreating the Second Life Mono continuation system. Previous posts covered [User Scripting in Second Life](https://davehillier.net/2015/03/10/user-scripting-in-second-life/) and [Second Life Mono Internals](https://davehillier.net/2015/03/11/second-life-mono-internals/).*
