# Reframe Prim's contributions around verified prior art

None of the whitepaper's four techniques is new on its own, and the paper names the wrong parallel development. Exception-based stack capture with re-entry dispatch was published for Java between 1998 and 2001, by Fünfrocken, by Sekiguchi and colleagues (JavaGo) and by Tao. Pettyjohn et al. showed it on **MSIL/.NET in 2005**, two years before the Second Life work. Bytecode-level restore dispatch appeared in Brakes (2000) and JavaGoX (2000). Back-edge yield points are standard VM practice going back to Feeley (1993). A bytecode-rewriting instruction budget for untrusted JVM code was published as J-RAF2 (2004–2008). Validating mobile-agent state on arrival goes back to Farmer, Guttman and Swarup (1996).

The whitepaper also misdescribes its own history. The author's 2015 account and the repository's blog draft both say Second Life used a **flag-and-return** unwind. Exception-based capture is Prim's design choice. And Stadler et al. (2009), cited as the "parallel development", is a HotSpot VM modification, not a program transformation.

What the paper can still defend is narrower and real:

- the first publicly documented **production** deployment of transformation-based serializable continuations on the CLR (announced 2006, live on the Second Life grid on 29 August 2008);
- a CIL-specific restore dispatch that is **verifiable** under ECMA-335's protected-region rules;
- instruction-budget preemption whose exhaustion produces a **serializable, migratable continuation** rather than a sleep or a kill;
- **verifier-style structural validation** of deserialized frames. The notes found no peer-reviewed precedent for this, and a 2025 attack on GraalVM Espresso's continuations is a current motivating case.

The rest of this report sets out the evidence, per contribution, and ends with concrete edits and a paste-ready bibliography block.

## Four decades of prior art sort into five families

Research on capturing execution state on managed runtimes without VM support splits into five families. Placing Prim among them is the first job of a revised §9.

**Family 1: mobile-agent thread migration on the JVM (1998–2002).** This is the direct ancestor of both Second Life and Prim.

