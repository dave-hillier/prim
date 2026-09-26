using Prim.Core;
using Prim.Runtime;
using Prim.Serialization;
using Xunit;

namespace Prim.Tests.Roslyn
{
    /// <summary>
    /// Honest end-to-end tests that actually invoke the GENERATED *_Continuable
    /// methods (produced by Prim.Roslyn.ContinuationGenerator) and drive a full
    /// suspend -> (serialize) -> resume -> complete cycle through the
    /// Prim.Runtime / Prim.Core / Prim.Serialization APIs.
    ///
    /// Unlike the legacy "GeneratedMethod_*" tests, these would FAIL if the source
    /// generator were removed, because they call the generator-produced
    /// CountToTen_Continuable() (not the hand-written CountToTen()), and they
    /// exercise the actual restore/resume state-machine logic the generator emits.
    /// </summary>
    public class GeneratedContinuableEndToEndTests
    {
        // Method token emitted by the generator for CountToTen_Continuable (see the
        // generated .g.cs). Used to assert the captured frame really belongs to the
        // generated method rather than some hand-rolled state.
        private const int CountToTenMethodToken = -718623490;

        /// <summary>
        /// Drives the GENERATED CountToTen_Continuable through a real
        /// suspend -> resume -> complete cycle (in-memory state, no serialization).
        /// This proves the generator-emitted restore/resume state machine works:
        /// it suspends at yield point 0 capturing sum=0, then resumes from the
        /// captured frame and runs the loop to completion, returning 55.
        /// </summary>
        [Fact]
        public void CountToTen_Generated_SuspendResumeCompleteCycle()
        {
            var instance = new SampleContinuableClass();
            var runner = new ContinuationRunner();

            // --- Phase 1: run the GENERATED continuable method and force a suspend ---
            // We request a yield before the method hits its first yield point, so the
            // generated catch(SuspendException) block captures the live frame (sum=0).
            var first = runner.Run(() =>
            {
                ScriptContext.Current.RequestYield();
                return instance.CountToTen_Continuable();
            });

            Assert.True(first.IsSuspended, "Generated method should suspend when a yield is requested.");
            var suspended = (ContinuationResult<int>.Suspended)first;
            Assert.NotNull(suspended.State);

            // The captured frame must be the generated method's frame.
            Assert.NotNull(suspended.State.StackHead);
            Assert.Equal(CountToTenMethodToken, suspended.State.StackHead.MethodToken);
            Assert.Equal(0, suspended.State.StackHead.YieldPointId);

            // --- Phase 2: resume the GENERATED method from the captured state ---
            // Resume re-invokes the continuable method as its own entry point; the
            // generated restore block detects IsRestoring, rehydrates sum, and runs
            // the loop to completion.
            var resumed = runner.Resume<int>(
                suspended.State,
                resumeValue: null,
                entryPoint: () => instance.CountToTen_Continuable());

            // --- Phase 3: it completed with the correct final value ---
            Assert.True(resumed.IsCompleted, "Resumed generated method should run to completion.");
            Assert.Equal(55, ((ContinuationResult<int>.Completed)resumed).Value);
        }

        /// <summary>
        /// Same cycle as above but with a JSON serialize/deserialize step inserted
        /// between suspend and resume (cross-process migration).
        ///
        /// Fixed by #40: slots now round-trip through a typed envelope
        /// (<see cref="Prim.Serialization.SlotEnvelope"/>) so the captured int slot
        /// (sum=0) is coerced back to System.Int32 on deserialize, and the generated
        /// restore block's FrameCapture.GetSlot&lt;int&gt;(...) no longer throws.
        /// </summary>
        [Fact]
        public void CountToTen_Generated_FullSuspendSerializeResumeCycle()
        {
            var instance = new SampleContinuableClass();
            var runner = new ContinuationRunner();
            var serializer = new JsonContinuationSerializer();

            // --- Phase 1: run the GENERATED continuable method and force a suspend ---
            var first = runner.Run(() =>
            {
                ScriptContext.Current.RequestYield();
                return instance.CountToTen_Continuable();
            });

            Assert.True(first.IsSuspended, "Generated method should suspend when a yield is requested.");
            var suspended = (ContinuationResult<int>.Suspended)first;
            Assert.NotNull(suspended.State);
            Assert.Equal(CountToTenMethodToken, suspended.State.StackHead.MethodToken);

            // --- Phase 2: serialize -> deserialize (simulate cross-process migration) ---
            var json = serializer.SerializeToString(suspended.State);
            Assert.NotEmpty(json);
            var restoredState = serializer.DeserializeFromString(json);
            Assert.NotNull(restoredState);
            Assert.Equal(CountToTenMethodToken, restoredState.StackHead.MethodToken);

            // --- Phase 3: resume the GENERATED method from the restored state ---
            var resumed = runner.Resume<int>(
                restoredState,
                resumeValue: null,
                entryPoint: () => instance.CountToTen_Continuable());

            // --- Phase 4: it completed with the correct final value ---
            Assert.True(resumed.IsCompleted, "Resumed generated method should run to completion.");
            Assert.Equal(55, ((ContinuationResult<int>.Completed)resumed).Value);
        }

        /// <summary>
        /// Direct resume (no entry point passed) of a two-frame continuation:
        /// OuterCallsInner_Continuable calls InnerSum_Continuable, which suspends.
        /// Capture prepends each frame as SuspendException unwinds, so StackHead is
        /// the OUTERMOST frame and HostFrameRecord.Caller points inward. Only the
        /// outer method is registered, as that is the method the host must invoke
        /// to replay the stack.
        /// </summary>
        [Fact]
        public void NestedCall_DirectResume_UsesOutermostFrameAsEntryPoint()
        {
            var instance = new SampleContinuableClass();
            var outerToken = StableHash.GenerateMethodToken(
                "Prim.Tests.Roslyn.SampleContinuableClass", "OuterCallsInner");
            var innerToken = StableHash.GenerateMethodToken(
                "Prim.Tests.Roslyn.SampleContinuableClass", "InnerSum", "int");

            var registry = new EntryPointRegistry();
            registry.Register(outerToken, () => instance.OuterCallsInner_Continuable());
            var runner = new ContinuationRunner { EntryPoints = registry };

            // The outer method has no yield point before its call, so a pending
            // yield request fires inside the inner method's loop and both frames
            // are captured.
            var first = runner.Run(() =>
            {
                ScriptContext.Current.RequestYield();
                return instance.OuterCallsInner_Continuable();
            });
            Assert.True(first.IsSuspended, "Nested call should suspend inside the inner method.");
            var suspended = (ContinuationResult<int>.Suspended)first;

            var head = suspended.State.StackHead;
            Assert.Equal(outerToken, head.MethodToken);
            Assert.NotNull(head.Caller);
            Assert.Equal(innerToken, head.Caller.MethodToken);
            Assert.Null(head.Caller.Caller);

            var resumed = runner.Resume(suspended.ToContinuation());

            Assert.True(resumed.IsCompleted, "Direct resume of the nested continuation should complete.");
            Assert.Equal(115, ((ContinuationResult<int>.Completed)resumed).Value);
        }
    }
}
