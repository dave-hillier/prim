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
            // v1 added a (SlotTypeResolver) ctor, so a bare null is ambiguous. Cast to
            // the settings overload this test targets; both null overloads throw.
            Assert.Throws<ArgumentNullException>(() =>
                new JsonContinuationSerializer((Newtonsoft.Json.JsonSerializerSettings)null!));
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
            var frame = new HostFrameRecord(100, 0, new object?[] { null, 42, null, "hello" }, null);
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

            HostFrameRecord? current = null;
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

        // Json.NET used to parse the DateTimeOffset's ISO string into a DateTime
        // before SlotCodec.Coerce saw it, so the slot came back as a DateTime and
        // the offset was lost. DateTimeOffset.Equals ignores the offset, so check
        // Offset explicitly.
        [Fact]
        public void JsonSerializer_RoundTrips_DateTimeOffsetSlot_KeepsOffset()
        {
            var serializer = new JsonContinuationSerializer();
            var original = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(1));
            var state = new ContinuationState(new HostFrameRecord(100, 0, new object[] { original }, null));

            var restored = serializer.Deserialize(serializer.Serialize(state));

            var value = Assert.IsType<DateTimeOffset>(restored.StackHead.Slots[0]);
            Assert.Equal(original.Offset, value.Offset);
            Assert.Equal(original.UtcDateTime, value.UtcDateTime);
        }

        [Theory]
        [InlineData("System.DateTimeOffset")]
        [InlineData("System.DateTimeOffset, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")]
        public void JsonSerializer_DateTimeOffsetSlotFromOtherRuntime_KeepsOffset(string typeName)
        {
            // State written by another runtime names DateTimeOffset with a different
            // assembly; the offset must still survive.
            var json = "{\"Version\":1,\"StackHead\":{\"MethodToken\":100,\"YieldPointId\":0,\"Slots\":[" +
                       "{\"TypeName\":\"" + typeName + "\",\"Value\":\"2026-09-27T12:00:00+01:00\"}]}}";

            var restored = new JsonContinuationSerializer().DeserializeFromString(json);

            var value = Assert.IsType<DateTimeOffset>(restored.StackHead.Slots[0]);
            Assert.Equal(TimeSpan.FromHours(1), value.Offset);
            Assert.Equal(new DateTime(2026, 9, 27, 11, 0, 0, DateTimeKind.Utc), value.UtcDateTime);
        }

        [Fact]
        public void JsonSerializer_RoundTrips_DateTimeOffsetArraySlot_KeepsOffsets()
        {
            var serializer = new JsonContinuationSerializer();
            var original = new[]
            {
                new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(1)),
                new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(-5.5))
            };
            var state = new ContinuationState(new HostFrameRecord(100, 0, new object[] { original }, null));

            var restored = serializer.Deserialize(serializer.Serialize(state));

            var values = Assert.IsType<DateTimeOffset[]>(restored.StackHead.Slots[0]);
            Assert.Equal(original.Length, values.Length);
            for (var i = 0; i < original.Length; i++)
            {
                Assert.Equal(original[i].Offset, values[i].Offset);
                Assert.Equal(original[i].UtcDateTime, values[i].UtcDateTime);
            }
        }

        [Theory]
        [InlineData(DateTimeKind.Utc)]
        [InlineData(DateTimeKind.Local)]
        [InlineData(DateTimeKind.Unspecified)]
        public void JsonSerializer_RoundTrips_DateTimeSlots_KeepKind(DateTimeKind kind)
        {
            var serializer = new JsonContinuationSerializer();
            var original = new DateTime(2026, 9, 27, 12, 0, 0, kind);
            var array = new[] { original, original.AddHours(1) };
            var state = new ContinuationState(
                new HostFrameRecord(100, 0, new object[] { original, array }, null), original);

            var restored = serializer.Deserialize(serializer.Serialize(state));

            var value = Assert.IsType<DateTime>(restored.StackHead.Slots[0]);
            Assert.Equal(original, value);
            Assert.Equal(kind, value.Kind);

            var values = Assert.IsType<DateTime[]>(restored.StackHead.Slots[1]);
            Assert.Equal(array, values);
            Assert.All(values, v => Assert.Equal(kind, v.Kind));

            var yielded = Assert.IsType<DateTime>(restored.YieldedValue);
            Assert.Equal(original, yielded);
            Assert.Equal(kind, yielded.Kind);
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
#pragma warning disable CS0618 // obsolete registry constructor still validates its argument
            Assert.Throws<ArgumentNullException>(() =>
                new MessagePackContinuationSerializer((ContinuationTypeRegistry)null!));
#pragma warning restore CS0618
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

        // The contractless resolver revives an object-typed DateTimeOffset as its
        // wire shape, object[] { clock time, offset minutes }. DateTimeOffset.Equals
        // ignores the offset, so check Offset explicitly.
        [Fact]
        public void MessagePackSerializer_RoundTrips_DateTimeOffsetSlots_KeepOffset()
        {
            var serializer = new MessagePackContinuationSerializer();
            var original = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(1));
            var array = new[] { original, new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(-5.5)) };
            var state = new ContinuationState(new HostFrameRecord(100, 0, new object[] { original, array }, null));

            var restored = serializer.Deserialize(serializer.Serialize(state));

            var value = Assert.IsType<DateTimeOffset>(restored.StackHead.Slots[0]);
            Assert.Equal(original.Offset, value.Offset);
            Assert.Equal(original.UtcDateTime, value.UtcDateTime);

            var values = Assert.IsType<DateTimeOffset[]>(restored.StackHead.Slots[1]);
            Assert.Equal(array.Length, values.Length);
            for (var i = 0; i < array.Length; i++)
            {
                Assert.Equal(array[i].Offset, values[i].Offset);
                Assert.Equal(array[i].UtcDateTime, values[i].UtcDateTime);
            }
        }

        #endregion

        #region ContinuationTypeRegistry (obsolete wrapper over ContinuationValidator)

#pragma warning disable CS0618

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
            var registry = new ContinuationTypeRegistry(new[] { typeof(System.IO.MemoryStream) });

            Assert.True(registry.IsAllowed(typeof(System.IO.MemoryStream)));
            // The wrapper always includes the validator's built-in types.
            Assert.True(registry.IsAllowed(typeof(string)));
            Assert.False(ContinuationTypeRegistry.Default.IsAllowed(typeof(System.IO.MemoryStream)));
        }

#pragma warning restore CS0618

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
