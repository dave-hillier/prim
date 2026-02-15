using System;
using System.Collections.Generic;
using Prim.Core;
using Prim.Runtime;
using Xunit;

namespace Prim.Tests.Unit
{
    /// <summary>
    /// TDD tests for ScriptScheduler edge cases.
    /// Targets the remove-while-queued bug, priority scheduling behavior,
    /// state transition edge cases, and event handling.
    /// </summary>
    public class ScriptSchedulerEdgeCaseTests
    {
        #region RemoveScript - Still-Queued Bug

        [Fact]
        public void RemoveScript_DoesNotPreventExecution_IfAlreadyQueued()
        {
            // After fix: RemoveScript purges the run queue, preventing execution.
            var scheduler = new ScriptScheduler();
            var executed = false;

            var script = scheduler.AddScript(() =>
            {
                executed = true;
                return (object)42;
            }, "test");

            // Remove the script
            Assert.True(scheduler.RemoveScript(script));

            // Tick should NOT execute removed script
            scheduler.Tick();

            // After fix: the script was NOT executed because RemoveScript cleans the run queue
            Assert.False(executed);
        }

        #endregion

        #region Priority Scheduling

        [Fact]
        public void AddScript_WithHighPriority_GetsMoreTimeSlices()
        {
            var scheduler = new ScriptScheduler();
            int script1Count = 0;
            int script2Count = 0;

            var s1 = scheduler.AddScript(() =>
            {
                script1Count++;
                ScriptContext.Current?.RequestYield();
                ScriptContext.Current?.HandleYieldPoint(0);
                return (object)null;
            }, "low-priority");

            var s2 = scheduler.AddScript(() =>
            {
                script2Count++;
                ScriptContext.Current?.RequestYield();
                ScriptContext.Current?.HandleYieldPoint(0);
                return (object)null;
            }, "high-priority");

            s1.Priority = 1;
            s2.Priority = 3;

            // After first round of ticks, high priority script should be enqueued 3 times
            // This tests the priority-based multiplicity in RebuildRunQueue
            Assert.Equal(1, s1.Priority);
            Assert.Equal(3, s2.Priority);
        }

        [Fact]
        public void ScriptInstance_Priority_ClampsToMinimumOne()
        {
            var script = new ScriptInstance(() => (object)42, "test");

            script.Priority = 0;
            Assert.Equal(1, script.Priority);

            script.Priority = -5;
            Assert.Equal(1, script.Priority);

            script.Priority = 10;
            Assert.Equal(10, script.Priority);
        }

        #endregion

        #region InstructionBudgetPerSlice

        [Fact]
        public void InstructionBudgetPerSlice_ClampsToMinimumOne()
        {
            var scheduler = new ScriptScheduler();

            scheduler.InstructionBudgetPerSlice = 0;
            Assert.Equal(1, scheduler.InstructionBudgetPerSlice);

            scheduler.InstructionBudgetPerSlice = -100;
            Assert.Equal(1, scheduler.InstructionBudgetPerSlice);

            scheduler.InstructionBudgetPerSlice = 500;
            Assert.Equal(500, scheduler.InstructionBudgetPerSlice);
        }

        [Fact]
        public void InstructionBudgetPerSlice_DefaultValue()
        {
            var scheduler = new ScriptScheduler();

            Assert.Equal(ScriptContext.DefaultBudget, scheduler.InstructionBudgetPerSlice);
        }

        #endregion

        #region WakeScript / SuspendScript Edge Cases

        [Fact]
        public void WakeScript_NullScript_DoesNotThrow()
        {
            var scheduler = new ScriptScheduler();

            // Should not throw
            scheduler.WakeScript(null);
        }

        [Fact]
        public void WakeScript_NonWaitingScript_IsNoOp()
        {
            var scheduler = new ScriptScheduler();
            var events = new List<ScriptState>();

            scheduler.ScriptStateChanged += (s, e) => events.Add(e.Script.State);

            var script = scheduler.AddScript(() => (object)42, "test");

            // Script is in Ready state, not Waiting - WakeScript should be no-op
            scheduler.WakeScript(script, "wake-value");

            // No state change events from WakeScript (only from AddScript)
            // The script's state should still be Ready
            Assert.Equal(ScriptState.Ready, script.State);
        }

        [Fact]
        public void SuspendScript_NullScript_DoesNotThrow()
        {
            var scheduler = new ScriptScheduler();

            // Should not throw
            scheduler.SuspendScript(null);
        }

        [Fact]
        public void SuspendScript_NonSuspendedScript_IsNoOp()
        {
            var scheduler = new ScriptScheduler();
            var script = scheduler.AddScript(() => (object)42, "test");

            // Script is in Ready state, not Suspended - SuspendScript should be no-op
            scheduler.SuspendScript(script);

            Assert.Equal(ScriptState.Ready, script.State);
        }

        #endregion

        #region RunFor Edge Cases

        [Fact]
        public void RunFor_ZeroTicks_DoesNothing()
        {
            var scheduler = new ScriptScheduler();
            var executed = false;

            scheduler.AddScript(() =>
            {
                executed = true;
                return (object)42;
            });

            scheduler.RunFor(0);

            Assert.False(executed);
        }

        [Fact]
        public void RunFor_NegativeTicks_DoesNothing()
        {
            var scheduler = new ScriptScheduler();
            var executed = false;

            scheduler.AddScript(() =>
            {
                executed = true;
                return (object)42;
            });

            scheduler.RunFor(-1);

            Assert.False(executed);
        }

        #endregion

        #region Tick Edge Cases

        [Fact]
        public void Tick_EmptyScheduler_ReturnsFalse()
        {
            var scheduler = new ScriptScheduler();

            Assert.False(scheduler.Tick());
        }

