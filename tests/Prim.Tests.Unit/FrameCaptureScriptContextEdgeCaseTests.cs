using System;
using System.Threading;
using Prim.Core;
using Prim.Runtime;
using Xunit;

namespace Prim.Tests.Unit
{
    /// <summary>
    /// TDD tests for FrameCapture and ScriptContext edge cases.
    /// Targets GetSlot casting issues, null handling, budget edge cases,
    /// and thread-local context isolation.
    /// </summary>
    public class FrameCaptureScriptContextEdgeCaseTests
    {
        #region FrameCapture.GetSlot - Type Casting Edge Cases

        [Fact]
        public void GetSlot_NullSlotsArray_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(() =>
                FrameCapture.GetSlot<int>(null, 0));
        }

        [Fact]
        public void GetSlot_NegativeIndex_ThrowsArgumentOutOfRange()
        {
            var slots = new object[] { 42 };

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FrameCapture.GetSlot<int>(slots, -1));
        }

        [Fact]
        public void GetSlot_IndexEqualToLength_ThrowsArgumentOutOfRange()
        {
            var slots = new object[] { 42 };

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FrameCapture.GetSlot<int>(slots, 1));
        }

        [Fact]
        public void GetSlot_IndexBeyondLength_ThrowsArgumentOutOfRange()
        {
            var slots = new object[] { 42 };

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FrameCapture.GetSlot<int>(slots, 100));
        }

        [Fact]
        public void GetSlot_IncompatibleType_ThrowsInvalidCast()
        {
            // GetSlot does a direct (T)value cast.
            // If the value is incompatible, it throws InvalidCastException
            // (not a descriptive error like "expected int but got string").
            var slots = new object[] { "hello" };

            Assert.Throws<InvalidCastException>(() =>
                FrameCapture.GetSlot<int>(slots, 0));
        }

        [Fact]
        public void GetSlot_NullForValueType_ReturnsDefault()
        {
            var slots = new object[] { null };

            var result = FrameCapture.GetSlot<int>(slots, 0);

            Assert.Equal(0, result);
        }

        [Fact]
        public void GetSlot_NullForNullableValueType_ReturnsNull()
        {
            var slots = new object[] { null };

            var result = FrameCapture.GetSlot<int?>(slots, 0);

            Assert.Null(result);
        }

        [Fact]
        public void GetSlot_NullForReferenceType_ReturnsNull()
        {
            var slots = new object[] { null };

            var result = FrameCapture.GetSlot<string>(slots, 0);

            Assert.Null(result);
        }

        [Fact]
        public void GetSlot_BoxedValueType_UnboxesCorrectly()
        {
            var slots = new object[] { (object)42 };

            var result = FrameCapture.GetSlot<int>(slots, 0);

            Assert.Equal(42, result);
        }

        [Fact]
        public void GetSlot_EmptySlotsArray_ThrowsForAnyIndex()
        {
            var slots = new object[0];

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FrameCapture.GetSlot<int>(slots, 0));
        }

        #endregion

        #region FrameCapture.UnboxSlot

        [Fact]
        public void UnboxSlot_DelegatesToGetSlot()
        {
            var slots = new object[] { 42, "hello" };

            Assert.Equal(42, FrameCapture.UnboxSlot<int>(slots, 0));
            Assert.Equal("hello", FrameCapture.UnboxSlot<string>(slots, 1));
        }

        [Fact]
        public void UnboxSlot_NullSlotsArray_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(() =>
                FrameCapture.UnboxSlot<int>(null, 0));
        }

        #endregion

        #region FrameCapture.PackSlots - Null Handling

        [Fact]
        public void PackSlots_Null_ReturnsEmptyArray()
        {
            // PackSlots uses params, so null means the array itself is null
            var result = FrameCapture.PackSlots(null);

            // When passed a single null, params creates object[] { null }
            // but the code handles null params array: values ?? Array.Empty<object>()
            // Single null value is passed as params element, not as null array
            Assert.NotNull(result);
        }

        [Fact]
        public void PackSlots_MixedNullAndValues()
        {
            var slots = FrameCapture.PackSlots(null, 42, null, "hello");

            Assert.Equal(4, slots.Length);
            Assert.Null(slots[0]);
            Assert.Equal(42, slots[1]);
            Assert.Null(slots[2]);
            Assert.Equal("hello", slots[3]);
        }

        #endregion

        #region ScriptContext - Budget Edge Cases

        [Fact]
        public void HandleYieldPointWithBudget_NegativeCost_IncreasesBudget()
        {
            // Negative cost would increase the budget - is this intended?
            var context = new ScriptContext();
            context.ResetBudget(100);

            context.HandleYieldPointWithBudget(0, -50);

            // Budget increases from 100 to 150
            Assert.Equal(150, context.InstructionBudget);
        }

        [Fact]
        public void HandleYieldPointWithBudget_ZeroCost_NoChange()
        {
            var context = new ScriptContext();
            context.ResetBudget(100);

            context.HandleYieldPointWithBudget(0, 0);

            Assert.Equal(100, context.InstructionBudget);
        }

        [Fact]
        public void HandleYieldPointWithBudget_CostExactlyEqualsToBudget_Throws()
        {
            var context = new ScriptContext();
            context.ResetBudget(10);

            // After subtracting 10, budget = 0 which is <= 0, so should throw
            var ex = Assert.Throws<SuspendException>(() =>
                context.HandleYieldPointWithBudget(5, 10));

            Assert.Equal(5, ex.YieldPointId);
            Assert.Equal(0, context.InstructionBudget);
        }

        [Fact]
        public void HandleYieldPointWithBudget_LargeCost_GoesNegative()
        {
            var context = new ScriptContext();
            context.ResetBudget(5);

            var ex = Assert.Throws<SuspendException>(() =>
                context.HandleYieldPointWithBudget(0, 1000));

            // Budget should be negative after subtraction
            Assert.True(context.InstructionBudget < 0);
        }

        #endregion

        #region ScriptContext - ClearYieldRequest

        [Fact]
        public void ClearYieldRequest_ResetsFlag()
        {
            var context = new ScriptContext();
            context.RequestYield();
            Assert.Equal(1, context.YieldRequested);

            context.ClearYieldRequest();
            Assert.Equal(0, context.YieldRequested);
        }

        [Fact]
        public void ClearYieldRequest_WhenNotSet_IsNoOp()
        {
            var context = new ScriptContext();

            context.ClearYieldRequest();
            Assert.Equal(0, context.YieldRequested);
        }

        #endregion

        #region ScriptContext - Restoration Constructor Edge Cases

        [Fact]
        public void RestorationConstructor_NullState_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ScriptContext(null, "resumeValue"));
        }

        [Fact]
        public void RestorationConstructor_SetsAllFields()
        {
            var frame = new HostFrameRecord(100, 5, new object[] { 1, 2 }, null);
            var state = new ContinuationState(frame);

            var context = new ScriptContext(state, "resume");

            Assert.True(context.IsRestoring);
            Assert.Same(frame, context.FrameChain);
            Assert.Equal("resume", context.ResumeValue);
            Assert.Equal(ScriptContext.DefaultBudget, context.InstructionBudget);
        }

        [Fact]
        public void RestorationConstructor_NullStackHead_SetsNullFrameChain()
        {
            var state = new ContinuationState(null);

            var context = new ScriptContext(state, null);

            Assert.True(context.IsRestoring);
            Assert.Null(context.FrameChain);
        }

        #endregion

        #region ScriptContext - RunWith Edge Cases

        [Fact]
        public void RunWith_Func_ReturnValue_PropagatedCorrectly()
        {
            var context = new ScriptContext();

            var result = context.RunWith(() => 42);

            Assert.Equal(42, result);
        }

        [Fact]
        public void RunWith_Func_Exception_RestoresContext()
        {
            var original = ScriptContext.Current;
            var context = new ScriptContext();

            try
            {
                context.RunWith<int>(() => throw new InvalidOperationException());
            }
            catch (InvalidOperationException) { }

            Assert.Same(original, ScriptContext.Current);
        }

        [Fact]
        public void RunWith_Action_Exception_RestoresContext()
        {
            var original = ScriptContext.Current;
            var context = new ScriptContext();

            try
            {
                context.RunWith(() => throw new InvalidOperationException());
            }
            catch (InvalidOperationException) { }

            Assert.Same(original, ScriptContext.Current);
        }

        [Fact]
        public void RunWith_NestedContexts_RestoreCorrectly()
        {
            var original = ScriptContext.Current;
            var context1 = new ScriptContext();
            var context2 = new ScriptContext();

            ScriptContext innermost = null;

            context1.RunWith(() =>
            {
                Assert.Same(context1, ScriptContext.Current);
                context2.RunWith(() =>
                {
                    innermost = ScriptContext.Current;
                });
                Assert.Same(context1, ScriptContext.Current);
            });

            Assert.Same(context2, innermost);
            Assert.Same(original, ScriptContext.Current);
        }

        #endregion

        #region ScriptContext - Thread Local Isolation

        [Fact]
        public void ScriptContext_ThreadLocal_IsolatedBetweenThreads()
        {
            var context1 = new ScriptContext();
            ScriptContext threadContext = null;

            context1.RunWith(() =>
            {
                var thread = new Thread(() =>
                {
                    // On a new thread, Current should be null (thread-static)
                    threadContext = ScriptContext.Current;
                });
                thread.Start();
                thread.Join();
            });

            Assert.Null(threadContext);
        }

        #endregion

        #region HandleYieldPoint - Value Overloads

        [Fact]
        public void HandleYieldPoint_WithValue_NotRequested_DoesNotThrow()
        {
            var context = new ScriptContext();

            // No yield requested - should not throw
            context.HandleYieldPoint(0, "some value");
        }

        [Fact]
        public void HandleYieldPoint_WithValue_Requested_IncludesValue()
        {
            var context = new ScriptContext();
            context.RequestYield();

            var ex = Assert.Throws<SuspendException>(() =>
                context.HandleYieldPoint(42, "my value"));

            Assert.Equal(42, ex.YieldPointId);
            Assert.Equal("my value", ex.YieldedValue);
        }

        [Fact]
        public void HandleYieldPointWithBudget_WithValue_NotExhausted_DoesNotThrow()
        {
            var context = new ScriptContext();
            context.ResetBudget(100);

            // Budget not exhausted, no yield requested - should not throw
            context.HandleYieldPointWithBudget(0, 10, "my value");

            Assert.Equal(90, context.InstructionBudget);
        }

        #endregion

        #region SuspendException Edge Cases

        [Fact]
        public void SuspendException_BuildContinuationState_WithNullFrameChain()
        {
            var ex = new SuspendException(0);
            // FrameChain is initially null

            var state = ex.BuildContinuationState();

            Assert.NotNull(state);
            Assert.Null(state.StackHead);
        }

        [Fact]
        public void SuspendException_BuildContinuationState_PreservesYieldedValue()
        {
            var ex = new SuspendException(5, "my value");
            var frame = new HostFrameRecord(100, 5, new object[0], null);
            ex.FrameChain = frame;

            var state = ex.BuildContinuationState();

            Assert.Equal("my value", state.YieldedValue);
            Assert.Same(frame, state.StackHead);
        }

        [Fact]
        public void SuspendException_ToString_ShowsDepthAndYieldPoint()
        {
            var ex = new SuspendException(42);
            var str = ex.ToString();

            Assert.Contains("42", str);
        }

        #endregion

        #region ContinuationRunner Edge Cases

        [Fact]
        public void ContinuationRunner_Run_NullComputation_Throws()
        {
            var runner = new ContinuationRunner();

            Assert.Throws<ArgumentNullException>(() =>
                runner.Run<int>(null));
        }

        [Fact]
        public void ContinuationRunner_Run_ActionOverload_NullComputation_Throws()
        {
            var runner = new ContinuationRunner();

            Assert.Throws<ArgumentNullException>(() =>
                runner.Run(null));
        }

        [Fact]
        public void ContinuationRunner_Resume_NullContinuation_Throws()
        {
            var runner = new ContinuationRunner();

            Assert.Throws<ArgumentNullException>(() =>
                runner.Resume<int>(null));
        }

        [Fact]
        public void ContinuationRunner_Resume_WithStateAndEntryPoint_NullState_Throws()
        {
            var runner = new ContinuationRunner();

            Assert.Throws<ArgumentNullException>(() =>
                runner.Resume<int>(null, null, () => 42));
        }

        [Fact]
        public void ContinuationRunner_Resume_WithStateAndEntryPoint_NullEntryPoint_Throws()
        {
            var runner = new ContinuationRunner();
            var state = new ContinuationState(null);

            Assert.Throws<ArgumentNullException>(() =>
                runner.Resume<int>(state, null, null));
        }

        [Fact]
        public void ContinuationRunner_RequestYield_NoCurrent_DoesNotThrow()
        {
            // When no context is set, RequestYield should be a no-op
            var original = ScriptContext.Current;
            ScriptContext.Current = null;
            try
            {
                ContinuationRunner.RequestYield();
            }
            finally
            {
                ScriptContext.Current = original;
            }
        }

        #endregion
    }
}
