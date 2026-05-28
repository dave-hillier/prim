using System;
using Prim.Core;

namespace Prim.Serialization
{
    /// <summary>
    /// Bounds the depth of the frame Caller chain during (de)serialization so a
    /// maliciously deep chain throws a clean exception instead of overflowing the
    /// stack before <see cref="ContinuationValidator"/> (which traverses
    /// iteratively) ever gets a chance to reject it (issue #42).
    ///
    /// The limit mirrors <see cref="ValidationOptions.MaxStackDepth"/> so the
    /// serializer never accepts a chain the validator would reject anyway.
    /// </summary>
    internal static class FrameDepthGuard
    {
        /// <summary>
        /// Maximum allowed frame Caller-chain depth, matching the validator's
        /// default maximum stack depth.
        /// </summary>
        public static readonly int MaxFrameDepth = ValidationOptions.Default.MaxStackDepth;

        /// <summary>
        /// Per-frame nesting overhead multiplier used to derive a parser-level
        /// depth bound from <see cref="MaxFrameDepth"/>.
        ///
        /// A single logical frame adds a small, bounded number of document nesting
        /// levels: the frame DTO nests its <c>Caller</c> under a property (the
        /// PreserveReferencesHandling <c>$id</c> and TypeNameHandling <c>$type</c>
        /// wrappers are sibling properties, not extra nesting, so a measured real
        /// 1000-frame chain nests to JSON depth ~1003). The MessagePack object
        /// graph adds the envelope/Value/slot-array levels similarly.
        ///
        /// A multiplier of 2 (parser bound = 2000 for the default 1000-frame limit)
        /// is comfortably above the measured legitimate depth so a valid chain up
        /// to <see cref="MaxFrameDepth"/> frames is always accepted, yet low enough
        /// that the parser rejects a pathological chain with a catchable exception
        /// BEFORE the serializer's recursive object construction can overflow the
        /// native stack. (Empirically, Newtonsoft's typed deserializer stack-
        /// overflows around a nesting depth of ~3500 on a default 1MB thread stack,
        /// so the bound must stay well under that — a larger multiplier such as 8
        /// would itself permit a crash.)
        /// </summary>
        public const int NestingOverheadPerFrame = 2;

        /// <summary>
        /// Parser-level maximum nesting depth. Configured on the JSON reader
        /// (<c>JsonSerializerSettings.MaxDepth</c>) and the MessagePack security
        /// options (<c>MaximumObjectGraphDepth</c>) so the parser itself rejects a
        /// deep payload <em>before</em> it recursively materializes the entire
        /// Caller chain and overflows the stack (issue #42). This is the real fix
        /// on the deserialize side; the post-parse <see cref="Check"/> loop remains
        /// as defense-in-depth.
        /// </summary>
        public static readonly int MaxParserDepth = MaxFrameDepth * NestingOverheadPerFrame;

        /// <summary>
        /// Throws when the running depth exceeds <see cref="MaxFrameDepth"/>.
        /// </summary>
        public static void Check(int depth)
        {
            if (depth > MaxFrameDepth)
            {
                throw new InvalidOperationException(
                    $"Continuation frame Caller chain exceeds the maximum allowed depth of {MaxFrameDepth}. " +
                    "The state may be corrupt or maliciously crafted.");
            }
        }
    }
}
