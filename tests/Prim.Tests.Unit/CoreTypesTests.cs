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
    }
}
