# Thread and continuation capture by program transformation on the JVM and CLR, 1998–2010

This note lists the systems that captured and restored thread state by transforming Java or .NET programs, and the public record of Second Life's Mono script engine.

## How the claims were checked

- **September 2026.** Citation metadata was checked against Crossref and Semantic Scholar. Mechanism descriptions come from the primary PDF where one could be downloaded: Stadler 2009, Srinivasan & Mycroft 2008, Pettyjohn et al. 2005, Loitsch 2007 and Bouchenak et al. (INRIA RR-4662). Springer chapters could not be read, so the mechanisms of JavaGo, Brakes, JavaGoX and Fünfrocken come from survey papers. Those are marked "(secondary)".
- **November 2026.** Open items were re-checked by web search. No page could be fetched directly in that pass, so each result rests on a search-result summary. Results from this pass are marked "(search)".

## 1. JavaGo

JavaGo is a source-to-source Java translator. To capture, it throws an exception; each frame catches it, saves its state into a chain of `State` objects and rethrows. To restore, it calls the methods again and jumps to the saved point with `switch`/`case`, because Java source has no `goto`. A 2001 paper by the same group gives the technique in general form for Java and C++, defined on bytecode.

Sources:

- Tatsurou Sekiguchi, Hidehiko Masuhara, Akinori Yonezawa. "A Simple Extension of Java Language for Controllable Transparent Migration and its Portable Implementation." *Coordination Languages and Models* (COORDINATION 1999), LNCS 1594, Springer, pp. 211–226. [doi:10.1007/3-540-48919-3_16](https://doi.org/10.1007/3-540-48919-3_16). Metadata checked against Crossref.
- Mechanism (secondary): "exceptions are thrown recursively until the whole call stack is saved as a chain of State objects. Because Java does not include a goto statement, these transformations must resort to switch-case statements." An `undock` statement marks the code to be migrated. From Milanés, Rodriguez, Schulze, "State of the art in heterogeneous strong migration of computations", *Concurrency and Computation: Practice and Experience* 20:1485–1508, 2008, doi:10.1002/cpe.1287 (search); preprint [arXiv cs/0612125](https://arxiv.org/pdf/cs/0612125).
- Tatsurou Sekiguchi, Takahiro Sakamoto, Akinori Yonezawa. "Portable Implementation of Continuation Operators in Imperative Languages by Exception Handling." *Advances in Exception Handling Techniques*, LNCS 2022, Springer, 2001, pp. 217–233. [doi:10.1007/3-540-45407-1_14](https://doi.org/10.1007/3-540-45407-1_14). The volume came out of an ECOOP 2000 workshop, which is why some indexes give 2000 (search).
- Pettyjohn et al. (2005) summarise the 2001 paper: it implements partial continuations in Java and C++ and "uses exception handling to construct continuation values as the stack unwinds". The translation is "defined on byte-codes". "During resumption an extra parameter carries the control state and extra logic is added to each function to distinguish between normal function calls and resumption." It identifies but does not solve the problem of untransformed frames on the stack. [doi:10.1145/1086365.1086393](https://doi.org/10.1145/1086365.1086393)
- Loitsch (2007) attributes the exception-based suspend/resume technique to "Tao (Tao 2001) and Sekiguchi et al. (Sekiguchi et al. 2001)" and says "Their work does not present full fledged continuations but only a way of suspending and resuming executions." [PDF](https://www.schemeworkshop.org/2007/procPaper4.pdf)
- Bouchenak et al. measured recursive Fibonacci: JavaGo about 246–250% execution overhead, Brakes about 189–193%, JavaGoX about 88–89%. [INRIA RR-4662](https://inria.hal.science/inria-00071923/document)

Not verified:

- The 1999 and 2001 Springer texts were not read.

## 2. Brakes

Brakes is a bytecode transformer from KU Leuven. It does not use exceptions. To capture, each method saves its frame into a per-thread `Context` object and returns, and its caller does the same. To restore, the methods are called again and each uses a bytecode `goto` to reach its saved "last performed instruction". Apache Commons Javaflow started from Brakes code.

Sources:

- Eddy Truyen, Bert Robben, Bart Vanhaute, Tim Coninx, Wouter Joosen, Pierre Verbaeten. "Portable Support for Transparent Thread Migration in Java." *Agent Systems, Mobile Agents, and Applications* (ASA/MA 2000), LNCS 1882, Springer, pp. 29–43. [doi:10.1007/978-3-540-45347-5_4](https://doi.org/10.1007/978-3-540-45347-5_4). Pages and DOI checked against Crossref; volume 1882 confirmed from the neighbouring chapter (search).
- Mechanism (secondary): "every method saves the current frame in the Context object and returns to the previous method recursively until the end of the stack is reached. Given that the instruction counter is not available in Java, a counter was created to represent the 'last performed instruction' (LPI). On recovery, the thread restores the first frame from the context stack… and moves the program counter to the saved LPI (using the goto instruction, available only at bytecode level)". [Milanés et al.](https://arxiv.org/pdf/cs/0612125)
- Hillier names Brakes and JavaGoX as the influences on the Second Life rewriter. [Hillier 2015](https://davehillier.net/2015/03/11/second-life-mono-internals/)
- Follow-up: Weyns, Truyen, Verbaeten, "Serialization of distributed execution state in Java" (NODe 2002, LNCS 2591), extends Brakes to capture initiated from outside the thread (search; details not checked).

Not verified:

- The Brakes paper was not read. The names of its flags are unknown.

## 3. Fünfrocken, JavaGoX, Tao, Bouchenak, Illmann

Fünfrocken (1998) used a Java source preprocessor that inserts `try`/`catch` to save state. JavaGoX (2000) is a bytecode transformer that uses a type system for bytecode to find the types of locals and operand-stack entries. Tao's 2001 thesis defines an exception-based transformation at source and bytecode level. Bouchenak and Illmann worked at VM level instead: a modified JVM, and the JPDA debugger interface.

Sources:

- Stefan Fünfrocken. "Transparent Migration of Java-Based Mobile Agents." *Mobile Agents* (MA '98), LNCS 1477, Springer, pp. 26–37. [doi:10.1007/BFb0057646](https://doi.org/10.1007/BFb0057646). Volume confirmed (search). Journal version: *Personal Technologies* 2(2):109–116, 1998, [doi:10.1007/BF01324941](https://doi.org/10.1007/BF01324941).
- Fünfrocken mechanism (secondary): "A compiler inserts try-catch statements in the source code to save the state of execution of each thread in different Java backup objects." Migration happens only at explicit points. [Milanés et al.](https://arxiv.org/pdf/cs/0612125). Bouchenak et al. call the system "Wasp".
- Takahiro Sakamoto, Tatsurou Sekiguchi, Akinori Yonezawa. "Bytecode Transformation for Portable Thread Migration in Java." ASA/MA 2000, LNCS 1882, Springer, pp. 16–28. [doi:10.1007/978-3-540-45347-5_3](https://doi.org/10.1007/978-3-540-45347-5_3). Abstract (search): it "does not need extended virtual machines nor source code of target programs", and uses "a type system for Java bytecode to correctly determine valid frame variables and operand stack entries".
- Wei Tao. *A Portable Mechanism for Thread Persistence and Migration.* PhD thesis, University of Utah, 2001. [ProQuest 3005121](https://www.proquest.com/openview/8ecca602b761150666e2badd3e4c43cb/1?pq-origsite=gscholar&cbl=18750&diss=y). Pettyjohn et al.: "Tao describes a method for thread persistence and migration, leveraging Java's exception mechanism. Tao defines the translation for source code and byte-codes." Tao "maintains an index and adds extra logic to the code so that the resumption point can be relocated" and "essentially makes two versions of the program: a shutdown version and a restart version".
- S. Bouchenak, D. Hagimont. "Pickling Threads State in the Java System." TOOLS 33, IEEE, 2000, pp. 22–32. [doi:10.1109/TOOLS.2000.848748](https://doi.org/10.1109/TOOLS.2000.848748).
- S. Bouchenak, D. Hagimont, S. Krakowiak, N. De Palma, F. Boyer. "Experiences Implementing Efficient Java Thread Serialization, Mobility and Persistence." *Software: Practice and Experience* 34(4):355–393, 2004. [doi:10.1002/spe.569](https://doi.org/10.1002/spe.569). Report version: [INRIA RR-4662](https://inria.hal.science/inria-00071923/document), December 2002. The JVM is extended "to capture the state of a Java thread as an object and to initialize a thread with a particular state". The report classes Wasp, JavaGo, Brakes and JavaGoX as application-level (portable, incomplete) and Sumatra, CIA, Merpati and its own ITS/CTS as JVM-level.
- Torsten Illmann, Tilman Krueger, Frank Kargl, Michael Weber. "Transparent Migration of Mobile Agents Using the Java Platform Debugger Architecture." *Mobile Agents* (MA 2001), LNCS 2240, Springer, pp. 198–212. [doi:10.1007/3-540-45647-3_14](https://doi.org/10.1007/3-540-45647-3_14). Simão, Garrochinho and Veiga note that JPDA-based solutions "are only able to extract the state of a single thread" (*CCPE* 24(13):1421–1442, 2012, [doi:10.1002/cpe.1879](https://doi.org/10.1002/cpe.1879)).
- Other VM-level systems: JESSICA2 (Zhu, Wang, Lau, IEEE Cluster 2002, pp. 381–388, doi:10.1109/CLUSTR.2002.1137770); Mobile JikesRVM (Quitadamo, Cabri, Leonardi, *Sci. Comput. Program.* 70:221–240, 2008, doi:10.1016/j.scico.2007.07.009); Bouchenak, "Making Java applications mobile or persistent", USENIX COOTS 2001 (search).

Not verified:

- **Whether JavaGoX unwinds with exception handlers.** An earlier search summary said it does and that it saves operand-stack values into extra locals. No source for that was found in either pass. A phrase about exception handling reducing overhead belongs to the 2001 Sekiguchi paper and may have been misattributed. Treat JavaGoX's unwind mechanism as unknown.
- The Tao thesis text was not read; its description comes from Pettyjohn et al. Its metadata was not re-confirmed in November.
- Volume, issue and pages of the 2004 *SPE* paper were not re-confirmed in November.

Conclusion from sections 1–3: by 2001, three groups had published exception-based unwinding with per-frame save and re-entry dispatch for Java (Fünfrocken, Sekiguchi et al., Tao), and Brakes had published the return-based alternative on bytecode.

## 4. Javaflow and RIFE

Both are bytecode instrumentation frameworks that unwind by returning and checking a flag, saving into a parallel stack. Javaflow's `Continuation` is `java.io.Serializable`. RIFE allowed capture in one method only, so its continuations hold one frame.

Sources:

- Javaflow API: "Snapshot of a thread execution state. A Continuation object is an immutable object that captures everything in the Java stack." The class implements `java.io.Serializable`. [Javadoc](https://commons.apache.org/sandbox/commons-javaflow/apidocs/org/apache/commons/javaflow/Continuation.html)
- Stadler et al. on Javaflow: "All affected methods are modified such that they can distinguish between normal execution, continuation capturing, and continuation resuming, which adds runtime overhead even when no continuations are used. The local variables, expressions, and position are stored in a thread-local parallel stack. This approach is not tied to a specific Java virtual machine and allows continuations to be serialized." Their VM implementation was "one to two orders of magnitude" faster. [PDF](https://ssw.jku.at/Research/Papers/Stadler09/Stadler09a.pdf)
- History: Torsten Curdt wrote in March 2004 that he had built a first proof of concept for Cocoon using Brakes, and that Stephan Michels then "rewrote all the parts that used Brakes". The code lived in Cocoon 2.1 before moving to Apache Commons. [Curdt 2004](https://vafer.org/blog/20040331171224/) (search). An earlier version of this note dated the start to 2005; that is too late.
- Stadler et al. on RIFE: it "allows continuation capturing only within a specific method (processElement), so that there is always only one activation frame per continuation". Kilim's authors: "RIFE does not handle nested pausable calls."
- RIFE (interview, 1 March 2007): "The continuations infrastructure maintains a parallel stack that references the actual data", and "the data structure must be cloneable or serializable". [Artima](https://artima.com/lejava/articles/continuations.html)

Not verified:

- The date Javaflow moved to the Commons sandbox.

## 5. Kilim

Kilim's weaver rewrites `@pausable` methods to take an extra `Fiber` argument. It unwinds by returning and checking. Each frame stores its PC, operand stack and locals in the `Fiber`; on re-entry each method reads the `Fiber` and jumps to the resumption point. Continuations are one-shot. The paper does not say the `Fiber` state can be serialized.

Sources:

- Sriram Srinivasan, Alan Mycroft. "Kilim: Isolation-Typed Actors for Java." *ECOOP 2008*, LNCS 5142, Springer, pp. 104–128. [doi:10.1007/978-3-540-70592-5_6](https://doi.org/10.1007/978-3-540-70592-5_6). [PDF](https://www.malhar.net/sriram/kilim/kilim_ecoop08.pdf)
- "When the callee pauses… the caller examines the fiber, learns that it is now in a pausing state, stores its activation frame on the fiber and returns… each method consults the fiber upon entry, jumps directly to the resumption point and restores its state where necessary."
- On exceptions: "Some researchers have used exceptions as a longjmp mechanism to unwind the stack; we use return because we found exceptions to be more expensive by almost two orders of magnitude. Not only do they have to be caught and re-thrown at each level of the stack chain, they clear the operand stack as well. This unnecessarily forces one to take a snapshot of the operand stack before making a call."
- On Pettyjohn et al.: their approach "relies on exceptions for stack unwinding, it creates custom objects per invocation site, splits the code into top-level procedures".
- Earlier description of the mechanics: Sriram Srinivasan, "A Thread of One's Own", Workshop on New Horizons in Compilers, Bangalore, December 2006 (search). [PDF](https://www.malhar.net/sriram/kilim/thread_of_ones_own.pdf)

## 6. Stadler et al.

This is a modification of the HotSpot interpreter and client compiler. It is not a program transformation. Frames are copied as raw stack contents, and the paper does not claim serialization.

Sources:

- Lukas Stadler, Christian Wimmer, Thomas Würthinger, Hanspeter Mössenböck, John Rose. "Lazy Continuations for Java Virtual Machines." PPPJ 2009, ACM, pp. 143–152. [doi:10.1145/1596655.1596679](https://doi.org/10.1145/1596655.1596679). [PDF](https://ssw.jku.at/Research/Papers/Stadler09/Stadler09a.pdf)
- Affiliations printed on the paper: Stadler, Würthinger and Mössenböck at Johannes Kepler University Linz; Wimmer at University of California, Irvine; Rose at Sun Microsystems.
- "We developed our implementation as an extension to the Java HotSpot VM", as part of the Da Vinci Machine (MLVM) project. The activation's `data` field "stores a plain copy of the stack contents of the activation frame". The design has "zero run-time overhead as long as no activations need to be saved and restored".
- Its related-work section covers Ovm, Javaflow, RIFE, Ruby 1.9, BCD Scheme and Chez Scheme. It does not cite JavaGo, Brakes, Tao or Kilim.

## 7. .NET: Mono.Tasklets and Second Life

Mono.Tasklets copies stacks inside the Mono runtime. It is not portable to other CLI implementations and not serializable. Second Life's system rewrote CIL. It was presented at Lang.NET in 2006 and went into production on 29 August 2008.

One peer-reviewed paper on capturing the stack by program transformation on .NET predates Second Life: Pettyjohn et al., ICFP 2005 (see [stack_inspection_continuations.md](stack_inspection_continuations.md)). It captures continuations and does not address migration. No paper on .NET thread migration or mobile agents by IL rewriting from before 2010 was found in either pass.

Sources:

- Miguel de Icaza, "Microthreading with Mono", 7 June 2006: "Their micro-threading implementation is different than Tomi's recent continuation work in that Second Life has added support to 'migrate' running code across machines." [Post](https://tirania.org/blog/archive/2006/Jun-07-1.html). This post predates the Lang.NET talk.
- Lang.NET Symposium, Microsoft, Redmond, 31 July – 2 August 2006. Cory Ondrejka and Jim Purbrick presented on 31 July. De Icaza linked the video on 15 September 2006. [Post](https://tirania.org/blog/archive/2006/Sep-15-2.html) (search). No live copy of the video was confirmed.
- Miguel de Icaza, "Continuations in Mono: Embrace and Extending .NET, Part 3", April 2009: Second Life used "a CIL rewriting engine that injected the state serialization and reincarnation into an existing CIL instructions stream". It "required that code be suspended at any point in time and that its entire state be serializable into a format suitable for storage into a database". "The technology went in production in 2008 and today this continuation framework powers 10 million Mono scripts on SecondLife." [Post](https://tirania.org/blog/archive/2009/Apr-09.html). Purbrick's FOSDEM 2010 abstract gives over 27 million scripts (search).
- Mono.Tasklets is based on Tomi Valkeinen's MonoCo and shipped in Mono 2.6, released 14 December 2009 (search). It "requires changes to the VM that are not portable to other ISO CLI implementations". [Mono docs](https://www.mono-project.com/archived/continuations/)
- Jim Purbrick: the 2006 work comprised "a new LSL compiler back end to generate CIL bytecode, a scavenging garbage collector to allow assembly unloading and a microthread injector to allow 10s of 1000s of scripts to run concurrently on Mono in a single process". [Blog](https://jimpurbrick.com/tag/mono.html). FOSDEM 2010 talk: "Building The Virtual Babel: Mono In Second Life".
- Babbage Linden, "Second Life In Mono", 1 August 2005. [Post](https://secondlife.blogs.com/babbage/2005/08/second_life_in_.html)
- Deployment: "Second Life Server 1.24.3 (August 29th, 2008)". A Mono script gets 64 KB of memory against 16 KB under LSL2. [SL Wiki: Mono](https://wiki.secondlife.com/wiki/Mono)
- InfoQ, "Mono Continuations", March 2011, contrasts Purbrick's CIL rewriting with Valkeinen's runtime approach. [Article](https://www.infoq.com/news/2011/03/Mono-Continuations) (search)
- RAIL, the rewriting library Second Life used: Bruno Cabral, Paulo Marques, Luís Silva, "RAIL: Code Instrumentation for .NET", ACM Symposium on Applied Computing (SAC '05), 2005; also an OOPSLA '04 Companion abstract, pp. 210–211 (search).

### Hillier's 2015 description

Dave Hillier, "IIRC: Second Life Mono internals", 11 March 2015, is the most detailed public description. [Post](https://davehillier.net/2015/03/11/second-life-mono-internals/). Read in September 2026:

- "The stack serialisation was achieved by modifying the program's byte code that inserted blocks do the capture and restore of the current threads state. Doing it at the code level meant that we did not have to modify Mono and the code could also be ported to a standard Windows .Net VM."
- "The assembly rewriter was implemented using RAIL, which is similar to Mono Cecil."
- "At the start of each method, a block of code is injected to do the restore. It get the saved frame and restores the local variables and the state of the stack. It then jumps to the part of the method that was previously executed. At the end of the method it inserts code to do the save which populates a stack frame."
- "Backwards jump is one of the places to insert a yield."
- Pseudocode: `if (IsRestoring) { var frame = PopFrame(); switch(frame.pc) {...goto PCn} }`; at a yield, `if (IsYieldDue) { frame.pc=1; frame.locals=...; frame.stack=...; goto Yield; }`; then `Yield: if (IsSaving) PushFrame(frame); // return normally`.
- Scripts were also moved between AppDomains so that assemblies could be unloaded. Only the subset of CIL that the LSL compiler emits was supported. Brakes and JavaGoX are named as influences.

The pseudocode shows a flag-and-return unwind, as in Brakes. It contains no exception handlers. The November pass could not fetch the page; search summaries confirmed `IsRestoring`, `IsYieldDue`, `goto Yield` and `PushFrame`, but not `IsSaving` or "return normally".

Not verified:

- Whether the shipped system matched the 2015 pseudocode. The simulator source is not public. The author is the person who can settle this.
- Page numbers and DOI of the RAIL SAC '05 paper.
- The year of a Second Life presentation at OOPSLA that Purbrick's blog mentions.
