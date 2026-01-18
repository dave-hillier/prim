using Prim.Core;

namespace Prim.Tests.Roslyn
{
    /// <summary>
    /// Sample class with methods marked [Continuable] for testing the source generator.
    /// </summary>
    public partial class SampleContinuableClass
    {
        /// <summary>
        /// A simple counter loop that can be suspended.
        /// </summary>
        [Continuable]
        public int CountToTen()
        {
            int sum = 0;
            for (int i = 1; i <= 10; i++)
            {
                sum += i;
            }
            return sum;
        }

        /// <summary>
        /// A while loop that can be suspended.
        /// </summary>
        [Continuable]
        public int WhileCounter()
        {
            int count = 0;
            while (count < 5)
            {
                count++;
            }
            return count;
        }

        /// <summary>
        /// A method with try-catch that can be suspended.
        /// </summary>
        [Continuable]
        public int TryCatchMethod()
        {
            int result = 0;
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    result += i;
                }
            }
            catch (System.InvalidOperationException)
            {
                result = -1;
            }
            return result;
        }

        /// <summary>
        /// A method with try-finally that can be suspended.
        /// </summary>
        [Continuable]
        public int TryFinallyMethod()
        {
            int result = 0;
            int cleanup = 0;
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    result += i;
                }
            }
            finally
            {
                cleanup = 1;
            }
            return result + cleanup;
        }

        /// <summary>
        /// A method with try-catch-finally that can be suspended.
        /// </summary>
        [Continuable]
        public int TryCatchFinallyMethod()
        {
            int result = 0;
            int cleanup = 0;
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    result += i;
                }
            }
            catch (System.InvalidOperationException)
            {
                result = -1;
            }
            finally
            {
                cleanup = 1;
            }
            return result + cleanup;
        }

        /// <summary>
        /// A method with nested try-catch-finally blocks.
        /// </summary>
        [Continuable]
        public int NestedTryMethod()
        {
            int result = 0;
            int outer = 0;
            int inner = 0;
            try
            {
                outer = 1;
                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        result += i;
                    }
                    inner = 2;
                }
                finally
                {
                    inner += 10;
                }
            }
            finally
            {
                outer += 100;
            }
            return result + outer + inner;
        }

        /// <summary>
        /// A method with loop inside finally block.
        /// </summary>
        [Continuable]
        public int LoopInFinallyMethod()
        {
            int result = 0;
            try
            {
                result = 10;
            }
            finally
            {
                for (int i = 0; i < 3; i++)
                {
                    result += i;
                }
            }
            return result;
        }

        #region Nested Method Call Tests

        /// <summary>
        /// A simple inner method that computes a sum with a loop.
        /// Can be called by other continuable methods.
        /// </summary>
        [Continuable]
        public int InnerSum(int n)
        {
            int sum = 0;
            for (int i = 1; i <= n; i++)
            {
                sum += i;
            }
            return sum;
        }

        /// <summary>
        /// An outer method that calls InnerSum.
        /// The generator transforms calls to [Continuable] methods.
        /// Tests nested method calls with yield points.
        /// </summary>
        [Continuable]
        public int OuterCallsInner()
        {
            int result = 0;
            result = InnerSum(5); // Generator will transform this
            return result + 100; // Add 100 to distinguish from direct call
        }

        /// <summary>
        /// A method that makes multiple nested calls.
        /// </summary>
        [Continuable]
        public int MultipleNestedCalls()
        {
            int a = InnerSum(3);  // 1+2+3 = 6
            int b = InnerSum(4);  // 1+2+3+4 = 10
            return a + b; // 16
        }

        /// <summary>
        /// A method that combines nested calls with loops.
        /// </summary>
        [Continuable]
        public int NestedCallInLoop()
        {
            int total = 0;
            for (int i = 1; i <= 3; i++)
            {
                total += InnerSum(i);
            }
            // InnerSum(1) = 1, InnerSum(2) = 3, InnerSum(3) = 6
            // Total = 1 + 3 + 6 = 10
            return total;
        }

        /// <summary>
        /// A method that uses nested call result in expression.
        /// </summary>
        [Continuable]
        public int NestedCallInExpression()
        {
            int result = InnerSum(5) * 2; // (1+2+3+4+5) * 2 = 30
            return result;
        }

        /// <summary>
        /// A method with nested call inside try block.
        /// </summary>
        [Continuable]
        public int NestedCallInTry()
        {
            int result = 0;
            try
            {
                result = InnerSum(4); // 1+2+3+4 = 10
            }
            finally
            {
                result += 5;
            }
            return result; // 15
        }

        /// <summary>
        /// Deeply nested: outer calls middle, middle calls inner.
        /// </summary>
        [Continuable]
        public int DeepNesting()
        {
            int result = MiddleMethod(3);
            return result;
        }

        /// <summary>
        /// Middle method for deep nesting test.
        /// </summary>
        [Continuable]
        public int MiddleMethod(int n)
        {
            int inner = InnerSum(n);
            return inner * 10; // Multiply by 10 to track call chain
        }

        #endregion
    }
}
