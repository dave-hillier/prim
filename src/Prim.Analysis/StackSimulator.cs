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
    /// depths agree but the per-slot types differ, they merge as the verifier merges them
    /// (ECMA-335 III.1.8.1.3): int32-family types to int32, object references to their
    /// closest common base class, the null literal into any object reference. A merge the
    /// simulator cannot name with confidence yields <see cref="UnknownType"/>.
    /// </para>
    /// </summary>
    public sealed class StackSimulator
    {
        /// <summary>
        /// Stack slot type for a value the simulator cannot type with confidence (for
        /// example a method pointer, or a join of unrelated value types). A transformer must
        /// not spill such a slot.
        /// </summary>
        public static readonly TypeReference UnknownType = new TypeReference("", "<unknown>", null, null);

        /// <summary>
        /// Stack slot type for the null literal. It merges into any object reference; a
        /// slot still typed null at a yield point holds null on every path.
        /// </summary>
        public static readonly TypeReference NullType = new TypeReference("", "<null>", null, null);

        private readonly MethodDefinition _method;
        private readonly Dictionary<int, StackState> _stateAtOffset = new Dictionary<int, StackState>();
        private TypeReference _thisType;

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
        /// slot types merge as described on the class.
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

            var ca = Category(a);
            var cb = Category(b);

            // A managed or unmanaged pointer must stay recognisable as one; widening it
            // would hide that it cannot be spilled.
            if (ca == StackCategory.ByRef || ca == StackCategory.Pointer) return a;
            if (cb == StackCategory.ByRef || cb == StackCategory.Pointer) return b;

            if (ca == StackCategory.Unknown || cb == StackCategory.Unknown) return UnknownType;

            // The null literal merges into any object reference.
            if (ca == StackCategory.Null) return cb == StackCategory.Reference ? b : UnknownType;
            if (cb == StackCategory.Null) return ca == StackCategory.Reference ? a : UnknownType;

            var ts = _method.Module.TypeSystem;
            if (ca == cb)
            {
                switch (ca)
                {
                    // bool, char, int8..uint32 are all int32 on the evaluation stack.
                    case StackCategory.Int32: return ts.Int32;
                    case StackCategory.Int64: return ts.Int64;
                    case StackCategory.NativeInt: return ts.IntPtr;
                    case StackCategory.Float: return ts.Double;
                    case StackCategory.Reference: return CommonBaseClass(a, b);
                }
            }

            // Different value types, a generic parameter against anything else, or kinds
            // that cannot meet in verifiable IL.
            return UnknownType;
        }

        /// <summary>
        /// The closest common base class of two object references, as the verifier
        /// computes it (ECMA-335 III.1.8.1.3), or <see cref="UnknownType"/> when it cannot be
        /// named with confidence (arrays, generic instances, interfaces, unresolvable types,
        /// or a common base defined outside this module other than System.Object).
        /// </summary>
        private TypeReference CommonBaseClass(TypeReference a, TypeReference b)
        {
            var ts = _method.Module.TypeSystem;
            if (a.FullName == "System.Object" || b.FullName == "System.Object") return ts.Object;

            var chainA = BaseChain(a);
            var chainB = BaseChain(b);
            if (chainA == null || chainB == null) return UnknownType;

            foreach (var candidate in chainB)
            {
                var match = chainA.FirstOrDefault(t => t.FullName == candidate.FullName);
                if (match == null) continue;
                if (match.FullName == "System.Object") return ts.Object;
                return match.Module == _method.Module ? match : UnknownType;
            }
            return UnknownType;
        }

        private static List<TypeReference> BaseChain(TypeReference type)
        {
            var chain = new List<TypeReference>();
            var current = type;
            while (current != null)
            {
                if (current.IsArray || current.IsGenericInstance || current.IsGenericParameter) return null;
                TypeDefinition definition;
                try
                {
                    definition = current.Resolve();
                }
                catch (AssemblyResolutionException)
                {
                    return null;
                }
                if (definition == null || definition.IsInterface) return null;
                chain.Add(current);
                current = definition.BaseType;
            }
            return chain;
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
        /// Determines the type pushed by an instruction at push-slot <paramref name="index"/>,
        /// following ECMA-335 III.1.5 (evaluation-stack types) for every opcode that pushes.
        /// Member signatures are resolved against their generic instantiation. A value the
        /// simulator cannot type with confidence (a method pointer, a typed reference, ...)
        /// is <see cref="UnknownType"/>; the null literal is <see cref="NullType"/>.
        /// </summary>
        private TypeReference GetPushType(Instruction instruction, int index, List<TypeReference> stackBeforePush)
        {
            var ts = _method.Module.TypeSystem;
            var code = instruction.OpCode.Code;

            TypeReference Top(int depth)
            {
                int i = stackBeforePush.Count - 1 - depth;
                return i >= 0 ? stackBeforePush[i] ?? UnknownType : UnknownType;
            }

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
                    return NullType;
                case Code.Ldstr:
                    return ts.String;
                case Code.Box:
                    return ts.Object;

                case Code.Ldloc:
                case Code.Ldloc_S:
                    return instruction.Operand is VariableDefinition v ? v.VariableType : UnknownType;
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
                    return instruction.Operand is ParameterDefinition p ? ParameterType(p) : UnknownType;
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
                    return instruction.Operand is FieldReference field
                        ? TypeSubstitution.FieldType(field)
                        : UnknownType;

                // Address-of loads push a managed pointer. Modelling it as a byref (rather
                // than widening to object) lets the transformer see that such a slot cannot
                // be spilled into an object[] frame record.
                case Code.Ldloca:
                case Code.Ldloca_S:
                    return instruction.Operand is VariableDefinition addrLocal
                        ? new ByReferenceType(addrLocal.VariableType)
                        : UnknownType;
                case Code.Ldarga:
                case Code.Ldarga_S:
                    if (instruction.Operand is ParameterDefinition addrParam)
                    {
                        var paramType = ParameterType(addrParam);
                        return paramType.IsByReference ? paramType : new ByReferenceType(paramType);
                    }
                    return UnknownType;
                case Code.Ldflda:
                case Code.Ldsflda:
                    return instruction.Operand is FieldReference addrField
                        ? new ByReferenceType(TypeSubstitution.FieldType(addrField))
                        : UnknownType;
                case Code.Ldelema:
                case Code.Unbox:
                case Code.Refanyval:
                    return instruction.Operand is TypeReference addrType
                        ? new ByReferenceType(addrType)
                        : UnknownType;

                case Code.Dup:
                    return Top(0);

                case Code.Newobj:
                    return instruction.Operand is MethodReference ctor ? ctor.DeclaringType : UnknownType;

                case Code.Call:
                case Code.Callvirt:
                    return instruction.Operand is MethodReference method
                        ? TypeSubstitution.ReturnType(method)
                        : UnknownType;
                case Code.Calli:
                    return instruction.Operand is CallSite cs
                        ? TypeSubstitution.Substitute(cs.ReturnType, null, null)
                        : UnknownType;

                // Binary numeric / bitwise operations: the result type follows the operands
                // (ECMA-335 III.1.5, tables III.2 and III.5). A temp typed 'object' would make
                // the post-resume reload + numeric op unverifiable.
                case Code.Add:
                case Code.Sub:
                case Code.Mul:
                case Code.Div:
                case Code.Div_Un:
                case Code.Rem:
                case Code.Rem_Un:
                case Code.And:
                case Code.Or:
                case Code.Xor:
                case Code.Add_Ovf:
                case Code.Add_Ovf_Un:
                case Code.Sub_Ovf:
                case Code.Sub_Ovf_Un:
                case Code.Mul_Ovf:
                case Code.Mul_Ovf_Un:
                    return BinaryNumericResult(Top(1), Top(0));

                // Shifts: the result has the type of the value shifted (table III.6).
                case Code.Shl:
                case Code.Shr:
                case Code.Shr_Un:
                    return StackNormalized(Top(1));

                case Code.Neg:
                case Code.Not:
                case Code.Ckfinite:
                    return StackNormalized(Top(0));

                // Conversions (table III.8): narrow integers widen to int32 on the stack.
                case Code.Conv_I1:
                case Code.Conv_I2:
                case Code.Conv_I4:
                case Code.Conv_U1:
                case Code.Conv_U2:
                case Code.Conv_U4:
                case Code.Conv_Ovf_I1:
                case Code.Conv_Ovf_I2:
                case Code.Conv_Ovf_I4:
                case Code.Conv_Ovf_U1:
                case Code.Conv_Ovf_U2:
                case Code.Conv_Ovf_U4:
                case Code.Conv_Ovf_I1_Un:
                case Code.Conv_Ovf_I2_Un:
                case Code.Conv_Ovf_I4_Un:
                case Code.Conv_Ovf_U1_Un:
                case Code.Conv_Ovf_U2_Un:
                case Code.Conv_Ovf_U4_Un:
                    return ts.Int32;
                case Code.Conv_I8:
                case Code.Conv_U8:
                case Code.Conv_Ovf_I8:
                case Code.Conv_Ovf_U8:
                case Code.Conv_Ovf_I8_Un:
                case Code.Conv_Ovf_U8_Un:
                    return ts.Int64;
                case Code.Conv_I:
                case Code.Conv_U:
                case Code.Conv_Ovf_I:
                case Code.Conv_Ovf_U:
                case Code.Conv_Ovf_I_Un:
                case Code.Conv_Ovf_U_Un:
                    return ts.IntPtr;
                case Code.Conv_R4:
                    return ts.Single;
                case Code.Conv_R8:
                case Code.Conv_R_Un:
                    return ts.Double;

                // Comparisons push an int32 0/1 (table III.4).
                case Code.Ceq:
                case Code.Cgt:
                case Code.Cgt_Un:
                case Code.Clt:
                case Code.Clt_Un:
                    return ts.Int32;

                case Code.Ldlen:
                    return ts.IntPtr;
                case Code.Sizeof:
                    return ts.Int32;

                case Code.Ldelem_I1:
                case Code.Ldelem_U1:
                case Code.Ldelem_I2:
                case Code.Ldelem_U2:
                case Code.Ldelem_I4:
                case Code.Ldelem_U4:
                    return ts.Int32;
                case Code.Ldelem_I8:
                    return ts.Int64;
                case Code.Ldelem_I:
                    return ts.IntPtr;
                case Code.Ldelem_R4:
                    return ts.Single;
                case Code.Ldelem_R8:
                    return ts.Double;
                case Code.Ldelem_Ref:
                    return Top(1) is ArrayType array && array.IsVector ? array.ElementType : UnknownType;
                case Code.Ldelem_Any:
                case Code.Ldobj:
                case Code.Unbox_Any:
                    return instruction.Operand is TypeReference valueType ? valueType : UnknownType;

                case Code.Ldind_I1:
                case Code.Ldind_U1:
                case Code.Ldind_I2:
                case Code.Ldind_U2:
                case Code.Ldind_I4:
                case Code.Ldind_U4:
                    return ts.Int32;
                case Code.Ldind_I8:
                    return ts.Int64;
                case Code.Ldind_I:
                    return ts.IntPtr;
                case Code.Ldind_R4:
                    return ts.Single;
                case Code.Ldind_R8:
                    return ts.Double;
                case Code.Ldind_Ref:
                    return Top(0) is ByReferenceType pointee ? pointee.ElementType : UnknownType;

                // isinst/castclass push an object reference: the operand type for a
                // reference type, a boxed value (typed as object here) for a value type or a
                // generic parameter.
                case Code.Isinst:
                case Code.Castclass:
                    if (instruction.Operand is TypeReference target)
                    {
                        return IsPossiblyValueType(target) ? ts.Object : target;
                    }
                    return UnknownType;

                case Code.Localloc:
                    return new PointerType(ts.Byte);

                // ldftn/ldvirtftn (method pointers), ldtoken, arglist, mkrefany and
                // refanytype push values the rewriter never needs to spill; if one is live
                // at a yield point that yield point is skipped.
                default:
                    return UnknownType;
            }
        }

        private static bool IsPossiblyValueType(TypeReference type)
        {
            if (type.IsGenericParameter || type.IsValueType) return true;
            if (type.IsArray) return false;
            try
            {
                var definition = type.Resolve();
                return definition == null || definition.IsValueType;
            }
            catch (AssemblyResolutionException)
            {
                return true;
            }
        }

        /// <summary>
        /// Result type of a binary numeric/bitwise op (ECMA-335 III.1.5, table III.2):
        /// int32 op int32 -> int32, int64 op int64 -> int64, native int with int32 or native
        /// int -> native int, F op F -> F. Byref arithmetic stays a byref. Anything else is
        /// not valid IL and is typed <see cref="UnknownType"/>.
        /// </summary>
        private TypeReference BinaryNumericResult(TypeReference a, TypeReference b)
        {
            var ts = _method.Module.TypeSystem;
            var ca = Category(a);
            var cb = Category(b);

            if (ca == StackCategory.ByRef || ca == StackCategory.Pointer) return a;
            if (cb == StackCategory.ByRef || cb == StackCategory.Pointer) return b;
            if (a.FullName == b.FullName && IsPrimitiveNumeric(a.FullName)) return a;

            if (ca == StackCategory.Int32 && cb == StackCategory.Int32) return ts.Int32;
            if (ca == StackCategory.Int64 && cb == StackCategory.Int64) return ts.Int64;
            if ((ca == StackCategory.NativeInt && (cb == StackCategory.NativeInt || cb == StackCategory.Int32)) ||
                (cb == StackCategory.NativeInt && ca == StackCategory.Int32))
            {
                return ts.IntPtr;
            }
            if (ca == StackCategory.Float && cb == StackCategory.Float) return ts.Double;
            return UnknownType;
        }

        /// <summary>
        /// The type itself for a primitive numeric type; the stack type for an enum (so a
        /// negated enum is typed by its underlying stack type); unknown for anything else.
        /// </summary>
        private TypeReference StackNormalized(TypeReference type)
        {
            if (type != null && IsPrimitiveNumeric(type.FullName)) return type;
            var ts = _method.Module.TypeSystem;
            switch (Category(type))
            {
                case StackCategory.Int32: return ts.Int32;
                case StackCategory.Int64: return ts.Int64;
                case StackCategory.NativeInt: return ts.IntPtr;
                case StackCategory.Float: return ts.Double;
                default: return UnknownType;
            }
        }

        private static bool IsPrimitiveNumeric(string fullName)
        {
            switch (fullName)
            {
                case "System.Boolean":
                case "System.Char":
                case "System.SByte":
                case "System.Byte":
                case "System.Int16":
                case "System.UInt16":
                case "System.Int32":
                case "System.UInt32":
                case "System.Int64":
                case "System.UInt64":
                case "System.IntPtr":
                case "System.UIntPtr":
                case "System.Single":
                case "System.Double":
                    return true;
                default:
                    return false;
            }
        }

        private enum StackCategory
        {
            Int32,
            Int64,
            NativeInt,
            Float,
            Reference,
            Value,
            ByRef,
            Pointer,
            GenericParameter,
            Null,
            Unknown,
        }

        /// <summary>
        /// The ECMA-335 evaluation-stack category of a type (III.1.5): small integers, bool
        /// and char are int32; enums take their underlying type.
        /// </summary>
        private static StackCategory Category(TypeReference type)
        {
            if (type == null || ReferenceEquals(type, UnknownType)) return StackCategory.Unknown;
            if (ReferenceEquals(type, NullType)) return StackCategory.Null;
            if (type is RequiredModifierType req) return Category(req.ElementType);
            if (type is OptionalModifierType opt) return Category(opt.ElementType);
            if (type.IsByReference) return StackCategory.ByRef;
            if (type.IsPointer || type.IsFunctionPointer) return StackCategory.Pointer;
            if (type.IsGenericParameter) return StackCategory.GenericParameter;

            switch (type.FullName)
            {
                case "System.Boolean":
                case "System.Char":
                case "System.SByte":
                case "System.Byte":
                case "System.Int16":
                case "System.UInt16":
                case "System.Int32":
                case "System.UInt32":
                    return StackCategory.Int32;
                case "System.Int64":
                case "System.UInt64":
                    return StackCategory.Int64;
                case "System.IntPtr":
                case "System.UIntPtr":
                    return StackCategory.NativeInt;
                case "System.Single":
                case "System.Double":
                    return StackCategory.Float;
            }

            if (type.IsArray) return StackCategory.Reference;

            TypeDefinition definition = null;
            try
            {
                definition = type.Resolve();
            }
            catch (AssemblyResolutionException)
            {
            }

            if (definition != null && definition.IsEnum)
            {
                var underlying = definition.Fields.FirstOrDefault(f => !f.IsStatic)?.FieldType;
                return underlying != null ? Category(underlying) : StackCategory.Unknown;
            }
            if (type.IsValueType || (definition != null && definition.IsValueType)) return StackCategory.Value;
            return StackCategory.Reference;
        }

        private TypeReference LocalType(int index)
        {
            var vars = _method.Body.Variables;
            return index < vars.Count ? vars[index].VariableType : UnknownType;
        }

        private TypeReference ParameterType(ParameterDefinition parameter)
        {
            // 'ldarg 0' in its long form names the implicit 'this' parameter.
            if (_method.HasThis && (parameter.Index < 0 || parameter == _method.Body.ThisParameter))
            {
                return ThisType();
            }
            return parameter.ParameterType;
        }

        private TypeReference ArgType(int index)
        {
            // For instance methods, arg0 is 'this'.
            if (_method.HasThis)
            {
                if (index == 0) return ThisType();
                int paramIndex = index - 1;
                return paramIndex < _method.Parameters.Count
                    ? _method.Parameters[paramIndex].ParameterType
                    : UnknownType;
            }
            return index < _method.Parameters.Count
                ? _method.Parameters[index].ParameterType
                : UnknownType;
        }

        /// <summary>
        /// The type of <c>this</c> inside the method: the declaring type instantiated over its
        /// own generic parameters (<c>G&lt;T&gt;</c>, not the open <c>G`1</c>, which is not a
        /// valid type in a signature), and a managed pointer to it for a value type
        /// (ECMA-335 II.13.3).
        /// </summary>
        private TypeReference ThisType()
        {
            if (_thisType == null)
            {
                var self = TypeSubstitution.SelfType(_method.DeclaringType);
                _thisType = _method.DeclaringType.IsValueType ? new ByReferenceType(self) : self;
            }
            return _thisType;
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
