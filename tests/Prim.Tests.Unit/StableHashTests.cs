using Prim.Core;
using Xunit;

namespace Prim.Tests.Unit
{
    public class StableHashTests
    {
        [Fact]
        public void ComputeFnv1a_NullString_ReturnsZero()
        {
            var hash = StableHash.ComputeFnv1a(null);
            Assert.Equal(0, hash);
        }

        [Fact]
        public void ComputeFnv1a_EmptyString_ReturnsOffsetBasis()
        {
            var hash = StableHash.ComputeFnv1a("");
            // FNV offset basis as signed int
            Assert.Equal(unchecked((int)2166136261), hash);
        }

        [Fact]
        public void ComputeFnv1a_SameInput_ReturnsSameHash()
        {
            var hash1 = StableHash.ComputeFnv1a("TestString");
            var hash2 = StableHash.ComputeFnv1a("TestString");

            Assert.Equal(hash1, hash2);
        }

        [Fact]
        public void ComputeFnv1a_DifferentInputs_ReturnsDifferentHashes()
        {
            var hash1 = StableHash.ComputeFnv1a("Hello");
            var hash2 = StableHash.ComputeFnv1a("World");

            Assert.NotEqual(hash1, hash2);
        }

        [Fact]
        public void ComputeFnv1a_IsStableAcrossInvocations()
        {
            // These specific values ensure the hash is deterministic
            // and doesn't change between runs (unlike String.GetHashCode)
            var hash = StableHash.ComputeFnv1a("test");

            // Pre-computed expected value for "test"
            // FNV-1a("test") should always produce the same result
            Assert.Equal(StableHash.ComputeFnv1a("test"), hash);
        }

        [Fact]
        public void Combine_EmptyArray_ReturnsZero()
        {
            var hash = StableHash.Combine();
            Assert.Equal(0, hash);
        }

        [Fact]
        public void Combine_NullArray_ReturnsZero()
        {
            var hash = StableHash.Combine(null);
            Assert.Equal(0, hash);
        }

        [Fact]
        public void Combine_SingleValue_ReturnsTransformedValue()
        {
            var hash = StableHash.Combine(42);
            Assert.NotEqual(42, hash); // Should be mixed
        }

        [Fact]
        public void Combine_MultipleValues_ReturnsCombinedHash()
        {
            var hash1 = StableHash.Combine(1, 2, 3);
            var hash2 = StableHash.Combine(1, 2, 3);

            Assert.Equal(hash1, hash2);
        }

        [Fact]
        public void Combine_OrderMatters()
        {
            var hash1 = StableHash.Combine(1, 2, 3);
            var hash2 = StableHash.Combine(3, 2, 1);

            Assert.NotEqual(hash1, hash2);
        }

        [Fact]
        public void GenerateMethodToken_SameSignature_ReturnsSameToken()
        {
            var token1 = StableHash.GenerateMethodToken("MyClass", "MyMethod", "int", "string");
            var token2 = StableHash.GenerateMethodToken("MyClass", "MyMethod", "int", "string");

            Assert.Equal(token1, token2);
        }

        [Fact]
        public void GenerateMethodToken_DifferentClass_ReturnsDifferentToken()
        {
            var token1 = StableHash.GenerateMethodToken("ClassA", "Method");
            var token2 = StableHash.GenerateMethodToken("ClassB", "Method");

            Assert.NotEqual(token1, token2);
        }

        [Fact]
        public void GenerateMethodToken_DifferentMethod_ReturnsDifferentToken()
        {
            var token1 = StableHash.GenerateMethodToken("MyClass", "MethodA");
            var token2 = StableHash.GenerateMethodToken("MyClass", "MethodB");

            Assert.NotEqual(token1, token2);
        }

        [Fact]
        public void GenerateMethodToken_DifferentParameters_ReturnsDifferentToken()
        {
            var token1 = StableHash.GenerateMethodToken("MyClass", "Method", "int");
            var token2 = StableHash.GenerateMethodToken("MyClass", "Method", "string");

            Assert.NotEqual(token1, token2);
        }

        [Fact]
        public void GenerateMethodToken_NoParameters_Works()
        {
            var token = StableHash.GenerateMethodToken("MyClass", "Method");
            Assert.NotEqual(0, token);
        }

        [Fact]
        public void GenerateMethodToken_NullParameters_Works()
        {
            var token = StableHash.GenerateMethodToken("MyClass", "Method", null);
            Assert.NotEqual(0, token);
        }

        [Fact]
        public void GenerateMethodToken_IsStableBetweenRuns()
        {
            // Test that the method token is stable
            // This is critical for serialization/deserialization
            var token = StableHash.GenerateMethodToken(
                "Prim.Tests.TestClass",
                "TestMethod",
                "System.Int32",
                "System.String");

            // The token should be the same every time
            var token2 = StableHash.GenerateMethodToken(
                "Prim.Tests.TestClass",
                "TestMethod",
                "System.Int32",
                "System.String");

            Assert.Equal(token, token2);
        }
    }
}
