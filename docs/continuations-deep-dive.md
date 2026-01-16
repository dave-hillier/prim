# Deep Dive: The WebAssembly Typed Continuations Proposal

## Executive Summary

The WebAssembly stack-switching proposal (also known as WasmFX or typed continuations) is the most relevant modern work to your original Second Life bytecode rewriter. It solves the same fundamental problem—how to suspend, capture, and resume execution—but does so at the instruction set level rather than through bytecode rewriting.

**Key insight:** Where you rewrote .NET bytecode to inject save/restore logic, WasmFX adds native instructions (`cont.new`, `suspend`, `resume`) that make continuations first-class. This eliminates the need for whole-program transformation.

---

## The Problem Being Solved

WebAssembly originally had no support for:
- Multiple stacks of execution
- Switching between stacks at arbitrary depth
- Capturing and resuming computation

This meant compilers targeting WASM had to use **whole-program transformations** like:
- Continuation-Passing Style (CPS) - transforms every function
- Asyncify (Binaryen) - bytecode rewriting, conceptually similar to your work
- State machine transformation - what C# does for `async/await`

These approaches have significant overhead (code size, performance) and complicate debugging.

---

## Core Concepts

### 1. Control Tags (Effect Labels)

A **control tag** is like a typed, resumable exception. It declares:
- What values are passed **out** when suspending (like exception payload)
- What values are passed **back** when resuming (unlike exceptions)

```wat
;; Tag declaration: yields an i32, expects nothing back when resumed
(tag $yield (param i32))

;; Tag with bidirectional communication: sends i32, receives i64 back
(tag $async_read (param i32) (result i64))
```

Tags are the "interface" between suspended code and its handler—they define the protocol for communication across the suspension boundary.

### 2. Continuation Types

A continuation type wraps a function type, representing a suspended computation:

```wat
(type $ft (func))                    ;; A function taking/returning nothing
(type $ct (cont $ft))                ;; A continuation of that function

(type $ft2 (func (param i32)))       ;; Function taking i32
(type $ct2 (cont $ft2))              ;; Continuation expecting i32 to start
```

The continuation type describes:
- What values are needed to **start/resume** the continuation (params)
- What values it produces when it **completes** (results)

### 3. The Three Core Instructions

#### `cont.new` - Create a Continuation

Turns a function reference into a suspended continuation:

```wat
(cont.new $ct (ref.func $my_function))
;; Stack: [] -> [(ref $ct)]
```

This allocates a new stack segment. The continuation is initially suspended at the function's entry point.

#### `suspend` - Yield Control

Suspends execution and passes control (with a payload) to the nearest handler:

```wat
(suspend $yield (local.get $value))
;; Stack: [i32] -> []  (after resumption)
```

When executed:
1. Current stack state is captured as a continuation
2. Control transfers to the handler for `$yield`
3. Handler receives the payload AND a reference to the suspended continuation
4. When resumed, execution continues after the `suspend`

#### `resume` - Resume a Continuation

Resumes a suspended continuation with handlers installed:

```wat
(resume $ct
  (on $yield $handle_yield)    ;; Handler clause
  (local.get $continuation))
;; Stack: [(ref $ct)] -> [<return values>]
```

The handler clause `(on $yield $handle_yield)` means: "if the continuation suspends with tag `$yield`, jump to block `$handle_yield`".

---

## Complete Example: Generator

This implements a Python-style generator in WAT:

```wat
(module
  ;; Type definitions
  (type $ft (func))
  (type $ct (cont $ft))

  ;; Tag: generator yields i32 values
  (tag $yield (param i32))

  ;; Generator function: yields 100, 99, 98, ..., 1
  (func $generator
    (local $i i32)
    (local.set $i (i32.const 100))

    (loop $loop
      ;; SUSPEND: yield current value to consumer
      ;; Control transfers to handler, which receives:
      ;;   - The i32 value (100, 99, etc.)
      ;;   - A continuation reference to resume here
      (suspend $yield (local.get $i))

      ;; When resumed, execution continues here
      (local.tee $i
        (i32.sub (local.get $i) (i32.const 1)))
      (br_if $loop)
    )
  )

  ;; Must declare functions used with cont.new
  (elem declare func $generator)

  ;; Consumer: creates and drives the generator
  (func $consumer
    (local $c (ref $ct))

    ;; Create continuation from generator function
    ;; This allocates a new stack, suspended at $generator entry
    (local.set $c (cont.new $ct (ref.func $generator)))

    (loop $loop
      (block $on_yield (result i32 (ref $ct))
        ;; Resume the generator
        ;; If it suspends with $yield, jump to $on_yield
        ;; If it returns normally, fall through
        (resume $ct (on $yield $on_yield) (local.get $c))

        ;; Generator completed (returned normally)
        (return)
      )
      ;; Generator suspended with $yield
      ;; Stack now has: [i32, (ref $ct)]
      ;;   - i32: the yielded value
      ;;   - (ref $ct): continuation to resume

      (local.set $c)        ;; Save continuation for next iteration
      (call $print_i32)     ;; Print the yielded value (consumes i32)
      (br $loop)
    )
  )
)
```

