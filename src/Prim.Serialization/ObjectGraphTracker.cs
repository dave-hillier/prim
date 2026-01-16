using System;
using System.Collections.Generic;

namespace Prim.Serialization
{
    /// <summary>
    /// Tracks object identity during serialization to handle:
    /// - Circular references
    /// - Multiple references to the same object
    /// - Preserving reference equality after deserialization
    /// </summary>
    public sealed class ObjectGraphTracker
    {
        private readonly Dictionary<object, int> _objectToId;
        private readonly List<object> _idToObject;
        private int _nextId;

        public ObjectGraphTracker()
        {
            _objectToId = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            _idToObject = new List<object>();
            _nextId = 0;
        }

        /// <summary>
        /// Registers an object during serialization.
        /// Returns true if this is the first time seeing this object.
        /// </summary>
        /// <param name="obj">The object to track.</param>
        /// <param name="id">The assigned or existing ID.</param>
        /// <returns>True if newly registered, false if already seen.</returns>
        public bool TryRegister(object obj, out int id)
        {
            if (obj == null)
            {
                id = -1;
                return true;
            }

            if (_objectToId.TryGetValue(obj, out id))
            {
                return false; // Already seen
            }

            id = _nextId++;
            _objectToId[obj] = id;
            return true;
        }

        /// <summary>
        /// Gets the ID for an already-registered object.
        /// </summary>
        public int GetId(object obj)
        {
            if (obj == null) return -1;
            return _objectToId[obj];
        }

        /// <summary>
        /// Checks if an object has already been registered.
        /// </summary>
        public bool IsRegistered(object obj)
        {
            return obj != null && _objectToId.ContainsKey(obj);
        }

        /// <summary>
        /// Registers an object during deserialization.
        /// </summary>
        /// <param name="id">The object's ID.</param>
        /// <param name="obj">The deserialized object.</param>
        public void RegisterDeserialized(int id, object obj)
        {
            // Ensure list is large enough
            while (_idToObject.Count <= id)
            {
                _idToObject.Add(null);
            }
            _idToObject[id] = obj;
        }

        /// <summary>
        /// Gets a previously deserialized object by ID.
        /// </summary>
        public object GetById(int id)
        {
            if (id < 0) return null;
            if (id >= _idToObject.Count) return null;
            return _idToObject[id];
        }

        /// <summary>
        /// Clears all tracking state.
        /// </summary>
        public void Clear()
        {
            _objectToId.Clear();
            _idToObject.Clear();
            _nextId = 0;
        }
    }

    /// <summary>
    /// Compares objects by reference identity.
    /// </summary>
    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

        private ReferenceEqualityComparer() { }

        bool IEqualityComparer<object>.Equals(object x, object y)
        {
            return ReferenceEquals(x, y);
        }

        int IEqualityComparer<object>.GetHashCode(object obj)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
