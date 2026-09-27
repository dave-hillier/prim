using System.Threading;
using MessagePack;
using MessagePack.Resolvers;
using Newtonsoft.Json;
using Prim.Core;
using Prim.Serialization;

namespace Prim.Tests.Unit
{
    /// <summary>
    /// A type that is NOT on any allow-list and whose constructor records a side
    /// effect. If deserialization ever constructs it, a gadget-chain type could be
    /// constructed the same way.
    /// </summary>
    public sealed class DeserializationGadgetProbe
    {
        private static int _constructed;

        public static int Constructed => Volatile.Read(ref _constructed);

        public DeserializationGadgetProbe()
        {
            Interlocked.Increment(ref _constructed);
        }

        public string? Payload { get; set; }
    }

    /// <summary>
    /// A user type that the caller explicitly allows via the validator whitelist.
    /// </summary>
    public sealed class AllowedSlotPayload
    {
        public int Number { get; set; }
        public string? Text { get; set; }
    }

    public class SerializationTypeBindingTests
    {
        private static string GadgetJson()
        {
            var probeType = typeof(DeserializationGadgetProbe);
            var typeName = $"{probeType.FullName}, {probeType.Assembly.GetName().Name}";
            return "{\"Version\":" + ContinuationState.CurrentVersion + "," +
                   "\"YieldedValue\":null," +
                   "\"StackHead\":{\"MethodToken\":1,\"YieldPointId\":0," +
                   "\"Slots\":[{\"TypeName\":null,\"Value\":{\"$type\":\"" + typeName + "\",\"Payload\":\"pwned\"}}]," +
                   "\"Caller\":null}}";
        }

        [Fact]
        public void Json_DisallowedTypeInSlot_IsNotConstructedDuringDeserialize()
        {
            var serializer = new JsonContinuationSerializer();
            var before = DeserializationGadgetProbe.Constructed;

            Assert.ThrowsAny<JsonSerializationException>(() => serializer.DeserializeFromString(GadgetJson()));

            Assert.Equal(before, DeserializationGadgetProbe.Constructed);
        }

        [Fact]
        public void JsonCompact_DisallowedTypeInSlot_IsNotConstructedDuringDeserialize()
        {
            var serializer = JsonContinuationSerializer.Compact();
            var before = DeserializationGadgetProbe.Constructed;

            Assert.ThrowsAny<JsonSerializationException>(() => serializer.DeserializeFromString(GadgetJson()));

            Assert.Equal(before, DeserializationGadgetProbe.Constructed);
        }

        [Fact]
        public void JsonCallerSettingsWithoutBinder_DisallowedTypeInSlot_IsNotConstructedDuringDeserialize()
        {
            var serializer = new JsonContinuationSerializer(new JsonSerializerSettings());
            var before = DeserializationGadgetProbe.Constructed;

            Assert.ThrowsAny<JsonSerializationException>(() => serializer.DeserializeFromString(GadgetJson()));

            Assert.Equal(before, DeserializationGadgetProbe.Constructed);
        }

        [Fact]
        public void Json_ValidatorWhitelistedUserType_RoundTrips()
        {
            var validator = new ContinuationValidator(ValidationOptions.Lenient);
            validator.RegisterAllowedType(typeof(AllowedSlotPayload));
            var serializer = new JsonContinuationSerializer { Validator = validator };

            var payload = new AllowedSlotPayload { Number = 7, Text = "ok" };
            var state = new ContinuationState(new HostFrameRecord(1, 0, new object[] { payload }, null));

            var restored = serializer.DeserializeFromString(serializer.SerializeToString(state));

            var slot = Assert.IsType<AllowedSlotPayload>(restored.StackHead.Slots[0]);
            Assert.Equal(7, slot.Number);
            Assert.Equal("ok", slot.Text);
        }

        [Fact]
        public void Json_RegistryAllowedUserType_RoundTrips()
        {
            var serializer = new JsonContinuationSerializer(
                ContinuationTypeRegistry.Default.With(typeof(AllowedSlotPayload)));

            var state = new ContinuationState(new HostFrameRecord(1, 0,
                new object[] { new AllowedSlotPayload { Number = 5 } }, null));

            var restored = serializer.DeserializeFromString(serializer.SerializeToString(state));

            Assert.Equal(5, Assert.IsType<AllowedSlotPayload>(restored.StackHead.Slots[0]).Number);
        }

        [Fact]
        public void Json_ResolverRegisteredUserType_RoundTrips()
        {
            var resolver = new SlotTypeResolver();
            resolver.AddResolver(name => name == typeof(AllowedSlotPayload).AssemblyQualifiedName
                ? typeof(AllowedSlotPayload)
                : null);
            var serializer = new JsonContinuationSerializer(resolver);

            var state = new ContinuationState(new HostFrameRecord(1, 0,
                new object[] { new AllowedSlotPayload { Number = 3 } }, null));

            var restored = serializer.DeserializeFromString(serializer.SerializeToString(state));

            Assert.Equal(3, Assert.IsType<AllowedSlotPayload>(restored.StackHead.Slots[0]).Number);
        }

        [Fact]
        public void Json_BuiltInArraySlot_StillRoundTrips()
        {
            var serializer = new JsonContinuationSerializer();
            var state = new ContinuationState(new HostFrameRecord(1, 0, new object[] { new[] { 1, 2, 3 } }, null));

            var restored = serializer.DeserializeFromString(serializer.SerializeToString(state));

            Assert.Equal(new[] { 1, 2, 3 }, Assert.IsType<int[]>(restored.StackHead.Slots[0]));
        }

        [Fact]
        public void Json_CallerSuppliedBinder_IsKept()
        {
            var binder = new Newtonsoft.Json.Serialization.DefaultSerializationBinder();
            var settings = new JsonSerializerSettings { SerializationBinder = binder };

            _ = new JsonContinuationSerializer(settings);

            Assert.Same(binder, settings.SerializationBinder);
        }

        [Fact]
        public void MessagePack_TypelessPayloadForDisallowedType_IsNotConstructedDuringDeserialize()
        {
            // Build a payload the attacker's way: the typeless resolver embeds the
            // CLR type name next to the slot value.
            var typelessOptions = MessagePackSerializerOptions.Standard
                .WithResolver(TypelessContractlessStandardResolver.Instance)
                .WithCompression(MessagePackCompression.Lz4BlockArray);
            var dto = new ContinuationStateDto
            {
                Version = ContinuationState.CurrentVersion,
                StackHead = new HostFrameRecordDto
                {
                    MethodToken = 1,
                    YieldPointId = 0,
                    Slots = new[]
                    {
                        new SlotEnvelope
                        {
                            TypeName = typeof(DeserializationGadgetProbe).AssemblyQualifiedName,
                            Value = new DeserializationGadgetProbe { Payload = "pwned" }
                        }
                    }
                }
            };
            var bytes = MessagePackSerializer.Serialize(dto, typelessOptions);

            var serializer = new MessagePackContinuationSerializer();
            var before = DeserializationGadgetProbe.Constructed;

            try
            {
                serializer.Deserialize(bytes);
            }
            catch (System.Exception)
            {
                // Rejection is fine; construction is not.
            }

            Assert.Equal(before, DeserializationGadgetProbe.Constructed);
        }
    }
}
