using System;
using System.Collections.Generic;
using System.Threading;
using Prim.Core;

namespace Prim.Runtime
{
    /// <summary>
    /// Represents a script instance managed by the scheduler.
    /// </summary>
    public sealed class ScriptInstance
    {
        private static int _nextId = 0;

        /// <summary>
        /// Unique identifier for this script instance.
        /// </summary>
        public int Id { get; }

        /// <summary>
        /// User-provided name for identification.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The current state of the script.
        /// </summary>
        public ScriptState State { get; internal set; }

        /// <summary>
        /// The saved continuation state when suspended.
        /// </summary>
        public ContinuationState ContinuationState { get; internal set; }

        /// <summary>
        /// The entry point function for the script.
        /// </summary>
        public Func<object> EntryPoint { get; }

        /// <summary>
        /// The last yielded value from the script.
        /// </summary>
        public object LastYieldedValue { get; internal set; }

        /// <summary>
        /// The result value when completed.
        /// </summary>
        public object Result { get; internal set; }

        /// <summary>
        /// The exception if the script failed.
        /// </summary>
        public Exception Error { get; internal set; }

        /// <summary>
        /// Number of times this script has yielded.
        /// </summary>
        public int YieldCount { get; internal set; }

        /// <summary>
        /// Number of instructions executed (approximate, based on ticks).
        /// </summary>
        public long TickCount { get; internal set; }

        /// <summary>
        /// Priority level (higher = more time slices).
        /// </summary>
        public int Priority
        {
            get => _priority;
            set => _priority = Math.Max(1, value);
        }

        private int _priority = 1;

        /// <summary>
        /// User data associated with this script.
        /// </summary>
        public object Tag { get; set; }

        public ScriptInstance(Func<object> entryPoint, string name = null)
        {
            Id = Interlocked.Increment(ref _nextId);
            Name = name ?? $"Script-{Id}";
            EntryPoint = entryPoint ?? throw new ArgumentNullException(nameof(entryPoint));
            State = ScriptState.Ready;
        }

        public override string ToString()
        {
            return $"{Name} [{State}]";
        }
    }

    /// <summary>
    /// State of a script in the scheduler.
    /// </summary>
    public enum ScriptState
    {
        /// <summary>
        /// Script is ready to run but hasn't started.
        /// </summary>
        Ready,

        /// <summary>
        /// Script is currently executing.
        /// </summary>
        Running,

        /// <summary>
        /// Script yielded and is waiting for its next time slice.
        /// </summary>
        Suspended,

        /// <summary>
        /// Script is waiting for an external event.
        /// </summary>
        Waiting,

        /// <summary>
        /// Script completed successfully.
        /// </summary>
        Completed,

        /// <summary>
        /// Script terminated due to an error.
        /// </summary>
        Failed
    }

    /// <summary>
    /// Event arguments for script state changes.
    /// </summary>
    public sealed class ScriptEventArgs : EventArgs
    {
        public ScriptInstance Script { get; }
        public ScriptState PreviousState { get; }

        public ScriptEventArgs(ScriptInstance script, ScriptState previousState)
        {
            Script = script;
            PreviousState = previousState;
        }
    }

    /// <summary>
    /// Cooperative scheduler for running multiple scripts on a single thread.
    /// Implements round-robin scheduling with support for priorities.
    /// </summary>
    public sealed class ScriptScheduler
    {
        private readonly List<ScriptInstance> _scripts = new List<ScriptInstance>();
        private readonly Queue<ScriptInstance> _runQueue = new Queue<ScriptInstance>();
        private readonly object _lock = new object();
        private readonly ContinuationRunner _runner = new ContinuationRunner();

        private volatile bool _running;
        private volatile bool _stopRequested;
        private int _instructionBudgetPerSlice = ScriptContext.DefaultBudget;

        /// <summary>
        /// Gets or sets the instruction budget per time slice.
        /// Scripts yield when their budget is exhausted, ensuring fair scheduling.
        /// </summary>
        public int InstructionBudgetPerSlice
        {
            get => _instructionBudgetPerSlice;
            set => _instructionBudgetPerSlice = Math.Max(1, value);
        }

