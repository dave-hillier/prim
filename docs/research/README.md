# Research

Prior work related to Prim, and how Prim differs from it.

| File | Contents |
|---|---|
| [prior-art-report.md](prior-art-report.md) | Which of Prim's techniques were published before it, what Prim and Second Life add, and what is still unconfirmed. Start here. |
| [wasm-stack-switching.md](wasm-stack-switching.md) | The WebAssembly stack-switching proposal and how it compares with Prim. |
| [notes/jvm_clr_migration_prior_art.md](notes/jvm_clr_migration_prior_art.md) | Java thread-migration systems 1998–2010, Mono.Tasklets, and the public record of Second Life's Mono engine. |
| [notes/stack_inspection_continuations.md](notes/stack_inspection_continuations.md) | Pettyjohn et al. 2005, JavaScript continuations, Scheme on the JVM and CLR, Racket and other systems that serialize continuations. |
| [notes/modern_runtime_systems.md](notes/modern_runtime_systems.md) | Espresso, WebAssembly, Asyncify, Loom, OCaml 5 and .NET runtime async, with current status. |
| [notes/preemption_and_security.md](notes/preemption_and_security.md) | Yield point placement, instruction counting, and validation of deserialized state. |
| [notes/durable_execution_industry.md](notes/durable_execution_industry.md) | Temporal, Durable Functions, Orleans, Kotlin coroutines, snapshots and game scripting. |

## How to read the notes

Each note section has a short description, a list of sources, and a list of what was not verified. Text in quotation marks is quoted from the linked source.

The research was done in two passes:

- **September 2026.** Papers and documentation were read directly where they could be downloaded. Citation metadata was checked against Crossref.
- **November 2026.** Open items and status claims were re-checked. Most publisher and blog sites could not be fetched in that pass, so many results rest on search-result summaries. Those are marked "(search)". GitHub could be read, so claims about Espresso, Wasmtime, V8, .NET, OpenJDK, Lua and Binaryen were checked against repository files.

A claim marked "(secondary)" was taken from a survey paper because the original could not be read.
