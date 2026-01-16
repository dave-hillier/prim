using System;

namespace Prim.Core
{
    /// <summary>
    /// Describes a single slot in a method's frame.
    /// A slot can be a local variable, argument, or evaluation stack item.
    /// </summary>
    public sealed class FrameSlot
    {
        /// <summary>
        /// The index of this slot within its category.
        /// </summary>
        public int Index { get; }

        /// <summary>
        /// The name of the slot (from debug info), or null if unavailable.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The kind of slot (Local, Argument, or EvalStack).
        /// </summary>
        public SlotKind Kind { get; }

        /// <summary>
        /// The CLR type of the slot.
        /// </summary>
        public Type Type { get; }

        /// <summary>
        /// Whether this slot needs to be serialized.
        /// False for constants or dead variables.
        /// </summary>
        public bool RequiresSerialization { get; }

        public FrameSlot(int index, string name, SlotKind kind, Type type, bool requiresSerialization = true)
        {
            Index = index;
            Name = name;
            Kind = kind;
            Type = type ?? throw new ArgumentNullException(nameof(type));
            RequiresSerialization = requiresSerialization;
        }

        public override string ToString()
        {
            var nameStr = Name != null ? $" '{Name}'" : "";
            return $"{Kind}[{Index}]{nameStr}: {Type.Name}";
        }
    }
}
