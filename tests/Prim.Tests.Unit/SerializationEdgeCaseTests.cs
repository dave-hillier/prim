using System;
using System.Text;
using Prim.Core;
using Prim.Serialization;
using Xunit;

namespace Prim.Tests.Unit
{
    /// <summary>
    /// TDD tests for serialization edge cases.
    /// Targets null handling, empty state, type registry behavior,
    /// and round-trip fidelity of edge-case data.
    /// </summary>
    public class SerializationEdgeCaseTests
    {
        #region JSON Serializer - Null/Empty Handling

        [Fact]
        public void JsonSerializer_Serialize_NullState_Throws()
        {
            var serializer = new JsonContinuationSerializer();

            Assert.Throws<ArgumentNullException>(() => serializer.Serialize(null));
        }

        [Fact]
        public void JsonSerializer_Deserialize_NullData_Throws()
        {
            var serializer = new JsonContinuationSerializer();

            Assert.Throws<ArgumentNullException>(() => serializer.Deserialize(null));
        }

        [Fact]
        public void JsonSerializer_SerializeToString_NullState_Throws()
        {
            var serializer = new JsonContinuationSerializer();

            Assert.Throws<ArgumentNullException>(() => serializer.SerializeToString(null));
        }

        [Fact]
        public void JsonSerializer_DeserializeFromString_NullJson_Throws()
        {
            var serializer = new JsonContinuationSerializer();

            Assert.Throws<ArgumentNullException>(() => serializer.DeserializeFromString(null));
        }

