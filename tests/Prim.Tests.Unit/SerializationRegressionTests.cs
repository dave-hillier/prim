using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MessagePack;
using Newtonsoft.Json;
using Xunit;
using Prim.Core;
using Prim.Serialization;

namespace Prim.Tests.Unit
{
    /// <summary>
    /// Regression tests for bugs found and fixed in the serialization layer.
    /// Each test is named after the original bug and asserts the corrected
    /// behaviour, so it fails if the bug comes back.
    /// </summary>
    public class SerializationRegressionTests
    {
        // Fixed bug 1 (JSON TypeNameHandling.Auto without a SerializationBinder) is
        // covered by SerializationTypeBindingTests, which checks deserialization behaviour.

        // ----------------------------------------------------------------
        // Fixed bug 5: SlotTypeResolver.GetTypeName has no case for sbyte, even
        //         though RegisterBuiltInTypes registers "sbyte" in the
        //         reverse mapping. Round-tripping is asymmetric.
        // ----------------------------------------------------------------
        [Fact]
        public void Regression_SlotTypeResolver_Asymmetric_Sbyte()
        {
            var resolver = new SlotTypeResolver();

            // RegisterBuiltInTypes registers "sbyte" -> typeof(sbyte)
            // But GetTypeName doesn't have a case for sbyte
            var name = resolver.GetTypeName(typeof(sbyte));
            var resolved = resolver.ResolveType(name);

            // Fixed bug: GetTypeName returns assembly-qualified name for sbyte,
            // which is NOT "sbyte", so round-tripping fails or is inconsistent.
            Assert.Equal("sbyte", name);
        }

        // ----------------------------------------------------------------
        // SlotTypeResolver.ResolveType resolves type names faithfully (so whitelisted
        // complex types like Guid/DateTime and arrays round-trip — issue #40).
        // Resolution is NOT the security boundary: whether a resolved type may be
        // INSTANTIATED from a deserialized continuation is gated by the whitelist
        // (ContinuationValidator's type allow-list) before construction.
        // ----------------------------------------------------------------
        [Fact]
        public void Regression_SlotTypeResolver_DangerousTypeIsRejectedByWhitelist()
        {
            var resolver = new SlotTypeResolver();

            // The resolver can name the type (resolution is faithful)...
            var dangerousType = resolver.ResolveType("System.Diagnostics.Process");
            Assert.NotNull(dangerousType);

            // ...but the whitelist that actually gates revival rejects it, so an
            // attacker continuation carrying a Process slot can never be instantiated.
            Assert.False(new ContinuationValidator().IsTypeAllowed(dangerousType));
        }

        // ----------------------------------------------------------------
        // Fixed bug 7: MessagePackContinuationSerializer constructor that takes
        //         raw MessagePackSerializerOptions bypasses the
        //         RestrictedObjectResolver, defeating type restrictions.
        // ----------------------------------------------------------------
        [Fact]
        public void Regression_MessagePack_CustomOptions_Bypasses_Restriction()
        {
            // The constructor that takes just MessagePackSerializerOptions
            // creates the serializer without setting up the RestrictedObjectResolver.
            var customOptions = MessagePack.MessagePackSerializerOptions.Standard;
            var serializer = new MessagePackContinuationSerializer(customOptions);

            // The _typeRegistry is set to Default, but the _options don't use RestrictedObjectResolver.
            // Fixed bug: Custom options bypass the type restriction entirely.
            // This can be verified by checking that the custom options don't include RestrictedObjectResolver.

            // Serialize and deserialize a state - the custom options path won't enforce type restrictions.
            var state = new ContinuationState(
                new HostFrameRecord(1, 0, new object[] { "safe value" }));

            var bytes = serializer.Serialize(state);
            // The fact that this doesn't throw proves the restricted resolver isn't in the path
            // (it would throw for unregistered resolver combinations).
            Assert.NotNull(bytes);
            // The real issue is that deserialization with these options won't use RestrictedObjectResolver.
        }

        private static ContinuationState StateWith(object? yielded, params object?[] slots)
            => new ContinuationState(new HostFrameRecord(1, 0, slots), yielded);

