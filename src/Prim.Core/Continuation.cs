using System;

namespace Prim.Core
{
    /// <summary>
    /// A suspended computation that can be resumed.
    /// Inspired by WasmFX's continuation reference type.
    /// </summary>
    /// <typeparam name="T">The type of the final result when the computation completes.</typeparam>
    public sealed class Continuation<T>
    {
        /// <summary>
        /// The captured execution state.
        /// </summary>
        public ContinuationState State { get; }

        /// <summary>
        /// The serializer to use for this continuation (optional).
        /// If not set, serialization operations will throw.
        /// </summary>
        public IContinuationSerializer Serializer { get; set; }

        /// <summary>
        /// Creates a continuation from captured state.
        /// </summary>
        public Continuation(ContinuationState state)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
        }

        /// <summary>
        /// Creates a continuation from captured state with a serializer.
        /// </summary>
        public Continuation(ContinuationState state, IContinuationSerializer serializer)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            Serializer = serializer;
        }

        /// <summary>
        /// Serialize the continuation to bytes for storage or migration.
        /// </summary>
        /// <exception cref="InvalidOperationException">If no serializer is configured.</exception>
        public byte[] Serialize()
        {
            if (Serializer == null)
            {
                throw new InvalidOperationException(
                    "No serializer configured. Set the Serializer property or use Serialize(IContinuationSerializer).");
            }
            return Serializer.Serialize(State);
        }

        /// <summary>
        /// Serialize the continuation using the specified serializer.
        /// </summary>
        public byte[] Serialize(IContinuationSerializer serializer)
        {
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));
            return serializer.Serialize(State);
        }

        /// <summary>
        /// Deserialize a continuation from bytes.
        /// </summary>
        public static Continuation<T> Deserialize(byte[] data, IContinuationSerializer serializer)
        {
            return Deserialize(data, serializer, validator: null);
        }

        /// <summary>
        /// Deserialize a continuation from bytes, optionally validating the
        /// deserialized state before returning.
        ///
        /// SECURITY: when <paramref name="data"/> is untrusted, pass a configured
        /// <paramref name="validator"/> (or validate at resume time via
        /// ContinuationRunner.Validator). Deserializing and resuming attacker-
        /// supplied state without validation is a full takeover primitive.
        /// </summary>
        /// <exception cref="ValidationException">
        /// If <paramref name="validator"/> is supplied and validation fails.
        /// </exception>
        public static Continuation<T> Deserialize(
            byte[] data,
            IContinuationSerializer serializer,
            ContinuationValidator validator)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));

            var state = serializer.Deserialize(data);
            validator?.Validate(state);
            return new Continuation<T>(state, serializer);
        }

        public override string ToString()
        {
            return $"Continuation<{typeof(T).Name}>(Depth={State.GetStackDepth()})";
        }
    }

    /// <summary>
    /// Interface for serializing/deserializing continuation state.
    /// </summary>
    public interface IContinuationSerializer
    {
        /// <summary>
        /// Serialize the continuation state to bytes.
        /// </summary>
        byte[] Serialize(ContinuationState state);

        /// <summary>
        /// Deserialize continuation state from bytes.
        /// </summary>
        ContinuationState Deserialize(byte[] data);
    }
}
