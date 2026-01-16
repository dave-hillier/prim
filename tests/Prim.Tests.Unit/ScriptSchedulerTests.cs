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
            var script1 = scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                executionOrder.Add("S1-Start");
                context.RequestYield();
                context.HandleYieldPoint(0);
                executionOrder.Add("S1-End");
                return 1;
            }, "Script1");

            var script2 = scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                executionOrder.Add("S2-Start");
                context.RequestYield();
                context.HandleYieldPoint(0);
                executionOrder.Add("S2-End");
                return 2;
            }, "Script2");

            // Run until completion
            for (int i = 0; i < 10 && scheduler.RunnableCount > 0; i++)
            {
                scheduler.Tick();
            }

            // Both scripts should have started before either finished
            var s1Start = executionOrder.IndexOf("S1-Start");
            var s2Start = executionOrder.IndexOf("S2-Start");
            var s1End = executionOrder.IndexOf("S1-End");
            var s2End = executionOrder.IndexOf("S2-End");

            Assert.True(s1Start < s1End);
            Assert.True(s2Start < s2End);
        }

        [Fact]
        public void ScriptInstance_TracksYieldCount()
        {
            var scheduler = new ScriptScheduler();
            var yieldCount = 0;

            var script = scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                for (int i = 0; i < 3; i++)
                {
                    context.RequestYield();
                    context.HandleYieldPoint(i);
                    yieldCount++;
                }
                return 42;
            });

            // Run until completion
            while (script.State != ScriptState.Completed && script.State != ScriptState.Failed)
            {
                scheduler.Tick();
            }

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

            scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                while (true)
                {
                    iterations++;
                    context.RequestYield();
                    context.HandleYieldPoint(0);
                    if (iterations >= 5)
                    {
                        scheduler.Stop();
                    }
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
            var resumed = false;

            var script = scheduler.AddScript(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                context.RequestYield();
                context.HandleYieldPoint(0);
                resumed = true;
                return 42;
            });

            // Run once to get it to suspend
            scheduler.Tick();
            Assert.Equal(ScriptState.Suspended, script.State);

            // Put it in waiting state
            scheduler.SuspendScript(script);
            Assert.Equal(ScriptState.Waiting, script.State);

            // Tick should not run the waiting script
            scheduler.Tick();
            Assert.False(resumed);

            // Wake it up
            scheduler.WakeScript(script);
            Assert.Equal(ScriptState.Suspended, script.State);

            // Now it should run
            scheduler.Tick();
            Assert.True(resumed);
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
