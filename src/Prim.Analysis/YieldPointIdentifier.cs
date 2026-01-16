using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Prim.Analysis
{
    /// <summary>
    /// Information about a yield point in IL code.
    /// </summary>
    public sealed class ILYieldPoint
    {
        /// <summary>
        /// Unique ID for this yield point within the method.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The instruction where this yield point should be inserted.
        /// </summary>
        public Instruction Instruction { get; set; }

        /// <summary>
        /// The kind of yield point.
        /// </summary>
        public ILYieldPointKind Kind { get; set; }

        /// <summary>
        /// The stack state at this yield point.
        /// </summary>
        public StackState StackState { get; set; }

        public override string ToString()
        {
            return $"YieldPoint[{Id}] at IL_{Instruction.Offset:X4} ({Kind})";
        }
    }

    /// <summary>
    /// Kind of IL yield point.
    /// </summary>
    public enum ILYieldPointKind
    {
        /// <summary>
        /// A backward branch (loop back-edge).
        /// </summary>
        BackwardBranch,

        /// <summary>
        /// A method call to an external API.
        /// </summary>
        ExternalCall,

        /// <summary>
        /// Method return point.
        /// </summary>
        Return
    }

    /// <summary>
    /// Identifies yield points in IL code.
    /// </summary>
    public sealed class YieldPointIdentifier
    {
        private readonly MethodDefinition _method;
        private readonly ControlFlowGraph _cfg;
        private readonly StackSimulator _stackSim;

        public YieldPointIdentifier(MethodDefinition method)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
            _cfg = ControlFlowGraph.Build(method);
            _stackSim = new StackSimulator(method);
            _stackSim.Simulate();
        }

        /// <summary>
        /// Finds all yield points in the method.
        /// </summary>
        public List<ILYieldPoint> FindYieldPoints()
        {
            var yieldPoints = new List<ILYieldPoint>();
            var nextId = 0;

            // Add yield points at loop back-edges (backward branches)
            foreach (var (from, to) in _cfg.BackEdges)
            {
                // The yield point is at the back-edge source (the branch instruction)
                var lastInstruction = from.Instructions[from.Instructions.Count - 1];
                yieldPoints.Add(new ILYieldPoint
                {
                    Id = nextId++,
                    Instruction = lastInstruction,
                    Kind = ILYieldPointKind.BackwardBranch,
                    StackState = _stackSim.GetStateAt(lastInstruction.Offset)
                });
            }

            // Optionally add yield points at external calls
            // (Commented out for simplicity - can be enabled for full Second Life-style behavior)
            /*
            foreach (var instruction in _method.Body.Instructions)
            {
                if (instruction.OpCode.Code == Code.Call ||
                    instruction.OpCode.Code == Code.Callvirt)
                {
                    if (instruction.Operand is MethodReference called &&
                        IsExternalCall(called))
                    {
                        yieldPoints.Add(new ILYieldPoint
                        {
                            Id = nextId++,
                            Instruction = instruction,
                            Kind = ILYieldPointKind.ExternalCall,
                            StackState = _stackSim.GetStateAt(instruction.Offset)
                        });
                    }
                }
            }
            */

            return yieldPoints;
        }

        /// <summary>
        /// Gets the control flow graph.
        /// </summary>
        public ControlFlowGraph GetControlFlowGraph() => _cfg;

        /// <summary>
        /// Gets the stack simulator.
        /// </summary>
        public StackSimulator GetStackSimulator() => _stackSim;

        private bool IsExternalCall(MethodReference method)
        {
            // Consider a call external if it's not in the same assembly
            var calledAssembly = method.DeclaringType.Scope.Name;
            var thisAssembly = _method.Module.Assembly.Name.Name;
            return calledAssembly != thisAssembly;
        }
    }
}
