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

        #region Additional Runtime Edge Cases

        [Fact]
        public void ScriptContext_MultipleRequestYields_OnlyCountsOnce()
        {
            var context = new ScriptContext();

            context.RequestYield();
            context.RequestYield();
            context.RequestYield();

            // YieldRequested is set to 1, multiple calls don't stack
            Assert.Equal(1, context.YieldRequested);
        }

        [Fact]
        public void ScriptContext_HandleYieldPointWithYieldValue()
        {
            var context = new ScriptContext();
            context.RequestYield();

            var ex = Assert.Throws<SuspendException>(() =>
                context.HandleYieldPoint(5, "yield data"));

            Assert.Equal(5, ex.YieldPointId);
            Assert.Equal("yield data", ex.YieldedValue);
        }

        [Fact]
        public void ScriptContext_EnsureCurrent_ReturnsSameInstance()
        {
            var context1 = ScriptContext.EnsureCurrent();
            var context2 = ScriptContext.EnsureCurrent();

            // Should return the same thread-local instance
            Assert.Same(context1, context2);
        }

        [Fact]
        public void ScriptContext_RunWith_RestoresOnException()
        {
            var original = ScriptContext.Current;
            var newContext = new ScriptContext();

            try
            {
                newContext.RunWith(() =>
                {
                    throw new InvalidOperationException("test");
                });
            }
            catch (InvalidOperationException)
            {
                // Expected
            }

            // Context should be restored even after exception
            Assert.Same(original, ScriptContext.Current);
        }

        [Fact]
        public void ScriptContext_NotRestoringByDefault()
        {
            var context = new ScriptContext();

            Assert.False(context.IsRestoring);
            Assert.Null(context.FrameChain);
            Assert.Null(context.ResumeValue);
        }

        [Fact]
        public void ScriptContext_RestoringWithNullResumeValue()
        {
            var frame = new HostFrameRecord(100, 0, new object[0], null);
            var state = new ContinuationState(frame);

            var context = new ScriptContext(state, null);

            Assert.True(context.IsRestoring);
            Assert.Null(context.ResumeValue);
        }

        [Fact]
        public void FrameCapture_PackSlots_EmptyArgs()
        {
            var slots = FrameCapture.PackSlots();

            Assert.NotNull(slots);
            Assert.Empty(slots);
        }

        [Fact]
        public void FrameCapture_PackSlots_SingleArg()
        {
            var slots = FrameCapture.PackSlots(42);

            Assert.Single(slots);
            Assert.Equal(42, slots[0]);
        }

        [Fact]
        public void FrameCapture_GetSlot_NullableType()
        {
            var slots = new object[] { null, 42 };

            var nullable1 = FrameCapture.GetSlot<int?>(slots, 0);
            var nullable2 = FrameCapture.GetSlot<int?>(slots, 1);

            Assert.Null(nullable1);
            Assert.Equal(42, nullable2);
        }

        [Fact]
        public void FrameCapture_CaptureFrame_WithNestedCallers()
        {
            var grandparent = new HostFrameRecord(100, 0, new object[] { 1 }, null);
            var parent = new HostFrameRecord(200, 1, new object[] { 2 }, grandparent);
            var child = FrameCapture.CaptureFrame(300, 2, new object[] { 3 }, parent);

            Assert.Equal(3, child.GetStackDepth());
            Assert.Same(parent, child.Caller);
            Assert.Same(grandparent, child.Caller.Caller);
        }

        [Fact]
        public void FrameCapture_GenerateMethodToken_WithNoParams()
        {
            var token1 = FrameCapture.GenerateMethodToken("MyClass", "MyMethod");
            var token2 = FrameCapture.GenerateMethodToken("MyClass", "MyMethod");

            Assert.Equal(token1, token2);
        }

        [Fact]
        public void FrameCapture_GenerateMethodToken_DifferentClassesDifferentTokens()
        {
            var token1 = FrameCapture.GenerateMethodToken("ClassA", "Method");
            var token2 = FrameCapture.GenerateMethodToken("ClassB", "Method");

            Assert.NotEqual(token1, token2);
        }

        [Fact]
        public void ContinuationRunner_Run_WithVoidDelegate()
        {
            var runner = new ContinuationRunner();
            var executed = false;

            var result = runner.Run(() =>
            {
                executed = true;
                return 0; // Need to return something
            });

            Assert.True(executed);
            Assert.True(result.IsCompleted);
        }

        [Fact]
        public void ContinuationRunner_Run_WithReferenceTypeResult()
        {
            var runner = new ContinuationRunner();
            var testObject = new { Name = "Test", Value = 42 };

            var result = runner.Run<object>(() => testObject);

            Assert.True(result.IsCompleted);
            var completed = (ContinuationResult<object>.Completed)result;
            Assert.Same(testObject, completed.Value);
        }

        [Fact]
        public void ContinuationRunner_Run_SuspendedResultContainsYieldValue()
        {
            var runner = new ContinuationRunner();

            var result = runner.Run<int>(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                context.RequestYield();
                context.HandleYieldPoint(0, "my yield value");
                return 42;
            });

            Assert.True(result.IsSuspended);
            var suspended = (ContinuationResult<int>.Suspended)result;
            Assert.Equal("my yield value", suspended.YieldedValue);
        }

        #endregion

        #region Instruction Counting Tests

        [Fact]
        public void ScriptContext_DefaultBudget_IsSet()
        {
            var context = new ScriptContext();

            Assert.Equal(ScriptContext.DefaultBudget, context.InstructionBudget);
        }

        [Fact]
        public void ScriptContext_ResetBudget_SetsNewValue()
        {
            var context = new ScriptContext();

            context.ResetBudget(500);

            Assert.Equal(500, context.InstructionBudget);
        }

        [Fact]
        public void ScriptContext_ResetBudget_DefaultValue()
        {
            var context = new ScriptContext();
            context.InstructionBudget = 0;

            context.ResetBudget();

            Assert.Equal(ScriptContext.DefaultBudget, context.InstructionBudget);
        }

        [Fact]
        public void ScriptContext_HandleYieldPointWithBudget_DecrementsBudget()
        {
            var context = new ScriptContext();
            context.ResetBudget(100);

            context.HandleYieldPointWithBudget(0, 10);

            Assert.Equal(90, context.InstructionBudget);
        }

        [Fact]
        public void ScriptContext_HandleYieldPointWithBudget_ThrowsWhenBudgetExhausted()
        {
            var context = new ScriptContext();
            context.ResetBudget(5);

            // First call succeeds (5 - 3 = 2 > 0)
            context.HandleYieldPointWithBudget(0, 3);
            Assert.Equal(2, context.InstructionBudget);

            // Second call exhausts budget (2 - 5 = -3 <= 0) and throws
            var ex = Assert.Throws<SuspendException>(() =>
                context.HandleYieldPointWithBudget(1, 5));

            Assert.Equal(1, ex.YieldPointId);
        }

        [Fact]
        public void ScriptContext_HandleYieldPointWithBudget_ThrowsOnExactZero()
        {
            var context = new ScriptContext();
            context.ResetBudget(10);

            // Cost equals budget exactly - should throw
            var ex = Assert.Throws<SuspendException>(() =>
                context.HandleYieldPointWithBudget(0, 10));

            Assert.Equal(0, ex.YieldPointId);
            Assert.Equal(0, context.InstructionBudget);
        }

        [Fact]
        public void ScriptContext_HandleYieldPointWithBudget_ThrowsWhenYieldRequested()
        {
            var context = new ScriptContext();
            context.ResetBudget(1000);
            context.RequestYield();

            // Should throw even with plenty of budget because yield was requested
            var ex = Assert.Throws<SuspendException>(() =>
                context.HandleYieldPointWithBudget(5, 1));

            Assert.Equal(5, ex.YieldPointId);
            Assert.Equal(999, context.InstructionBudget); // Budget still decremented
        }

        [Fact]
        public void ScriptContext_HandleYieldPointWithBudget_WithValue_ThrowsWhenExhausted()
        {
            var context = new ScriptContext();
            context.ResetBudget(5);

            var ex = Assert.Throws<SuspendException>(() =>
                context.HandleYieldPointWithBudget(2, 10, "my value"));

            Assert.Equal(2, ex.YieldPointId);
            Assert.Equal("my value", ex.YieldedValue);
        }

        [Fact]
        public void ScriptContext_HandleYieldPointWithBudget_MultipleCalls()
        {
            var context = new ScriptContext();
            context.ResetBudget(100);

            // Simulate multiple yield points in a loop
            for (int i = 0; i < 9; i++)
            {
                context.HandleYieldPointWithBudget(0, 10);
            }

            Assert.Equal(10, context.InstructionBudget);

            // 10th iteration exhausts budget
            Assert.Throws<SuspendException>(() =>
                context.HandleYieldPointWithBudget(0, 10));
        }

        [Fact]
        public void ScriptContext_RestorationConstructor_SetsBudget()
        {
            var frame = new HostFrameRecord(100, 0, new object[0], null);
            var state = new ContinuationState(frame);

            var context = new ScriptContext(state, null);

            Assert.Equal(ScriptContext.DefaultBudget, context.InstructionBudget);
        }

        #endregion
    }
}

