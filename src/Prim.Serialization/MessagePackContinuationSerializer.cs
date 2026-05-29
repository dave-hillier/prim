using System;
using System.Collections.Generic;
using Prim.Core;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace Prim.Serialization
{
    /// <summary>
    /// Serializes continuation state using MessagePack.
    /// Fast and compact binary format.
    /// </summary>
    public sealed class MessagePackContinuationSerializer : IContinuationSerializer
    {
        private readonly MessagePackSerializerOptions _options;
        private readonly SlotCodec _codec;
        private readonly SlotTypeResolver _resolver;
        private readonly ContinuationTypeRegistry _typeRegistry;

        /// <summary>
        /// Optional validator. When set it does two things on <see cref="Deserialize"/>:
        ///
        /// 1. Drives WHITELIST-DRIVEN slot-value revival (issue #41). With the
        ///    contractless resolver an object-typed slot value revives a custom
        ///    reference type as a Dictionary. Using the envelope's recorded TypeName,
        ///    the value is re-materialized into that concrete CLR type ONLY when the
        ///    type is allowed by this validator's policy. A disallowed type is left
        ///    un-revived and then rejected by step 2, so an attacker cannot use the
        ///    revival to instantiate an arbitrary shape.
        ///
        /// 2. Runs <see cref="Prim.Core.ContinuationValidator.Validate"/> on the
        ///    final state before returning, so a disallowed type / malformed frame
        ///    throws instead of being handed back.
        ///
        /// Leave null for trusted/round-trip scenarios.
        /// </summary>
        public Prim.Core.ContinuationValidator Validator { get; set; }

        public MessagePackContinuationSerializer()
            : this(new SlotTypeResolver(), ContinuationTypeRegistry.Default)
        {
        }

        public MessagePackContinuationSerializer(SlotTypeResolver resolver)
            : this(resolver, ContinuationTypeRegistry.Default)
        {
        }

        // main's API: choose the allow-list registry while keeping v1's typed-envelope path.
        public MessagePackContinuationSerializer(ContinuationTypeRegistry typeRegistry)
            : this(new SlotTypeResolver(), typeRegistry)
        {
        }

        public MessagePackContinuationSerializer(SlotTypeResolver resolver, ContinuationTypeRegistry typeRegistry)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            _resolver = resolver;
            _codec = new SlotCodec(resolver);
            _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));

            // Use contractless resolver to serialize any object
            _options = MessagePackSerializerOptions.Standard
                .WithResolver(MessagePack.Resolvers.ContractlessStandardResolver.Instance)
                .WithCompression(MessagePackCompression.Lz4BlockArray)
                // Bound deserialize recursion so a deep Caller chain throws before
                // the reader recursively materializes it and overflows the stack
                // (issue #42). UntrustedData defaults to a 500-deep graph; raise it
                // to the same bound the JSON path uses so a legitimate chain up to
                // FrameDepthGuard.MaxFrameDepth is accepted but a pathological one
                // is rejected with a catchable exception.
                .WithSecurity(MessagePackSecurity.UntrustedData
                    .WithMaximumObjectGraphDepth(FrameDepthGuard.MaxParserDepth));
        }

        public MessagePackContinuationSerializer(MessagePackSerializerOptions options)
            : this(options, new SlotTypeResolver(), ContinuationTypeRegistry.Default)
        {
        }

        public MessagePackContinuationSerializer(MessagePackSerializerOptions options, SlotTypeResolver resolver)
            : this(options, resolver, ContinuationTypeRegistry.Default)
        {
        }

        // main's API: explicit options + allow-list registry.
        public MessagePackContinuationSerializer(MessagePackSerializerOptions options, ContinuationTypeRegistry typeRegistry)
            : this(options, new SlotTypeResolver(), typeRegistry)
        {
        }

        public MessagePackContinuationSerializer(MessagePackSerializerOptions options, SlotTypeResolver resolver, ContinuationTypeRegistry typeRegistry)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            _resolver = resolver;
            _codec = new SlotCodec(resolver);
            _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        }

        /// <inheritdoc/>
        public byte[] Serialize(ContinuationState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var dto = ConvertToDto(state);
            return MessagePackSerializer.Serialize(dto, _options);
        }

        /// <inheritdoc/>
        public ContinuationState Deserialize(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            var dto = MessagePackSerializer.Deserialize<ContinuationStateDto>(data, _options);
            // Belt-and-suspenders: main's registry allow-list check on the raw DTO
            // values (#41) AND v1's typed-envelope revival + ContinuationValidator
            // on the final state.
            ValidateDtoTypes(dto);
            var state = ConvertFromDto(dto);
            Validator?.Validate(state);
            return state;
        }

        private ContinuationStateDto ConvertToDto(ContinuationState state)
        {
            return new ContinuationStateDto
            {
                Version = state.Version,
                YieldedValue = state.YieldedValue,
                StackHead = ConvertFrameToDto(state.StackHead)
            };
        }

        private HostFrameRecordDto ConvertFrameToDto(HostFrameRecord frame)
        {
            if (frame == null) return null;

            // Build the DTO chain iteratively (leaf -> root) with a depth guard so
            // a maliciously deep Caller chain throws a clean exception instead of
            // overflowing the stack before validation runs (issue #42).
            HostFrameRecordDto head = null;
            HostFrameRecordDto tail = null;
            var depth = 0;

            for (var current = frame; current != null; current = current.Caller)
            {
                FrameDepthGuard.Check(++depth);

                var node = new HostFrameRecordDto
                {
                    MethodToken = current.MethodToken,
                    YieldPointId = current.YieldPointId,
                    Slots = _codec.Encode(current.Slots),
                    Caller = null
                };

                if (head == null)
                {
                    head = node;
                }
                else
                {
                    tail.Caller = node;
                }

                tail = node;
            }

            return head;
        }

        private ContinuationState ConvertFromDto(ContinuationStateDto dto)
        {
            if (dto == null) return null;

            return new ContinuationState
            {
                Version = dto.Version,
                YieldedValue = dto.YieldedValue,
                StackHead = ConvertFrameFromDto(dto.StackHead)
            };
        }

        private HostFrameRecord ConvertFrameFromDto(HostFrameRecordDto dto)
        {
            if (dto == null) return null;

            // Decode the DTO chain iteratively with a depth guard so a deep Caller
            // chain cannot overflow the stack (issue #42).
            var dtos = new List<HostFrameRecordDto>();
            var depth = 0;
            for (var current = dto; current != null; current = current.Caller)
            {
                FrameDepthGuard.Check(++depth);
                dtos.Add(current);
            }

            HostFrameRecord caller = null;
            for (var i = dtos.Count - 1; i >= 0; i--)
            {
                var d = dtos[i];
                caller = new HostFrameRecord
                {
                    MethodToken = d.MethodToken,
                    YieldPointId = d.YieldPointId,
                    Slots = DecodeSlots(d.Slots),
                    Caller = caller
                };
            }

            return caller;
        }

        /// <summary>
        /// Decodes slot envelopes, applying WHITELIST-DRIVEN typed revival for
        /// reference-type slots (issue #41). The contractless resolver revives an
        /// object-typed custom reference type as a Dictionary; using the envelope's
        /// recorded TypeName we re-materialize it into the concrete CLR type, but
        /// ONLY when that type is permitted by the validator's allowed-type policy.
        /// A disallowed type is left as-is (a Dictionary) and is then rejected by
        /// the validator that runs after deserialization, so revival cannot be used
        /// to instantiate an arbitrary type.
        /// </summary>
        private object[] DecodeSlots(SlotEnvelope[] envelopes)
        {
            var decoded = _codec.Decode(envelopes);
            if (decoded == null || envelopes == null) return decoded;

            for (var i = 0; i < envelopes.Length && i < decoded.Length; i++)
            {
                decoded[i] = ReviveReferenceType(envelopes[i], decoded[i]);
            }

            return decoded;
        }

        private object ReviveReferenceType(SlotEnvelope envelope, object decodedValue)
        {
            if (envelope == null || decodedValue == null) return decodedValue;
            if (string.IsNullOrEmpty(envelope.TypeName)) return decodedValue;

            Type targetType;
            try
            {
                targetType = _resolver.ResolveType(envelope.TypeName);
            }
            catch (TypeLoadException)
            {
                // Unknown type name: leave the value un-revived. If a validator is
                // configured it will reject the resulting shape.
                return decodedValue;
            }

            if (targetType == null) return decodedValue;

            // Already the right type (SlotCodec coerced primitives/arrays, or JSON-
            // style metadata revived it). Nothing to do.
            if (targetType.IsInstanceOfType(decodedValue)) return decodedValue;

            // Only re-materialize custom reference types here; value/array/string
            // are handled by SlotCodec.Coerce.
            if (targetType.IsValueType || targetType == typeof(string) || targetType.IsArray)
            {
                return decodedValue;
            }

            // WHITELIST GATE: only instantiate a type the policy allows. Without an
            // allowed-type policy we refuse to re-materialize, leaving the safe
            // (Dictionary) shape for the validator to reject.
            if (Validator == null || !Validator.IsTypeAllowed(targetType))
            {
                return decodedValue;
            }

            // Re-serialize the loosely-revived value (a Dictionary) and deserialize
            // it into the concrete whitelisted type. This recovers reference-type
            // fidelity without enabling the typeless gadget resolver.
            try
            {
                var bytes = MessagePackSerializer.Serialize(decodedValue, _options);
                return MessagePackSerializer.Deserialize(targetType, bytes, _options);
            }
            catch (Exception)
            {
                // Shape did not match the target; leave it for the validator.
                return decodedValue;
            }
        }

        private void ValidateDtoTypes(ContinuationStateDto dto)
        {
            if (dto == null) return;

            ValidateValue(dto.YieldedValue, "YieldedValue");
            ValidateFrameTypes(dto.StackHead);
        }

        private void ValidateFrameTypes(HostFrameRecordDto frame)
        {
            if (frame == null) return;

            // v1 stores slots as typed envelopes. main's allow-list (#41) applies to
            // the actual slot VALUE inside each envelope, not the envelope wrapper.
            // Reference types revived by the contractless resolver arrive as a
            // Dictionary/composite shape and are intentionally left for v1's
            // whitelist-driven revival + ContinuationValidator to authorize; the
            // registry gate here enforces main's allow-list on simple scalar values.
            if (frame.Slots != null)
            {
                for (var i = 0; i < frame.Slots.Length; i++)
                {
                    var envelope = frame.Slots[i];
                    ValidateValue(envelope?.Value, $"Slots[{i}]");
                }
            }

            ValidateFrameTypes(frame.Caller);
        }

        private void ValidateValue(object value, string context)
        {
            if (value == null) return;

            // Composite shapes produced by the contractless resolver for reference
            // types (Dictionary, untyped object[]) are deferred to v1's typed
            // revival + ContinuationValidator. Only scalar/array slot values are
            // gated by main's registry allow-list here.
            if (IsDeferredToRevival(value)) return;

            if (!_typeRegistry.IsAllowedValue(value))
            {
                var typeName = value.GetType().FullName ?? "null";
                throw new MessagePackSerializationException($"Type '{typeName}' is not allowed for {context}.");
            }
        }

        private static bool IsDeferredToRevival(object value)
        {
            // A custom reference type comes back from the contractless resolver as a
            // map (IDictionary) or, for object-typed members, an object[]. These are
            // not final slot types; v1's ReviveReferenceType + Validator decide them.
            if (value is System.Collections.IDictionary) return true;
            if (value is object[]) return true;
            return false;
        }

        private static MessagePackSerializerOptions CreateRestrictedOptions(ContinuationTypeRegistry typeRegistry)
        {
            var resolver = CompositeResolver.Create(new IFormatterResolver[]
            {
                new RestrictedObjectResolver(typeRegistry),
                TypelessContractlessStandardResolver.Instance
            });

            return MessagePackSerializerOptions.Standard
                .WithResolver(resolver)
                .WithCompression(MessagePackCompression.Lz4BlockArray)
                .WithSecurity(MessagePackSecurity.UntrustedData);
        }
    }

    /// <summary>
    /// Registry of allowed types for continuation serialization.
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

    internal sealed class RestrictedObjectResolver : IFormatterResolver
    {
        private readonly ContinuationTypeRegistry _typeRegistry;

        public RestrictedObjectResolver(ContinuationTypeRegistry typeRegistry)
        {
            _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        }

        public IMessagePackFormatter<T> GetFormatter<T>()
        {
            if (typeof(T) == typeof(object))
            {
                return (IMessagePackFormatter<T>)(object)new RestrictedObjectFormatter(_typeRegistry);
            }

            if (typeof(T) == typeof(object[]))
            {
                return (IMessagePackFormatter<T>)(object)new RestrictedObjectArrayFormatter(_typeRegistry);
            }

            return null;
        }
    }

    internal sealed class RestrictedObjectFormatter : IMessagePackFormatter<object>
    {
        private readonly ContinuationTypeRegistry _typeRegistry;

        public RestrictedObjectFormatter(ContinuationTypeRegistry typeRegistry)
        {
            _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        }

        public void Serialize(ref MessagePackWriter writer, object value, MessagePackSerializerOptions options)
        {
            if (!_typeRegistry.IsAllowedValue(value))
            {
                var typeName = value?.GetType().FullName ?? "null";
                throw new MessagePackSerializationException($"Type '{typeName}' is not allowed for YieldedValue or Slots.");
            }

            MessagePackSerializer.Serialize(ref writer, value, options.WithResolver(TypelessContractlessStandardResolver.Instance));
        }

        public object Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            var result = MessagePackSerializer.Deserialize<object>(ref reader, options.WithResolver(TypelessContractlessStandardResolver.Instance));
            if (!_typeRegistry.IsAllowedValue(result))
            {
                var typeName = result?.GetType().FullName ?? "null";
                throw new MessagePackSerializationException($"Type '{typeName}' is not allowed for YieldedValue or Slots.");
            }

            return result;
        }
    }

    internal sealed class RestrictedObjectArrayFormatter : IMessagePackFormatter<object[]>
    {
        private readonly ContinuationTypeRegistry _typeRegistry;

        public RestrictedObjectArrayFormatter(ContinuationTypeRegistry typeRegistry)
        {
            _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        }

        public void Serialize(ref MessagePackWriter writer, object[] value, MessagePackSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNil();
                return;
            }

            writer.WriteArrayHeader(value.Length);

            for (var i = 0; i < value.Length; i++)
            {
                var item = value[i];
                if (!_typeRegistry.IsAllowedValue(item))
                {
                    var typeName = item?.GetType().FullName ?? "null";
                    throw new MessagePackSerializationException($"Type '{typeName}' is not allowed for Slots[{i}].");
                }

                MessagePackSerializer.Serialize(ref writer, item, options.WithResolver(TypelessContractlessStandardResolver.Instance));
            }
        }

        public object[] Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil())
            {
                return null;
            }

            var length = reader.ReadArrayHeader();
            var result = new object[length];

            for (var i = 0; i < length; i++)
            {
                var item = MessagePackSerializer.Deserialize<object>(ref reader, options.WithResolver(TypelessContractlessStandardResolver.Instance));
                if (!_typeRegistry.IsAllowedValue(item))
                {
                    var typeName = item?.GetType().FullName ?? "null";
                    throw new MessagePackSerializationException($"Type '{typeName}' is not allowed for Slots[{i}].");
                }

                result[i] = item;
            }

            return result;
        }
    }

    /// <summary>
    /// DTO for ContinuationState serialization.
    /// </summary>
    [MessagePackObject]
    public sealed class ContinuationStateDto
    {
        [Key(0)]
        public int Version { get; set; }

        [Key(1)]
        public object YieldedValue { get; set; }

        [Key(2)]
        public HostFrameRecordDto StackHead { get; set; }
    }

    /// <summary>
    /// DTO for HostFrameRecord serialization.
    /// </summary>
    [MessagePackObject]
    public sealed class HostFrameRecordDto
    {
        [Key(0)]
        public int MethodToken { get; set; }

        [Key(1)]
        public int YieldPointId { get; set; }

        [Key(2)]
        public SlotEnvelope[] Slots { get; set; }

        [Key(3)]
        public HostFrameRecordDto Caller { get; set; }
    }
}
