using System.Collections;
using Prim.Core;
using Xunit;

namespace Prim.Tests.Unit
{
    public class CoreTypesTests
    {
        [Fact]
        public void FrameSlot_ConstructsCorrectly()
        {
            var slot = new FrameSlot(0, "counter", SlotKind.Local, typeof(int), true);

            Assert.Equal(0, slot.Index);
            Assert.Equal("counter", slot.Name);
            Assert.Equal(SlotKind.Local, slot.Kind);
            Assert.Equal(typeof(int), slot.Type);
            Assert.True(slot.RequiresSerialization);
        }

        [Fact]
        public void HostFrameRecord_LinkedListWorks()
        {
            var innermost = new HostFrameRecord(100, 0, new object[] { 1, 2 }, null);
            var middle = new HostFrameRecord(200, 1, new object[] { 3 }, innermost);
            var outermost = new HostFrameRecord(300, 2, new object[] { 4, 5, 6 }, middle);

            Assert.Equal(3, outermost.GetStackDepth());
            Assert.Equal(2, middle.GetStackDepth());
            Assert.Equal(1, innermost.GetStackDepth());

            Assert.Same(middle, outermost.Caller);
            Assert.Same(innermost, middle.Caller);
            Assert.Null(innermost.Caller);
        }

        [Fact]
        public void ContinuationState_CapturesStackHead()
        {
            var frame = new HostFrameRecord(100, 0, new object[] { 42 }, null);
            var state = new ContinuationState(frame, "yielded");

            Assert.Same(frame, state.StackHead);
            Assert.Equal("yielded", state.YieldedValue);
            Assert.Equal(1, state.GetStackDepth());
            Assert.Equal(ContinuationState.CurrentVersion, state.Version);
        }

        [Fact]
        public void SuspendException_BuildsContinuationState()
        {
            var frame1 = new HostFrameRecord(100, 0, new object[] { 1 }, null);
            var frame2 = new HostFrameRecord(200, 1, new object[] { 2 }, frame1);

            var ex = new SuspendException(5, "yield value");
            ex.FrameChain = frame2;

            var state = ex.BuildContinuationState();

            Assert.Same(frame2, state.StackHead);
            Assert.Equal("yield value", state.YieldedValue);
            Assert.Equal(2, state.GetStackDepth());
        }

        [Fact]
        public void ContinuationResult_CompletedPattern()
        {
            var result = new ContinuationResult<int>.Completed(42);

            Assert.True(result.IsCompleted);
            Assert.False(result.IsSuspended);
            Assert.Equal(42, result.Match(
                onCompleted: c => c.Value,
                onSuspended: s => -1));
        }

        [Fact]
        public void ContinuationResult_SuspendedPattern()
        {
            var state = new ContinuationState();
            var result = new ContinuationResult<int>.Suspended("paused", state);

            Assert.False(result.IsCompleted);
            Assert.True(result.IsSuspended);
            Assert.Equal("paused", result.YieldedValue);
            Assert.Same(state, result.State);
        }

        [Fact]
        public void FrameDescriptor_GetLiveSlotsForYieldPoint()
        {
            var slots = new[]
            {
                new FrameSlot(0, "a", SlotKind.Local, typeof(int)),
                new FrameSlot(1, "b", SlotKind.Local, typeof(string)),
                new FrameSlot(2, "c", SlotKind.Local, typeof(bool))
            };

            var liveAtYield0 = new BitArray(new[] { true, false, true });
            var liveAtYield1 = new BitArray(new[] { true, true, false });

            var descriptor = new FrameDescriptor(
                methodToken: 123,
                methodName: "TestMethod",
                slots: slots,
                yieldPointIds: new[] { 0, 1 },
                liveSlotsAtYieldPoint: new[] { liveAtYield0, liveAtYield1 });

            Assert.Equal(2, descriptor.CountLiveSlots(0)); // a and c
            Assert.Equal(2, descriptor.CountLiveSlots(1)); // a and b
        }

