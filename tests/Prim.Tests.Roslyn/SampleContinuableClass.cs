using System;
using System.Collections.Generic;
using Prim.Core;

namespace Prim.Tests.Roslyn
{
    /// <summary>
    /// Sample class with methods marked [Continuable] for testing the source generator.
    /// Tests various control flow constructs.
    /// </summary>
    public partial class SampleContinuableClass
    {
        #region Basic Loop Tests

        /// <summary>
        /// A simple for loop counter that can be suspended.
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
        /// A do-while loop that can be suspended.
        /// </summary>
        [Continuable]
        public int DoWhileCounter()
        {
            int count = 0;
            do
            {
                count++;
            } while (count < 5);
            return count;
        }

        /// <summary>
        /// A foreach loop iterating over an array.
        /// </summary>
        [Continuable]
        public int ForEachSum()
        {
            int[] numbers = { 1, 2, 3, 4, 5 };
            int sum = 0;
            foreach (var num in numbers)
            {
                sum += num;
            }
            return sum;
        }

        #endregion

        #region Nested Loop Tests

        /// <summary>
        /// Nested for loops.
        /// </summary>
        [Continuable]
        public int NestedForLoops()
        {
            int sum = 0;
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    sum += i * j;
                }
            }
            return sum;
        }

        /// <summary>
        /// Mixed nested loops - for inside while.
        /// </summary>
        [Continuable]
        public int MixedNestedLoops()
        {
            int result = 0;
            int outer = 0;
            while (outer < 3)
            {
                for (int inner = 0; inner < outer + 1; inner++)
                {
                    result++;
                }
                outer++;
            }
            return result; // Should be 1 + 2 + 3 = 6
        }

        #endregion

        #region Conditional Tests

        /// <summary>
        /// If-else statement with loops.
        /// </summary>
        [Continuable]
        public int ConditionalWithLoop(bool useFor)
        {
            int result = 0;
            if (useFor)
            {
                for (int i = 0; i < 5; i++)
                {
                    result += i;
                }
            }
            else
            {
                int j = 0;
                while (j < 5)
                {
                    result += j;
                    j++;
                }
            }
            return result;
        }

        #endregion

        #region Try-Catch-Finally Tests

        /// <summary>
        /// Try-catch with loop inside try block.
        /// </summary>
        [Continuable]
        public int TryCatchWithLoop()
        {
            int result = 0;
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    result += i;
                }
            }
            catch (Exception)
            {
                result = -1;
            }
            return result;
        }

        /// <summary>
        /// Try-finally with loop inside try block.
        /// </summary>
        [Continuable]
        public int TryFinallyWithLoop()
        {
            int result = 0;
            int finallyExecuted = 0;
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    result += i;
                }
            }
            finally
            {
                finallyExecuted = 1;
            }
            return result + finallyExecuted;
        }

        /// <summary>
        /// Try-catch-finally with exception filter.
        /// </summary>
        [Continuable]
        public int TryCatchWithFilter(bool shouldThrow)
        {
            int result = 0;
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    if (shouldThrow && i == 3)
                    {
                        throw new InvalidOperationException("Test exception");
                    }
                    result += i;
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Test"))
            {
                result = -100;
            }
            catch (Exception)
            {
                result = -1;
            }
            return result;
        }

        #endregion

        #region Switch Statement Tests

        /// <summary>
        /// Switch statement with loops in cases.
        /// </summary>
        [Continuable]
        public int SwitchWithLoops(int mode)
        {
            int result = 0;
            switch (mode)
            {
                case 1:
                    for (int i = 0; i < 3; i++)
                    {
                        result += i;
                    }
                    break;
                case 2:
                    int j = 0;
                    while (j < 3)
                    {
                        result += j * 2;
                        j++;
                    }
                    break;
                default:
                    result = -1;
                    break;
            }
            return result;
        }

        /// <summary>
        /// Switch with pattern matching.
        /// </summary>
        [Continuable]
        public string PatternMatchingSwitch(object value)
        {
            string result = "";
            switch (value)
            {
                case int i when i > 0:
                    for (int x = 0; x < i; x++)
                    {
                        result += "P";
                    }
                    break;
                case int i when i < 0:
                    for (int x = 0; x > i; x--)
                    {
                        result += "N";
                    }
                    break;
                case string s:
                    foreach (var c in s)
                    {
                        result += c.ToString().ToUpper();
                    }
                    break;
                case null:
                    result = "NULL";
                    break;
                default:
                    result = "UNKNOWN";
                    break;
            }
            return result;
        }

        #endregion

        #region Using Statement Tests

        /// <summary>
        /// Using statement with loop.
        /// </summary>
        [Continuable]
        public int UsingWithLoop()
        {
            int result = 0;
            using (var disposable = new DisposableResource())
            {
                for (int i = 0; i < 5; i++)
                {
                    result += disposable.GetValue();
                }
            }
            return result;
        }

        #endregion

        #region Complex Control Flow Tests

        /// <summary>
        /// Complex method with multiple control flow constructs.
        /// </summary>
        [Continuable]
        public int ComplexControlFlow(int input)
        {
            int result = 0;

            // Outer loop
            for (int i = 0; i < input; i++)
            {
                // Conditional
                if (i % 2 == 0)
                {
                    // Nested loop
                    int j = 0;
                    while (j < i)
                    {
                        result += j;
                        j++;

                        // Early exit condition
                        if (j > 5)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    // Try-catch inside loop
                    try
                    {
                        result += i * 2;
                    }
                    catch (Exception)
                    {
                        result = -1;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Method with early return inside loop.
        /// </summary>
        [Continuable]
        public int EarlyReturnInLoop(int[] numbers, int target)
        {
            for (int i = 0; i < numbers.Length; i++)
            {
                if (numbers[i] == target)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Method with continue statement.
        /// </summary>
        [Continuable]
        public int LoopWithContinue()
        {
            int sum = 0;
            for (int i = 0; i < 10; i++)
            {
                if (i % 2 == 0)
                {
                    continue;
                }
                sum += i;
            }
            return sum; // 1 + 3 + 5 + 7 + 9 = 25
        }

        /// <summary>
        /// Method with break statement.
        /// </summary>
        [Continuable]
        public int LoopWithBreak()
        {
            int sum = 0;
            for (int i = 0; i < 100; i++)
            {
                sum += i;
                if (sum > 10)
                {
                    break;
                }
            }
            return sum;
        }

        #endregion

        #region Void Return Tests

        /// <summary>
        /// Void method with loop.
        /// </summary>
        [Continuable]
        public void VoidMethodWithLoop()
        {
            int dummy = 0;
            for (int i = 0; i < 5; i++)
            {
                dummy += i;
            }
        }

        #endregion

        #region Expression Body Tests

        /// <summary>
        /// Expression-bodied method (simple case).
        /// </summary>
        [Continuable]
        public int ExpressionBodied(int x) => x * 2;

        #endregion
    }

    /// <summary>
    /// Helper class for testing using statements.
    /// </summary>
    public class DisposableResource : IDisposable
    {
        private int _value = 1;
        private bool _disposed = false;

        public int GetValue()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DisposableResource));
            return _value;
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
