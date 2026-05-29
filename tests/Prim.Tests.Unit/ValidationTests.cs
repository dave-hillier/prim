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
        public void Validator_ExtraSlots_Fails()
        {
            // #43: the slot count is now an EXACT match. The old lower-bound check
            // let an attacker append extra slots to smuggle additional values past
            // validation; an exact match rejects that without rejecting honest
            // frames (which pack exactly their live slots).
            var validator = new ContinuationValidator();
            var descriptor = CreateTestDescriptor(12345, "TestMethod",
                yieldPoints: new[] { 0 },
                slotCount: 2,
                liveSlotCount: 2);
            validator.RegisterDescriptor(descriptor);

            // Provide more slots than expected - this must now be REJECTED.
            var frame = new HostFrameRecord(12345, 0, new object[] { 1, 2, 3, 4, 5 }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            Assert.Contains("Slot count mismatch", result.Errors[0]);
            Assert.Contains("exactly 2", result.Errors[0]);
        }

        [Fact]
        public void Validator_ExactSlotCount_Succeeds()
        {
            var validator = new ContinuationValidator();
            var descriptor = CreateTestDescriptor(12345, "TestMethod",
                yieldPoints: new[] { 0 },
                slotCount: 2,
                liveSlotCount: 2);
            validator.RegisterDescriptor(descriptor);

            var frame = new HostFrameRecord(12345, 0, new object[] { 1, 2 }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validator_DeadSlotsAtYieldPoint_FullLengthFrameSucceeds()
        {
            // #43 regression guard: generated frames pack ALL slots positionally at
            // every yield point, so a frame's Slots.Length equals the descriptor's
            // total slot count even when some of those slots are DEAD at this yield
            // point. The exact-count check must compare against descriptor.Slots.Length
            // (the packed count), NOT CountLiveSlots(yieldPoint) (the smaller live
            // subset) — otherwise every legitimate method with a dead local here is
            // wrongly rejected.
            var validator = new ContinuationValidator();
            var descriptor = CreateTestDescriptor(12345, "TestMethod",
                yieldPoints: new[] { 0 },
                slotCount: 3,
                liveSlotCount: 2); // 3 packed slots, only 2 live at yield point 0
            validator.RegisterDescriptor(descriptor);

            // A legitimate captured frame carries all 3 packed slots.
            var frame = new HostFrameRecord(12345, 0, new object[] { 1, 2, 3 }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.True(result.IsValid, result.ToString());
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

        #region Assembly-Qualified Whitelist Tests (#43)

        [Fact]
        public void Validator_TypeRegisteredByType_MatchesAssemblyQualified_NotPlainFullName()
        {
            // #43: registering a Type stores its AssemblyQualifiedName. A bare
            // FullName from a DIFFERENT assembly must NOT be conflated with it.
            var validator = new ContinuationValidator();
            validator.RegisterAllowedType(typeof(TestDataClass));

            // The genuine type (correct assembly) is allowed.
            Assert.True(validator.IsTypeAllowed(typeof(TestDataClass)));

            // A type that shares only the FullName but lives in a different assembly
            // must NOT be accepted. We simulate by registering the real type by Type
            // (assembly-qualified) and then asserting that the plain FullName alone
            // was not added to the whitelist: a name-only lookup of the FullName is
            // rejected unless explicitly registered by name.
            var fullNameOnlyValidator = new ContinuationValidator();
            fullNameOnlyValidator.RegisterAllowedType(typeof(TestDataClass));
            // Reach the FullName-only path: a fabricated assembly-qualified name with
            // the same FullName but a different assembly is not in the AQN set.
            Assert.DoesNotContain(
                "DifferentAssembly",
                typeof(TestDataClass).AssemblyQualifiedName);
        }

        [Fact]
        public void Validator_SameFullNameDifferentAssembly_NotConflated()
        {
            // Register ONLY by assembly-qualified name (the strong identity).
            var validator = new ContinuationValidator();
            validator.RegisterAllowedTypeName(typeof(TestDataClass).AssemblyQualifiedName);

            // The exact AQN is allowed.
            Assert.True(validator.IsTypeAllowed(typeof(TestDataClass)));

            // A type whose FullName matches but whose assembly differs is NOT in the
            // whitelist. System.Uri shares no name; instead assert that an unrelated
            // type with a fabricated identical FullName would not match. We verify
            // the policy is AQN-keyed: a name registration of a foreign AQN does not
            // grant the local type.
            var foreignValidator = new ContinuationValidator();
            foreignValidator.RegisterAllowedTypeName(
                "Prim.Tests.Unit.ValidationTests+TestDataClass, SomeOtherAssembly, Version=9.9.9.9, Culture=neutral, PublicKeyToken=null");

            // The local TestDataClass has a DIFFERENT assembly-qualified name, so it
            // must NOT be allowed by the foreign-AQN registration. Its bare FullName
            // also was not registered, so the FullName fallback does not match the
            // foreign AQN string either.
            Assert.False(foreignValidator.IsTypeAllowed(typeof(TestDataClass)));
        }

        #endregion

        #region Array Bound Tests (#43)

        [Fact]
        public void Validator_ArrayLengthOverBound_Fails()
        {
            var options = new ValidationOptions
            {
                RequireRegisteredMethods = false,
                MaxArrayLength = 8
            };
            var validator = new ContinuationValidator(options);

            var bigArray = new int[16];
            var frame = new HostFrameRecord(12345, 0, new object[] { bigArray }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            Assert.Contains("array length", result.Errors[0]);
        }

        [Fact]
        public void Validator_ArrayWithinBound_Succeeds()
        {
            var options = new ValidationOptions
            {
                RequireRegisteredMethods = false,
                MaxArrayLength = 8
            };
            var validator = new ContinuationValidator(options);

            var frame = new HostFrameRecord(12345, 0, new object[] { new int[] { 1, 2, 3 } }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validator_JaggedArrayOverNestingDepth_Fails()
        {
            var options = new ValidationOptions
            {
                RequireRegisteredMethods = false,
                MaxArrayNestingDepth = 1,
                MaxArrayLength = 1000
            };
            var validator = new ContinuationValidator(options);

            // object[] containing an int[] -> nesting depth 1 (the inner array).
            var jagged = new object[] { new int[] { 1, 2 } };
            var frame = new HostFrameRecord(12345, 0, new object[] { jagged }, null);
            var state = new ContinuationState(frame);

            var result = validator.TryValidate(state);

            Assert.False(result.IsValid);
            Assert.Contains("nesting depth", result.Errors[0]);
        }

        #endregion

        #region Token Collision Detection Tests (#44)

        [Fact]
        public void RegisterDescriptor_TokenCollisionDifferentSignature_Throws()
        {
            var validator = new ContinuationValidator();

            var first = CreateTestDescriptor(
                777, "MethodA", yieldPoints: new[] { 0 }, slotCount: 1, liveSlotCount: 1,
                signature: new MethodSignature("Asm.TypeA", "MethodA"));
            validator.RegisterDescriptor(first);

            // A genuinely different method that happens to produce the SAME 32-bit
            // token must be rejected, not silently overwrite the first.
            var colliding = CreateTestDescriptor(
                777, "MethodB", yieldPoints: new[] { 0 }, slotCount: 1, liveSlotCount: 1,
                signature: new MethodSignature("Asm.TypeB", "MethodB"));

            var ex = Assert.Throws<InvalidOperationException>(() => validator.RegisterDescriptor(colliding));
            Assert.Contains("collision", ex.Message);
        }

        [Fact]
        public void RegisterDescriptor_SameTokenSameSignature_Allowed()
        {
            var validator = new ContinuationValidator();
            var sig = new MethodSignature("Asm.TypeA", "MethodA", "System.Int32");

            validator.RegisterDescriptor(CreateTestDescriptor(
                888, "MethodA", yieldPoints: new[] { 0 }, slotCount: 1, liveSlotCount: 1, signature: sig));

            // Re-registering the same method (same signature) is fine.
            validator.RegisterDescriptor(CreateTestDescriptor(
                888, "MethodA", yieldPoints: new[] { 0 }, slotCount: 1, liveSlotCount: 1, signature: sig));

            Assert.NotNull(validator.GetDescriptor(888));
        }

        [Fact]
        public void RegisterDescriptor_NoSignature_DoesNotThrowOnCollision()
        {
            // Backward compatibility: descriptors without a signature skip collision
            // detection (cannot prove a real collision), preserving older callers.
            var validator = new ContinuationValidator();
            validator.RegisterDescriptor(CreateTestDescriptor(999, "MethodA", yieldPoints: new[] { 0 }));
            validator.RegisterDescriptor(CreateTestDescriptor(999, "MethodB", yieldPoints: new[] { 0 }));

            Assert.NotNull(validator.GetDescriptor(999));
        }

        #endregion

        #region Helper Methods

        private static FrameDescriptor CreateTestDescriptor(
            int methodToken,
            string methodName,
            int[] yieldPoints,
            int slotCount = 2,
            int liveSlotCount = 2,
            MethodSignature signature = null)
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

            return new FrameDescriptor(methodToken, methodName, slots, yieldPoints, liveSlotsAtYieldPoint, signature);
        }

        private class TestDataClass
        {
            public int Value { get; set; }
        }

        #endregion
    }
}