        [Fact]
        public void SuspensionTag_TypedCorrectly()
        {
            var generatorTag = SuspensionTags.Generator<int>();
            Assert.Equal("generator", generatorTag.Name);

            var yieldTag = SuspensionTags.Yield;
            Assert.Equal("yield", yieldTag.Name);
        }

        #region Continuation<T> Tests

        [Fact]
        public void Continuation_ConstructorRequiresNonNullState()
        {
            Assert.Throws<ArgumentNullException>(() => new Continuation<int>(null));
        }

        [Fact]
        public void Continuation_ConstructorWithStateSucceeds()
        {
            var frame = new HostFrameRecord(100, 0, new object[] { 1 }, null);
            var state = new ContinuationState(frame, "test");

            var continuation = new Continuation<int>(state);

            Assert.Same(state, continuation.State);
            Assert.Null(continuation.Serializer);
        }

        [Fact]
        public void Continuation_ConstructorWithSerializerSucceeds()
        {
            var frame = new HostFrameRecord(100, 0, new object[] { 1 }, null);
            var state = new ContinuationState(frame, "test");
            var serializer = new TestSerializer();

            var continuation = new Continuation<int>(state, serializer);

            Assert.Same(state, continuation.State);
            Assert.Same(serializer, continuation.Serializer);
        }

        [Fact]
        public void Continuation_SerializeWithoutSerializerThrows()
        {
            var frame = new HostFrameRecord(100, 0, new object[] { 1 }, null);
            var state = new ContinuationState(frame);
            var continuation = new Continuation<int>(state);

            var ex = Assert.Throws<InvalidOperationException>(() => continuation.Serialize());
            Assert.Contains("No serializer configured", ex.Message);
        }

        [Fact]
        public void Continuation_SerializeWithSerializerSucceeds()
        {
            var frame = new HostFrameRecord(100, 0, new object[] { 1 }, null);
            var state = new ContinuationState(frame);
            var serializer = new TestSerializer();
            var continuation = new Continuation<int>(state, serializer);

            var bytes = continuation.Serialize();

            Assert.NotNull(bytes);
            Assert.True(serializer.SerializeCalled);
        }

        [Fact]
        public void Continuation_SerializeWithExplicitSerializerSucceeds()
        {
            var frame = new HostFrameRecord(100, 0, new object[] { 1 }, null);
            var state = new ContinuationState(frame);
            var continuation = new Continuation<int>(state);
            var serializer = new TestSerializer();

            var bytes = continuation.Serialize(serializer);

            Assert.NotNull(bytes);
            Assert.True(serializer.SerializeCalled);
        }

        [Fact]
        public void Continuation_SerializeWithNullExplicitSerializerThrows()
        {
            var frame = new HostFrameRecord(100, 0, new object[] { 1 }, null);
            var state = new ContinuationState(frame);
            var continuation = new Continuation<int>(state);

            Assert.Throws<ArgumentNullException>(() => continuation.Serialize(null));
        }

        [Fact]
        public void Continuation_DeserializeRequiresNonNullData()
        {
            var serializer = new TestSerializer();

            Assert.Throws<ArgumentNullException>(() =>
                Continuation<int>.Deserialize(null, serializer));
        }

        [Fact]
        public void Continuation_DeserializeRequiresNonNullSerializer()
        {
            Assert.Throws<ArgumentNullException>(() =>
                Continuation<int>.Deserialize(new byte[] { 1, 2, 3 }, null));
        }

        [Fact]
        public void Continuation_DeserializeSucceeds()
        {
            var serializer = new TestSerializer();
            var bytes = new byte[] { 1, 2, 3 };

            var continuation = Continuation<int>.Deserialize(bytes, serializer);

            Assert.NotNull(continuation);
            Assert.NotNull(continuation.State);
            Assert.Same(serializer, continuation.Serializer);
            Assert.True(serializer.DeserializeCalled);
        }

