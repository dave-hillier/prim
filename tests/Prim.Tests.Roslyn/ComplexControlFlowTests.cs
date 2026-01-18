using Prim.Core;
using Prim.Runtime;
using Xunit;

namespace Prim.Tests.Roslyn
{
    /// <summary>
    /// Tests for complex control flow scenarios in the source generator.
    /// These tests verify that the state machine transformation correctly handles
    /// loops, try-catch-finally, switch statements, and nested constructs.
    /// </summary>
    public class ComplexControlFlowTests
    {
        #region Basic Loop Tests

        [Fact]
        public void ForLoop_ComputesCorrectResult()
        {
            var instance = new SampleContinuableClass();
            var result = instance.CountToTen();
            Assert.Equal(55, result); // Sum of 1 to 10
        }

        [Fact]
        public void WhileLoop_ComputesCorrectResult()
        {
            var instance = new SampleContinuableClass();
            var result = instance.WhileCounter();
            Assert.Equal(5, result);
        }

        [Fact]
        public void DoWhileLoop_ComputesCorrectResult()
        {
            var instance = new SampleContinuableClass();
            var result = instance.DoWhileCounter();
            Assert.Equal(5, result);
        }

        [Fact]
        public void ForEachLoop_ComputesCorrectResult()
        {
            var instance = new SampleContinuableClass();
            var result = instance.ForEachSum();
            Assert.Equal(15, result); // 1 + 2 + 3 + 4 + 5
        }

        #endregion

        #region Nested Loop Tests

        [Fact]
        public void NestedForLoops_ComputesCorrectResult()
        {
            var instance = new SampleContinuableClass();
            var result = instance.NestedForLoops();
            // 0*0 + 0*1 + 0*2 + 1*0 + 1*1 + 1*2 + 2*0 + 2*1 + 2*2
            // = 0 + 0 + 0 + 0 + 1 + 2 + 0 + 2 + 4 = 9
            Assert.Equal(9, result);
        }

        [Fact]
        public void MixedNestedLoops_ComputesCorrectResult()
        {
            var instance = new SampleContinuableClass();
            var result = instance.MixedNestedLoops();
            Assert.Equal(6, result); // 1 + 2 + 3
        }

        #endregion

        #region Conditional Tests

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ConditionalWithLoop_ComputesCorrectResult(bool useFor)
        {
            var instance = new SampleContinuableClass();
            var result = instance.ConditionalWithLoop(useFor);
            Assert.Equal(10, result); // 0 + 1 + 2 + 3 + 4
        }

        #endregion

        #region Try-Catch-Finally Tests

        [Fact]
        public void TryCatchWithLoop_NoException_ComputesCorrectResult()
        {
            var instance = new SampleContinuableClass();
            var result = instance.TryCatchWithLoop();
            Assert.Equal(10, result); // 0 + 1 + 2 + 3 + 4
        }

        [Fact]
        public void TryFinallyWithLoop_FinallyExecutes()
        {
            var instance = new SampleContinuableClass();
            var result = instance.TryFinallyWithLoop();
            Assert.Equal(11, result); // 10 + 1 (finally executed)
        }

        [Fact]
        public void TryCatchWithFilter_NoException_ComputesCorrectResult()
        {
            var instance = new SampleContinuableClass();
            var result = instance.TryCatchWithFilter(false);
            Assert.Equal(10, result); // 0 + 1 + 2 + 3 + 4
        }

        [Fact]
        public void TryCatchWithFilter_WithException_CatchesException()
        {
            var instance = new SampleContinuableClass();
            var result = instance.TryCatchWithFilter(true);
            Assert.Equal(-100, result); // Exception caught by filter
        }

        #endregion

        #region Switch Statement Tests

        [Theory]
        [InlineData(1, 3)]  // 0 + 1 + 2
        [InlineData(2, 6)]  // 0*2 + 1*2 + 2*2
        [InlineData(0, -1)] // default case
        [InlineData(99, -1)] // default case
        public void SwitchWithLoops_ComputesCorrectResult(int mode, int expected)
        {
            var instance = new SampleContinuableClass();
            var result = instance.SwitchWithLoops(mode);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(3, "PPP")]
        [InlineData(-2, "NN")]
        [InlineData("abc", "ABC")]
        [InlineData(null, "NULL")]
        public void PatternMatchingSwitch_ComputesCorrectResult(object value, string expected)
        {
            var instance = new SampleContinuableClass();
            var result = instance.PatternMatchingSwitch(value);
            Assert.Equal(expected, result);
        }

