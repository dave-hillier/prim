using System;
using System.Collections.Generic;
using Prim.Core;
using MessagePack;

namespace Prim.Serialization
{
    /// <summary>
    /// Serializes continuation state using MessagePack.
    /// Fast and compact binary format.
    /// </summary>
    public sealed class MessagePackContinuationSerializer : IContinuationSerializer
    {
        private readonly MessagePackSerializerOptions _options;

        public MessagePackContinuationSerializer()
        {
            // Use contractless resolver to serialize any object
            _options = MessagePackSerializerOptions.Standard
                .WithResolver(MessagePack.Resolvers.ContractlessStandardResolver.Instance)
                .WithCompression(MessagePackCompression.Lz4BlockArray);
        }

        public MessagePackContinuationSerializer(MessagePackSerializerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
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
