using System;
using Prim.Core;
using Xunit;

namespace Prim.Tests.Unit
{
    /// <summary>
    /// TDD tests for StableHash edge cases and core type boundary conditions.
    /// Targets hash stability, null handling, and ContinuationState/HostFrameRecord
    /// edge cases that existing tests don't cover.
    /// </summary>
    public class StableHashCoreTypesEdgeCaseTests
    {
        #region StableHash.ComputeFnv1a Edge Cases

        [Fact]
        public void ComputeFnv1a_Null_ReturnsZero()
        {
            Assert.Equal(0, StableHash.ComputeFnv1a(null));
        }

        [Fact]
        public void ComputeFnv1a_EmptyString_ReturnsNonZero()
        {
            // FNV-1a of empty string should be the offset basis, not zero
            var hash = StableHash.ComputeFnv1a("");

            // FNV-1a offset basis is 2166136261 = 0x811C9DC5
            // which as int is -2128831035
            Assert.NotEqual(0, hash);
        }

        [Fact]
        public void ComputeFnv1a_SameInput_SameOutput()
        {
            var hash1 = StableHash.ComputeFnv1a("test");
            var hash2 = StableHash.ComputeFnv1a("test");

            Assert.Equal(hash1, hash2);
        }

        [Fact]
        public void ComputeFnv1a_DifferentInputs_DifferentOutputs()
        {
            var hash1 = StableHash.ComputeFnv1a("hello");
            var hash2 = StableHash.ComputeFnv1a("world");

            Assert.NotEqual(hash1, hash2);
        }

        [Fact]
        public void ComputeFnv1a_SingleCharacterDifference_DifferentOutputs()
        {
            var hash1 = StableHash.ComputeFnv1a("test1");
            var hash2 = StableHash.ComputeFnv1a("test2");

            Assert.NotEqual(hash1, hash2);
        }

        [Fact]
        public void ComputeFnv1a_LongString_DoesNotOverflow()
        {
            // FNV-1a uses unchecked arithmetic, so long strings should work
            var longStr = new string('x', 10000);
            var hash = StableHash.ComputeFnv1a(longStr);

            // Should produce some hash without throwing
            Assert.IsType<int>(hash);
        }

        [Fact]
        public void ComputeFnv1a_UnicodeCharacters_HandledCorrectly()
        {
            var hash1 = StableHash.ComputeFnv1a("hello \u00e9");
            var hash2 = StableHash.ComputeFnv1a("hello \u00e8");

            Assert.NotEqual(hash1, hash2);
        }

        #endregion

        #region StableHash.Combine Edge Cases

        [Fact]
        public void Combine_Null_ReturnsZero()
        {
            Assert.Equal(0, StableHash.Combine(null));
        }

        [Fact]
        public void Combine_Empty_ReturnsZero()
        {
            Assert.Equal(0, StableHash.Combine(new int[0]));
        }

        [Fact]
        public void Combine_SingleValue_ReturnsCombination()
        {
            var hash = StableHash.Combine(42);

            Assert.NotEqual(0, hash);
            Assert.NotEqual(42, hash); // Should be mixed, not pass-through
        }

        [Fact]
        public void Combine_OrderMatters()
        {
            var hash1 = StableHash.Combine(1, 2, 3);
            var hash2 = StableHash.Combine(3, 2, 1);

            Assert.NotEqual(hash1, hash2);
        }

        [Fact]
        public void Combine_DeterministicAcrossInvocations()
        {
            var hash1 = StableHash.Combine(100, 200, 300);
            var hash2 = StableHash.Combine(100, 200, 300);

            Assert.Equal(hash1, hash2);
        }

        [Fact]
        public void Combine_Zeros_ProducesNonZero()
        {
            // Combining zeros: hash = ((17 << 5) + 17) ^ 0 = 561
            var hash = StableHash.Combine(0, 0, 0);

            Assert.NotEqual(0, hash);
        }

        #endregion

        #region StableHash.GenerateMethodToken Edge Cases

        [Fact]
        public void GenerateMethodToken_NullParams_Works()
        {
            var token = StableHash.GenerateMethodToken("Type", "Method", null);

            Assert.NotEqual(0, token);
        }

        [Fact]
        public void GenerateMethodToken_EmptyParams_Works()
        {
            var token = StableHash.GenerateMethodToken("Type", "Method");

            Assert.NotEqual(0, token);
        }

        [Fact]
        public void GenerateMethodToken_NullTypeName_Works()
        {
            // ComputeFnv1a(null) returns 0, so this should work
            var token = StableHash.GenerateMethodToken(null, "Method");

            Assert.IsType<int>(token);
        }

        [Fact]
        public void GenerateMethodToken_Stable_AcrossInvocations()
        {
            var token1 = StableHash.GenerateMethodToken("NS.Type", "Method", "int", "string");
            var token2 = StableHash.GenerateMethodToken("NS.Type", "Method", "int", "string");

            Assert.Equal(token1, token2);
        }

