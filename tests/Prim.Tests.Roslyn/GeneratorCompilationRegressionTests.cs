using System.Linq;
using System.Text.RegularExpressions;
using Prim.Core;
using Xunit;

namespace Prim.Tests.Roslyn
{
    /// <summary>
    /// Regression tests for generated code that did not compile (review items B1-B6),
    /// for shapes that are now rejected with a diagnostic instead, and for method tokens
    /// and attribute matching. Each test runs the generator over a snippet and asserts
    /// the resulting compilation has no errors.
    /// </summary>
    public class GeneratorCompilationRegressionTests
    {
        private static T Run<T>(string source, params string[] methods)
        {
            var go = GeneratorHarness.CompileEntry<T>(source, methods);
            var plain = GeneratorHarness.RunToCompletion(go, int.MaxValue);
            var sliced = GeneratorHarness.RunToCompletion(go, 7);
            Assert.Equal(plain.Value, sliced.Value);
            return sliced.Value;
        }

        // ---- B1: the user's using directives ----

        [Fact]
        public void B1_UserUsingsAliasesAndStaticUsingsAreCopied()
        {
            var src = @"
using System.IO;
using System.Linq;
using Prim.Core;
using static System.Math;
using IntList = System.Collections.Generic.List<int>;

namespace Snippet
{
    using System.Text;

    public partial class C
    {
        [Continuable]
        public int M()
        {
            var list = new IntList { -3, 1, 2 };
            int total = 0;
            foreach (var x in list.Where(v => v != 0)) total += Abs(x);
            using (var ms = new MemoryStream()) { ms.WriteByte(1); total += (int)ms.Length; }
            var sb = new StringBuilder(""ab"");
            return total + sb.Length;
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(); }
}";
            Assert.Equal(9, Run<int>(src, "M"));
        }

        [Fact]
        public void B1_PartialTypeAcrossFilesUsesEachFilesUsings()
        {
            var a = @"
using System.IO;
using Prim.Core;
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int A() { int n = 0; for (int i = 0; i < 3; i++) { using (var ms = new MemoryStream()) n++; } return n; }
    }
    public static class Entry { public static int Go() => new C().B_Continuable(); }
}";
            var b = @"
using System.Linq;
using Prim.Core;
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int B() { int n = A(); return n + new[] { 1, 2 }.Sum(); }
    }
}";
            var result = GeneratorHarness.AssertCompiles(new[] { a, b }, "A", "B");
            // One file per partial declaration (each with its own usings) plus the
            // shared replay helper.
            Assert.Equal(3, result.Run.GeneratedTrees.Length);
        }

        // ---- B2: type shape ----

        [Fact]
        public void B2_PartialStructRecordAndRecordStruct()
        {
            var src = @"
using Prim.Core;
namespace Snippet
{
    public partial struct S
    {
        public int Seed;
        [Continuable]
        public int M() { int t = Seed; for (int i = 0; i < 4; i++) t += i; return t; }
    }

    public partial record R(int Seed)
    {
        [Continuable]
        public int M() { int t = Seed; for (int i = 0; i < 4; i++) t += i; return t; }
    }

    public readonly partial record struct RS(int Seed)
    {
        [Continuable]
        public int M() { int t = Seed; for (int i = 0; i < 4; i++) t += i; return t; }
    }

    public static class Entry
    {
        public static int Go() => new S { Seed = 1 }.M_Continuable() + new R(10).M_Continuable() + new RS(100).M_Continuable();
    }
}";
            Assert.Equal(111 + 18, Run<int>(src, "M"));
        }

        [Fact]
        public void B2_StaticAndPrivateMethodsKeepTheirModifiers()
        {
            var src = @"
using Prim.Core;
namespace Snippet
{
    public static partial class Util
    {
        [Continuable]
        public static int Sum(int n) { int t = 0; for (int i = 1; i <= n; i++) t += i; return t; }

        [Continuable]
        private static int Twice(int n) { int s = Sum(n); return s * 2; }

        public static int CallTwice(int n) => Twice_Continuable(n);
    }
    public static class Entry { public static int Go() => Util.Sum_Continuable(4) + Util.CallTwice(3); }
}";
            var result = GeneratorHarness.AssertCompiles(src, "Sum", "Twice");
            Assert.Contains("public static int Sum_Continuable(int n)", result.GeneratedText);
            Assert.Contains("private static int Twice_Continuable(int n)", result.GeneratedText);
            Assert.Equal(10 + 12, Run<int>(src, "Sum", "Twice"));
        }

        [Fact]
        public void B2_NestedTypesAndNestedNamespaces()
        {
            var src = @"
using Prim.Core;
namespace A
{
    namespace B
    {
        public partial class Outer
        {
            public partial class Inner
            {
                [Continuable]
                public int M() { int t = 0; for (int i = 0; i < 5; i++) t += i; return t; }
            }
        }
    }
}
namespace A.B
{
    public static class Entry { public static int Go() => new Outer.Inner().M_Continuable(); }
}";
            Assert.Equal(10, Run<int>(src, "M"));
        }

        [Fact]
        public void B2_FileScopedNamespace()
        {
            var src = @"
using Prim.Core;
namespace Snippet.Deep;

public partial class C
{
    [Continuable]
    public int M() { int t = 0; for (int i = 0; i < 5; i++) t += i; return t; }
}
public static class Entry { public static int Go() => new C().M_Continuable(); }";
            Assert.Equal(10, Run<int>(src, "M"));
        }

        [Fact]
        public void B2_NestedTypeInNonPartialOuter_IsRejected()
        {
            var src = @"
using Prim.Core;
namespace Snippet
{
    public class Outer
    {
        public partial class Inner
        {
            [Continuable]
            public int M() { int t = 0; for (int i = 0; i < 5; i++) t += i; return t; }
        }
    }
}";
            var diag = GeneratorHarness.AssertRejected(src, "PRIM002", "M");
            Assert.Contains("'Outer' must also be partial", diag.GetMessage());
        }

        [Fact]
        public void B2_NonPartialStruct_IsRejected()
        {
            var src = @"
using Prim.Core;
namespace Snippet
{
    public struct S
    {
        [Continuable]
        public int M() { int t = 0; for (int i = 0; i < 5; i++) t += i; return t; }
    }
}";
            GeneratorHarness.AssertRejected(src, "PRIM002", "M");
        }

        [Fact]
        public void B2_RefReturningMethod_IsRejected()
        {
            var src = @"
using Prim.Core;
namespace Snippet
{
    public partial class C
    {
        private int _f;
        [Continuable]
        public ref int M() { for (int i = 0; i < 2; i++) _f++; return ref _f; }
    }
}";
            GeneratorHarness.AssertRejected(src, "PRIM002", "M");
        }

        // ---- B3: non-partial classes ----

        [Fact]
        public void B3_NonPartialClass_QualifiesInstanceMembersAndPassesInstanceToNestedCalls()
        {
            var src = @"
using Prim.Core;
namespace Snippet
{
    public class Counter
    {
        public int Field = 5;
        public int Prop { get; set; } = 7;
        public static int Shared = 100;
        public int Help(int x) => x + 1;
        public static int StaticHelp(int x) => x * 2;

        [Continuable]
        public int Inner(int n) { int s = 0; for (int i = 0; i < n; i++) s += Field; return s; }

        [Continuable]
        public int Outer()
        {
            int t = Help(Prop) + StaticHelp(1) + Shared + this.Field;
            int fromInner = Inner(3);
            Counter other = new Counter { Field = 1 };
            int fromOther = other.Inner(2);
            int fromStatic = Twice(4);
            return t + fromInner + fromOther + fromStatic;
        }

        [Continuable]
        public static int Twice(int n) { int r = 0; for (int i = 0; i < 2; i++) r += n; return r; }
    }

    public static class Entry { public static int Go() => CounterContinuations.Outer_Continuable(new Counter()); }
}";
            // (8 + 2 + 100 + 5) + 15 + 2 + 8 = 140
            Assert.Equal(140, Run<int>(src, "Inner", "Outer", "Twice"));
        }

        [Fact]
        public void B3_NonPartialClass_PrivateMemberAccess_IsRejected()
        {
            var src = @"
using Prim.Core;
namespace Snippet
{
    public class C
    {
        private int _f = 3;
        [Continuable]
        public int M() { int t = 0; for (int i = 0; i < 3; i++) t += _f; return t; }
    }
}";
            var diag = GeneratorHarness.AssertRejected(src, "PRIM004", "M");
            Assert.Contains("_f", diag.GetMessage());
        }

        [Fact]
        public void B3_NonPartialClass_BaseAccess_IsRejected()
        {
            var src = @"
using Prim.Core;
namespace Snippet
{
    public class B { public virtual int V() => 1; }
    public class C : B
    {
        public override int V() => 2;
        [Continuable]
        public int M() { int t = 0; for (int i = 0; i < 3; i++) t += base.V(); return t; }
    }
}";
            GeneratorHarness.AssertRejected(src, "PRIM004", "M");
        }

        [Fact]
        public void B3_InternalNonPartialClass_GetsInternalWrapper()
        {
            var src = @"
using Prim.Core;
namespace Snippet
{
    internal class C
    {
        internal int F = 2;
        [Continuable]
        public int M() { int t = 0; for (int i = 0; i < 3; i++) t += F; return t; }
    }
    public static class Entry { public static int Go() => CContinuations.M_Continuable(new C()); }
}";
            var result = GeneratorHarness.AssertCompiles(src, "M");
            Assert.Contains("internal static partial class CContinuations", result.GeneratedText);
            Assert.Equal(6, Run<int>(src, "M"));
        }

        // ---- B4: continuable calls are rewritten by symbol, not by text ----

        [Fact]
        public void B4_OnlyCallsToTheContinuableOverloadAreRewritten()
        {
            var src = @"
using Prim.Core;
namespace Snippet
{
    public class Other
    {
        public int Calc(int x) => x + 1000;
    }

    public partial class C
    {
        public int Calc(string s) => s.Length;

        [Continuable]
        public int Calc(int n) { int t = 0; for (int i = 0; i < n; i++) t += i; return t; }

        [Continuable]
        public int M()
        {
            var other = new Other();
            string text = ""Calc(3)"";
            int a = Calc(""xyz"");
            int b = other.Calc(3);
            int c = Calc(4);
            int d = this.Calc(3);
            return a + b + c + d + text.Length;
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(); }
}";
            var result = GeneratorHarness.AssertCompiles(src, "Calc", "M");
            Assert.Contains("Calc(\"xyz\")", result.GeneratedText);
            Assert.Contains("other.Calc(3)", result.GeneratedText);
            Assert.Contains("\"Calc(3)\"", result.GeneratedText);
            Assert.Contains("Calc_Continuable(4)", result.GeneratedText);
            Assert.Contains("this.Calc_Continuable(3)", result.GeneratedText);
            // 3 + 1003 + 6 + 3 + 7
            Assert.Equal(1022, Run<int>(src, "Calc", "M"));
        }

        [Fact]
        public void B4_CallsInsideLambdasAreNotRewritten()
        {
            var src = @"
using System;
using Prim.Core;
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int Calc(int n) { int t = 0; for (int i = 0; i < n; i++) t += i; return t; }

        [Continuable]
        public int M()
        {
            Func<int, int> f = x => Calc(x);
            int Local(int y) => Calc(y);
            return f(3) + Local(4);
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(); }
}";
            var result = GeneratorHarness.AssertCompiles(src, "Calc", "M");
            Assert.Contains("x => Calc(x)", result.GeneratedText);
            Assert.Contains("int Local(int y) => Calc(y);", result.GeneratedText);
            Assert.Equal(9, Run<int>(src, "Calc", "M"));
        }

        // ---- B5: sibling using statements ----

        [Fact]
        public void B5_SiblingExpressionUsingsCompile()
        {
            var src = @"
using System;
using System.IO;
using Prim.Core;
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int M()
        {
            var a = new MemoryStream();
            var b = new MemoryStream();
            int t = 0;
            using (a) { for (int i = 0; i < 2; i++) t++; }
            using (b) { for (int i = 0; i < 3; i++) t++; }
            return t + (a.CanRead ? 100 : 0) + (b.CanRead ? 100 : 0);
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(); }
}";
            Assert.Equal(5, Run<int>(src, "M"));
        }

        // ---- B6: locals whose type cannot be hoisted ----

        [Fact]
        public void B6_AnonymousRefSpanPointerAndConstLocals()
        {
            var src = @"
using System;
using Prim.Core;
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public unsafe int M(int mode)
        {
            const int Two = 2;
            var anon = new { X = 3, Y = ""ab"" };
            var arr = new[] { 1, 2, 3 };
            ref int first = ref arr[0];
            first = 10;
            Span<int> span = stackalloc int[3];
            int total = 0;
            for (int i = 0; i < span.Length; i++) { span[i] = i; total += span[i]; }
            int value = 7;
            int* p = &value;
            switch (mode)
            {
                case Two:
                    total += *p;
                    break;
                default:
                    total -= 1;
                    break;
            }
            return total + anon.X + anon.Y.Length + arr[0];
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(2); }
}";
            // 3 + 7 + 3 + 2 + 10
            Assert.Equal(25, Run<int>(src, "M"));
        }

        // ---- Extras: tokens and attribute matching ----

        private static int TokenOf(string generated, string method)
        {
            var start = generated.IndexOf($" {method}_Continuable(", System.StringComparison.Ordinal);
            Assert.True(start >= 0);
            var match = new Regex(@"const int __methodToken = (-?\d+);").Match(generated, start);
            return int.Parse(match.Groups[1].Value);
        }

        [Fact]
        public void MethodToken_UsesResolvedTypesNotSyntax()
        {
            string Source(string intType, string listType) => $@"
using System;
using System.Collections.Generic;
using Prim.Core;
namespace Snippet
{{
    public partial class C
    {{
        [Continuable]
        public int M({intType} a, {listType} b, ref int c) {{ int t = a; for (int i = 0; i < b.Count; i++) t += b[i]; return t + c; }}
    }}
}}";
            var keyword = GeneratorHarness.AssertCompiles(Source("int", "List<int>"), "M").GeneratedText;
            var framework = GeneratorHarness.AssertCompiles(Source("Int32", "System.Collections.Generic.List<System.Int32>"), "M").GeneratedText;

            var expected = StableHash.GenerateMethodToken(
                "Snippet.C", "M", "int", "System.Collections.Generic.List<int>", "ref int");
            Assert.Equal(expected, TokenOf(keyword, "M"));
            Assert.Equal(expected, TokenOf(framework, "M"));
        }

        [Fact]
        public void MethodToken_NestedTypeIncludesContainingType()
        {
            var src = @"
using Prim.Core;
namespace Snippet
{
    public partial class Outer
    {
        public partial class Inner
        {
            [Continuable]
            public int M(string s) { int t = 0; for (int i = 0; i < s.Length; i++) t++; return t; }
        }
    }
}";
            var generated = GeneratorHarness.AssertCompiles(src, "M").GeneratedText;
            Assert.Equal(StableHash.GenerateMethodToken("Snippet.Outer.Inner", "M", "string"), TokenOf(generated, "M"));
        }

        [Fact]
        public void Attribute_IsMatchedBySymbol()
        {
            var src = @"
using Prim.Core;
using Cont = Prim.Core.ContinuableAttribute;
namespace Other
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class ContinuableAttribute : System.Attribute { }
}
namespace Snippet
{
    public partial class C
    {
        [Cont]
        public int ViaAlias() { int t = 0; for (int i = 0; i < 2; i++) t++; return t; }

