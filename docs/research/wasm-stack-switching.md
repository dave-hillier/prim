# WebAssembly stack switching

This document describes the WebAssembly stack-switching proposal (also called WasmFX or typed continuations) and compares it with Prim. Status and sources are in [notes/modern_runtime_systems.md](notes/modern_runtime_systems.md), section 2.

The proposal adds instructions for creating, suspending and resuming continuations. Prim gets the same control flow by rewriting .NET bytecode. The two differ in one way that matters for Prim's purpose: a WasmFX continuation is a reference inside one engine and cannot be serialized, and a Prim continuation is data.

## Status

As of November 2026 the proposal is at phase 3 of the WebAssembly standardization process. Wasmtime has an implementation that is off by default and limited to x86-64. V8 has one behind an experimental flag. No browser ships it.

## Why the proposal exists

Core WebAssembly has one stack per thread of execution and no way to switch between stacks or capture one. Compilers that need generators, async functions or lightweight threads have had to transform the whole program:

- continuation-passing style, which changes every function;
- Asyncify, a Binaryen pass that instruments functions to unwind and rewind, similar in approach to Prim;
- state machines, as the C# compiler generates for `async`.

These increase code size and run time. Asyncify's author reports about 50% larger code on average in the worst case, with slowdown roughly in proportion.

## Concepts

### Control tags

A control tag declares the types of the values passed out when a computation suspends and the types passed back when it resumes.

```wat
;; Suspending with this tag passes out an i32. Resuming passes nothing back.
(tag $yield (param i32))

;; Suspending passes out an i32. Resuming passes back an i64.
(tag $async_read (param i32) (result i64))
```

### Continuation types

A continuation type is built from a function type. The function type's parameters are the values needed to resume the continuation, and its results are the values it produces when it finishes.

```wat
(type $ft (func))
(type $ct (cont $ft))

(type $ft2 (func (param i32)))
(type $ct2 (cont $ft2))        ;; needs an i32 to start
```

### Instructions

| Instruction | Effect |
|---|---|
| `cont.new $ct` | Takes a function reference and returns a suspended continuation that will start at the function's entry. |
| `suspend $tag` | Suspends the current continuation up to the nearest enclosing handler for `$tag`. The handler receives the tag's parameters and a reference to the suspended continuation. |
| `resume $ct (on $tag $label)*` | Resumes a continuation and installs handlers. `(on $tag $label)` means: if the continuation suspends with `$tag`, branch to `$label`. |
| `cont.bind $ct1 $ct2` | Supplies some of a continuation's arguments in advance. |
| `resume_throw`, `resume_throw_ref` | Resumes a continuation by raising an exception at its suspension point. |
| `switch $ct $tag` | Transfers control directly from the current continuation to another one, without returning to a handler in between. |

A continuation can be resumed once. A second attempt traps.

The handler is not reinstalled automatically. When a continuation suspends, its handler receives a new continuation, and the code that resumes it supplies handlers again in the new `resume`. The paper calls this design "sheep handlers": like deep handlers, a continuation always runs under some handler; like shallow handlers, the handler can differ on each resume.

## Example: a generator

This is the proposal explainer's generator example with the tag renamed.

```wat
(module
  (func $print (import "spectest" "print_i32") (param i32))

  (type $ft (func))
  (type $ct (cont $ft))

  (tag $yield (param i32))

  ;; Yields 100, 99, ..., 1.
  (func $generator
    (local $i i32)
    (local.set $i (i32.const 100))
    (loop $loop
      (suspend $yield (local.get $i))
      ;; Execution continues here when resumed.
      (local.tee $i (i32.sub (local.get $i) (i32.const 1)))
      (br_if $loop)
    )
  )

  ;; A function used with cont.new must be declared.
  (elem declare func $generator)

  (func $consumer
    (local $c (ref $ct))
    (local.set $c (cont.new $ct (ref.func $generator)))

    (loop $loop
      (block $on_yield (result i32 (ref $ct))
        ;; If the generator suspends with $yield, branch to $on_yield.
        ;; If it returns, fall through.
        (resume $ct (on $yield $on_yield) (local.get $c))
        (return)
      )
      ;; The stack holds the yielded i32 and the new continuation.
      (local.set $c)
      (call $print)
      (br $loop)
    )
  )
)
```

