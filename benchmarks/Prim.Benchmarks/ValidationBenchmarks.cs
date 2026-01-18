using System.Collections;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Prim.Core;

namespace Prim.Benchmarks;

/// <summary>
/// Benchmarks measuring continuation state validation overhead.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class ValidationBenchmarks
{
    private ContinuationValidator _strictValidator = null!;
    private ContinuationValidator _lenientValidator = null!;
    private ContinuationState _validState = null!;
    private ContinuationState _deepState = null!;

    [GlobalSetup]
    public void Setup()
    {
        _strictValidator = new ContinuationValidator();
        _lenientValidator = new ContinuationValidator(ValidationOptions.Lenient);

        // Register descriptors for all frames
        for (int i = 0; i < 20; i++)
        {
            var descriptor = CreateDescriptor(1000 + i, $"Method{i}", slotCount: 5);
            _strictValidator.RegisterDescriptor(descriptor);
        }

        // Register allowed types
        _strictValidator.RegisterAllowedType(typeof(string));

        // Create valid state
        _validState = CreateValidState(frameCount: 5);

        // Create deep state
        _deepState = CreateValidState(frameCount: 20);
    }

    private static FrameDescriptor CreateDescriptor(int methodToken, string methodName, int slotCount)
    {
        var slots = new FrameSlot[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            slots[i] = new FrameSlot(i, $"var{i}", SlotKind.Local, typeof(object));
        }

        var yieldPoints = new[] { 0, 1, 2, 3, 4 };
        var liveSlotsAtYieldPoint = new BitArray[yieldPoints.Length];
        for (int i = 0; i < yieldPoints.Length; i++)
        {
            var bits = new BitArray(slotCount);
            for (int j = 0; j < slotCount; j++)
            {
                bits[j] = true;
            }
            liveSlotsAtYieldPoint[i] = bits;
        }

        return new FrameDescriptor(methodToken, methodName, slots, yieldPoints, liveSlotsAtYieldPoint);
    }

    private static ContinuationState CreateValidState(int frameCount)
    {
        HostFrameRecord? frame = null;

        for (int i = 0; i < frameCount; i++)
        {
            frame = new HostFrameRecord(
                methodToken: 1000 + i,
                yieldPointId: i % 5,
                slots: new object[] { i, $"str{i}", (double)i, i % 2 == 0, i * 10L },
                caller: frame);
        }

        return new ContinuationState(frame);
    }

    #region Validation Benchmarks

    /// <summary>
    /// Baseline: No validation.
    /// </summary>
    [Benchmark(Baseline = true)]
    public ContinuationState NoValidation()
    {
        // Just return the state without validation
        return _validState;
    }

    /// <summary>
    /// Lenient validation (minimal checks).
    /// </summary>
    [Benchmark]
    public ValidationResult LenientValidation()
    {
        return _lenientValidator.TryValidate(_validState);
    }

    /// <summary>
    /// Strict validation with registered descriptors.
    /// </summary>
    [Benchmark]
    public ValidationResult StrictValidation()
    {
        return _strictValidator.TryValidate(_validState);
    }

    /// <summary>
    /// Strict validation on deep stack.
    /// </summary>
    [Benchmark]
    public ValidationResult StrictValidation_DeepStack()
    {
        return _strictValidator.TryValidate(_deepState);
    }

    #endregion

    #region Type Checking Benchmarks

    /// <summary>
    /// Check if primitive type is allowed.
    /// </summary>
    [Benchmark]
    public bool IsTypeAllowed_Primitive()
    {
        return _strictValidator.IsTypeAllowed(typeof(int));
    }

    /// <summary>
    /// Check if registered type is allowed.
    /// </summary>
    [Benchmark]
    public bool IsTypeAllowed_Registered()
    {
        return _strictValidator.IsTypeAllowed(typeof(string));
    }

    /// <summary>
    /// Check if array of allowed type is allowed.
    /// </summary>
    [Benchmark]
    public bool IsTypeAllowed_Array()
    {
        return _strictValidator.IsTypeAllowed(typeof(int[]));
    }

    /// <summary>
    /// Check if nullable of allowed type is allowed.
    /// </summary>
    [Benchmark]
    public bool IsTypeAllowed_Nullable()
    {
        return _strictValidator.IsTypeAllowed(typeof(int?));
    }

    /// <summary>
    /// Check if unregistered type is not allowed.
    /// </summary>
    [Benchmark]
    public bool IsTypeAllowed_Unregistered()
    {
        return _strictValidator.IsTypeAllowed(typeof(System.Diagnostics.Process));
    }

    #endregion

    #region Descriptor Lookup Benchmarks

    /// <summary>
    /// Look up a registered descriptor.
    /// </summary>
    [Benchmark]
    public FrameDescriptor? GetDescriptor_Found()
    {
        return _strictValidator.GetDescriptor(1005);
    }

    /// <summary>
    /// Look up an unregistered descriptor.
    /// </summary>
    [Benchmark]
    public FrameDescriptor? GetDescriptor_NotFound()
    {
        return _strictValidator.GetDescriptor(99999);
    }

    #endregion
}
