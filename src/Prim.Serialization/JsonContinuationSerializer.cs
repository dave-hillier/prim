using System;
using System.Collections.Generic;
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
        private readonly SlotCodec _codec;

        /// <summary>
        /// Optional validator run on every <see cref="Deserialize"/> /
        /// <see cref="DeserializeFromString"/> call BEFORE the state is returned.
        /// When set, a state that fails validation throws
        /// <see cref="Prim.Core.ValidationException"/> instead of being handed back
        /// to the caller. Leave null for trusted/round-trip scenarios.
        ///
        /// Types on this validator's whitelist are also accepted as a slot
        /// <c>$type</c> by the default <see cref="SlotSerializationBinder"/>.
        ///
        /// SECURITY: deserializing attacker-supplied continuation state and resuming
        /// it is a full takeover primitive. Set this validator (or validate at
        /// resume via ContinuationRunner.Validator) whenever the bytes are untrusted.
        /// </summary>
        public Prim.Core.ContinuationValidator Validator { get; set; }

        public JsonContinuationSerializer()
            : this(new SlotTypeResolver(), ContinuationTypeRegistry.Default)
        {
        }

        public JsonContinuationSerializer(SlotTypeResolver resolver)
            : this(resolver, ContinuationTypeRegistry.Default)
        {
        }

        public JsonContinuationSerializer(ContinuationTypeRegistry typeRegistry)
            : this(new SlotTypeResolver(), typeRegistry)
        {
        }

        public JsonContinuationSerializer(SlotTypeResolver resolver, ContinuationTypeRegistry typeRegistry)
            : this(DefaultSettings(Formatting.Indented, NullValueHandling.Include), resolver, typeRegistry)
        {
        }

        public JsonContinuationSerializer(JsonSerializerSettings settings)
            : this(settings, new SlotTypeResolver(), ContinuationTypeRegistry.Default)
        {
        }

        public JsonContinuationSerializer(JsonSerializerSettings settings, SlotTypeResolver resolver)
            : this(settings, resolver, ContinuationTypeRegistry.Default)
        {
        }

        /// <param name="settings">Json.NET settings. If they carry no SerializationBinder,
        /// a <see cref="SlotSerializationBinder"/> is installed. A binder
        /// the caller set is kept, and the caller is then responsible for what it allows.</param>
        /// <param name="resolver">Resolves slot type names.</param>
        /// <param name="typeRegistry">Types a slot value's <c>$type</c> may name. Types allowed
        /// by <see cref="Validator"/> are also accepted.</param>
        public JsonContinuationSerializer(
            JsonSerializerSettings settings,
            SlotTypeResolver resolver,
            ContinuationTypeRegistry typeRegistry)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (typeRegistry == null) throw new ArgumentNullException(nameof(typeRegistry));
            _codec = new SlotCodec(resolver);

            // Bound parser recursion for caller-supplied settings that leave
            // MaxDepth unset, so a deep Caller chain throws a catchable
            // JsonReaderException instead of overflowing the stack (issue #42). A
            // caller that deliberately set MaxDepth keeps their value.
            if (_settings.MaxDepth == null)
            {
                _settings.MaxDepth = FrameDepthGuard.MaxParserDepth;
            }

            // Slot values carry a $type, which Json.NET instantiates during
            // deserialize, before Validator runs. Without a binder that is a gadget
            // construction primitive, so restrict $type to allowed types. A caller
            // that supplies their own binder keeps it and owns that decision.
            if (_settings.SerializationBinder == null)
            {
                _settings.SerializationBinder = new SlotSerializationBinder(resolver, typeRegistry, () => Validator);
            }
        }

        private static JsonSerializerSettings DefaultSettings(Formatting formatting, NullValueHandling nullValueHandling)
        {
            return new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.None,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
                Formatting = formatting,
                NullValueHandling = nullValueHandling,
                // Bound parser recursion so a deep Caller chain throws a catchable
                // JsonReaderException instead of overflowing the stack (issue #42).
                MaxDepth = FrameDepthGuard.MaxParserDepth
            };
        }

        /// <summary>
        /// Creates a compact serializer (no indentation).
        /// </summary>
        public static JsonContinuationSerializer Compact()
        {
            return new JsonContinuationSerializer(
                DefaultSettings(Formatting.None, NullValueHandling.Ignore),
                new SlotTypeResolver(),
                ContinuationTypeRegistry.Default);
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
            return DeserializeFromString(json);
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

            // Reject a pathologically deep Caller chain with a clean, catchable
            // exception BEFORE the typed deserialize recursively materializes the
            // whole graph and overflows the native stack (issue #42).
            //
            // Setting JsonSerializerSettings.MaxDepth alone is NOT sufficient:
            // Newtonsoft's typed object construction recurses once per nesting
            // level, so a deep document can overflow the stack before the reader's
            // MaxDepth check ever fires (the threshold depends on the available
            // thread stack and is well below MaxParserDepth). A linear token scan
            // with a JsonTextReader enforces the depth bound without building any
            // object graph and therefore cannot recurse or overflow. We pre-scan
            // here; the subsequent typed deserialize then runs only on input whose
            // depth is already bounded.
            ValidateJsonDepth(json);

            var dto = JsonConvert.DeserializeObject<JsonContinuationStateDto>(json, _settings);
            var state = ConvertFromDto(dto);
            Validator?.Validate(state);
            return state;
        }

        private void ValidateJsonDepth(string json)
        {
            using var reader = new JsonTextReader(new System.IO.StringReader(json))
            {
                MaxDepth = _settings.MaxDepth ?? FrameDepthGuard.MaxParserDepth
            };

            try
            {
                while (reader.Read())
                {
                    // Linear scan only; the reader throws when MaxDepth is exceeded.
                }
            }
            catch (JsonReaderException ex) when (ex.Message.IndexOf("MaxDepth", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Surface the same exception type as the serialize-side
                // FrameDepthGuard for a consistent contract across both directions.
                throw new InvalidOperationException(
                    $"Continuation frame Caller chain exceeds the maximum allowed depth of {FrameDepthGuard.MaxFrameDepth}. " +
                    "The state may be corrupt or maliciously crafted.", ex);
            }
            catch (JsonReaderException)
            {
                // Other malformed-JSON errors are left for the typed deserialize to
                // surface with its natural diagnostics.
            }
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

            // Build the DTO chain iteratively (leaf -> root) so a maliciously deep
            // Caller chain throws a clean exception instead of overflowing the
            // stack before validation can run (issue #42).
            JsonHostFrameRecordDto head = null;
            JsonHostFrameRecordDto tail = null;
            var depth = 0;

            for (var current = frame; current != null; current = current.Caller)
            {
                FrameDepthGuard.Check(++depth);

                var node = new JsonHostFrameRecordDto
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

            // Decode the DTO chain iteratively (root -> leaf rebuild) with a depth
            // guard so a deep Caller chain cannot overflow the stack (issue #42).
            var dtos = new List<JsonHostFrameRecordDto>();
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
        public SlotEnvelope[] Slots { get; set; }
        public JsonHostFrameRecordDto Caller { get; set; }
    }
}
