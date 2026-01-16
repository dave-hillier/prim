using System.Collections.Generic;
using Mono.Cecil.Cil;

namespace Prim.Analysis
{
    /// <summary>
    /// Represents a basic block in the control flow graph.
    /// A basic block is a sequence of instructions with one entry point
    /// and one exit point.
    /// </summary>
    public sealed class BasicBlock
    {
        /// <summary>
        /// The byte offset of the first instruction in this block.
        /// </summary>
        public int StartOffset { get; }

        /// <summary>
        /// The byte offset of the last instruction in this block.
        /// </summary>
        public int EndOffset { get; private set; }

        /// <summary>
        /// The instructions in this block.
        /// </summary>
        public List<Instruction> Instructions { get; } = new List<Instruction>();

        /// <summary>
        /// Successor blocks (blocks this block can jump to).
        /// </summary>
        public List<BasicBlock> Successors { get; } = new List<BasicBlock>();

        /// <summary>
        /// Predecessor blocks (blocks that can jump to this block).
        /// </summary>
        public List<BasicBlock> Predecessors { get; } = new List<BasicBlock>();

        /// <summary>
        /// True if this block is a loop header (target of a back-edge).
        /// </summary>
        public bool IsLoopHeader { get; set; }

        /// <summary>
        /// True if this block starts an exception handler.
        /// </summary>
        public bool IsExceptionHandler { get; set; }

        public BasicBlock(int startOffset)
        {
            StartOffset = startOffset;
            EndOffset = startOffset;
        }

        /// <summary>
        /// Adds an instruction to this block.
        /// </summary>
        public void AddInstruction(Instruction instruction)
        {
            Instructions.Add(instruction);
            EndOffset = instruction.Offset;
        }

        public override string ToString()
        {
            return $"Block[{StartOffset:X4}-{EndOffset:X4}] ({Instructions.Count} instructions, {Successors.Count} successors)";
        }
    }
}
