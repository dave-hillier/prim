using System;
using Prim.Serialization;
using Xunit;

namespace Prim.Tests.Unit
{
    /// <summary>
    /// TDD tests for SlotTypeResolver edge cases.
    /// Targets null/empty handling, custom resolver behavior,
    /// caching, and the GetTypeName/ResolveType round-trip gap.
    /// </summary>
    public class SlotTypeResolverEdgeCaseTests
    {
        #region ResolveType - Null/Empty Handling

        [Fact]
        public void ResolveType_Null_ReturnsNull()
        {
            var resolver = new SlotTypeResolver();

            Assert.Null(resolver.ResolveType(null));
        }

        [Fact]
        public void ResolveType_EmptyString_ReturnsNull()
        {
            var resolver = new SlotTypeResolver();

            Assert.Null(resolver.ResolveType(""));
        }

        [Fact]
        public void ResolveType_UnknownType_ReturnsNull()
        {
            var resolver = new SlotTypeResolver();

            Assert.Null(resolver.ResolveType("CompletelyFakeType.ThatDoesNotExist"));
        }

        #endregion

        #region ResolveType - Built-In Types

        [Theory]
        [InlineData("int", typeof(int))]
        [InlineData("long", typeof(long))]
        [InlineData("short", typeof(short))]
        [InlineData("byte", typeof(byte))]
        [InlineData("sbyte", typeof(sbyte))]
        [InlineData("bool", typeof(bool))]
        [InlineData("float", typeof(float))]
        [InlineData("double", typeof(double))]
        [InlineData("decimal", typeof(decimal))]
        [InlineData("char", typeof(char))]
        [InlineData("string", typeof(string))]
        [InlineData("object", typeof(object))]
        public void ResolveType_ShortNames_ResolveCorrectly(string shortName, Type expectedType)
        {
            var resolver = new SlotTypeResolver();

            Assert.Equal(expectedType, resolver.ResolveType(shortName));
        }

        [Theory]
        [InlineData("System.Int32", typeof(int))]
        [InlineData("System.Int64", typeof(long))]
        [InlineData("System.Int16", typeof(short))]
        [InlineData("System.Byte", typeof(byte))]
        [InlineData("System.SByte", typeof(sbyte))]
        [InlineData("System.Boolean", typeof(bool))]
        [InlineData("System.Single", typeof(float))]
        [InlineData("System.Double", typeof(double))]
        [InlineData("System.Decimal", typeof(decimal))]
        [InlineData("System.Char", typeof(char))]
        [InlineData("System.String", typeof(string))]
        [InlineData("System.Object", typeof(object))]
        public void ResolveType_FullNames_ResolveCorrectly(string fullName, Type expectedType)
        {
            var resolver = new SlotTypeResolver();

            Assert.Equal(expectedType, resolver.ResolveType(fullName));
        }

        [Theory]
        [InlineData("System.DateTime", typeof(DateTime))]
        [InlineData("System.TimeSpan", typeof(TimeSpan))]
        [InlineData("System.Guid", typeof(Guid))]
        [InlineData("System.DateTimeOffset", typeof(DateTimeOffset))]
        public void ResolveType_CommonTypes_ResolveCorrectly(string fullName, Type expectedType)
        {
            var resolver = new SlotTypeResolver();

            Assert.Equal(expectedType, resolver.ResolveType(fullName));
        }

        #endregion

        #region GetTypeName - Built-In Types

        [Theory]
        [InlineData(typeof(int), "int")]
        [InlineData(typeof(long), "long")]
        [InlineData(typeof(short), "short")]
        [InlineData(typeof(byte), "byte")]
        [InlineData(typeof(bool), "bool")]
        [InlineData(typeof(float), "float")]
        [InlineData(typeof(double), "double")]
        [InlineData(typeof(decimal), "decimal")]
        [InlineData(typeof(char), "char")]
        [InlineData(typeof(string), "string")]
        [InlineData(typeof(object), "object")]
        public void GetTypeName_BuiltInTypes_ReturnShortNames(Type type, string expectedName)
        {
            var resolver = new SlotTypeResolver();

            Assert.Equal(expectedName, resolver.GetTypeName(type));
        }

        [Fact]
        public void GetTypeName_Null_ReturnsNull()
        {
            var resolver = new SlotTypeResolver();

            Assert.Null(resolver.GetTypeName(null));
        }