        [Fact]
        public void GenerateMethodToken_DifferentOverloads_DifferentTokens()
        {
            var token1 = StableHash.GenerateMethodToken("Type", "Method", "int");
            var token2 = StableHash.GenerateMethodToken("Type", "Method", "string");
            var token3 = StableHash.GenerateMethodToken("Type", "Method", "int", "int");

            Assert.NotEqual(token1, token2);
            Assert.NotEqual(token1, token3);
            Assert.NotEqual(token2, token3);
        }

        [Fact]
        public void GenerateMethodToken_ParamOrder_Matters()
        {
            var token1 = StableHash.GenerateMethodToken("Type", "Method", "int", "string");
            var token2 = StableHash.GenerateMethodToken("Type", "Method", "string", "int");

            Assert.NotEqual(token1, token2);
        }

        #endregion

        #region HostFrameRecord Edge Cases

        [Fact]
        public void HostFrameRecord_DefaultConstructor_AllDefaults()
        {
            var frame = new HostFrameRecord();

            Assert.Equal(0, frame.MethodToken);
            Assert.Equal(0, frame.YieldPointId);
            Assert.Null(frame.Slots);
            Assert.Null(frame.Caller);
        }

        [Fact]
        public void HostFrameRecord_GetStackDepth_SingleFrame()
        {
            var frame = new HostFrameRecord(100, 0, new object[0], null);

            Assert.Equal(1, frame.GetStackDepth());
        }

        [Fact]
        public void HostFrameRecord_GetStackDepth_DeepChain()
        {
            HostFrameRecord current = null;
            for (int i = 0; i < 100; i++)
            {
                current = new HostFrameRecord(i, 0, new object[0], current);
            }

            Assert.Equal(100, current.GetStackDepth());
        }

        [Fact]
        public void HostFrameRecord_ToString_IncludesSlotCount()
        {
            var frame = new HostFrameRecord(100, 5, new object[] { 1, 2, 3 }, null);

            var str = frame.ToString();

            Assert.Contains("100", str);
            Assert.Contains("5", str);
            Assert.Contains("3", str);
        }

        [Fact]
        public void HostFrameRecord_ToString_NullSlots_ShowsZero()
        {
            var frame = new HostFrameRecord(100, 5, null, null);

            var str = frame.ToString();

            Assert.Contains("Slots=0", str);
        }

        #endregion

        #region ContinuationState Edge Cases

        [Fact]
        public void ContinuationState_DefaultConstructor_SetsCurrentVersion()
        {
            var state = new ContinuationState();

            Assert.Equal(ContinuationState.CurrentVersion, state.Version);
            Assert.Null(state.StackHead);
            Assert.Null(state.YieldedValue);
        }

        [Fact]
        public void ContinuationState_GetStackDepth_NullHead_ReturnsZero()
        {
            var state = new ContinuationState(null);

            Assert.Equal(0, state.GetStackDepth());
        }

        [Fact]
        public void ContinuationState_GetStackDepth_WithFrames()
        {
            var inner = new HostFrameRecord(100, 0, new object[0], null);
            var outer = new HostFrameRecord(200, 1, new object[0], inner);
            var state = new ContinuationState(outer);

            Assert.Equal(2, state.GetStackDepth());
        }

        [Fact]
        public void ContinuationState_ToString_IncludesVersionAndDepth()
        {
            var frame = new HostFrameRecord(100, 0, new object[0], null);
            var state = new ContinuationState(frame);

            var str = state.ToString();

            Assert.Contains("v1", str);
            Assert.Contains("Depth=1", str);
        }

        #endregion

        #region ContinuationResult Edge Cases

        [Fact]
        public void ContinuationResult_Completed_IsCompleted_True()
        {
            var result = new ContinuationResult<int>.Completed(42);

            Assert.True(result.IsCompleted);
            Assert.False(result.IsSuspended);
            Assert.Equal(42, result.Value);
        }

        [Fact]
        public void ContinuationResult_Suspended_IsCompleted_False()
        {
            var state = new ContinuationState(null);
            var result = new ContinuationResult<int>.Suspended("yielded", state);

            Assert.False(result.IsCompleted);
            Assert.True(result.IsSuspended);
            Assert.Equal("yielded", result.YieldedValue);
            Assert.Same(state, result.State);
        }

