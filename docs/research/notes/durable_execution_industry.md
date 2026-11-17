# Durable, persistent and migratable execution in industry

This note covers systems that keep a computation alive across process restarts or move it between machines: replay systems, actor frameworks, Kotlin coroutines, process and Wasm snapshots, and game scripting. For each it records what is persisted and whether in-flight execution is captured.

Prim's source generator also re-runs code on resume, but it is not a replay system in the sense used here. It keeps no log of side-effect results. It restores locals and re-runs each method from its start to its yield point.

## How the claims were checked

- **September 2026.** Product documentation and papers were read directly unless marked otherwise.
- **November 2026.** Open items were re-checked by web search. No page could be fetched directly, so each result rests on a search-result summary. These are marked "(search)".

## 1. Replay systems

Temporal, Azure Durable Functions, Restate and Golem do not serialize the stack. They persist a log of the results of side effects. To resume, they run user code again from the start and feed it the recorded results. User code must therefore be deterministic. Changing the code of an in-flight workflow can break replay. The log grows with the number of steps.

### Azure Durable Functions

- "Orchestrator functions use event sourcing to ensure reliable execution and to maintain local variable state… an orchestrator function replays multiple times, and it must produce the same result each time." [Code constraints](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-code-constraints)
- Not allowed in orchestrators: `DateTime.Now`, `Guid.NewGuid()`, random numbers, direct I/O, static variables, `Task.Run`, `Task.Delay`, `Thread.Sleep`. The context supplies replacements such as `CurrentUtcDateTime` and `NewGuid()`.
- "The return values of activity functions are always safe for replay because they're saved into the orchestration history."
- The framework throws `NonDeterministicOrchestrationException` for some violations, but "this detection behavior won't catch all violations."
- JavaScript orchestrators must be generators, not `async` functions, "because the Node.js runtime doesn't guarantee deterministic behavior for `async` functions".
- "Code changes that affect running orchestrations can break replay behavior."
- Sebastian Burckhardt, Chris Gillum, David Justo, Konstantinos Kallas, Connor McMahon, Christopher S. Meiklejohn. "Durable Functions: Semantics for Stateful Serverless." *Proc. ACM Program. Lang.* 5(OOPSLA), Article 133, 2021. [doi:10.1145/3485510](https://doi.org/10.1145/3485510). The paper says record-replay persists progress without needing checkpointing support from the language runtime, and names nondeterminism and history growth as its costs (search; paraphrased, not quoted).
- Burckhardt et al. "Netherite: Efficient Execution of Serverless Workflows." *PVLDB* 15(8):1591–1604, 2022. doi:10.14778/3529337.3529344 (search).

The .NET implementation runs orchestrators as ordinary C# `async` methods on a single-threaded scheduler. It uses the compiler's state machine but never serializes it.

### Temporal

- Event History "is the source of truth for everything that happens in the Workflow". On resumption Temporal "starts the Workflow code from the beginning, replays the Event History step by step". [Workflows](https://docs.temporal.io/workflows)
- "If a generated Command doesn't match… the Workflow Execution returns a non-deterministic error." [Workflow Definition](https://docs.temporal.io/workflow-definition)
- Default history limit: 51,200 events or 50 MB, with warnings at 10,240 events or 10 MB. Self-hosted deployments can change these. [Limits](https://docs.temporal.io/workflow-execution/limits)
- Continue-As-New ends an execution and starts a new one with an empty history. The application passes its state forward as arguments. [Continue-As-New](https://docs.temporal.io/workflow-execution/continue-as-new)
- Sticky Execution keeps workflow state cached on one worker to avoid replaying the history for every task. [Sticky Execution](https://docs.temporal.io/sticky-execution)

### Restate

- "Restate tracks every step of your code execution in a journal… If your function crashes or fails, Restate replays the journal, skipping completed steps and resuming from exactly where it left off." [Docs](https://docs.restate.dev/concepts/durable_execution)
- "Use `run` to safely wrap any non-deterministic operation… Without `run()`, these operations would produce different results during replay." [Docs](https://docs.restate.dev/concepts/durable_building_blocks)

### Golem

- Golem runs WebAssembly components and records an operation log. An agent is recovered by replaying its log. [Golem 1.5](https://golem.cloud/blog/golem-1-5-the-agent-runtime/)
- Golem 1.5 (2026) adds snapshots written by the application: an agent supplies save and load functions, recovery starts from the latest snapshot, and only later log entries are replayed. The snapshot is application state, not a Wasm memory image (search). [Post](https://golem.cloud/blog/golem-1-5-features-part-6-user-defined-snapshotting/)

### Others, not examined in detail

- AWS Step Functions defines workflows in Amazon States Language, a JSON format. The state is data and no user code is replayed (first half confirmed by search).
- AWS Lambda durable functions (announced December 2025), Cloudflare Workflows and DBOS checkpoint per step (search).

Not verified:

- Verbatim text from the Burckhardt paper.
- Netflix Conductor. Whether Temporal has a Kotlin SDK.

Both replay and continuation serialization break when code changes: a replay log is invalid if the sequence of commands changes, and a serialized continuation is invalid if yield point numbers or slot layouts change.

## 2. Orleans

Orleans persists and migrates grain state. It does not capture a call that is executing.

- Philip Bernstein, Sergey Bykov, Alan Geller, Gabriel Kliot, Jorgen Thelin. "Orleans: Distributed Virtual Actors for Programmability and Scalability." Microsoft Research MSR-TR-2014-41, 2014. [Page](https://www.microsoft.com/en-us/research/publication/orleans-distributed-virtual-actors-for-programmability-and-scalability/)
- Sergey Bykov, Alan Geller, Gabriel Kliot, James Larus, Ravi Pandya, Jorgen Thelin. "Orleans: Cloud Computing for Everyone." SoCC 2011, pp. 1–14. doi:10.1145/2038916.2038932 (search).
- Live grain migration arrived in Orleans 8: it "allows grain activations to move from one silo to another while preserving their in-memory state". "The grain's in-memory state (application state, enqueued messages, metadata) is dehydrated into a migration packet." [Grain lifecycle](https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-lifecycle); [What's new in Orleans 8](https://devblogs.microsoft.com/dotnet/whats-new-in-orleans-8/)
- Migration happens "when no requests are executing". Executing requests finish first. Queued requests move with the grain. [Design issue #7692](https://github.com/dotnet/orleans/issues/7692) (search)

## 3. Kotlin coroutines

Kotlin compiles a suspend function into a state machine object with a label field and fields for the locals that live across suspension points. That is the same information Prim saves. kotlinx.coroutines has not made these objects serializable.

- Roman Elizarov, Mikhail Belyaev, Marat Akhin, Ilmir Usmanov. "Kotlin Coroutines: Design and Implementation." Onward! 2021, ACM. [doi:10.1145/3486607.3486751](https://doi.org/10.1145/3486607.3486751)
- "A suspending function is compiled to a state machine, where states correspond to suspension points." "Resuming the same continuation more than once is not allowed and produces `IllegalStateException`." [KEEP](https://github.com/Kotlin/KEEP/blob/master/proposals/coroutines.md)
- Issue "Serializability of coroutine classes", opened by Elizarov on 20 June 2017: the object graph reaches `StandaloneCoroutine` and contexts that "are not currently serializable". [kotlinx.coroutines#76](https://github.com/Kotlin/kotlinx.coroutines/issues/76). It was open in September 2026. Its state was not re-confirmed in November.

The state machine object alone would be easy to serialize. The references it holds to the job, dispatcher and context are the obstacle. C# `async` state machines have the same problem. Prim avoids it by generating its own frame records.

## 4. Snapshots

A snapshot captures a whole process or VM image. User code needs no changes, but the image is tied to a runtime and often an architecture.

- CRIU checkpoints Linux processes and containers, including memory, file descriptors and TCP connections. [Repository](https://github.com/checkpoint-restore/criu). It cannot dump processes with open device files or ptraced tasks. [What cannot be checkpointed](https://www.criu.org/What_cannot_be_checkpointed) (search)
- Wizer runs a Wasm module's initialization function and writes the resulting memory and globals into a new module. It captures the heap after initialization, not a suspended stack. Development has moved into the Wasmtime repository (search). [Repository](https://github.com/bytecodealliance/wizer)
- Cloudflare Durable Objects: "after a brief period of inactivity, the Durable Object will be evicted, and all in-memory state will be lost". Hibernation happens only when no request is being processed and no timer is pending. [Lifecycle](https://developers.cloudflare.com/durable-objects/concepts/durable-object-lifecycle/)

Wasm migration research transforms code to save locals at chosen points, because native stacks are not portable between hosts. This is the same approach as Prim's:

- Yiwei Yang et al. "Transparent and Efficient Live Migration across Heterogeneous Hosts with Wharf." [arXiv 2410.15894](https://arxiv.org/abs/2410.15894), 2024. Built on the WAMR ahead-of-time compiler; checkpoints at designated places.
- Daigo Fujii, Katsuya Matsubara, Yuki Nakata. "Stateful VM Migration Among Heterogeneous WebAssembly Runtimes for Efficient Edge-cloud Collaborations." EdgeSys '24, pp. 19–24. [doi:10.1145/3642968.3654816](https://doi.org/10.1145/3642968.3654816)
- E. Tinto, L. Marchiori, T. Vardanega. "Live migration of compiled Wasm modules across the Compute Continuum." *Journal of Systems Architecture* 168, 103532, 2025. [doi:10.1016/j.sysarc.2025.103532](https://doi.org/10.1016/j.sysarc.2025.103532). Injects checkpoint and restore procedures into the bytecode of each function (search).
- Wanco, a Wasm ahead-of-time compiler with checkpoint and restore across instruction sets. MPLR '25, doi:10.1145/3759426.3760985 (search; not read).

## 5. Game scripting

Unity and Godot coroutines are not serializable, so games save state by hand. Systems that do persist suspended scripts either own the VM (Lua with Eris, Ink, LSL2) or rewrite bytecode (Second Life on Mono).

- Eris "can serialize almost anything you can have in a Lua VM and can unserialize it again at a later point, even in a different VM", including yielded coroutines. It depends on the internals of the reference Lua VM and does not work with LuaJIT. Author: Florian Nuecke. [Repository](https://github.com/fnuecke/eris). OpenComputers uses a fork. [OC-Eris](https://github.com/MightyPirates/OC-Eris)
- Ink: `story.state.ToJson()` and `LoadJson()`. The saved state includes "the call stack (for tunnels, functions, etc)". [Third-party write-up](https://videlais.com/2022/02/11/ink-unity-story-saving-and-restoring-using-json-serialization/)
- Yarn Spinner saves the current node and the variable storage, and restarts at the start of that node. It does not resume in the middle of a node (search). [FAQ](https://docs.yarnspinner.dev/faq)
- Godot 3's `yield` returned a `GDScriptFunctionState` that could be resumed. Godot 4's `await` does not expose it. [Proposal #3469](https://github.com/godotengine/godot-proposals/issues/3469)
- Second Life: the LSL2 VM kept all of a script's state in one 16 KB block, which could be copied as it was. On Mono, assemblies were rewritten to save and restore frames. See [jvm_clr_migration_prior_art.md](jvm_clr_migration_prior_art.md), section 7.

Not verified:

- That Unity coroutines and Godot 3 function states cannot be serialized. Both are commonly stated; no primary source was found.
- A sentence previously quoted here from the Second Life wiki ("Serializing a mono script is a lot more work than serializing an LSL2 script…") could not be found on the wiki in November and has been removed.

## Summary

| Approach | What is persisted | How it resumes | What it requires of user code | Examples |
|---|---|---|---|---|
| Replay | Log of side-effect results | Run code from the start, substituting recorded results | Deterministic; side effects only through framework APIs; versioning discipline; bounded history | Temporal, Durable Functions, Restate, Golem |
| Actor state | Explicit fields | Reactivate the object; no execution in flight | Work between awaits must be restartable; moves only when idle | Orleans, Cloudflare Durable Objects |
| Process or VM snapshot | Memory image | Restore the image | Nothing, but tied to runtime, kernel or architecture | CRIU, Wizer (initialization only) |
| Coroutines serialized by the VM | Interpreter frames and heap | Load into the same VM | Must run in that VM; native functions cannot be saved | Lua with Eris or Pluto, Ink, LSL2, Stackless Python |
| Program transformation | Locals, evaluation stack, resume point, reachable heap | Re-enter each method and jump to the resume point | Captured values serializable; same code version | Second Life on Mono, Javaflow, Racket stateless servlets, Wasm checkpoint injection, Prim |