### Execution Flow

```
Consumer                          Generator
--------                          ---------
cont.new $generator
  |
  +---> (stack allocated, suspended at entry)

resume
  +---> local.set $i = 100
        loop:
          suspend $yield 100 ----+
                                 |
  <-----(continuation captured)--+

(block $on_yield receives i32=100 and continuation ref)
print 100
resume
  +---> (continues after suspend)
        $i = 99
        suspend $yield 99 -------+
                                 |
  <------------------------------+

... continues until $i = 0 ...

resume
  +---> (loop exits)
        (function returns normally)
  <-----(no suspension, falls through resume)

(return from $consumer)
```

---

## Comparison to Your Original Approach

| Aspect | Second Life Bytecode Rewriter | WasmFX |
|--------|------------------------------|--------|
| **Transformation level** | Post-compile bytecode rewriting | Native instructions |
| **Stack capture** | Injected save blocks serialize locals + stack | Hardware/runtime captures stack directly |
| **Yield points** | Injected at loop back-edges, calls | Explicit `suspend` instruction |
| **Resume mechanism** | Switch dispatch at method entry | `resume` instruction |
| **Continuation representation** | Serializable byte array | Opaque reference type |
| **Portability** | Works on any .NET runtime | Requires WasmFX-enabled runtime |
| **Serialization** | Built-in (your goal) | **Not supported** (opaque refs) |

### Critical Difference: Serialization

WasmFX continuations are **opaque references**—you cannot serialize them to bytes and restore them later or on another machine. The continuation exists only as a runtime artifact.

**Your original approach is still more powerful** for the migration use case because you explicitly serialize the state to a portable format.

---

## The Type System

WasmFX uses **typed continuations** following the effect handler tradition:

```
Tag:          (tag $e (param tp*) (result tr*))
Continuation: (cont $ft) where $ft : [σ*] -> [τ*]
```

The type system ensures:
- Payloads passed via `suspend` match the tag's param types
- Values passed on `resume` match the tag's result types
- Handler blocks expect the right stack shape

This is more structured than your approach, which had to handle arbitrary stack states.

---

## Delimited Continuations Theory

WasmFX is based on **delimited continuations** with **effect handlers**. Key theoretical concepts:

### Shift/Reset (Classical Operators)

```
reset (... shift k (... k v ...) ...)
       |__________________________|
              "delimited" part

- reset: marks the boundary of what gets captured
- shift: captures continuation up to nearest reset, binds it to k
- k: the captured continuation, can be invoked (resumed)
```

In WasmFX terms:
- `resume` = `reset` (establishes a handler boundary)
- `suspend` = `shift` (captures up to the handler)
- The continuation passed to the handler = `k`

### Deep vs Shallow Handlers

**Deep handlers** (WasmFX default): When you resume a continuation, the same handler remains installed. Good for recursion patterns.

**Shallow handlers**: Handler is consumed on first suspension. Must re-install on each resume. More explicit control.

### One-shot vs Multi-shot

**One-shot** (WasmFX): A continuation can be resumed at most once. Attempting to resume twice traps.

**Multi-shot**: A continuation can be resumed multiple times (enables backtracking, non-determinism).

WasmFX chose one-shot for:
- Simpler implementation (no copying stacks)
- Easier resource management
- Still covers most use cases (generators, async, threads)

---

## Implementation Approaches

### 1. Asyncify (Current Workaround)

Binaryen's Asyncify is conceptually similar to your approach:

```
Original:           Transformed:

func foo():         func foo():
  bar()               if (rewinding):
  ...                   restore_locals()
                        goto saved_pc
                      bar()
                      if (unwinding):
                        save_locals()
                        return
                      ...
```

**Overhead:** ~30% code size, ~30% runtime slowdown

### 2. Wasmtime Fibers (WasmFX Implementation)

Wasmtime implements WasmFX using its existing **fibers** infrastructure:

