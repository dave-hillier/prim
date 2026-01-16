using System;

namespace Prim.Core
{
    /// <summary>
    /// Result of running a continuable computation.
    /// Either Completed (with a value) or Suspended (with yielded value and continuation).
    /// </summary>
    public abstract class ContinuationResult<T>
    {
        private ContinuationResult() { }

        /// <summary>
        /// Whether this result represents a completed computation.
        /// </summary>
        public abstract bool IsCompleted { get; }

        /// <summary>
        /// Whether this result represents a suspended computation.
        /// </summary>
        public bool IsSuspended => !IsCompleted;

        /// <summary>
        /// The computation completed normally with a result value.
        /// </summary>
        public sealed class Completed : ContinuationResult<T>
        {
            /// <summary>
            /// The final result value of the computation.
            /// </summary>
            public T Value { get; }

            public override bool IsCompleted => true;

            public Completed(T value)
            {
                Value = value;
            }

            public override string ToString() => $"Completed({Value})";
        }

        /// <summary>
        /// The computation suspended and can be resumed later.
        /// </summary>
        public sealed class Suspended : ContinuationResult<T>
        {
            /// <summary>
            /// The value that was yielded when suspending.
            /// </summary>
            public object YieldedValue { get; }

            /// <summary>
            /// The captured state that can be used to resume the computation.
            /// </summary>
            public ContinuationState State { get; }

            public override bool IsCompleted => false;

            public Suspended(object yieldedValue, ContinuationState state)
            {
                YieldedValue = yieldedValue;
                State = state ?? throw new ArgumentNullException(nameof(state));
            }

            public override string ToString() => $"Suspended(Yielded={YieldedValue}, Depth={State.GetStackDepth()})";
        }

        /// <summary>
        /// Pattern match on the result.
        /// </summary>
        public TResult Match<TResult>(
            Func<Completed, TResult> onCompleted,
            Func<Suspended, TResult> onSuspended)
        {
            if (onCompleted == null) throw new ArgumentNullException(nameof(onCompleted));
            if (onSuspended == null) throw new ArgumentNullException(nameof(onSuspended));

            return this switch
            {
                Completed c => onCompleted(c),
                Suspended s => onSuspended(s),
                _ => throw new InvalidOperationException("Unknown result type")
            };
        }

        /// <summary>
        /// Pattern match on the result with actions.
        /// </summary>
        public void Match(Action<Completed> onCompleted, Action<Suspended> onSuspended)
        {
            if (onCompleted == null) throw new ArgumentNullException(nameof(onCompleted));
            if (onSuspended == null) throw new ArgumentNullException(nameof(onSuspended));

            switch (this)
            {
                case Completed c:
                    onCompleted(c);
                    break;
                case Suspended s:
                    onSuspended(s);
                    break;
            }
        }
    }
}
