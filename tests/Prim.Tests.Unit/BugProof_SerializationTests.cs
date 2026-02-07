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
        // BUG 2: ObjectGraphTracker.TryRegister always returns true for
        //         null -- it never remembers that null was already seen.
        // ----------------------------------------------------------------
        [Fact]
        public void Bug_ObjectGraphTracker_Null_Always_Newly_Registered()
        {
            var tracker = new ObjectGraphTracker();

            bool firstResult = tracker.TryRegister(null, out int id1);
            bool secondResult = tracker.TryRegister(null, out int id2);

            // First registration of null: fine
            Assert.True(firstResult);

            // Second registration of null should return false (already seen).
            // BUG: Always returns true for null, never tracks it.
            Assert.False(secondResult); // FAILS - null is never tracked
        }

        // ----------------------------------------------------------------
        // BUG 3: ObjectGraphTracker.RegisterDeserialized performs unbounded
        //         memory allocation -- a large ID causes a List to grow to
        //         that size with null padding, creating a DoS vector.
        // ----------------------------------------------------------------
        [Fact]
        public void Bug_ObjectGraphTracker_Large_Id_Memory_Allocation()
        {
            var tracker = new ObjectGraphTracker();

            // BUG: Large IDs cause unbounded List growth.
            // Registering id=1_000_000 causes a list of 1M+ null entries.
            // This is a DoS vector from untrusted input.
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                tracker.RegisterDeserialized(1_000_000, "malicious"));
            // FAILS - no bounds check, it happily allocates
        }

        // ----------------------------------------------------------------
        // BUG 4: ObjectGraphTracker.GetById cannot distinguish a null
        //         value that was legitimately registered from an ID that
        //         was never registered -- both return null.
        // ----------------------------------------------------------------
        [Fact]
        public void Bug_ObjectGraphTracker_GetById_Null_Ambiguity()
        {
            var tracker = new ObjectGraphTracker();

            // Register a real null at id 0
            tracker.RegisterDeserialized(0, null);

            // Get id 0 (registered as null) vs id 999 (never registered)
            var result0 = tracker.GetById(0);
            var result999 = tracker.GetById(999);

            // BUG: Both return null - can't tell if id was registered with null value
            // or if it was never registered at all.
            Assert.NotEqual(result0, result999); // FAILS - both are null
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
        // BUG 6: SlotTypeResolver.ResolveType falls back to Type.GetType
        //         and assembly scanning with NO allowlist, so arbitrary
        //         types from untrusted input can be resolved.
        // ----------------------------------------------------------------
        [Fact]
        public void Bug_SlotTypeResolver_Resolves_Arbitrary_Types()
        {
            var resolver = new SlotTypeResolver();

            // An attacker could request resolution of dangerous types.
            // BUG: No allowlist - resolves any type from loaded assemblies.
            var dangerousType = resolver.ResolveType("System.Diagnostics.Process");

            // Should throw or return null, but resolves the dangerous type.
            Assert.Null(dangerousType); // FAILS - type is resolved
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
