# Continuations implemented in the runtime

This note covers current systems that implement continuations inside the runtime: GraalVM Espresso, WebAssembly stack switching and JSPI, Binaryen's Asyncify (a transformation, included for comparison), Project Loom, OCaml 5 and .NET. For each it records status and whether continuations can be serialized. Of these, only Espresso serializes them.

Status is as of 17 November 2026.

## How the claims were checked

- **September 2026.** Documentation pages and papers were read directly.
- **November 2026.** Status claims and open items were re-checked. GitHub and Maven Central could be read directly, so most results come from primary files: proposal repositories, release notes, and the GitHub sources of documentation pages. Results that rest only on a search-result summary are marked "(search)".

## 1. GraalVM Espresso Continuation API

Espresso is a JVM written on Truffle. Its Continuation API copies suspended frames to the heap as Java objects. `Continuation` implements `Serializable`, so a suspended continuation can be resumed in another JVM running the same code. The feature is experimental.

Sources (text from [espresso/docs/continuations.md](https://github.com/oracle/graal/blob/master/espresso/docs/continuations.md), the source of the [GraalVM page](https://www.graalvm.org/latest/reference-manual/espresso/continuations/)):

- On suspend, "the stack is unwound and copied onto the heap as ordinary Java objects".
- "The heap objects can be serialized to resume execution in a different JVM running the same code (for example, after a restart)." "`Continuation` implements `Serializable` and can serialize to a backwards compatible format."
- "Deserializing a continuation supplied by an attacker will allow complete takeover of the JVM. Only resume continuations you persisted yourself!"
- "Serializing a continuation makes it multi-shot, meaning you can restart a continuation more than once."
- Capture works by throwing: frames are copied "into a new host-side object called a `HostFrameRecord` (HFR). The exception is then rethrown. The HFRs are chained" into a list. Prim's frame record class has the same name.
- "The continuation feature is experimental and needs to be explicitly enabled by using these options: `--experimental-options --java.Continuum=true`."
- The library is published as `org.graalvm.espresso:continuations`. The earliest version on Maven Central is 24.1.0, dated 16 September 2024. [Maven Central](https://repo1.maven.org/maven2/org/graalvm/espresso/continuations/)
- In August 2025 a security researcher showed that a forged serialized continuation gives command execution. See [preemption_and_security.md](preemption_and_security.md), section 3.

Not verified:

- No peer-reviewed paper on the Espresso Continuation API was found.

## 2. WebAssembly stack switching and JSPI

The stack-switching proposal (also called WasmFX or typed continuations) is at phase 3. Its continuations are references managed by the engine. They are one-shot and cannot be inspected, copied or serialized from Wasm. JSPI is at phase 5 and has shipped in Chrome, Edge, Firefox and Safari. [wasm-stack-switching.md](../wasm-stack-switching.md) describes the proposal's instructions.

Sources:

- Phase: "Stack Switching — Francis McCabe & Sam Lindley" is listed under phase 3. The phase 3 vote passed at the Community Group meeting of 29 October 2025. Phase 2 was reached in August 2024. [Proposals list](https://github.com/WebAssembly/proposals/blob/main/README.md); [meeting notes](https://github.com/WebAssembly/meetings/blob/main/main/2025/CG-10.md)
- Instructions: `cont.new`, `cont.bind`, `suspend`, `resume`, `resume_throw`, `resume_throw_ref`, `switch`. [Explainer](https://github.com/WebAssembly/stack-switching/blob/main/proposals/stack-switching/Explainer.md)
- "Continuations in the current proposal are single-shot (aka linear), meaning that they should be invoked exactly once… An attempt to invoke a continuation more than once results in a trap."
- Handlers are neither deep nor shallow. The paper calls them "sheep handlers": "As with deep handlers the body of a continuation is guaranteed to be wrapped in some handler. As with shallow handlers this handler can be changed each time we resume."
- Luna Phipps-Costin, Andreas Rossberg, Arjun Guha, Daan Leijen, Daniel Hillerström, KC Sivaramakrishnan, Matija Pretnar, Sam Lindley. "Continuing WebAssembly with Effect Handlers." *Proc. ACM Program. Lang.* 7(OOPSLA2), Article 238, 2023. [doi:10.1145/3622814](https://doi.org/10.1145/3622814). [arXiv 2308.08347](https://arxiv.org/abs/2308.08347)
- Wasmtime: `Config::wasm_stack_switching` first appears in 31.0.0 (March 2025). It is off by default, tier 3, and works only on x86-64 with Cranelift. The implementation has its own continuation stacks and a Cranelift `stack_switch` instruction; it does not use Wasmtime's fiber API, which the 2023 paper's prototype did. [Tracking issue #10248](https://github.com/bytecodealliance/wasmtime/issues/10248), still open; [proposal status](https://github.com/bytecodealliance/wasmtime/blob/main/docs/stability-wasm-proposals.md)
- V8 implements stack switching as an experimental feature, disabled by default (`--experimental-wasm-wasmfx`, later renamed `--wasm-wasmfx`). No browser ships it. [feature-flags.h](https://github.com/v8/v8/blob/main/src/flags/feature-flags.h)
- JSPI suspends a Wasm program when it calls a function that returns a Promise and resumes it when the Promise resolves. It shipped in Chrome 137 (27 May 2025), Edge 137, Firefox 153 (July 2026) and Safari 27 (search). [V8 blog](https://v8.dev/blog/jspi)
- Formal work: Legoupil, Pedersen, Birkedal, Lindley, Pichon-Pharabod, "Iris-WasmFX: Modular Reasoning for Wasm Stack Switching", *Proc. ACM Program. Lang.* 10(PLDI), 2026 (search).

Not verified:

- A claim from a WAW 2025 talk that native stack switching in Wasmtime is up to 6× faster than the fiber-based prototype.
- Whether SpiderMonkey or JavaScriptCore implement stack switching.

## 3. Binaryen Asyncify

Asyncify is a Binaryen pass over a whole Wasm program. It instruments functions so they can unwind, saving locals and the call position into a buffer in linear memory, and rewind, re-entering the functions and skipping code up to the resume point. It unwinds by returning. Of the systems in this note it is the closest to Prim, because it is a transformation and its saved state is visible to the program.

Sources ([Zakai, "Pause and Resume WebAssembly with Binaryen's Asyncify", 16 July 2019](https://kripken.github.io/blog/wasm/2019/07/16/asyncify.html)):

- Size: "the size overhead is in the 1-2x range, that is, the binary is around 50% larger on average." This is the worst case, in which every import and indirect call is assumed able to unwind. With a list of the imports that can unwind, and indirect calls ignored, "almost all the overhead vanishes".
- Speed: "if the binary is 20% bigger, it tends to be at worst 20% slower. However, I did notice one large outlier, SQLite, on which the slowdown is around 5x."
- Unwind and rewind: 1000 sleeps of 1 ms took 1.18 s against an ideal 1 s.
- The buffer has a header with the current position at offset 0 and the end at offset 4.
- Options that limit instrumentation: `asyncify-imports`, `asyncify-ignore-indirect`, `asyncify-onlylist`, `asyncify-removelist`, `asyncify-addlist`. [Asyncify.cpp](https://github.com/WebAssembly/binaryen/blob/main/src/passes/Asyncify.cpp). Emscripten exposes them as `ASYNCIFY_IMPORTS`, `ASYNCIFY_ONLY` and so on. [settings.js](https://github.com/emscripten-core/emscripten/blob/main/src/settings.js)

An earlier document in this repository gave Asyncify's overhead as "~30% code size, ~30% runtime". That figure is not in Zakai's post.

## 4. Project Loom

JEP 444 delivered virtual threads in JDK 21. The continuations underneath are internal and not serializable.

Sources:

- JEP 444, "Virtual Threads", Ron Pressler and Alan Bateman, delivered in JDK 21. [JEP 444](https://openjdk.org/jeps/444)
- `jdk.internal.vm.Continuation` is documented as "A one-shot delimited continuation." It implements no interfaces, so it is not `Serializable`. The package is exported only to named JDK modules. [Continuation.java](https://github.com/openjdk/jdk/blob/master/src/java.base/share/classes/jdk/internal/vm/Continuation.java); [module-info.java](https://github.com/openjdk/jdk/blob/master/src/java.base/share/classes/module-info.java)

No JEP that exposes or serializes these continuations was found.

## 5. OCaml 5

OCaml 5 continuations are one-shot. They are stacks allocated by the runtime.

Sources:

- KC Sivaramakrishnan, Stephen Dolan, Leo White, Tom Kelly, Sadiq Jaffer, Anil Madhavapeddy. "Retrofitting Effect Handlers onto OCaml." PLDI 2021, ACM, pp. 206–221. [doi:10.1145/3453483.3454039](https://doi.org/10.1145/3453483.3454039)
- Manual: "Attempting to use a continuation more than once raises a `Continuation_already_resumed` exception… Hence, OCaml does not support multi-shot continuations." [Effects chapter](https://github.com/ocaml/ocaml/blob/trunk/manual/src/refman/extensions/effects.etex)

Not verified:

- The manual does not say whether a continuation can be marshalled. No statement either way was found.

## 6. .NET

The 2023 green-threads experiment was abandoned. Runtime Async moves `async`/`await` suspension from compiler-generated state machines into the runtime. It shipped in .NET 11 on 10 November 2026 as an opt-in for user code. Neither exposes or serializes continuations.

Sources:

- Green threads: 100,000,000 minimal P/Invoke calls went from about 300 ms to about 1800 ms on a green thread, and there were conflicts with security mitigations such as shadow stacks. The issue was closed in January 2025 in favour of Runtime Async. [dotnet/runtimelab#2398](https://github.com/dotnet/runtimelab/issues/2398)
- The Runtime Async experiment ran during .NET 9. [dotnet/runtime#94620](https://github.com/dotnet/runtime/issues/94620)
- .NET 11 "introduces runtime-native async (Runtime Async V2), a significant step toward replacing compiler-generated async state machines with runtime-managed suspension and resumption". "The .NET runtime libraries themselves are compiled with runtime-async=on." User projects opt in with `<Features>runtime-async=on</Features>`; it no longer requires `EnablePreviewFeatures`. [What's new in the .NET 11 runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)
- .NET 11.0.0 was released on 10 November 2026. [Release notes](https://github.com/dotnet/core/blob/main/release-notes/11.0/README.md)
- The tracking issue for the feature is open with milestone 12.0.0. [dotnet/runtime#109632](https://github.com/dotnet/runtime/issues/109632)

No .NET runtime feature that serializes continuations was found. Durable Functions, the main .NET system that survives restarts mid-workflow, replays instead (see [durable_execution_industry.md](durable_execution_industry.md)).

## 7. References used by the whitepaper

These were checked because the whitepaper cites them.

- Thomas Würthinger, Christian Wimmer, Christian Humer, Andreas Wöß, Lukas Stadler, Chris Seaton, Gilles Duboscq, Doug Simon, Matthias Grimmer. "Practical Partial Evaluation for High-Performance Dynamic Language Runtimes." PLDI 2017, pp. 662–676. [doi:10.1145/3062341.3062381](https://doi.org/10.1145/3062341.3062381)
- Ole Agesen. "GC Points in a Threaded Environment." Sun Microsystems Laboratories Technical Report SMLI TR-98-70, December 1998. [PDF](https://www.cs.tufts.edu/~nr/cs257/archive/ole-agesen/gc-points.pdf)
- Gavin Bierman, Claudio Russo, Geoffrey Mainland, Erik Meijer, Mads Torgersen. "Pause 'n' Play: Formalizing Asynchronous C#." ECOOP 2012, LNCS 7313, pp. 233–257. [doi:10.1007/978-3-642-31057-7_12](https://doi.org/10.1007/978-3-642-31057-7_12)
