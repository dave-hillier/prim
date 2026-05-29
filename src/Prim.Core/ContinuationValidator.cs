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

        // Assembly-qualified type identity. Matching on FullName alone conflates two
        // assemblies that share a FullName, letting a forged type in a different
        // assembly impersonate an allowed one (issue #43). The primary whitelist is
        // therefore keyed on AssemblyQualifiedName.
        private readonly HashSet<string> _allowedAssemblyQualifiedNames = new HashSet<string>(StringComparer.Ordinal);

        // Secondary, weaker whitelist for types registered by FullName only (via
        // RegisterAllowedTypeName) when the concrete Type / assembly is not known at
        // registration time. Kept separate so it cannot silently widen the
        // assembly-qualified path.
        private readonly HashSet<string> _allowedFullNames = new HashSet<string>(StringComparer.Ordinal);

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

            // #44: a 32-bit method token is only an FNV-1a hash, so two genuinely
            // different methods can collide. If a descriptor is already registered
            // under this token and its signature differs, this is either a real
            // collision or a forgery attempt that crafts a (type, method, params)
            // tuple colliding with a registered method to resume into it. Turn the
            // silent overwrite into a loud registration error so the collision is
            // surfaced at startup, not exploited at resume time.
            if (_descriptors.TryGetValue(descriptor.MethodToken, out var existing)
                && existing.Signature != null
                && descriptor.Signature != null
                && !existing.Signature.Equals(descriptor.Signature))
            {
                throw new InvalidOperationException(
                    $"Method token {descriptor.MethodToken} collision: a descriptor for " +
                    $"'{existing.Signature}' is already registered, but a different method " +
                    $"'{descriptor.Signature}' produced the same 32-bit token. " +
                    "Refusing to register to prevent resuming into the wrong method.");
            }

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
            // Identity is the assembly-qualified name so two assemblies sharing a
            // FullName are NOT conflated (issue #43).
            if (type.AssemblyQualifiedName != null)
            {
                _allowedAssemblyQualifiedNames.Add(type.AssemblyQualifiedName);
            }
        }

        /// <summary>
        /// Registers a type by name as allowed in slot values.
        /// Useful when the actual Type is not available at registration time.
        /// </summary>
        public void RegisterAllowedTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) throw new ArgumentNullException(nameof(typeName));
            // A name-only registration may be an assembly-qualified name or a bare
            // FullName. Store it in both buckets so either form of supplied name can
            // match, while keeping Type-based registrations strictly
            // assembly-qualified.
            _allowedAssemblyQualifiedNames.Add(typeName);
            _allowedFullNames.Add(typeName);
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
            return IsTypeAllowed(type, depth: 0);
        }

        private bool IsTypeAllowed(Type type, int depth)
        {
            if (type == null) return true; // null values are allowed
            if (type.IsPrimitive) return true;
            if (type.IsEnum) return true;
            // object is a legitimate container/element type (e.g. object[] slots).
            // Each actual element's runtime type is still whitelist-checked when the
            // slot value is validated, so allowing the object container is safe.
            if (type == typeof(object)) return true;
            if (_allowedTypes.Contains(type)) return true;

            // Assembly-qualified match is the primary, unambiguous identity (#43).
            if (type.AssemblyQualifiedName != null
                && _allowedAssemblyQualifiedNames.Contains(type.AssemblyQualifiedName))
            {
                return true;
            }

            // Fall back to the weaker FullName match only for types explicitly
            // registered by name (RegisterAllowedTypeName).
            var fullName = type.FullName ?? type.Name;
            if (_allowedFullNames.Contains(fullName)) return true;

            // Bound the nesting depth so a maliciously deep array-of-array-of...
            // type (or generic recursion) cannot drive unbounded recursion (#43).
            if (depth >= _options.MaxArrayNestingDepth)
            {
                return false;
            }

            // Check if it's an array of allowed type.
            if (type.IsArray)
            {
                return IsTypeAllowed(type.GetElementType(), depth + 1);
            }

            // Check if it's a nullable of allowed type.
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                return IsTypeAllowed(Nullable.GetUnderlyingType(type), depth + 1);
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

            // 3. Slot count validation (EXACT match, issue #43).
            //
            // The old check only enforced a lower bound (actual >= expected), so an
            // attacker could append extra slots to smuggle additional values past
            // validation. The generated frame layout packs ALL of the method's slots
            // positionally at every yield point (slots dead at a given point still
            // occupy their index), so the captured slot array length must match the
            // descriptor's total declared slot count exactly: not fewer (missing
            // state) and not more (smuggled state). NB: this is descriptor.Slots.Length,
            // NOT CountLiveSlots(yieldPoint) — the live-bit subset is generally smaller
            // than the packed array, so checking against it would reject any legitimate
            // frame that has a dead slot at this yield point.
            if (_options.ValidateSlotCounts)
            {
                var yieldPointIndex = Array.IndexOf(descriptor.YieldPointIds, frame.YieldPointId);
                if (yieldPointIndex >= 0)
                {
                    var expectedSlotCount = descriptor.Slots.Length;
                    var actualSlotCount = frame.Slots?.Length ?? 0;

                    if (actualSlotCount != expectedSlotCount)
                    {
                        errors.Add($"{prefix}: Slot count mismatch. Expected exactly {expectedSlotCount}, got {actualSlotCount}");
                    }
                }
            }

            // 4. Slot type validation.
            //
            // Validate EVERY provided slot index, not only the descriptor-live ones,
            // so a dead/extra index cannot escape structural validation (#43). With
            // the exact-count check above this is the full live set, but we still
            // iterate the whole array defensively. Where a declared type exists for
            // an index we additionally enforce type compatibility.
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

                    // Bound array values by length and nesting depth (#43).
                    ValidateSlotValueBounds(slotValue, $"{prefix}: Slot[{i}]", errors, depth: 0);

                    // Check type compatibility with declared slot type (if available)
                    if (liveSlots != null && i < descriptor.Slots.Length && i < liveSlots.Length && liveSlots[i])
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

        /// <summary>
        /// Enforces the configured array length and nesting-depth bounds on a slot
        /// value that is (or contains) arrays. A deserialized slot can carry a
        /// jagged or huge array crafted to exhaust memory; bounding both dimensions
        /// turns that into a clean validation failure (issue #43).
        /// </summary>
        private void ValidateSlotValueBounds(object value, string prefix, List<string> errors, int depth)
        {
            if (value == null) return;
            if (value is not Array array) return;

            if (depth >= _options.MaxArrayNestingDepth)
            {
                errors.Add($"{prefix}: array nesting depth exceeds maximum allowed ({_options.MaxArrayNestingDepth})");
                return;
            }

            if (array.Length > _options.MaxArrayLength)
            {
                errors.Add($"{prefix}: array length {array.Length} exceeds maximum allowed ({_options.MaxArrayLength})");
                return;
            }

            // Walk elements: whitelist-check each non-null element's runtime type
            // (closing the array-element gap) and recurse into nested arrays to
            // enforce the nesting-depth bound (#43).
            var elementType = array.GetType().GetElementType();
            var elementsMayBeArrays = elementType != null && (elementType.IsArray || elementType == typeof(object));

            if (!elementsMayBeArrays) return; // scalar element array: nothing deeper to bound

            foreach (var element in array)
            {
                if (element == null) continue;

                if (element is Array)
                {
                    ValidateSlotValueBounds(element, prefix, errors, depth + 1);
                }
                else if (!IsTypeAllowed(element.GetType()))
                {
                    errors.Add($"{prefix}: array element of type '{element.GetType().FullName}' is not in the allowed type list");
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
                        continue;
                    }

                    ValidateSlotValueBounds(slotValue, $"{prefix}: Slot[{i}]", errors, depth: 0);
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

        /// <summary>
        /// Maximum allowed length of an array slot value. A deserialized continuation
        /// can carry an attacker-sized array; bounding the length prevents a memory
        /// exhaustion DoS via a single slot (issue #43).
        /// Default: 1,000,000
        /// </summary>
        public int MaxArrayLength { get; set; } = 1_000_000;

        /// <summary>
        /// Maximum allowed nesting depth for array (or array-of-array) slot values
        /// and for array/nullable type whitelist recursion. Bounds both the type
        /// whitelist recursion and the runtime array-value recursion so a crafted
        /// jagged array cannot drive unbounded recursion (issue #43).
        /// Default: 16
        /// </summary>
        public int MaxArrayNestingDepth { get; set; } = 16;
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