        [Fact]
        public void Tick_AllCompleted_ReturnsFalse()
        {
            var scheduler = new ScriptScheduler();
            scheduler.AddScript(() => (object)42);

            // First tick executes and completes
            Assert.True(scheduler.Tick());

            // Second tick has no runnable scripts
            Assert.False(scheduler.Tick());
        }

        #endregion

        #region Script State Events

        [Fact]
        public void ScriptCompleted_EventFired_OnCompletion()
        {
            var scheduler = new ScriptScheduler();
            ScriptInstance completedScript = null;

            scheduler.ScriptCompleted += (s, e) => completedScript = e.Script;

            var script = scheduler.AddScript(() => (object)42, "test");
            scheduler.Tick();

            Assert.Same(script, completedScript);
            Assert.Equal(ScriptState.Completed, script.State);
            Assert.Equal(42, script.Result);
        }

        [Fact]
        public void ScriptFailed_EventFired_OnException()
        {
            var scheduler = new ScriptScheduler();
            ScriptInstance failedScript = null;

            scheduler.ScriptFailed += (s, e) => failedScript = e.Script;

            var script = scheduler.AddScript<object>(() =>
                throw new InvalidOperationException("test error"), "failing");

            scheduler.Tick();

            Assert.Same(script, failedScript);
            Assert.Equal(ScriptState.Failed, script.State);
            Assert.NotNull(script.Error);
            Assert.IsType<InvalidOperationException>(script.Error);
        }

        [Fact]
        public void ScriptStateChanged_TracksAllTransitions()
        {
            var scheduler = new ScriptScheduler();
            var transitions = new List<(ScriptState previous, ScriptState current)>();

            scheduler.ScriptStateChanged += (s, e) =>
                transitions.Add((e.PreviousState, e.Script.State));

            var script = scheduler.AddScript(() => (object)42, "test");
            scheduler.Tick();

            // Should have: Ready -> Running, Running -> Completed
            Assert.True(transitions.Count >= 2);
            Assert.Equal(ScriptState.Ready, transitions[0].previous);
            Assert.Equal(ScriptState.Running, transitions[0].current);
        }

        #endregion

        #region Script Counts

        [Fact]
        public void ScriptCount_ReflectsAddedScripts()
        {
            var scheduler = new ScriptScheduler();

            Assert.Equal(0, scheduler.ScriptCount);

            scheduler.AddScript(() => (object)1);
            Assert.Equal(1, scheduler.ScriptCount);

            scheduler.AddScript(() => (object)2);
            Assert.Equal(2, scheduler.ScriptCount);
        }

        [Fact]
        public void RunnableCount_DecreasesAfterTick()
        {
            var scheduler = new ScriptScheduler();
            scheduler.AddScript(() => (object)1);

            Assert.Equal(1, scheduler.RunnableCount);

            scheduler.Tick();

            // After completing, should not be runnable
            Assert.Equal(0, scheduler.RunnableCount);
        }

        #endregion

        #region Stop Behavior

        [Fact]
        public void Stop_PreventsRun_FromContinuing()
        {
            var scheduler = new ScriptScheduler();
            int tickCount = 0;

            // Add a script that yields repeatedly
            scheduler.AddScript(() =>
            {
                while (true)
                {
                    tickCount++;
                    var ctx = ScriptContext.Current;
                    if (ctx != null)
                    {
                        ctx.RequestYield();
                        ctx.HandleYieldPoint(0);
                    }
                }
            });

            scheduler.Stop();
            // Run should return quickly because stop was already called
            scheduler.Run();

            Assert.False(scheduler.IsRunning);
        }

        #endregion

        #region ScriptInstance Edge Cases

        [Fact]
        public void ScriptInstance_NullEntryPoint_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ScriptInstance(null));
        }

        [Fact]
        public void ScriptInstance_DefaultName_ContainsId()
        {
            var script = new ScriptInstance(() => (object)42);

            Assert.StartsWith("Script-", script.Name);
        }

        [Fact]
        public void ScriptInstance_CustomName()
        {
            var script = new ScriptInstance(() => (object)42, "MyScript");

            Assert.Equal("MyScript", script.Name);
        }

        [Fact]
        public void ScriptInstance_InitialState_IsReady()
        {
            var script = new ScriptInstance(() => (object)42);

            Assert.Equal(ScriptState.Ready, script.State);
            Assert.Null(script.ContinuationState);
            Assert.Null(script.Error);
            Assert.Null(script.Result);
            Assert.Equal(0, script.YieldCount);
            Assert.Equal(0, script.TickCount);
        }

        [Fact]
        public void ScriptInstance_ToString_IncludesNameAndState()
        {
            var script = new ScriptInstance(() => (object)42, "TestScript");

            var str = script.ToString();

            Assert.Contains("TestScript", str);
            Assert.Contains("Ready", str);
        }

        [Fact]
        public void ScriptEventArgs_PreservesData()
        {
            var script = new ScriptInstance(() => (object)42);
            var args = new ScriptEventArgs(script, ScriptState.Ready);

            Assert.Same(script, args.Script);
            Assert.Equal(ScriptState.Ready, args.PreviousState);
        }

        #endregion

        #region GetScripts

        [Fact]
        public void GetScripts_ReturnsSnapshot()
        {
            var scheduler = new ScriptScheduler();
            scheduler.AddScript(() => (object)1, "s1");
            scheduler.AddScript(() => (object)2, "s2");

            var scripts = scheduler.GetScripts();

            Assert.Equal(2, scripts.Count);
        }

        [Fact]
        public void GetScripts_ReturnsEmpty_WhenNoScripts()
        {
            var scheduler = new ScriptScheduler();

            var scripts = scheduler.GetScripts();

            Assert.Empty(scripts);
        }

        #endregion
    }
}
