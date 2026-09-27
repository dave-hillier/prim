using System;
using Prim.Core;
using Prim.Serialization;
using Xunit;

namespace Prim.Tests.Unit
{
    public class SerializationTests
    {
        [Fact]
        public void MessagePackSerializer_RoundTripsSimpleState()
        {
            var serializer = new MessagePackContinuationSerializer();

            var frame = new HostFrameRecord(100, 5, new object[] { 42, "hello", 3.14 }, null);
            var state = new ContinuationState(frame, "yielded");

            var bytes = serializer.Serialize(state);
            var restored = serializer.Deserialize(bytes);

            Assert.Equal(state.Version, restored.Version);
            Assert.Equal(state.YieldedValue, restored.YieldedValue);
            Assert.NotNull(restored.StackHead);
            Assert.Equal(frame.MethodToken, restored.StackHead.MethodToken);
            Assert.Equal(frame.YieldPointId, restored.StackHead.YieldPointId);
            Assert.Equal(frame.Slots.Length, restored.StackHead.Slots.Length);
        }

        [Fact]
        public void MessagePackSerializer_RoundTripsNestedFrames()
        {
            var serializer = new MessagePackContinuationSerializer();

            var inner = new HostFrameRecord(100, 0, new object[] { 1 }, null);
            var outer = new HostFrameRecord(200, 1, new object[] { 2, 3 }, inner);
            var state = new ContinuationState(outer);

            var bytes = serializer.Serialize(state);
            var restored = serializer.Deserialize(bytes);

            Assert.Equal(2, restored.GetStackDepth());
            Assert.Equal(200, restored.StackHead.MethodToken);
            Assert.NotNull(restored.StackHead.Caller);
            Assert.Equal(100, restored.StackHead.Caller.MethodToken);
        }

        [Fact]
        public void JsonSerializer_RoundTripsSimpleState()
        {
            var serializer = new JsonContinuationSerializer();

            var frame = new HostFrameRecord(100, 5, new object[] { 42, "hello", 3.14 }, null);
            var state = new ContinuationState(frame, "yielded");

            var bytes = serializer.Serialize(state);
            var restored = serializer.Deserialize(bytes);

            Assert.Equal(state.Version, restored.Version);
            Assert.Equal(state.YieldedValue, restored.YieldedValue);
            Assert.NotNull(restored.StackHead);
            Assert.Equal(frame.MethodToken, restored.StackHead.MethodToken);
        }

        [Fact]
        public void JsonSerializer_ProducesReadableOutput()
        {
            var serializer = new JsonContinuationSerializer();

            var frame = new HostFrameRecord(100, 5, new object[] { 42 }, null);
            var state = new ContinuationState(frame, "test");

            var json = serializer.SerializeToString(state);

            Assert.Contains("MethodToken", json);
            Assert.Contains("100", json);
            Assert.Contains("YieldPointId", json);
        }

        [Fact]
        public void JsonSerializer_CompactModeProducesSmallerOutput()
        {
            var normalSerializer = new JsonContinuationSerializer();
            var compactSerializer = JsonContinuationSerializer.Compact();

            var frame = new HostFrameRecord(100, 5, new object[] { 42, "hello" }, null);
            var state = new ContinuationState(frame);

            var normalBytes = normalSerializer.Serialize(state);
            var compactBytes = compactSerializer.Serialize(state);

            Assert.True(compactBytes.Length < normalBytes.Length);
        }

        [Fact]
        public void SlotTypeResolver_ResolvesBuiltInTypes()
        {
            var resolver = new SlotTypeResolver();

            Assert.Equal(typeof(int), resolver.ResolveType("int"));
            Assert.Equal(typeof(string), resolver.ResolveType("string"));
            Assert.Equal(typeof(bool), resolver.ResolveType("bool"));
            Assert.Equal(typeof(double), resolver.ResolveType("System.Double"));
        }

        [Fact]
        public void SlotTypeResolver_GetsShortNames()
        {
            var resolver = new SlotTypeResolver();

            Assert.Equal("int", resolver.GetTypeName(typeof(int)));
            Assert.Equal("string", resolver.GetTypeName(typeof(string)));
            Assert.Equal("bool", resolver.GetTypeName(typeof(bool)));
        }

