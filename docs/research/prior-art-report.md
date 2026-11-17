# Prior art for Prim

This report states which of Prim's techniques were published before it, by whom, and what Prim and Second Life add. It summarises the five files in [notes/](notes/), which hold the quotations, citations and a record of how each claim was checked.

Last revised 17 November 2026.

## Summary

None of the four techniques in the [whitepaper](../whitepaper.md) is new.

| Technique | Earliest publication found | Closest prior work on .NET |
|---|---|---|
| Capture by throwing an exception that each frame catches, saves into and rethrows | Fünfrocken 1998; JavaGo 1999 | Pettyjohn et al. 2005 |
| Restore by calling each method again and jumping to a saved index | JavaGo 1999; Brakes 2000 | Pettyjohn et al. 2005 |
| Yield points on loop back-edges | Feeley 1993 | n/a (standard VM practice) |
| Instruction budget injected by bytecode rewriting | J-RAF2, 2004 | none found |
| Checking migrated state on arrival | Farmer, Guttman, Swarup 1996 | none found |
| Checking deserialized frames against verifier types | Espresso, 2024 (documentation and code only) | none found |

What Second Life and Prim add:

1. **Production use on the CLR.** Second Life's rewriter was presented in 2006 and ran in production from 29 August 2008. No earlier production system that serializes continuations by transforming .NET code was found.
2. **Verifiable CIL.** An exception-based rewriter for CIL has to satisfy ECMA-335's rules for protected regions. The earlier Java systems did not face those rules, and Pettyjohn et al. do not discuss verifiability.
3. **Preemption that produces a continuation.** In J-RAF2, running out of budget delays or terminates a thread. In Second Life and Prim it suspends the script into state that can be saved or moved to another machine.
4. **An allow-list on deserialized frames.** Espresso checks that restored frames are well formed but accepts frames for any method. Prim accepts only registered methods and yield points. A published attack on Espresso used well-formed frames of JDK methods.

Second Life and Prim use different capture mechanisms. The public description of Second Life shows each frame saving itself and returning, as in Brakes. Prim throws an exception, as in JavaGo and Pettyjohn et al.

## 1. Groups of prior work

**Mobile-agent thread migration on the JVM, 1998–2002.** Second Life's rewriter descends from this work.

- Fünfrocken (1998): a source preprocessor that inserts `try`/`catch` to save thread state.
- JavaGo (Sekiguchi, Masuhara, Yonezawa, 1999): source-to-source. "Exceptions are thrown recursively until the whole call stack is saved as a chain of State objects." Restores by calling methods again and dispatching with `switch`.
- Brakes (Truyen et al., 2000): bytecode. "Every method saves the current frame in the Context object and returns to the previous method recursively." Restores with a bytecode `goto` to the saved instruction. No exceptions.
- JavaGoX (Sakamoto, Sekiguchi, Yonezawa, 2000): bytecode, with a type system to find the types of locals and operand-stack entries. Its unwind mechanism is not confirmed.
- Sekiguchi, Sakamoto, Yonezawa (2001): the exception technique in general form, for Java and C++.
- Tao (2001, PhD thesis): exception-based, at source and bytecode level.

**Continuations from stack inspection, 2005 onward.**

- Pettyjohn, Clements, Marshall, Krishnamurthi, Felleisen (ICFP 2005): "we show how to use our new technique to copy and reconstitute the stack on MSIL.Net using exception handlers." Each call is wrapped in a handler that saves the frame and rethrows.
- Loitsch (2007): the same technique in JavaScript, described as "an adaption and evolution of the suspension and migration techniques presented in Tao's thesis… and Sekiguchi et al.'s paper".
- Stopify (Baxter et al., PLDI 2018): JavaScript, with exception-based, return-based and shadow-stack strategies.

**Return-based bytecode instrumentation, 2004 onward.** Apache Commons Javaflow started from Brakes code; its `Continuation` is `java.io.Serializable`. RIFE is similar and limited to one frame. Kilim (2008) unwinds by returning and rejected exceptions as slower.

**Continuations in the runtime.** Stadler et al. (2009, HotSpot), Mono.Tasklets (2009), Loom, OCaml 5, WebAssembly stack switching and .NET runtime async. None of these can be serialized. Espresso is the exception: it serializes continuations, because Truffle can copy interpreter frames to the heap.

**Durable execution by replay.** Temporal, Azure Durable Functions, Restate and Golem persist a log of side-effect results and re-run user code against it. They do not serialize a stack, and they require user code to be deterministic.

