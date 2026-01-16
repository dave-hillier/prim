using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Prim.Analysis
{
    /// <summary>
    /// Builds and represents a control flow graph for a method.
    /// </summary>
    public sealed class ControlFlowGraph
    {
        /// <summary>
        /// All basic blocks in the method.
        /// </summary>
        public List<BasicBlock> Blocks { get; } = new List<BasicBlock>();

        /// <summary>
        /// The entry block of the method.
        /// </summary>
        public BasicBlock EntryBlock { get; private set; }

        /// <summary>
        /// Maps instruction offsets to their containing blocks.
        /// </summary>
        public Dictionary<int, BasicBlock> OffsetToBlock { get; } = new Dictionary<int, BasicBlock>();

        /// <summary>
        /// Back-edges in the graph (indicates loops).
        /// </summary>
        public List<(BasicBlock From, BasicBlock To)> BackEdges { get; } = new List<(BasicBlock, BasicBlock)>();

        /// <summary>
        /// Builds a control flow graph for the given method.
        /// </summary>
        public static ControlFlowGraph Build(MethodDefinition method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            if (!method.HasBody) throw new ArgumentException("Method has no body", nameof(method));

            var cfg = new ControlFlowGraph();
            cfg.BuildGraph(method.Body);
            return cfg;
        }

        private void BuildGraph(MethodBody body)
        {
            var instructions = body.Instructions;
            if (instructions.Count == 0) return;

            // Step 1: Find block leaders (first instructions of blocks)
            var leaders = FindLeaders(body);

            // Step 2: Create basic blocks
            CreateBlocks(instructions, leaders);

            // Step 3: Connect blocks (add edges)
            ConnectBlocks(body);

            // Step 4: Identify back-edges (loops)
            IdentifyBackEdges();
        }

        private HashSet<int> FindLeaders(MethodBody body)
        {
            var leaders = new HashSet<int>();
            var instructions = body.Instructions;

            // First instruction is always a leader
            if (instructions.Count > 0)
            {
                leaders.Add(instructions[0].Offset);
            }

            foreach (var instruction in instructions)
            {
                // Target of a branch is a leader
                if (instruction.Operand is Instruction target)
                {
                    leaders.Add(target.Offset);
                }
                else if (instruction.Operand is Instruction[] targets)
                {
                    foreach (var t in targets)
                    {
                        leaders.Add(t.Offset);
                    }
                }

                // Instruction after a branch or return is a leader
                if (IsBranch(instruction) || IsReturn(instruction))
                {
                    var next = instruction.Next;
                    if (next != null)
                    {
                        leaders.Add(next.Offset);
                    }
                }
            }

            // Exception handler starts are leaders
            foreach (var handler in body.ExceptionHandlers)
            {
                leaders.Add(handler.TryStart.Offset);
                leaders.Add(handler.HandlerStart.Offset);
                if (handler.FilterStart != null)
                {
                    leaders.Add(handler.FilterStart.Offset);
                }
            }

            return leaders;
        }

        private void CreateBlocks(Mono.Collections.Generic.Collection<Instruction> instructions, HashSet<int> leaders)
        {
            BasicBlock currentBlock = null;

            foreach (var instruction in instructions)
            {
                if (leaders.Contains(instruction.Offset))
                {
                    // Start a new block
                    currentBlock = new BasicBlock(instruction.Offset);
                    Blocks.Add(currentBlock);
                    OffsetToBlock[instruction.Offset] = currentBlock;
                }

                currentBlock?.AddInstruction(instruction);
                if (!OffsetToBlock.ContainsKey(instruction.Offset))
                {
                    OffsetToBlock[instruction.Offset] = currentBlock;
                }
            }

            if (Blocks.Count > 0)
            {
                EntryBlock = Blocks[0];
            }
        }

        private void ConnectBlocks(MethodBody body)
        {
            foreach (var block in Blocks)
            {
                if (block.Instructions.Count == 0) continue;

                var lastInstruction = block.Instructions[block.Instructions.Count - 1];

                // Handle branch targets
                if (lastInstruction.Operand is Instruction target)
                {
                    if (OffsetToBlock.TryGetValue(target.Offset, out var targetBlock))
                    {
                        AddEdge(block, targetBlock);
                    }
                }
                else if (lastInstruction.Operand is Instruction[] targets)
                {
                    foreach (var t in targets)
                    {
                        if (OffsetToBlock.TryGetValue(t.Offset, out var targetBlock))
                        {
                            AddEdge(block, targetBlock);
                        }
                    }
                }

                // Fall-through to next block (if not unconditional branch or return)
                if (!IsUnconditionalBranch(lastInstruction) && !IsReturn(lastInstruction))
                {
                    var next = lastInstruction.Next;
                    if (next != null && OffsetToBlock.TryGetValue(next.Offset, out var nextBlock))
                    {
                        AddEdge(block, nextBlock);
                    }
                }
            }

            // Connect exception handlers
            foreach (var handler in body.ExceptionHandlers)
            {
                if (OffsetToBlock.TryGetValue(handler.TryStart.Offset, out var tryBlock) &&
                    OffsetToBlock.TryGetValue(handler.HandlerStart.Offset, out var handlerBlock))
                {
                    handlerBlock.IsExceptionHandler = true;
                    // Don't add edge here - exception flow is implicit
                }
            }
        }

        private void AddEdge(BasicBlock from, BasicBlock to)
        {
            if (!from.Successors.Contains(to))
            {
                from.Successors.Add(to);
            }
            if (!to.Predecessors.Contains(from))
            {
                to.Predecessors.Add(from);
            }
        }

        private void IdentifyBackEdges()
        {
            // Use DFS to find back-edges
            var visited = new HashSet<BasicBlock>();
            var inStack = new HashSet<BasicBlock>();

            void Dfs(BasicBlock block)
            {
                visited.Add(block);
                inStack.Add(block);

                foreach (var successor in block.Successors)
                {
                    if (inStack.Contains(successor))
                    {
                        // Back-edge found
                        BackEdges.Add((block, successor));
                        successor.IsLoopHeader = true;
                    }
                    else if (!visited.Contains(successor))
                    {
                        Dfs(successor);
                    }
                }

                inStack.Remove(block);
            }

            if (EntryBlock != null)
            {
                Dfs(EntryBlock);
            }
        }

        private static bool IsBranch(Instruction instruction)
        {
            var code = instruction.OpCode.Code;
            return code == Code.Br || code == Code.Br_S ||
                   code == Code.Brfalse || code == Code.Brfalse_S ||
                   code == Code.Brtrue || code == Code.Brtrue_S ||
                   code == Code.Beq || code == Code.Beq_S ||
                   code == Code.Bne_Un || code == Code.Bne_Un_S ||
                   code == Code.Blt || code == Code.Blt_S ||
                   code == Code.Blt_Un || code == Code.Blt_Un_S ||
                   code == Code.Ble || code == Code.Ble_S ||
                   code == Code.Ble_Un || code == Code.Ble_Un_S ||
                   code == Code.Bgt || code == Code.Bgt_S ||
                   code == Code.Bgt_Un || code == Code.Bgt_Un_S ||
                   code == Code.Bge || code == Code.Bge_S ||
                   code == Code.Bge_Un || code == Code.Bge_Un_S ||
                   code == Code.Switch ||
                   code == Code.Leave || code == Code.Leave_S;
        }

        private static bool IsUnconditionalBranch(Instruction instruction)
        {
            var code = instruction.OpCode.Code;
            return code == Code.Br || code == Code.Br_S ||
                   code == Code.Leave || code == Code.Leave_S ||
                   code == Code.Throw || code == Code.Rethrow;
        }

        private static bool IsReturn(Instruction instruction)
        {
            return instruction.OpCode.Code == Code.Ret;
        }
    }
}
