using System;
using System.Collections.Generic;

namespace Prim.Serialization
{
    /// <summary>
    /// Resolves types for slots during deserialization.
    /// Handles primitives, common BCL types, and custom types.
    /// </summary>
    public sealed class SlotTypeResolver
    {
        private readonly Dictionary<string, Type> _typeCache;
        private readonly List<Func<string, Type>> _customResolvers;

        public SlotTypeResolver()
        {
            _typeCache = new Dictionary<string, Type>();
            _customResolvers = new List<Func<string, Type>>();
            RegisterBuiltInTypes();
        }

        /// <summary>
        /// Registers a custom type resolver.
        /// </summary>
        public void AddResolver(Func<string, Type> resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            _customResolvers.Add(resolver);
        }

        /// <summary>
        /// Resolves a type by its full name.
        /// </summary>
        public Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            // Check cache first
            if (_typeCache.TryGetValue(typeName, out var cached))
            {
                return cached;
            }

            // Try custom resolvers (user-registered, trusted)
            foreach (var resolver in _customResolvers)
            {
                var resolved = resolver(typeName);
                if (resolved != null)
                {
                    _typeCache[typeName] = resolved;
                    return resolved;
                }
            }

            // Only resolve types that are explicitly registered or cached.
            // Do NOT fall back to Type.GetType or assembly scanning, as that
            // would allow arbitrary type resolution (security risk).
            return null;
        }

        /// <summary>
        /// Gets a short name for serialization if possible.
        /// </summary>
        public string GetTypeName(Type type)
        {
            if (type == null) return null;

            // For built-in types, use short names
            if (type == typeof(int)) return "int";
            if (type == typeof(long)) return "long";
            if (type == typeof(short)) return "short";
            if (type == typeof(byte)) return "byte";
            if (type == typeof(sbyte)) return "sbyte";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(decimal)) return "decimal";
            if (type == typeof(char)) return "char";
            if (type == typeof(string)) return "string";
            if (type == typeof(object)) return "object";

            // Use assembly-qualified name for complex types
            return type.AssemblyQualifiedName;
        }

        private void RegisterBuiltInTypes()
        {
            // Primitives
            _typeCache["int"] = typeof(int);
            _typeCache["long"] = typeof(long);
            _typeCache["short"] = typeof(short);
            _typeCache["byte"] = typeof(byte);
            _typeCache["sbyte"] = typeof(sbyte);
            _typeCache["bool"] = typeof(bool);
            _typeCache["float"] = typeof(float);
            _typeCache["double"] = typeof(double);
            _typeCache["decimal"] = typeof(decimal);
            _typeCache["char"] = typeof(char);
            _typeCache["string"] = typeof(string);
            _typeCache["object"] = typeof(object);

            // System types by full name
            _typeCache["System.Int32"] = typeof(int);
            _typeCache["System.Int64"] = typeof(long);
            _typeCache["System.Int16"] = typeof(short);
            _typeCache["System.Byte"] = typeof(byte);
            _typeCache["System.SByte"] = typeof(sbyte);
            _typeCache["System.Boolean"] = typeof(bool);
            _typeCache["System.Single"] = typeof(float);
            _typeCache["System.Double"] = typeof(double);
            _typeCache["System.Decimal"] = typeof(decimal);
            _typeCache["System.Char"] = typeof(char);
            _typeCache["System.String"] = typeof(string);
            _typeCache["System.Object"] = typeof(object);

            // Common types
            _typeCache["System.DateTime"] = typeof(DateTime);
            _typeCache["System.TimeSpan"] = typeof(TimeSpan);
            _typeCache["System.Guid"] = typeof(Guid);
            _typeCache["System.DateTimeOffset"] = typeof(DateTimeOffset);
        }
    }
}
