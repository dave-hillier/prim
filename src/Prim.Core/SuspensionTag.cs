namespace Prim.Core
{
    /// <summary>
    /// A suspension tag defines the protocol for a suspension point.
    /// Inspired by WasmFX's typed control tags.
    ///
    /// TOut: The type of value passed out when suspending.
    /// TIn: The type of value expected back when resuming.
    /// </summary>
    /// <typeparam name="TOut">Type yielded on suspend</typeparam>
    /// <typeparam name="TIn">Type received on resume</typeparam>
    public sealed class SuspensionTag<TOut, TIn>
    {
        /// <summary>
        /// Human-readable name for this suspension tag.
        /// </summary>
        public string Name { get; }

        public SuspensionTag(string name)
        {
            Name = name ?? "anonymous";
        }

        public override string ToString() => $"SuspensionTag<{typeof(TOut).Name}, {typeof(TIn).Name}>({Name})";

        public override bool Equals(object obj)
        {
            if (obj is SuspensionTag<TOut, TIn> other)
            {
                return Name == other.Name;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return (Name?.GetHashCode() ?? 0) ^
                   typeof(TOut).GetHashCode() ^
                   typeof(TIn).GetHashCode();
        }
    }

    /// <summary>
    /// Unit type for suspension tags that don't need a resume value.
    /// </summary>
    public struct Unit
    {
        /// <summary>
        /// The singleton Unit value.
        /// </summary>
        public static readonly Unit Value = default;

        public override string ToString() => "()";
    }

    /// <summary>
    /// Common suspension tags for typical patterns.
    /// </summary>
    public static class SuspensionTags
    {
        /// <summary>
        /// Tag for generator pattern: yields a value, receives nothing back.
        /// </summary>
        public static SuspensionTag<T, Unit> Generator<T>() => new SuspensionTag<T, Unit>("generator");

        /// <summary>
        /// Tag for cooperative yield: yields nothing, receives nothing.
        /// </summary>
        public static readonly SuspensionTag<Unit, Unit> Yield = new SuspensionTag<Unit, Unit>("yield");

        /// <summary>
        /// Tag for async read: yields an address, receives data.
        /// </summary>
        public static SuspensionTag<TRequest, TResponse> Async<TRequest, TResponse>()
            => new SuspensionTag<TRequest, TResponse>("async");
    }
}
