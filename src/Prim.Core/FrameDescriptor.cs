using System;
using System.Collections;

namespace Prim.Core
{
    /// <summary>
    /// Pre-computed description of a method's frame layout.
    /// Generated at transform time, used for efficient serialization
    /// without runtime reflection.
    /// </summary>
    public sealed class FrameDescriptor
    {
        /// <summary>
        /// Unique identifier for the method.
        /// Computed as a hash of namespace, type, method name, and parameter types.
        /// </summary>
        public int MethodToken { get; }

        /// <summary>
        /// The full name of the method for debugging purposes.
        /// </summary>
        public string MethodName { get; }

        /// <summary>
        /// All slots in this frame (locals, arguments, eval stack slots).
        /// </summary>
        public FrameSlot[] Slots { get; }

        /// <summary>
        /// IDs of all yield points in this method.
        /// </summary>
        public int[] YieldPointIds { get; }

        /// <summary>
        /// For each yield point, which slots are live (need to be saved).
        /// Index corresponds to YieldPointIds index.
        /// </summary>
        public BitArray[] LiveSlotsAtYieldPoint { get; }

        public FrameDescriptor(
            int methodToken,
            string methodName,
            FrameSlot[] slots,
            int[] yieldPointIds,
            BitArray[] liveSlotsAtYieldPoint)
        {
            MethodToken = methodToken;
            MethodName = methodName ?? throw new ArgumentNullException(nameof(methodName));
            Slots = (FrameSlot[])(slots ?? throw new ArgumentNullException(nameof(slots))).Clone();
            YieldPointIds = (int[])(yieldPointIds ?? throw new ArgumentNullException(nameof(yieldPointIds))).Clone();
            LiveSlotsAtYieldPoint = (BitArray[])(liveSlotsAtYieldPoint ?? throw new ArgumentNullException(nameof(liveSlotsAtYieldPoint))).Clone();

            if (yieldPointIds.Length != liveSlotsAtYieldPoint.Length)
            {
                throw new ArgumentException(
                    "YieldPointIds and LiveSlotsAtYieldPoint must have the same length");
            }
        }

        /// <summary>
        /// Gets the live slots for a specific yield point ID.
        /// </summary>
        public BitArray GetLiveSlotsForYieldPoint(int yieldPointId)
        {
            var index = Array.IndexOf(YieldPointIds, yieldPointId);
            if (index < 0)
            {
                throw new ArgumentException($"Unknown yield point ID: {yieldPointId}", nameof(yieldPointId));
            }
            return LiveSlotsAtYieldPoint[index];
        }

        /// <summary>
        /// Returns the number of live slots for a given yield point.
        /// </summary>
        public int CountLiveSlots(int yieldPointId)
        {
            var liveSlots = GetLiveSlotsForYieldPoint(yieldPointId);
            var count = 0;
            for (int i = 0; i < liveSlots.Length; i++)
            {
                if (liveSlots[i]) count++;
            }
            return count;
        }

        public override string ToString()
        {
            return $"FrameDescriptor({MethodName}, {Slots.Length} slots, {YieldPointIds.Length} yield points)";
        }
    }
}
