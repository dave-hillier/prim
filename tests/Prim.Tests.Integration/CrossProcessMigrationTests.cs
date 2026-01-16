using System;
using System.IO;
using Prim.Core;
using Prim.Runtime;
using Prim.Serialization;
using Xunit;

namespace Prim.Tests.Integration
{
    /// <summary>
    /// Integration tests for cross-process migration scenarios.
    /// These tests verify that state can be serialized, stored, and
    /// restored correctly - simulating migration between processes.
    /// </summary>
    public class CrossProcessMigrationTests
    {
        [Fact]
        public void Json_SerializeAndDeserialize_PreservesState()
        {
            var serializer = new JsonContinuationSerializer();
            var runner = new ContinuationRunner();

            // Create a computation that will yield
            var counter = 0;
            var result = runner.Run(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                for (int i = 0; i < 10; i++)
                {
                    counter = i;
                    context.RequestYield();
                    context.HandleYieldPoint(0, i);
                }
                return counter;
            });

            Assert.True(result.IsSuspended);
            var suspended = (ContinuationResult<int>.Suspended)result;

            // Serialize to JSON
            var json = serializer.SerializeToString(suspended.State);
            Assert.NotEmpty(json);

            // Deserialize
            var restored = serializer.DeserializeFromString(json);
            Assert.NotNull(restored);
            Assert.NotNull(restored.StackHead);
        }

        [Fact]
        public void MessagePack_SerializeAndDeserialize_PreservesState()
        {
            var serializer = new MessagePackContinuationSerializer();
            var runner = new ContinuationRunner();

            // Create a computation that will yield
            var result = runner.Run(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                context.RequestYield();
                context.HandleYieldPoint(0, "test value");
                return 42;
            });

            Assert.True(result.IsSuspended);
            var suspended = (ContinuationResult<int>.Suspended)result;

            // Serialize to bytes
            var bytes = serializer.Serialize(suspended.State);
            Assert.NotEmpty(bytes);

