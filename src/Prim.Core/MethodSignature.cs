using System;
using System.Linq;

namespace Prim.Core
{
    /// <summary>
    /// The full identity of a continuable method: its declaring type, method name,
    /// and ordered parameter type names.
    ///
    /// The 32-bit <see cref="StableHash.GenerateMethodToken"/> derived from these
    /// components is only a 32-bit FNV-1a value, so two genuinely different methods
    /// can collide. The signature is the strong trust anchor used at descriptor
    /// registration time to turn such a collision into a loud error rather than a
    /// silent resume into the wrong method (issue #44).
    /// </summary>
    public sealed class MethodSignature : IEquatable<MethodSignature>
    {
        /// <summary>
        /// Declaring type name. Prefer the AssemblyQualifiedName for the strongest
        /// identity, but FullName is accepted for callers that only have that.
        /// </summary>
        public string TypeName { get; }

        /// <summary>
        /// The method name.
        /// </summary>
        public string MethodName { get; }

        /// <summary>
        /// Ordered parameter type names (may be empty, never null).
        /// </summary>
        public string[] ParameterTypeNames { get; }

        public MethodSignature(string typeName, string methodName, params string[] parameterTypeNames)
        {
            TypeName = typeName;
            MethodName = methodName;
            ParameterTypeNames = parameterTypeNames ?? Array.Empty<string>();
        }

        public bool Equals(MethodSignature other)
        {
            if (other == null) return false;
            if (ReferenceEquals(this, other)) return true;

            return string.Equals(TypeName, other.TypeName, StringComparison.Ordinal)
                && string.Equals(MethodName, other.MethodName, StringComparison.Ordinal)
                && ParameterTypeNames.Length == other.ParameterTypeNames.Length
                && ParameterTypeNames.SequenceEqual(other.ParameterTypeNames, StringComparer.Ordinal);
        }

        public override bool Equals(object obj) => Equals(obj as MethodSignature);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(TypeName ?? string.Empty);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(MethodName ?? string.Empty);
                foreach (var p in ParameterTypeNames)
                {
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(p ?? string.Empty);
                }
                return hash;
            }
        }

        public override string ToString()
        {
            return $"{TypeName}.{MethodName}({string.Join(", ", ParameterTypeNames)})";
        }
    }
}