**Other systems that serialize continuations.** Racket stateless servlets (McCarthy 2009) transform Scheme source and serialize the result. Stackless Python, Pluto and Eris serialize coroutines from inside the interpreter.

Details: [notes/jvm_clr_migration_prior_art.md](notes/jvm_clr_migration_prior_art.md), [notes/stack_inspection_continuations.md](notes/stack_inspection_continuations.md), [notes/modern_runtime_systems.md](notes/modern_runtime_systems.md), [notes/durable_execution_industry.md](notes/durable_execution_industry.md).

## 2. Second Life's mechanism

The public record:

| Date | Event |
|---|---|
| 1 August 2005 | Babbage Linden blogs "Second Life In Mono". |
| 7 June 2006 | Miguel de Icaza: "Second Life has added support to 'migrate' running code across machines." |
| 31 July 2006 | Cory Ondrejka and Jim Purbrick present at the Lang.NET Symposium. |
| 29 August 2008 | Mono goes live on the main grid with server 1.24.3. |
| April 2009 | De Icaza describes "a CIL rewriting engine that injected the state serialization and reincarnation into an existing CIL instructions stream". |
| 11 March 2015 | Dave Hillier publishes "IIRC: Second Life Mono internals". |

Hillier's 2015 post is the only detailed technical description. Its pseudocode restores with `if (IsRestoring) { var frame = PopFrame(); switch(frame.pc) {...} }` and saves with `if (IsYieldDue) { frame.pc=1; frame.locals=...; frame.stack=...; goto Yield; }` followed by `Yield: if (IsSaving) PushFrame(frame); // return normally`. This is a flag-and-return unwind. It contains no exception handlers. The post names Brakes and JavaGoX as influences and RAIL as the rewriting library.

The blog draft in this repository says the same: the original "injected explicit save logic at each yield point", and exceptions are a choice made for the reimplementation.

The whitepaper now describes the two systems separately (§1.2, §3).

Open: whether the shipped system matched the 2015 pseudocode. The simulator source is not public and the post is written from memory.

## 3. Capture and restore

Exception-based capture has six publications before or around Second Life: Fünfrocken 1998, JavaGo 1999, Sekiguchi et al. 2001, Tao 2001, Pettyjohn et al. 2005 (on .NET) and Loitsch 2007. Espresso uses it today and calls its frame record `HostFrameRecord`, the name Prim also uses.

Restore dispatch is equally established: Brakes, Tao, Kilim and Javaflow all re-enter each method and jump to a saved position.

Three trade-offs separate exception-based from return-based capture:

- **Where the capture code is.** With exceptions there is one catch block per method. With returns there is a save sequence at each yield point and a flag check after each call.
- **Cost when not suspending.** A return-based design checks a flag after every call. On the CLR an exception handler costs nothing until something is thrown.
- **Cost of suspending.** Kilim's authors measured exceptions as "more expensive by almost two orders of magnitude" on the JVM in 2008, and noted that a throw clears the operand stack. Prim spills the evaluation stack before each yield check, which removes the second problem. The first has not been measured on a current CLR.

Both designs capture lazily. Stopify's authors say so directly: "Both checked-return and exceptional continuations reify the stack lazily."

**What is specific to Prim.** ECMA-335 allows a protected region to be entered only at its first instruction and forbids `ret` inside one. A CIL rewriter that wraps the method body in a `try` therefore cannot jump from a restore prologue to a label inside the body. Prim puts the prologue outside the `try`, makes a dispatch `switch` the first instruction inside it, and rewrites each `ret` as `leave`. Its output passes `ilverify` in tests. The Java systems had no such rule to satisfy. Pettyjohn et al. worked in C# on a small example and do not discuss verifiability, user `try` blocks or `finally`.

External measurements for comparison:

- Loitsch: instrumentation alone slowed code by 1.1×–4.1× in Firefox 2 and up to 16.9× in Opera 9.
- Stopify: median slowdown between 1.7× and 24× depending on source language and browser.
- Asyncify (return-based, WebAssembly): about 50% larger code on average in the worst case, with slowdown roughly proportional.
- Bouchenak et al.: execution overhead on recursive Fibonacci of about 250% for JavaGo, 190% for Brakes and 90% for JavaGoX.

Prim has benchmarks and has not published results from them.

## 4. Yield points and instruction counting

Placing polls on loop back-edges and in method prologues is standard. Lin et al. (2015): "To avoid unbounded waits, yieldpoints typically occur on loop backedges and on method prologs or epilogs." Feeley (1993) is the early reference on compiler-inserted polling.