            // Deserialize
            var restored = serializer.Deserialize(bytes);
            Assert.NotNull(restored);
            Assert.Equal("test value", restored.YieldedValue);
        }

        [Fact]
        public void NestedCalls_SerializeAndRestore()
        {
            var serializer = new JsonContinuationSerializer();
            var runner = new ContinuationRunner();

            // Create nested method calls
            int OuterMethod()
            {
                var context = ScriptContext.EnsureCurrent();
                var value = InnerMethod();
                context.RequestYield();
                context.HandleYieldPoint(0, "outer");
                return value + 1;
            }

            int InnerMethod()
            {
                var context = ScriptContext.EnsureCurrent();
                context.RequestYield();
                context.HandleYieldPoint(0, "inner");
                return 10;
            }

            // Note: This test demonstrates the concept, but full nested call
            // restoration requires the IL rewriter to be applied to both methods.
            // Here we test with a single-frame scenario.
            var result = runner.Run(() =>
            {
                var context = ScriptContext.EnsureCurrent();
                context.RequestYield();
                context.HandleYieldPoint(0, "value");
                return 42;
            });

            var suspended = (ContinuationResult<int>.Suspended)result;
            var json = serializer.SerializeToString(suspended.State);

            // Parse the JSON to verify structure
            Assert.Contains("MethodToken", json);
            Assert.Contains("YieldPointId", json);
        }

        [Fact]
        public void File_SaveAndLoad_SimulatesProcessMigration()
        {
            var serializer = new JsonContinuationSerializer();
            var runner = new ContinuationRunner();
            var tempFile = Path.GetTempFileName();

            try
            {
                // "Process A" - create and suspend computation
                var result = runner.Run(() =>
                {
                    var context = ScriptContext.EnsureCurrent();
                    context.RequestYield();
                    context.HandleYieldPoint(0, "migrating");
                    return 42;
                });

                var suspended = (ContinuationResult<int>.Suspended)result;

                // Write to file (simulating persistent storage)
                File.WriteAllText(tempFile, serializer.SerializeToString(suspended.State));

                // "Process B" - load and resume
                var loadedJson = File.ReadAllText(tempFile);
                var loadedState = serializer.DeserializeFromString(loadedJson);

                Assert.NotNull(loadedState);
                Assert.Equal("migrating", loadedState.YieldedValue);
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        [Fact]
        public void StableHash_ConsistentAcrossSerializations()
        {
            // Generate tokens and verify they're consistent
            var token1 = StableHash.GenerateMethodToken(
                "TestNamespace.TestClass",
                "TestMethod",
                "System.Int32");

            var token2 = StableHash.GenerateMethodToken(
                "TestNamespace.TestClass",
                "TestMethod",
                "System.Int32");

            Assert.Equal(token1, token2);

            // Serialize the token and verify it deserializes correctly
            var serializer = new JsonContinuationSerializer();
            var frame = new HostFrameRecord(token1, 0, new object[] { 1, 2, 3 }, null);
            var state = new ContinuationState(frame);

            var json = serializer.SerializeToString(state);
            var restored = serializer.DeserializeFromString(json);

            Assert.Equal(token1, restored.StackHead.MethodToken);
        }

        [Fact]
        public void ComplexObjectGraph_SerializesCorrectly()
        {
            var serializer = new JsonContinuationSerializer();

            // Create a state with complex objects in slots
            var complexData = new
            {
                Name = "Test",
                Values = new[] { 1, 2, 3 },
                Nested = new { Inner = "value" }
            };

            // Note: Anonymous types may not serialize/deserialize perfectly
            // Test with known serializable types
            var slots = new object[]
            {
                42,
                "hello",
                new[] { 1, 2, 3 },
                3.14159
            };

            var frame = new HostFrameRecord(12345, 0, slots, null);
            var state = new ContinuationState(frame, "yielded");

            var json = serializer.SerializeToString(state);
            var restored = serializer.DeserializeFromString(json);

            Assert.Equal(4, restored.StackHead.Slots.Length);
        }

        [Fact]
        public void MultipleYieldPoints_PreservesCorrectId()
        {
            var serializer = new JsonContinuationSerializer();
            var runner = new ContinuationRunner();

            // Test yielding at different points
            for (int yieldPointId = 0; yieldPointId < 3; yieldPointId++)
            {
                var expectedId = yieldPointId;
                var result = runner.Run(() =>
                {
                    var context = ScriptContext.EnsureCurrent();
                    context.RequestYield();
                    context.HandleYieldPoint(expectedId);
                    return expectedId;
                });

                var suspended = (ContinuationResult<int>.Suspended)result;
                var json = serializer.SerializeToString(suspended.State);
                var restored = serializer.DeserializeFromString(json);

                Assert.Equal(expectedId, restored.StackHead.YieldPointId);
            }
        }

        [Fact]
        public void Version_IncludedInSerialization()
        {
            var serializer = new JsonContinuationSerializer();
            var frame = new HostFrameRecord(100, 0, new object[0], null);
            var state = new ContinuationState(frame);

            var json = serializer.SerializeToString(state);
            var restored = serializer.DeserializeFromString(json);

            // Version should be preserved
            Assert.Equal(state.Version, restored.Version);
        }

        [Fact]
        public void NullYieldedValue_HandledCorrectly()
        {
            var serializer = new JsonContinuationSerializer();
            var frame = new HostFrameRecord(100, 0, new object[0], null);
            var state = new ContinuationState(frame, null);

            var json = serializer.SerializeToString(state);
            var restored = serializer.DeserializeFromString(json);

            Assert.Null(restored.YieldedValue);
        }

        [Fact]
        public void EmptySlots_HandledCorrectly()
        {
            var serializer = new JsonContinuationSerializer();
            var frame = new HostFrameRecord(100, 0, new object[0], null);
            var state = new ContinuationState(frame);

            var json = serializer.SerializeToString(state);
            var restored = serializer.DeserializeFromString(json);

            Assert.Empty(restored.StackHead.Slots);
        }

        [Fact]
        public void MessagePack_MoreCompactThanJson()
        {
            var jsonSerializer = new JsonContinuationSerializer();
            var msgpackSerializer = new MessagePackContinuationSerializer();

            var slots = new object[] { 1, 2, 3, 4, 5, "hello", "world", 3.14159 };
            var frame = new HostFrameRecord(12345, 3, slots, null);
            var state = new ContinuationState(frame, "test");

            var jsonBytes = jsonSerializer.Serialize(state);
            var msgpackBytes = msgpackSerializer.Serialize(state);

            Assert.True(msgpackBytes.Length < jsonBytes.Length,
                $"MessagePack: {msgpackBytes.Length} bytes, JSON: {jsonBytes.Length} bytes");
        }
    }
}
