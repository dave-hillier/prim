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
        /// A method with a loop inside a finally block. This shape is NOT continuable:
        /// a yield point inside a finally cannot be suspended (whitepaper §10.2), so the
        /// generator reports PRIM003 and skips it. It is intentionally left WITHOUT the
        /// [Continuable] attribute here so the build stays green; the PRIM003 diagnostic
        /// is asserted directly via the generator driver in GeneratorDiagnosticTests.
        /// The plain method is still exercised by LoopInFinallyMethod_GeneratedMethod_Works.
        /// </summary>
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

        #region Batch 5 Feature Tests (#25 var types, #27 sibling-local dedup, #28 foreach, #29 using)

        /// <summary>
        /// Uses a 'var' local of a non-trivial type (System.Text.StringBuilder).
        /// Exercises #25: the generator must resolve the var's type via the semantic
        /// model and emit a fully-qualified type for the hoisted slot.
        /// </summary>
        [Continuable]
        public int VarLocalOfNonTrivialType()
        {
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < 4; i++)
            {
                builder.Append('x');
            }
            return builder.Length; // 4
        }

        /// <summary>
        /// Two sibling-scope locals both named 'i' (legal C#). Exercises #27: each
        /// hoisted local must get a unique synthetic name so they do not collide
        /// (CS0128) in the generated prologue.
        /// </summary>
        [Continuable]
        public int SiblingScopeLocalsSameName()
        {
            int total = 0;
            {
                int i = 10;
                total += i;
            }
            {
                int i = 20;
                total += i;
            }
            return total; // 30
        }

        /// <summary>
        /// A foreach over an int array with a loop-body local. Exercises #25 (element
        /// type resolution) and #28 (foreach generates compiling, correctly typed code
        /// and replays cleanly).
        /// </summary>
        [Continuable]
        public int ForEachSum()
        {
            int sum = 0;
            int[] numbers = new int[] { 1, 2, 3, 4 };
            foreach (var n in numbers)
            {
                sum += n;
            }
            return sum; // 10
        }

        /// <summary>
        /// A while loop with a body-local. Exercises hoisting of a nested local plus
        /// the loop back-edge yield point.
        /// </summary>
        [Continuable]
        public int WhileWithLocal()
        {
            int sum = 0;
            int n = 0;
            while (n < 5)
            {
                int step = n + 1;
                sum += step;
                n++;
            }
            return sum; // 1+2+3+4+5 = 15
        }

        /// <summary>
        /// A using statement whose resource local must be collected up front (#29).
        /// Exercises correct dispose-in-finally semantics.
        /// </summary>
        [Continuable]
        public int UsingStatementMethod()
        {
            int result = 0;
            using (var resource = new TrackedDisposable())
            {
                result = resource.Value; // 42
            }
            return result;
        }

        /// <summary>
        /// A try with two sibling typed catch clauses. Exercises that sibling catches
        /// get distinct synthetic guard names (#27) and that the SuspendException-escape
        /// filter is not emitted on unrelated typed catches (no CS0184). The generated
        /// variant must compile and return the normal-path value.
        /// </summary>
        [Continuable]
        public int SiblingCatchClauses()
        {
            int result = 0;
            try
            {
                result = 7;
            }
            catch (System.InvalidOperationException)
            {
                result = -1;
            }
            catch (System.ArgumentException)
            {
                result = -2;
            }
            return result;
        }

        #endregion

        #region Batch 6 replay-model coverage (#20 / #21)

        /// <summary>
        /// A continuable call nested inside an IF block (#20). The replay model re-runs
        /// the method body from the top on resume; because the if-condition is a pure
        /// re-computation over restored locals, the same branch is taken on replay and the
        /// inner call's restore block resumes it. Proves a yield point inside an if works.
        /// </summary>
        [Continuable]
        public int IfNestedCall(bool take)
        {
            int result = 0;
            if (take)
            {
                result = InnerSum(5); // 15
            }
            else
            {
                result = -1;
            }
            return result;
        }

        /// <summary>
        /// A continuable call inside a loop body (#20). On resume the loop is replayed
        /// from its header against the restored accumulator; the inner call resumes via
        /// the frame chain. Distinct from NestedCallInLoop only in that the suspend/resume
        /// round-trip is asserted directly.
        /// </summary>
        [Continuable]
        public int LoopBodyNestedCall()
        {
            int total = 0;
            for (int i = 1; i <= 3; i++)
            {
                total += InnerSum(i); // 1 + 3 + 6 = 10
            }
            return total;
        }

        #endregion
    }

    /// <summary>
    /// A simple disposable used by UsingStatementMethod to verify using semantics.
    /// </summary>
    public sealed class TrackedDisposable : System.IDisposable
    {
        public int Value => 42;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