        [Fact]
        public void GetTypeName_CustomType_ReturnsAssemblyQualifiedName()
        {
            var resolver = new SlotTypeResolver();

            var name = resolver.GetTypeName(typeof(DateTime));

            // DateTime is not in the shortname list, so it should return
            // the assembly-qualified name
            Assert.Equal(typeof(DateTime).AssemblyQualifiedName, name);
        }

        #endregion

        #region GetTypeName/ResolveType Round-Trip Gap

        [Fact]
        public void GetTypeName_Then_ResolveType_RoundTrips_ForBuiltInTypes()
        {
            var resolver = new SlotTypeResolver();

            var builtInTypes = new[]
            {
                typeof(int), typeof(long), typeof(short), typeof(byte),
                typeof(bool), typeof(float), typeof(double), typeof(decimal),
                typeof(char), typeof(string), typeof(object)
            };

            foreach (var type in builtInTypes)
            {
                var name = resolver.GetTypeName(type);
                var resolved = resolver.ResolveType(name);
                Assert.Equal(type, resolved);
            }
        }

        [Fact]
        public void GetTypeName_Then_ResolveType_Works_ForNonBuiltInTypes()
        {
            // GetTypeName for non-built-in types returns AssemblyQualifiedName.
            // ResolveType should be able to resolve assembly-qualified names
            // via Type.GetType or assembly scanning.
            var resolver = new SlotTypeResolver();

            var name = resolver.GetTypeName(typeof(DateTime));
            var resolved = resolver.ResolveType(name);

            Assert.Equal(typeof(DateTime), resolved);
        }

        #endregion

        #region Custom Resolvers

        [Fact]
        public void AddResolver_NullResolver_ThrowsArgumentNull()
        {
            var resolver = new SlotTypeResolver();

            Assert.Throws<ArgumentNullException>(() =>
                resolver.AddResolver(null));
        }

        [Fact]
        public void AddResolver_CustomResolver_IsUsedForUnknownTypes()
        {
            var resolver = new SlotTypeResolver();
            var customCalled = false;

            resolver.AddResolver(name =>
            {
                if (name == "MyCustomType")
                {
                    customCalled = true;
                    return typeof(int); // Map to int for test purposes
                }
                return null;
            });

            var result = resolver.ResolveType("MyCustomType");

            Assert.True(customCalled);
            Assert.Equal(typeof(int), result);
        }

        [Fact]
        public void AddResolver_CustomResolver_ResultIsCached()
        {
            var resolver = new SlotTypeResolver();
            var callCount = 0;

            resolver.AddResolver(name =>
            {
                if (name == "MyCustomType")
                {
                    callCount++;
                    return typeof(int);
                }
                return null;
            });

            resolver.ResolveType("MyCustomType");
            resolver.ResolveType("MyCustomType");

            // Second call should use cache, so resolver only called once
            Assert.Equal(1, callCount);
        }

        [Fact]
        public void AddResolver_MultipleResolvers_TriedInOrder()
        {
            var resolver = new SlotTypeResolver();
            var order = new System.Collections.Generic.List<int>();

            resolver.AddResolver(name =>
            {
                order.Add(1);
                return null; // First resolver doesn't know this type
            });

            resolver.AddResolver(name =>
            {
                order.Add(2);
                return name == "MyType" ? typeof(string) : null;
            });

            resolver.ResolveType("MyType");

            Assert.Equal(new[] { 1, 2 }, order);
        }

        [Fact]
        public void AddResolver_BuiltInTypes_TakePrecedence()
        {
            var resolver = new SlotTypeResolver();
            var customCalled = false;

            resolver.AddResolver(name =>
            {
                customCalled = true;
                return typeof(string); // Try to override int
            });

            var result = resolver.ResolveType("int");

            // Built-in types are resolved from cache first, so custom resolver not called
            Assert.False(customCalled);
            Assert.Equal(typeof(int), result);
        }

        #endregion

        #region Caching Behavior

        [Fact]
        public void ResolveType_CachesResults_ForTypeGetType()
        {
            var resolver = new SlotTypeResolver();

            // First resolution should cache
            var result1 = resolver.ResolveType("System.DateTime");
            var result2 = resolver.ResolveType("System.DateTime");

            Assert.Same(result1, result2);
        }

        #endregion
    }
}
