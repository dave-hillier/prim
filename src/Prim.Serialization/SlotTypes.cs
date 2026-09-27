using Prim.Core;

namespace Prim.Serialization
{
    /// <summary>
    /// The slot types every serializer accepts, whether or not it has a
    /// <c>Validator</c>: the built-in types of a default <see cref="ContinuationValidator"/>.
    /// </summary>
    internal static class SlotTypes
    {
        // Only IsTypeAllowed / IsValueAllowed are ever called on this instance, and
        // nothing registers types on it, so sharing it is safe.
        internal static readonly ContinuationValidator BuiltIns = new ContinuationValidator();
    }
}
