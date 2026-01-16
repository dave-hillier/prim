using System;
using Prim.Core;

namespace Prim.Runtime
{
    /// <summary>
    /// Helper methods for capturing and restoring frame state.
    /// Used by generated catch and restore blocks.
    /// </summary>
    public static class FrameCapture
    {
        /// <summary>
        /// Creates a HostFrameRecord for the current frame.
        /// Called by generated catch blocks during suspension.
        /// </summary>
        /// <param name="methodToken">The method's unique token.</param>
        /// <param name="yieldPointId">The yield point where suspension occurred.</param>
        /// <param name="slots">The captured slot values.</param>
        /// <param name="caller">The caller's frame record (from the exception).</param>
        /// <returns>A new HostFrameRecord for this frame.</returns>
        public static HostFrameRecord CaptureFrame(
            int methodToken,
            int yieldPointId,
            object[] slots,
            HostFrameRecord caller)
        {
            return new HostFrameRecord(methodToken, yieldPointId, slots, caller);
        }

        /// <summary>
        /// Packs values into a slot array.
        /// Called by generated code to create the slots array.
        /// </summary>
        public static object[] PackSlots(params object[] values)
        {
            return values ?? Array.Empty<object>();
        }

        /// <summary>
        /// Gets a typed value from the slots array.
        /// Called by generated restore blocks.
        /// </summary>
        /// <typeparam name="T">The expected type.</typeparam>
        /// <param name="slots">The slots array.</param>
        /// <param name="index">The slot index.</param>
        /// <returns>The value cast to the expected type.</returns>
        public static T GetSlot<T>(object[] slots, int index)
        {
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            if (index < 0 || index >= slots.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"Slot index {index} is out of range. Slots count: {slots.Length}");
            }

            var value = slots[index];
            if (value == null)
            {
                // For value types, return default
                if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null)
                {
                    return default;
                }
                return default;
            }

            return (T)value;
        }

        /// <summary>
        /// Unboxes a value type from the slots array.
        /// For reference types, just returns the value.
        /// </summary>
        public static T UnboxSlot<T>(object[] slots, int index)
        {
            return GetSlot<T>(slots, index);
        }

        /// <summary>
        /// Generates a stable method token from a method signature.
        /// Used during code generation to create consistent tokens.
        /// Uses FNV-1a hash for deterministic results across processes.
        /// </summary>
        /// <param name="typeName">The full type name.</param>
        /// <param name="methodName">The method name.</param>
        /// <param name="parameterTypes">Parameter type names.</param>
        /// <returns>A stable hash code for the method.</returns>
        public static int GenerateMethodToken(string typeName, string methodName, params string[] parameterTypes)
        {
            return StableHash.GenerateMethodToken(typeName, methodName, parameterTypes);
        }
    }
}
