using System;

namespace Prim.Core
{
    /// <summary>
    /// Captured state of a single stack frame.
    /// Forms a linked list representing the captured call stack, ordered
    /// outermost-first: the head (<see cref="ContinuationState.StackHead"/>) is the
    /// entry-point frame and each <see cref="Caller"/> link points one frame inward.
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
        /// Link to the next frame INWARD, i.e. the frame record of the method this
        /// frame was calling when execution suspended (its callee). Null for the
        /// innermost frame, where the suspension actually occurred.
        /// </summary>
        /// <remarks>
        /// The name is historical: capture builds the chain by prepending each frame
        /// as <c>SuspendException</c> unwinds (<c>record.Caller = ex.FrameChain</c>),
        /// so the link holds the chain captured so far, which consists of callees.
        /// It is kept as <c>Caller</c> because it is the JSON property name and the
        /// MessagePack DTO member, and because Roslyn-generated and Cecil-woven code
        /// binds to it by name (<c>__context.FrameChain = __frame.Caller</c>).
        /// </remarks>
        public HostFrameRecord Caller { get; set; }

        public HostFrameRecord()
        {
        }

        /// <param name="methodToken">Token identifying the method.</param>
        /// <param name="yieldPointId">Yield point where execution was suspended.</param>
        /// <param name="slots">Captured slot values.</param>
        /// <param name="caller">The next frame inward (callee); see <see cref="Caller"/>.</param>
        public HostFrameRecord(int methodToken, int yieldPointId, object[] slots, HostFrameRecord caller = null)
        {
            MethodToken = methodToken;
            YieldPointId = yieldPointId;
            Slots = slots;
            Caller = caller;
        }

        /// <summary>
        /// Returns the number of frames from this frame to the innermost frame, inclusive.
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
