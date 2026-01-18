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
        /// The validator to use for continuation state (optional).
        /// When set, all continuation states are validated before resumption.
        /// This is critical for security when accepting untrusted state.
        /// </summary>
        public ContinuationValidator Validator { get; set; }

        /// <summary>
        /// Registry of entry point delegates for direct resume support.
        /// When set, allows resuming continuations without re-specifying the entry point.
        /// </summary>
        public EntryPointRegistry EntryPoints { get; set; }

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
        /// Creates a new ContinuationRunner with a validator.
        /// </summary>
        public ContinuationRunner(ContinuationValidator validator)
        {
            Validator = validator;
        }

        /// <summary>
        /// Creates a new ContinuationRunner with both serializer and validator.
        /// </summary>
        public ContinuationRunner(IContinuationSerializer serializer, ContinuationValidator validator)
        {
            Serializer = serializer;
            Validator = validator;
        }

        /// <summary>
        /// Creates a new ContinuationRunner with an entry point registry.
        /// </summary>
        public ContinuationRunner(EntryPointRegistry entryPoints)
        {
            EntryPoints = entryPoints;
        }

        /// <summary>
        /// Creates a new ContinuationRunner with all components.
        /// </summary>
        public ContinuationRunner(
            IContinuationSerializer serializer,
            ContinuationValidator validator,
            EntryPointRegistry entryPoints)
        {
            Serializer = serializer;
            Validator = validator;
            EntryPoints = entryPoints;
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
        /// <exception cref="ValidationException">If validator is set and validation fails.</exception>
        public ContinuationResult<T> Resume<T>(Continuation<T> continuation, object resumeValue = null)
        {
            if (continuation == null) throw new ArgumentNullException(nameof(continuation));

            // Validate if validator is configured
            Validator?.Validate(continuation.State);

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
        /// <exception cref="ValidationException">If validator is set and validation fails.</exception>
        public ContinuationResult<T> Resume<T>(
            ContinuationState state,
            object resumeValue,
            Func<T> entryPoint)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (entryPoint == null) throw new ArgumentNullException(nameof(entryPoint));

            // Validate if validator is configured
            Validator?.Validate(state);

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
            if (EntryPoints == null)
            {
                throw new InvalidOperationException(
                    "Direct resume requires an EntryPointRegistry. " +
                    "Either set the EntryPoints property or use Resume(state, resumeValue, entryPoint).");
            }

            // Find the root frame (entry point) by following the Caller chain
            var rootFrame = GetRootFrame(context.FrameChain);
            if (rootFrame == null)
            {
                throw new InvalidOperationException(
                    "Cannot resume: continuation state has no frames.");
            }

            // Look up the entry point delegate by method token
            if (!EntryPoints.TryGet<T>(rootFrame.MethodToken, out var entryPoint))
            {
                throw new InvalidOperationException(
                    $"No entry point registered for method token {rootFrame.MethodToken}. " +
                    "Register entry points with EntryPoints.Register() before resuming.");
            }

            return RunWithContext(context, entryPoint);
        }

        /// <summary>
        /// Gets the root (outermost) frame from a frame chain.
        /// </summary>
        private static HostFrameRecord GetRootFrame(HostFrameRecord frame)
        {
            if (frame == null) return null;

            while (frame.Caller != null)
            {
                frame = frame.Caller;
            }
            return frame;
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