        [Fact]
        public void JsonSerializer_CustomSettings_NullThrows()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new JsonContinuationSerializer(null));
        }

        #endregion

        #region JSON Serializer - Edge Case Round Trips

        [Fact]
        public void JsonSerializer_RoundTrips_NullYieldedValue()
        {
            var serializer = new JsonContinuationSerializer();
            var frame = new HostFrameRecord(100, 0, new object[] { 42 }, null);
            var state = new ContinuationState(frame, null);

            var bytes = serializer.Serialize(state);
            var restored = serializer.Deserialize(bytes);

            Assert.Null(restored.YieldedValue);
            Assert.Equal(100, restored.StackHead.MethodToken);
        }

        [Fact]
        public void JsonSerializer_RoundTrips_EmptySlots()
        {
            var serializer = new JsonContinuationSerializer();
            var frame = new HostFrameRecord(100, 0, new object[0], null);
            var state = new ContinuationState(frame);

            var bytes = serializer.Serialize(state);
            var restored = serializer.Deserialize(bytes);

            Assert.NotNull(restored.StackHead.Slots);
            Assert.Empty(restored.StackHead.Slots);
        }

        [Fact]
        public void JsonSerializer_RoundTrips_NullSlots()
        {
            var serializer = new JsonContinuationSerializer();
            var frame = new HostFrameRecord(100, 0, null, null);
            var state = new ContinuationState(frame);

            var bytes = serializer.Serialize(state);
            var restored = serializer.Deserialize(bytes);

            Assert.Null(restored.StackHead.Slots);
        }

        [Fact]
        public void JsonSerializer_RoundTrips_NullStackHead()
        {
            var serializer = new JsonContinuationSerializer();
            var state = new ContinuationState(null);

            var bytes = serializer.Serialize(state);
            var restored = serializer.Deserialize(bytes);

            Assert.Null(restored.StackHead);
        }

        [Fact]
        public void JsonSerializer_RoundTrips_SlotsWithNullValues()
        {
            var serializer = new JsonContinuationSerializer();
            var frame = new HostFrameRecord(100, 0, new object[] { null, 42, null, "hello" }, null);
            var state = new ContinuationState(frame);

            var bytes = serializer.Serialize(state);
            var restored = serializer.Deserialize(bytes);

            Assert.Equal(4, restored.StackHead.Slots.Length);
            Assert.Null(restored.StackHead.Slots[0]);
            Assert.Null(restored.StackHead.Slots[2]);
        }

        [Fact]
        public void JsonSerializer_RoundTrips_DeepFrameChain()
        {
            var serializer = new JsonContinuationSerializer();

            HostFrameRecord current = null;
            for (int i = 0; i < 10; i++)
            {
                current = new HostFrameRecord(i, i, new object[] { i }, current);
            }
            var state = new ContinuationState(current);

            var bytes = serializer.Serialize(state);
            var restored = serializer.Deserialize(bytes);

            Assert.Equal(10, restored.GetStackDepth());

            // Verify chain integrity
            var frame = restored.StackHead;
            for (int i = 9; i >= 0; i--)
            {
                Assert.Equal(i, frame.MethodToken);
                Assert.Equal(i, frame.YieldPointId);
                frame = frame.Caller;
            }
            Assert.Null(frame);
        }

        [Fact]
        public void JsonSerializer_StringRoundTrip_Matches_ByteRoundTrip()
        {
            var serializer = new JsonContinuationSerializer();
            var frame = new HostFrameRecord(100, 5, new object[] { 42, "test" }, null);
            var state = new ContinuationState(frame, "yielded");

            var json = serializer.SerializeToString(state);
            var restoredFromString = serializer.DeserializeFromString(json);

            var bytes = serializer.Serialize(state);
            var restoredFromBytes = serializer.Deserialize(bytes);

            Assert.Equal(restoredFromString.Version, restoredFromBytes.Version);
            Assert.Equal(restoredFromString.StackHead.MethodToken, restoredFromBytes.StackHead.MethodToken);
        }

        [Fact]
        public void JsonSerializer_PreservesVersion()
        {
            var serializer = new JsonContinuationSerializer();
            var state = new ContinuationState(null) { Version = 42 };

            var bytes = serializer.Serialize(state);
            var restored = serializer.Deserialize(bytes);

            Assert.Equal(42, restored.Version);
        }

        #endregion

        #region MessagePack Serializer - Null Handling

        [Fact]
        public void MessagePackSerializer_Serialize_NullState_Throws()
        {
            var serializer = new MessagePackContinuationSerializer();

            Assert.Throws<ArgumentNullException>(() => serializer.Serialize(null));
        }

        [Fact]
        public void MessagePackSerializer_Deserialize_NullData_Throws()
        {
            var serializer = new MessagePackContinuationSerializer();

            Assert.Throws<ArgumentNullException>(() => serializer.Deserialize(null));
        }

        [Fact]
        public void MessagePackSerializer_CustomTypeRegistry_NullThrows()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new MessagePackContinuationSerializer((ContinuationTypeRegistry)null));
        }

        #endregion

        #region MessagePack Serializer - Edge Case Round Trips

        [Fact]
        public void MessagePackSerializer_RoundTrips_NullYieldedValue()
        {
            var serializer = new MessagePackContinuationSerializer();
            var frame = new HostFrameRecord(100, 0, new object[] { 42 }, null);
            var state = new ContinuationState(frame, null);

            var bytes = serializer.Serialize(state);
            var restored = serializer.Deserialize(bytes);

            Assert.Null(restored.YieldedValue);
        }

        [Fact]
        public void MessagePackSerializer_RoundTrips_EmptySlots()
        {
            var serializer = new MessagePackContinuationSerializer();
            var frame = new HostFrameRecord(100, 0, new object[0], null);
            var state = new ContinuationState(frame);

            var bytes = serializer.Serialize(state);
            var restored = serializer.Deserialize(bytes);

            Assert.NotNull(restored.StackHead);
        }

        [Fact]
        public void MessagePackSerializer_RoundTrips_NullStackHead()
        {
            var serializer = new MessagePackContinuationSerializer();
            var state = new ContinuationState(null);

            var bytes = serializer.Serialize(state);
            var restored = serializer.Deserialize(bytes);

            Assert.Null(restored.StackHead);
        }

        [Fact]
        public void MessagePackSerializer_PreservesVersion()
        {
            var serializer = new MessagePackContinuationSerializer();
            var frame = new HostFrameRecord(100, 0, new object[] { 1 }, null);
            var state = new ContinuationState(frame) { Version = 1 };

            var bytes = serializer.Serialize(state);
            var restored = serializer.Deserialize(bytes);

            Assert.Equal(1, restored.Version);
        }

        #endregion

        #region ContinuationTypeRegistry

        [Fact]
        public void TypeRegistry_NullIsAllowed()
        {
            var registry = ContinuationTypeRegistry.Default;

            Assert.True(registry.IsAllowed(null));
            Assert.True(registry.IsAllowedValue(null));
        }

        [Fact]
        public void TypeRegistry_PrimitivesAllowed()
        {
            var registry = ContinuationTypeRegistry.Default;

            Assert.True(registry.IsAllowed(typeof(int)));
            Assert.True(registry.IsAllowed(typeof(bool)));
            Assert.True(registry.IsAllowed(typeof(string)));
            Assert.True(registry.IsAllowed(typeof(double)));
            Assert.True(registry.IsAllowed(typeof(DateTime)));
            Assert.True(registry.IsAllowed(typeof(Guid)));
        }

        [Fact]
        public void TypeRegistry_EnumsAlwaysAllowed()
        {
            var registry = ContinuationTypeRegistry.Default;

            Assert.True(registry.IsAllowed(typeof(DayOfWeek)));
        }

        [Fact]
        public void TypeRegistry_ArraysOfAllowedTypes_Allowed()
        {
            var registry = ContinuationTypeRegistry.Default;

            Assert.True(registry.IsAllowed(typeof(int[])));
            Assert.True(registry.IsAllowed(typeof(string[])));
        }

        [Fact]
        public void TypeRegistry_NullableOfAllowedType_Allowed()
        {
            var registry = ContinuationTypeRegistry.Default;

            Assert.True(registry.IsAllowed(typeof(int?)));
            Assert.True(registry.IsAllowed(typeof(DateTime?)));
        }

        [Fact]
        public void TypeRegistry_CustomType_NotAllowed()
        {
            var registry = ContinuationTypeRegistry.Default;

            Assert.False(registry.IsAllowed(typeof(System.IO.MemoryStream)));
        }

        [Fact]
        public void TypeRegistry_IsAllowedValue_ChecksArrayElements()
        {
            var registry = ContinuationTypeRegistry.Default;

            Assert.True(registry.IsAllowedValue(new object[] { 1, "test", 3.14 }));
            Assert.False(registry.IsAllowedValue(new object[] { 1, new System.IO.MemoryStream() }));
        }

        [Fact]
        public void TypeRegistry_Constructor_NullThrows()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ContinuationTypeRegistry(null));
        }

        [Fact]
        public void TypeRegistry_CustomTypes_CanBeRegistered()
        {
            var registry = new ContinuationTypeRegistry(new[] { typeof(int), typeof(System.IO.MemoryStream) });

            Assert.True(registry.IsAllowed(typeof(int)));
            Assert.True(registry.IsAllowed(typeof(System.IO.MemoryStream)));
            Assert.False(registry.IsAllowed(typeof(string))); // Not registered
        }

        #endregion

        #region Continuation<T> Serialization

        [Fact]
        public void Continuation_Serialize_WithoutSerializer_Throws()
        {
            var state = new ContinuationState(null);
            var continuation = new Continuation<int>(state);

            Assert.Throws<InvalidOperationException>(() =>
                continuation.Serialize());
        }

        [Fact]
        public void Continuation_Serialize_WithExplicitSerializer_Works()
        {
            var serializer = new JsonContinuationSerializer();
            var frame = new HostFrameRecord(100, 0, new object[] { 42 }, null);
            var state = new ContinuationState(frame);
            var continuation = new Continuation<int>(state);

            var bytes = continuation.Serialize(serializer);

            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 0);
        }

        [Fact]
        public void Continuation_Serialize_NullSerializer_Throws()
        {
            var state = new ContinuationState(null);
            var continuation = new Continuation<int>(state);

            Assert.Throws<ArgumentNullException>(() =>
                continuation.Serialize(null));
        }

        [Fact]
        public void Continuation_Deserialize_NullData_Throws()
        {
            var serializer = new JsonContinuationSerializer();

            Assert.Throws<ArgumentNullException>(() =>
                Continuation<int>.Deserialize(null, serializer));
        }

        [Fact]
        public void Continuation_Deserialize_NullSerializer_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                Continuation<int>.Deserialize(new byte[0], null));
        }

        [Fact]
        public void Continuation_NullState_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new Continuation<int>(null));
        }

        [Fact]
        public void Continuation_RoundTrip_WithJsonSerializer()
        {
            var serializer = new JsonContinuationSerializer();
            var frame = new HostFrameRecord(100, 5, new object[] { 42, "hello" }, null);
            var state = new ContinuationState(frame, "yielded");
            var continuation = new Continuation<int>(state, serializer);

            var bytes = continuation.Serialize();
            var restored = Continuation<int>.Deserialize(bytes, serializer);

            Assert.Equal(state.Version, restored.State.Version);
            Assert.Equal(100, restored.State.StackHead.MethodToken);
        }

        #endregion
    }
}
