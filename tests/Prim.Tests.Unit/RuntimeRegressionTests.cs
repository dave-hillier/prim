using System;
using System.Threading.Tasks;
using Prim.Core;
using Prim.Runtime;
using Xunit;

namespace Prim.Tests.Unit
{
    /// <summary>
    /// Regression tests for bugs found and fixed in the Runtime layer.
    /// Each test is named after the original bug and asserts the corrected
    /// behaviour, so it fails if the bug comes back.
    /// </summary>
    public class RuntimeRegressionTests
    {
        #region Fixed bug: GetRootFrame / GetStackDepth infinite loop on circular chain

        /// <summary>
        /// HostFrameRecord.GetStackDepth (HostFrameRecord.cs lines 48-58) and
        /// ContinuationRunner.GetRootFrame (ContinuationRunner.cs lines 155-164)
        /// walk the Caller chain with a simple while-loop and no cycle detection.
        /// A circular Caller chain causes an infinite loop.
        ///
        /// GetStackDepth is public and directly testable.
        /// </summary>
        [Fact]
        public async Task Regression_GetStackDepth_Infinite_Loop_On_Circular_Frame_Chain()
        {
            var frame1 = new HostFrameRecord(100, 0, new object[0]);
            var frame2 = new HostFrameRecord(200, 0, new object[0], frame1);
            // Create a cycle: frame1 -> frame2 -> frame1 -> ...
            frame1.Caller = frame2;

            // Fixed bug: GetStackDepth follows Caller without cycle detection.
            // This will loop forever (or until integer overflow / OOM).
            var task = Task.Run(() => frame1.GetStackDepth());
            bool completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2))) == task;

            Assert.True(completed,
                "GetStackDepth hung due to circular frame chain -- no cycle detection");
        }

        /// <summary>
        /// Direct Resume used to walk the Caller chain (GetRootFrame) to find the
        /// entry frame. It now uses the chain head, but a circular chain must still
        /// not hang Resume.
        /// </summary>
        [Fact]
        public async Task Regression_GetRootFrame_Infinite_Loop_On_Circular_Frame_Chain()
        {
            var registry = new EntryPointRegistry();
            var runner = new ContinuationRunner { EntryPoints = registry };

            var frame1 = new HostFrameRecord(100, 0, new object[0]);
            var frame2 = new HostFrameRecord(200, 0, new object[0], frame1);
            frame1.Caller = frame2; // cycle

            var state = new ContinuationState(frame2);
            var continuation = new Continuation<int>(state);

            var task = Task.Run(() =>
            {
                try { runner.Resume(continuation); }
                catch { /* ignore other errors once it escapes the loop */ }
            });

            bool completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2))) == task;

            Assert.True(completed,
                "Resume hung due to circular frame chain -- no cycle detection in GetRootFrame");
        }

        #endregion

        #region Fixed bug: Action-registered entry points could not be resumed

        [Fact]
        public void Regression_Resume_Works_For_Action_Registered_EntryPoint()
        {
            var ran = false;
            var registry = new EntryPointRegistry();
            registry.Register(4242, () => { ran = true; });
            var runner = new ContinuationRunner { EntryPoints = registry };

            var state = new ContinuationState(new HostFrameRecord(4242, 0, new object[0]));
            var result = runner.Resume(new Continuation<Prim.Core.Unit>(state));

            Assert.True(ran);
            Assert.True(result.IsCompleted);
        }

        #endregion

        #region Fixed bug: Resume could not set the instruction budget

        [Fact]
        public void Regression_Runner_InstructionBudget_Applies_To_Run_And_Resume()
        {
            var registry = new EntryPointRegistry();
            int seenOnResume = -1;
            registry.Register(4243, () => { seenOnResume = ScriptContext.Current.InstructionBudget; return 1; });
            var runner = new ContinuationRunner { EntryPoints = registry, InstructionBudget = 7 };

            var state = new ContinuationState(new HostFrameRecord(4243, 0, new object[0]));
            runner.Resume(new Continuation<int>(state));
            Assert.Equal(7, seenOnResume);

            int seenOnRun = -1;
            runner.Run(() => { seenOnRun = ScriptContext.Current.InstructionBudget; return 1; });
            Assert.Equal(7, seenOnRun);
        }

        [Fact]
        public void Regression_Runner_InstructionBudget_Default_Is_Unchanged()
        {
            var registry = new EntryPointRegistry();
            int seen = -1;
            registry.Register(4244, () => { seen = ScriptContext.Current.InstructionBudget; return 1; });
            var runner = new ContinuationRunner { EntryPoints = registry };

            runner.Resume(new Continuation<int>(new ContinuationState(new HostFrameRecord(4244, 0, new object[0]))));
            Assert.Equal(new ScriptContext().InstructionBudget, seen);
        }

        #endregion

    }
}
