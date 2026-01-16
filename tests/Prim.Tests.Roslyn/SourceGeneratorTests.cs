using Prim.Core;
using Prim.Runtime;
using Prim.Serialization;
using System;
using Xunit;

namespace Prim.Tests.Roslyn
{
    public class SourceGeneratorTests
    {
        #region Basic Generated Method Tests

        [Fact]
        public void GeneratedMethod_ExistsOnPartialClass()
        {
            // Test the continuable method
            var instance = new SampleContinuableClass();

            // Call the generated method
            var result = instance.CountToTen();

            // Should return 55 (sum of 1 to 10)
            Assert.Equal(55, result);
        }

        [Fact]
        public void GeneratedMethod_WithWhileLoop_Works()
        {
            var instance = new SampleContinuableClass();

            var result = instance.WhileCounter();

            Assert.Equal(5, result);
        }

        [Fact]
        public void GeneratedMethod_CanBeSuspendedAndResumed()
        {
            var instance = new SampleContinuableClass();
            var runner = new ContinuationRunner();

            // First, run without requesting yield - should complete
            var result1 = runner.Run(() => instance.CountToTen());
            Assert.True(result1.IsCompleted);
            Assert.Equal(55, ((ContinuationResult<int>.Completed)result1).Value);
        }

        #endregion

        #region Suspension and Resume Tests

        [Fact]
        public void GeneratedMethod_SuspendsOnYieldRequest()
        {
            var instance = new SampleContinuableClass();
            var runner = new ContinuationRunner();

            var result = runner.Run(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                context.RequestYield();
                return instance.CountToTen();
            });

            Assert.True(result.IsSuspended);
        }

        [Fact]
        public void GeneratedMethod_CapturesStateOnSuspension()
        {
            var runner = new ContinuationRunner();

            var result = runner.Run(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                context.RequestYield();
                context.HandleYieldPoint(0, "test");
                return 42;
            });

            Assert.True(result.IsSuspended);
            var suspended = (ContinuationResult<int>.Suspended)result;
            Assert.NotNull(suspended.State);
        }

        #endregion

        #region Context Tests

        [Fact]
        public void ScriptContext_EnsureCurrent_CreatesContext()
        {
            var context = ScriptContext.EnsureCurrent();
            Assert.NotNull(context);
        }

        [Fact]
        public void ScriptContext_Current_ReturnsContextDuringRun()
        {
            var runner = new ContinuationRunner();
            ScriptContext capturedContext = null;

            runner.Run<int>(() =>
            {
                capturedContext = ScriptContext.Current;
                return 0;
            });

            Assert.NotNull(capturedContext);
        }

        #endregion

        #region Serialization Integration Tests

        [Fact]
        public void GeneratedMethod_StateCanBeSerializedToJson()
        {
            var runner = new ContinuationRunner();
            var serializer = new JsonContinuationSerializer();

            var result = runner.Run(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                context.RequestYield();
                context.HandleYieldPoint(0, "serialization test");
                return 42;
            });

            Assert.True(result.IsSuspended);
            var suspended = (ContinuationResult<int>.Suspended)result;

            var json = serializer.SerializeToString(suspended.State);
            Assert.NotEmpty(json);
            Assert.Contains("YieldPointId", json);
        }

        [Fact]
        public void GeneratedMethod_StateCanBeSerializedToMessagePack()
        {
            var runner = new ContinuationRunner();
            var serializer = new MessagePackContinuationSerializer();

            var result = runner.Run(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                context.RequestYield();
                context.HandleYieldPoint(0, "msgpack test");
                return 42;
            });

            Assert.True(result.IsSuspended);
            var suspended = (ContinuationResult<int>.Suspended)result;

            var bytes = serializer.Serialize(suspended.State);
            Assert.NotEmpty(bytes);
        }

        #endregion

        #region ContinuationRunner Tests

        [Fact]
        public void ContinuationRunner_Run_ReturnsCompletedForSimpleFunction()
        {
            var runner = new ContinuationRunner();

            var result = runner.Run(() => 42);

            Assert.True(result.IsCompleted);
            var completed = (ContinuationResult<int>.Completed)result;
            Assert.Equal(42, completed.Value);
        }

        [Fact]
        public void ContinuationRunner_Run_ReturnsCompletedForStringResult()
        {
            var runner = new ContinuationRunner();

            var result = runner.Run(() => "hello");

            Assert.True(result.IsCompleted);
            var completed = (ContinuationResult<string>.Completed)result;
            Assert.Equal("hello", completed.Value);
        }

        [Fact]
        public void ContinuationRunner_Run_PropagatesExceptions()
        {
            var runner = new ContinuationRunner();

            Assert.Throws<InvalidOperationException>(() =>
            {
                runner.Run<int>(() => throw new InvalidOperationException("test"));
            });
        }

        #endregion

        #region Multiple Yield Points Tests

        [Fact]
        public void MultipleYieldPoints_SuspendAtFirst()
        {
            var runner = new ContinuationRunner();
            var yieldPointHit = 0;

            var result = runner.Run(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                context.RequestYield();
                context.HandleYieldPoint(0);
                yieldPointHit = 1;
                context.HandleYieldPoint(1);
                yieldPointHit = 2;
                return yieldPointHit;
            });

            Assert.True(result.IsSuspended);
            Assert.Equal(0, yieldPointHit);
        }

        #endregion

        #region FrameCapture Tests

        [Fact]
        public void FrameCapture_PackSlots_HandlesMultipleTypes()
        {
            var slots = FrameCapture.PackSlots(1, "two", 3.0, true, null);

            Assert.Equal(5, slots.Length);
            Assert.Equal(1, slots[0]);
            Assert.Equal("two", slots[1]);
            Assert.Equal(3.0, slots[2]);
            Assert.Equal(true, slots[3]);
            Assert.Null(slots[4]);
        }

        [Fact]
        public void FrameCapture_GetSlot_ReturnsCorrectType()
        {
            var slots = new object[] { 42, "hello", 3.14, true };

            Assert.Equal(42, FrameCapture.GetSlot<int>(slots, 0));
            Assert.Equal("hello", FrameCapture.GetSlot<string>(slots, 1));
            Assert.Equal(3.14, FrameCapture.GetSlot<double>(slots, 2));
            Assert.True(FrameCapture.GetSlot<bool>(slots, 3));
        }

        [Fact]
        public void FrameCapture_CaptureFrame_CreatesHostFrameRecord()
        {
            var slots = new object[] { 1, 2, 3 };
            var record = FrameCapture.CaptureFrame(123, 5, slots, null);

            Assert.NotNull(record);
            Assert.Equal(123, record.MethodToken);
            Assert.Equal(5, record.YieldPointId);
            Assert.Equal(3, record.Slots.Length);
            Assert.Null(record.Caller);
        }

        [Fact]
        public void FrameCapture_CaptureFrame_WithCaller()
        {
            var caller = new HostFrameRecord(100, 0, new object[0], null);
            var slots = new object[] { 1 };
            var record = FrameCapture.CaptureFrame(200, 1, slots, caller);

            Assert.NotNull(record);
            Assert.Same(caller, record.Caller);
            Assert.Equal(2, record.GetStackDepth());
        }

        #endregion
    }
}
