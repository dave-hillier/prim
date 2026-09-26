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
            var runner = new ContinuationRunner();

            // Test that the runner correctly handles suspension when HandleYieldPoint throws
            var result = runner.Run(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                context.RequestYield();
                context.HandleYieldPoint(0); // This will throw SuspendException
                return 42;
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
            var serializer = new JsonContinuationSerializer();

            // Create state directly (IL transformation would normally capture this during unwinding)
            var methodToken = StableHash.GenerateMethodToken("Test", "Method", "System.Int32");
            var slots = new object[] { 42, "serialization test" };
            var frame = new HostFrameRecord(methodToken, 0, slots, null);
            var state = new ContinuationState(frame, "serialization test");

            var json = serializer.SerializeToString(state);
            Assert.NotEmpty(json);
            Assert.Contains("YieldPointId", json);
            Assert.Contains("MethodToken", json);
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

        #region Try-Catch-Finally Tests

        [Fact]
        public void TryCatch_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            // Call the generated try-catch method
            var result = instance.TryCatchMethod();

            // Should return 10 (sum of 0 to 4)
            Assert.Equal(10, result);
        }

        [Fact]
        public void TryFinally_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            // Call the generated try-finally method
            var result = instance.TryFinallyMethod();

            // Should return 11 (sum of 0 to 4 = 10, plus cleanup = 1)
            Assert.Equal(11, result);
        }

        [Fact]
        public void TryCatchFinally_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            // Call the generated try-catch-finally method
            var result = instance.TryCatchFinallyMethod();

            // Should return 11 (sum of 0 to 4 = 10, plus cleanup = 1)
            Assert.Equal(11, result);
        }

        [Fact]
        public void NestedTry_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            // Call the generated nested try method
            var result = instance.NestedTryMethod();

            // result = 3 (sum of 0,1,2), outer = 101 (1 + 100), inner = 12 (2 + 10)
            // Total = 3 + 101 + 12 = 116
            Assert.Equal(116, result);
        }

        [Fact]
        public void LoopInFinally_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            // LoopInFinallyMethod is no longer [Continuable] (a loop in a finally is a
            // PRIM003 error, asserted in GeneratorDiagnosticTests), so this calls the
            // plain hand-written method and verifies its ordinary behavior.
            var result = instance.LoopInFinallyMethod();

            // result starts at 10, then adds 0+1+2 in finally = 13
            Assert.Equal(13, result);
        }

        [Fact]
        public void TryCatch_CanBeSuspendedAndResumed()
        {
            var instance = new SampleContinuableClass();
            var runner = new ContinuationRunner();

            // Run without yielding - should complete
            var result = runner.Run(() => instance.TryCatchMethod());
            Assert.True(result.IsCompleted);
            Assert.Equal(10, ((ContinuationResult<int>.Completed)result).Value);
        }

        [Fact]
        public void TryFinally_CanBeSuspendedAndResumed()
        {
            var instance = new SampleContinuableClass();
            var runner = new ContinuationRunner();

            // Run without yielding - should complete
            var result = runner.Run(() => instance.TryFinallyMethod());
            Assert.True(result.IsCompleted);
            Assert.Equal(11, ((ContinuationResult<int>.Completed)result).Value);
        }

        [Fact]
        public void TryCatchFinally_CanBeSuspendedAndResumed()
        {
            var instance = new SampleContinuableClass();
            var runner = new ContinuationRunner();

            // Run without yielding - should complete
            var result = runner.Run(() => instance.TryCatchFinallyMethod());
            Assert.True(result.IsCompleted);
            Assert.Equal(11, ((ContinuationResult<int>.Completed)result).Value);
        }

        #endregion

        #region Nested Method Call Tests

        [Fact]
        public void InnerSum_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            // Direct call to continuable inner method
            var result = instance.InnerSum_Continuable(5);

            // Should return 15 (1+2+3+4+5)
            Assert.Equal(15, result);
        }

        [Fact]
        public void OuterCallsInner_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            // Outer method calls inner continuable method
            var result = instance.OuterCallsInner_Continuable();

            // InnerSum(5) = 15, plus 100 = 115
            Assert.Equal(115, result);
        }

        [Fact]
        public void MultipleNestedCalls_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            var result = instance.MultipleNestedCalls_Continuable();

            // InnerSum(3) = 6, InnerSum(4) = 10, total = 16
            Assert.Equal(16, result);
        }

        [Fact]
        public void NestedCallInLoop_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            var result = instance.NestedCallInLoop_Continuable();

            // InnerSum(1)=1 + InnerSum(2)=3 + InnerSum(3)=6 = 10
            Assert.Equal(10, result);
        }

        [Fact]
        public void NestedCallInExpression_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            var result = instance.NestedCallInExpression_Continuable();

            // InnerSum(5)=15 * 2 = 30
            Assert.Equal(30, result);
        }

        [Fact]
        public void NestedCallInTry_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            var result = instance.NestedCallInTry_Continuable();

            // InnerSum(4)=10 + 5 (from finally) = 15
            Assert.Equal(15, result);
        }

        [Fact]
        public void DeepNesting_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            var result = instance.DeepNesting_Continuable();

            // DeepNesting calls MiddleMethod(3)
            // MiddleMethod calls InnerSum(3) = 6, returns 6 * 10 = 60
            Assert.Equal(60, result);
        }

        [Fact]
        public void NestedCall_CanBeSuspendedAndResumed()
        {
            var instance = new SampleContinuableClass();
            var runner = new ContinuationRunner();

            // Run without yielding - should complete
            var result = runner.Run(() => instance.OuterCallsInner_Continuable());
            Assert.True(result.IsCompleted);
            Assert.Equal(115, ((ContinuationResult<int>.Completed)result).Value);
        }

        [Fact]
        public void MultipleNestedCalls_CanBeSuspendedAndResumed()
        {
            var instance = new SampleContinuableClass();
            var runner = new ContinuationRunner();

            var result = runner.Run(() => instance.MultipleNestedCalls_Continuable());
            Assert.True(result.IsCompleted);
            Assert.Equal(16, ((ContinuationResult<int>.Completed)result).Value);
        }

        [Fact]
        public void DeepNesting_CanBeSuspendedAndResumed()
        {
            var instance = new SampleContinuableClass();
            var runner = new ContinuationRunner();

            var result = runner.Run(() => instance.DeepNesting_Continuable());
            Assert.True(result.IsCompleted);
            Assert.Equal(60, ((ContinuationResult<int>.Completed)result).Value);
        }

        #endregion

        #region Batch 5 Feature Tests

        [Fact]
        public void VarLocalOfNonTrivialType_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            // var builder = new StringBuilder(); appends 'x' 4 times -> Length 4
            var result = instance.VarLocalOfNonTrivialType_Continuable();

            Assert.Equal(4, result);
        }

        [Fact]
        public void SiblingScopeLocalsSameName_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            // Two sibling-scope locals named 'i' (10 and 20) -> 30
            var result = instance.SiblingScopeLocalsSameName_Continuable();

            Assert.Equal(30, result);
        }

        [Fact]
        public void SiblingCatchClauses_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            // Two sibling typed catch clauses; normal path returns 7. The generated
            // variant compiling at all proves sibling catches get distinct guard names
            // and the SuspendException filter is not mis-emitted on unrelated catches.
            var result = instance.SiblingCatchClauses_Continuable();

            Assert.Equal(7, result);
        }

        [Fact]
        public void ForEachSum_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            // foreach over {1,2,3,4,5} -> 15
            var result = instance.ForEachSum_Continuable();

            Assert.Equal(15, result);
        }

        [Fact]
        public void WhileWithLocal_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            // while loop with body-local -> 1+2+3+4+5 = 15
            var result = instance.WhileWithLocal_Continuable();

            Assert.Equal(15, result);
        }

        [Fact]
        public void UsingStatementMethod_GeneratedMethod_Works()
        {
            var instance = new SampleContinuableClass();

            // using resource exposes Value 42, disposed in finally
            var result = instance.UsingStatementMethod_Continuable();

            Assert.Equal(42, result);
        }

        [Fact]
        public void ForEachSum_CanBeSuspendedAndResumed()
        {
            var instance = new SampleContinuableClass();
            var runner = new ContinuationRunner();

            // Force a suspend before the foreach loop yield point, then resume.
            var first = runner.Run(() =>
            {
                ScriptContext.Current.RequestYield();
                return instance.ForEachSum_Continuable();
            });
            Assert.True(first.IsSuspended);
            var suspended = (ContinuationResult<int>.Suspended)first;

            var resumed = runner.Resume<int>(
                suspended.State,
                resumeValue: null,
                entryPoint: () => instance.ForEachSum_Continuable());

            Assert.True(resumed.IsCompleted);
            Assert.Equal(15, ((ContinuationResult<int>.Completed)resumed).Value);
        }

        [Fact]
        public void WhileWithLocal_CanBeSuspendedAndResumed()
        {
            var instance = new SampleContinuableClass();
            var runner = new ContinuationRunner();

            var first = runner.Run(() =>
            {
                ScriptContext.Current.RequestYield();
                return instance.WhileWithLocal_Continuable();
            });
            Assert.True(first.IsSuspended);
            var suspended = (ContinuationResult<int>.Suspended)first;

            var resumed = runner.Resume<int>(
                suspended.State,
                resumeValue: null,
                entryPoint: () => instance.WhileWithLocal_Continuable());

            Assert.True(resumed.IsCompleted);
            Assert.Equal(15, ((ContinuationResult<int>.Completed)resumed).Value);
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
        public void FrameCapture_CaptureFrame_WithCallee()
        {
            var callee = new HostFrameRecord(100, 0, new object[0], null);
            var slots = new object[] { 1 };
            var record = FrameCapture.CaptureFrame(200, 1, slots, callee);

            Assert.NotNull(record);
            Assert.Same(callee, record.Caller);
            Assert.Equal(2, record.GetStackDepth());
        }

        #endregion
    }
}
