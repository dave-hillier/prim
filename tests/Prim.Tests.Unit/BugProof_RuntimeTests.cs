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
        /// Direct Resume used to walk the Caller chain (GetRootFrame) to find the
        /// entry frame. It now uses the chain head, but a circular chain must still
        /// not hang Resume.
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

    }
}
