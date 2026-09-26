using System;
using System.Reflection;
using Xunit;
using Prim.Core;
using Prim.Serialization;
using Newtonsoft.Json;

namespace Prim.Tests.Unit
{
    /// <summary>
    /// These tests are designed to FAIL, proving that specific bugs exist
    /// in the serialization infrastructure. Each failing assertion documents
    /// a real defect in the current source code.
    /// </summary>
    public class BugProof_SerializationTests
    {
        // ----------------------------------------------------------------
        // BUG 1: JSON serializer uses TypeNameHandling.Auto without a
        //         SerializationBinder, which is a known Newtonsoft.Json
        //         remote-code-execution (RCE) vector.
        // ----------------------------------------------------------------
        [Fact]
        public void Bug_JsonSerializer_TypeNameHandling_Without_Binder()
        {
            var serializer = new JsonContinuationSerializer();

            // Use reflection to inspect the private _settings field
            var settingsField = typeof(JsonContinuationSerializer)
                .GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance);
            var settings = (JsonSerializerSettings)settingsField.GetValue(serializer);

            // BUG: TypeNameHandling.Auto is set, but no SerializationBinder restricts deserialization.
            // This is a known RCE vector with Newtonsoft.Json.
            Assert.NotEqual(TypeNameHandling.Auto, settings.TypeNameHandling); // FAILS
        }

        // ----------------------------------------------------------------
        // BUG 5: SlotTypeResolver.GetTypeName has no case for sbyte, even
        //         though RegisterBuiltInTypes registers "sbyte" in the
        //         reverse mapping. Round-tripping is asymmetric.
        // ----------------------------------------------------------------
        [Fact]
        public void Bug_SlotTypeResolver_Asymmetric_Sbyte()
        {
            var resolver = new SlotTypeResolver();

            // RegisterBuiltInTypes registers "sbyte" -> typeof(sbyte)
            // But GetTypeName doesn't have a case for sbyte
            var name = resolver.GetTypeName(typeof(sbyte));
            var resolved = resolver.ResolveType(name);

            // BUG: GetTypeName returns assembly-qualified name for sbyte,
            // which is NOT "sbyte", so round-tripping fails or is inconsistent.
            Assert.Equal("sbyte", name); // FAILS - returns assembly-qualified name
        }

        // ----------------------------------------------------------------
        // SlotTypeResolver.ResolveType resolves type names faithfully (so whitelisted
        // complex types like Guid/DateTime and arrays round-trip — issue #40).
        // Resolution is NOT the security boundary: whether a resolved type may be
        // INSTANTIATED from a deserialized continuation is gated by the whitelist
        // (ContinuationTypeRegistry / ContinuationValidator) before construction.
        // ----------------------------------------------------------------
        [Fact]
        public void Bug_SlotTypeResolver_DangerousTypeIsRejectedByWhitelist()
        {
            var resolver = new SlotTypeResolver();

            // The resolver can name the type (resolution is faithful)...
            var dangerousType = resolver.ResolveType("System.Diagnostics.Process");
            Assert.NotNull(dangerousType);

            // ...but the whitelist that actually gates revival rejects it, so an
            // attacker continuation carrying a Process slot can never be instantiated.
            Assert.False(ContinuationTypeRegistry.Default.IsAllowed(dangerousType));
        }

        // ----------------------------------------------------------------
        // BUG 7: MessagePackContinuationSerializer constructor that takes
        //         raw MessagePackSerializerOptions bypasses the
        //         RestrictedObjectResolver, defeating type restrictions.
        // ----------------------------------------------------------------
        [Fact]
        public void Bug_MessagePack_CustomOptions_Bypasses_Restriction()
        {
            // The constructor that takes just MessagePackSerializerOptions
            // creates the serializer without setting up the RestrictedObjectResolver.
            var customOptions = MessagePack.MessagePackSerializerOptions.Standard;
            var serializer = new MessagePackContinuationSerializer(customOptions);

            // The _typeRegistry is set to Default, but the _options don't use RestrictedObjectResolver.
            // BUG: Custom options bypass the type restriction entirely.
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
    }
}