        [Prim.Core.Continuable]
        public int Qualified() { int t = 0; for (int i = 0; i < 2; i++) t++; return t; }

        [Other.Continuable]
        public int Unrelated() { int t = 0; for (int i = 0; i < 2; i++) t++; return t; }
    }
}";
            var result = GeneratorHarness.AssertCompiles(src, "ViaAlias", "Qualified");
            Assert.DoesNotContain("Unrelated_Continuable", result.GeneratedText);
        }

        [Fact]
        public void PrimaryConstructorParameter_IsRejected()
        {
            var src = @"
using Prim.Core;
namespace Snippet
{
    public partial class C(int seed)
    {
        [Continuable]
        public int M() { int t = seed; for (int i = 0; i < 2; i++) t++; return t; }
    }
}";
            GeneratorHarness.AssertRejected(src, "PRIM004", "M");
        }

        [Fact]
        public void ConditionalDirectiveInBody_IsRejected()
        {
            var src = @"
using Prim.Core;
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int M()
        {
            int t = 0;
#if DEBUG
            t = 1;
#endif
            for (int i = 0; i < 2; i++) t++;
            return t;
        }
    }
}";
            GeneratorHarness.AssertRejected(src, "PRIM004", "M");
        }

        [Fact]
        public void RegionsAndPragmasInBody_AreDropped()
        {
            var src = @"
using Prim.Core;
namespace Snippet
{
    public partial class C
    {
        [Continuable]
        public int M()
        {
            #region setup
            int t = 0;
            #endregion
#pragma warning disable CS0168
            for (int i = 0; i < 2; i++) t++;
#pragma warning restore CS0168
            return t;
        }
    }
    public static class Entry { public static int Go() => new C().M_Continuable(); }
}";
            Assert.Equal(2, Run<int>(src, "M"));
        }
    }
}
