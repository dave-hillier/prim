using System;
using System.Collections.Generic;
using System.Linq;

namespace Prim.Core
{
    /// <summary>
    /// Validates deserialized continuation state before resumption.
    ///
    /// As noted in the Espresso documentation: "Deserializing a continuation
    /// supplied by an attacker will allow a complete takeover." This validator
    /// prevents such attacks by verifying:
    ///
    /// - Method tokens correspond to registered (allowed) methods
    /// - Yield point IDs are within valid bounds for each method
    /// - Slot counts match expected counts for each yield point
    /// - Slot values are type-compatible with their declared types
    /// - Reference types are from the allowed type whitelist
    /// </summary>
    public sealed class ContinuationValidator
    {
        private readonly Dictionary<int, FrameDescriptor> _descriptors = new Dictionary<int, FrameDescriptor>();
        private readonly HashSet<Type> _allowedTypes = new HashSet<Type>();
        private readonly HashSet<string> _allowedTypeNames = new HashSet<string>();
        private readonly ValidationOptions _options;

        /// <summary>
        /// Creates a validator with default options.
        /// </summary>
        public ContinuationValidator() : this(ValidationOptions.Default)
        {
        }

        /// <summary>
        /// Creates a validator with custom options.
        /// </summary>
        public ContinuationValidator(ValidationOptions options)
        {
            _options = options ?? ValidationOptions.Default;

            // Register primitive types as always allowed
            RegisterAllowedType(typeof(bool));
            RegisterAllowedType(typeof(byte));
            RegisterAllowedType(typeof(sbyte));
            RegisterAllowedType(typeof(short));
            RegisterAllowedType(typeof(ushort));
            RegisterAllowedType(typeof(int));
            RegisterAllowedType(typeof(uint));
            RegisterAllowedType(typeof(long));
            RegisterAllowedType(typeof(ulong));
            RegisterAllowedType(typeof(float));
            RegisterAllowedType(typeof(double));
            RegisterAllowedType(typeof(decimal));
            RegisterAllowedType(typeof(char));
            RegisterAllowedType(typeof(string));
            RegisterAllowedType(typeof(DateTime));
            RegisterAllowedType(typeof(TimeSpan));
            RegisterAllowedType(typeof(Guid));
        }

        /// <summary>
        /// Registers a frame descriptor for a method.
        /// Only methods with registered descriptors can be resumed.
        /// </summary>
        public void RegisterDescriptor(FrameDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            _descriptors[descriptor.MethodToken] = descriptor;
        }

        /// <summary>
        /// Registers multiple frame descriptors.
        /// </summary>
        public void RegisterDescriptors(IEnumerable<FrameDescriptor> descriptors)
        {
            if (descriptors == null) throw new ArgumentNullException(nameof(descriptors));
            foreach (var descriptor in descriptors)
            {
                RegisterDescriptor(descriptor);
            }
        }

        /// <summary>
        /// Registers a type as allowed in slot values.
        /// </summary>
        public void RegisterAllowedType(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            _allowedTypes.Add(type);
            _allowedTypeNames.Add(type.FullName ?? type.Name);
        }

