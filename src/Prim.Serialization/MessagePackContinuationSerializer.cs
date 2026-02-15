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
        private readonly ContinuationTypeRegistry _typeRegistry;

        public MessagePackContinuationSerializer()
        {
            _typeRegistry = ContinuationTypeRegistry.Default;
            _options = CreateRestrictedOptions(_typeRegistry);
        }

        public MessagePackContinuationSerializer(ContinuationTypeRegistry typeRegistry)
        {
            _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
            _options = CreateRestrictedOptions(_typeRegistry);
        }

        public MessagePackContinuationSerializer(MessagePackSerializerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _typeRegistry = ContinuationTypeRegistry.Default;
        }

        public MessagePackContinuationSerializer(MessagePackSerializerOptions options, ContinuationTypeRegistry typeRegistry)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
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
            ValidateDtoTypes(dto);
            return ConvertFromDto(dto);
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

            return new HostFrameRecordDto
            {
                MethodToken = frame.MethodToken,
                YieldPointId = frame.YieldPointId,
                Slots = frame.Slots,
                Caller = ConvertFrameToDto(frame.Caller)
            };
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

            return new HostFrameRecord
            {
                MethodToken = dto.MethodToken,
                YieldPointId = dto.YieldPointId,
                Slots = dto.Slots,
                Caller = ConvertFrameFromDto(dto.Caller)
            };
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

            if (frame.Slots != null)
            {
                for (var i = 0; i < frame.Slots.Length; i++)
                {
                    ValidateValue(frame.Slots[i], $"Slots[{i}]");
                }
            }

            ValidateFrameTypes(frame.Caller);
        }

        private void ValidateValue(object value, string context)
        {
            if (!_typeRegistry.IsAllowedValue(value))
            {
                var typeName = value?.GetType().FullName ?? "null";
                throw new MessagePackSerializationException($"Type '{typeName}' is not allowed for {context}.");
            }
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
        public object[] Slots { get; set; }

        [Key(3)]
        public HostFrameRecordDto Caller { get; set; }
    }
}
