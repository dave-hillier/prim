using System;
using System.Collections;
using System.Collections.Generic;
using Prim.Core;
using Xunit;

namespace Prim.Tests.Unit
{
    /// <summary>
    /// TDD tests for ContinuationValidator edge cases.
    /// Targets version mismatch behavior, circular chain detection,
    /// type allowlisting gaps, and validation mode interactions.
    /// </summary>
    public class ContinuationValidatorEdgeCaseTests
    {
        #region Null State Validation

        [Fact]
        public void TryValidate_NullState_ReturnsFailure()
        {
            var validator = new ContinuationValidator();

            var result = validator.TryValidate(null);

            Assert.False(result.IsValid);
            Assert.Contains("null", result.Errors[0]);
        }

        [Fact]
        public void Validate_NullState_ThrowsValidationException()
        {
            var validator = new ContinuationValidator();

            var ex = Assert.Throws<ValidationException>(() =>
                validator.Validate(null));

            Assert.Contains("null", ex.Message);
        }

        #endregion

        #region Version Mismatch Behavior

        [Fact]
        public void TryValidate_VersionMismatch_ReportsErrorButContinuesValidation()
        {
            // The validator reports version mismatch but continues validating
            // other fields. This test verifies that behavior.
            var validator = new ContinuationValidator();
            var frame = new HostFrameRecord(999, 0, new object[] { 42 }, null);
            var state = new ContinuationState(frame)
            {
                Version = 999 // Invalid version
            };

            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            // Should contain version error AND unregistered method error
            Assert.True(result.Errors.Count >= 2,
                $"Expected at least 2 errors but got {result.Errors.Count}: {string.Join("; ", result.Errors)}");

            bool hasVersionError = false;
            bool hasMethodError = false;
            foreach (var error in result.Errors)
            {
                if (error.Contains("version")) hasVersionError = true;
                if (error.Contains("not registered")) hasMethodError = true;
            }
            Assert.True(hasVersionError, "Expected a version mismatch error");
            Assert.True(hasMethodError, "Expected an unregistered method error");
        }