The consumer creates the continuation and resumes it. Each `suspend` returns control to the consumer's `$on_yield` block with the value and a new continuation. The consumer stores the continuation, prints the value and resumes again. When the generator's loop ends, the function returns, `resume` falls through, and the consumer returns.

## Comparison with Prim and Second Life

| | Second Life rewriter | Prim | WasmFX |
|---|---|---|---|
| Where it is implemented | Rewriting compiled CIL | Rewriting compiled CIL, or generating C# | Instructions in the engine |
| How the stack is captured | Each frame saves itself and returns | An exception unwinds; each frame's handler saves it | The engine keeps the stack; nothing is copied |
| Where suspension can happen | Injected yield points at backward jumps | Injected yield points at backward jumps and calls | Explicit `suspend` |
| How it resumes | Dispatch at method entry | Dispatch at method entry | `resume` switches stacks |
| Form of the continuation | Serialized bytes | Chain of frame records, serializable | Reference, not serializable |
| Runtime requirement | Any .NET runtime | Any .NET runtime | An engine that implements the proposal |
| Times a continuation can be resumed | Any, after deserializing | Any, after deserializing | Once |

WasmFX checks statically that the values passed at `suspend` and `resume` match the tag. Prim's `ContinuationResult` and yielded values are typed `object` and checked at run time.

## Relation to delimited continuations

`resume` delimits the continuation that `suspend` captures. The captured continuation does not include the handler, and the handler's code runs outside the delimiter. In the terminology of delimited control this is closer to `control0`/`shift0` with named prompts than to `shift`/`reset`. The explainer does not describe the proposal in those terms.

## How engines implement it

Each continuation gets its own stack. `suspend` and `resume` save the callee-saved registers, swap stack pointers and continue on the other stack. Wasmtime implements this with a Cranelift `stack_switch` instruction. The prototype described in the 2023 paper instead reused Wasmtime's existing fiber API.

## Effect handlers in other languages

OCaml 5 has the same model: one-shot continuations on stacks allocated by the runtime. This is the generator in OCaml 5.3 or later:

```ocaml
open Effect
open Effect.Deep

type _ Effect.t += Yield : int -> unit Effect.t

let generator () =
  for i = 100 downto 1 do
    perform (Yield i)
  done

let consumer () =
  try generator () with
  | effect (Yield n), k ->
      print_int n;
      continue k ()
```

Koka compiles effect handlers without switching stacks, by transforming the program.

| | Resumable more than once | Implementation |
|---|---|---|
| WasmFX | No | Stack switching |
| OCaml 5 | No | Stack switching |
| Koka | Yes | Program transformation |

## What this means for Prim

- **Wasm is not a substitute.** Compiling scripts to Wasm and using stack switching would give cheap suspension, but the continuation could not be saved or moved, which is the reason Prim exists. Saving it would need a second mechanism, such as the checkpoint-injecting transformations in the Wasm migration literature.
- **Typed tags could be adopted.** A Prim API in which each suspension point declares the type it yields and the type it expects back would move checks from run time to compile time. A sketch:

  ```csharp
  public sealed class Tag<TOut, TIn> { }

  public static class Continuation
  {
      // Suspend, passing value out. Returns the value passed to resume.
      public static TIn Suspend<TOut, TIn>(Tag<TOut, TIn> tag, TOut value);
  }
  ```

  Prim does not implement this.
- **One-shot is not a constraint Prim shares.** A serialized Prim continuation can be deserialized and resumed any number of times.

## References

- [Stack-switching explainer](https://github.com/WebAssembly/stack-switching/blob/main/proposals/stack-switching/Explainer.md)
- [Proposal repository](https://github.com/WebAssembly/stack-switching)
- Phipps-Costin et al., "Continuing WebAssembly with Effect Handlers", *Proc. ACM Program. Lang.* 7(OOPSLA2), 2023. [doi:10.1145/3622814](https://doi.org/10.1145/3622814)
- Pinckney, Guha, Brun, "Wasm/k: Delimited Continuations for WebAssembly", DLS 2020. [arXiv 2010.01723](https://arxiv.org/abs/2010.01723)
- Zakai, "Pause and Resume WebAssembly with Binaryen's Asyncify", 2019. [Post](https://kripken.github.io/blog/wasm/2019/07/16/asyncify.html)
- [OCaml manual, effect handlers](https://ocaml.org/manual/effects.html)
