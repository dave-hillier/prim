# Preemption by injected yield points, and validating deserialized continuation state

This note covers prior work on three things Prim does: placing yield points so that a thread reaches one in bounded time, counting instructions to limit untrusted code, and checking continuation state that arrives from outside.

## How the claims were checked

- **September 2026.** Papers and documentation were read directly unless marked otherwise.
- **November 2026.** Open items were re-checked. Public GitHub repositories could be read directly, so the Espresso, Wasmtime, Lua and Stopify results come from source files. Other results rest on search-result summaries and are marked "(search)".

## 1. Where yield points go

Virtual machines put polls on loop back-edges and in method prologues or epilogues so that a thread reaches a poll in bounded time. This was established practice before Second Life.

Sources:

- Marc Feeley. "Polling Efficiently on Stock Hardware." FPCA '93, ACM, pp. 179–187. [doi:10.1145/165180.165205](https://doi.org/10.1145/165180.165205). The ACM record and Feeley's publication list give 179–187; DBLP gives 179–190 (search).
- Ole Agesen. "GC Points in a Threaded Environment." Sun Microsystems Laboratories SMLI TR-98-70, December 1998. Compares polling with code patching for GC safepoints. [PDF](https://www.cs.tufts.edu/~nr/cs257/archive/ole-agesen/gc-points.pdf)
- B. Alpern et al. "The Jalapeño Virtual Machine." *IBM Systems Journal* 39(1):211–238, 2000. [doi:10.1147/sj.391.0211](https://doi.org/10.1147/sj.391.0211). The paper compiles yield points into method prologues and says they will also be needed on the back edges of loops (search; the wording seen was fragmentary). Its successor, Jikes RVM, has both.
- Yi Lin, Kunshan Wang, Stephen Blackburn, Antony Hosking, Michael Norrish. "Stop and Go: Understanding Yieldpoint Behavior." ISMM 2015, ACM, pp. 70–80. [doi:10.1145/2754169.2754187](https://doi.org/10.1145/2754169.2754187). [PDF](https://www.steveblackburn.org/pubs/papers/yieldpoint-ismm-2015.pdf)
  - "To avoid unbounded waits, yieldpoints typically occur on loop backedges and on method prologs or epilogs of the application, either in the interpreter or in code placed there by the compiler."
  - Conditional yieldpoints on back-edges, prologues and epilogues grew machine code by 7.2% (13.6 MB to 14.6 MB).
- Second Life: "Backwards jump is one of the places to insert a yield." [Hillier 2015](https://davehillier.net/2015/03/11/second-life-mono-internals/). The Second Life wiki says the injector "searches through the script assembly finding points where the script should yield and inject extra opcodes that copy the stack into a heap object". [SL Wiki: Mono](https://wiki.secondlife.com/wiki/Mono)

A VM safepoint parks the native thread. A Prim yield point unwinds the stack into serializable frame records. The placement is the same; what happens at the poll differs.

A method that recurses without looping never takes a backward branch. A poll in the method prologue covers that case.

## 2. Instruction counting

J-RAF2 (Binder and Hulaas, 2004–2008) rewrites Java bytecode so that each thread counts the bytecodes it executes and polls a threshold. It is the closest prior work to Prim's instruction budget. When the budget runs out, J-RAF2 can delay or terminate the thread. Prim suspends the computation into a continuation that can be saved or moved.

Sources:

- Jarle Hulaas, Walter Binder. "Program Transformations for Light-Weight CPU Accounting and Control in the Java Virtual Machine: A Systematic Review." *Higher-Order and Symbolic Computation* 21(1–2):119–146, 2008. [doi:10.1007/s10990-008-9026-4](https://doi.org/10.1007/s10990-008-9026-4). [Author PDF](https://hulaas.com/jraf2/publications/HOSC08.pdf)
  - Each method is rewritten to insert `if (cpu.consumption >= 0) cpu.triggerConsume();` at the start of the method and before return or throw, at the start of each exception handler, at the start of each loop, and on any path longer than MAXPATH bytecodes.
  - In `consume`, "the thread may terminate the component if there is a hard limit on the total number of bytecode instructions it may execute, or it may delay itself (i.e., put itself to sleep)."
  - The PDF text was extracted with spaces lost, so the list of sites is a close paraphrase. The November pass confirmed the conditional, the handler site, the method-start site and MAXPATH (search).
- Walter Binder. "Using Bytecode Instruction Counting as Portable CPU Consumption Metric." ENTCS 153. [PDF](https://www.inf.usi.ch/faculty/binder/documents/entcs153.pdf)
- Lua: `lua_sethook` with `LUA_MASKCOUNT` calls a hook every N VM instructions. It first appears in Lua 5.0 (2003); Lua 4.0 has only call and line hooks. [lua.h at v5.0](https://raw.githubusercontent.com/lua/lua/v5.0/lua.h)
- Ethereum: execution consumes gas, and running out ends execution and reverts state. [Yellow Paper](https://ethereum.github.io/yellowpaper/paper.pdf)
- Wasm metering by injection: tools compute a cost per basic block and insert a charge at the start of each block. [wasm-instrument](https://github.com/paritytech/wasm-instrument); [wasm-metering](https://github.com/ewasm/wasm-metering)
- Wasmtime offers two mechanisms. Fuel is a counter: "Fuel, in contrast, should be used when *deterministic* yielding or trapping is needed." Epoch interruption checks a counter the host increments, and "is based on wall-time rather than an exact count of how many Wasm instructions are executed". The documentation puts epoch overhead near 10% and fuel higher. [config.rs](https://github.com/bytecodealliance/wasmtime/blob/main/crates/wasmtime/src/config.rs); [Interrupting execution](https://github.com/bytecodealliance/wasmtime/blob/main/docs/examples-interrupting-wasm.md)
- eBPF takes a different route: the verifier proves termination statically and there is no preemption at run time. [LWN](https://lwn.net/Articles/794934/)
- Stopify (2018) instruments every function and loop to save the continuation and yield to the browser's event loop. Its repository has several estimators for when to yield, including a countdown and a sampled velocity estimate. [Repository](https://github.com/nuprl/Stopify). It postdates Second Life.

### Second Life's counter

No public source documents an instruction counter in Second Life's Mono engine. Hillier's 2015 post mentions yields at backward jumps and does not mention counting. The whitepaper and the blog draft describe a counter from the author's memory.

The older LSL2 VM did slice by instruction count: Hillier's companion post says each script got a slot in the main loop to execute a number of instructions before the next script ran. [Hillier, "IIRC: User Scripting in Second Life", 2015](https://davehillier.net/2015/03/10/user-scripting-in-second-life/) (search)

## 3. Validating deserialized continuation state

Deserializing an untrusted object graph is known to be dangerous. Systems that serialize continuations have responded in three ways: tell users to load only their own state, sign the state, or check it. Espresso does the first and some of the third. Racket offers the second. Prim does the third with an allow-list of methods and types.

### Espresso checks frames on resume

An earlier version of this note said Espresso does minimal validation and that no implementation of structural frame checks had been found. That was wrong. Espresso documents and implements such checks.

- Documentation: "When restoring from a materialized frame (dematerialization), only minimal checks are performed by the VM, and only to prevent a VM crash. Examples of these checks are: Ensures that resume only happens on `invoke` bytecodes. Ensures that the recorded frame data is consistent with what was computed by the bytecode verifier. Ensures that the last frame in the record is `ContinuationImpl.suspend`." It then warns: "Deserializing a continuation supplied by an attacker will allow complete takeover of the JVM. Only resume continuations you persisted yourself!" [continuations.md](https://github.com/oracle/graal/blob/master/espresso/docs/continuations.md)
- Code: `HostFrameRecord.verify` calls `EspressoFrameDescriptor.validateImport` on the saved object and primitive arrays, requires the saved bytecode index to be at an invoke other than `invokedynamic`, and checks that the next frame's method matches that invoke. [HostFrameRecord.java](https://github.com/oracle/graal/blob/master/espresso/src/com.oracle.truffle.espresso/src/com/oracle/truffle/espresso/vm/continuation/HostFrameRecord.java). Read on 17 November 2026.
- The serialization page says everything reachable from the stack should "(ideally) be whitelisted by a serialization filter". [serialization.md](https://github.com/oracle/graal/blob/master/espresso/docs/serialization.md)

So Espresso checks slot counts, slot kinds and resume position against what the bytecode verifier computed. Its purpose is to prevent a crash, and it accepts any method in the JVM.

### The 2025 attack on Espresso

- X1r0z, "Hacking GraalVM Espresso: Abusing Continuation API to Make ROP-like Attack", August 2025. [Post](https://exp10it.io/2025/08/hacking-graalvm-espresso-abusing-continuation-api-to-make-rop-like-attack/). Proof of concept: [hacking-espresso](https://github.com/X1r0z/hacking-espresso). A blog post, not peer-reviewed.
- The proof of concept forges a chain of frame records with chosen `method`, `bci`, `pointers` and `primitives`. The frames are `sun.print.UnixPrintJob$PrinterSpooler.run` at bci 98 and `UnixPrintJob.printExecCmd` at bci 367, with a command array placed in the locals. Resuming runs the command.
- The attack passes Espresso's checks. Every forged frame is consistent with its method's bytecode. What Espresso does not restrict is which methods may appear: any JDK method at any invoke is accepted.
- No change to Espresso's documentation or frame checks was found between August 2025 and November 2026, and no advisory.

This is the difference Prim can claim. Prim's validator accepts only registered methods and only their registered yield points, so a frame for an arbitrary library method is rejected. Slot-count and slot-type checks alone would not have stopped this attack.

### General deserialization risk

- Chris Frohoff, Gabriel Lawrence. "Marshalling Pickles: How Deserializing Objects Will Ruin Your Day." OWASP AppSec California, January 2015. [Slides](https://frohoff.github.io/appseccali-marshalling-pickles/). Released ysoserial.
- Microsoft: "The BinaryFormatter.Deserialize method is never safe when used with untrusted input." [Security guide](https://learn.microsoft.com/en-us/dotnet/standard/serialization/binaryformatter-security-guide)
- Java: JEP 290, "Filter Incoming Serialization Data", JDK 9, backported to JDK 8u121 in January 2017 (search). [JEP 290](https://openjdk.org/jeps/290)

### Signing

- Racket stateless servlets send serialized continuations to the client. "Without explicit provision, it is possible for users to modify the continuations they are sent." `HMAC-SHA1-stuffer` "allows the servlet to certify that stuffed data was truly generated by the servlet". The default stuffer does not sign. [Docs](https://docs.racket-lang.org/web-server/stateless.html)
- Christian Queinnec, "Continuations and Web Servers", *Higher-Order and Symbolic Computation* 17(4), 2004, proposes encrypting continuations sent to clients (search; venue from memory).

A signature shows who produced the state. It does not show that the state still matches the code. A script recompiled between suspend and resume can have different yield points and slot layouts.

### Checking state on arrival

- William Farmer, Joshua Guttman, Vipin Swarup. "Security for Mobile Agents: Authentication and State Appraisal." ESORICS '96, LNCS 1146, Springer, pp. 118–130. [doi:10.1007/3-540-61770-1_31](https://doi.org/10.1007/3-540-61770-1_31). State-appraisal functions check invariants of an arriving agent's state, "to protect a host from attack… maintain critical state invariants, and to control privileges."
- George Necula, Peter Lee. "Safe, Untrusted Agents Using Proof-Carrying Code." In *Mobile Agents and Security*, LNCS 1419, Springer, 1998. [Springer](https://link.springer.com/chapter/10.1007/3-540-68671-1_5)
- Grégoire Henry, Michel Mauny, Emmanuel Chailloux, Pascal Manoury. "Typing Unmarshalling without Marshalling Types." ICFP 2012, pp. 287–298. doi:10.1145/2364527.2364569. Checks an unmarshalled OCaml memory graph against an expected static type (search). This validates heap values, not frames.
- Peter Sewell et al., "Acute: High-level programming language design for distributed computation", ICFP 2005: type-safe marshalling with a type check at unmarshal time (search).

The JVM's StackMapTable and CLR verification already assign a type to each frame slot at each instruction. Prim's slot checks apply the same typing to state loaded from outside.

### What was not found

No peer-reviewed paper was found that proposes validating deserialized stack frames by method identity, resume point, slot count, slot type and depth. Espresso's implementation is the closest existing work and is documented only in its repository. Searches in November covered mobile-agent security, strong mobility, typed unmarshalling and checkpointing.

Not verified:

- Whether Brakes or JavaGoX verify state on arrival. Milanés et al. write that "State verification is done by code inserted automatically by the bytecode transformer at the beginning of every invoked method" about Brakes; in context this appears to mean checking whether the thread is restoring, not validating the state.
- Any published analysis of forging continuation state in Javaflow, Kilim or Seaside.
