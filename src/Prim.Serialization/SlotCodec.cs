using System;
using System.Collections.Generic;
using MessagePack;
using Newtonsoft.Json;

namespace Prim.Serialization
{
    /// <summary>
    /// A typed envelope for a single frame slot value.
    ///
    /// Both the JSON and MessagePack serializers encode slots through this
    /// envelope so that a slot round-trips to the EXACT same CLR type on both
    /// paths (issue #40). Without this, JSON.NET widens int-&gt;long and
    /// float-&gt;double and loses Guid/DateTime/decimal distinctions, while
    /// MessagePack's contractless resolver preserves the original CLR type,
    /// making the two serializers non-interchangeable.
    ///
    /// On serialize, <see cref="TypeName"/> records the canonical type name from
    /// <see cref="SlotTypeResolver.GetTypeName"/>. On deserialize, the value is
    /// coerced back to the exact type resolved from that name.
    /// </summary>
    [MessagePackObject]
    public sealed class SlotEnvelope
    {
        /// <summary>
        /// Canonical type name of the slot value, or null when the value is null.
        /// </summary>
        [Key(0)]
        public string TypeName { get; set; }

        /// <summary>
        /// The slot value. For JSON, reference identity of user-defined reference
        /// types is preserved across slots via PreserveReferencesHandling, and the
        /// concrete type is carried via TypeNameHandling.
        /// </summary>
        [Key(1)]
        [JsonProperty(TypeNameHandling = TypeNameHandling.Auto)]
        public object Value { get; set; }
    }

    /// <summary>
    /// Encodes and decodes slot arrays through <see cref="SlotEnvelope"/>.
    /// Shared by both continuation serializers so the typed slot encoding is
    /// canonical across formats.
    /// </summary>
    public sealed class SlotCodec
    {
        private readonly SlotTypeResolver _resolver;

        public SlotCodec(SlotTypeResolver resolver)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        /// <summary>
        /// Wraps a raw slot array into typed envelopes. Null slots round-trip as
        /// envelopes with a null <see cref="SlotEnvelope.TypeName"/> and value.
        /// </summary>
        public SlotEnvelope[] Encode(object[] slots)
        {
            if (slots == null) return null;

            var encoded = new SlotEnvelope[slots.Length];
            for (var i = 0; i < slots.Length; i++)
            {
                var value = slots[i];
                if (value == null)
                {
                    encoded[i] = new SlotEnvelope { TypeName = null, Value = null };
                }
                else
                {
                    encoded[i] = new SlotEnvelope
                    {
                        TypeName = _resolver.GetTypeName(value.GetType()),
                        Value = value
                    };
                }
            }

            return encoded;
        }

        /// <summary>
        /// Restores a raw slot array from typed envelopes, coercing each value
        /// back to its exact recorded CLR type.
        /// </summary>
        public object[] Decode(SlotEnvelope[] envelopes)
        {
            if (envelopes == null) return null;

            var decoded = new object[envelopes.Length];
            for (var i = 0; i < envelopes.Length; i++)
            {
                decoded[i] = DecodeValue(envelopes[i]);
            }

            return decoded;
        }

        private object DecodeValue(SlotEnvelope envelope)
        {
            if (envelope == null) return null;
            if (envelope.Value == null) return null;
            if (string.IsNullOrEmpty(envelope.TypeName)) return envelope.Value;

            var targetType = _resolver.ResolveType(envelope.TypeName);
            return Coerce(envelope.Value, targetType);
        }

        /// <summary>
        /// Coerces a deserialized value to the exact target CLR type. Handles the
        /// widening that JSON.NET applies (long/double) as well as
        /// Guid/DateTime/decimal that may arrive as strings, while leaving values
        /// that are already the correct type (or reference types revived via type
        /// metadata) untouched.
        /// </summary>
        internal static object Coerce(object value, Type targetType)
        {
            if (value == null || targetType == null) return value;

            var valueType = value.GetType();
            if (valueType == targetType) return value;

            // Array slots (e.g. int[]). MessagePack's contractless resolver revives
            // an object-typed array slot as object[] of widened/byte-typed elements
            // (so int[] {1,2,3} comes back as object[] of System.Byte), whereas JSON
            // already produces the exact element type. Rebuild a correctly-typed
            // T[] so both formats agree on the slot's runtime type (issue #40). The
            // valueType == targetType short-circuit above means JSON arrays that are
            // already correct are left untouched.
            if (targetType.IsArray)
            {
                return CoerceArray(value, targetType);
            }

            // Reference types and other assignable values were already revived as
            // the correct concrete type (JSON TypeNameHandling / MessagePack
            // contractless resolver). Don't disturb them.
            if (!targetType.IsValueType && targetType.IsAssignableFrom(valueType))
            {
                return value;
            }

            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (underlying.IsEnum)
            {
                return Enum.ToObject(underlying, Convert.ChangeType(value, Enum.GetUnderlyingType(underlying)));
            }

            if (underlying == typeof(Guid))
            {
                if (value is Guid g) return g;
                if (value is string gs) return Guid.Parse(gs);
                if (value is byte[] gb) return new Guid(gb);
                return value;
            }

            if (underlying == typeof(DateTime))
            {
                if (value is DateTime dt) return dt;
                if (value is string ds) return DateTime.Parse(ds, null, System.Globalization.DateTimeStyles.RoundtripKind);
                return Convert.ToDateTime(value);
            }

            if (underlying == typeof(DateTimeOffset))
            {
                if (value is DateTimeOffset dto) return dto;
                if (value is string dos) return DateTimeOffset.Parse(dos, null, System.Globalization.DateTimeStyles.RoundtripKind);
                return value;
            }

            if (underlying == typeof(TimeSpan))
            {
                if (value is TimeSpan ts) return ts;
                if (value is string tss) return TimeSpan.Parse(tss);
                if (value is long tl) return new TimeSpan(tl);
                return value;
            }

            if (underlying == typeof(decimal))
            {
                return Convert.ToDecimal(value);
            }

            if (underlying == typeof(char))
            {
                if (value is char c) return c;
                if (value is string cs && cs.Length > 0) return cs[0];
                return Convert.ToChar(value);
            }

            // Primitive numeric / bool / string widening or narrowing.
            if (underlying.IsPrimitive || underlying == typeof(string))
            {
                try
                {
                    return Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (InvalidCastException)
                {
                    return value;
                }
            }

            return value;
        }

        /// <summary>
        /// Rebuilds an array slot as the exact element type recorded in the
        /// envelope, coercing each element via <see cref="Coerce"/>. Handles null
        /// and empty arrays. Only the multi-element/source enumeration is
        /// rebuilt; a value that is already the exact target array type never
        /// reaches here (caller short-circuits on an exact type match).
        /// </summary>
        private static object CoerceArray(object value, Type targetType)
        {
            var elementType = targetType.GetElementType();
            if (elementType == null) return value;

            // Source may be a typed array (object[], byte[], ...) or any other
            // enumerable revived by the underlying serializer.
            if (value is not System.Collections.IEnumerable enumerable)
            {
                return value;
            }

            var items = new List<object>();
            foreach (var item in enumerable)
            {
                items.Add(item);
            }

            var result = Array.CreateInstance(elementType, items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                result.SetValue(Coerce(items[i], elementType), i);
            }

            return result;
        }
    }
}
