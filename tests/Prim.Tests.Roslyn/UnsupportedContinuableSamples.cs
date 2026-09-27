using System.Threading.Tasks;
using System.Collections.Generic;
using Prim.Core;

// Every member here is unsupported on purpose; PRIM002 is the expected result.
#pragma warning disable PRIM002

namespace Prim.Tests.Roslyn
{
    /// <summary>
    /// Members carrying [Continuable] that the generator must DETECT as unsupported
    /// and SKIP (emitting a diagnostic instead of broken generated code) per #26.
    ///
    /// These are deliberately not partial-class transformed: there must be NO
    /// *_Continuable variant generated for any member here. The presence of these
    /// members must not break the build (the diagnostics are warnings).
    /// </summary>
    public partial class UnsupportedContinuableSamples
    {
        /// <summary>
        /// async method -> diagnosed + skipped (#26).
        /// </summary>
        [Continuable]
        public async Task<int> AsyncMethod()
        {
            await Task.Yield();
            return 42;
        }

        /// <summary>
        /// iterator method (yield return) -> diagnosed + skipped (#26).
        /// </summary>
        [Continuable]
        public IEnumerable<int> IteratorMethod()
        {
            yield return 1;
            yield return 2;
        }

        /// <summary>
        /// generic method -> diagnosed + skipped (#26).
        /// </summary>
        [Continuable]
        public T GenericMethod<T>(T value)
        {
            return value;
        }

        /// <summary>
        /// expression-bodied method -> diagnosed + skipped (#26).
        /// </summary>
        [Continuable]
        public int ExpressionBodied() => 7;
    }
}