        // ---------------------------------------------------------------------
        // #40: JSON and MessagePack must produce slot types that are identical
        // CLR types for the same input frame. Previously the JSON path widened
        // int->long and double-only floats and lost Guid/DateTime/decimal, while
        // MessagePack kept exact types, so the two serializers were not
        // interchangeable and FrameCapture.GetSlot<int> threw on JSON-revived
        // state.
        // ---------------------------------------------------------------------

        public static object[] MixedTypeSlots()
        {
            return new object[]
            {
                42,                                                   // int
                9_000_000_000L,                                       // long
                3.14,                                                 // double
                2.5f,                                                 // float
                123.456m,                                             // decimal
                Guid.Parse("11111111-2222-3333-4444-555555555555"),   // Guid
                new DateTime(2026, 5, 28, 13, 30, 0, DateTimeKind.Utc), // DateTime
                "hello",                                              // string
                true,                                                 // bool
                null                                                  // null slot
            };
        }

        [Fact]
        public void JsonSerializer_PreservesExactSlotClrTypes()
        {
            AssertSlotTypesRoundTrip(new JsonContinuationSerializer());
        }

        [Fact]
        public void MessagePackSerializer_PreservesExactSlotClrTypes()
        {
            AssertSlotTypesRoundTrip(new MessagePackContinuationSerializer());
        }

        [Fact]
        public void JsonAndMessagePack_ProduceIdenticalSlotClrTypes()
        {
            var original = MixedTypeSlots();
            var frame = new HostFrameRecord(100, 0, original, null);
            var state = new ContinuationState(frame);

            var jsonRestored = RoundTrip(new JsonContinuationSerializer(), state);
            var msgPackRestored = RoundTrip(new MessagePackContinuationSerializer(), state);

            var jsonSlots = jsonRestored.StackHead.Slots;
            var mpSlots = msgPackRestored.StackHead.Slots;

            Assert.Equal(original.Length, jsonSlots.Length);
            Assert.Equal(original.Length, mpSlots.Length);

            for (var i = 0; i < original.Length; i++)
            {
                if (original[i] == null)
                {
                    Assert.Null(jsonSlots[i]);
                    Assert.Null(mpSlots[i]);
                    continue;
                }

                Assert.Equal(original[i].GetType(), jsonSlots[i].GetType());
                Assert.Equal(original[i].GetType(), mpSlots[i].GetType());
                Assert.Equal(jsonSlots[i].GetType(), mpSlots[i].GetType());
                Assert.Equal(original[i], jsonSlots[i]);
                Assert.Equal(original[i], mpSlots[i]);
            }
        }

        private static void AssertSlotTypesRoundTrip(IContinuationSerializer serializer)
        {
            var original = MixedTypeSlots();
            var frame = new HostFrameRecord(100, 0, original, null);
            var state = new ContinuationState(frame);

            var restored = RoundTrip(serializer, state);
            var slots = restored.StackHead.Slots;

            Assert.Equal(original.Length, slots.Length);
            for (var i = 0; i < original.Length; i++)
            {
                if (original[i] == null)
                {
                    Assert.Null(slots[i]);
                    continue;
                }

                Assert.Equal(original[i].GetType(), slots[i].GetType());
                Assert.Equal(original[i], slots[i]);
            }
        }

        private static ContinuationState RoundTrip(IContinuationSerializer serializer, ContinuationState state)
        {
            var bytes = serializer.Serialize(state);
            return serializer.Deserialize(bytes);
        }

        // ---------------------------------------------------------------------
        // #39: a single user-defined reference instance shared across two slots
        // must remain a single shared instance after a round-trip.
        // ---------------------------------------------------------------------