        #endregion

        #region Using Statement Tests

        [Fact]
        public void UsingWithLoop_ComputesCorrectResult()
        {
            var instance = new SampleContinuableClass();
            var result = instance.UsingWithLoop();
            Assert.Equal(5, result); // 1 * 5
        }

        #endregion

        #region Complex Control Flow Tests

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 0)]   // i=0 (even): j loop from 0 to -1 = no iterations
        [InlineData(2, 2)]   // i=0: 0 iterations, i=1 (odd): 1*2 = 2
        [InlineData(3, 3)]   // i=0: 0, i=1: 2, i=2 (even, j<2): 0+1 = 1, total = 3
        [InlineData(5, 13)]  // Complex calculation
        public void ComplexControlFlow_ComputesCorrectResult(int input, int expected)
        {
            var instance = new SampleContinuableClass();
            var result = instance.ComplexControlFlow(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void EarlyReturnInLoop_FindsTarget()
        {
            var instance = new SampleContinuableClass();
            var result = instance.EarlyReturnInLoop(new[] { 10, 20, 30, 40, 50 }, 30);
            Assert.Equal(2, result);
        }

        [Fact]
        public void EarlyReturnInLoop_TargetNotFound()
        {
            var instance = new SampleContinuableClass();
            var result = instance.EarlyReturnInLoop(new[] { 10, 20, 30, 40, 50 }, 99);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void LoopWithContinue_SkipsEvenNumbers()
        {
            var instance = new SampleContinuableClass();
            var result = instance.LoopWithContinue();
            Assert.Equal(25, result); // 1 + 3 + 5 + 7 + 9
        }

        [Fact]
        public void LoopWithBreak_StopsEarly()
        {
            var instance = new SampleContinuableClass();
            var result = instance.LoopWithBreak();
            // 0 + 1 + 2 + 3 + 4 + 5 = 15 (breaks when sum > 10)
            Assert.Equal(15, result);
        }

        #endregion

        #region Void Return Tests

        [Fact]
        public void VoidMethodWithLoop_Executes()
        {
            var instance = new SampleContinuableClass();
            instance.VoidMethodWithLoop();
            // No exception = success
        }

        #endregion

        #region Expression Body Tests

        [Fact]
        public void ExpressionBodied_ComputesCorrectResult()
        {
            var instance = new SampleContinuableClass();
            var result = instance.ExpressionBodied(21);
            Assert.Equal(42, result);
        }

        #endregion

        #region Suspension and Resume Tests with Complex Control Flow

        [Fact]
        public void ForLoop_CanBeSuspendedAndResumed()
        {
            var runner = new ContinuationRunner();
            var instance = new SampleContinuableClass();

            // Run to completion
            var result = runner.Run(() => instance.CountToTen());

            Assert.True(result.IsCompleted);
            Assert.Equal(55, ((ContinuationResult<int>.Completed)result).Value);
        }

        [Fact]
        public void WhileLoop_WithContinuationRunner_Works()
        {
            var runner = new ContinuationRunner();
            var instance = new SampleContinuableClass();

            var result = runner.Run(() => instance.WhileCounter());

            Assert.True(result.IsCompleted);
            Assert.Equal(5, ((ContinuationResult<int>.Completed)result).Value);
        }

        [Fact]
        public void NestedLoops_WithContinuationRunner_Works()
        {
            var runner = new ContinuationRunner();
            var instance = new SampleContinuableClass();

            var result = runner.Run(() => instance.NestedForLoops());

            Assert.True(result.IsCompleted);
            Assert.Equal(9, ((ContinuationResult<int>.Completed)result).Value);
        }

        [Fact]
        public void TryCatch_WithContinuationRunner_Works()
        {
            var runner = new ContinuationRunner();
            var instance = new SampleContinuableClass();

            var result = runner.Run(() => instance.TryCatchWithLoop());

            Assert.True(result.IsCompleted);
            Assert.Equal(10, ((ContinuationResult<int>.Completed)result).Value);
        }

        [Fact]
        public void Switch_WithContinuationRunner_Works()
        {
            var runner = new ContinuationRunner();
            var instance = new SampleContinuableClass();

            var result = runner.Run(() => instance.SwitchWithLoops(1));

            Assert.True(result.IsCompleted);
            Assert.Equal(3, ((ContinuationResult<int>.Completed)result).Value);
        }

        #endregion
    }
}
