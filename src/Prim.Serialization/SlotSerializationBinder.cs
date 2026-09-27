using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Prim.Core;

namespace Prim.Serialization
{
    /// <summary>
    /// Allow-list <see cref="ISerializationBinder"/> for continuation JSON.
    ///
    /// <see cref="SlotEnvelope.Value"/> uses <c>TypeNameHandling.Auto</c>, so Json.NET
    /// reads a <c>$type</c> from the payload and instantiates that type while
    /// deserializing — before <see cref="ContinuationValidator.Validate"/> can look at
    /// the result. Without a binder any loadable type (a gadget) can be constructed
    /// and have its setters run. This binder runs when the <c>$type</c> is read and
    /// throws for any type that is not allowed, so nothing is constructed.
    ///
    /// A type is allowed when any of these permit it:
    /// - the <see cref="SlotTypeResolver"/> built-ins or its registered resolvers
    ///   (<see cref="SlotTypeResolver.IsKnownType"/>);
    /// - the built-in slot types of <see cref="ContinuationValidator"/>;
    /// - the current validator's whitelist (<see cref="ContinuationValidator.IsTypeAllowed"/>).
    /// Arrays and nullables are allowed when their element / underlying type is.
    /// </summary>
    public sealed class SlotSerializationBinder : ISerializationBinder
    {
        private const int MaxNestingDepth = 16;

        private readonly DefaultSerializationBinder _inner = new DefaultSerializationBinder();
        private readonly SlotTypeResolver _resolver;
        private readonly ContinuationValidator _baseTypes;
        private readonly Func<ContinuationValidator> _validator;

        /// <param name="resolver">Slot type resolver whose built-ins and registered resolvers are trusted.</param>
        /// <param name="validator">
        /// Returns the validator whose whitelist also applies, or null. Read on every
        /// bind so a validator assigned after construction takes effect.
        /// </param>
        public SlotSerializationBinder(SlotTypeResolver resolver, Func<ContinuationValidator> validator)
            : this(resolver, SlotTypes.BuiltIns, validator)
        {
        }

        [Obsolete("ContinuationTypeRegistry is obsolete; use SlotSerializationBinder(resolver, validator) and register types on the validator.")]
        public SlotSerializationBinder(
            SlotTypeResolver resolver,
            ContinuationTypeRegistry typeRegistry,
            Func<ContinuationValidator> validator)
            : this(resolver, (typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry))).AllowedTypes, validator)
        {
        }

        /// <param name="baseTypes">Types allowed even when <paramref name="validator"/> returns null.</param>
        internal SlotSerializationBinder(
            SlotTypeResolver resolver,
            ContinuationValidator baseTypes,
            Func<ContinuationValidator> validator)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _baseTypes = baseTypes ?? throw new ArgumentNullException(nameof(baseTypes));
            _validator = validator ?? (() => null);
        }

        public Type BindToType(string assemblyName, string typeName)
        {
            Type type;
            try
            {
                type = _inner.BindToType(assemblyName, typeName);
            }
            catch (JsonSerializationException ex)
            {
                throw Disallowed(assemblyName, typeName, ex);
            }

            if (!IsAllowed(type, 0))
            {
                throw Disallowed(assemblyName, typeName, null);
            }

            return type;
        }

        public void BindToName(Type serializedType, out string assemblyName, out string typeName)
        {
            _inner.BindToName(serializedType, out assemblyName, out typeName);
        }

        private bool IsAllowed(Type type, int depth)
        {
            if (type == null) return false;
            if (_resolver.IsKnownType(type)) return true;
            if (_baseTypes.IsTypeAllowed(type)) return true;

            var validator = _validator();
            if (validator != null && validator.IsTypeAllowed(type)) return true;

            if (depth >= MaxNestingDepth) return false;

            if (type.IsArray)
            {
                return IsAllowed(type.GetElementType(), depth + 1);
            }

            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
            {
                return IsAllowed(underlying, depth + 1);
            }

            return false;
        }

        private static JsonSerializationException Disallowed(string assemblyName, string typeName, Exception inner)
        {
            var name = string.IsNullOrEmpty(assemblyName) ? typeName : $"{typeName}, {assemblyName}";
            return new JsonSerializationException(
                $"Type '{name}' is not allowed in continuation state. " +
                "Register it on the serializer's ContinuationValidator " +
                "or with a SlotTypeResolver resolver.", inner);
        }
    }
}
