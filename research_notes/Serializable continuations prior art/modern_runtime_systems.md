# Modern runtime-level continuation systems vs. serializable continuations (as of Sept 2026)

## GraalVM Espresso Continuation API

### Takeaway
Espresso (Java on Truffle) provides an experimental Continuation API that copies suspended frames to the heap. The `Continuation` class is `Serializable`, so a suspended continuation can be resumed in another JVM running the same code. Serializing a continuation also makes it multi-shot. The docs carry an explicit warning that deserializing an attacker-supplied continuation gives the attacker full control of the JVM. The internal frame record class is really called `HostFrameRecord` (HFR). This is the closest runtime-level analogue to the whitepaper's approach.

### Cited Findings
- On suspend, "the stack is unwound and copied onto the heap as ordinary Java objects". On resume, those objects are put back onto the stack along with the metadata needed to continue from the pause point. — [GraalVM Continuation API docs](https://www.graalvm.org/latest/reference-manual/espresso/continuations/)
- API surface: `Continuation`, `ContinuationEntryPoint`, `SuspendCapability`, a higher-level `Generator<T>`, and `ContinuationSerializable`. — [GraalVM docs](https://www.graalvm.org/latest/reference-manual/espresso/continuations/); [Generators page](https://www.graalvm.org/jdk24/reference-manual/espresso/continuations/generators/)
- Serialization: "The heap objects can be serialized to resume execution in a different JVM running the same code (for example, after a restart)." "Continuation implements Serializable and can serialize to a backwards compatible format." `ContinuationSerializable` provides static `readObjectExternal`/`writeObjectExternal` so that non-JDK serialization engines (e.g. Kryo) can handle continuation objects. With `--java.Continuum=true`, "all lambdas are serializable but deserialization will require special support from your serializer engine." — [GraalVM docs](https://www.graalvm.org/latest/reference-manual/espresso/continuations/); [Serialization page (jdk25)](https://www.graalvm.org/jdk25/reference-manual/espresso/continuations/serialization/)
- Security warning, quoted: "Deserializing a continuation supplied by an attacker will allow complete takeover of the JVM. Only resume continuations you persisted yourself!" The docs also say that frame materialization (the `private ContinuationImpl.stackFrameHead` field) "Currently, the only path for materialization is through serialization." — [GraalVM docs](https://www.graalvm.org/latest/reference-manual/espresso/continuations/)
- Multi-shot: "Serializing a continuation makes it multi-shot, meaning you can restart a continuation more than once." The docs suggest use cases such as speculative RPC trees, distributed computing, and "your app as a continuation-based actor [that you] serialize after each state mutation". — [GraalVM docs](https://www.graalvm.org/latest/reference-manual/espresso/continuations/)
- Frame record class name (confirmed): frames are copied "into a new host-side object called a `HostFrameRecord` (HFR). The exception is then rethrown. The HFRs are chained" into a linked list. On resume, call targets "take a single argument: the `HostFrameRecord` that was stored into the `Continuation`". — [oracle/graal espresso/docs/continuations.md](https://github.com/oracle/graal/blob/master/espresso/docs/continuations.md); [GraalVM docs](https://www.graalvm.org/latest/reference-manual/espresso/continuations/)
- Stability: "The continuation feature is experimental and needs to be explicitly enabled by using these options: `--experimental-options --java.Continuum=true`." This wording is unchanged on the current ("latest") docs as of Sept 2026. — [GraalVM docs](https://www.graalvm.org/latest/reference-manual/espresso/continuations/)
- Third-party security research (Aug 2025) showed that Espresso's Continuation API can be abused for a ROP-like attack via crafted serialized continuations. This is a blog post, not peer-reviewed, but it independently backs up the official warning. — [X1r0z Blog, "Hacking GraalVM Espresso - Abusing Continuation API to Make ROP-like Attack"](https://exp10it.io/2025/08/hacking-graalvm-espresso-abusing-continuation-api-to-make-rop-like-attack/)

### Inferences
- Espresso gets serialization "for free" because frames are reified as ordinary guest-heap Java objects and serialized with the guest's own serialization. The whitepaper's CIL-rewriting approach reifies frames in user code instead. Both designs therefore inherit the classic deserialization-gadget risk. The whitepaper should cite Espresso's warning as precedent for its own trust model.
- In Espresso, continuations are one-shot in memory but effectively multi-shot once serialized. The whitepaper can reasonably make the same point about its own design.

### Gaps
- I found no peer-reviewed paper that specifically describes the Espresso Continuation API. I did not verify a specific conference talk (for example by Mike Hearn, who is often linked to the feature). Only docs and the podcast/blog ecosystem turned up.
- I did not confirm the exact GraalVM release that first shipped the API. It was present by the 24.x docs (a jdk24 Generators page exists). Earlier claims (around 23.x/24.0) are unverified.
- A WebFetch summary gave a Maven coordinate "org.graalvm.espresso:continuations:25.4.4.1.1". I could not verify it and it looks garbled, so do not cite it.

## WebAssembly stack switching (WasmFX / typed continuations) and JSPI

### Takeaway
The Stack Switching proposal (champions Francis McCabe and Sam Lindley) is at **Phase 3 (Implementation)** in the WebAssembly proposals tracker. Its instructions are `cont.new`, `cont.bind`, `suspend`, `resume`, `resume_throw` (plus `resume_throw_ref`), and `switch`. Continuations are one-shot (linear), with a trap on reuse. The WasmFX paper calls its handlers "sheep handlers", a hybrid of shallow and deep. Wasmtime has an in-progress native implementation. JSPI is at Phase 4/5: it is standardized and shipped in Chrome 137 (May 2025). None of these provide serializable continuations.

### Cited Findings
- Phase: the WebAssembly/proposals README lists "Stack Switching — Francis McCabe & Sam Lindley" under "Phase 3 - Implementation Phase (CG + WG)". — [WebAssembly/proposals README](https://github.com/WebAssembly/proposals/blob/main/README.md)
- Earlier history: the Stack Switching subgroup "advanced to stage 2 in August 2024". — [Emrich & Hillerström, WAW 2025 talk proposal](https://dhil.net/research/papers/wasmfxtime-waw2025.pdf); see also [Lindley, Wasm CG Feb 2025 slides](https://effect-handlers.org/talks/stack-switching-wasmcg-feb2025.pdf)
- Instructions, as given in the explainer:
  - `cont.new $ct : [(ref null $ft)] -> [(ref $ct)]`
  - `resume $ct hdl* : [t1* (ref null $ct)] -> [t2*]`, where a handler clause is either `(on $e $l)` or `(on $e switch)`
  - `resume_throw $ct $exn hdl*` and `resume_throw_ref $ct hdl*`
  - `switch $ct1 $e : [t1* (ref null $ct1)] -> [t2*]`
  - `suspend $e : [t1*] -> [t2*]`
  - `cont.bind $ct1 $ct2 : [t1* (ref null $ct1)] -> [(ref $ct2)]`

  Control tags are declared with `tag`. — [Stack-switching Explainer.md](https://github.com/WebAssembly/stack-switching/blob/main/proposals/stack-switching/Explainer.md)
- Suspend semantics: `suspend` "suspends the current continuation up to the nearest enclosing handler for `$e`", like raising an exception but resumable. — [Explainer](https://github.com/WebAssembly/stack-switching/blob/main/proposals/stack-switching/Explainer.md)
- One-shot: "Continuations in the current proposal are single-shot (aka linear), meaning that they should be invoked exactly once… An attempt to invoke a continuation more than once results in a trap. Some applications such as backtracking, probabilistic programming, and process duplication exploit multi-shot continuations, but none of our critical use-cases requires multi-shot continuations." The proposal builds on Wasm 3.0 function references and exception handling. — [Explainer](https://github.com/WebAssembly/stack-switching/blob/main/proposals/stack-switching/Explainer.md)
- Deep vs. shallow: "The handlers in WasmFX can be seen as a hybrid of shallow and deep: 'sheep handlers'. As with deep handlers the body of a continuation is guaranteed to be wrapped in some handler. As with shallow handlers this handler can be changed each time we resume." — [Phipps-Costin et al., arXiv 2308.08347v3](https://arxiv.org/html/2308.08347v3)
- Paper citation (verified):
  - Luna Phipps-Costin, Andreas Rossberg, Arjun Guha, Daan Leijen, Daniel Hillerström, KC Sivaramakrishnan, Matija Pretnar, Sam Lindley.
  - "Continuing WebAssembly with Effect Handlers." Proc. ACM Program. Lang. (PACMPL) **7, OOPSLA2, Article 238** (October 2023). DOI **10.1145/3622814**.
  - Sources: [ACM DL](https://dl.acm.org/doi/10.1145/3622814); [NSF PAR](https://par.nsf.gov/biblio/10483984-continuing-webassembly-effect-handlers); [Rossberg's PDF, header "238"](https://people.mpi-sws.org/~rossberg/papers/Phipps-Costin,%20Rossberg,%20Guha,%20Leijen,%20Hillerstr%C3%B6m,%20Sivaramakrishnan,%20Pretnar,%20Lindley%20-%20Continuing%20WebAssembly%20with%20Effect%20Handlers.pdf); [arXiv HTML metadata "Journal: PACMPL Volume: 7 OOPSLA2 238"](https://arxiv.org/html/2308.08347v3)
- The paper's implementation was an extension of the reference interpreter plus "a prototype WasmFX extension for Wasmtime… piggybacking on Wasmtime's existing fibers API". — [arXiv 2308.08347v3](https://arxiv.org/html/2308.08347v3)
- Wasmtime status: tracking issue #10248 was opened 19 Feb 2025. The initial implementation covers tags, types, runtime support, and CLIF compilation. It excludes:
  - non-x64 ISAs
  - `resume.throw`
  - continuation deallocation and GC integration
  - Windows
  - Pulley and Winch

  Many TODOs remain open. — [bytecodealliance/wasmtime#10248](https://github.com/bytecodealliance/wasmtime/issues/10248)
- WAW 2025 talk: the Wasmtime implementation moved from a host-fiber prototype to fully native stack switching, "achieving up to 6x performance improvements in micro-benchmarks". An upstreaming PR mentions x64 and ARM64 backends plus Binaryen and wasm-tools support. Cranelift gained a `stack_switch` CLIF instruction. — [Emrich & Hillerström, "Continuing Stack Switching in Wasmtime", WAW 2025 slides](https://effect-handlers.org/talks/wasmfx-waw2025.pdf); [talk proposal](https://dhil.net/research/papers/wasmfxtime-waw2025.pdf); [Wasmtime 25.0 release notes](https://bytecodealliance.org/articles/wasmtime-25.0)
- Follow-on formal work: "Iris-WasmFX: Modular Reasoning for Wasm Stack Switching", PACMPL. — [ACM DL 10.1145/3808271](https://dl.acm.org/doi/10.1145/3808271)
- JSPI: listed under Phase 5 (standardized) in the current proposals README. — [WebAssembly/proposals README](https://github.com/WebAssembly/proposals/blob/main/README.md). The V8 blog said it was Phase 4 and "standardized by the W3C WebAssembly CG in April 2025", and that Chrome 137 (released 27 May 2025) supports JSPI. — [V8 blog: Introducing JSPI](https://v8.dev/blog/jspi); [New in Chrome 137](https://developer.chrome.com/blog/new-in-chrome-137)
- JSPI mechanism: it suspends the Wasm application when it calls a Promise-bearing function and resumes it when the Promise resolves. — [V8 blog](https://v8.dev/blog/jspi); [Chrome Status entry](https://chromestatus.com/api/v0/features?q=stack%20switching)

### Inferences
- The phase sources differ over time but do not conflict. The proposals README (live, Sept 2026) is authoritative: Stack Switching is at Phase 3 and JSPI at Phase 5. The V8 blog's "Phase 4" was accurate when it was written in 2025.
- WasmFX continuations are opaque, linear, engine-managed stack references. They cannot be inspected, copied or serialized from Wasm. That is exactly the gap a bytecode-rewriting approach fills. Asyncify, by contrast, writes locals into linear memory, which makes it closer in spirit to serialization.

### Gaps
- I could not confirm V8/Chrome support for the full stack-switching proposal (e.g. a `--experimental-wasm-wasmfx` flag or an origin trial) from a primary source. The whitepaper should not claim V8 ships it.
- I could not confirm the Wasmtime release in which stack switching became available, or whether it is enabled by default.

## Binaryen Asyncify

### Takeaway
Asyncify is a whole-program Binaryen pass. It instruments functions so they can unwind (saving locals and call-stack position to a linear-memory buffer) and rewind (re-entering functions while skipping code up to the resume point). Zakai's 2019 post reports a code-size overhead "in the 1-2x range… around 50% larger on average" in the worst (uninstrumented-knowledge) case. Speed overhead is roughly proportional ("20% bigger … at worst 20% slower"), with an outlier: SQLite was about 5x slower.

### Cited Findings
- Mechanism: unwinding and rewinding the stack. Saved data goes into a data buffer whose header gives the current position (i32 at offset 0) and end (i32 at offset 4). The `asyncify_*` functions trap with `unreachable` if the buffer overflows. When rewinding, "the general idea is to keep skipping code while rewinding"; whole-program analysis adds overhead "only where it is actually needed". — [Alon Zakai, "Pause and Resume WebAssembly with Binaryen's Asyncify", 16 Jul 2019](https://kripken.github.io/blog/wasm/2019/07/16/asyncify.html)
- Size, quoted: "the size overhead is in the 1-2x range, that is, the binary is around 50% larger on average. Even on the realistic macrobenchmarks… it barely exceeds 2." This is the worst case, where every import and indirect call is assumed to unwind. Supplying an import list gives "about 25% smaller" on Box2D. With the list plus ignoring indirect calls, "almost all the overhead vanishes, on every single benchmark". — [Zakai 2019](https://kripken.github.io/blog/wasm/2019/07/16/asyncify.html)
- Speed, quoted: "if the binary is 20% bigger, it tends to be at worst 20% slower. However, I did notice one large outlier, SQLite, on which the slowdown is around 5x." The summary line: "should do no worse than double size / halve speed for most code". — [Zakai 2019](https://kripken.github.io/blog/wasm/2019/07/16/asyncify.html)
- Unwind/rewind cost: on fannkuch, sleeping in the innermost loop, just enabling Asyncify added 22%. 1000 sleeps of 1 ms added 1.18 s against a theoretical 1 s. "The unwind/rewind overhead of Asyncify is basically negligible if you are using it to do anything asynchronous." — [Zakai 2019](https://kripken.github.io/blog/wasm/2019/07/16/asyncify.html)
- Options to limit instrumentation:
  - `asyncify-imports`: the list of imports that can unwind/rewind. In Emscripten this is `ASYNCIFY_IMPORTS`.
  - `asyncify-ignore-indirect`: pass `--pass-arg=asyncify-ignore-indirect` to wasm-opt, or use `-s ASYNCIFY_IGNORE_INDIRECT` in Emscripten.
  - Optimize while running the pass (`-O --asyncify`). Without optimization, code size becomes "huge".

  Source: [Zakai 2019](https://kripken.github.io/blog/wasm/2019/07/16/asyncify.html)

### Inferences
- Asyncify is the closest Wasm analogue to CIL rewriting: a compile-time transformation with selective instrumentation, and frame state stored in a program-visible buffer. Its figures (about 50% average size growth in the worst case, near-zero with precise lists) are a good benchmark to set against the whitepaper's overheads.

### Gaps
- Later Binaryen versions added options such as `asyncify-onlylist`, `asyncify-removelist` and `asyncify-addlist`, and Emscripten added `ASYNCIFY_ONLY`/`ASYNCIFY_REMOVE`. I did not re-verify these names from primary docs in this session, so check them against Emscripten's Asyncify docs before citing.

## Project Loom (JEP 444)

### Takeaway
JEP 444 "Virtual Threads" was written by Ron Pressler and Alan Bateman and delivered in JDK 21 (September 2023). Loom's underlying continuations (`jdk.internal.vm.Continuation`) are not public API and are not serializable.

### Cited Findings
- JEP 444 facts: Author "Ron Pressler & Alan Bateman"; Owner Alan Bateman; Status "Closed / Delivered"; Release 21; created 2023/03/06. Related JEPs are JEP 436 (Second Preview) and JEP 491 (Synchronize Virtual Threads without Pinning). — [JEP 444](https://openjdk.org/jeps/444)

### Inferences
- The JEP exposes virtual threads, not continuations. The continuation class lives in the non-exported `jdk.internal.vm` package. I found no OpenJDK proposal to make Loom continuations serializable. Espresso's API (above) is the JVM-world serializable alternative.

### Gaps
- In this session I did not fetch a primary source saying explicitly that `jdk.internal.vm.Continuation` is internal-only. This is widely known but uncited here; openjdk/loom sources or Pressler's talks would be the place to confirm it. Similarly, I found no primary source on any serialization proposal, only the absence of one.
- JDK 21 GA date: I did not re-fetch it here. September 19, 2023 is widely reported.

## OCaml 5 effects

### Takeaway
The citation is verified: Sivaramakrishnan, Dolan, White, Kelly, Jaffer, Madhavapeddy, "Retrofitting Effect Handlers onto OCaml", PLDI 2021, pp. 206–221, DOI 10.1145/3453483.3454039. OCaml 5 continuations are one-shot runtime fiber stacks, not serializable.

### Cited Findings
- Authors: KC Sivaramakrishnan, Stephen Dolan, Leo White, Tom Kelly, Sadiq Jaffer, Anil Madhavapeddy. Proceedings of the 42nd ACM SIGPLAN PLDI 2021, pages 206–221, DOI 10.1145/3453483.3454039. — [ACM DL](https://dl.acm.org/doi/10.1145/3453483.3454039); [PLDI 2021 page](https://pldi21.sigplan.org/details/pldi-2021-papers/14/Retrofitting-Effect-Handlers-onto-OCaml); [Zenodo artifact](https://zenodo.org/records/5059817)

### Inferences
- The one-shot restriction and lack of serialization are well-known properties of OCaml 5's `Effect` module. In OCaml 5, continuations are heap-allocated fiber stacks that can be resumed at most once. They were not re-verified from primary docs in this session.

### Gaps
- I did not fetch a quotable primary statement for "one-shot" and "not serializable". The OCaml manual's Effects chapter would be the one to cite: it states continuations are one-shot and raises `Continuation_already_resumed`.

## .NET runtime-level continuations (green threads, runtime async)

### Takeaway
The 2023 .NET green threads experiment (dotnet/runtimelab) was put on hold. Runtime Async moves async/await suspension from compiler-generated state machines into the runtime. It was experimental in .NET 10 and is a preview feature in .NET 11 (compiler opt-in via `<Features>runtime-async=on</Features>`), with CoreCLR runtime support on by default and the BCL itself compiled with runtime-async. Neither provides serializable continuations.

### Cited Findings
- Green threads experiment results: interaction with the existing async model was complex. Interop with native code was "complex and slower": 100,000,000 minimal P/Invoke calls went from about 300 ms to about 1800 ms on a green thread. There were compatibility issues with security mitigations such as shadow stacks, and uncertainty about beating async on performance. The experiment was put on hold. — [dotnet/runtimelab#2398 "Green Thread Experiment Results"](https://github.com/dotnet/runtimelab/issues/2398); [design doc](https://github.com/dotnet/runtimelab/blob/feature/green-threads/docs/design/features/greenthreads.md); [original issue #2057](https://github.com/dotnet/runtimelab/issues/2057)
- In .NET 9, Microsoft pivoted to a "Runtime Async" experiment ("async2") to improve the existing Task model. — [dotnet/runtime#94620](https://github.com/dotnet/runtime/issues/94620); [Steven Giesel, "async2 – experiment concludes"](https://steven-giesel.com/blogPost/59752c38-9c99-4641-9853-9cfa97bb2d29)
- .NET 11, quoted: "introduces runtime-native async (Runtime Async V2), a significant step toward replacing compiler-generated async state machines with runtime-managed suspension and resumption". "Runtime Async is a preview feature" enabled with `<Features>runtime-async=on</Features>`. "The .NET runtime libraries themselves are compiled with runtime-async=on." The `DOTNET_RuntimeAsync` env vars were removed; opting out uses `<UseRuntimeAsync>false</UseRuntimeAsync>`. — [What's new in .NET 11 runtime (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)
- .NET 11 Preview 1 release notes: CoreCLR support for RuntimeAsync is enabled by default, whereas in .NET 10 it needed environment variables. — [dotnet/core .NET 11 Preview 1 runtime notes](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/preview1/runtime.md); [InfoQ](https://www.infoq.com/news/2026/02/dotnet-11-preview1)

### Inferences
- Runtime Async keeps suspended state in runtime-managed continuation objects and captures only the locals that are live. It has no public API to inspect or serialize them. Existing serialization of async state in .NET relies on user-level approaches (e.g. Durable Functions' replay, or bytecode or compiler rewriting), which is the whitepaper's niche.

### Gaps
- I found no .NET runtime feature, experimental or shipped, that provides continuation serialization.
- I did not confirm whether .NET 11 GA (expected Nov 2026) will make runtime-async the default for user code.

## Bibliographic verifications (Würthinger 2017, Agesen 1998, Bierman 2012)

### Takeaway
All three references exist and are cited correctly:
- Würthinger et al.: PLDI 2017, pp. 662–676.
- Agesen: "GC Points in a Threaded Environment", Sun Microsystems Laboratories Technical Report SMLI TR-98-70, 1998.
- Bierman et al.: "Pause 'n' Play", ECOOP 2012, LNCS 7313, pp. 233–257.

### Cited Findings
- Thomas Würthinger, Christian Wimmer, Christian Humer, Andreas Wöß, Lukas Stadler, Chris Seaton, Gilles Duboscq, Doug Simon, Matthias Grimmer. "Practical Partial Evaluation for High-Performance Dynamic Language Runtimes." PLDI 2017, pp. 662–676, DOI 10.1145/3062341.3062381. — [ACM DL](https://dl.acm.org/doi/10.1145/3062341.3062381); [PLDI 2017 page](https://pldi17.sigplan.org/details/pldi-2017-papers/49/Practical-Partial-Evaluation-for-High-Performance-Dynamic-Language-Runtimes); [DBLP PLDI 2017](https://dblp.org/db/conf/pldi/pldi2017.html)
- Ole Agesen. "GC Points in a Threaded Environment." Technical Report SMLI TR-98-70, Sun Microsystems Laboratories, Palo Alto, CA, 1998. — [Richard Jones' GC Bibliography](https://www.cs.kent.ac.uk/people/staff/rej/gcbib/gcbibA.html); [PDF mirror (Tufts)](https://www.cs.tufts.edu/~nr/cs257/archive/ole-agesen/gc-points.pdf); [ACM DL guide entry](https://dl.acm.org/doi/abs/10.5555/974974)
- Gavin Bierman, Claudio Russo, Geoffrey Mainland, Erik Meijer, Mads Torgersen. "Pause 'n' Play: Formalizing Asynchronous C#." ECOOP 2012, LNCS vol. 7313, Springer, pp. 233–257, DOI 10.1007/978-3-642-31057-7_12. — [Springer](https://link.springer.com/chapter/10.1007/978-3-642-31057-7_12)

### Inferences
- The title is capitalized in different ways ("GC points in a threaded environment" in Jones' bibliography, title case in the PDF). Either form is fine.

### Gaps
- The exact month of the Agesen TR (often given as December 1998) was not verified in this session.
