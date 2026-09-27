using System.Collections;
using System.Threading.Tasks;
using Prim.Core;
using Xunit;

namespace Prim.Tests.Unit
{
    /// <summary>
    /// Regression tests for bugs found and fixed in Prim.Core.
    /// Each test is named after the original bug and asserts the corrected
    /// behaviour, so it fails if the bug comes back.
    /// </summary>
    public class CoreRegressionTests
    {
        #region Fixed bug 1: Mutable shared ValidationOptions.Default singleton

        /// <summary>
        /// ValidationOptions.Default is a public static readonly instance, but
        /// ValidationOptions has mutable public setters. Any caller can mutate
        /// the shared singleton, affecting all subsequent consumers.
        /// File: ContinuationValidator.cs:344
        /// </summary>
        [Fact]
        public void Regression_ValidationOptions_Default_Is_Mutable_Shared_Singleton()
        {
            // Save original
            var original = ValidationOptions.Default.MaxStackDepth;
            try
            {
                // Mutate the "default" singleton
                ValidationOptions.Default.MaxStackDepth = 5;

                // A new validator using Default should get the mutated value
                // Fixed bug: This proves the shared singleton is mutable
                Assert.Equal(1000, ValidationOptions.Default.MaxStackDepth);
            }
            finally
            {
                ValidationOptions.Default.MaxStackDepth = original; // restore
            }
        }

        #endregion

        #region Fixed bug 2: GetStackDepth() has no cycle detection

        /// <summary>
        /// HostFrameRecord.GetStackDepth() walks the Caller chain until null.
        /// If a circular reference exists, it loops forever with no cycle detection.
        /// File: HostFrameRecord.cs:48-57
        /// </summary>
        [Fact]
        public async Task Regression_GetStackDepth_InfiniteLoop_On_Circular_Caller_Chain()
        {
            var frame1 = new HostFrameRecord(100, 0, new object[0]);
            var frame2 = new HostFrameRecord(200, 0, new object[0], frame1);
            frame1.Caller = frame2; // Create cycle

            // Fixed bug: This will hang forever - no cycle detection
            var task = Task.Run(() => frame1.GetStackDepth());
            bool completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2))) == task;

            Assert.True(completed, "GetStackDepth() hung due to circular Caller chain - no cycle detection");
        }

        #endregion

        #region Fixed bug 3: Off-by-one in validator MaxStackDepth check

        /// <summary>
        /// ContinuationValidator.TryValidate uses "if (frameIndex > MaxStackDepth)"
        /// after incrementing frameIndex. This should be ">=" so that the depth check
        /// fires after processing exactly MaxStackDepth frames. With ">", MaxStackDepth+1
        /// frames are fully validated before the check triggers.
        /// File: ContinuationValidator.cs:188
        /// </summary>
        [Fact]
        public void Regression_Validator_OffByOne_MaxStackDepth()
        {
            var options = new ValidationOptions
            {
                MaxStackDepth = 2,
                RequireRegisteredMethods = true,
                ValidateSlotTypes = false
            };
            var validator = new ContinuationValidator(options);
            // Intentionally do NOT register any methods.
            // Each frame that gets validated will produce a "Method token X is not registered" error,
            // letting us count exactly how many frames were processed before the depth limit kicked in.

            // Build a chain of exactly 3 frames (MaxStackDepth + 1)
            var frame1 = new HostFrameRecord(1, 0, new object[0]);
            var frame2 = new HostFrameRecord(2, 0, new object[0], frame1);
            var frame3 = new HostFrameRecord(3, 0, new object[0], frame2);
            var state = new ContinuationState(frame3);

            var result = validator.TryValidate(state);

            // Count how many frames were actually validated (each produces a "Method token" error)
            int frameValidationErrors = 0;
            foreach (var error in result.Errors)
            {
                if (error.Contains("Method token"))
                    frameValidationErrors++;
            }

            // With MaxStackDepth=2, the validator should process at most 2 frames before the
            // depth limit stops further processing. frameIndex is incremented after each frame:
            //   - After frame 0: frameIndex=1, with >= would check 1>=2 -> no
            //   - After frame 1: frameIndex=2, with >= would check 2>=2 -> YES, break
            // Fixed bug: The check uses ">" instead of ">=", so it does not fire until frameIndex=3,
            // meaning all 3 frames are validated before the depth limit triggers.
            Assert.Equal(2, frameValidationErrors);
        }

        #endregion

        #region Fixed bug 4: StableHash processes chars instead of bytes (non-standard FNV-1a)

        /// <summary>
        /// StableHash.ComputeFnv1a iterates over chars (16-bit) instead of UTF-8 bytes.
        /// Standard FNV-1a operates on bytes. For multi-byte characters the results differ.
        /// File: StableHash.cs:30-35
        /// </summary>
        [Fact]
        public void Regression_StableHash_Fnv1a_Processes_Chars_Not_Bytes()
        {
            // Standard FNV-1a processes bytes. Multi-byte characters should produce
            // different results when processed as chars vs bytes.
            // The Unicode character U+20AC (euro sign) is 3 bytes in UTF-8: 0xE2 0x82 0xAC
            // Processing as a single char (0x20AC) vs 3 separate bytes gives different hashes.

            string input = "\u20AC"; // euro sign, multi-byte in UTF-8
            int primHash = StableHash.ComputeFnv1a(input);

            // Compute standard byte-based FNV-1a
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(input);
            uint hash = 2166136261;
            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= 16777619;
            }
            int standardHash = (int)hash;

            // Fixed bug: These should be equal if using standard FNV-1a, but won't be
            // because the implementation XORs the full 16-bit char value instead of
            // individual UTF-8 bytes.
            Assert.Equal(standardHash, primHash);
        }

        #endregion

        #region Fixed bug 5: SuspensionTag factory methods create non-equal instances

        /// <summary>
        /// SuspensionTags.Generator&lt;T&gt;() creates a new SuspensionTag instance on every call.
        /// SuspensionTag has no Equals/GetHashCode override, so two tags for the same type
        /// are not equal despite being logically identical.
        /// File: SuspensionTag.cs:48-59
        /// </summary>
        [Fact]
        public void Regression_SuspensionTag_Factory_Creates_NonEqual_Instances()
        {
            var tag1 = SuspensionTags.Generator<int>();
            var tag2 = SuspensionTags.Generator<int>();

            // These are logically the same tag but will not be equal
            // Fixed bug: No Equals/GetHashCode override, and factory creates new instance each time
            Assert.Equal(tag1, tag2);
        }

        #endregion

        #region Fixed bug 6: FrameDescriptor arrays are publicly mutable

        /// <summary>
        /// FrameDescriptor stores direct references to the arrays passed to its constructor
        /// instead of defensive copies. External code can mutate the descriptor's internal
        /// state after construction, breaking encapsulation and invariants.
        /// File: FrameDescriptor.cs:27-38
        /// </summary>
        [Fact]
        public void Regression_FrameDescriptor_Mutable_Arrays_Break_Invariants()
        {
            var slots = new FrameSlot[] { new FrameSlot(0, "x", SlotKind.Local, typeof(int), true) };
            var yieldPointIds = new int[] { 0 };
            var liveSlots = new BitArray[] { new BitArray(1, true) };

            var descriptor = new FrameDescriptor(100, "Test", slots, yieldPointIds, liveSlots);

            // Mutate the array after construction
            yieldPointIds[0] = 999;

            // After fix: constructor clones the array, so external mutation is not visible.
            Assert.Equal(0, descriptor.YieldPointIds[0]);
        }

        #endregion
    }
}