        public sealed class SharedPayload
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        [Fact]
        public void JsonSerializer_PreservesSharedReferenceIdentityAcrossSlots()
        {
            var shared = new SharedPayload { Id = 7, Name = "shared" };
            var frame = new HostFrameRecord(100, 0, new object[] { shared, shared }, null);
            var state = new ContinuationState(frame);

            var resolver = new SlotTypeResolver();
            resolver.AddResolver(name => name.StartsWith(typeof(SharedPayload).FullName) ? typeof(SharedPayload) : null);
            var serializer = new JsonContinuationSerializer(
                resolver, ContinuationTypeRegistry.Default.With(typeof(SharedPayload)));

            var restored = RoundTrip(serializer, state);
            var slots = restored.StackHead.Slots;

            Assert.IsType<SharedPayload>(slots[0]);
            Assert.IsType<SharedPayload>(slots[1]);
            Assert.Same(slots[0], slots[1]);
            Assert.Equal(7, ((SharedPayload)slots[0]).Id);
        }

        // NOTE (#39 / future issue): MessagePack with the ContractlessStandardResolver
        // does NOT preserve cross-slot reference identity — each occurrence of the
        // shared instance is serialized independently and revived as a distinct
        // object. The JSON path uses Newtonsoft PreserveReferencesHandling.Objects
        // to retain identity; the MessagePack path would require the (unsafe)
        // TypelessContractlessStandardResolver plus a reference-preserving formatter
        // to match. This is intentionally not faked here.
        [Fact(Skip = "MessagePack ContractlessStandardResolver does not preserve cross-slot reference identity (#39 follow-up). JSON path covers the guarantee.")]
        public void MessagePackSerializer_PreservesSharedReferenceIdentityAcrossSlots()
        {
            var shared = new SharedPayload { Id = 7, Name = "shared" };
            var frame = new HostFrameRecord(100, 0, new object[] { shared, shared }, null);
            var state = new ContinuationState(frame);

            var restored = RoundTrip(new MessagePackContinuationSerializer(), state);
            var slots = restored.StackHead.Slots;

            Assert.Same(slots[0], slots[1]);
        }

        // ---------------------------------------------------------------------
        // #42: a Caller chain deeper than the bound must throw a clean exception
        // (not StackOverflow) during serialization.
        // ---------------------------------------------------------------------

        [Fact]
        public void JsonSerializer_DeepCallerChain_ThrowsCleanlyInsteadOfStackOverflow()
        {
            var state = new ContinuationState(BuildDeepChain(5000));
            var serializer = new JsonContinuationSerializer();

            Assert.Throws<InvalidOperationException>(() => serializer.Serialize(state));
        }

        [Fact]
        public void MessagePackSerializer_DeepCallerChain_ThrowsCleanlyInsteadOfStackOverflow()
        {
            var state = new ContinuationState(BuildDeepChain(5000));
            var serializer = new MessagePackContinuationSerializer();

            Assert.Throws<InvalidOperationException>(() => serializer.Serialize(state));
        }

        private static HostFrameRecord BuildDeepChain(int depth)
        {
            HostFrameRecord head = null;
            for (var i = 0; i < depth; i++)
            {
                head = new HostFrameRecord(100 + i, 0, new object[] { i }, head);
            }
            return head;
        }

        // ---------------------------------------------------------------------
        // #42 (DESERIALIZE side): a payload representing a Caller chain deeper
        // than the parser bound must throw a clean, catchable exception during
        // Deserialize — NOT crash the process with a stack overflow. The parser's
        // depth limit (JSON MaxDepth / MessagePack MaximumObjectGraphDepth) is the
        // real fix; it rejects the document before recursively materializing the
        // whole chain. This was the primary attack vector: a ~50,000-deep JSON
        // document previously hard-crashed the host.
        // ---------------------------------------------------------------------

        [Fact]
        public void JsonSerializer_DeserializeDeepCallerChain_ThrowsCleanlyInsteadOfStackOverflow()
        {
            // Hand-build a JSON document with a Caller chain far deeper than the
            // parser bound, exercising the reader's MaxDepth directly.
            var json = BuildDeepJsonChain(50_000);
            var serializer = new JsonContinuationSerializer();

            Assert.Throws<InvalidOperationException>(() => serializer.DeserializeFromString(json));
        }

