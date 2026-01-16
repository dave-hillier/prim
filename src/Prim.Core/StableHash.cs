using System;

namespace Prim.Core
{
    /// <summary>
    /// Provides stable, deterministic hash functions that produce consistent
    /// results across different processes and .NET versions.
    ///
    /// Unlike String.GetHashCode(), these functions are guaranteed to return
    /// the same value for the same input, making them suitable for serialization
    /// and cross-process scenarios.
    /// </summary>
    public static class StableHash
    {
        /// <summary>
        /// Computes a stable FNV-1a hash for a string.
        /// FNV-1a is a fast, non-cryptographic hash with good distribution.
        /// </summary>
        /// <param name="value">The string to hash.</param>
        /// <returns>A 32-bit hash code.</returns>
        public static int ComputeFnv1a(string value)
        {
            if (value == null) return 0;

            unchecked
            {
                const uint fnvPrime = 16777619;
                const uint fnvOffsetBasis = 2166136261;

                uint hash = fnvOffsetBasis;
                foreach (char c in value)
                {
                    hash ^= c;
                    hash *= fnvPrime;
                }
                return (int)hash;
            }
        }

        /// <summary>
        /// Combines multiple hash codes into a single hash.
        /// Uses a mixing function to maintain good distribution.
        /// </summary>
        /// <param name="hashes">The hash codes to combine.</param>
        /// <returns>A combined hash code.</returns>
        public static int Combine(params int[] hashes)
        {
            if (hashes == null || hashes.Length == 0) return 0;

            unchecked
            {
                int hash = 17;
                foreach (var h in hashes)
                {
                    // Rotate and mix
                    hash = ((hash << 5) + hash) ^ h;
                }
                return hash;
            }
        }

        /// <summary>
        /// Generates a stable method token from method signature components.
        /// This is deterministic across processes and .NET versions.
        /// </summary>
        /// <param name="typeName">The full type name.</param>
        /// <param name="methodName">The method name.</param>
        /// <param name="parameterTypes">Parameter type names.</param>
        /// <returns>A stable hash code for the method.</returns>
        public static int GenerateMethodToken(string typeName, string methodName, params string[] parameterTypes)
        {
            var typeHash = ComputeFnv1a(typeName);
            var methodHash = ComputeFnv1a(methodName);

            if (parameterTypes == null || parameterTypes.Length == 0)
            {
                return Combine(typeHash, methodHash);
            }

            var paramHashes = new int[parameterTypes.Length + 2];
            paramHashes[0] = typeHash;
            paramHashes[1] = methodHash;
            for (int i = 0; i < parameterTypes.Length; i++)
            {
                paramHashes[i + 2] = ComputeFnv1a(parameterTypes[i]);
            }

            return Combine(paramHashes);
        }
    }
}
