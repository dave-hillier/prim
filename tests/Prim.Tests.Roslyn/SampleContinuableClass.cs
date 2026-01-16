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
    }
}