        [Fact]
        public void MessagePackSerializer_DeserializeDeepCallerChain_ThrowsCleanlyInsteadOfStackOverflow()
        {
            // Hand-build a MessagePack document: nested 2-element maps that the
            // deserializer must recurse into. With MaximumObjectGraphDepth bounded
            // the reader rejects it before deep native recursion can overflow.
            var bytes = BuildDeepMessagePackChain(50_000);
            var serializer = new MessagePackContinuationSerializer();

            // MessagePack surfaces the depth breach as a MessagePackSerializationException
            // wrapping an InsufficientExecutionStackException (catchable, not a crash).
            Assert.ThrowsAny<Exception>(() => serializer.Deserialize(bytes));
        }

        private static string BuildDeepJsonChain(int depth)
        {
            // Each frame nests its Caller under the "Caller" property. Build the
            // innermost frame first, then wrap it repeatedly.
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"Version\":1,\"YieldedValue\":null,\"StackHead\":");
            for (var i = 0; i < depth; i++)
            {
                sb.Append("{\"MethodToken\":1,\"YieldPointId\":0,\"Slots\":[],\"Caller\":");
            }
            sb.Append("null");
            for (var i = 0; i < depth; i++)
            {
                sb.Append('}');
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static byte[] BuildDeepMessagePackChain(int depth)
        {
            // Build nested 2-element maps {0:0, 1:<nested>} to force the reader to
            // recurse; the leaf is nil. This mirrors a deep object graph regardless
            // of the actual DTO schema — the reader trips its depth limit while
            // descending.
            var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
            var writer = new MessagePack.MessagePackWriter(bufferWriter);
            for (var i = 0; i < depth; i++)
            {
                writer.WriteMapHeader(1);
                writer.Write(3); // Caller key on HostFrameRecordDto
            }
            writer.WriteNil();
            writer.Flush();
            return bufferWriter.WrittenSpan.ToArray();
        }

        // ---------------------------------------------------------------------
        // #40 (arrays): an int[] slot must round-trip to the exact element type
        // (Int32[]) on BOTH serializers. MessagePack's contractless resolver
        // previously revived it as object[] of System.Byte; SlotCodec now rebuilds
        // the correctly-typed array.
        // ---------------------------------------------------------------------

        [Fact]
        public void JsonSerializer_RoundTripsIntArraySlotWithExactElementType()
        {
            AssertIntArraySlotRoundTrips(new JsonContinuationSerializer());
        }

        [Fact]
        public void MessagePackSerializer_RoundTripsIntArraySlotWithExactElementType()
        {
            AssertIntArraySlotRoundTrips(new MessagePackContinuationSerializer());
        }

        private static void AssertIntArraySlotRoundTrips(IContinuationSerializer serializer)
        {
            var original = new int[] { 1, 2, 3 };
            var frame = new HostFrameRecord(100, 0, new object[] { original }, null);
            var state = new ContinuationState(frame);

            var restored = RoundTrip(serializer, state);
            var slot = restored.StackHead.Slots[0];

            Assert.IsType<int[]>(slot);
            Assert.Equal(original, (int[])slot);
        }

        // ---------------------------------------------------------------------
        // #40 / #41 (DEFERRED): MessagePack does NOT yet preserve custom
        // reference-type slot fidelity. With ContractlessStandardResolver and an
        // object-typed Value, a custom reference type revives as a
        // Dictionary<object,object> because the recorded TypeName is not used to
        // drive MessagePack revival. TypeName-driven MessagePack deserialization
        // belongs with the Batch 8 security resolver work (#41). JSON remains the
        // supported path for reference-type slots.
        // ---------------------------------------------------------------------

        // #41 (Batch 8): MessagePack now performs WHITELIST-DRIVEN typed revival.
        // With a validator that allows the custom type, the recorded TypeName drives
        // re-materialization of the contractless-revived Dictionary back into the
        // concrete type, so a whitelisted custom reference type round-trips as itself
        // (not a Dictionary). UN-SKIPPED now that revival is implemented.
        [Fact]
        public void MessagePackSerializer_PreservesCustomReferenceTypeSlotFidelity()
        {
            var payload = new SharedPayload { Id = 7, Name = "shared" };
            var frame = new HostFrameRecord(100, 0, new object[] { payload }, null);
            var state = new ContinuationState(frame);

            var resolver = new SlotTypeResolver();
            resolver.AddResolver(name =>
                name != null && name.StartsWith(typeof(SharedPayload).FullName) ? typeof(SharedPayload) : null);

            var serializer = new MessagePackContinuationSerializer(resolver);

            // The whitelist is what authorizes typed revival.
            var validator = new ContinuationValidator(ValidationOptions.Lenient);
            validator.RegisterAllowedType(typeof(SharedPayload));
            serializer.Validator = validator;

            var restored = RoundTrip(serializer, state);

            Assert.IsType<SharedPayload>(restored.StackHead.Slots[0]);
            Assert.Equal(7, ((SharedPayload)restored.StackHead.Slots[0]).Id);
            Assert.Equal("shared", ((SharedPayload)restored.StackHead.Slots[0]).Name);
        }

        // ---------------------------------------------------------------------
        // #43 / #41 (Batch 8): the serializers can run the validator as part of
        // deserialization. A state carrying a disallowed type is REJECTED instead of
        // being handed back, closing the "validation depends on the caller wiring
        // runner.Validator" gap.
        // ---------------------------------------------------------------------

        public sealed class ForbiddenPayload
        {
            public int Value { get; set; }
        }

        [Fact]
        public void JsonSerializer_WithValidator_RejectsDisallowedTypeOnDeserialize()
        {
            // Serialize WITHOUT a validator (producer side), then deserialize WITH a
            // validator that does not allow the payload type.
            var resolver = new SlotTypeResolver();
            resolver.AddResolver(name =>
                name != null && name.StartsWith(typeof(ForbiddenPayload).FullName) ? typeof(ForbiddenPayload) : null);

            var producer = new JsonContinuationSerializer(resolver);
            var frame = new HostFrameRecord(100, 0, new object[] { new ForbiddenPayload { Value = 1 } }, null);
            var bytes = producer.Serialize(new ContinuationState(frame));

            var consumerResolver = new SlotTypeResolver();
            consumerResolver.AddResolver(name =>
                name != null && name.StartsWith(typeof(ForbiddenPayload).FullName) ? typeof(ForbiddenPayload) : null);
            var consumer = new JsonContinuationSerializer(consumerResolver)
            {
                // Type checking on, but no descriptor required: the disallowed slot
                // type is what triggers the rejection.
                Validator = new ContinuationValidator(new ValidationOptions
                {
                    RequireRegisteredMethods = false,
                    ValidateSlotTypes = true,
                    ValidateSlotCounts = false
                })
            };

            Assert.Throws<ValidationException>(() => consumer.Deserialize(bytes));
        }

        [Fact]
        public void MessagePackSerializer_WithValidator_RejectsDisallowedTypeOnDeserialize()
        {
            var resolver = new SlotTypeResolver();
            resolver.AddResolver(name =>
                name != null && name.StartsWith(typeof(ForbiddenPayload).FullName) ? typeof(ForbiddenPayload) : null);

            var producer = new MessagePackContinuationSerializer(resolver);
            var frame = new HostFrameRecord(100, 0, new object[] { new ForbiddenPayload { Value = 1 } }, null);
            var bytes = producer.Serialize(new ContinuationState(frame));

            var consumerResolver = new SlotTypeResolver();
            consumerResolver.AddResolver(name =>
                name != null && name.StartsWith(typeof(ForbiddenPayload).FullName) ? typeof(ForbiddenPayload) : null);
            var consumer = new MessagePackContinuationSerializer(consumerResolver)
            {
                // ForbiddenPayload is NOT registered, so revival is refused and the
                // value remains a Dictionary, which the type-checking validator
                // rejects.
                Validator = new ContinuationValidator(new ValidationOptions
                {
                    RequireRegisteredMethods = false,
                    ValidateSlotTypes = true,
                    ValidateSlotCounts = false
                })
            };

            Assert.Throws<ValidationException>(() => consumer.Deserialize(bytes));
        }
    }
}