        [Fact]
        public void Continuation_ToStringIncludesTypeAndDepth()
        {
            var frame = new HostFrameRecord(100, 0, new object[] { 1 }, null);
            var state = new ContinuationState(frame);
            var continuation = new Continuation<int>(state);

            var str = continuation.ToString();

            Assert.Contains("Continuation<Int32>", str);
            Assert.Contains("Depth=1", str);
        }

        [Fact]
        public void Continuation_ToStringWithNestedFrames()
        {
            var inner = new HostFrameRecord(100, 0, new object[] { 1 }, null);
            var outer = new HostFrameRecord(200, 1, new object[] { 2 }, inner);
            var state = new ContinuationState(outer);
            var continuation = new Continuation<string>(state);

            var str = continuation.ToString();

            Assert.Contains("Continuation<String>", str);
            Assert.Contains("Depth=2", str);
        }

        /// <summary>
        /// Test serializer for Continuation tests.
        /// </summary>
        private class TestSerializer : IContinuationSerializer
        {
            public bool SerializeCalled { get; private set; }
            public bool DeserializeCalled { get; private set; }

            public byte[] Serialize(ContinuationState state)
            {
                SerializeCalled = true;
                return new byte[] { 1, 2, 3 };
            }

            public ContinuationState Deserialize(byte[] data)
            {
                DeserializeCalled = true;
                var frame = new HostFrameRecord(100, 0, new object[0], null);
                return new ContinuationState(frame);
            }
        }

        #endregion

        #region Additional Edge Case Tests

        [Fact]
        public void HostFrameRecord_WithNullSlots()
        {
            var frame = new HostFrameRecord(100, 0, null, null);
            Assert.Null(frame.Slots);
        }

        [Fact]
        public void HostFrameRecord_DeepNestedChain()
        {
            HostFrameRecord current = null;
            for (int i = 0; i < 100; i++)
            {
                current = new HostFrameRecord(i, i, new object[] { i }, current);
            }

            Assert.Equal(100, current.GetStackDepth());
        }

        [Fact]
        public void ContinuationState_WithNullStackHead()
        {
            var state = new ContinuationState();
            Assert.Null(state.StackHead);
            Assert.Equal(0, state.GetStackDepth());
        }

        [Fact]
        public void ContinuationState_VersionIsSet()
        {
            var state = new ContinuationState();
            Assert.Equal(ContinuationState.CurrentVersion, state.Version);
        }

        [Fact]
        public void SuspendException_WithYieldValue()
        {
            var ex = new SuspendException(5, "test value");

            Assert.Equal(5, ex.YieldPointId);
            Assert.Equal("test value", ex.YieldedValue);
        }

        [Fact]
        public void SuspendException_IsException()
        {
            var ex = new SuspendException(0);

            Assert.IsAssignableFrom<Exception>(ex);
        }

        [Fact]
        public void FrameSlot_DefaultRequiresSerialization()
        {
            var slot = new FrameSlot(0, "test", SlotKind.Local, typeof(int));
            // Default should be false when not specified
            Assert.False(slot.RequiresSerialization);
        }

        [Fact]
        public void FrameDescriptor_WithNoYieldPoints()
        {
            var slots = new FrameSlot[0];
            var descriptor = new FrameDescriptor(
                methodToken: 123,
                methodName: "TestMethod",
                slots: slots,
                yieldPointIds: new int[0],
                liveSlotsAtYieldPoint: new BitArray[0]);

            Assert.Equal(0, descriptor.YieldPointIds.Length);
        }

        [Fact]
        public void SlotKind_ValuesAreDistinct()
        {
            Assert.NotEqual(SlotKind.Local, SlotKind.Argument);
            Assert.NotEqual(SlotKind.Argument, SlotKind.EvalStack);
            Assert.NotEqual(SlotKind.Local, SlotKind.EvalStack);
        }

        #endregion
    }
}
