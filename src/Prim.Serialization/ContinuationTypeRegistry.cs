using System;
using System.Collections.Generic;
using Prim.Core;

namespace Prim.Serialization
{
    /// <summary>
    /// Obsolete: <see cref="ContinuationValidator"/> now owns the slot type allow-list.
    /// Register types with <see cref="ContinuationValidator.RegisterAllowedType"/> and
    /// set the serializer's <c>Validator</c>.
    ///
    /// Kept as a thin wrapper so existing callers compile: it holds a validator with
    /// the built-in types plus the types passed in. The built-in types are always
    /// allowed; a registry can no longer be narrower than them.
    /// </summary>
    [Obsolete("Register types on a ContinuationValidator and set the serializer's Validator instead.")]
    public sealed class ContinuationTypeRegistry
    {
        private readonly Type[] _extraTypes;

        public ContinuationTypeRegistry(IEnumerable<Type> allowedTypes)
        {
            if (allowedTypes == null) throw new ArgumentNullException(nameof(allowedTypes));
            _extraTypes = new List<Type>(allowedTypes).ToArray();
            AllowedTypes = new ContinuationValidator();
            AllowedTypes.RegisterAllowedTypes(_extraTypes);
        }

        /// <summary>
        /// The built-in slot types only.
        /// </summary>
        public static ContinuationTypeRegistry Default { get; } = new ContinuationTypeRegistry(Array.Empty<Type>());

        /// <summary>
        /// The validator whose type allow-list this registry exposes.
        /// </summary>
        internal ContinuationValidator AllowedTypes { get; }

        /// <summary>
        /// Returns a new registry that allows this registry's types plus <paramref name="types"/>.
        /// </summary>
        public ContinuationTypeRegistry With(params Type[] types)
        {
            if (types == null) throw new ArgumentNullException(nameof(types));
            var combined = new List<Type>(_extraTypes);
            combined.AddRange(types);
            return new ContinuationTypeRegistry(combined);
        }

        public bool IsAllowed(Type type) => AllowedTypes.IsTypeAllowed(type);

        public bool IsAllowedValue(object value) => AllowedTypes.IsValueAllowed(value);
    }
}
