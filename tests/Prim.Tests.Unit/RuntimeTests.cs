using System;
using Prim.Core;
using Prim.Runtime;
using Xunit;

namespace Prim.Tests.Unit
{
    public class RuntimeTests
    {
        [Fact]
        public void ScriptContext_RequestYieldSetsFlag()
        {
            var context = new ScriptContext();
            Assert.Equal(0, context.YieldRequested);

            context.RequestYield();
            Assert.Equal(1, context.YieldRequested);
        }

        [Fact]
        public void ScriptContext_HandleYieldPointThrowsWhenFlagSet()
        {
            var context = new ScriptContext();
            context.RequestYield();

            var ex = Assert.Throws<SuspendException>(() =>
                context.HandleYieldPoint(42));

            Assert.Equal(42, ex.YieldPointId);
            Assert.Equal(0, context.YieldRequested); // Flag cleared
        }

        [Fact]
        public void ScriptContext_HandleYieldPointDoesNothingWhenFlagNotSet()
        {
            var context = new ScriptContext();

            // Should not throw
            context.HandleYieldPoint(42);
        }

        [Fact]
        public void ScriptContext_RunWithSetsAndRestoresCurrent()
        {
            var original = ScriptContext.Current;
            var newContext = new ScriptContext();

            ScriptContext insideContext = null;
            newContext.RunWith(() =>
            {
                insideContext = ScriptContext.Current;
            });

            Assert.Same(newContext, insideContext);
            Assert.Same(original, ScriptContext.Current);
        }

        [Fact]
        public void ScriptContext_ConfiguredForRestoration()
        {
            var frame = new HostFrameRecord(100, 5, new object[] { 1, 2 }, null);
            var state = new ContinuationState(frame);

            var context = new ScriptContext(state, "resumeValue");

            Assert.True(context.IsRestoring);
            Assert.Same(frame, context.FrameChain);
            Assert.Equal("resumeValue", context.ResumeValue);
        }

        [Fact]
        public void FrameCapture_PackSlots()
        {
            var slots = FrameCapture.PackSlots(1, "two", 3.0);

            Assert.Equal(3, slots.Length);
            Assert.Equal(1, slots[0]);
            Assert.Equal("two", slots[1]);
            Assert.Equal(3.0, slots[2]);
        }

        [Fact]
        public void FrameCapture_GetSlot()
        {
            var slots = new object[] { 42, "hello", 3.14 };

            Assert.Equal(42, FrameCapture.GetSlot<int>(slots, 0));
            Assert.Equal("hello", FrameCapture.GetSlot<string>(slots, 1));
            Assert.Equal(3.14, FrameCapture.GetSlot<double>(slots, 2));
        }

        [Fact]
        public void FrameCapture_GetSlotHandlesNull()
        {
            var slots = new object[] { null };

            // Reference type returns null
            Assert.Null(FrameCapture.GetSlot<string>(slots, 0));

            // Value type returns default
            Assert.Equal(0, FrameCapture.GetSlot<int>(slots, 0));
        }

        [Fact]
        public void FrameCapture_GenerateMethodToken_IsStable()
        {
            var token1 = FrameCapture.GenerateMethodToken("MyNamespace.MyClass", "MyMethod", "int", "string");
            var token2 = FrameCapture.GenerateMethodToken("MyNamespace.MyClass", "MyMethod", "int", "string");

            Assert.Equal(token1, token2);

            // Different params should give different tokens
            var token3 = FrameCapture.GenerateMethodToken("MyNamespace.MyClass", "MyMethod", "int", "int");
            Assert.NotEqual(token1, token3);
        }

        [Fact]
        public void ContinuationRunner_RunReturnsCompletedForNonSuspendingCode()
        {
            var runner = new ContinuationRunner();

            var result = runner.Run(() => 42);

            Assert.True(result.IsCompleted);
            Assert.Equal(42, ((ContinuationResult<int>.Completed)result).Value);
        }

        [Fact]
        public void ContinuationRunner_RunReturnsSuspendedWhenYieldThrown()
        {
            var runner = new ContinuationRunner();

            var result = runner.Run<int>(() =>
            {
                // Simulate what generated code does
                var context = ScriptContext.Current;
                context.RequestYield();
                context.HandleYieldPoint(0); // Will throw
                return 42;
            });

            Assert.True(result.IsSuspended);
            var suspended = (ContinuationResult<int>.Suspended)result;
            Assert.NotNull(suspended.State);
        }
    }
}