        /// <summary>
        /// Registers a type by name as allowed in slot values.
        /// Useful when the actual Type is not available at registration time.
        /// </summary>
        public void RegisterAllowedTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) throw new ArgumentNullException(nameof(typeName));
            _allowedTypeNames.Add(typeName);
        }

        /// <summary>
        /// Registers multiple types as allowed.
        /// </summary>
        public void RegisterAllowedTypes(IEnumerable<Type> types)
        {
            if (types == null) throw new ArgumentNullException(nameof(types));
            foreach (var type in types)
            {
                RegisterAllowedType(type);
            }
        }

        /// <summary>
        /// Gets a registered descriptor by method token.
        /// </summary>
        public FrameDescriptor GetDescriptor(int methodToken)
        {
            return _descriptors.TryGetValue(methodToken, out var desc) ? desc : null;
        }

        /// <summary>
        /// Checks if a type is allowed in slot values.
        /// </summary>
        public bool IsTypeAllowed(Type type)
        {
            if (type == null) return true; // null values are allowed
            if (type.IsPrimitive) return true;
            if (type.IsEnum) return true;
            if (_allowedTypes.Contains(type)) return true;
            if (_allowedTypeNames.Contains(type.FullName ?? type.Name)) return true;

            // Check if it's an array of allowed type
            if (type.IsArray)
            {
                return IsTypeAllowed(type.GetElementType());
            }

            // Check if it's a nullable of allowed type
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                return IsTypeAllowed(Nullable.GetUnderlyingType(type));
            }

            return false;
        }

        /// <summary>
        /// Validates a continuation state.
        /// Throws ValidationException if validation fails.
        /// </summary>
        public void Validate(ContinuationState state)
        {
            var result = TryValidate(state);
            if (!result.IsValid)
            {
                throw new ValidationException(result);
            }
        }

        /// <summary>
        /// Attempts to validate a continuation state.
        /// Returns a result indicating success or failure with details.
        /// </summary>
        public ValidationResult TryValidate(ContinuationState state)
        {
            if (state == null)
            {
                return ValidationResult.Failure("Continuation state is null");
            }

            var errors = new List<string>();
            if (state.Version != ContinuationState.CurrentVersion)
            {
                errors.Add($"Unsupported continuation version {state.Version} (expected {ContinuationState.CurrentVersion})");
            }
            var frame = state.StackHead;
            var frameIndex = 0;

            while (frame != null)
            {
                ValidateFrame(frame, frameIndex, errors);
                frame = frame.Caller;
                frameIndex++;

                // Prevent infinite loops from malicious circular references
                if (frameIndex > _options.MaxStackDepth)
                {
                    errors.Add($"Stack depth exceeds maximum allowed ({_options.MaxStackDepth})");
                    break;
                }
            }

            // Validate yielded value if type checking is enabled
            if (_options.ValidateSlotTypes && state.YieldedValue != null)
            {
                var valueType = state.YieldedValue.GetType();
                if (!IsTypeAllowed(valueType))
                {
                    errors.Add($"Yielded value type '{valueType.FullName}' is not in the allowed type list");
                }
            }

            return errors.Count == 0
                ? ValidationResult.Success()
                : ValidationResult.Failure(errors);
        }

        private void ValidateFrame(HostFrameRecord frame, int frameIndex, List<string> errors)
        {
            var prefix = $"Frame[{frameIndex}]";

            // 1. Method token validation
            if (!_descriptors.TryGetValue(frame.MethodToken, out var descriptor))
            {
                if (_options.RequireRegisteredMethods)
                {
                    errors.Add($"{prefix}: Method token {frame.MethodToken} is not registered");
                    return; // Can't validate further without descriptor
                }
                else
                {
                    // Without descriptor, we can only do basic validation
                    ValidateFrameWithoutDescriptor(frame, prefix, errors);
                    return;
                }
            }

            // 2. Yield point bounds checking
            if (!descriptor.YieldPointIds.Contains(frame.YieldPointId))
            {
                errors.Add($"{prefix}: Yield point ID {frame.YieldPointId} is not valid for method '{descriptor.MethodName}'. " +
                          $"Valid IDs: [{string.Join(", ", descriptor.YieldPointIds)}]");
            }

            // 3. Slot count validation
            if (_options.ValidateSlotCounts)
            {
                var yieldPointIndex = Array.IndexOf(descriptor.YieldPointIds, frame.YieldPointId);
                if (yieldPointIndex >= 0)
                {
                    var expectedSlotCount = descriptor.CountLiveSlots(frame.YieldPointId);
                    var actualSlotCount = frame.Slots?.Length ?? 0;

                    // Allow some flexibility - actual can be >= expected (extra slots are ignored)
                    // but should not be less
                    if (actualSlotCount < expectedSlotCount)
                    {
                        errors.Add($"{prefix}: Slot count mismatch. Expected at least {expectedSlotCount}, got {actualSlotCount}");
                    }
                }
            }

            // 4. Slot type validation
            if (_options.ValidateSlotTypes && frame.Slots != null)
            {
                var liveSlots = descriptor.YieldPointIds.Contains(frame.YieldPointId)
                    ? descriptor.GetLiveSlotsForYieldPoint(frame.YieldPointId)
                    : null;

                for (int i = 0; i < frame.Slots.Length; i++)
                {
                    var slotValue = frame.Slots[i];
                    if (slotValue == null) continue;

                    var slotType = slotValue.GetType();

                    // Check if type is in whitelist
                    if (!IsTypeAllowed(slotType))
                    {
                        errors.Add($"{prefix}: Slot[{i}] contains type '{slotType.FullName}' which is not in the allowed type list");
                        continue;
                    }

                    // Check type compatibility with declared slot type (if available)
                    if (liveSlots != null && i < descriptor.Slots.Length && liveSlots[i])
                    {
                        var declaredType = descriptor.Slots[i].Type;
                        if (!IsTypeCompatible(slotType, declaredType))
                        {
                            errors.Add($"{prefix}: Slot[{i}] type mismatch. Expected '{declaredType.Name}', got '{slotType.Name}'");
                        }
                    }
                }
            }
        }

        private void ValidateFrameWithoutDescriptor(HostFrameRecord frame, string prefix, List<string> errors)
        {
            // Basic validation when we don't have a descriptor

            // Yield point ID should be non-negative
            if (frame.YieldPointId < 0)
            {
                errors.Add($"{prefix}: Yield point ID {frame.YieldPointId} is negative");
            }

            // Validate slot types if enabled
            if (_options.ValidateSlotTypes && frame.Slots != null)
            {
                for (int i = 0; i < frame.Slots.Length; i++)
                {
                    var slotValue = frame.Slots[i];
                    if (slotValue == null) continue;

                    var slotType = slotValue.GetType();
                    if (!IsTypeAllowed(slotType))
                    {
                        errors.Add($"{prefix}: Slot[{i}] contains type '{slotType.FullName}' which is not in the allowed type list");
                    }
                }
            }
        }

        private static bool IsTypeCompatible(Type actualType, Type declaredType)
        {
            if (declaredType == null) return true;
            if (actualType == declaredType) return true;
            if (declaredType.IsAssignableFrom(actualType)) return true;

            // Handle boxing of value types
            if (declaredType == typeof(object)) return true;

            // Handle nullable types
            if (declaredType.IsGenericType && declaredType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                var underlyingType = Nullable.GetUnderlyingType(declaredType);
                return actualType == underlyingType;
            }

            return false;
        }
    }

    /// <summary>
    /// Options for continuation validation.
    /// </summary>
    public sealed class ValidationOptions
    {
        /// <summary>
        /// Default validation options (strict).
        /// </summary>
        public static readonly ValidationOptions Default = new ValidationOptions();

        /// <summary>
        /// Lenient options for trusted environments.
        /// </summary>
        public static readonly ValidationOptions Lenient = new ValidationOptions
        {
            RequireRegisteredMethods = false,
            ValidateSlotCounts = false,
            ValidateSlotTypes = false
        };

        /// <summary>
        /// Whether to require all method tokens to be registered.
        /// Default: true
        /// </summary>
        public bool RequireRegisteredMethods { get; set; } = true;

        /// <summary>
        /// Whether to validate slot counts match expectations.
        /// Default: true
        /// </summary>
        public bool ValidateSlotCounts { get; set; } = true;

        /// <summary>
        /// Whether to validate slot value types.
        /// Default: true
        /// </summary>
        public bool ValidateSlotTypes { get; set; } = true;

        /// <summary>
        /// Maximum allowed stack depth to prevent DoS attacks.
        /// Default: 1000
        /// </summary>
        public int MaxStackDepth { get; set; } = 1000;
    }

    /// <summary>
    /// Result of continuation validation.
    /// </summary>
    public sealed class ValidationResult
    {
        /// <summary>
        /// Whether validation succeeded.
        /// </summary>
        public bool IsValid { get; }

        /// <summary>
        /// Error messages if validation failed.
        /// </summary>
        public IReadOnlyList<string> Errors { get; }

        private ValidationResult(bool isValid, IReadOnlyList<string> errors)
        {
            IsValid = isValid;
            Errors = errors ?? Array.Empty<string>();
        }

        /// <summary>
        /// Creates a successful validation result.
        /// </summary>
        public static ValidationResult Success() => new ValidationResult(true, null);

        /// <summary>
        /// Creates a failed validation result with a single error.
        /// </summary>
        public static ValidationResult Failure(string error) =>
            new ValidationResult(false, new[] { error });

        /// <summary>
        /// Creates a failed validation result with multiple errors.
        /// </summary>
        public static ValidationResult Failure(IEnumerable<string> errors) =>
            new ValidationResult(false, errors.ToArray());

        public override string ToString()
        {
            if (IsValid) return "Validation succeeded";
            return $"Validation failed: {string.Join("; ", Errors)}";
        }
    }

    /// <summary>
    /// Exception thrown when continuation validation fails.
    /// </summary>
    public sealed class ValidationException : Exception
    {
        /// <summary>
        /// The validation result containing error details.
        /// </summary>
        public ValidationResult Result { get; }

        public ValidationException(ValidationResult result)
            : base(FormatMessage(result))
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        private static string FormatMessage(ValidationResult result)
        {
            if (result == null || result.Errors.Count == 0)
                return "Continuation validation failed";

            if (result.Errors.Count == 1)
                return $"Continuation validation failed: {result.Errors[0]}";

            return $"Continuation validation failed with {result.Errors.Count} errors: {result.Errors[0]}";
        }
    }
}
