using System;
using Xunit;
using Prim.Core;
using Prim.Serialization;
using Newtonsoft.Json;

namespace Prim.Tests.Unit
{
    /// <summary>
    /// A slot value's <c>$type</c> must be checked against an allow-list BEFORE
    /// Json.NET constructs the object. Validation at resume is too late: by then
    /// the constructor and setters of whatever type the payload named have run.
    /// </summary>
    [Collection(nameof(JsonTypeBindingTests))]
    public class JsonTypeBindingTests
    {
        public sealed class Gadget
        {
            public static int Constructed;

            public Gadget()
            {
                Constructed++;
            }

            public int X { get; set; }
        }

        public sealed class Point
        {
            public int X { get; set; }
            public int Y { get; set; }
        }

        private static string PayloadWithSlotType(Type type)
        {
            var typeName = JsonConvert.ToString(type.AssemblyQualifiedName);
            return "{\"Version\":1,\"StackHead\":{\"MethodToken\":1,\"YieldPointId\":0,\"Slots\":[" +
                   "{\"TypeName\":\"int\",\"Value\":{\"$type\":" + typeName + ",\"X\":1}}" +
                   "],\"Caller\":null}}";
        }

        [Fact]
        public void Deserialize_UnlistedSlotType_ThrowsWithoutConstructingIt()
        {
            Gadget.Constructed = 0;
            var serializer = new JsonContinuationSerializer();

            Assert.Throws<ValidationException>(
                () => serializer.DeserializeFromString(PayloadWithSlotType(typeof(Gadget))));
            Assert.Equal(0, Gadget.Constructed);
        }

        [Fact]
        public void Deserialize_UnlistedSlotType_Compact_Throws()
        {
            Gadget.Constructed = 0;
            var serializer = JsonContinuationSerializer.Compact();

            Assert.Throws<ValidationException>(
                () => serializer.DeserializeFromString(PayloadWithSlotType(typeof(Gadget))));
            Assert.Equal(0, Gadget.Constructed);
        }

        [Fact]
        public void Deserialize_UnlistedSlotType_CallerSettingsWithoutBinder_Throws()
        {
            Gadget.Constructed = 0;
            var serializer = new JsonContinuationSerializer(new JsonSerializerSettings());

            Assert.Throws<ValidationException>(
                () => serializer.DeserializeFromString(PayloadWithSlotType(typeof(Gadget))));
            Assert.Equal(0, Gadget.Constructed);
        }

        [Fact]
        public void RoundTrip_TypeInRegistry_Succeeds()
        {
            var registry = ContinuationTypeRegistry.Default.With(typeof(Point));
            var serializer = new JsonContinuationSerializer(registry);
            var state = new ContinuationState(
                new HostFrameRecord(1, 0, new object[] { new Point { X = 3, Y = 4 } }));

            var restored = serializer.DeserializeFromString(serializer.SerializeToString(state));

            var point = Assert.IsType<Point>(restored.StackHead.Slots[0]);
            Assert.Equal(3, point.X);
            Assert.Equal(4, point.Y);
        }

        [Fact]
        public void RoundTrip_TypeAllowedByValidator_Succeeds()
        {
            var validator = new ContinuationValidator(ValidationOptions.Lenient);
            validator.RegisterAllowedType(typeof(Point));
            var serializer = new JsonContinuationSerializer { Validator = validator };
            var state = new ContinuationState(
                new HostFrameRecord(1, 0, new object[] { new Point { X = 5, Y = 6 } }));

            var restored = serializer.DeserializeFromString(serializer.SerializeToString(state));

            Assert.Equal(5, Assert.IsType<Point>(restored.StackHead.Slots[0]).X);
        }

        [Fact]
        public void RoundTrip_BuiltInArraySlot_Succeeds()
        {
            var serializer = new JsonContinuationSerializer();
            var state = new ContinuationState(
                new HostFrameRecord(1, 0, new object[] { new[] { 1, 2, 3 }, new[] { "a", null } }));

            var restored = serializer.DeserializeFromString(serializer.SerializeToString(state));

            Assert.Equal(new[] { 1, 2, 3 }, restored.StackHead.Slots[0]);
            Assert.Equal(new[] { "a", null }, restored.StackHead.Slots[1]);
        }
    }
}
