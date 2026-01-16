using System;
using System.Collections.Generic;
using System.Threading;
using Prim.Core;
using Prim.Runtime;
using Xunit;

namespace Prim.Tests.Unit
{
    public class ScriptSchedulerTests
    {
        [Fact]
        public void AddScript_CreatesScriptInstance()
        {
            var scheduler = new ScriptScheduler();

            var script = scheduler.AddScript(() => 42, "TestScript");

            Assert.NotNull(script);
            Assert.Equal("TestScript", script.Name);
            Assert.Equal(ScriptState.Ready, script.State);
        }

        [Fact]
        public void AddScript_IncrementsScriptCount()
        {
            var scheduler = new ScriptScheduler();

            scheduler.AddScript(() => 1);
            scheduler.AddScript(() => 2);
            scheduler.AddScript(() => 3);

            Assert.Equal(3, scheduler.ScriptCount);
        }

        [Fact]
        public void RemoveScript_DecreasesCount()
        {
            var scheduler = new ScriptScheduler();
            var script = scheduler.AddScript(() => 42);

            var removed = scheduler.RemoveScript(script);

            Assert.True(removed);
            Assert.Equal(0, scheduler.ScriptCount);
        }

        [Fact]
        public void RemoveScript_ReturnsFalseForUnknownScript()
        {
            var scheduler = new ScriptScheduler();
            var script = new ScriptInstance(() => 42);

            var removed = scheduler.RemoveScript(script);

            Assert.False(removed);
        }

        [Fact]
        public void GetScripts_ReturnsAllScripts()
        {
            var scheduler = new ScriptScheduler();
            scheduler.AddScript(() => 1, "Script1");
            scheduler.AddScript(() => 2, "Script2");

            var scripts = scheduler.GetScripts();

            Assert.Equal(2, scripts.Count);
        }

        [Fact]
        public void Tick_ExecutesScriptAndCompletes()
        {
            var scheduler = new ScriptScheduler();
            var executed = false;
            var script = scheduler.AddScript(() =>
            {
                executed = true;
                return 42;
            });

            scheduler.Tick();

            Assert.True(executed);
            Assert.Equal(ScriptState.Completed, script.State);
            Assert.Equal(42, script.Result);
        }

        [Fact]
        public void Tick_ReturnsFalseWhenNoScripts()
        {
            var scheduler = new ScriptScheduler();

            var result = scheduler.Tick();

            Assert.False(result);
        }

        [Fact]
        public void Tick_RoundRobinsBetweenScripts()
        {
            var scheduler = new ScriptScheduler();
            var executionOrder = new List<string>();

            // Create scripts that yield and track execution
            // Note: Without IL transformation, scripts restart from the beginning on resume
            // So we track execution order at the start of each tick
            var script1 = scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                executionOrder.Add("S1");
                context.RequestYield();
                context.HandleYieldPoint(0);
                return 1;
            }, "Script1");

