# Durable / persistent / migratable execution in industry vs. serializable continuations

Scope: replay/event-sourcing systems (Temporal/Cadence, Azure Durable Functions/DTFx, Restate, Golem), actor/state-persistence systems (Orleans, Cloudflare Durable Objects), language-level coroutines (Kotlin), snapshot systems (CRIU, Wasm), and game scripting (Eris/Lua, Ink, Unity, Godot, Second Life Mono). Framing for Prim: Prim's source-generator path "replays the method body from the top over restored locals" — this is *not* history replay (no event log of side-effect results); it is re-entry into a transformed method with a resume point, which is structurally closest to Second Life's Mono rewriting and Kotlin's state machine, while sharing Temporal/DF's "re-execute from the top" surface shape.

## 1. Replay / event-sourcing systems (Temporal/Cadence, Azure Durable Functions / Durable Task Framework, Restate, Golem)

### Takeaway
Industry "durable execution" overwhelmingly does NOT serialize the stack: it persists an append-only log of side-effect results (event history / journal / oplog) and reconstructs in-memory execution state by re-running user code from the start, feeding recorded results back. This forces a determinism contract on user code and makes code changes to in-flight workflows a versioning hazard; the persisted artifact grows with the number of steps (hence history limits and Continue-As-New).

### Cited Findings
**Azure Durable Functions / Durable Task Framework (DTFx)**
- "Orchestrator functions use event sourcing to ensure reliable execution and to maintain local variable state. The replay behavior of orchestrator code creates constraints ... orchestrator functions must be *deterministic*: an orchestrator function replays multiple times, and it must produce the same result each time." — [MS Learn: Durable orchestrator code constraints](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-code-constraints)
- Forbidden/replaced in orchestrators: `DateTime.Now/UtcNow`, `Stopwatch` (use `context.CurrentUtcDateTime`); `Guid.NewGuid()` (use `context.NewGuid()`, which yields Type 5 name-based UUIDs); random numbers (use an activity, or fixed-seed RNG); bindings/direct I/O, HTTP (move to activities); static variables; environment variables; `Task.Run`, `Task.Delay`, `HttpClient.SendAsync`; `Thread.Sleep` (use durable timers); `ConfigureAwait(false)`. — [same page](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-code-constraints)
- "The return values of activity functions are always safe for replay because they're saved into the orchestration history." — [same page](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-code-constraints)
- Implementation: durable tasks are backed by a list of `TaskCompletionSource` objects; "During replay, orchestrator code creates these tasks. The dispatcher completes them as it enumerates the corresponding history events. The runtime executes the tasks synchronously on a single thread until it replays the history." The framework throws `NonDeterministicOrchestrationException` on some detected violations but "this detection behavior won't catch all violations." — [same page](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-code-constraints); source: [TaskOrchestrationExecutor.cs](https://github.com/Azure/durabletask/blob/master/src/DurableTask.Core/TaskOrchestrationExecutor.cs)
- Language-level consequence: JavaScript orchestrators must be synchronous generators, not `async`, "because the Node.js runtime doesn't guarantee deterministic behavior for `async` functions"; Python orchestrators must be generators because "coroutine semantics don't align with the Durable Functions replay model." — [same page](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-code-constraints)
- Subtle .NET pitfall: re-enumerating a deferred LINQ query that calls `CallActivityAsync` schedules new durable tasks and can hang the orchestrator. — [same page](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-code-constraints)
- Versioning: "Code changes that affect running orchestrations can break replay behavior"; strategies include side-by-side deployment and version-specific task hub names. — [same page](https://learn.microsoft.com/en-us/azure/durable-task/common/durable-task-code-constraints)
- **Verified citation:** Sebastian Burckhardt, Chris Gillum, David Justo, Konstantinos Kallas, Connor McMahon, Christopher S. Meiklejohn. "Durable Functions: Semantics for Stateful Serverless." Proc. ACM Program. Lang. 5 (OOPSLA), Article 133, October 2021. DOI [10.1145/3485510](https://dl.acm.org/doi/10.1145/3485510). Abstract: provides a formal model grounded in lambda calculus, explains how the runtime achieves progress persistence "through record-replay", defines two progressively more sophisticated execution models and proves equivalence to the high-level spec. — [MSR publication page](https://www.microsoft.com/en-us/research/publication/durable-functions-semantics-for-stateful-serverless/); [PDF](https://www.microsoft.com/en-us/research/wp-content/uploads/2021/10/DF-Semantics-Final.pdf); also presented in PLDI 2022 SIGPLAN track ([PLDI'22](https://pldi22.sigplan.org/details/pldi-2022-sigplan-track/66/-OOPSLA-2021-Durable-functions-semantics-for-stateful-serverless))

**Temporal (Cadence lineage)**
- Event History "is the source of truth for everything that happens in the Workflow." On resumption Temporal "starts the Workflow code from the beginning, replays the Event History step by step" rather than restoring memory snapshots. Workflow code "has to make the same decisions when given the same history. It shouldn't depend on any values *not* recorded in the history." — [Temporal docs: Workflows](https://docs.temporal.io/workflows)
- "When the Workflow's code replays, the Commands that are emitted are compared with the existing Event History. ... If a generated Command doesn't match ... the Workflow Execution returns a *non-deterministic* error." Determinism = "makes the same Workflow API calls in the same sequence, given the same input." Code changes require Workflow Versioning. — [Temporal docs: Workflow Definition](https://docs.temporal.io/workflow-definition)
- Event History limited to 51,200 events or 50 MB (warning at 10,240 events / 10 MB). — [Temporal: Workflow Execution limits](https://docs.temporal.io/workflow-execution/limits)
- Continue-As-New completes the execution and starts a new one with fresh history to avoid these limits (i.e. the application must manually carry forward a serializable summary of state). — [Temporal: Continue-As-New](https://docs.temporal.io/design-patterns/continue-as-new)
- Sticky Execution: Workflow Tasks are routed to the same Worker, which "caches the Workflow state in memory ... reducing the need to reconstruct the Workflow from its Event History for every Task." — [Temporal: Sticky Execution](https://docs.temporal.io/sticky-execution)

**Restate**
- "Restate tracks every step of your code execution in a journal ... records both the operation and its result. ... If your function crashes or fails, Restate replays the journal, skipping completed steps and resuming from exactly where it left off." Functions can be *suspended* while waiting, enabling long workflows on FaaS "without paying for wait time." State updates "are recorded alongside execution steps." — [Restate: Durable execution](https://docs.restate.dev/concepts/durable_execution)
- "Use `run` to safely wrap any non-deterministic operation ... and have Restate persist its result. ... Without `run()`, these operations would produce different results during replay." Virtual Objects/Workflows get persistent K/V state. — [Restate: Durable building blocks](https://docs.restate.dev/concepts/durable_building_blocks)

**Golem Cloud (durable WebAssembly)**
- Golem is an open-source, Wasm-based durable runtime; workers are persisted via an operation log ("oplog") and "resumed deterministically by replaying its own oplog"; idle agents suspend to zero. Workers can be interrupted, resumed, updated, reverted, forked. — [Golem blog: Golem 1.5](https://golem.cloud/blog/golem-1-5-the-agent-runtime/); [golemcloud/golem PRs on replay](https://github.com/golemcloud/golem/pull/3882); secondary summary: [API Evangelist profile](https://github.com/api-evangelist/golem-cloud)

### Inferences
- The persisted artifact in replay systems is a *log of effects* (inputs/results), not a continuation; the continuation is re-derived by execution. Cost of resume is O(history length) (mitigated by in-memory caching: Temporal sticky execution), and history size is bounded (Temporal 51,200 events/50 MB), forcing Continue-As-New.
- Determinism constraints (no clock, GUID, RNG, I/O, statics, env vars, threads, non-framework awaits) are the price of replay. A serializable-continuation approach that captures locals + resume point does not need side-effect results to be re-derived, so it does not in principle require determinism of code *before* the suspension point — but Prim's "replay the method body from the top over restored locals" variant does re-execute some code on resume, so the paper should state precisely which statements re-run (and thus which must be idempotent/pure) — this is the key point of contrast/overlap to articulate.
- DF's .NET implementation is notable for Prim: it already rides on C# `async`/`Task` with a custom single-threaded scheduler, i.e. it reuses the C# compiler's state machine but never serializes it — it replays instead. This is direct evidence that the .NET async state machine is treated as non-serializable in production practice.
- Both replay and continuation serialization share the code-versioning problem: a replay log becomes invalid if the command sequence changes; a serialized continuation becomes invalid if the resume-point numbering/local layout changes.

### Gaps
- Could not extract full text of the Burckhardt et al. PDF (tooling limits), so no verbatim quotes beyond the abstract summary; whether the paper explicitly contrasts record-replay with stack checkpointing is unverified.
- Cadence (Uber) docs and Temporal's lineage from Cadence not directly fetched; the "Cadence lineage" statement is from general knowledge.
- AWS Step Functions and Netflix Conductor were not researched in this pass (no sources fetched). Both are generally known to model workflows as declarative state machines/JSON definitions whose state is data (no user-code replay), but this needs citation before use.
- Netherite (Burckhardt et al., VLDB 2022) backend for DF not verified.

## 2. Microsoft Orleans: grain state vs. execution state; migration

### Takeaway
Orleans persists and migrates *grain (object) state*, not in-flight execution. Live grain migration (Orleans 8+) dehydrates in-memory state only when the grain is idle; a request that is mid-`await` is not captured.

### Cited Findings
- Verified citation: Philip A. Bernstein, Sergey Bykov, Alan Geller, Gabriel Kliot, Jorgen Thelin. "Orleans: Distributed Virtual Actors for Programmability and Scalability." Microsoft Research Technical Report MSR-TR-2014-41, 2014. — [MSR page](https://www.microsoft.com/en-us/research/publication/orleans-distributed-virtual-actors-for-programmability-and-scalability/); [PDF](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/Orleans-MSR-TR-2014-41.pdf)
- Grain lifecycle stage `SetupState` is where Orleans loads `IStorage<TState>.State` from storage; grains "aren't always deactivated during some error cases (such as silo crashes)" so apps shouldn't rely on deactivation hooks. — [MS Learn: Grain lifecycle](https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-lifecycle)
- Grain migration (Orleans 8/9/10): "allows grain activations to move from one silo to another while preserving their in-memory state" via Dehydration → Transfer → Rehydration (before `OnActivateAsync`). Triggered by `this.MigrateOnIdle()`, or the experimental activation repartitioner/rebalancer. Grains implement `IGrainMigrationParticipant { OnDehydrate(IDehydrationContext); OnRehydrate(IRehydrationContext); }` or use `Grain<TState>`/`IPersistentState<T>` for automatic support. Stateless workers, system targets, grain services and client grains cannot migrate. — [MS Learn: Grain lifecycle](https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-lifecycle)
- "The grain's in-memory state (application state, enqueued messages, metadata) is dehydrated into a migration packet"; dehydration occurs after `OnDeactivateAsync`. — [.NET Blog: What's new in Orleans 8](https://devblogs.microsoft.com/dotnet/whats-new-in-orleans-8/); implementation PR [dotnet/orleans#8452 "Live grain migration"](https://github.com/dotnet/orleans/pull/8452)

### Inferences
- Migration is "on idle": the unit of migration is the actor *between* turns. Execution state inside a turn (async state machines on the stack/heap) is never captured — long-running logic must be decomposed into turns with explicit state, or delegated to a DF/Temporal-style replay layer. This is exactly the gap serializable continuations address (migrating a grain *mid-method*).
- Enqueued messages are migrated, which is analogous to Second Life migrating the script's event queue alongside its continuation.

### Gaps
- Did not find any Orleans feature for migrating an in-flight request; absence is inferred from the "MigrateOnIdle" design, not an explicit doc statement.
- Bykov et al. "Orleans: cloud computing for everyone" (SoCC 2011) not verified in this pass.

## 3. Kotlin coroutines: state-machine transform and serializability

### Takeaway
Kotlin compiles suspend functions into a state-machine continuation object whose fields hold a label and spilled locals — structurally the same thing Prim needs — but kotlinx.coroutines has never made continuations serializable; the request has been open since 2017.

### Cited Findings
- Verified citation: Roman Elizarov, Mikhail Belyaev, Marat Akhin, Ilmir Usmanov. "Kotlin coroutines: design and implementation." Onward! '21 (ACM SIGPLAN Int'l Symposium on New Ideas, New Paradigms, and Reflections on Programming and Software), Chicago, Oct 20–22 2021. DOI [10.1145/3486607.3486751](https://dl.acm.org/doi/10.1145/3486607.3486751). — [Semantic Scholar](https://www.semanticscholar.org/paper/Kotlin-coroutines:-design-and-implementation-Elizarov-Beliaev/0113bac81892215e87a7fd47f89fa30dadabc9e8)
- KEEP design doc: "a suspending function is compiled to a state machine, where states correspond to suspension points"; the generated class has "a field holding the current state of the state machine, and fields for local variables of the coroutine that are shared between states." Continuations are one-shot: "Resuming the same continuation more than once is not allowed and produces `IllegalStateException`"; multi-shot could be done as a library by "cloning the state of the coroutine that is captured in continuation." — [Kotlin KEEP: coroutines.md](https://github.com/Kotlin/KEEP/blob/master/proposals/coroutines.md)
- Issue "Serializability of coroutine classes" opened by Roman Elizarov, 20 June 2017: with Java Serialization, the reference graph reaches `StandaloneCoroutine` and contexts such as `CommonPool` which "are not currently serializable. They should be made properly serializable." Still open. — [kotlinx.coroutines#76](https://github.com/Kotlin/kotlinx.coroutines/issues/76); related [kotlinx.serialization#44 "Serialization of closures and continuations"](https://github.com/Kotlin/kotlinx.serialization/issues/44)

### Inferences
- The Kotlin state-machine object is "almost" serializable (label + spilled locals), but the continuation chain drags in runtime objects (Job, dispatcher, context) — the same problem as C#'s `AsyncStateMachineBuilder`/`Task`/`SynchronizationContext`. Prim's approach of generating its own frame representation sidesteps that runtime graph; this is a clean point of comparison.

### Gaps
- No verified production library that persists Kotlin continuations to disk found. Temporal's Java/Kotlin SDKs use replay (not continuation serialization) per Section 1, but the Kotlin SDK specifically was not checked.
- Full text of the Onward! paper not accessed (ACM returned 403).

## 4. Snapshot-based approaches (CRIU, Wasm snapshot/migration, Cloudflare Durable Objects)

### Takeaway
Snapshotting captures the whole machine/process/VM image, which is transparent to user code (no determinism constraints) but is coarse-grained, runtime/architecture-coupled, and not a portable language-level value. Cloudflare Durable Objects explicitly do NOT persist execution: in-memory state is lost on eviction/hibernation.

### Cited Findings
- CRIU checkpoints/restores Linux processes/containers including memory, fds and network connections; TCP sockets are restored without breaking the connection via kernel "TCP repair mode" (libsoccr); the phaul sub-project provides live migration. — [CRIU GitHub](https://github.com/checkpoint-restore/criu); [Wikipedia: CRIU](https://en.wikipedia.org/wiki/CRIU)
- Wizer "instantiates your WebAssembly module, executes its initialization function, and then snapshots the initialized state out into a new WebAssembly module" (globals + memory data segments); initialization may not call imported functions; reported 1.35–6.00× faster startup. It snapshots *post-init heap*, not a suspended stack. — [bytecodealliance/wizer](https://github.com/bytecodealliance/wizer)
- Wasm live migration research: Wharf (WAMR AOT) commits LLVM values to a Wasm auxiliary stack at "checkpointable places" — [arXiv 2410.15894](https://arxiv.org/html/2410.15894v1); stateful migration between WasmEdge and WAMR — [EdgeSys '24, DOI 10.1145/3642968.3654816](https://dl.acm.org/doi/10.1145/3642968.3654816); injecting checkpoint/restore procedures into function bytecode of compiled Wasm modules — [J. Systems Architecture 2025 (Tinto et al.)](https://www.sciencedirect.com/science/article/pii/S1383762125002048)
- Cloudflare Durable Objects: in-memory state "can be retained between requests, but after a brief period of inactivity, the Durable Object will be evicted, and all in-memory state will be lost"; durable state must go through the Storage API. Hibernation (after ~10 s idle) only occurs with no pending timers, no in-progress awaited `fetch()`, and no request still being processed; in-memory state is not preserved across hibernation. — [Cloudflare: In-memory state](https://developers.cloudflare.com/durable-objects/reference/in-memory-state/); [Durable Object lifecycle](https://developers.cloudflare.com/durable-objects/concepts/durable-object-lifecycle/)

### Inferences
- The Wasm migration literature has converged on *code transformation to spill locals at designated points* (Wharf's checkpointable places; bytecode-injected checkpoint/restore) because raw native stacks are not portable across heterogeneous hosts — the same argument Prim makes for .NET.
- Durable Objects, like Orleans, hibernate only when no execution is in flight — the platform explicitly refuses to capture a pending `await`.

### Gaps
- V8 startup snapshots and Golem's use of snapshots (if any) vs pure oplog replay were not verified.
- CRIU limitations (e.g. same kernel/CPU features, external resources like GPUs) not verified from primary docs.

## 5. Game engines / scripting systems

### Takeaway
Mainstream engine coroutines (Unity `IEnumerator`, Godot `await`) are not serializable, so save games require hand-written state; systems that do persist suspended script execution either own the VM (Lua+Eris, Ink's JSON call stack, LSL2's fixed 16 KB image) or rewrite bytecode (Second Life Mono) — the latter being the closest prior art to Prim on .NET.

### Cited Findings
- **Eris (Lua 5.2/5.3):** "can serialize almost anything you can have in a Lua VM and can unserialize it again at a later point, even in a different VM", including yielded coroutines — "very handy for saving long running, multi-session scripting systems, for example in games ... the scripts won't even know the game was quit inbetween"; also migration to another machine (load-balancing). It is a rewrite of Pluto (Lua 5.1) and relies on vanilla Lua internals (won't work with LuaJIT). — [fnuecke/eris README](https://github.com/fnuecke/eris/blob/master/README.md); forks used by Terasology ([MovingBlocks/eris](https://github.com/MovingBlocks/eris)) and OpenComputers ([MightyPirates/OC-Eris](https://github.com/MightyPirates/OC-Eris)); Pluto coroutine persistence example — [Mike Hadlow, 2013](http://mikehadlow.blogspot.com/2013/04/serializing-lua-coroutines-with-pluto.html)
- **Ink (inkle):** `story.state.ToJson()` / `LoadJson()`; StoryState records "global variables, read counts, the pointer to the current point in the story, the call stack (for tunnels, functions, etc)". — [videlais.com: ink + Unity saving](https://videlais.com/2022/02/11/ink-unity-story-saving-and-restoring-using-json-serialization/); [inkle/ink#374](https://github.com/inkle/ink/issues/374)
- **Godot:** Godot 3 `yield` returned a `GDScriptFunctionState` object that could be `resume()`d; Godot 4 `await` removed user access to it. — [godot-proposals#3469](https://github.com/godotengine/godot-proposals/issues/3469); [godot-proposals#5673](https://github.com/godotengine/godot-proposals/issues/5673)
- **Second Life (Linden Lab) Mono script migration** (author: Dave Hillier, who worked on it): LSL2 VM kept all script state in a 16 KB block (so effectively already serialized); CLR does not expose stack/PC, so assemblies were rewritten (using RAIL, similar to Mono.Cecil): "At the start of each method, a block of code is injected to do the restore. It get the saved frame and restores the local variables and the state of the stack. It then jumps to the part of the method that was previously executed." Yield points inserted at backward jumps; heap objects reachable from frames/Script object and the message queue are serialized and restored at the destination simulator. Cites Java mobile-agent work Brakes and JavaGoX as influences. — [Dave's Blog: "IIRC: Second Life Mono internals" (2015)](https://davehillier.net/2015/03/11/second-life-mono-internals/)
- Linden Lab wiki: region crossing/teleport moves script execution to the new simulator; "Serializing a mono script is a lot more work than serializing an LSL2 script, because the LSL2 script is basically serialized already." — [Second Life Wiki: Mono](https://wiki.secondlife.com/wiki/Mono); [Mono/Beta FAQ](https://wiki.secondlife.com/wiki/Mono/Beta_FAQ)

### Inferences
- Second Life Mono is a direct .NET precedent for Prim: bytecode-level (vs. Prim's source-level) injection of save/restore with a jump table on method entry and yield points at loop back-edges — i.e. serializable continuations by code transformation, deployed at scale for migration, not replay. It restores the evaluation stack as well as locals (Prim at source level only needs locals + resume point).
- Unity `IEnumerator` coroutines are C#-compiler-generated state machines (like `async`), whose private generated fields are not designed for serialization — hence ad-hoc save-state in games. (Needs primary citation; see Gaps.)

### Gaps
- No primary Unity documentation found stating coroutines cannot be serialized; the claim is widely known but uncited here.
- The claim that Godot 3 `GDScriptFunctionState` could not be serialized came from a search-summarizer, not a primary source; treat as unverified.
- Yarn Spinner state saving not researched.
- No peer-reviewed Linden Lab publication located; the Lang.NET/other talks on Mono in SL not verified. Brakes (Truyen et al.) and JavaGoX (Sekiguchi et al.) citations need to be verified separately.

## Summary comparison (for the related-work table)

| Approach | What is persisted | Resume mechanism | Constraints on user code | Examples |
|---|---|---|---|---|
| Event-sourcing / replay | Ordered log of effect results (history/journal/oplog) + inputs | Re-run code from start, feed recorded results | Determinism of orchestrator code; side effects only via framework APIs (activities/`ctx.run`); versioning via patch APIs; history size limits | Temporal/Cadence, Durable Functions/DTFx, Restate, Golem |
| Object/actor state persistence | Explicit fields / state record | Re-activate object; no in-flight execution | Logic between awaits must be restartable; migration/hibernation only when idle | Orleans (incl. 8+ live migration), Cloudflare Durable Objects |
| Process / VM snapshot | Entire memory image (+ fds, sockets) | Restore image | Transparent, but runtime/arch/kernel-coupled; coarse; not a language value | CRIU, Wizer (init-only), WAMR/Wharf research |
| VM-owned serializable coroutines | Interpreter frames/heap | Unpersist into VM | Must run in that VM; natives/C functions problematic | Lua+Eris/Pluto, Ink, LSL2 |
| Compiler/bytecode transform to serializable continuation | Locals (+eval stack) + resume label + reachable heap | Re-enter method, jump to resume point | Captured locals must be serializable; code version must match; re-executed prelude (if any) must be idempotent | Second Life Mono, Wasm bytecode-injection research, Kotlin state machine (not serializable in practice), **Prim** |
