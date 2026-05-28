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
        private readonly SlotCodec _codec;

        public MessagePackContinuationSerializer()
            : this(new SlotTypeResolver())
        {
        }

        public MessagePackContinuationSerializer(SlotTypeResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            _codec = new SlotCodec(resolver);

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
            : this(options, new SlotTypeResolver())
        {
        }

        public MessagePackContinuationSerializer(MessagePackSerializerOptions options, SlotTypeResolver resolver)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            _codec = new SlotCodec(resolver);
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
                    Slots = _codec.Decode(d.Slots),
                    Caller = caller
                };
            }

            return caller;
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