- Each continuation gets a separate stack allocation
- `suspend` = stack switch (save registers, swap stack pointers)
- `resume` = stack switch back
- Implemented in assembly for each architecture

```
Stack switching (x86_64 assembly pseudocode):
1. Push callee-saved registers to current stack
2. Save current RSP to fiber header
3. Load new RSP from target fiber header
4. Pop callee-saved registers from new stack
5. Return (pops saved return address, continues there)
```

### 3. Segmented/Growable Stacks

Alternative implementation strategy:
- Stacks grow/shrink dynamically
- Continuation = pointer to stack segment
- Can pool and reuse stack segments

---

## Relation to Effect Handlers in Other Languages

### OCaml 5.x

```ocaml
effect Yield : int -> unit

let generator () =
  for i = 100 downto 1 do
    perform (Yield i)
  done

let consumer () =
  try generator () with
  | effect (Yield n) k ->
      print_int n;
      continue k ()  (* resume *)
```

OCaml's effects are also one-shot, implemented via fibers (heap-allocated stack segments).

### Koka

```koka
effect yield
  ctl yield(x: int): ()

fun generator(): () yield ()
  for(100, 1) fn(i)
    yield(i)

fun consumer(): ()
  with handler
    ctl yield(x)
      println(x)
      resume(())
  generator()
```

Koka compiles effects via **evidence passing** and CPS transformation—no stack switching needed, but more compiler complexity.

### Comparison

| Language | Multi-shot? | Implementation | Overhead |
|----------|-------------|----------------|----------|
| WasmFX | No | Stack switching | Very low |
| OCaml 5 | No | Fibers | Very low |
| Koka | Yes (conceptually) | Evidence + CPS | Medium |
| Eff | Yes | Interpreter | High |

---

## Implications for Your Project

### Option A: Target WasmFX

If your goal is running untrusted code with suspend/resume:

**Pros:**
- Native efficiency (no rewriting overhead)
- Clean semantics (typed)
- Growing ecosystem (Wasmtime support)

**Cons:**
- No serialization (can't migrate to another machine)
- Requires WASM compilation step
- WasmFX still evolving (not in browsers yet)

### Option B: Apply WasmFX Design to .NET Rewriter

Adopt the **conceptual model** but implement via bytecode rewriting:

```csharp
// Conceptual API inspired by WasmFX

public class Tag<TOut, TIn> { }

public static class Continuation
{
    // suspend: yield TOut, receive TIn when resumed
    public static TIn Suspend<TOut, TIn>(Tag<TOut, TIn> tag, TOut value);

    // Create continuation from delegate
    public static Cont<T> New<T>(Action body);

    // Resume with handlers
    public static T Resume<T>(Cont<T> c, params Handler[] handlers);
}
```

Your rewriter would transform this into the save/restore blocks you originally built.

### Option C: Hybrid Approach

1. **Use WasmFX semantics** for the API design
2. **Use bytecode rewriting** for .NET implementation
3. **Add serialization** that WasmFX lacks

This gives you:
- Clean, well-typed API based on proven design
- Works on standard .NET (no runtime modifications)
- Unique capability: serializable continuations for migration

---

## Key Takeaways

1. **WasmFX validates your original approach** - the problem you solved is now getting first-class support in WASM

2. **Tags/effects are a cleaner model** than arbitrary yield points - they make the suspension protocol explicit

3. **One-shot is sufficient** for most use cases - you don't need multi-shot complexity

4. **Serialization remains your differentiator** - WasmFX continuations are opaque, yours are portable

5. **The type system matters** - WasmFX's typed continuations catch errors at compile time that your system would only catch at runtime

---

## References

- [WasmFX Explainer](https://wasmfx.dev/specs/explainer/)
- [WebAssembly Stack Switching Proposal](https://github.com/WebAssembly/stack-switching)
- [Continuing WebAssembly with Effect Handlers (OOPSLA 2023)](https://dl.acm.org/doi/10.1145/3622814)
- [Wasm/k: Delimited Continuations for WebAssembly](https://arxiv.org/abs/2010.01723)
- [Asyncify (Binaryen)](https://kripken.github.io/blog/wasm/2019/07/16/asyncify.html)
- [OCaml Effects Tutorial](https://github.com/ocaml-multicore/ocaml-effects-tutorial)
- [Algebraic Effects for Functional Programming (Koka)](https://www.microsoft.com/en-us/research/publication/algebraic-effects-for-functional-programming/)
- [Delimited Continuations (Oleg Kiselyov)](https://okmij.org/ftp/continuations/index.html)