            var script2 = scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                executionOrder.Add("S2");
                context.RequestYield();
                context.HandleYieldPoint(0);
                return 2;
            }, "Script2");

            // Run for a fixed number of ticks
            scheduler.RunFor(4);

            // Both scripts should be interleaved - S1 and S2 should alternate
            Assert.True(executionOrder.Count >= 4);
            Assert.Contains("S1", executionOrder);
            Assert.Contains("S2", executionOrder);

            // Verify interleaving: S1 and S2 should both run before one runs twice
            var firstS1 = executionOrder.IndexOf("S1");
            var firstS2 = executionOrder.IndexOf("S2");
            Assert.True(firstS1 >= 0 && firstS2 >= 0);
            Assert.True(Math.Abs(firstS1 - firstS2) <= 1, "Scripts should be interleaved");
        }

        [Fact]
        public void ScriptInstance_TracksYieldCount()
        {
            var scheduler = new ScriptScheduler();

            // Note: Without IL transformation, scripts restart from the beginning on resume.
            // Each tick causes a yield, so we test that YieldCount increments correctly.
            var script = scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                context.RequestYield();
                context.HandleYieldPoint(0);
                return 42;
            });

            // Run for a fixed number of ticks
            scheduler.RunFor(3);

            // Each tick should increment YieldCount
            Assert.Equal(3, script.YieldCount);
        }

        [Fact]
        public void ScriptCompleted_EventRaised()
        {
            var scheduler = new ScriptScheduler();
            ScriptInstance completedScript = null;

            scheduler.ScriptCompleted += (s, e) => completedScript = e.Script;

            var script = scheduler.AddScript(() => 42);
            scheduler.Tick();

            Assert.Same(script, completedScript);
        }

        [Fact]
        public void ScriptFailed_EventRaised()
        {
            var scheduler = new ScriptScheduler();
            ScriptInstance failedScript = null;

            scheduler.ScriptFailed += (s, e) => failedScript = e.Script;

            var script = scheduler.AddScript<int>(() => throw new InvalidOperationException("Test error"));
            scheduler.Tick();

            Assert.Same(script, failedScript);
            Assert.Equal(ScriptState.Failed, script.State);
            Assert.IsType<InvalidOperationException>(script.Error);
        }

        [Fact]
        public void ScriptYielded_EventRaised()
        {
            var scheduler = new ScriptScheduler();
            var yieldedScripts = new List<ScriptInstance>();

            scheduler.ScriptYielded += (s, e) => yieldedScripts.Add(e.Script);

            var script = scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                context.RequestYield();
                context.HandleYieldPoint(0);
                return 42;
            });

            scheduler.Tick(); // Should yield

            Assert.Single(yieldedScripts);
            Assert.Same(script, yieldedScripts[0]);
        }

        [Fact]
        public void Stop_StopsRunningScheduler()
        {
            var scheduler = new ScriptScheduler();
            var iterations = 0;

            // Note: Without IL transformation, code after HandleYieldPoint is never reached
            // because the script restarts from the beginning on resume.
            // So we call Stop() BEFORE yielding when iterations reaches 5.
            scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                while (true)
                {
                    iterations++;
                    if (iterations >= 5)
                    {
                        scheduler.Stop();
                    }
                    context.RequestYield();
                    context.HandleYieldPoint(0);
                }
                return 0;
            });

            scheduler.Run();

            Assert.True(iterations >= 5);
            Assert.False(scheduler.IsRunning);
        }

        [Fact]
        public void RunFor_ExecutesSpecifiedTicks()
        {
            var scheduler = new ScriptScheduler();
            var tickCount = 0;

            scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                while (true)
                {
                    tickCount++;
                    context.RequestYield();
                    context.HandleYieldPoint(0);
                }
                return 0;
            });

            scheduler.RunFor(5);

            Assert.Equal(5, tickCount);
        }

        [Fact]
        public void Priority_AffectsSchedulingFrequency()
        {
            var scheduler = new ScriptScheduler();
            var lowPriorityTicks = 0;
            var highPriorityTicks = 0;

            var lowPriority = scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                while (true)
                {
                    lowPriorityTicks++;
                    context.RequestYield();
                    context.HandleYieldPoint(0);
                }
                return 0;
            }, "LowPriority");
            lowPriority.Priority = 1;

            var highPriority = scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                while (true)
                {
                    highPriorityTicks++;
                    context.RequestYield();
                    context.HandleYieldPoint(0);
                }
                return 0;
            }, "HighPriority");
            highPriority.Priority = 3;

            // Run for a fixed number of ticks
            scheduler.RunFor(20);

            // Higher priority should get more time slices
            Assert.True(highPriorityTicks > lowPriorityTicks,
                $"High: {highPriorityTicks}, Low: {lowPriorityTicks}");
        }

        [Fact]
        public void WakeScript_ResumesWaitingScript()
        {
            var scheduler = new ScriptScheduler();
            var runCount = 0;

            var script = scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                runCount++;
                context.RequestYield();
                context.HandleYieldPoint(0);
                return 42;
            });

            // Run once - script yields and goes to Suspended state
            scheduler.Tick();
            Assert.Equal(1, runCount);
            Assert.Equal(ScriptState.Suspended, script.State);

            // Put it in waiting state
            scheduler.SuspendScript(script);
            Assert.Equal(ScriptState.Waiting, script.State);

            // WakeScript should change state back to Suspended and add to queue
            scheduler.WakeScript(script);
            Assert.Equal(ScriptState.Suspended, script.State);

            // Verify script was re-added to run queue
            // The queue will have the script from the original yield + the wake
            Assert.True(scheduler.RunnableCount >= 1, "Script should be in run queue after wake");
        }

        [Fact]
        public void ScriptInstance_HasUniqueIds()
        {
            var scripts = new List<ScriptInstance>();
            for (int i = 0; i < 10; i++)
            {
                scripts.Add(new ScriptInstance(() => i));
            }

            var ids = new HashSet<int>(scripts.ConvertAll(s => s.Id));
            Assert.Equal(10, ids.Count);
        }

        [Fact]
        public void ScriptInstance_TagCanBeSet()
        {
            var script = new ScriptInstance(() => 42);
            script.Tag = "Custom Data";

            Assert.Equal("Custom Data", script.Tag);
        }
    }
}
