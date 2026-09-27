using System;
using System.Collections.Generic;

namespace Prim.Serialization
{
    /// <summary>
    /// Types a slot value may have when continuation state is deserialized. Used by
    /// both <see cref="JsonContinuationSerializer"/> and
    /// <see cref="MessagePackContinuationSerializer"/>.
    /// </summary>
    public sealed class ContinuationTypeRegistry
    {
        private static readonly Type[] DefaultTypes =
        {
            typeof(bool),
            typeof(byte),
            typeof(sbyte),
            typeof(short),
            typeof(ushort),
            typeof(int),
            typeof(uint),
            typeof(long),
            typeof(ulong),
            typeof(float),
            typeof(double),
            typeof(decimal),
            typeof(char),
            typeof(string),
            typeof(DateTime),
            typeof(DateTimeOffset),
            typeof(TimeSpan),
            typeof(Guid)
        };

        private readonly HashSet<Type> _allowedTypes;

        public ContinuationTypeRegistry(IEnumerable<Type> allowedTypes)
        {
            if (allowedTypes == null) throw new ArgumentNullException(nameof(allowedTypes));
            _allowedTypes = new HashSet<Type>(allowedTypes);
        }

        public static ContinuationTypeRegistry Default { get; } = new ContinuationTypeRegistry(DefaultTypes);

        /// <summary>
        /// Returns a new registry that allows this registry's types plus <paramref name="types"/>.
        /// </summary>
        public ContinuationTypeRegistry With(params Type[] types)
        {
            if (types == null) throw new ArgumentNullException(nameof(types));
            var combined = new HashSet<Type>(_allowedTypes);
            combined.UnionWith(types);
            return new ContinuationTypeRegistry(combined);
        }

        public bool IsAllowed(Type type)
        {
            if (type == null) return true;

            if (type.IsEnum) return true;

            if (type.IsArray)
            {
                return IsAllowed(type.GetElementType());
            }

            var underlyingNullable = Nullable.GetUnderlyingType(type);
            if (underlyingNullable != null)
            {
                return IsAllowed(underlyingNullable);
            }

            return _allowedTypes.Contains(type);
        }

        public bool IsAllowedValue(object value)
        {
            if (value == null) return true;

            if (value is Array array)
            {
                foreach (var item in array)
                {
                    if (!IsAllowedValue(item))
                    {
                        return false;
                    }
                }

                return true;
            }

            return IsAllowed(value.GetType());
        }
    }
}
