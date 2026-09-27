using System;
using Newtonsoft.Json;
using Prim.Core;
using Newtonsoft.Json.Serialization;

namespace Prim.Serialization
{
    /// <summary>
    /// Resolves <c>$type</c> names during JSON deserialization and refuses any type
    /// the allow-list rejects. This runs before Json.NET constructs the object, so a
    /// payload cannot use <c>$type</c> to run the constructor or setters of an
    /// arbitrary type. A rejected type throws <see cref="ValidationException"/>.
    /// </summary>
    internal sealed class AllowListSerializationBinder : ISerializationBinder
    {
        private static readonly DefaultSerializationBinder Default = new DefaultSerializationBinder();

        private readonly Func<Type, bool> _isAllowed;

        public AllowListSerializationBinder(Func<Type, bool> isAllowed)
        {
            _isAllowed = isAllowed ?? throw new ArgumentNullException(nameof(isAllowed));
        }

        public Type BindToType(string assemblyName, string typeName)
        {
            Type type;
            try
            {
                type = Default.BindToType(assemblyName, typeName);
            }
            catch (JsonSerializationException)
            {
                type = null;
            }

            if (type == null || !_isAllowed(type))
            {
                // Json.NET wraps this in a JsonSerializationException;
                // JsonContinuationSerializer unwraps it.
                throw new ValidationException(ValidationResult.Failure(
                    $"Type '{typeName}, {assemblyName}' is not allowed in continuation state. " +
                    "Add it to the serializer's ContinuationTypeRegistry or to the Validator's allowed types."));
            }

            return type;
        }

        public void BindToName(Type serializedType, out string assemblyName, out string typeName)
        {
            Default.BindToName(serializedType, out assemblyName, out typeName);
        }
    }
}
