using System;
using Prim.Core;

namespace Prim.Runtime
{
    /// <summary>
    /// Entry point for running transformed code with continuation support.
    /// </summary>
    public sealed class ContinuationRunner
    {
        /// <summary>
        /// The serializer to use for created continuations (optional).
        /// </summary>
        public IContinuationSerializer Serializer { get; set; }

        /// <summary>
        /// Creates a new ContinuationRunner.
        /// </summary>
        public ContinuationRunner()
        {
        }

        /// <summary>
        /// Creates a new ContinuationRunner with a serializer.
        /// </summary>
        public ContinuationRunner(IContinuationSerializer serializer)
        {
            Serializer = serializer;
        }

        /// <summary>
        /// Runs a computation that may suspend.
        /// </summary>
        /// <typeparam name="T">The return type of the computation.</typeparam>
        /// <param name="computation">The computation to run.</param>
        /// <returns>Completed with result or Suspended with continuation state.</returns>
        public ContinuationResult<T> Run<T>(Func<T> computation)
        {
            if (computation == null) throw new ArgumentNullException(nameof(computation));

            var context = new ScriptContext();
            return RunWithContext<T>(context, computation);
        }

        /// <summary>
        /// Runs a void computation that may suspend.
        /// </summary>
        /// <param name="computation">The computation to run.</param>
        /// <returns>Completed or Suspended with continuation state.</returns>
        public ContinuationResult<Unit> Run(Action computation)
        {
            if (computation == null) throw new ArgumentNullException(nameof(computation));

            return Run(() =>
            {
                computation();
                return Unit.Value;
            });
        }

        /// <summary>
        /// Resumes a suspended continuation.
        /// </summary>
        /// <typeparam name="T">The return type of the computation.</typeparam>
        /// <param name="continuation">The continuation to resume.</param>
        /// <param name="resumeValue">Value to pass to the resume point (default: null).</param>
        /// <returns>Completed with result or Suspended again.</returns>
        public ContinuationResult<T> Resume<T>(Continuation<T> continuation, object resumeValue = null)
        {
            if (continuation == null) throw new ArgumentNullException(nameof(continuation));

            var context = new ScriptContext(continuation.State, resumeValue);
            return RunRestoringWithContext<T>(context);
        }

        /// <summary>
        /// Resumes a suspended continuation from its state.
        /// </summary>
        /// <typeparam name="T">The return type of the computation.</typeparam>
        /// <param name="state">The continuation state to resume.</param>
        /// <param name="resumeValue">Value to pass to the resume point.</param>
        /// <param name="entryPoint">The entry point function for the computation.</param>
        /// <returns>Completed with result or Suspended again.</returns>
        public ContinuationResult<T> Resume<T>(
            ContinuationState state,
            object resumeValue,
            Func<T> entryPoint)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (entryPoint == null) throw new ArgumentNullException(nameof(entryPoint));

            var context = new ScriptContext(state, resumeValue);
            return RunWithContext(context, entryPoint);
        }

        /// <summary>
        /// Requests that the current computation yield at the next yield point.
        /// </summary>
        public static void RequestYield()
        {
            ScriptContext.Current?.RequestYield();
        }

        private ContinuationResult<T> RunWithContext<T>(ScriptContext context, Func<T> computation)
        {
            try
            {
                var result = context.RunWith(computation);
                return new ContinuationResult<T>.Completed(result);
            }
            catch (SuspendException ex)
            {
                var state = ex.BuildContinuationState();
                return new ContinuationResult<T>.Suspended(ex.YieldedValue, state);
            }
        }

        private ContinuationResult<T> RunRestoringWithContext<T>(ScriptContext context)
        {
            // When restoring, we need the entry point function to be called.
            // The generated code will handle the restoration based on IsRestoring.
            // This is a placeholder - actual restoration requires knowing the entry point.
            throw new NotImplementedException(
                "Direct resume without entry point requires runtime metadata. " +
                "Use Resume(state, resumeValue, entryPoint) instead.");
        }
    }

    /// <summary>
    /// Extension methods for ContinuationRunner.
    /// </summary>
    public static class ContinuationRunnerExtensions
    {
        /// <summary>
        /// Creates a Continuation from a Suspended result.
        /// </summary>
        public static Continuation<T> ToContinuation<T>(
            this ContinuationResult<T>.Suspended suspended,
            IContinuationSerializer serializer = null)
        {
            return new Continuation<T>(suspended.State, serializer);
        }

        /// <summary>
        /// Runs until completion, repeatedly resuming suspensions.
        /// Useful for testing or when you want to ignore suspension points.
        /// </summary>
        public static T RunToCompletion<T>(
            this ContinuationRunner runner,
            Func<T> computation,
            Func<object, object> handleSuspension = null)
        {
            var result = runner.Run(computation);

            while (!result.IsCompleted)
            {
                var suspended = (ContinuationResult<T>.Suspended)result;
                var resumeValue = handleSuspension?.Invoke(suspended.YieldedValue);
                result = runner.Resume(suspended.State, resumeValue, computation);
            }

            return ((ContinuationResult<T>.Completed)result).Value;
        }
    }
}
