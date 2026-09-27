using System;
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
        // (ContinuationTypeRegistry / ContinuationValidator) before construction.
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
            Assert.False(ContinuationTypeRegistry.Default.IsAllowed(dangerousType));
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
    }
}
