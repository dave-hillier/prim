using System;
using Prim.Core;
using Newtonsoft.Json;

namespace Prim.Serialization
{
    /// <summary>
    /// Serializes continuation state using JSON.
    /// Human-readable format, useful for debugging.
    /// </summary>
    public sealed class JsonContinuationSerializer : IContinuationSerializer
    {
        private readonly JsonSerializerSettings _settings;

        public JsonContinuationSerializer()
        {
            _settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Include
            };
        }

        public JsonContinuationSerializer(JsonSerializerSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Creates a compact serializer (no indentation).
        /// </summary>
        public static JsonContinuationSerializer Compact()
        {
            return new JsonContinuationSerializer(new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
                Formatting = Formatting.None,
                NullValueHandling = NullValueHandling.Ignore
            });
        }

        /// <inheritdoc/>
        public byte[] Serialize(ContinuationState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var dto = ConvertToDto(state);
            var json = JsonConvert.SerializeObject(dto, _settings);
            return System.Text.Encoding.UTF8.GetBytes(json);
        }

        /// <inheritdoc/>
        public ContinuationState Deserialize(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            var json = System.Text.Encoding.UTF8.GetString(data);
            var dto = JsonConvert.DeserializeObject<JsonContinuationStateDto>(json, _settings);
            return ConvertFromDto(dto);
        }

        /// <summary>
        /// Serializes to a JSON string (for debugging).
        /// </summary>
        public string SerializeToString(ContinuationState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var dto = ConvertToDto(state);
            return JsonConvert.SerializeObject(dto, _settings);
        }

        /// <summary>
        /// Deserializes from a JSON string.
        /// </summary>
        public ContinuationState DeserializeFromString(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));

            var dto = JsonConvert.DeserializeObject<JsonContinuationStateDto>(json, _settings);
            return ConvertFromDto(dto);
        }

        private JsonContinuationStateDto ConvertToDto(ContinuationState state)
        {
            return new JsonContinuationStateDto
            {
                Version = state.Version,
                YieldedValue = state.YieldedValue,
                StackHead = ConvertFrameToDto(state.StackHead)
            };
        }

        private JsonHostFrameRecordDto ConvertFrameToDto(HostFrameRecord frame)
        {
            if (frame == null) return null;

            return new JsonHostFrameRecordDto
            {
                MethodToken = frame.MethodToken,
                YieldPointId = frame.YieldPointId,
                Slots = frame.Slots,
                Caller = ConvertFrameToDto(frame.Caller)
            };
        }

        private ContinuationState ConvertFromDto(JsonContinuationStateDto dto)
        {
            if (dto == null) return null;

            return new ContinuationState
            {
                Version = dto.Version,
                YieldedValue = dto.YieldedValue,
                StackHead = ConvertFrameFromDto(dto.StackHead)
            };
        }

        private HostFrameRecord ConvertFrameFromDto(JsonHostFrameRecordDto dto)
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
    /// JSON DTO for ContinuationState.
    /// </summary>
    public sealed class JsonContinuationStateDto
    {
        public int Version { get; set; }
        public object YieldedValue { get; set; }
        public JsonHostFrameRecordDto StackHead { get; set; }
    }

    /// <summary>
    /// JSON DTO for HostFrameRecord.
    /// </summary>
    public sealed class JsonHostFrameRecordDto
    {
        public int MethodToken { get; set; }
        public int YieldPointId { get; set; }
        public object[] Slots { get; set; }
        public JsonHostFrameRecordDto Caller { get; set; }
    }
}
