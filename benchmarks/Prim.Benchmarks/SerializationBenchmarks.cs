using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Prim.Core;
using Prim.Serialization;

namespace Prim.Benchmarks;

/// <summary>
/// Benchmarks comparing serialization formats and measuring serialization overhead.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class SerializationBenchmarks
{
    private JsonContinuationSerializer _jsonSerializer = null!;
    private JsonContinuationSerializer _jsonCompactSerializer = null!;
    private MessagePackContinuationSerializer _msgpackSerializer = null!;

    private ContinuationState _smallState = null!;
    private ContinuationState _mediumState = null!;
    private ContinuationState _largeState = null!;

    private byte[] _smallJsonBytes = null!;
    private byte[] _smallMsgpackBytes = null!;
    private byte[] _mediumJsonBytes = null!;
    private byte[] _mediumMsgpackBytes = null!;
    private byte[] _largeJsonBytes = null!;
    private byte[] _largeMsgpackBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _jsonSerializer = new JsonContinuationSerializer();
        _jsonCompactSerializer = JsonContinuationSerializer.Compact();
        _msgpackSerializer = new MessagePackContinuationSerializer();

        // Small state: 1 frame, few slots
        _smallState = CreateState(frameCount: 1, slotsPerFrame: 3);

        // Medium state: 5 frames, moderate slots
        _mediumState = CreateState(frameCount: 5, slotsPerFrame: 10);

        // Large state: 20 frames, many slots
        _largeState = CreateState(frameCount: 20, slotsPerFrame: 20);

        // Pre-serialize for deserialization benchmarks
        _smallJsonBytes = _jsonCompactSerializer.Serialize(_smallState);
        _smallMsgpackBytes = _msgpackSerializer.Serialize(_smallState);
        _mediumJsonBytes = _jsonCompactSerializer.Serialize(_mediumState);
        _mediumMsgpackBytes = _msgpackSerializer.Serialize(_mediumState);
        _largeJsonBytes = _jsonCompactSerializer.Serialize(_largeState);
        _largeMsgpackBytes = _msgpackSerializer.Serialize(_largeState);
    }

    private static ContinuationState CreateState(int frameCount, int slotsPerFrame)
    {
        HostFrameRecord? frame = null;

        for (int i = 0; i < frameCount; i++)
        {
            var slots = new object[slotsPerFrame];
            for (int j = 0; j < slotsPerFrame; j++)
            {
                // Mix of primitive types
                slots[j] = (j % 4) switch
                {
                    0 => i * 100 + j,           // int
                    1 => $"string_{i}_{j}",     // string
                    2 => (double)(i + j) / 10,  // double
                    _ => j % 2 == 0             // bool
                };
            }

            frame = new HostFrameRecord(
                methodToken: 1000 + i,
                yieldPointId: i % 5,
                slots: slots,
                caller: frame);
        }

        return new ContinuationState(frame, yieldedValue: "yielded");
    }

    #region JSON Serialization

    [Benchmark]
    public byte[] Json_Serialize_Small() => _jsonCompactSerializer.Serialize(_smallState);

    [Benchmark]
    public byte[] Json_Serialize_Medium() => _jsonCompactSerializer.Serialize(_mediumState);

    [Benchmark]
    public byte[] Json_Serialize_Large() => _jsonCompactSerializer.Serialize(_largeState);

    [Benchmark]
    public ContinuationState Json_Deserialize_Small() => _jsonCompactSerializer.Deserialize(_smallJsonBytes);

    [Benchmark]
    public ContinuationState Json_Deserialize_Medium() => _jsonCompactSerializer.Deserialize(_mediumJsonBytes);

    [Benchmark]
    public ContinuationState Json_Deserialize_Large() => _jsonCompactSerializer.Deserialize(_largeJsonBytes);

    #endregion

    #region MessagePack Serialization

    [Benchmark]
    public byte[] MsgPack_Serialize_Small() => _msgpackSerializer.Serialize(_smallState);

    [Benchmark]
    public byte[] MsgPack_Serialize_Medium() => _msgpackSerializer.Serialize(_mediumState);

    [Benchmark]
    public byte[] MsgPack_Serialize_Large() => _msgpackSerializer.Serialize(_largeState);

    [Benchmark]
    public ContinuationState MsgPack_Deserialize_Small() => _msgpackSerializer.Deserialize(_smallMsgpackBytes);

    [Benchmark]
    public ContinuationState MsgPack_Deserialize_Medium() => _msgpackSerializer.Deserialize(_mediumMsgpackBytes);

    [Benchmark]
    public ContinuationState MsgPack_Deserialize_Large() => _msgpackSerializer.Deserialize(_largeMsgpackBytes);

    #endregion

    #region Round-trip Benchmarks

    [Benchmark]
    public ContinuationState Json_RoundTrip_Medium()
    {
        var bytes = _jsonCompactSerializer.Serialize(_mediumState);
        return _jsonCompactSerializer.Deserialize(bytes);
    }

    [Benchmark]
    public ContinuationState MsgPack_RoundTrip_Medium()
    {
        var bytes = _msgpackSerializer.Serialize(_mediumState);
        return _msgpackSerializer.Deserialize(bytes);
    }

    #endregion

    #region Size Comparison (reported via GlobalSetup output)

    [GlobalCleanup]
    public void ReportSizes()
    {
        Console.WriteLine();
        Console.WriteLine("=== Serialization Sizes ===");
        Console.WriteLine($"Small  - JSON: {_smallJsonBytes.Length,6} bytes, MsgPack: {_smallMsgpackBytes.Length,6} bytes");
        Console.WriteLine($"Medium - JSON: {_mediumJsonBytes.Length,6} bytes, MsgPack: {_mediumMsgpackBytes.Length,6} bytes");
        Console.WriteLine($"Large  - JSON: {_largeJsonBytes.Length,6} bytes, MsgPack: {_largeMsgpackBytes.Length,6} bytes");
    }

    #endregion
}
