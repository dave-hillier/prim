# Continuations by exception-based capture outside the Java migration literature

This note covers Pettyjohn et al. (2005) and the work around it: its sources, its descendants in JavaScript, Scheme implementations on the JVM and CLR, and other systems that serialize continuations. The Java thread-migration systems are in [jvm_clr_migration_prior_art.md](jvm_clr_migration_prior_art.md).

## How the claims were checked

- **September 2026.** Citation metadata was checked against Crossref. The full texts of Pettyjohn et al. 2005, Loitsch 2007, Baxter et al. 2018 (Stopify) and McCarthy 2009 were read, and quotations from them come from the PDFs.
- **November 2026.** Open items were re-checked by web search. No page could be fetched directly, so each result rests on a search-result summary. These are marked "(search)".

## 1. Pettyjohn et al., ICFP 2005

The paper gives a transformation that captures continuations on .NET. The program is put in A-normal form and each call is wrapped in an exception handler. Capture throws an exception; each handler saves its frame's live locals into a heap object and rethrows. Restore calls the saved frame objects again. The paper presents this in C# on a factorial example. It predates Second Life's production deployment by three years and its first public presentation by one.

The .NET prototype does not serialize continuations. Serialization appears in a second prototype in the same paper, for the PLT Scheme web server.

Sources:

- Greg Pettyjohn, John Clements, Joe Marshall, Shriram Krishnamurthi, Matthias Felleisen. "Continuations from Generalized Stack Inspection." ICFP 2005, Tallinn, ACM, pp. 216–227. [doi:10.1145/1086365.1086393](https://doi.org/10.1145/1086365.1086393). [PDF](https://cs.brown.edu/~sk/Publications/Papers/Published/pcmkf-cont-from-gen-stack-insp/paper.pdf)
- Abstract: "we show how to use our new technique to copy and reconstitute the stack on MSIL.Net using exception handlers. This establishes that Scheme's first-class continuations can exist on non-cooperative virtual machines."
- Motivation: "these machines do not provide instructions for installing and saving the run-time stack. Usually, Scheme-on-VM implementors give up on first-class continuations [JScheme, Bigloo.NET], or they allocate the control stack in the heap of the machine [SISC]."
- Model: `call/cc` is translated into a language with continuation marks. "The resulting transformation is less radical than CPS, and it is thus natural to run the resulting program on the natively available stack."
- .NET mechanism (section 4.2): "marking a continuation (w-c-m) corresponds to the installation of an exception handler; the inspection of the continuation marks (c-c-m) can be accomplished by raising an exception, thus transferring control to code that writes a representation of the mark to the heap as the stack unwinds." Capture throws a `SaveContinuation` exception "that only the newly inserted handlers know about and catch". Each handler runs `sce.Extend(new fact_frame0(x)); throw;`.
- Capture is lazy: "the analogous closures are only created as the stack is unwound by the special exception."
- Cost, stated without measurements: "a program that does not use first-class continuations pays only for the establishment of exception handlers around (non-tail) function calls." The paper has no benchmark table.
- Restore (figure 14): each saved frame reloads the more recent frames, then calls `frame.Invoke(returnValue)` inside a `try`/`catch` whose handler appends the older saved frames and rethrows. A second capture inside a restored context therefore shares the older frames.
- Untransformed frames on the stack: the paper adds "safety marks" as "a partial solution".
- Serialization: in the web-server prototype, `send/suspend` "collects the continuation marks and serializes them" into a URL or hidden form field.
- Its related-work section says Sekiguchi et al. (2001) and Tao (2001) already used "exception handling to construct continuation values as the stack unwinds".

Related material found in November (search):

- A PLT-hosted page, "A Technique for Implementing First-Class Continuations", has C# code for the technique (`ContinuationFrame`, `SaveContinuationException`). A later report attributes it to Joe Marshall. [Page](https://www2.ccs.neu.edu/racket/pubs/stackhack4.html)
- Joe Marshall, "An Unexceptional Implementation of First-Class Continuations", International Lisp Conference 2009, pp. 36–40. Same goal, by a co-author of the 2005 paper, but "use of the exception handler is avoided". Its abstract says "measurements demonstrate acceptable performance". [Abstract](https://research.google/pubs/an-unexceptional-implementation-of-first-class-continuations/)

Not verified:

- No measurements of the 2005 .NET prototype were found.

## 2. Sources of the technique

Exception-based capture comes from the thread-migration work of Sekiguchi et al. and Tao. The stack-inspection theory comes from Clements and Felleisen.

Sources:

- Sekiguchi, Sakamoto, Yonezawa (2001) and Tao (2001): see [jvm_clr_migration_prior_art.md](jvm_clr_migration_prior_art.md). Tao's thesis: University of Utah, 2001, adviser Gary Lindstrom, ProQuest AAI3005121 (search).
- John Clements, Matthias Felleisen. "A Tail-Recursive Machine with Stack Inspection." *ACM TOPLAS* 26(6):1029–1052, 2004. [doi:10.1145/1034774.1034778](https://doi.org/10.1145/1034774.1034778). Conference version: ESOP 2003, pp. 22–37, [doi:10.1007/3-540-36575-3_3](https://doi.org/10.1007/3-540-36575-3_3).
- John Clements. *Portable and High-Level Access to the Stack with Continuation Marks.* PhD dissertation, Northeastern University, February 2006. [PDF](https://www2.ccs.neu.edu/racket/pubs/dissertation-clements.pdf)
- John Clements, Ayswarya Sundaram, David Herman. "Implementing Continuation Marks in JavaScript." Workshop on Scheme and Functional Programming, 2008 (search).
- Andrew Tolmach, Andrew Appel. "Debugging Standard ML Without Reverse Engineering." LFP 1990, pp. 1–12. [doi:10.1145/91556.91564](https://doi.org/10.1145/91556.91564). Journal version: *J. Functional Programming* 5(2):155–200, 1995. The debugger instruments user source and checkpoints with SML/NJ's own `call/cc`. It does not use exceptions for capture (search).

## 3. JavaScript

Loitsch (2007) adapted the technique to JavaScript, with full `call/cc`. Stopify (2018) offers three capture strategies. Neither serializes continuations. Doppio and Whalesong avoid the problem by running interpreters that keep their own stacks on the heap.

Sources:

- Florian Loitsch. "Exceptional Continuations in JavaScript." 2007 Workshop on Scheme and Functional Programming, Freiburg, Université Laval Technical Report DIUL-RT-0701, starting at p. 37. No DOI. [PDF](https://www.schemeworkshop.org/2007/procPaper4.pdf)
- Lineage: "Our work is an adaption and evolution of the suspension and migration techniques presented in Tao's thesis (Tao 2001) and Sekiguchi et al.'s paper (Sekiguchi et al. 2001). Pettyjohn et al. later extended this technique for call/cc (Pettyjohn et al. 2005)."
- Mechanism: suspend throws a special exception. Instrumented calls catch it and save locals and a resumption index. Restore sets a global flag and uses a `switch` to emulate the jump back to the saved call site.
- Why not CPS: two hand-written CPS benchmarks "were 30 and 130 times slower than the native versions".
- Overhead of instrumentation alone, with no continuation captured, relative to uninstrumented code: Firefox 2.0 from 1.1× to 4.1×; Opera 9.2 up to 16.9×; Konqueror 3.5 from 1.0× to 3.1×. The benchmarks were modified to stop the compiler's typing pass from removing the instrumentation.
- Samuel Baxter, Rachit Nigam, Joe Gibbs Politz, Shriram Krishnamurthi, Arjun Guha. "Putting in All the Stops: Execution Control for JavaScript." PLDI 2018, ACM, pp. 30–45. [doi:10.1145/3192366.3192370](https://doi.org/10.1145/3192366.3192370). [arXiv 1802.02974](https://arxiv.org/abs/1802.02974)
- Stopify's strategies: checked-return (every call checks whether a capture is in progress); exceptional, after Loitsch ("every function application is guarded by an exception handler that catches the special exception, reifies the stack frame, and re-throws it… exception handlers come at a cost"); and eager, which keeps a shadow stack. "Both checked-return and exceptional continuations reify the stack lazily." The fastest strategy differs between browsers.
- Stopify rejected CPS and generators as "3x and 2x slower than the approach we present".
- Stopify's median slowdowns depend on the source compiler: PyJS 1.7×–3.8× across platforms, ScalaJS 11.8×–23.9×. Octane median 1.3×, Kraken 41.0×.
- The Stopify paper does not mention serializing or persisting continuations.
- John Vilk, Emery Berger. "Doppio: Breaking the Browser Language Barrier." PLDI 2014, pp. 508–518. [doi:10.1145/2594291.2594293](https://doi.org/10.1145/2594291.2594293)
- Danny Yoo, Shriram Krishnamurthi. "Whalesong: Running Racket in the Browser." DLS 2013, pp. 97–108. [doi:10.1145/2508168.2508172](https://doi.org/10.1145/2508168.2508172)
- Eric Thivierge, Marc Feeley. "Efficient Compilation of Tail Calls and Continuations to JavaScript." Scheme Workshop 2012, pp. 47–57. [doi:10.1145/2661103.2661108](https://doi.org/10.1145/2661103.2661108)

Not verified:

- The last page of Loitsch's paper (probably 45).
- The Loitsch figures and the Stopify ScalaJS and CPS figures were read from the PDFs in September and not re-confirmed in November.

## 4. Scheme on the JVM and CLR; Scala

Scheme implementations on these VMs either restrict continuations to escaping (Bigloo.NET, Kawa, IronScheme) or keep the stack on the heap (SISC). Scala's continuations plugin uses a selective CPS transform, not exceptions.

Sources:

- Yannis Bres, Bernard Serpette, Manuel Serrano. "Bigloo.NET: Compiling Scheme to .NET CLR." *Journal of Object Technology* 3(9):71–94, 2004. [doi:10.5381/jot.2004.3.9.a4](https://doi.org/10.5381/jot.2004.3.9.a4)
- SISC: Scott G. Miller, "SISC: A Complete Scheme Interpreter in Java", 2002 (search; venue not determined).
- Kawa: "Kawa continuations are implemented using Java exceptions, and can be used to prematurely exit (throw), but not to implement co-routines". [Kawa internals](https://www.gnu.org/software/kawa/internals/complications.html)
- Andrea Bernardini, "First-Class Continuations on the Java Virtual Machine", MSc thesis, Politecnico di Milano, 2014–2015, supervisor Matteo Pradella. It implements `call/cc` in Kawa with the Pettyjohn technique. The work was not merged into Kawa (search). [Thesis site](https://andrebask.github.io/thesis/)
- IronScheme: `call/cc` "does not support reifying continuations… the continuation must be called from the dynamic extent" (project wiki, search).
- Tiark Rompf, Ingo Maier, Martin Odersky. "Implementing First-Class Polymorphic Delimited Continuations by a Type-Directed Selective CPS-Transform." ICFP 2009, pp. 317–328. [doi:10.1145/1596550.1596596](https://doi.org/10.1145/1596550.1596596)

## 5. Other systems that serialize continuations

Racket's stateless servlets serialize continuations produced by a source transformation. Stackless Python, Pluto and Eris serialize suspended coroutines from inside the interpreter, which has direct access to its frames. Seaside copies Smalltalk contexts but does not serialize them.

Sources:

- Jay McCarthy. "Automatically RESTful Web Applications: Marking Modular Serializable Continuations." ICFP 2009, ACM, pp. 299–310. [doi:10.1145/1596550.1596594](https://doi.org/10.1145/1596550.1596594). [PDF](https://jeapostrophe.github.io/home/static/icfp065-mccarthy.pdf)
  - "Our modular RESTful transformation is based on one from Pettyjohn et al. (2005)."
  - "Each continuation record is scarcely more than 100 bytes."
  - "Our system uses 10 percent (or less) of the memory required by previous approaches."
  - Limitation: a higher-order library procedure that was not transformed cannot be on the stack at capture.
- Jay McCarthy. "The Two-State Solution: Native and Serializable Continuations Accord." OOPSLA 2010, pp. 567–582. [doi:10.1145/1869459.1869506](https://doi.org/10.1145/1869459.1869506)
- Racket documentation, "Stateless Servlets": the pipeline is A-normal form, continuation marks, removal of `call/cc` by mark inspection, then defunctionalization "with a serializable data-structure for each lambda". "Your server can restart in the middle of a long running Web interaction." [Docs](https://docs.racket-lang.org/web-server/stateless.html)
- Stackless Python: running tasklets can be pickled and resumed elsewhere. Tasklets that are inside a C function cannot. [Docs](https://stackless.readthedocs.io/en/latest/library/stackless/pickling.html). CCP uses Stackless Python in EVE Online, on client and server (search).
- Pluto: Ben Sunshine-Hill announced the first alpha on the Lua mailing list on 2 June 2004 (search). [Post](https://lua-users.org/lists/lua-l/2004-06/msg00019.html)
- Eris: "essentially a rewrite of Pluto, targeting Lua 5.2". It persists yielded coroutines. C functions cannot be persisted, and "no running thread can be persisted, only suspended ones". [Repository](https://github.com/fnuecke/eris)
- Stéphane Ducasse, Adrian Lienhard, Lukas Renggli. "Seaside: A Flexible Environment for Building Dynamic Web Applications." *IEEE Software* 24(5):56–63, 2007. [doi:10.1109/MS.2007.144](https://doi.org/10.1109/MS.2007.144). Seaside copies the chain of `MethodContext` objects in the image. Sessions are "not serializable in Seaside/Smalltalk, but in theory it is possible" (Seaside mailing list, 2005; search).

Racket is the closest prior system that both transforms the program and serializes the result. It works on Scheme source and uses Racket's native continuation marks. The exception-based variant exists only in the 2005 C# example.

Not verified:

- No paper describing Stackless pickling, Pluto or Eris was found. Cite the software.
