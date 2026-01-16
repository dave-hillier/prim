using System;

namespace Prim.Core
{
    /// <summary>
    /// Exception thrown to trigger stack unwinding during suspension.
    /// As the stack unwinds, each transformed method's catch block
    /// captures its locals into a HostFrameRecord and rethrows.
    /// </summary>
    public sealed class SuspendException : Exception
    {
        /// <summary>
        /// The ID of the yield point where suspension was triggered.
        /// </summary>
        public int YieldPointId { get; }

        /// <summary>
        /// The chain of captured frames, built during stack unwinding.
        /// Initially null; each catch block prepends its frame.
        /// </summary>
        public HostFrameRecord FrameChain { get; set; }

        /// <summary>
        /// The value being yielded (if any).
        /// </summary>
        public object YieldedValue { get; set; }

        public SuspendException(int yieldPointId)
            : base("Continuation suspended")
        {
            YieldPointId = yieldPointId;
        }

        public SuspendException(int yieldPointId, object yieldedValue)
            : base("Continuation suspended")
        {
            YieldPointId = yieldPointId;
            YieldedValue = yieldedValue;
        }

        /// <summary>
        /// Builds the final ContinuationState from the captured frame chain.
        /// Called by the outermost handler after unwinding completes.
        /// </summary>
        public ContinuationState BuildContinuationState()
        {
            return new ContinuationState(FrameChain, YieldedValue);
        }

        public override string ToString()
        {
            var depth = FrameChain?.GetStackDepth() ?? 0;
            return $"SuspendException(YieldPoint={YieldPointId}, FrameDepth={depth})";
        }
    }
}
