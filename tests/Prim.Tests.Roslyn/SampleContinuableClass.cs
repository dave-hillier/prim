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
    }
}