        [Fact]
        public void ContinuationResult_Suspended_NullState_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ContinuationResult<int>.Suspended("value", null));
        }

        [Fact]
        public void ContinuationResult_Match_Func_CompletedCase()
        {
            ContinuationResult<int> result = new ContinuationResult<int>.Completed(42);

            var output = result.Match(
                c => $"completed:{c.Value}",
                s => $"suspended:{s.YieldedValue}");

            Assert.Equal("completed:42", output);
        }

        [Fact]
        public void ContinuationResult_Match_Func_SuspendedCase()
        {
            var state = new ContinuationState(null);
            ContinuationResult<int> result = new ContinuationResult<int>.Suspended("hello", state);

            var output = result.Match(
                c => $"completed:{c.Value}",
                s => $"suspended:{s.YieldedValue}");

            Assert.Equal("suspended:hello", output);
        }

        [Fact]
        public void ContinuationResult_Match_NullOnCompleted_Throws()
        {
            ContinuationResult<int> result = new ContinuationResult<int>.Completed(42);

            Assert.Throws<ArgumentNullException>(() =>
                result.Match<string>(null, s => "suspended"));
        }

        [Fact]
        public void ContinuationResult_Match_NullOnSuspended_Throws()
        {
            ContinuationResult<int> result = new ContinuationResult<int>.Completed(42);

            Assert.Throws<ArgumentNullException>(() =>
                result.Match<string>(c => "completed", null));
        }

        [Fact]
        public void ContinuationResult_Match_Action_CompletedCase()
        {
            ContinuationResult<int> result = new ContinuationResult<int>.Completed(42);
            int capturedValue = 0;

            result.Match(
                c => capturedValue = c.Value,
                s => { });

            Assert.Equal(42, capturedValue);
        }

        [Fact]
        public void ContinuationResult_Match_Action_SuspendedCase()
        {
            var state = new ContinuationState(null);
            ContinuationResult<int> result = new ContinuationResult<int>.Suspended("hello", state);
            object capturedValue = null;

            result.Match(
                c => { },
                s => capturedValue = s.YieldedValue);

            Assert.Equal("hello", capturedValue);
        }

        [Fact]
        public void ContinuationResult_Completed_ToString()
        {
            var result = new ContinuationResult<int>.Completed(42);

            Assert.Contains("42", result.ToString());
        }

        [Fact]
        public void ContinuationResult_Suspended_ToString()
        {
            var state = new ContinuationState(null);
            var result = new ContinuationResult<int>.Suspended("hello", state);

            var str = result.ToString();
            Assert.Contains("hello", str);
        }

        #endregion

        #region FrameSlot Edge Cases

        [Fact]
        public void FrameSlot_NullType_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new FrameSlot(0, "test", SlotKind.Local, null));
        }

        [Fact]
        public void FrameSlot_NullName_Allowed()
        {
            var slot = new FrameSlot(0, null, SlotKind.Local, typeof(int));

            Assert.Null(slot.Name);
        }

        [Fact]
        public void FrameSlot_ToString_WithName()
        {
            var slot = new FrameSlot(0, "myVar", SlotKind.Local, typeof(int));

            var str = slot.ToString();

            Assert.Contains("myVar", str);
            Assert.Contains("Local", str);
            Assert.Contains("Int32", str);
        }

        [Fact]
        public void FrameSlot_ToString_WithoutName()
        {
            var slot = new FrameSlot(2, null, SlotKind.Argument, typeof(string));

            var str = slot.ToString();

            Assert.Contains("Argument", str);
            Assert.Contains("[2]", str);
            Assert.DoesNotContain("'", str); // No name quotes
        }

        [Fact]
        public void FrameSlot_AllSlotKinds()
        {
            var local = new FrameSlot(0, "a", SlotKind.Local, typeof(int));
            var arg = new FrameSlot(0, "b", SlotKind.Argument, typeof(int));
            var eval = new FrameSlot(0, "c", SlotKind.EvalStack, typeof(int));

            Assert.Equal(SlotKind.Local, local.Kind);
            Assert.Equal(SlotKind.Argument, arg.Kind);
            Assert.Equal(SlotKind.EvalStack, eval.Kind);
        }

        #endregion

        #region SuspensionTag Edge Cases

        [Fact]
        public void SuspensionTag_NullName_DefaultsToAnonymous()
        {
            var tag = new SuspensionTag<int, Prim.Core.Unit>(null);

            Assert.Equal("anonymous", tag.Name);
        }

        [Fact]
        public void SuspensionTag_CustomName()
        {
            var tag = new SuspensionTag<string, int>("my-tag");

            Assert.Equal("my-tag", tag.Name);
        }

        [Fact]
        public void SuspensionTag_ToString_IncludesTypeNames()
        {
            var tag = new SuspensionTag<int, string>("test");

            var str = tag.ToString();

            Assert.Contains("Int32", str);
            Assert.Contains("String", str);
            Assert.Contains("test", str);
        }

        [Fact]
        public void SuspensionTags_Generator_CreatesCorrectTag()
        {
            var tag = SuspensionTags.Generator<int>();

            Assert.Equal("generator", tag.Name);
        }

        [Fact]
        public void SuspensionTags_Yield_HasCorrectName()
        {
            Assert.Equal("yield", SuspensionTags.Yield.Name);
        }

        [Fact]
        public void SuspensionTags_Async_CreatesCorrectTag()
        {
            var tag = SuspensionTags.Async<string, byte[]>();

            Assert.Equal("async", tag.Name);
        }

        #endregion

        #region Unit Type

        [Fact]
        public void Unit_Value_IsDefault()
        {
            var a = Prim.Core.Unit.Value;
            var b = Prim.Core.Unit.Value;

            Assert.Equal(a, b);
        }

        [Fact]
        public void Unit_ToString_ReturnsParens()
        {
            Assert.Equal("()", Prim.Core.Unit.Value.ToString());
        }

        #endregion
    }
}