        /// <summary>
        /// Gets whether the scheduler is currently running.
        /// </summary>
        public bool IsRunning => _running;

        /// <summary>
        /// Gets the number of scripts in the scheduler.
        /// </summary>
        public int ScriptCount
        {
            get
            {
                lock (_lock)
                {
                    return _scripts.Count;
                }
            }
        }

        /// <summary>
        /// Gets the number of runnable scripts.
        /// </summary>
        public int RunnableCount
        {
            get
            {
                lock (_lock)
                {
                    return _runQueue.Count;
                }
            }
        }

        /// <summary>
        /// Raised when a script changes state.
        /// </summary>
        public event EventHandler<ScriptEventArgs> ScriptStateChanged;

        /// <summary>
        /// Raised when a script yields a value.
        /// </summary>
        public event EventHandler<ScriptEventArgs> ScriptYielded;

        /// <summary>
        /// Raised when a script completes.
        /// </summary>
        public event EventHandler<ScriptEventArgs> ScriptCompleted;

        /// <summary>
        /// Raised when a script fails with an exception.
        /// </summary>
        public event EventHandler<ScriptEventArgs> ScriptFailed;

        /// <summary>
        /// Adds a script to the scheduler.
        /// </summary>
        /// <param name="entryPoint">The script's entry point function.</param>
        /// <param name="name">Optional name for the script.</param>
        /// <returns>The created script instance.</returns>
        public ScriptInstance AddScript(Func<object> entryPoint, string name = null)
        {
            var script = new ScriptInstance(entryPoint, name);

            lock (_lock)
            {
                _scripts.Add(script);
                _runQueue.Enqueue(script);
            }

            return script;
        }

        /// <summary>
        /// Adds a script with typed result.
        /// </summary>
        public ScriptInstance AddScript<T>(Func<T> entryPoint, string name = null)
        {
            return AddScript(() => (object)entryPoint(), name);
        }

        /// <summary>
        /// Removes a script from the scheduler.
        /// </summary>
        public bool RemoveScript(ScriptInstance script)
        {
            if (script == null) return false;

            lock (_lock)
            {
                return _scripts.Remove(script);
            }
        }

        /// <summary>
        /// Gets all scripts in the scheduler.
        /// </summary>
        public IReadOnlyList<ScriptInstance> GetScripts()
        {
            lock (_lock)
            {
                return _scripts.ToArray();
            }
        }

