namespace Prim.Core
{
    /// <summary>
    /// Complete captured execution state of a suspended continuation.
    /// Can be serialized for persistence or migration.
    /// </summary>
    public sealed class ContinuationState
    {
        /// <summary>
        /// Current version of the state format.
        /// Used for compatibility checking during deserialization.
        /// </summary>
        public const int CurrentVersion = 1;

        /// <summary>
        /// The head of the linked list of captured frames.
        /// This is the innermost (most recently called) frame.
        /// </summary>
        public HostFrameRecord StackHead { get; set; }

        /// <summary>
        /// Version of the serialization format.
        /// </summary>
        public int Version { get; set; }

        /// <summary>
        /// The value that was yielded when the continuation suspended.
        /// </summary>
        public object YieldedValue { get; set; }

        public ContinuationState()
        {
            Version = CurrentVersion;
        }

        public ContinuationState(HostFrameRecord stackHead, object yieldedValue = null)
        {
            StackHead = stackHead;
            YieldedValue = yieldedValue;
            Version = CurrentVersion;
        }

        /// <summary>
        /// Returns the depth of the captured call stack.
        /// </summary>
        public int GetStackDepth()
        {
            return StackHead?.GetStackDepth() ?? 0;
        }

        public override string ToString()
        {
            return $"ContinuationState(v{Version}, Depth={GetStackDepth()})";
        }
    }
}
