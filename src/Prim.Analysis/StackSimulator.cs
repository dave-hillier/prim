using System;
using System.Collections.Generic;
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
    /// Simulates the evaluation stack through IL instructions.
    /// Tracks stack depth and types at each instruction.
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
        /// Simulates the entire method to compute stack states.
        /// </summary>
        public void Simulate()
        {
            if (!_method.HasBody) return;

            var body = _method.Body;
            var instructions = body.Instructions;

            // Compute instruction offsets (Cecil doesn't auto-compute for programmatic assemblies)
            ComputeOffsets(instructions);

            // Simple forward simulation
            var currentDepth = 0;
            var stack = new List<TypeReference>();

            foreach (var instruction in instructions)
            {
                // Record state before instruction
                _stateAtOffset[instruction.Offset] = new StackState(currentDepth, stack.ToArray());

                // Compute effect on stack
                var (pop, push) = GetStackEffect(instruction);

                // Pop items
                for (int i = 0; i < pop && stack.Count > 0; i++)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                currentDepth = Math.Max(0, currentDepth - pop);

                // Push items
                for (int i = 0; i < push; i++)
                {
                    stack.Add(GetPushType(instruction, i));
                    currentDepth++;
                }
            }
        }

        /// <summary>
        /// Gets the number of items popped and pushed by an instruction.
        /// Simplified - a full implementation would handle all opcodes precisely.
        /// </summary>
        private (int pop, int push) GetStackEffect(Instruction instruction)
        {
            var opcode = instruction.OpCode;

            // Use built-in stack behavior when available
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
            // Handle variable pop (mainly calls)
            if (instruction.Operand is MethodReference method)
            {
                var count = method.Parameters.Count;
                if (method.HasThis && instruction.OpCode.Code != Code.Newobj)
                {
                    count++; // Include 'this'
                }
                return count;
            }
            return 0;
        }

        private int GetVarPush(Instruction instruction)
        {
            // Handle variable push (mainly calls with return value)
            if (instruction.Operand is MethodReference method)
            {
                if (method.ReturnType.FullName != "System.Void")
                {
                    return 1;
                }
            }
            return 0;
        }

        private TypeReference GetPushType(Instruction instruction, int index)
        {
            // Simplified type inference
            // A full implementation would track precise types

            if (instruction.Operand is MethodReference method && method.ReturnType.FullName != "System.Void")
            {
                return method.ReturnType;
            }

            if (instruction.Operand is FieldReference field)
            {
                return field.FieldType;
            }

            // Default to object for simplicity
            return _method.Module.TypeSystem.Object;
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
