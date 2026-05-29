using System;
using System.Threading.Tasks;
using Prim.Core;
using Prim.Runtime;
using Xunit;

namespace Prim.Tests.Unit
{
    /// <summary>
    /// Tests that demonstrate existing bugs in the Runtime layer.
    /// Each test is expected to FAIL, proving the bug exists.
    /// </summary>
    public class BugProof_RuntimeTests
    {
        #region BUG: RemoveScript does not remove from _runQueue

        /// <summary>
        /// ScriptScheduler.RemoveScript only removes from _scripts list but does NOT
        /// remove the script from _runQueue. A removed script will still be dequeued
        /// and executed on the next Tick().
        ///
        /// See ScriptScheduler.cs lines 254-262: only _scripts.Remove is called.
        /// The _runQueue (populated in AddScript at line 237) retains the entry.
        /// </summary>
        [Fact]
        public void Bug_RemoveScript_Does_Not_Remove_From_RunQueue()
        {
            var scheduler = new ScriptScheduler();
            int executionCount = 0;

            var script = scheduler.AddScript<object>(() =>
            {
                executionCount++;
                return "done";
            }, "test");

            // Remove the script before it ever runs
            bool removed = scheduler.RemoveScript(script);
            Assert.True(removed);
            Assert.Equal(0, scheduler.ScriptCount);

            // BUG: Script was removed from _scripts but NOT from _runQueue.
            // Tick() will dequeue the script from _runQueue and call RunScript on it.
            scheduler.Tick();

            // If the removal were correct, the script should never have executed.
            Assert.Equal(0, executionCount);
        }

        #endregion

        #region BUG: Priority > 1 causes re-execution of completed scripts

        /// <summary>
        /// When a script with Priority > 1 suspends, Tick() re-enqueues it
        /// Priority times (ScriptScheduler.cs lines 302-306). On the next Tick,
        /// one copy is dequeued and executed; if the script completes, the remaining
        /// (Priority - 1) stale copies stay in _runQueue. Subsequent Ticks dequeue
        /// those stale copies and call RunScript, which blindly sets State = Running
        /// and re-invokes the entry point on an already-completed script.
        /// </summary>
        [Fact]
        public void Bug_Priority_Stale_Entries_Cause_Reexecution_Of_Completed_Script()
        {
            var scheduler = new ScriptScheduler();
            int executionCount = 0;

            var script = scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                executionCount++;

                if (executionCount == 1)
                {
                    // First invocation: yield so the script suspends
                    context.RequestYield();
                    context.HandleYieldPoint(0);
                }

                // Second invocation onward: return immediately to complete
                return (object)"done";
            }, "test");
            script.Priority = 3;

            // Tick 1: Script runs, increments executionCount to 1, yields, goes Suspended.
            // Post-run re-enqueue adds 3 copies to _runQueue (Priority = 3).
            scheduler.Tick();
            Assert.Equal(ScriptState.Suspended, script.State);
            Assert.Equal(1, executionCount);

            // Tick 2: Dequeues one copy, runs again (executionCount = 2), script completes.
            // 2 stale copies remain in _runQueue.
            scheduler.Tick();
            Assert.Equal(ScriptState.Completed, script.State);
            Assert.Equal(2, executionCount);

            // Tick 3: BUG -- dequeues a stale copy and re-executes the completed script.
            scheduler.Tick();

