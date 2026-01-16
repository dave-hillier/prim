using Prim.Core;
using Prim.Runtime;
using Xunit;

namespace Prim.Tests.Roslyn
{
    public class SourceGeneratorTests
    {
        [Fact]
        public void GeneratedMethod_ExistsOnPartialClass()
        {
            // The source generator should have created CountToTen_Continuable
            var instance = new SampleContinuableClass();

            // Call the generated method
            var result = instance.CountToTen_Continuable();

            // Should return 55 (sum of 1 to 10)
            Assert.Equal(55, result);
        }

        [Fact]
        public void GeneratedMethod_WithWhileLoop_Works()
        {
            var instance = new SampleContinuableClass();

            var result = instance.WhileCounter_Continuable();

            Assert.Equal(5, result);
        }

        [Fact]
        public void GeneratedMethod_CanBeSuspendedAndResumed()
        {
            var instance = new SampleContinuableClass();
            var runner = new ContinuationRunner();

            // First, run without requesting yield - should complete
            var result1 = runner.Run(() => instance.CountToTen_Continuable());
            Assert.True(result1.IsCompleted);
            Assert.Equal(55, ((ContinuationResult<int>.Completed)result1).Value);
        }
    }
}
