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
        public void ObjectGraphTracker_TracksIdentity()
        {
            var tracker = new ObjectGraphTracker();
            var obj = new object();

            Assert.True(tracker.TryRegister(obj, out var id1));
            Assert.False(tracker.TryRegister(obj, out var id2));
            Assert.Equal(id1, id2);
        }

        [Fact]
        public void ObjectGraphTracker_HandlesDifferentObjects()
        {
            var tracker = new ObjectGraphTracker();
            var obj1 = new object();
            var obj2 = new object();

            Assert.True(tracker.TryRegister(obj1, out var id1));
            Assert.True(tracker.TryRegister(obj2, out var id2));
            Assert.NotEqual(id1, id2);
        }

        [Fact]
        public void ObjectGraphTracker_RegistersAndRetrievesByReference()
        {
            var tracker = new ObjectGraphTracker();
            var original = new { Name = "Test" };

            tracker.TryRegister(original, out var id);
            tracker.RegisterDeserialized(id, original);
            var retrieved = tracker.GetById(id);

            Assert.Same(original, retrieved);
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
    }
}