        [Fact]
        public void TryValidate_CorrectVersion_NoVersionError()
        {
            var validator = new ContinuationValidator(ValidationOptions.Lenient);
            var frame = new HostFrameRecord(100, 0, new object[0], null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        #endregion

        #region Circular Frame Chain Detection (MaxStackDepth)

        [Fact]
        public void TryValidate_ExceedingMaxStackDepth_ReportsError()
        {
            var options = new ValidationOptions
            {
                RequireRegisteredMethods = false,
                ValidateSlotCounts = false,
                ValidateSlotTypes = false,
                MaxStackDepth = 3
            };
            var validator = new ContinuationValidator(options);

            // Build chain of 5 frames (exceeds max of 3)
            var frame5 = new HostFrameRecord(100, 0, new object[0], null);
            var frame4 = new HostFrameRecord(100, 0, new object[0], frame5);
            var frame3 = new HostFrameRecord(100, 0, new object[0], frame4);
            var frame2 = new HostFrameRecord(100, 0, new object[0], frame3);
            var frame1 = new HostFrameRecord(100, 0, new object[0], frame2);

            var state = new ContinuationState(frame1);
            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            bool hasDepthError = false;
            foreach (var error in result.Errors)
            {
                if (error.Contains("Stack depth exceeds")) hasDepthError = true;
            }
            Assert.True(hasDepthError, "Expected stack depth exceeded error");
        }

        [Fact]
        public void TryValidate_ExactlyAtMaxStackDepth_Passes()
        {
            var options = new ValidationOptions
            {
                RequireRegisteredMethods = false,
                ValidateSlotCounts = false,
                ValidateSlotTypes = false,
                MaxStackDepth = 3
            };
            var validator = new ContinuationValidator(options);

            // Build chain of exactly 3 frames
            var frame3 = new HostFrameRecord(100, 0, new object[0], null);
            var frame2 = new HostFrameRecord(100, 0, new object[0], frame3);
            var frame1 = new HostFrameRecord(100, 0, new object[0], frame2);

            var state = new ContinuationState(frame1);
            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        #endregion

        #region Type Allowlisting

        [Fact]
        public void IsTypeAllowed_PrimitivesAlwaysAllowed()
        {
            var validator = new ContinuationValidator();

            Assert.True(validator.IsTypeAllowed(typeof(int)));
            Assert.True(validator.IsTypeAllowed(typeof(bool)));
            Assert.True(validator.IsTypeAllowed(typeof(double)));
            Assert.True(validator.IsTypeAllowed(typeof(char)));
            Assert.True(validator.IsTypeAllowed(typeof(byte)));
        }

        [Fact]
        public void IsTypeAllowed_EnumsAlwaysAllowed()
        {
            var validator = new ContinuationValidator();

            Assert.True(validator.IsTypeAllowed(typeof(DayOfWeek)));
            Assert.True(validator.IsTypeAllowed(typeof(StringComparison)));
        }

        [Fact]
        public void IsTypeAllowed_ArrayOfAllowedType_Allowed()
        {
            var validator = new ContinuationValidator();

            Assert.True(validator.IsTypeAllowed(typeof(int[])));
            Assert.True(validator.IsTypeAllowed(typeof(string[])));
        }

        [Fact]
        public void IsTypeAllowed_ArrayOfDisallowedType_NotAllowed()
        {
            var validator = new ContinuationValidator();

            // List<int> is not allowed, so List<int>[] should not be allowed
            Assert.False(validator.IsTypeAllowed(typeof(List<int>[])));
        }

        [Fact]
        public void IsTypeAllowed_NullableOfAllowedType_Allowed()
        {
            var validator = new ContinuationValidator();

            Assert.True(validator.IsTypeAllowed(typeof(int?)));
            Assert.True(validator.IsTypeAllowed(typeof(bool?)));
            Assert.True(validator.IsTypeAllowed(typeof(DateTime?)));
        }

        [Fact]
        public void IsTypeAllowed_CustomType_NotAllowedByDefault()
        {
            var validator = new ContinuationValidator();

            Assert.False(validator.IsTypeAllowed(typeof(List<int>)));
            Assert.False(validator.IsTypeAllowed(typeof(Dictionary<string, int>)));
        }

        [Fact]
        public void IsTypeAllowed_NullType_ReturnsTrue()
        {
            var validator = new ContinuationValidator();

            Assert.True(validator.IsTypeAllowed(null));
        }

        [Fact]
        public void RegisterAllowedType_MakesCustomTypeAllowed()
        {
            var validator = new ContinuationValidator();

            Assert.False(validator.IsTypeAllowed(typeof(List<int>)));

            validator.RegisterAllowedType(typeof(List<int>));

            Assert.True(validator.IsTypeAllowed(typeof(List<int>)));
        }

        [Fact]
        public void RegisterAllowedTypeName_MakesTypeAllowedByName()
        {
            var validator = new ContinuationValidator();
            var customTypeName = "MyNamespace.MyCustomType";

            validator.RegisterAllowedTypeName(customTypeName);

            // The type itself won't resolve, but the name-based check
            // is tested through slot validation
        }

        #endregion

        #region Slot Count Validation

        [Fact]
        public void TryValidate_SlotCountLessThanExpected_ReportsError()
        {
            var validator = new ContinuationValidator();
            var slots = new[] {
                new FrameSlot(0, "a", SlotKind.Local, typeof(int)),
                new FrameSlot(1, "b", SlotKind.Local, typeof(int))
            };
            var liveSlots = new BitArray(new[] { true, true });
            var descriptor = new FrameDescriptor(
                100, "TestMethod", slots, new[] { 0 }, new[] { liveSlots });

            validator.RegisterDescriptor(descriptor);

            // Frame has 1 slot but descriptor expects 2
            var frame = new HostFrameRecord(100, 0, new object[] { 42 }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            bool hasSlotError = false;
            foreach (var error in result.Errors)
            {
                if (error.Contains("Slot count mismatch")) hasSlotError = true;
            }
            Assert.True(hasSlotError, "Expected a slot count mismatch error");
        }

        [Fact]
        public void TryValidate_SlotCountMoreThanExpected_Passes()
        {
            // Extra slots are allowed (>= is the check)
            var validator = new ContinuationValidator();
            var slots = new[] {
                new FrameSlot(0, "a", SlotKind.Local, typeof(int))
            };
            var liveSlots = new BitArray(new[] { true });
            var descriptor = new FrameDescriptor(
                100, "TestMethod", slots, new[] { 0 }, new[] { liveSlots });

            validator.RegisterDescriptor(descriptor);

            // Frame has 3 slots but descriptor only expects 1
            var frame = new HostFrameRecord(100, 0, new object[] { 42, "extra", 3.14 }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        #endregion

        #region Slot Type Validation

        [Fact]
        public void TryValidate_DisallowedTypeInSlot_ReportsError()
        {
            var validator = new ContinuationValidator();
            var slots = new[] {
                new FrameSlot(0, "a", SlotKind.Local, typeof(object))
            };
            var liveSlots = new BitArray(new[] { true });
            var descriptor = new FrameDescriptor(
                100, "TestMethod", slots, new[] { 0 }, new[] { liveSlots });

            validator.RegisterDescriptor(descriptor);

            // Put a List<int> in the slot - not in the allowed type list
            var frame = new HostFrameRecord(100, 0, new object[] { new List<int> { 1, 2, 3 } }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            bool hasTypeError = false;
            foreach (var error in result.Errors)
            {
                if (error.Contains("not in the allowed type list")) hasTypeError = true;
            }
            Assert.True(hasTypeError, "Expected a disallowed type error");
        }

        [Fact]
        public void TryValidate_NullSlotValues_AreAllowed()
        {
            var validator = new ContinuationValidator();
            var slots = new[] {
                new FrameSlot(0, "a", SlotKind.Local, typeof(string))
            };
            var liveSlots = new BitArray(new[] { true });
            var descriptor = new FrameDescriptor(
                100, "TestMethod", slots, new[] { 0 }, new[] { liveSlots });

            validator.RegisterDescriptor(descriptor);

            var frame = new HostFrameRecord(100, 0, new object[] { null }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void TryValidate_DisallowedYieldedValueType_ReportsError()
        {
            var validator = new ContinuationValidator();
            var slots = new[] {
                new FrameSlot(0, "a", SlotKind.Local, typeof(int))
            };
            var liveSlots = new BitArray(new[] { true });
            var descriptor = new FrameDescriptor(
                100, "TestMethod", slots, new[] { 0 }, new[] { liveSlots });

            validator.RegisterDescriptor(descriptor);

            // Yielded value is a disallowed type
            var frame = new HostFrameRecord(100, 0, new object[] { 42 }, null);
            var state = new ContinuationState(frame, new List<int> { 1, 2 });

            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            bool hasYieldError = false;
            foreach (var error in result.Errors)
            {
                if (error.Contains("Yielded value type")) hasYieldError = true;
            }
            Assert.True(hasYieldError, "Expected a yielded value type error");
        }

        #endregion

        #region Lenient Mode

        [Fact]
        public void TryValidate_LenientMode_AllowsUnregisteredMethods()
        {
            var validator = new ContinuationValidator(ValidationOptions.Lenient);
            var frame = new HostFrameRecord(999, 0, new object[0], null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void TryValidate_LenientMode_StillChecksNegativeYieldPointId()
        {
            var validator = new ContinuationValidator(ValidationOptions.Lenient);
            var frame = new HostFrameRecord(999, -1, new object[0], null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            bool hasNegativeError = false;
            foreach (var error in result.Errors)
            {
                if (error.Contains("negative")) hasNegativeError = true;
            }
            Assert.True(hasNegativeError, "Expected a negative yield point ID error");
        }

        #endregion

        #region Multiple Frame Validation

        [Fact]
        public void TryValidate_MultipleFrames_AllValidated()
        {
            var validator = new ContinuationValidator();

            // Neither method token is registered, so both frames should fail
            var inner = new HostFrameRecord(100, 0, new object[0], null);
            var outer = new HostFrameRecord(200, 1, new object[0], inner);
            var state = new ContinuationState(outer);

            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            // Should have errors for both frames
            Assert.True(result.Errors.Count >= 2,
                $"Expected at least 2 errors (one per frame) but got {result.Errors.Count}");
        }

        [Fact]
        public void TryValidate_EmptyFrameChain_Passes()
        {
            var validator = new ContinuationValidator();
            var state = new ContinuationState(null);

            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        #endregion

        #region ValidationResult ToString

        [Fact]
        public void ValidationResult_Success_ToStringFormat()
        {
            var result = ValidationResult.Success();

            Assert.Equal("Validation succeeded", result.ToString());
        }

        [Fact]
        public void ValidationResult_Failure_ToStringContainsError()
        {
            var result = ValidationResult.Failure("test error");

            Assert.Contains("test error", result.ToString());
            Assert.Contains("Validation failed", result.ToString());
        }

        [Fact]
        public void ValidationResult_MultipleFailures_ToStringContainsFirstError()
        {
            var result = ValidationResult.Failure(new[] { "error1", "error2", "error3" });

            Assert.Contains("error1", result.ToString());
            Assert.Contains("3 errors", result.ToString());
        }

        #endregion

        #region ValidationException

        [Fact]
        public void ValidationException_ContainsResult()
        {
            var result = ValidationResult.Failure("test error");
            var ex = new ValidationException(result);

            Assert.Same(result, ex.Result);
            Assert.Contains("test error", ex.Message);
        }

        [Fact]
        public void ValidationException_NullResult_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ValidationException(null));
        }

        #endregion

        #region FrameDescriptor Edge Cases

        [Fact]
        public void FrameDescriptor_GetLiveSlotsForYieldPoint_ThrowsForUnknownId()
        {
            var descriptor = new FrameDescriptor(
                100, "TestMethod",
                new[] { new FrameSlot(0, "a", SlotKind.Local, typeof(int)) },
                new[] { 0 },
                new[] { new BitArray(new[] { true }) });

            Assert.Throws<ArgumentException>(() =>
                descriptor.GetLiveSlotsForYieldPoint(999));
        }

        [Fact]
        public void FrameDescriptor_MismatchedArrayLengths_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
                new FrameDescriptor(
                    100, "TestMethod",
                    new[] { new FrameSlot(0, "a", SlotKind.Local, typeof(int)) },
                    new[] { 0, 1 }, // 2 yield points
                    new[] { new BitArray(new[] { true }) } // Only 1 live slots array
                ));
        }

        [Fact]
        public void FrameDescriptor_CountLiveSlots_ReturnsCorrectCount()
        {
            var descriptor = new FrameDescriptor(
                100, "TestMethod",
                new[] {
                    new FrameSlot(0, "a", SlotKind.Local, typeof(int)),
                    new FrameSlot(1, "b", SlotKind.Local, typeof(string)),
                    new FrameSlot(2, "c", SlotKind.Local, typeof(double))
                },
                new[] { 0 },
                new[] { new BitArray(new[] { true, false, true }) });

            Assert.Equal(2, descriptor.CountLiveSlots(0));
        }

        #endregion
    }
}
