using System;

namespace Prim.Core
{
    /// <summary>
    /// Captured state of a single stack frame.
    /// Forms a linked list representing the captured call stack.
    /// Inspired by Espresso's HostFrameRecord design.
    /// </summary>
    public sealed class HostFrameRecord
    {
        /// <summary>
        /// Token identifying the method (hash of signature).
        /// </summary>
        public int MethodToken { get; set; }

        /// <summary>
        /// ID of the yield point where execution was suspended.
        /// Used to jump to the correct location on resume.
        /// </summary>
        public int YieldPointId { get; set; }

        /// <summary>
        /// Captured values of locals, arguments, and evaluation stack items.
        /// Order matches the FrameDescriptor's Slots array.
        /// </summary>
        public object[] Slots { get; set; }

        /// <summary>
        /// Link to the caller's frame record, forming a linked list.
        /// Null for the outermost frame.
        /// </summary>
        public HostFrameRecord Caller { get; set; }

        public HostFrameRecord()
        {
        }

        public HostFrameRecord(int methodToken, int yieldPointId, object[] slots, HostFrameRecord caller = null)
        {
            MethodToken = methodToken;
            YieldPointId = yieldPointId;
            Slots = slots;
            Caller = caller;
        }

        /// <summary>
        /// Returns the depth of the call stack from this frame.
        /// </summary>
        public int GetStackDepth()
        {
            var depth = 1;
            var slow = this;
            var fast = this;
            var current = Caller;
            while (current != null)
            {
                depth++;
                current = current.Caller;

                slow = slow.Caller;
                fast = fast?.Caller?.Caller;
                if (fast != null && fast == slow)
                {
                    break; // Circular chain detected - return depth so far
                }
            }
            return depth;
        }

        public override string ToString()
        {
            var slotCount = Slots?.Length ?? 0;
            return $"HostFrameRecord(Method={MethodToken}, YieldPoint={YieldPointId}, Slots={slotCount})";
        }
    }
}