        /// <summary>
        /// Runs the scheduler for one tick (processes one script's time slice).
        /// </summary>
        /// <returns>True if work was done, false if all scripts are idle/complete.</returns>
        public bool Tick()
        {
            ScriptInstance script;

            lock (_lock)
            {
                if (_runQueue.Count == 0)
                {
                    RebuildRunQueue();
                    if (_runQueue.Count == 0)
                        return false;
                }

                script = _runQueue.Dequeue();
            }

            RunScript(script);

            // Re-enqueue if still runnable
            lock (_lock)
            {
                if (script.State == ScriptState.Suspended)
                {
                    // Add back to queue based on priority
                    for (int i = 0; i < script.Priority; i++)
                    {
                        _runQueue.Enqueue(script);
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Runs the scheduler continuously until all scripts complete or Stop is called.
        /// </summary>
        public void Run()
        {
            _running = true;
            _stopRequested = false;

            try
            {
                while (!_stopRequested)
                {
                    if (!Tick())
                    {
                        // No work to do - check if all scripts are done
                        lock (_lock)
                        {
                            bool allDone = true;
                            foreach (var s in _scripts)
                            {
                                if (s.State != ScriptState.Completed &&
                                    s.State != ScriptState.Failed)
                                {
                                    allDone = false;
                                    break;
                                }
                            }
                            if (allDone) break;
                        }

                        // Brief pause to avoid busy waiting
                        Thread.Sleep(1);
                    }
                }
            }
            finally
            {
                _running = false;
            }
        }

        /// <summary>
        /// Runs the scheduler for a specified number of ticks.
        /// </summary>
        public void RunFor(int ticks)
        {
            for (int i = 0; i < ticks && !_stopRequested; i++)
            {
                if (!Tick()) break;
            }
        }

        /// <summary>
        /// Stops the scheduler.
        /// </summary>
        public void Stop()
        {
            _stopRequested = true;
        }

        /// <summary>
        /// Wakes up a waiting script.
        /// </summary>
        public void WakeScript(ScriptInstance script, object resumeValue = null)
        {
            if (script == null) return;

            lock (_lock)
            {
                if (script.State == ScriptState.Waiting)
                {
                    var previousState = script.State;
                    script.State = ScriptState.Suspended;
                    script.LastYieldedValue = resumeValue;
                    _runQueue.Enqueue(script);
                    OnScriptStateChanged(script, previousState);
                }
            }
        }

        /// <summary>
        /// Puts a script into waiting state.
        /// </summary>
        public void SuspendScript(ScriptInstance script)
        {
            if (script == null) return;

            lock (_lock)
            {
                if (script.State == ScriptState.Suspended)
                {
                    var previousState = script.State;
                    script.State = ScriptState.Waiting;
                    OnScriptStateChanged(script, previousState);
                }
            }
        }

        private void RunScript(ScriptInstance script)
        {
            var previousState = script.State;
            script.State = ScriptState.Running;
            OnScriptStateChanged(script, previousState);

            try
            {
                ContinuationResult<object> result;

                if (script.ContinuationState != null)
                {
                    // Resume from saved state with fresh budget
                    var context = new ScriptContext(script.ContinuationState, script.LastYieldedValue);
                    context.ResetBudget(_instructionBudgetPerSlice);

                    result = context.RunWith(() =>
                        _runner.Resume(
                            script.ContinuationState,
                            script.LastYieldedValue,
                            script.EntryPoint));

                    // Track instructions consumed
                    script.TickCount += _instructionBudgetPerSlice - context.InstructionBudget;
                }
                else
                {
                    // First run with instruction budget
                    var context = new ScriptContext();
                    context.ResetBudget(_instructionBudgetPerSlice);

                    result = context.RunWith(() =>
                        _runner.Run(script.EntryPoint));

                    // Track instructions consumed
                    script.TickCount += _instructionBudgetPerSlice - context.InstructionBudget;
                }

                if (result.IsCompleted)
                {
                    var completed = (ContinuationResult<object>.Completed)result;
                    script.Result = completed.Value;
                    previousState = script.State;
                    script.State = ScriptState.Completed;
                    script.ContinuationState = null;
                    OnScriptStateChanged(script, previousState);
                    OnScriptCompleted(script, previousState);
                }
                else
                {
                    var suspended = (ContinuationResult<object>.Suspended)result;
                    script.ContinuationState = suspended.State;
                    script.LastYieldedValue = suspended.YieldedValue;
                    script.YieldCount++;
                    previousState = script.State;
                    script.State = ScriptState.Suspended;
                    OnScriptStateChanged(script, previousState);
                    OnScriptYielded(script, previousState);
                }
            }
            catch (Exception ex)
            {
                script.Error = ex;
                previousState = script.State;
                script.State = ScriptState.Failed;
                script.ContinuationState = null;
                OnScriptStateChanged(script, previousState);
                OnScriptFailed(script, previousState);
            }
        }

        private void RebuildRunQueue()
        {
            foreach (var script in _scripts)
            {
                if (script.State == ScriptState.Ready ||
                    script.State == ScriptState.Suspended)
                {
                    for (int i = 0; i < script.Priority; i++)
                    {
                        _runQueue.Enqueue(script);
                    }
                }
            }
        }

        private void OnScriptStateChanged(ScriptInstance script, ScriptState previousState)
        {
            ScriptStateChanged?.Invoke(this, new ScriptEventArgs(script, previousState));
        }

        private void OnScriptYielded(ScriptInstance script, ScriptState previousState)
        {
            ScriptYielded?.Invoke(this, new ScriptEventArgs(script, previousState));
        }

        private void OnScriptCompleted(ScriptInstance script, ScriptState previousState)
        {
            ScriptCompleted?.Invoke(this, new ScriptEventArgs(script, previousState));
        }

        private void OnScriptFailed(ScriptInstance script, ScriptState previousState)
        {
            ScriptFailed?.Invoke(this, new ScriptEventArgs(script, previousState));
        }
    }
}
