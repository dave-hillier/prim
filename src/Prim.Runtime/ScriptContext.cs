using System;
using System.Threading;
using Prim.Core;

namespace Prim.Runtime
{
    /// <summary>
    /// Thread-local context for script execution.
    /// Manages the yield flag, frame chain, and restore state.
    /// </summary>
    public sealed class ScriptContext
    {
        /// <summary>
        /// Thread-local current context.
        /// </summary>
        [ThreadStatic]
        private static ScriptContext _current;

        /// <summary>
        /// Gets or sets the current context for this thread.
        /// </summary>
        public static ScriptContext Current
        {
            get => _current;
            set => _current = value;
        }

        /// <summary>
        /// Volatile flag checked at yield points.
        /// Set to 1 by the host to request suspension.
        /// </summary>
        public volatile int YieldRequested;

        /// <summary>
        /// Instruction budget for preemptive scheduling.
        /// Decremented at yield points; when zero or negative, forces a yield.
        /// This provides bounded execution without requiring timer interrupts.
        /// </summary>
        public int InstructionBudget;

        /// <summary>
        /// Default budget assigned when none is specified.
        /// </summary>
        public const int DefaultBudget = 1000;

        /// <summary>
        /// True when restoring from a saved state.
        /// Generated restore blocks check this at method entry.
        /// </summary>
        public bool IsRestoring;

        /// <summary>
        /// The chain of frames being restored.
        /// During restore, each method pops its frame from this chain.
        /// </summary>
        public HostFrameRecord FrameChain;

        /// <summary>
        /// Value passed to the resumed continuation.
        /// The innermost restore block retrieves this.
        /// </summary>
        public object ResumeValue;

        /// <summary>
        /// Creates a new ScriptContext with default settings.
        /// </summary>
        public ScriptContext()
        {
            InstructionBudget = DefaultBudget;
        }

        /// <summary>
        /// Creates a ScriptContext configured for restoration.
        /// </summary>
        public ScriptContext(ContinuationState state, object resumeValue)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            InstructionBudget = DefaultBudget;
            IsRestoring = true;
            FrameChain = state.StackHead;
            ResumeValue = resumeValue;
        }

        /// <summary>
        /// Called by the host to request that the script yield.
        /// The next yield point check will trigger suspension.
        /// </summary>
        public void RequestYield()
        {
            Interlocked.Exchange(ref YieldRequested, 1);
        }

        /// <summary>
        /// Clears the yield request flag.
        /// </summary>
        public void ClearYieldRequest()
        {
            Interlocked.Exchange(ref YieldRequested, 0);
        }

        /// <summary>
        /// Called by generated code at yield points.
        /// If a yield was requested, throws SuspendException to trigger unwinding.
        /// </summary>
        /// <param name="yieldPointId">The unique ID of this yield point.</param>
        public void HandleYieldPoint(int yieldPointId)
        {
            if (YieldRequested != 0)
            {
                Interlocked.Exchange(ref YieldRequested, 0);
                throw new SuspendException(yieldPointId);
            }
        }

        /// <summary>
        /// Called by generated code at yield points with a value to yield.
        /// If a yield was requested, throws SuspendException with the value.
        /// </summary>
        /// <param name="yieldPointId">The unique ID of this yield point.</param>
        /// <param name="value">The value to yield.</param>
        public void HandleYieldPoint(int yieldPointId, object value)
        {
            if (YieldRequested != 0)
            {
                Interlocked.Exchange(ref YieldRequested, 0);
                throw new SuspendException(yieldPointId, value);
            }
        }

        /// <summary>
        /// Called by generated code at yield points with instruction counting.
        /// Decrements the budget by the specified cost; if budget exhausted or
        /// yield requested, throws SuspendException.
        /// </summary>
        /// <param name="yieldPointId">The unique ID of this yield point.</param>
        /// <param name="cost">The instruction cost since last yield point.</param>
        public void HandleYieldPointWithBudget(int yieldPointId, int cost)
        {
            InstructionBudget -= cost;
            if (YieldRequested != 0 || InstructionBudget <= 0)
            {
                Interlocked.Exchange(ref YieldRequested, 0);
                throw new SuspendException(yieldPointId);
            }
        }

        /// <summary>
        /// Called by generated code at yield points with instruction counting and a value.
        /// Decrements the budget; if exhausted or yield requested, throws SuspendException.
        /// </summary>
        /// <param name="yieldPointId">The unique ID of this yield point.</param>
        /// <param name="cost">The instruction cost since last yield point.</param>
        /// <param name="value">The value to yield.</param>
        public void HandleYieldPointWithBudget(int yieldPointId, int cost, object value)
        {
            InstructionBudget -= cost;
            if (YieldRequested != 0 || InstructionBudget <= 0)
            {
                Interlocked.Exchange(ref YieldRequested, 0);
                throw new SuspendException(yieldPointId, value);
            }
        }

        /// <summary>
        /// Resets the instruction budget to the specified value.
        /// Called by the scheduler before each time slice.
        /// </summary>
        /// <param name="budget">The new budget value.</param>
        public void ResetBudget(int budget = DefaultBudget)
        {
            InstructionBudget = budget;
        }

        /// <summary>
        /// Ensures a ScriptContext exists for the current thread.
        /// Creates one if necessary.
        /// </summary>
        public static ScriptContext EnsureCurrent()
        {
            if (_current == null)
            {
                _current = new ScriptContext();
            }
            return _current;
        }

        /// <summary>
        /// Runs an action with this context as the current context.
        /// Restores the previous context when done.
        /// </summary>
        public T RunWith<T>(Func<T> action)
        {
            var previous = _current;
            try
            {
                _current = this;
                return action();
            }
            finally
            {
                _current = previous;
            }
        }

        /// <summary>
        /// Runs an action with this context as the current context.
        /// Restores the previous context when done.
        /// </summary>
        public void RunWith(Action action)
        {
            var previous = _current;
            try
            {
                _current = this;
                action();
            }
            finally
            {
                _current = previous;
            }
        }
    }
}