            // executionCount should still be 2, but the stale queue entry causes
            // RunScript to run the entry point again.
            Assert.Equal(2, executionCount);
        }

        #endregion

        #region BUG: SuspendScript leaves stale entries in _runQueue

        /// <summary>
        /// ScriptScheduler.SuspendScript (lines 396-409) changes a Suspended script's
        /// state to Waiting, but does NOT remove any existing entries for that script
        /// from _runQueue. The stale entry is dequeued on the next Tick(), and
        /// RunScript executes a script that the caller intended to be waiting/paused.
        /// </summary>
        [Fact]
        public void Bug_SuspendScript_Leaves_Stale_RunQueue_Entries()
        {
            var scheduler = new ScriptScheduler();
            int executionCount = 0;

            var script = scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                executionCount++;
                // Always yield so the script suspends each tick
                context.RequestYield();
                context.HandleYieldPoint(0);
                return (object)42;
            }, "test");

            // Tick 1: Script runs (executionCount = 1), yields, goes Suspended.
            // Post-run re-enqueue adds 1 copy to _runQueue (Priority = 1).
            scheduler.Tick();
            Assert.Equal(ScriptState.Suspended, script.State);
            Assert.Equal(1, executionCount);

            // Move to Waiting -- the API intends for this script to stop executing.
            scheduler.SuspendScript(script);
            Assert.Equal(ScriptState.Waiting, script.State);

            // BUG: The entry from the post-yield re-enqueue is still in _runQueue.
            // Tick dequeues it and calls RunScript on the Waiting script.
            scheduler.Tick();

            // If SuspendScript properly removed _runQueue entries, executionCount
            // would remain 1. Instead the script runs again.
            Assert.Equal(1, executionCount);
        }

        #endregion

        #region BUG: GetRootFrame / GetStackDepth infinite loop on circular chain

        /// <summary>
        /// HostFrameRecord.GetStackDepth (HostFrameRecord.cs lines 48-58) and
        /// ContinuationRunner.GetRootFrame (ContinuationRunner.cs lines 155-164)
        /// walk the Caller chain with a simple while-loop and no cycle detection.
        /// A circular Caller chain causes an infinite loop.
        ///
        /// GetStackDepth is public and directly testable.
        /// </summary>
        [Fact]
        public void Bug_GetStackDepth_Infinite_Loop_On_Circular_Frame_Chain()
        {
            var frame1 = new HostFrameRecord(100, 0, new object[0]);
            var frame2 = new HostFrameRecord(200, 0, new object[0], frame1);
            // Create a cycle: frame1 -> frame2 -> frame1 -> ...
            frame1.Caller = frame2;

            // BUG: GetStackDepth follows Caller without cycle detection.
            // This will loop forever (or until integer overflow / OOM).
            var task = Task.Run(() => frame1.GetStackDepth());
            bool completed = task.Wait(TimeSpan.FromSeconds(2));

            Assert.True(completed,
                "GetStackDepth hung due to circular frame chain -- no cycle detection");
        }

        /// <summary>
        /// ContinuationRunner.GetRootFrame (private) is reached through Resume.
        /// With a circular Caller chain, Resume hangs forever.
        /// </summary>
        [Fact]
        public void Bug_GetRootFrame_Infinite_Loop_On_Circular_Frame_Chain()
        {
            var registry = new EntryPointRegistry();
            var runner = new ContinuationRunner { EntryPoints = registry };

            var frame1 = new HostFrameRecord(100, 0, new object[0]);
            var frame2 = new HostFrameRecord(200, 0, new object[0], frame1);
            frame1.Caller = frame2; // cycle

            var state = new ContinuationState(frame2);
            var continuation = new Continuation<int>(state);

            // BUG: GetRootFrame follows Caller chain without cycle detection.
            var task = Task.Run(() =>
            {
                try { runner.Resume(continuation); }
                catch { /* ignore other errors once it escapes the loop */ }
            });

            bool completed = task.Wait(TimeSpan.FromSeconds(2));

            Assert.True(completed,
                "Resume hung due to circular frame chain -- no cycle detection in GetRootFrame");
        }

        #endregion

        #region BUG: ContinuationRunner.Run overwrites scheduler's budget context

        /// <summary>
        /// ScriptScheduler.RunScript (lines 437-446) creates a ScriptContext with
        /// the scheduler's InstructionBudgetPerSlice and wraps the runner call in
        /// context.RunWith. However, ContinuationRunner.Run (lines 35-41) creates
        /// its OWN new ScriptContext() with DefaultBudget and calls RunWithContext,
        /// which sets ScriptContext.Current to the runner's context. The computation
        /// therefore sees DefaultBudget, not the scheduler's custom budget.
        ///
        /// Additionally, the scheduler's TickCount tracking (line 446) reads
        /// InstructionBudget from its own context, which was never decremented
        /// (the budget decrements happened on the runner's context). TickCount is
        /// therefore always 0 for first-run scripts.
        /// </summary>
        [Fact]
        public void Bug_Scheduler_Budget_Overwritten_By_ContinuationRunner()
        {
            var scheduler = new ScriptScheduler();
            scheduler.InstructionBudgetPerSlice = 500;

            int? observedBudget = null;

            var script = scheduler.AddScript(() =>
            {
                // Capture the budget the computation actually sees
                observedBudget = ScriptContext.Current?.InstructionBudget;
                return (object)42;
            }, "test");

            scheduler.Tick();

            // The scheduler intended the computation to run with budget = 500.
            // But ContinuationRunner.Run creates a new ScriptContext with
            // DefaultBudget (1000), so the computation actually sees 1000.
            Assert.NotNull(observedBudget);
            Assert.Equal(500, observedBudget.Value);
        }

        /// <summary>
        /// Because the scheduler's context budget is never decremented (the actual
        /// budget decrements happen on the runner's internal context), TickCount
        /// tracking is always zero for first-run scripts that complete immediately.
        /// </summary>
        [Fact]
        public void Bug_Scheduler_TickCount_Always_Zero_Due_To_Context_Overwrite()
        {
            var scheduler = new ScriptScheduler();
            scheduler.InstructionBudgetPerSlice = 500;

            var script = scheduler.AddScript(() =>
            {
                // Consume some budget via yield-point checking
                var context = ScriptContext.Current;
                context.HandleYieldPointWithBudget(0, 100);
                context.HandleYieldPointWithBudget(1, 150);
                return (object)"done";
            }, "test");

            scheduler.Tick();
            Assert.Equal(ScriptState.Completed, script.State);

            // TickCount should reflect instruction cost (250), but the budget
            // decrements happened on the runner's internal context, not the
            // scheduler's. The scheduler computes:
            //   TickCount += 500 - schedulerContext.InstructionBudget
            // Since schedulerContext.InstructionBudget was never touched, this is 0.
            Assert.True(script.TickCount > 0,
                $"Expected TickCount > 0 after executing instructions, but got {script.TickCount}");
        }

        #endregion
    }
}
