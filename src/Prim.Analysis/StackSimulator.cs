using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Prim.Analysis
{
    /// <summary>
    /// Represents the state of the evaluation stack at a point in the method.
    /// </summary>
    public sealed class StackState
    {
        /// <summary>
        /// The depth of the stack (number of items).
        /// </summary>
        public int Depth { get; }

        /// <summary>
        /// The types of items on the stack (bottom to top).
        /// </summary>
        public TypeReference[] Types { get; }

        public StackState(int depth, TypeReference[] types)
        {
            Depth = depth;
            Types = types ?? Array.Empty<TypeReference>();
        }

        public static StackState Empty => new StackState(0, Array.Empty<TypeReference>());
    }

    /// <summary>
    /// Simulates the evaluation stack through IL instructions using a forward
    /// dataflow worklist over the control-flow graph.
    ///
    /// <para>
    /// Unlike a naive linear pass, this follows CFG edges (fall-through, branch
    /// targets, leave targets), seeds exception-handler/filter entry blocks with the
    /// exception object, stops carrying depth past unconditional control transfers
    /// (br/throw/ret/endfinally/leave), and merges states at join points to converge
    /// at loop headers.
    /// </para>
    ///
    /// <para>
    /// Merge policy: at a join point the depths of all incoming edges must agree.
    /// This is required by ECMA-335 for verifiable IL. If they disagree the simulator
    /// throws <see cref="InvalidProgramException"/> (the input IL is not valid). When
    /// depths agree but the per-slot types differ, the merged slot type is widened to
    /// <c>System.Object</c> (a safe common supertype the spiller can box into).
    /// </para>
    /// </summary>
    public sealed class StackSimulator
    {
        private readonly MethodDefinition _method;
        private readonly Dictionary<int, StackState> _stateAtOffset = new Dictionary<int, StackState>();

        public StackSimulator(MethodDefinition method)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
        }

        /// <summary>
        /// Gets the stack state at a specific instruction offset.
        /// </summary>
        public StackState GetStateAt(int offset)
        {
            return _stateAtOffset.TryGetValue(offset, out var state) ? state : StackState.Empty;
        }

        /// <summary>
        /// Simulates the entire method to compute stack states using a forward
        /// dataflow worklist over the control-flow graph.
        /// </summary>
        public void Simulate()
        {
            if (!_method.HasBody) return;

            var body = _method.Body;
            var instructions = body.Instructions;
            if (instructions.Count == 0) return;

            // Compute instruction offsets (Cecil doesn't auto-compute for programmatic
            // assemblies). The CFG build will recompute identically, which is fine.
            ComputeOffsets(instructions);

            var cfg = ControlFlowGraph.Build(_method);

            // Per-block entry states (stack contents on entry to the block).
            var blockEntry = new Dictionary<BasicBlock, List<TypeReference>>();
            var worklist = new Queue<BasicBlock>();
            var inWorklist = new HashSet<BasicBlock>();

            void Enqueue(BasicBlock block)
            {
                if (block != null && inWorklist.Add(block))
                {
                    worklist.Enqueue(block);
                }
            }

            // (1) Seed the entry block with an empty stack.
            if (cfg.EntryBlock != null)
            {
                blockEntry[cfg.EntryBlock] = new List<TypeReference>();
                Enqueue(cfg.EntryBlock);
            }

            // (1b) Seed each exception handler/filter entry block with a stack of
            // depth 1 (the exception object). For catch the type is the CatchType;
            // for filter/finally/fault we use System.Object.
            foreach (var handler in body.ExceptionHandlers)
            {
                if (handler.FilterStart != null &&
                    cfg.OffsetToBlock.TryGetValue(handler.FilterStart.Offset, out var filterBlock))
                {
                    SeedHandler(blockEntry, filterBlock, _method.Module.TypeSystem.Object);
                    Enqueue(filterBlock);
                }

                if (handler.HandlerStart != null &&
                    cfg.OffsetToBlock.TryGetValue(handler.HandlerStart.Offset, out var handlerBlock))
                {
                    if (handler.HandlerType == ExceptionHandlerType.Catch)
                    {
                        var catchType = handler.CatchType ?? _method.Module.TypeSystem.Object;
                        SeedHandler(blockEntry, handlerBlock, catchType);
                    }
                    else if (handler.HandlerType == ExceptionHandlerType.Filter)
                    {
                        // The handler of a filtered clause is entered with the
                        // exception object on the stack (depth 1), exactly like a
                        // catch. The filter clause consumes its OWN copy via endfilter;
                        // that is independent of the handler body. (ECMA-335 III.)
                        SeedHandler(blockEntry, handlerBlock, _method.Module.TypeSystem.Object);
                    }
                    else
                    {
                        // Finally/fault handlers begin with an empty evaluation stack.
                        if (!blockEntry.ContainsKey(handlerBlock))
                        {
                            blockEntry[handlerBlock] = new List<TypeReference>();
                        }
                    }
                    Enqueue(handlerBlock);
                }
            }

            // (2) Process blocks from the worklist.
            while (worklist.Count > 0)
            {
                var block = worklist.Dequeue();
                inWorklist.Remove(block);

                if (!blockEntry.TryGetValue(block, out var entry))
                {
                    // Unreachable from any seeded block; skip.
                    continue;
                }

                // Simulate the block, recording state at each instruction offset.
                var stack = new List<TypeReference>(entry);
                bool fallsThrough = SimulateBlock(block, stack);

                // Propagate the resulting state to successors. Fall-through and
                // branch/leave targets are all CFG successors; if the block ends in
                // an unconditional transfer (br/ret/throw/leave/endfinally) we do not
                // model fall-through, but those edges are not present as fall-through
                // successors in the CFG anyway. We only suppress propagation entirely
                // when the terminator is ret/throw/rethrow/endfinally (no successors
                // carry the stack).
                if (!fallsThrough && BlockEndsWithStackTerminator(block))
                {
                    continue;
                }

                foreach (var succ in block.Successors)
                {
                    if (MergeInto(blockEntry, succ, stack))
                    {
                        Enqueue(succ);
                    }
                }
            }
        }

        private static void SeedHandler(
            Dictionary<BasicBlock, List<TypeReference>> blockEntry,
            BasicBlock block,
            TypeReference exceptionType)
        {
            if (!blockEntry.ContainsKey(block))
            {
                blockEntry[block] = new List<TypeReference> { exceptionType };
            }
        }

        /// <summary>
        /// Simulates a block's instructions, recording the stack state at each
        /// instruction offset (state BEFORE the instruction executes). Mutates
        /// <paramref name="stack"/> to the state on exit. Returns false if the block
        /// ends in a terminator that does not fall through (br/ret/throw/etc.).
        /// </summary>
        private bool SimulateBlock(BasicBlock block, List<TypeReference> stack)
        {
            for (int idx = 0; idx < block.Instructions.Count; idx++)
            {
                var instruction = block.Instructions[idx];

                // Record state before instruction.
                _stateAtOffset[instruction.Offset] = new StackState(stack.Count, stack.ToArray());

                var (pop, push) = GetStackEffect(instruction);

                // Snapshot the stack BEFORE popping so push-type inference that
                // depends on consumed operands (e.g. dup replicating the top type)
                // sees the real operands, not the already-popped stack.
                var stackBeforePop = new List<TypeReference>(stack);

                if (instruction.OpCode.StackBehaviourPop == StackBehaviour.PopAll)
                {
                    // leave/leave.s empty the evaluation stack (ECMA-335 III.3.55).
                    stack.Clear();
                }
                else
                {
                    for (int i = 0; i < pop && stack.Count > 0; i++)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                }

                for (int i = 0; i < push; i++)
                {
                    stack.Add(GetPushType(instruction, i, stackBeforePop));
                }
            }

            // (4) After a non-falling-through terminator, do not fall through.
            var last = block.Instructions.Count > 0
                ? block.Instructions[block.Instructions.Count - 1]
                : null;
            return last == null || !IsNonFallThroughTerminator(last);
        }

        private static bool IsNonFallThroughTerminator(Instruction instr)
        {
            switch (instr.OpCode.Code)
            {
                case Code.Br:
                case Code.Br_S:
                case Code.Leave:
                case Code.Leave_S:
                case Code.Ret:
                case Code.Throw:
                case Code.Rethrow:
                case Code.Endfinally:
                case Code.Endfilter:
                    return true;
                default:
                    return false;
            }
        }

        private static bool BlockEndsWithStackTerminator(BasicBlock block)
        {
            if (block.Instructions.Count == 0) return false;
            var last = block.Instructions[block.Instructions.Count - 1];
            switch (last.OpCode.Code)
            {
                case Code.Ret:
                case Code.Throw:
                case Code.Rethrow:
                case Code.Endfinally:
                case Code.Endfilter:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Merges <paramref name="incoming"/> into the recorded entry state of
        /// <paramref name="succ"/>. Returns true if the entry state changed (and the
        /// successor must be re-enqueued). Depths must agree (ECMA-335); differing
        /// slot types widen to System.Object.
        /// </summary>
        private bool MergeInto(
            Dictionary<BasicBlock, List<TypeReference>> blockEntry,
            BasicBlock succ,
            List<TypeReference> incoming)
        {
            if (!blockEntry.TryGetValue(succ, out var existing))
            {
                blockEntry[succ] = new List<TypeReference>(incoming);
                return true;
            }

            // (3) Depths must agree at a join point.
            if (existing.Count != incoming.Count)
            {
                throw new InvalidProgramException(
                    $"Stack depth mismatch at IL_{succ.StartOffset:X4} in {_method.FullName}: " +
                    $"existing depth {existing.Count} vs incoming depth {incoming.Count}. " +
                    "The input IL is not verifiable.");
            }

            bool changed = false;
            for (int i = 0; i < existing.Count; i++)
            {
                var merged = MergeType(existing[i], incoming[i]);
                if (!ReferenceEquals(merged, existing[i]))
                {
                    existing[i] = merged;
                    changed = true;
                }
            }

            return changed;
        }

        private TypeReference MergeType(TypeReference a, TypeReference b)
        {
            if (a == null) return b;
            if (b == null) return a;
            if (a.FullName == b.FullName) return a;
            // Differing types widen to a safe common supertype.
            return _method.Module.TypeSystem.Object;
        }

        /// <summary>
        /// Gets the number of items popped and pushed by an instruction.
        /// </summary>
        private (int pop, int push) GetStackEffect(Instruction instruction)
        {
            var opcode = instruction.OpCode;

            var behavior = opcode.StackBehaviourPop;
            var pushBehavior = opcode.StackBehaviourPush;

            int pop = behavior switch
            {
                StackBehaviour.Pop0 => 0,
                StackBehaviour.Pop1 => 1,
                StackBehaviour.Pop1_pop1 => 2,
                StackBehaviour.Popi => 1,
                StackBehaviour.Popi_pop1 => 2,
                StackBehaviour.Popi_popi => 2,
                StackBehaviour.Popi_popi8 => 2,
                StackBehaviour.Popi_popi_popi => 3,
                StackBehaviour.Popi_popr4 => 2,
                StackBehaviour.Popi_popr8 => 2,
                StackBehaviour.Popref => 1,
                StackBehaviour.Popref_pop1 => 2,
                StackBehaviour.Popref_popi => 2,
                StackBehaviour.Popref_popi_popi => 3,
                StackBehaviour.Popref_popi_popi8 => 3,
                StackBehaviour.Popref_popi_popr4 => 3,
                StackBehaviour.Popref_popi_popr8 => 3,
                StackBehaviour.Popref_popi_popref => 3,
                StackBehaviour.PopAll => 0, // handled specially below (leave clears)
                StackBehaviour.Varpop => GetVarPop(instruction),
                _ => 0
            };

            int push = pushBehavior switch
            {
                StackBehaviour.Push0 => 0,
                StackBehaviour.Push1 => 1,
                StackBehaviour.Push1_push1 => 2,
                StackBehaviour.Pushi => 1,
                StackBehaviour.Pushi8 => 1,
                StackBehaviour.Pushr4 => 1,
                StackBehaviour.Pushr8 => 1,
                StackBehaviour.Pushref => 1,
                StackBehaviour.Varpush => GetVarPush(instruction),
                _ => 0
            };

            return (pop, push);
        }

        private int GetVarPop(Instruction instruction)
        {
            var code = instruction.OpCode.Code;

            // (5) newobj: pops the constructor argument count (no 'this').
            if (code == Code.Newobj)
            {
                if (instruction.Operand is MethodReference ctor)
                {
                    return ctor.Parameters.Count;
                }
                return 0;
            }

            // (5) calli: operand is a CallSite, not a MethodReference. Pops the
            // declared parameters, +1 for the function pointer, +1 if HasThis.
            if (code == Code.Calli)
            {
                if (instruction.Operand is CallSite callSite)
                {
                    int count = callSite.Parameters.Count + 1; // + function pointer
                    if (callSite.HasThis)
                    {
                        count++;
                    }
                    return count;
                }
                return 0;
            }

            // ret: pops 1 if the method returns a value, else 0.
            if (code == Code.Ret)
            {
                return _method.ReturnType.FullName == "System.Void" ? 0 : 1;
            }

            // call / callvirt and other variable-pop method instructions.
            if (instruction.Operand is MethodReference method)
            {
                var count = method.Parameters.Count;
                if (method.HasThis && code != Code.Newobj)
                {
                    count++; // include 'this'
                }
                return count;
            }

            return 0;
        }

        private int GetVarPush(Instruction instruction)
        {
            var code = instruction.OpCode.Code;

            // (5) newobj always pushes exactly 1 (the constructed object).
            if (code == Code.Newobj)
            {
                return 1;
            }

            // (5) calli pushes 1 iff the CallSite return type is non-void.
            if (code == Code.Calli)
            {
                if (instruction.Operand is CallSite callSite)
                {
                    return callSite.ReturnType.FullName != "System.Void" ? 1 : 0;
                }
                return 0;
            }

            if (instruction.Operand is MethodReference method)
            {
                if (method.ReturnType.FullName != "System.Void")
                {
                    return 1;
                }
            }

            return 0;
        }

        /// <summary>
        /// Determines the type pushed by an instruction at push-slot <paramref name="index"/>.
        /// Covers the common cases the eval-stack spiller needs; falls back to
        /// System.Object for anything not modeled.
        /// </summary>
        private TypeReference GetPushType(Instruction instruction, int index, List<TypeReference> stackBeforePush)
        {
            var ts = _method.Module.TypeSystem;
            var code = instruction.OpCode.Code;

            switch (code)
            {
                case Code.Ldc_I4:
                case Code.Ldc_I4_S:
                case Code.Ldc_I4_0:
                case Code.Ldc_I4_1:
                case Code.Ldc_I4_2:
                case Code.Ldc_I4_3:
                case Code.Ldc_I4_4:
                case Code.Ldc_I4_5:
                case Code.Ldc_I4_6:
                case Code.Ldc_I4_7:
                case Code.Ldc_I4_8:
                case Code.Ldc_I4_M1:
                    return ts.Int32;
                case Code.Ldc_I8:
                    return ts.Int64;
                case Code.Ldc_R4:
                    return ts.Single;
                case Code.Ldc_R8:
                    return ts.Double;
                case Code.Ldnull:
                    return ts.Object;
                case Code.Ldstr:
                    return ts.String;
                case Code.Box:
                    return ts.Object;

                case Code.Ldloc:
                case Code.Ldloc_S:
                    if (instruction.Operand is VariableDefinition v)
                        return v.VariableType;
                    break;
                case Code.Ldloc_0:
                    return LocalType(0);
                case Code.Ldloc_1:
                    return LocalType(1);
                case Code.Ldloc_2:
                    return LocalType(2);
                case Code.Ldloc_3:
                    return LocalType(3);

                case Code.Ldarg:
                case Code.Ldarg_S:
                    if (instruction.Operand is ParameterDefinition p)
                        return p.ParameterType;
                    break;
                case Code.Ldarg_0:
                    return ArgType(0);
                case Code.Ldarg_1:
                    return ArgType(1);
                case Code.Ldarg_2:
                    return ArgType(2);
                case Code.Ldarg_3:
                    return ArgType(3);

                case Code.Ldsfld:
                case Code.Ldfld:
                    if (instruction.Operand is FieldReference field)
                        return field.FieldType;
                    break;

                case Code.Dup:
                    // Replicate the current top type.
                    if (stackBeforePush.Count > 0)
                        return stackBeforePush[stackBeforePush.Count - 1];
                    return ts.Object;

                case Code.Newobj:
                    if (instruction.Operand is MethodReference ctor)
                        return ctor.DeclaringType;
                    return ts.Object;
            }

            // Method-call return values (call / callvirt / calli).
            if (instruction.Operand is MethodReference method &&
                method.ReturnType.FullName != "System.Void")
            {
                return method.ReturnType;
            }
            if (code == Code.Calli && instruction.Operand is CallSite cs &&
                cs.ReturnType.FullName != "System.Void")
            {
                return cs.ReturnType;
            }

            if (instruction.Operand is FieldReference f)
            {
                return f.FieldType;
            }

            return ts.Object;
        }

        private TypeReference LocalType(int index)
        {
            var vars = _method.Body.Variables;
            return index < vars.Count ? vars[index].VariableType : _method.Module.TypeSystem.Object;
        }

        private TypeReference ArgType(int index)
        {
            // For instance methods, arg0 is 'this'.
            if (_method.HasThis)
            {
                if (index == 0) return _method.DeclaringType;
                int paramIndex = index - 1;
                return paramIndex < _method.Parameters.Count
                    ? _method.Parameters[paramIndex].ParameterType
                    : _method.Module.TypeSystem.Object;
            }
            return index < _method.Parameters.Count
                ? _method.Parameters[index].ParameterType
                : _method.Module.TypeSystem.Object;
        }

        /// <summary>
        /// Computes instruction offsets for programmatically created assemblies.
        /// Cecil doesn't auto-compute offsets until assembly is written/read.
        /// </summary>
        private static void ComputeOffsets(Mono.Collections.Generic.Collection<Instruction> instructions)
        {
            int offset = 0;
            foreach (var instruction in instructions)
            {
                instruction.Offset = offset;
                offset += instruction.GetSize();
            }
        }
    }
}