- **Fünfrocken** (MA '98) used a source preprocessor that inserted try/catch blocks to save each thread's state into backup objects ([Fünfrocken 1998](https://doi.org/10.1007/BFb0057646); mechanism per [Milanés et al.](https://arxiv.org/pdf/cs/0612125)).
- **JavaGo** (COORDINATION 1999) was a source-to-source translator. "Exceptions are thrown recursively until the whole call stack is saved as a chain of State objects." It restored by re-invocation plus switch/case "unfolding", because Java has no goto ([Sekiguchi, Masuhara, Yonezawa 1999](https://doi.org/10.1007/3-540-48919-3_16); [Milanés et al.](https://arxiv.org/pdf/cs/0612125)).
- **Sekiguchi, Sakamoto and Yonezawa** (2001) generalised the technique to Java and C++, with the translation defined on bytecode ([LNCS 2022](https://doi.org/10.1007/3-540-45407-1_14)).
- **Wei Tao's** 2001 Utah thesis defined an exception-based shutdown/restart transformation at both source and bytecode level ([ProQuest](https://www.proquest.com/openview/8ecca602b761150666e2badd3e4c43cb/1?pq-origsite=gscholar&cbl=18750&diss=y)).
- **Brakes** (ASA/MA 2000) took the other route. It is a bytecode transformer in which "every method saves the current frame in the Context object and returns to the previous method recursively", then restores with a bytecode `goto` to the "last performed instruction" ([Truyen et al. 2000](https://doi.org/10.1007/978-3-540-45347-5_4); [Milanés et al.](https://arxiv.org/pdf/cs/0612125)).
- **JavaGoX** (same venue) moved JavaGo to bytecode, "using a type system for Java bytecode to correctly determine valid frame variables and operand stack entries" ([Sakamoto et al. 2000](https://link.springer.com/chapter/10.1007/978-3-540-45347-5_3)).

A 2002 INRIA comparison classes Wasp, JavaGo, Brakes and JavaGoX as portable "application-level" systems. It sets them against JVM-level ones such as ITS/CTS, Sumatra and the JPDA-based CIA ([Bouchenak et al., RR-4662](https://inria.hal.science/inria-00071923/document)).

**Family 2: continuations from stack inspection (2005 onward).** This family added theory and moved to other runtimes.

- **Pettyjohn, Clements, Marshall, Krishnamurthi and Felleisen** (ICFP 2005) showed "how to use our new technique to copy and reconstitute the stack on MSIL.Net using exception handlers". Each call is wrapped in a handler that does `sce.Extend(new fact_frame0(x)); throw;` ([Pettyjohn et al. 2005](https://cs.brown.edu/~sk/Publications/Papers/Published/pcmkf-cont-from-gen-stack-insp/paper.pdf)).
- **Loitsch** (Scheme Workshop 2007) adapted the technique to JavaScript, calling it "an adaption and evolution of the suspension and migration techniques presented in Tao's thesis… and Sekiguchi et al.'s paper" ([Loitsch 2007](https://www.schemeworkshop.org/2007/procPaper4.pdf)).
- **Stopify** (PLDI 2018) offers exceptional, checked-return and eager strategies side by side ([Baxter et al. 2018](https://arxiv.org/abs/1802.02974)).

**Family 3: return-based bytecode instrumentation (mid-2000s).** Apache Commons **Javaflow** grew out of Brakes and has a `java.io.Serializable` `Continuation` ([Javaflow javadoc](https://commons.apache.org/sandbox/commons-javaflow/apidocs/org/apache/commons/javaflow/Continuation.html); [tascalate-javaflow](https://github.com/vsilaev/tascalate-javaflow)). **RIFE** did the same, limited to a single frame ([Artima 2007](https://artima.com/lejava/articles/continuations.html)). **Kilim** also belongs here, and it explicitly rejected exceptions ([Srinivasan & Mycroft 2008](https://www.malhar.net/sriram/kilim/kilim_ecoop08.pdf)).

**Family 4: VM-level continuations.** None of these can be serialized portably:

- Stadler et al.'s HotSpot extension ([PPPJ 2009](https://ssw.jku.at/Research/Papers/Stadler09/Stadler09a.pdf)).
- Mono.Tasklets, which "requires changes to the VM that are not portable to other ISO CLI implementations" ([Mono docs](https://www.mono-project.com/archived/continuations/)).
- Loom ([JEP 444](https://openjdk.org/jeps/444)).
- OCaml 5 ([Sivaramakrishnan et al. 2021](https://dl.acm.org/doi/10.1145/3453483.3454039)).
- WasmFX, whose continuations are "single-shot… An attempt to invoke a continuation more than once results in a trap" ([Explainer](https://github.com/WebAssembly/stack-switching/blob/main/proposals/stack-switching/Explainer.md)).
- .NET's Runtime Async, a preview feature in .NET 11 ([Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)).

GraalVM Espresso is the exception: it serializes, but only because Truffle can materialise frames ([GraalVM docs](https://www.graalvm.org/latest/reference-manual/espresso/continuations/)).

**Family 5: durable execution by replay.** Temporal, Durable Functions, Restate and Golem avoid serializing the stack altogether. They log side-effect results and re-run user code from the top, which imposes a determinism contract ([Temporal](https://docs.temporal.io/workflows); [Durable Functions constraints](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-code-constraints); [Burckhardt et al. 2021](https://dl.acm.org/doi/10.1145/3485510)).

Two other groups matter because they are the only other systems that actually serialize continuations:

- **Racket stateless servlets** transform source into serializable continuation records that are "scarcely more than 100 bytes" ([McCarthy 2009](https://jeapostrophe.github.io/home/static/icfp065-mccarthy.pdf)).
- **Lua's Eris**, whose README says it persists yielded coroutines so that game "scripts won't even know the game was quit inbetween" ([Eris](https://github.com/fnuecke/eris)).

Against this map, Second Life sits squarely in Family 1's return-based branch, transplanted to the CLR. Prim sits in Family 1's exception-based branch and in Family 2. The current §9 discusses only Espresso, WasmFX, Asyncify and Loom, which are all modern systems from Families 4 and 5. It leaves out the entire lineage the author himself credited in 2015.

## The Second Life mechanism was flag-and-return, not exceptions

This is the most consequential factual problem in the paper. §1.1 and §1.2 present exception-based capture as the core technique "developed for Linden Lab's Second Life (2007–2008)". The Abstract says "the techniques were originally developed for Second Life". The primary sources disagree:

- **The author's 2015 post.** It shows `if (IsYieldDue) { frame.pc=1; frame.locals=...; frame.stack=...; goto Yield; }` followed by `Yield: if (IsSaving) PushFrame(frame); // return normally`, and it names **Brakes and JavaGoX** as influences ([Hillier 2015](https://davehillier.net/2015/03/11/second-life-mono-internals/)). That is a Brakes-style flag-driven save-on-return. No per-frame catch-and-rethrow appears.
- **The repository's blog draft.** [`docs/blog/continuations-revisited.md`](../blog/continuations-revisited.md) says the same thing more directly. Under "Exception-Based Capture over Explicit Save Blocks" it states: "The original design injected explicit save logic at each yield point… The exception approach is more elegant". In other words, exceptions are a **design decision for the recreation**.
- **The whitepaper itself.** §3.3 already concedes that "LSL has no exception handling, so the original Second Life implementation never had to deal with any of this". That fits a return-based original better than an exception-based one.

The contemporaneous public record supports a CIL-rewriting system but says nothing about exceptions either way. Miguel de Icaza described "a CIL rewriting engine that injected the state serialization and reincarnation into an existing CIL instructions stream" ([de Icaza 2009](https://tirania.org/blog/archive/2009/Apr-09.html)). The Second Life wiki describes an injector that "searches through the script assembly finding points where the script should yield and inject extra opcodes that copy the stack into a heap object" ([SL Wiki: Mono](https://wiki.secondlife.com/wiki/Mono)).

The dates also need adjusting. The whitepaper says 2007–2008, but the public record starts earlier:

- Babbage Linden blogged "Second Life In Mono" in August 2005 ([secondlife.blogs.com](https://secondlife.blogs.com/babbage/2005/08/second_life_in_.html)).
- Jim Purbrick and Cory Ondrejka presented the microthreading work at **Lang.NET 2006** ([Purbrick](https://jimpurbrick.com/tag/mono.html)).
- De Icaza wrote in June 2006 that "Second Life has added support to 'migrate' running code across machines" ([de Icaza 2006](https://tirania.org/blog/archive/2006/Jun-07-1.html)).
- Mono went live on the main grid on **29 August 2008** with server 1.24.3 ([SL Wiki: Mono](https://wiki.secondlife.com/wiki/Mono)).

"2007–2008" may accurately describe the author's own involvement. The design itself was public by mid-2006, and saying so strengthens the priority claim over Kilim (2008) and Stadler (2009).

The paper should therefore separate two systems throughout.

- **Second Life (2006–2008):** RAIL-based CIL rewriting, flag-and-return unwind, a restore prologue that switches to the saved PC, back-edge yield points, and a controlled LSL compiler. Its closest relatives are Brakes, JavaGoX and Javaflow.
- **Prim (2026):** Cecil and Roslyn, exception-based unwind, a verifiable prologue/dispatch split, a replay-based source generator, and state validation. Its closest relatives are JavaGo, Tao, Pettyjohn et al. and Loitsch.

The author should also confirm the shipped Second Life mechanism against the original source or colleagues before publication. The notes flag this as unresolved. The 2015 pseudocode could be a simplification.

## Contributions one and two: techniques are inherited, CIL verifiability is not

### Contribution 1: exception-based capture has at least six earlier instances

**Exception-based capture cannot be claimed as a contribution in any form.** The notes turn up at least six published precedents before or around Second Life:

- Fünfrocken 1998
- JavaGo 1999
- Sekiguchi et al. 2001
- Tao 2001
- Pettyjohn et al. 2005, on the CLR itself
- Loitsch 2007

GraalVM Espresso uses the same pattern today. Its internal docs say frames are copied "into a new host-side object called a `HostFrameRecord` (HFR). The exception is then rethrown. The HFRs are chained" ([graal continuations.md](https://github.com/oracle/graal/blob/master/espresso/docs/continuations.md)).

That last point has a presentational consequence. §3.1 and §7.1 use the class name **`HostFrameRecord`**, which is Espresso's internal name. §9.1 then describes the similarity as "convergent design". A reviewer who notices the identical name will read the convergence claim as disingenuous. The paper should either credit Espresso as the source of the name or rename the record (for example `FrameRecord`).

The paper's argument for exceptions also needs work. §3 cites "lazy capture" as the key advantage. Stopify's authors observe that "both checked-return and exceptional continuations reify the stack lazily" ([Baxter et al.](https://arxiv.org/pdf/1802.02974)), so laziness does not tell the two designs apart. The real trade-offs are elsewhere:

- **Where the capture code lives.** It is one catch block per method rather than one save block per yield point. This is the blog draft's actual argument, and it is sound.
- **Steady-state cost.** Exceptions cost a handler per call site. Return-based unwinding costs a flag check after every call.
- **Unwind cost.** Kilim's authors found exceptions "more expensive by almost two orders of magnitude" and noted that they "clear the operand stack as well" ([Srinivasan & Mycroft 2008](https://www.malhar.net/sriram/kilim/kilim_ecoop08.pdf)).

Prim's design already spills the evaluation stack before the check (§4.4), which neutralises the second half of Kilim's objection. §3 should say so explicitly. The first half, throw cost, is exactly what Prim's unpublished benchmarks (§10.1) should measure.

Two external reference points exist:

- Loitsch measured **1.1×–4.1×** instrumentation-only slowdown on Firefox, with outliers up to 16.9× on Opera ([Loitsch 2007](https://www.schemeworkshop.org/2007/procPaper4.pdf)).
- Asyncify reports about **50% average code growth** in its worst case, and "if the binary is 20% bigger, it tends to be at worst 20% slower" ([Zakai 2019](https://kripken.github.io/blog/wasm/2019/07/16/asyncify.html)).

### Contribution 2: restore dispatch is standard, but verifiable CIL dispatch is new

**Restore dispatch is equally well established.** Every example below re-enters each method and jumps to a saved index:

- Brakes jumps to a saved "last performed instruction" with bytecode `goto`.
- Tao "maintains an index and adds extra logic to the code so that the resumption point can be relocated" ([Pettyjohn et al.](https://doi.org/10.1145/1086365.1086393)).
- In Kilim, each method "consults the fiber upon entry, jumps directly to the resumption point".
- Javaflow distinguishes "normal execution, continuation capturing, and continuation resuming" in every method ([Stadler et al.](https://ssw.jku.at/Research/Papers/Stadler09/Stadler09a.pdf)).

What is genuinely specific to Prim is **the CIL verifiability problem in §4.2**. ECMA-335 allows a protected region to be entered only at its first instruction, and forbids `ret` inside one. An exception-based rewriter must therefore split the restore logic: a prologue outside the `try`, and a dispatch `switch` as the `try`'s first instruction. Every `ret` becomes a `leave`.

None of the JVM precedents faced this. The JVM has no equivalent protected-region entry rule, and Brakes and Kilim use no exceptions at all. Pettyjohn et al. worked on MSIL but presented their transformation in C# on a small example. They report no benchmark table and no verifiability discussion ([Pettyjohn et al.](https://cs.brown.edu/~sk/Publications/Papers/Published/pcmkf-cont-from-gen-stack-insp/paper.pdf)).

Contribution 2 should therefore be rewritten around this narrow point: **an exception-based capture transformation over CIL whose output passes `ilverify`**. The supporting pieces are the CFG-based stack simulation and the spill discipline for byrefs and pinned locals. The claim must be scoped honestly. §10.4 admits that Prim's rewriter "does not yet support suspension across a chain of rewritten methods". A contribution the implementation cannot yet demonstrate end to end should be labelled as design, not result.

## Contribution three: placement and counting predate Second Life, the combination does not

### Back-edge placement

**Back-edge yield points are textbook VM engineering.** Feeley's 1993 FPCA paper is the classic treatment of compiler-inserted polling ([Feeley 1993](https://dl.acm.org/doi/10.1145/165180.165205)). Agesen's 1998 Sun Labs report, which the whitepaper already cites as [9], compares polling strategies for GC points. Jalapeño/Jikes RVM placed yieldpoints in prologues and on back-edges ([Alpern et al. 2000](https://dl.acm.org/doi/10.1147/sj.391.0211)).

Lin et al. state the convention directly: "to avoid unbounded waits, yieldpoints typically occur on loop backedges and on method prologs or epilogs". They also measured a 7.2% code-size cost for conditional back-edge yieldpoints ([Lin et al., ISMM 2015](https://www.steveblackburn.org/pubs/papers/yieldpoint-ismm-2015.pdf)). Their "prologs or epilogs" placement also answers the recursion gap that §5.1 acknowledges. Method-entry polls are the standard fix, not an open option.

### Instruction counting

**Instruction counting for untrusted code has a direct, portable, bytecode-rewriting precedent.** J-RAF2 (Hulaas and Binder, 2004–2008) rewrites every Java method to count executed bytecodes per thread. It polls `if (cpu.consumption >= 0) cpu.triggerConsume();` at:

- method entry and exit,
- loop heads,
- exception-handler entry,
- every MAXPATH instructions.

Inside `consume`, the thread "may terminate the component… or it may delay itself" ([Hulaas & Binder 2008](https://hulaas.com/jraf2/publications/HOSC08.pdf)). That anticipates §5.2's "decrement a counter at yield points; when it reaches zero, force a yield" by three to four years.

The idea recurs across many systems:

- Lua's `debug.sethook` count hook ([Lua 5.1](https://pgl.yoyo.org/luai/i/debug.sethook)).
- Ethereum gas ([Yellow Paper](https://ethereum.github.io/yellowpaper/paper.pdf)).
- Wasm metering injectors that charge per basic block ([wasm-instrument](https://github.com/paritytech/wasm-instrument)).
- Wasmtime's fuel and epoch interruption. The docs describe fuel as deterministic and epochs as cheaper but "based on wall-time rather than an exact count" ([Wasmtime Config](https://docs.wasmtime.dev/api/wasmtime/struct.Config.html)).

### What is distinctive

The defensible element is the combination, and the paper should say so plainly. J-RAF2 can only sleep or kill a thread. VM safepoints only park a native thread. **In Second Life and Prim, budget exhaustion triggers capture of a serializable continuation**, so a preempted script can be persisted or moved to another simulator. That is qualitatively different from throttling.

§5.2's split, with a timer setting policy and a managed-code counter providing the cheap mechanism, is a sensible design for avoiding managed/native transitions. It is the same determinism-versus-cost trade-off that Wasmtime later formalised as fuel versus epochs. Citing Wasmtime would show the design anticipated a pattern the Wasm ecosystem adopted in 2021–22.

### Sourcing the Second Life counter

One caveat needs explicit handling. The notes found **no public source** for Second Life's instruction counter. The 2015 post mentions back-edge yields but not counting, and the counter's only documentary basis is the author's own recollection in the blog draft. The whitepaper should present it as first-hand testimony ("in the author's recollection of the deployed system…"). It should not be stated as documented fact.

## Contribution four: the strongest novelty claim, but it belongs to Prim

### What is already established

**Deserialization danger is thoroughly established.** Frohoff and Lawrence's 2015 gadget-chain work and ysoserial made it mainstream ([Marshalling Pickles](https://frohoff.github.io/appseccali-marshalling-pickles/)). Microsoft states that "the BinaryFormatter.Deserialize method is never safe when used with untrusted input" ([BinaryFormatter guide](https://learn.microsoft.com/en-us/dotnet/standard/serialization/binaryformatter-security-guide)).

Continuation systems have handled the risk in two ways:

- **Refuse to accept foreign state.** Espresso's docs say "only resume continuations you persisted yourself!" ([GraalVM](https://www.graalvm.org/latest/reference-manual/espresso/continuations/)).
- **Authenticate the state.** Racket's `HMAC-SHA1-stuffer` lets a servlet "certify that stuffed data was truly generated by the servlet". The default stuffer does not sign ([Racket docs](https://docs.racket-lang.org/web-server/stateless.html)).

For migrating agents, Farmer, Guttman and Swarup proposed **state appraisal** in 1996: functions that check invariants on arriving agent state, "to protect a host from attack… maintain critical state invariants, and to control privileges" ([ESORICS '96](https://link.springer.com/chapter/10.1007/3-540-61770-1_31)).

### The 2025 Espresso attack

The best new evidence is a 2025 attack on Espresso. It forged a `FrameRecord` chain with attacker-chosen `method`, `bci`, locals and `next` fields. The chain resumed into JDK gadgets (`UnixPrintJob.printExecCmd()` at bci 367, then `Runtime.exec`) and achieved command execution ([X1r0z 2025](https://exp10it.io/2025/08/hacking-graalvm-espresso-abusing-continuation-api-to-make-rop-like-attack/)).

The fields that attack manipulated are **exactly the fields §8.2 validates**: method identity, resume index, and slot count and type. It is a non-peer-reviewed blog post, and the paper should say so. It is still the most persuasive motivation available, because it shows that an unvalidated resumable-frame format is an arbitrary-jump primitive.

### What to claim, and whose contribution it is

The notes found **no peer-reviewed paper proposing structural validation of deserialized stack frames**. That covers checks on method identity, yield-point bounds, exact slot counts, per-slot types and depth limits. This is weak evidence of novelty, not proof.

The honest framing is "applying bytecode-verifier-style frame typing to deserialized resume state". JVM StackMapTable and CLR verification already type frames at each instruction. The new part is enforcing that typing on state that arrives from outside.

Two revisions follow.

- **Attribute contribution 4 to Prim, not to Second Life.** §8.3 and the blog draft both describe Second Life's model as a controlled compiler requiring "minimal validation", backed by an instruction whitelist on bytecode. Second Life did not validate state.
- **Explain why a MAC alone is insufficient.** Examples include code-version skew between hosts (resume indices and slot layouts change when a script is recompiled), stale or corrupted state, and multi-host trust boundaries of the kind Farmer et al. described. Without this, a reviewer will ask why Prim does not simply sign continuations as Racket does.

The paper should also cite the classic gadget-chain and BinaryFormatter sources in §8.2, where it rightly says the type whitelist must be enforced during deserialization.

## Section-by-section revision plan for the whitepaper

**Abstract and §1.1.** Replace "the core contribution is a bytecode transformation… using exception-based unwinding" with a claim of *systems contribution and documentation*. Suggested contributions list:

1. **A documented account of the first production system for transformation-based serializable continuations on the CLR.** Second Life's system was announced in 2006 and deployed in 2008. Its mechanism, flag-and-return unwinding in the Brakes/JavaGoX lineage, should be described accurately.
2. **A verifiable exception-based CIL transformation.** Frame it as adopting the capture technique of Sekiguchi et al., Tao and Pettyjohn et al. The contribution is the protected-region-legal prologue/dispatch split, the spill discipline and CFG-based stack simulation, all checked with `ilverify`.
3. **Preemption that yields serializable state.** Back-edge and entry yield points plus an instruction budget, positioned against Feeley, Lin et al. and J-RAF2, where budget exhaustion produces a migratable continuation.
4. **Structural validation of deserialized continuation frames**, motivated by the Espresso forgery attack and positioned against state appraisal, MAC-based integrity and serializer allow-lists.

Each item should carry its prior-art citations inline.

**§1.2 Context.** Two changes:

- Date the public record from Lang.NET 2006 and the 29 August 2008 deployment.
- Replace the Stadler sentence. It currently says "Stadler et al. at JKU Linz and Sun Microsystems developed lazy continuations for the JVM… parallel evolution suggests the patterns described here are fundamental". Stadler's paper lists Stadler, Würthinger and Mössenböck at JKU Linz, Wimmer at the **University of California, Irvine**, and Rose at Sun Microsystems. Its mechanism is a HotSpot interpreter and compiler extension in which frames are copied as "a plain copy of the stack contents". It imposes "zero run-time overhead as long as no activations need to be saved and restored" and makes no serialization claim ([PDF](https://ssw.jku.at/Research/Papers/Stadler09/Stadler09a.pdf)). It is a *contrast*, not a parallel. The true parallel lineage is the mobile-agent work that Second Life's authors knew and cited.

The blog draft's "Oracle Labs team" phrasing is inconsistent with the affiliations printed on the paper and should be corrected there too.

**§3.** Open the section by crediting Fünfrocken, JavaGo, Sekiguchi et al., Tao, Pettyjohn et al., Loitsch and Stopify. State that Second Life used flag-and-return while Prim uses exceptions, and give the real trade-off: code locality against Kilim's measured throw cost. Rename or attribute `HostFrameRecord`.

**§5.** Add Feeley, Lin et al. and Alpern et al. to §5.1 and §5.3. Add J-RAF2, Wasmtime fuel and epochs, and Wasm metering to §5.2. Mark the Second Life counter as author testimony.

**§6.3.** Distinguish Prim's replay from Temporal and Durable Functions replay. Those systems replay an *effect log* and demand whole-program determinism. Prim re-runs a method prefix over restored locals, so only code *before the yield in each frame* must be idempotent.

**§8.** Add the Espresso attack, Farmer et al., the gadget-chain work, BinaryFormatter and the Racket HMAC stuffer. Justify validation beyond a MAC.

**§9.** Restructure §9 into the five families above, keeping the existing Espresso, WasmFX, Asyncify and Loom subsections. Four additions and corrections:

- Add Javaflow, RIFE and Kilim as the return-based alternative, with Kilim's note that nothing indicates its Fiber state is serializable.
- Add McCarthy's serializable Racket continuations and Eris as the other serializing systems.
- Add Kotlin coroutines, whose serializability issue has been open since 2017 ([kotlinx.coroutines#76](https://github.com/Kotlin/kotlinx.coroutines/issues/76)), and Orleans live migration, which moves grains only when idle ([Orleans lifecycle](https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-lifecycle)).
- Update WasmFX. It is now at **Phase 3**, and JSPI is at Phase 5 and shipped in Chrome 137 ([proposals README](https://github.com/WebAssembly/proposals/blob/main/README.md); [V8](https://v8.dev/blog/jspi)).

Also soften §9.3's flat statement that Asyncify's linear-memory buffer is "not a serializable format". The buffer is ordinary program-visible bytes. What it lacks is a portable, versioned, validated encoding, and the Wasm live-migration literature already injects checkpoint and restore into bytecode ([Tinto et al. 2025](https://www.sciencedirect.com/science/article/pii/S1383762125002048); [Wharf](https://arxiv.org/html/2410.15894v1)).

**§11 Conclusion.** Delete "the independent development of similar techniques by Stadler et al." as evidence of fundamentality. Replace it with the stronger claim the evidence supports. The same pattern has been independently rediscovered at least five times, across the JVM, the CLR, JavaScript, Wasm and Espresso, from 1998 to 2025. Serializability, validation and production deployment are what remain rare.

**Existing references to correct.**

- [6]: add DOI 10.1145/1596655.1596679.
- [8]: add LNCS 7313 and DOI 10.1007/978-3-642-31057-7_12.
- [19]: should read *Proc. ACM Program. Lang.* 7(OOPSLA2), Article 238, October 2023, DOI 10.1145/3622814 ([ACM DL](https://dl.acm.org/doi/10.1145/3622814)). The current text says "Proceedings of OOPSLA 2023".
- [20]: add DOI 10.1145/3453483.3454039.
- [9] and [10]: verified as cited ([Jones GC bibliography](https://www.cs.kent.ac.uk/people/staff/rej/gcbib/gcbibA.html); [ACM DL](https://dl.acm.org/doi/10.1145/3062341.3062381)).

### Proposed additions to the References block

The entries continue the whitepaper's numbering. The notes flag several details as unverified, marked as follows:

- **[unverified: …]** means the notes could not confirm that specific detail.
- **[secondary]** means the mechanism was confirmed only through surveys.

Resolve every marked detail before submission.

[23] Fünfrocken, S. (1998). "Transparent Migration of Java-Based Mobile Agents." In *Mobile Agents (MA '98)*, LNCS 1477 [unverified: volume number], Springer, 26–37. doi:10.1007/BFb0057646

[24] Sekiguchi, T., Masuhara, H., Yonezawa, A. (1999). "A Simple Extension of Java Language for Controllable Transparent Migration and its Portable Implementation." In *Coordination Languages and Models (COORDINATION 1999)*, LNCS 1594, Springer, 211–226. doi:10.1007/3-540-48919-3_16 [secondary: mechanism]

[25] Truyen, E., Robben, B., Vanhaute, B., Coninx, T., Joosen, W., Verbaeten, P. (2000). "Portable Support for Transparent Thread Migration in Java." In *Agent Systems, Mobile Agents, and Applications (ASA/MA 2000)*, LNCS 1882 [unverified: volume number], Springer, 29–43. doi:10.1007/978-3-540-45347-5_4 [secondary: mechanism]

[26] Sakamoto, T., Sekiguchi, T., Yonezawa, A. (2000). "Bytecode Transformation for Portable Thread Migration in Java." In *Agent Systems, Mobile Agents, and Applications (ASA/MA 2000)*, LNCS 1882, Springer, 16–28. doi:10.1007/978-3-540-45347-5_3 [unverified: whether JavaGoX unwinds via exception handlers]

[27] Sekiguchi, T., Sakamoto, T., Yonezawa, A. (2001). "Portable Implementation of Continuation Operators in Imperative Languages by Exception Handling." In *Advances in Exception Handling Techniques*, LNCS 2022, Springer, 217–233. doi:10.1007/3-540-45407-1_14 [secondary: mechanism]

[28] Tao, W. (2001). *A Portable Mechanism for Thread Persistence and Migration.* PhD thesis, University of Utah. ProQuest 3005121. [secondary: mechanism; thesis text not read]

[29] Bouchenak, S., Hagimont, D. (2000). "Pickling Threads State in the Java System." In *Proceedings of TOOLS 33*, IEEE, 22–32. doi:10.1109/TOOLS.2000.848748

[30] Bouchenak, S., Hagimont, D., Krakowiak, S., De Palma, N., Boyer, F. (2004). "Experiences Implementing Efficient Java Thread Serialization, Mobility and Persistence." *Software: Practice and Experience*, 34(4), 355–393. doi:10.1002/spe.569

[31] Illmann, T., Krueger, T., Kargl, F., Weber, M. (2001). "Transparent Migration of Mobile Agents Using the Java Platform Debugger Architecture." In *Mobile Agents (MA 2001)*, LNCS 2240 [unverified: volume number], Springer, 198–212. doi:10.1007/3-540-45647-3_14

[32] Pettyjohn, G., Clements, J., Marshall, J., Krishnamurthi, S., Felleisen, M. (2005). "Continuations from Generalized Stack Inspection." In *Proceedings of ICFP 2005*, ACM, 216–227. doi:10.1145/1086365.1086393

[33] Loitsch, F. (2007). "Exceptional Continuations in JavaScript." In *Proceedings of the 2007 Workshop on Scheme and Functional Programming*, Université Laval Technical Report DIUL-RT-0701, 37–45 [unverified: end page]. https://www.schemeworkshop.org/2007/procPaper4.pdf

[34] Srinivasan, S., Mycroft, A. (2008). "Kilim: Isolation-Typed Actors for Java." In *Proceedings of ECOOP 2008*, LNCS 5142 [unverified: volume number], Springer, 104–128. doi:10.1007/978-3-540-70592-5_6

[35] Apache Commons Javaflow. "Continuation" API documentation. https://commons.apache.org/sandbox/commons-javaflow/apidocs/org/apache/commons/javaflow/Continuation.html [unverified: project start date (c. 2005)]

[36] Venners, B., Sommers, F. (2007). "Continuations in Java" (interview with G. Bevin on RIFE). Artima. https://artima.com/lejava/articles/continuations.html [unverified: exact byline]

[37] McCarthy, J. A. (2009). "Automatically RESTful Web Applications: Marking Modular Serializable Continuations." In *Proceedings of ICFP 2009*, ACM, 299–310. doi:10.1145/1596550.1596594

[38] Baxter, S., Nigam, R., Politz, J. G., Krishnamurthi, S., Guha, A. (2018). "Putting in All the Stops: Execution Control for JavaScript." In *Proceedings of PLDI 2018*, ACM, 30–45. doi:10.1145/3192366.3192370

[39] Feeley, M. (1993). "Polling Efficiently on Stock Hardware." In *Proceedings of FPCA '93*, ACM, 179–187 [unverified: sources give 179–187 or 179–190]. doi:10.1145/165180.165205

[40] Alpern, B., Attanasio, C. R., Barton, J. J., et al. (2000). "The Jalapeño Virtual Machine." *IBM Systems Journal*, 39(1), 211–238. doi:10.1147/sj.391.0211 [secondary: yieldpoint placement detail]

[41] Lin, Y., Wang, K., Blackburn, S. M., Hosking, A. L., Norrish, M. (2015). "Stop and Go: Understanding Yieldpoint Behavior." In *Proceedings of ISMM 2015*, ACM, 70–80. doi:10.1145/2887746.2754187 (SIGPLAN Notices) [unverified: proceedings DOI 10.1145/2754169.2754187]

[42] Hulaas, J., Binder, W. (2008). "Program Transformations for Light-Weight CPU Accounting and Control in the Java Virtual Machine: A Systematic Review." *Higher-Order and Symbolic Computation*, 21, 119–146. doi:10.1007/s10990-008-9026-4

[43] Farmer, W. M., Guttman, J. D., Swarup, V. (1996). "Security for Mobile Agents: Authentication and State Appraisal." In *Computer Security – ESORICS '96*, LNCS 1146, Springer, 118–130. doi:10.1007/3-540-61770-1_31

[44] Frohoff, C., Lawrence, G. (2015). "Marshalling Pickles: How Deserializing Objects Will Ruin Your Day." OWASP AppSec California. https://frohoff.github.io/appseccali-marshalling-pickles/

[45] Microsoft. "Deserialization Risks in Use of BinaryFormatter and Related Types." https://learn.microsoft.com/en-us/dotnet/standard/serialization/binaryformatter-security-guide

[46] X1r0z. (2025). "Hacking GraalVM Espresso: Abusing Continuation API to Make ROP-like Attack." https://exp10it.io/2025/08/hacking-graalvm-espresso-abusing-continuation-api-to-make-rop-like-attack/ (non-peer-reviewed)

[47] de Icaza, M. (2009). "Continuations in Mono: Embrace and Extending .NET, Part 3." https://tirania.org/blog/archive/2009/Apr-09.html

[48] de Icaza, M. (2006). Blog post on Second Life microthreading, 7 June 2006. https://tirania.org/blog/archive/2006/Jun-07-1.html

[49] Second Life Wiki. "Mono." https://wiki.secondlife.com/wiki/Mono

[50] Burckhardt, S., Gillum, C., Justo, D., Kallas, K., McMahon, C., Meiklejohn, C. S. (2021). "Durable Functions: Semantics for Stateful Serverless." *Proc. ACM Program. Lang.*, 5(OOPSLA), Article 133. doi:10.1145/3485510

[51] Elizarov, R., Belyaev, M., Akhin, M., Usmanov, I. (2021). "Kotlin Coroutines: Design and Implementation." In *Proceedings of Onward! 2021*, ACM. doi:10.1145/3486607.3486751

[52] Racket Documentation. "Stateless Servlets." https://docs.racket-lang.org/web-server/stateless.html

[53] Tinto et al. (2025). Checkpoint/restore by bytecode injection in compiled WebAssembly modules. *Journal of Systems Architecture*. https://www.sciencedirect.com/science/article/pii/S1383762125002048 [unverified: full author list and title]

[54] Nuecke, F. "Eris: Persistence for Lua 5.2/5.3." https://github.com/fnuecke/eris [unverified: author attribution from repository owner]

## Conclusion

The whitepaper's real asset is not a new technique. It is an **unusual vantage point**: one author who shipped the Brakes-lineage design at production scale on the CLR in 2008, and who has now rebuilt it with the JavaGo and Pettyjohn lineage's exception unwinding. That makes Prim a rare controlled comparison of the two dominant capture strategies on one runtime. Publishing the §10.1 benchmarks for exceptional against return-based capture would add a result, where the current framing only makes an overclaim. It would also test Kilim's two-orders-of-magnitude claim on a modern CLR, a question the literature has left open since 2008.

The field has also moved towards Prim's niche rather than away from it. WasmFX, Loom, OCaml 5 and .NET Runtime Async are all opaque and one-shot. Espresso is the only mainstream serializable design, and it has now been shown exploitable through exactly the frame fields Prim validates. Industry durable execution chose effect-log replay precisely to avoid serializing stacks. A whitepaper that concedes the inherited mechanics up front, and argues for serializability, verifiability, preemption-to-migration and validated resumption, will be both more accurate and more persuasive than one that claims the mechanics as original.
