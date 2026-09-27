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
    /// A candidate yield point that was not placed, and why.
    /// </summary>
    public sealed class SkippedYieldPoint
    {
        /// <summary>
        /// The instruction the yield point would have guarded.
        /// </summary>
        public Instruction Instruction { get; set; }

        /// <summary>
        /// The kind of yield point that was skipped.
        /// </summary>
        public ILYieldPointKind Kind { get; set; }

        /// <summary>
        /// Why the yield point was skipped.
        /// </summary>
        public string Reason { get; set; }

        public override string ToString()
        {
            return $"IL_{Instruction.Offset:X4} ({Kind}): {Reason}";
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
        /// A call to another transformed method. This is a resume point only: no yield
        /// check is injected, but if the callee suspends, the caller's frame records this
        /// call site so that on resume it re-executes the call and the callee restores
        /// itself.
        /// </summary>
        ContinuableCall,

        /// <summary>
        /// Method return point.
        /// </summary>
        Return
    }

    /// <summary>
    /// Options for yield point identification.
    /// </summary>
    public sealed class YieldPointOptions
    {
        /// <summary>
        /// Whether to add yield points at backward branches (loops).
        /// Default: true
        /// </summary>
        public bool IncludeBackwardBranches { get; set; } = true;

        /// <summary>
        /// Whether to add yield points at external method calls.
        /// Default: false
        /// </summary>
        public bool IncludeExternalCalls { get; set; } = false;

        /// <summary>
        /// List of assembly names to consider as "internal" (not external).
        /// Calls to methods in these assemblies won't be yield points.
        /// </summary>
        public HashSet<string> InternalAssemblies { get; set; } = new HashSet<string>();

        /// <summary>
        /// Identifies calls to other transformed methods. Each such call becomes a
        /// <see cref="ILYieldPointKind.ContinuableCall"/> resume point. Null means no call
        /// is treated as continuable.
        /// </summary>
        public Func<MethodReference, bool> IsContinuableCall { get; set; }

        /// <summary>
        /// Default options (backward branches only).
        /// </summary>
        public static YieldPointOptions Default => new YieldPointOptions();

        /// <summary>
        /// Options that include both backward branches and external calls.
        /// </summary>
        public static YieldPointOptions Full => new YieldPointOptions
        {
            IncludeBackwardBranches = true,
            IncludeExternalCalls = true
        };
    }

    /// <summary>
    /// Identifies yield points in IL code.
    /// </summary>
    public sealed class YieldPointIdentifier
    {
        private readonly MethodDefinition _method;
        private readonly ControlFlowGraph _cfg;
        private readonly StackSimulator _stackSim;
        private readonly YieldPointOptions _options;
        private readonly List<SkippedYieldPoint> _skipped = new List<SkippedYieldPoint>();

        public YieldPointIdentifier(MethodDefinition method)
            : this(method, YieldPointOptions.Default)
        {
        }

        public YieldPointIdentifier(MethodDefinition method, YieldPointOptions options)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
            _options = options ?? YieldPointOptions.Default;
            _cfg = ControlFlowGraph.Build(method);
            _stackSim = new StackSimulator(method);
            _stackSim.Simulate();
        }

        /// <summary>
        /// Finds all yield points in the method based on configured options.
        /// </summary>
        public List<ILYieldPoint> FindYieldPoints()
        {
            _skipped.Clear();
            var yieldPoints = new List<ILYieldPoint>();
            var nextId = 0;

            // Add yield points at loop back-edges (backward branches)
            if (_options.IncludeBackwardBranches)
            {
                foreach (var (from, to) in _cfg.BackEdges)
                {
                    // The yield point is at the back-edge source (the branch instruction)
                    var lastInstruction = from.Instructions[from.Instructions.Count - 1];

                    // Yield points inside try blocks are placed; those inside handlers,
                    // filters and lock bodies are skipped (whitepaper §10.2).
                    if (Skip(lastInstruction, ILYieldPointKind.BackwardBranch))
                        continue;

                    yieldPoints.Add(new ILYieldPoint
                    {
                        Id = nextId++,
                        Instruction = lastInstruction,
                        Kind = ILYieldPointKind.BackwardBranch,
                        StackState = _stackSim.GetStateAt(lastInstruction.Offset)
                    });
                }
            }

            // Add resume points at calls to other transformed methods, and (optionally)
            // yield points at external calls (Second Life-style behavior).
            if (_options.IsContinuableCall != null || _options.IncludeExternalCalls)
            {
                foreach (var instruction in _method.Body.Instructions)
                {
                    if (instruction.OpCode.Code != Code.Call &&
                        instruction.OpCode.Code != Code.Callvirt)
                        continue;

                    if (!(instruction.Operand is MethodReference called))
                        continue;

                    ILYieldPointKind kind;
                    if (_options.IsContinuableCall != null && _options.IsContinuableCall(called))
                        kind = ILYieldPointKind.ContinuableCall;
                    else if (_options.IncludeExternalCalls && IsExternalCall(called))
                        kind = ILYieldPointKind.ExternalCall;
                    else
                        continue;

                    // Yield points inside try blocks are placed; those inside handlers,
                    // filters and lock bodies are skipped (whitepaper §10.2).
                    if (Skip(instruction, kind))
                        continue;

                    yieldPoints.Add(new ILYieldPoint
                    {
                        Id = nextId++,
                        Instruction = instruction,
                        Kind = kind,
                        StackState = _stackSim.GetStateAt(instruction.Offset)
                    });
                }
            }

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

        /// <summary>
        /// Candidate yield points that the last <see cref="FindYieldPoints"/> call did not
        /// place, with the reason for each.
        /// </summary>
        public IReadOnlyList<SkippedYieldPoint> SkippedYieldPoints => _skipped;

        private bool Skip(Instruction instruction, ILYieldPointKind kind)
        {
            var reason = GetSkipReason(instruction.Offset);
            if (reason == null) return false;

            _skipped.Add(new SkippedYieldPoint { Instruction = instruction, Kind = kind, Reason = reason });
            return true;
        }

        /// <summary>
        /// Returns why a yield point at the given IL offset cannot be placed, or null if it
        /// can. A yield point may sit inside a <c>try</c> block: the transformer re-enters
        /// the block at its first instruction on resume and keeps the method's own handlers
        /// from observing the suspension. It may not sit inside a catch handler, a
        /// <c>finally</c>/<c>fault</c> block or a filter, because the runtime is the only
        /// way into those, so there is nowhere to resume (whitepaper §10.2). Nor may it sit
        /// inside a <c>lock</c> body: the monitor belongs to the suspending thread and
        /// cannot be carried across a suspension.
        /// </summary>
        private string GetSkipReason(int offset)
        {
            foreach (var handler in _method.Body.ExceptionHandlers)
            {
                // Handler body (catch/finally/fault): [HandlerStart, HandlerEnd); a null
                // HandlerEnd means the handler runs to the end of the method.
                if (handler.HandlerStart != null && offset >= handler.HandlerStart.Offset &&
                    (handler.HandlerEnd == null || offset < handler.HandlerEnd.Offset))
                {
                    switch (handler.HandlerType)
                    {
                        case ExceptionHandlerType.Finally: return "inside a finally block";
                        case ExceptionHandlerType.Fault: return "inside a fault block";
                        default: return "inside a catch handler";
                    }
                }

                // Filter region: [FilterStart, HandlerStart)
                if (handler.FilterStart != null && handler.HandlerStart != null &&
                    offset >= handler.FilterStart.Offset && offset < handler.HandlerStart.Offset)
                {
                    return "inside an exception filter";
                }

                // Try block of a lock statement: [TryStart, TryEnd) protected by a finally
                // that calls Monitor.Exit.
                if (handler.HandlerType == ExceptionHandlerType.Finally &&
                    handler.TryStart != null && handler.TryEnd != null &&
                    offset >= handler.TryStart.Offset && offset < handler.TryEnd.Offset &&
                    FinallyExitsMonitor(handler))
                {
                    return "inside a lock statement (a try/finally that calls Monitor.Exit)";
                }
            }

            return null;
        }

        private static bool FinallyExitsMonitor(ExceptionHandler handler)
        {
            for (var instruction = handler.HandlerStart;
                 instruction != null && instruction != handler.HandlerEnd;
                 instruction = instruction.Next)
            {
                if ((instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt) &&
                    instruction.Operand is MethodReference called &&
                    called.Name == "Exit" &&
                    called.DeclaringType.FullName == "System.Threading.Monitor")
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsExternalCall(MethodReference method)
        {
            // Consider a call external if it's not in the same assembly
            // and not in the list of internal assemblies
            var calledAssembly = method.DeclaringType.Scope.Name;
            var thisAssembly = _method.Module.Name;

            if (calledAssembly == thisAssembly)
                return false;

            if (_options.InternalAssemblies.Contains(calledAssembly))
                return false;

            return true;
        }
    }
}
