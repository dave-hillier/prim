using System;
using System.Collections;
using Prim.Core;
using Xunit;

namespace Prim.Tests.Unit
{
    public class ValidationTests
    {
        #region Basic Validation Tests

        [Fact]
        public void Validator_NullState_ReturnsFailure()
        {
            var validator = new ContinuationValidator();

            var result = validator.TryValidate(null);

            Assert.False(result.IsValid);
            Assert.Contains("null", result.Errors[0]);
        }

        [Fact]
        public void Validator_EmptyState_Succeeds()
        {
            var validator = new ContinuationValidator();

            var state = new ContinuationState(null);
            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validator_RegisteredMethod_Succeeds()
        {
            var validator = new ContinuationValidator();
            var descriptor = CreateTestDescriptor(12345, "TestMethod", yieldPoints: new[] { 0, 1, 2 }, slotCount: 2, liveSlotCount: 2);
            validator.RegisterDescriptor(descriptor);

            var frame = new HostFrameRecord(12345, 1, new object[] { 42, "test" }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validator_UnregisteredMethod_FailsByDefault()
        {
            var validator = new ContinuationValidator();

            var frame = new HostFrameRecord(99999, 0, new object[0], null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            Assert.Contains("not registered", result.Errors[0]);
        }

        [Fact]
        public void Validator_UnregisteredMethod_SucceedsWithLenientOptions()
        {
            var validator = new ContinuationValidator(ValidationOptions.Lenient);

            var frame = new HostFrameRecord(99999, 0, new object[] { 42 }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        #endregion

        #region Yield Point Validation Tests

        [Fact]
        public void Validator_InvalidYieldPointId_Fails()
        {
            var validator = new ContinuationValidator();
            var descriptor = CreateTestDescriptor(12345, "TestMethod", yieldPoints: new[] { 0, 1, 2 }, slotCount: 0, liveSlotCount: 0);
            validator.RegisterDescriptor(descriptor);

            // Use yield point 99 which doesn't exist
            var frame = new HostFrameRecord(12345, 99, new object[0], null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            Assert.Contains("Yield point ID 99", result.Errors[0]);
            Assert.Contains("not valid", result.Errors[0]);
        }

        [Fact]
        public void Validator_ValidYieldPointId_Succeeds()
        {
            var validator = new ContinuationValidator();
            var descriptor = CreateTestDescriptor(12345, "TestMethod", yieldPoints: new[] { 0, 5, 10 }, slotCount: 0, liveSlotCount: 0);
            validator.RegisterDescriptor(descriptor);

            var frame = new HostFrameRecord(12345, 5, new object[0], null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validator_NegativeYieldPointId_FailsWithoutDescriptor()
        {
            var validator = new ContinuationValidator(ValidationOptions.Lenient);

            var frame = new HostFrameRecord(12345, -1, new object[0], null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            Assert.Contains("negative", result.Errors[0]);
        }

        #endregion

        #region Slot Count Validation Tests

        [Fact]
        public void Validator_TooFewSlots_Fails()
        {
            var validator = new ContinuationValidator();
            var descriptor = CreateTestDescriptor(12345, "TestMethod",
                yieldPoints: new[] { 0 },
                slotCount: 5,
                liveSlotCount: 3);
            validator.RegisterDescriptor(descriptor);

            // Only provide 1 slot when 3 are expected
            var frame = new HostFrameRecord(12345, 0, new object[] { 42 }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            Assert.Contains("Slot count mismatch", result.Errors[0]);
        }

        [Fact]
        public void Validator_ExtraSlots_Succeeds()
        {
            var validator = new ContinuationValidator();
            var descriptor = CreateTestDescriptor(12345, "TestMethod",
                yieldPoints: new[] { 0 },
                slotCount: 2,
                liveSlotCount: 2);
            validator.RegisterDescriptor(descriptor);

            // Provide more slots than expected - this is OK
            var frame = new HostFrameRecord(12345, 0, new object[] { 1, 2, 3, 4, 5 }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        #endregion

        #region Type Validation Tests

        [Fact]
        public void Validator_PrimitiveTypes_AlwaysAllowed()
        {
            var validator = new ContinuationValidator();

            Assert.True(validator.IsTypeAllowed(typeof(int)));
            Assert.True(validator.IsTypeAllowed(typeof(bool)));
            Assert.True(validator.IsTypeAllowed(typeof(double)));
            Assert.True(validator.IsTypeAllowed(typeof(string)));
        }

        [Fact]
        public void Validator_UnregisteredReferenceType_NotAllowed()
        {
            var validator = new ContinuationValidator();

            Assert.False(validator.IsTypeAllowed(typeof(System.Diagnostics.Process)));
        }

        [Fact]
        public void Validator_RegisteredType_Allowed()
        {
            var validator = new ContinuationValidator();
            validator.RegisterAllowedType(typeof(TestDataClass));

            Assert.True(validator.IsTypeAllowed(typeof(TestDataClass)));
        }

        [Fact]
        public void Validator_SlotWithForbiddenType_Fails()
        {
            var validator = new ContinuationValidator();
            var descriptor = CreateTestDescriptor(12345, "TestMethod", yieldPoints: new[] { 0 }, slotCount: 1, liveSlotCount: 1);
            validator.RegisterDescriptor(descriptor);

            // Try to use an unregistered type in a slot
            var frame = new HostFrameRecord(12345, 0, new object[] { new TestDataClass() }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            Assert.Contains("not in the allowed type list", result.Errors[0]);
        }

        [Fact]
        public void Validator_SlotWithRegisteredType_Succeeds()
        {
            var validator = new ContinuationValidator();
            validator.RegisterAllowedType(typeof(TestDataClass));
            var descriptor = CreateTestDescriptor(12345, "TestMethod", yieldPoints: new[] { 0 }, slotCount: 1, liveSlotCount: 1);
            validator.RegisterDescriptor(descriptor);

            var frame = new HostFrameRecord(12345, 0, new object[] { new TestDataClass() }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validator_NullSlotValue_Allowed()
        {
            var validator = new ContinuationValidator();
            var descriptor = CreateTestDescriptor(12345, "TestMethod", yieldPoints: new[] { 0 }, slotCount: 2, liveSlotCount: 2);
            validator.RegisterDescriptor(descriptor);

            var frame = new HostFrameRecord(12345, 0, new object[] { null, null }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validator_ArrayOfAllowedType_Allowed()
        {
            var validator = new ContinuationValidator();

            Assert.True(validator.IsTypeAllowed(typeof(int[])));
            Assert.True(validator.IsTypeAllowed(typeof(string[])));
        }

        [Fact]
        public void Validator_NullableOfAllowedType_Allowed()
        {
            var validator = new ContinuationValidator();

            Assert.True(validator.IsTypeAllowed(typeof(int?)));
            Assert.True(validator.IsTypeAllowed(typeof(DateTime?)));
        }

        [Fact]
        public void Validator_EnumType_Allowed()
        {
            var validator = new ContinuationValidator();

            Assert.True(validator.IsTypeAllowed(typeof(SlotKind)));
            Assert.True(validator.IsTypeAllowed(typeof(DayOfWeek)));
        }

        #endregion

        #region Stack Depth Validation Tests

        [Fact]
        public void Validator_DeepStack_Fails()
        {
            var options = new ValidationOptions { MaxStackDepth = 5, RequireRegisteredMethods = false };
            var validator = new ContinuationValidator(options);

            // Create a stack deeper than allowed
            HostFrameRecord frame = null;
            for (int i = 0; i < 10; i++)
            {
                frame = new HostFrameRecord(i, 0, new object[0], frame);
            }

            var state = new ContinuationState(frame);
            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            Assert.Contains("Stack depth exceeds maximum", result.Errors[0]);
        }

        [Fact]
        public void Validator_AcceptableStackDepth_Succeeds()
        {
            var options = new ValidationOptions { MaxStackDepth = 100, RequireRegisteredMethods = false };
            var validator = new ContinuationValidator(options);

            // Create a reasonable stack
            var inner = new HostFrameRecord(1, 0, new object[0], null);
            var outer = new HostFrameRecord(2, 0, new object[0], inner);

            var state = new ContinuationState(outer);
            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        #endregion

        #region Multiple Frame Validation Tests

        [Fact]
        public void Validator_MultipleFrames_AllValidated()
        {
            var validator = new ContinuationValidator();
            validator.RegisterDescriptor(CreateTestDescriptor(100, "Inner", yieldPoints: new[] { 0 }, slotCount: 1, liveSlotCount: 1));
            validator.RegisterDescriptor(CreateTestDescriptor(200, "Outer", yieldPoints: new[] { 0 }, slotCount: 1, liveSlotCount: 1));

            var inner = new HostFrameRecord(100, 0, new object[] { 1 }, null);
            var outer = new HostFrameRecord(200, 0, new object[] { 2 }, inner);

            var state = new ContinuationState(outer);
            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validator_OneInvalidFrame_ReportsError()
        {
            var validator = new ContinuationValidator();
            validator.RegisterDescriptor(CreateTestDescriptor(100, "Inner", yieldPoints: new[] { 0 }, slotCount: 0, liveSlotCount: 0));
            // Outer method not registered

            var inner = new HostFrameRecord(100, 0, new object[0], null);
            var outer = new HostFrameRecord(200, 0, new object[0], inner); // Not registered

            var state = new ContinuationState(outer);
            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            Assert.Contains("Frame[0]", result.Errors[0]); // Outer frame fails
        }

        #endregion

        #region Yielded Value Validation Tests

        [Fact]
        public void Validator_YieldedValueWithForbiddenType_Fails()
        {
            var validator = new ContinuationValidator();

            var frame = new HostFrameRecord(12345, 0, new object[0], null);
            var state = new ContinuationState(frame, new TestDataClass());

            // With lenient method checking but strict type checking
            var options = new ValidationOptions { RequireRegisteredMethods = false, ValidateSlotTypes = true };
            var strictValidator = new ContinuationValidator(options);

            var result = strictValidator.TryValidate(state);

            Assert.False(result.IsValid);
            Assert.Contains("Yielded value type", result.Errors[0]);
        }

        [Fact]
        public void Validator_YieldedValueWithAllowedType_Succeeds()
        {
            var validator = new ContinuationValidator(ValidationOptions.Lenient);
            validator.RegisterAllowedType(typeof(TestDataClass));

            var frame = new HostFrameRecord(12345, 0, new object[0], null);
            var state = new ContinuationState(frame, new TestDataClass());

            // Need to enable slot type validation to check yielded value
            var options = new ValidationOptions { RequireRegisteredMethods = false, ValidateSlotTypes = true };
            var strictValidator = new ContinuationValidator(options);
            strictValidator.RegisterAllowedType(typeof(TestDataClass));

            var result = strictValidator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        #endregion

        #region ValidationException Tests

        [Fact]
        public void Validator_Validate_ThrowsOnFailure()
        {
            var validator = new ContinuationValidator();

            var frame = new HostFrameRecord(99999, 0, new object[0], null);
            var state = new ContinuationState(frame);

            var ex = Assert.Throws<ValidationException>(() => validator.Validate(state));
            Assert.NotNull(ex.Result);
            Assert.False(ex.Result.IsValid);
        }

        [Fact]
        public void Validator_Validate_DoesNotThrowOnSuccess()
        {
            var validator = new ContinuationValidator();
            var descriptor = CreateTestDescriptor(12345, "Test", yieldPoints: new[] { 0 }, slotCount: 0, liveSlotCount: 0);
            validator.RegisterDescriptor(descriptor);

            var frame = new HostFrameRecord(12345, 0, new object[0], null);
            var state = new ContinuationState(frame);

            // Should not throw
            validator.Validate(state);
        }

        #endregion

        #region Registration Tests

        [Fact]
        public void Validator_GetDescriptor_ReturnsRegistered()
        {
            var validator = new ContinuationValidator();
            var descriptor = CreateTestDescriptor(12345, "Test", yieldPoints: new[] { 0 });
            validator.RegisterDescriptor(descriptor);

            var retrieved = validator.GetDescriptor(12345);

            Assert.Same(descriptor, retrieved);
        }

        [Fact]
        public void Validator_GetDescriptor_ReturnsNullForUnregistered()
        {
            var validator = new ContinuationValidator();

            var retrieved = validator.GetDescriptor(99999);

            Assert.Null(retrieved);
        }

        [Fact]
        public void Validator_RegisterAllowedTypeName_WorksWithTypeName()
        {
            var validator = new ContinuationValidator();
            validator.RegisterAllowedTypeName("Prim.Tests.Unit.ValidationTests+TestDataClass");

            Assert.True(validator.IsTypeAllowed(typeof(TestDataClass)));
        }

        #endregion

        #region Helper Methods

        private static FrameDescriptor CreateTestDescriptor(
            int methodToken,
            string methodName,
            int[] yieldPoints,
            int slotCount = 2,
            int liveSlotCount = 2)
        {
            var slots = new FrameSlot[slotCount];
            for (int i = 0; i < slotCount; i++)
            {
                slots[i] = new FrameSlot(i, $"var{i}", SlotKind.Local, typeof(object));
            }

            var liveSlotsAtYieldPoint = new BitArray[yieldPoints.Length];
            for (int i = 0; i < yieldPoints.Length; i++)
            {
                var bits = new BitArray(slotCount);
                for (int j = 0; j < Math.Min(liveSlotCount, slotCount); j++)
                {
                    bits[j] = true;
                }
                liveSlotsAtYieldPoint[i] = bits;
            }

            return new FrameDescriptor(methodToken, methodName, slots, yieldPoints, liveSlotsAtYieldPoint);
        }

        private class TestDataClass
        {
            public int Value { get; set; }
        }

        #endregion
    }
}
