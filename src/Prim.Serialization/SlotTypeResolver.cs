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
        private readonly HashSet<Type> _builtInTypes;

        public SlotTypeResolver()
        {
            _typeCache = new Dictionary<string, Type>();
            _customResolvers = new List<Func<string, Type>>();
            RegisterBuiltInTypes();
            _builtInTypes = new HashSet<Type>(_typeCache.Values);
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

            // Resolve assembly-qualified / full names. Resolution is NOT the security
            // boundary and constructs nothing. Construction is gated elsewhere:
            // - JSON: SlotSerializationBinder rejects a disallowed $type while Json.NET
            //   reads the payload, before that type is instantiated.
            // - MessagePack: the contractless resolver never reads type names from the
            //   payload, and typed revival only runs for types the validator allows.
            // ContinuationValidator.Validate then checks the rebuilt state. Resolving
            // here is required so GetTypeName(AssemblyQualifiedName) round-trips
            // (issue #40).
            var type = Type.GetType(typeName);
            if (type != null)
            {
                _typeCache[typeName] = type;
                return type;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName);
                if (type != null)
                {
                    _typeCache[typeName] = type;
                    return type;
                }
            }

            throw new TypeLoadException($"Could not resolve type: {typeName}");
        }

        /// <summary>
        /// True when <paramref name="type"/> is one of the built-in slot types or a
        /// registered custom resolver maps the type's full or assembly-qualified name
        /// to it. Types found only by the reflection fallback in
        /// <see cref="ResolveType"/> do not count.
        /// </summary>
        public bool IsKnownType(Type type)
        {
            if (type == null) return false;
            if (_builtInTypes.Contains(type)) return true;

            foreach (var resolver in _customResolvers)
            {
                if (resolver(type.AssemblyQualifiedName) == type) return true;
                if (resolver(type.FullName) == type) return true;
            }

            return false;
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