J-RAF2 rewrites Java bytecode so that each thread counts executed bytecodes and polls at method entry and exit, loop heads, handler entries and after a maximum path length. When the limit is reached, "the thread may terminate the component… or it may delay itself". The same idea appears in Lua's count hook (2003), Ethereum gas, WebAssembly metering tools, and Wasmtime's fuel.

Wasmtime offers both a deterministic counter (fuel) and a cheaper check driven by wall-clock time (epochs). Second Life's design, as the whitepaper describes it, used a timer to decide when a slice ended and a counter in managed code to avoid calling into native code at every yield point.

**What is specific to Second Life and Prim.** Running out of budget captures a serializable continuation. The script can then be saved or moved to another machine. J-RAF2 and VM safepoints only stop or pause a thread in place.

Open: no public source documents the instruction counter in Second Life's Mono engine. The whitepaper presents it as the author's recollection. A companion post by Hillier says the older LSL2 VM gave each script a number of instructions per slot.

Details: [notes/preemption_and_security.md](notes/preemption_and_security.md), sections 1 and 2.

## 5. Validating deserialized state

Systems that serialize continuations have handled untrusted state in three ways.

- **Load only your own state.** Espresso: "Only resume continuations you persisted yourself!"
- **Sign it.** Racket's `HMAC-SHA1-stuffer` "allows the servlet to certify that stuffed data was truly generated by the servlet". The default stuffer does not sign.
- **Check it.** Farmer, Guttman and Swarup (1996) proposed state appraisal for mobile agents: functions that check invariants of an agent's state when it arrives.

Espresso also checks. Its documentation says that on restore the VM ensures "that resume only happens on `invoke` bytecodes" and "that the recorded frame data is consistent with what was computed by the bytecode verifier". Its `HostFrameRecord.verify` method checks the saved arrays against the frame descriptor and checks that each frame's resume position is an invoke of the next frame's method. The documentation calls these checks minimal and says their purpose is to prevent a VM crash.

A 2025 proof of concept by X1r0z forged a chain of Espresso frame records for two JDK printing classes, with a command array in the locals, and obtained command execution. The forged frames pass Espresso's checks, because each is a valid frame of a real method. Espresso does not limit which methods a continuation may contain.

**What is specific to Prim.** Prim's validator accepts a frame only if its method token and yield point were registered by the host. It also checks exact slot counts, slot types against an allow-list, and stack depth. The method allow-list is the check that stops the Espresso attack. The slot checks correspond to what Espresso already does.

Two qualifications:

- An earlier version of this report said that no precedent for verifier-style validation of deserialized frames had been found and that the Espresso attack manipulated exactly the fields Prim validates. The first statement was wrong: Espresso is a precedent. The second was incomplete: Espresso validates those fields too, and the attack succeeded through the absence of a method allow-list.
- No peer-reviewed paper proposing this kind of frame validation was found. Espresso's is documented only in its repository. Not finding a paper is weak evidence that none exists.

Second Life did not validate state. It compiled all scripts itself and checked the resulting bytecode against a list of allowed instructions, so only its own serializer produced state.

A signature is not a substitute for these checks. It shows who produced the state, not that the state matches the code now loaded. Recompiling a script changes yield point numbers and slot layouts.

Details: [notes/preemption_and_security.md](notes/preemption_and_security.md), section 3.

## 6. State of the whitepaper

The whitepaper was revised in September 2026 to credit this prior work and to describe Second Life and Prim separately. The November 2026 revision made these further changes:

- §1.1 and §8 now say that Espresso checks restored frames and that Prim's addition is the method allow-list.
- §9.3 gives the date WebAssembly stack switching reached phase 3.
- §11 no longer says Espresso was exploited through "exactly the frame fields that §8 validates".
- References gained LNCS volume numbers, page numbers for Feeley 1993, and a DOI for Lin et al. 2015.

Not yet done in the whitepaper:

- Benchmark results (§10.1). A comparison of exception-based and return-based capture on one CLR would test Kilim's cost claim.
- Citations for Javaflow's history, Marshall's 2009 paper, and Henry et al. 2012 on typed unmarshalling.

## 7. Open questions

For the author:

1. Did the shipped Second Life rewriter unwind by flag-and-return, as the 2015 pseudocode shows?
2. Did the Mono engine decrement an instruction budget at yield points? No public source says so.

For further research:

3. Whether JavaGoX unwinds with exception handlers. The paper could not be read.
4. Whether Brakes or JavaGoX check restored state.
5. Tao's thesis has been read only through other papers' summaries.
6. `davehillier.net`, Crossref and Springer could not be fetched in the November pass. Claims re-checked then rest on search-result summaries; the notes mark each one.