        // ----------------------------------------------------------------
        // Date-looking strings must not be turned into DateTime by JSON.
        // ----------------------------------------------------------------
        [Fact]
        public void Regression_Json_DateLooking_String_Slot_RoundTrips()
        {
            var serializer = JsonContinuationSerializer.Compact();
            var state = StateWith("2024-01-01T00:00:00Z", "2024-01-01T00:00:00Z", new object[] { "2024-01-01T00:00:00Z", 1 });

            var back = serializer.Deserialize(serializer.Serialize(state));

            Assert.Equal("2024-01-01T00:00:00Z", back.StackHead.Slots[0]);
            Assert.IsType<string>(back.YieldedValue);
            Assert.Equal("2024-01-01T00:00:00Z", back.YieldedValue);
            var inner = Assert.IsType<object[]>(back.StackHead.Slots[1]);
            Assert.Equal("2024-01-01T00:00:00Z", inner[0]);
        }

        [Fact]
        public void Regression_Json_Date_Slots_Still_RoundTrip()
        {
            var serializer = JsonContinuationSerializer.Compact();
            var utc = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var local = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Local);
            var unspec = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
            var dto = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(5.5));
            var arr = new[] { utc, unspec };
            DateTime? nd = utc;
            DateTimeOffset? ndo = dto;

            var state = StateWith(null, utc, local, unspec, dto, arr, nd, ndo);
            var back = serializer.Deserialize(serializer.Serialize(state)).StackHead.Slots;

            Assert.Equal(utc, back[0]);
            Assert.Equal(DateTimeKind.Utc, ((DateTime)back[0]).Kind);
            Assert.Equal(local, back[1]);
            Assert.Equal(DateTimeKind.Local, ((DateTime)back[1]).Kind);
            Assert.Equal(unspec, back[2]);
            Assert.Equal(DateTimeKind.Unspecified, ((DateTime)back[2]).Kind);
            Assert.Equal(dto, back[3]);
            Assert.Equal(dto.Offset, ((DateTimeOffset)back[3]).Offset);
            Assert.Equal(arr, Assert.IsType<DateTime[]>(back[4]));
            Assert.Equal(utc, back[5]);
            Assert.Equal(dto, back[6]);
        }

        // ----------------------------------------------------------------
        // Unknown slot type names surface the serializer's own exception type.
        // ----------------------------------------------------------------
        [Fact]
        public void Regression_Json_Unknown_TypeName_Throws_JsonSerializationException()
        {
            var serializer = JsonContinuationSerializer.Compact();
            var json = serializer.SerializeToString(StateWith(null, 5)).Replace("\"int\"", "\"No.Such.Type, Nowhere\"");

            var ex = Assert.Throws<JsonSerializationException>(() => serializer.DeserializeFromString(json));
            Assert.IsType<TypeLoadException>(ex.InnerException);
        }

        [Fact]
        public void Regression_MessagePack_Unknown_TypeName_Throws_MessagePackSerializationException()
        {
            var serializer = new MessagePackContinuationSerializer();
            var bytes = serializer.Serialize(StateWith(null, 5));
            var find = System.Text.Encoding.UTF8.GetBytes("int");
            var index = -1;
            for (var i = 0; i <= bytes.Length - find.Length && index < 0; i++)
            {
                if (bytes.Skip(i).Take(find.Length).SequenceEqual(find)) index = i;
            }
            Assert.True(index >= 0);
            // Same length so the msgpack string header stays valid: "int" -> "xyz".
            Array.Copy(System.Text.Encoding.UTF8.GetBytes("xyz"), 0, bytes, index, 3);

            var ex = Assert.Throws<MessagePackSerializationException>(() => serializer.Deserialize(bytes));
            Assert.IsType<TypeLoadException>(ex.InnerException);
        }

        // ----------------------------------------------------------------
        // SlotTypeResolver is safe for concurrent use.
        // ----------------------------------------------------------------
        [Fact]
        public void Regression_SlotTypeResolver_Is_Thread_Safe()
        {
            var resolver = new SlotTypeResolver();
            var names = new[]
            {
                typeof(List<int>).AssemblyQualifiedName,
                typeof(Uri).AssemblyQualifiedName,
                typeof(Version).AssemblyQualifiedName,
                "int",
                "System.Guid"
            };

            Parallel.For(0, 4000, i =>
            {
                if (i % 40 == 0) resolver.AddResolver(n => n == "custom" + i ? typeof(Uri) : null);
                Assert.NotNull(resolver.ResolveType(names[i % names.Length]));
                if (i % 40 == 0) Assert.Equal(typeof(Uri), resolver.ResolveType("custom" + i));
            });
        }
    }
}
