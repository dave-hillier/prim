using System;
using System.Collections.Concurrent;

namespace Prim.Runtime
{
    /// <summary>
    /// Registry that maps method tokens to entry point delegates.
    /// Used by ContinuationRunner to resume continuations without
    /// requiring the caller to re-specify the entry point function.
    /// </summary>
    public sealed class EntryPointRegistry
    {
        private readonly ConcurrentDictionary<int, Delegate> _entryPoints = new ConcurrentDictionary<int, Delegate>();

        /// <summary>
        /// Registers an entry point delegate for a method token.
        /// </summary>
        /// <typeparam name="T">The return type of the entry point.</typeparam>
        /// <param name="methodToken">The method token (from StableHash or FrameDescriptor).</param>
        /// <param name="entryPoint">The delegate to invoke when resuming.</param>
        public void Register<T>(int methodToken, Func<T> entryPoint)
        {
            if (entryPoint == null) throw new ArgumentNullException(nameof(entryPoint));
            _entryPoints[methodToken] = entryPoint;
        }

        /// <summary>
        /// Registers an entry point delegate for a method token (void return).
        /// </summary>
        /// <param name="methodToken">The method token.</param>
        /// <param name="entryPoint">The delegate to invoke when resuming.</param>
        public void Register(int methodToken, Action entryPoint)
        {
            if (entryPoint == null) throw new ArgumentNullException(nameof(entryPoint));
            _entryPoints[methodToken] = entryPoint;
        }

        /// <summary>
        /// Unregisters an entry point.
        /// </summary>
        /// <param name="methodToken">The method token to unregister.</param>
        /// <returns>True if the entry point was removed.</returns>
        public bool Unregister(int methodToken)
        {
            return _entryPoints.Remove(methodToken);
        }

        /// <summary>
        /// Tries to get an entry point delegate for a method token.
        /// </summary>
        /// <typeparam name="T">The expected return type.</typeparam>
        /// <param name="methodToken">The method token to look up.</param>
        /// <param name="entryPoint">The entry point delegate if found.</param>
        /// <returns>True if the entry point was found and has the correct type.</returns>
        public bool TryGet<T>(int methodToken, out Func<T> entryPoint)
        {
            if (_entryPoints.TryGetValue(methodToken, out var del) && del is Func<T> func)
            {
                entryPoint = func;
                return true;
            }
            entryPoint = null;
            return false;
        }

        /// <summary>
        /// Tries to get an entry point delegate for a method token (void return).
        /// </summary>
        /// <param name="methodToken">The method token to look up.</param>
        /// <param name="entryPoint">The entry point delegate if found.</param>
        /// <returns>True if the entry point was found.</returns>
        public bool TryGetAction(int methodToken, out Action entryPoint)
        {
            if (_entryPoints.TryGetValue(methodToken, out var del) && del is Action action)
            {
                entryPoint = action;
                return true;
            }
            entryPoint = null;
            return false;
        }

        /// <summary>
        /// Checks if an entry point is registered for the given method token.
        /// </summary>
        public bool Contains(int methodToken)
        {
            return _entryPoints.ContainsKey(methodToken);
        }

        /// <summary>
        /// Gets the number of registered entry points.
        /// </summary>
        public int Count => _entryPoints.Count;

        /// <summary>
        /// Clears all registered entry points.
        /// </summary>
        public void Clear()
        {
            _entryPoints.Clear();
        }
    }
}
