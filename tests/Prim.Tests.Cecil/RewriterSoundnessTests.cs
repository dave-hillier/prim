using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Prim.Cecil;
using Prim.Core;
using Prim.Runtime;
using Prim.Serialization;
using Xunit;

namespace Prim.Tests.Cecil
{
    /// <summary>
    /// Regression tests for rewriter bugs that produced silently wrong results or invalid
    /// IL: mutated arguments, generic-typed state, spill-temp typing, prefixed
    /// instructions, how <c>[Continuable]</c> selects methods, and suspensions that arrive
    /// through a call that is not a resume point.
    ///
    /// The fixtures are C# compiled with Roslyn at test time. Each method is run three
    /// ways: the original method (the oracle), the rewritten method without suspending,
    /// and the rewritten method suspended at every yield check it reaches and resumed
    /// until it completes. All three must agree.
    ///
    /// None of the fixture types carries a type-level <c>[Continuable]</c>: the
    /// method-level attribute alone selects a method.
    /// </summary>
    public class RewriterSoundnessTests
    {
        private const string FixtureSource = @"
using System;
using System.Collections.Generic;
using Prim.Core;

namespace SoundFixtures
{
    public struct Pair
    {
        public int A;
        public int B;
    }

    public static class Methods
    {
        // -- 1. mutated arguments ------------------------------------------------------

        [Continuable]
        public static int MutatedArg(int n)
        {
            int s = 0;
            while (n > 0) { n--; s += n; }
            return s;
        }

        // The caller re-pushes its own (unmodified) argument on resume; the callee must
        // restore its mutated copy.
        [Continuable]
        public static int CallsMutatedArg(int n)
        {
            int t = 0;
            for (int k = 1; k <= 2; k++) t += MutatedArg(n + k);
            return t;
        }

        // A struct argument mutated through its address (ldarga).
        [Continuable]
        public static int MutatedStructArg(Pair p, int n)
        {
            for (int i = 0; i < n; i++) { p.A += i; p.B++; }
            return p.A * 100 + p.B;
        }

        // -- 2. generic-typed locals and spill temps ------------------------------------

        [Continuable]
        public static T Gen<T>(T seed, int n)
        {
            T r = default;
            for (int i = 0; i < n; i++) r = seed;
            return r;
        }

        // A T value is live on the evaluation stack at the back-edge (stloc r).
        [Continuable]
        public static T GenSpill<T>(T seed, int n)
        {
            T r = default;
            int i = 0;
            while (i < n) { i++; r = seed; }
            return r;
        }

        [Continuable]
        public static int CallsGen(int n)
        {
            int t = 0;
            for (int k = 1; k <= 2; k++) t += Gen<int>(k, n) + GenSpill<int>(k, n);
            return t;
        }

        // -- 3a. opcodes whose result type the stack simulator must model --------------

        [Continuable]
        public static int DoWhileLength(int[] a)
        {
            int i = 0;
            do { i++; } while (i < a.Length);
            return i;
        }

        [Continuable]
        public static int WhileElem(int[] a)
        {
            int x = 0;
            int sum = 0;
            int i = 0;
            while (i < a.Length) { sum += x; i++; x = a[i - 1]; }
            return sum + x;
        }

        [Continuable]
        public static int WhileCompare(int n)
        {
            bool b = false;
            int c = 0;
            int i = 0;
            while (i < n) { i++; if (b) c++; b = i > 1; }
            return c;
        }

        [Continuable]
        public static int WhileConv(int n)
        {
            long acc = 0;
            long sum = 0;
            double d = 0;
            int i = 0;
            while (i < n) { i++; sum += acc + (long)d; d = i; acc = i; }
            return (int)sum;
        }

        // int32 merged with char at the join before 'stloc x'.
        [Continuable]
        public static int WhileMerge(int n)
        {
            char c = 'A';
            int x = 0;
            int sum = 0;
            int i = 0;
            while (i < n) { i++; sum += x; x = i > 2 ? c : 7; }
            return sum;
        }

        [Continuable]
        public static int WhileIsinst(object[] items)
        {
            int count = 0;
            string s = null;
            int i = 0;
            while (i < items.Length) { if (s != null) count += s.Length; i++; s = items[i - 1] as string; }
            return count;
        }

        // A null literal is live on the stack at the back-edge.
        [Continuable]
        public static int WhileNull(int n)
        {
            string s = ""x"";
            int count = 0;
            int i = 0;
            while (i < n) { if (s != null) count++; i++; s = null; }
            return count;
        }

        // ArgumentException and InvalidOperationException meet at the join before
        // 'stloc e'; their common base class (SystemException) is defined outside this
        // module, so the slot is not typed and that yield point is skipped. The first
        // loop's yield point is still placed.
        [Continuable]
        public static int UntypedMerge(int n)
        {
            int s = 0;
            for (int k = 0; k < n; k++) s += k;
            Exception e = null;
            int i = 0;
            while (i < n) { i++; e = i > 2 ? (Exception)new ArgumentException() : new InvalidOperationException(); }
            return s * 10 + (e is ArgumentException ? 1 : 0);
        }

        // -- 3b. generic return and field types -----------------------------------------

        [Continuable]
        public static int DictLoop(int k)
        {
            var d = new Dictionary<string, int> { [""k""] = k };
            int s = 0;
            while (d[""k""] > 0) { s++; d[""k""] = d[""k""] - 1; }
            return s;
        }

        [Continuable]
        public static int ListLoop(int n)
        {
            var list = new List<int>();
            for (int i = 0; i < n; i++) list.Add(i * 2);
            int s = 0;
            int j = 0;
            while (j < list.Count) { s += j; j++; s = s + list[j - 1]; }
            return s;
        }

        // -- 4. prefixed instructions ---------------------------------------------------

        public static volatile int V;

        [Continuable]
        public static int VolatileStore(int n)
        {
            int i = 0;
            while (i < n) { i++; V = i; }
            return V;
        }

        // -- 6. calls that are (or are not) resume points --------------------------------

        [Continuable]
        public static int ViaInterface(IStep s, int n)
        {
            int t = 0;
            int calls = 0;
            for (int i = 0; i < n; i++) { calls++; t += s.Step(i); }
            return t * 1000 + calls;
        }

        [Continuable]
        public static int ViaAbstract(StepBase s, int n)
        {
            int t = 0;
            int calls = 0;
            for (int i = 0; i < n; i++) { calls++; t += s.Step(i); }
            return t * 1000 + calls;
        }

        [Continuable]
        public static int ViaDelegate(int n)
        {
            Func<int, int> f = Sum;
            int t = 0;
            int calls = 0;
            for (int i = 0; i < n; i++) { calls++; t += f(i); }
            return t * 1000 + calls;
        }

        // Skipped (byref local), so not a resume point for its callers.
        [Continuable]
        public static int WithRefLocal(int[] a, int n)
        {
            ref int r = ref a[0];
            for (int i = 1; i <= n; i++) r += Sum(i);
            return r;
        }

        [Continuable]
        public static int CallsSkipped(int n)
        {
            int[] a = new int[1];
            int t = 0;
            for (int k = 0; k < 2; k++) t += WithRefLocal(a, n);
            return t;
        }

        [Continuable]
        public static int CallInCatch(int n)
        {
            int t = 0;
            int calls = 0;
            for (int i = 0; i < n; i++) t += i;
            try
            {
                throw new InvalidOperationException();
            }
            catch (InvalidOperationException)
            {
                calls++;
                t += Sum(n);
            }
            return t * 1000 + calls;
        }

        [Continuable]
        public static int Sum(int n)
        {
            int s = 0;
            for (int i = 1; i <= n; i++) s += i;
            return s;
        }
    }

    // -- 3c. generic declaring type --------------------------------------------------------

    public class G<T>
    {
        public T Tag;

        [Continuable]
        public int Run(int n)
        {
            int s = 0;
            for (int i = 0; i < n; i++) s += Step(i);
            return s;
        }

        [Continuable]
        public int Step(int i)
        {
            int r = 0;
            for (int j = 0; j <= i; j++) r += j;
            return r;
        }

        // A field of type T loaded through the self-instantiated type.
        [Continuable]
        public T LastTag(T value, int n)
        {
            int i = 0;
            while (i < n) { i++; Tag = value; }
            return Tag;
        }
    }

    // -- 5. attribute placement --------------------------------------------------------------

    public static class Outer
    {
        public static class Inner
        {
            [Continuable]
            public static int Nested(int n)
            {
                int s = 0;
                for (int i = 1; i <= n; i++) s += i;
                return s;
            }
        }
    }

    public struct Counter
    {
        public int Total;

        // 'this' is a managed pointer into the caller's storage: skipped.
        [Continuable]
        public int Add(int n)
        {
            for (int i = 1; i <= n; i++) Total += i;
            return Total;
        }

        [Continuable]
        public static int StaticSum(int n)
        {
            int s = 0;
            for (int i = 1; i <= n; i++) s += i;
            return s;
        }
    }

    public abstract class StepBase
    {
        [Continuable]
        public abstract int Step(int i);
    }

    public sealed class StepDerived : StepBase
    {
        [Continuable]
        public override int Step(int i)
        {
            int r = 0;
            for (int j = 0; j <= i; j++) r += j;
            return r;
        }
    }

    public interface IStep
    {
        int Step(int i);
    }

    public sealed class StepImpl : IStep
    {
        [Continuable]
        public int Step(int i)
        {
            int r = 0;
            for (int j = 0; j <= i; j++) r += j;
            return r;
        }
    }
}
";

        private static readonly Lazy<byte[]> OriginalBytes = new Lazy<byte[]>(CompileFixture);
        private static readonly Lazy<Assembly> Original = new Lazy<Assembly>(() => Assembly.Load(OriginalBytes.Value));
        private static readonly Lazy<Transformed> Rewritten = new Lazy<Transformed>(Transform);

        private sealed class Transformed
        {
            public string Path { get; init; } = "";
            public Assembly Assembly { get; init; } = null!;
            public AssemblyRewriter Rewriter { get; init; } = null!;
        }

        // -- 1 ---------------------------------------------------------------------------

        [Theory]
        [InlineData("MutatedArg", 6)]
        [InlineData("CallsMutatedArg", 4)]
        public void MutatedArgument_IsCapturedAndRestored(string name, int n)
        {
            AssertSuspendsAndMatches("Methods", name, new object[] { n });
        }

        [Fact]
        public void StructArgumentMutatedThroughItsAddress_IsCapturedAndRestored()
        {
            var pair = Activator.CreateInstance(Original.Value.GetType("SoundFixtures.Pair")!)!;
            var pairT = Activator.CreateInstance(Rewritten.Value.Assembly.GetType("SoundFixtures.Pair")!)!;
            AssertSuspendsAndMatches("Methods", "MutatedStructArg", new[] { pair, 5 },
                transformedArgs: new[] { pairT, 5 }, serialize: false);
        }

        // -- 2 ---------------------------------------------------------------------------

        [Theory]
        [InlineData("Gen")]
        [InlineData("GenSpill")]
        public void GenericLocalAndSpill_OfValueTypeInstantiation_AreBoxed(string name)
        {
            AssertSuspendsAndMatches("Methods", name, new object[] { 7, 5 }, typeArgs: new[] { typeof(int) });
        }

        [Theory]
        [InlineData("Gen")]
        [InlineData("GenSpill")]
        public void GenericLocalAndSpill_OfReferenceTypeInstantiation_Work(string name)
        {
            AssertSuspendsAndMatches("Methods", name, new object[] { "x", 5 }, typeArgs: new[] { typeof(string) });
        }

        [Fact]
        public void CallsToGenericMethods_ResumeCorrectly()
        {
            AssertSuspendsAndMatches("Methods", "CallsGen", new object[] { 4 });
        }

        // -- 3a --------------------------------------------------------------------------

        [Fact]
        public void DoWhileOverArrayLength_SpillsTypedTemps()
        {
            AssertSuspendsAndMatches("Methods", "DoWhileLength", new object[] { new int[6] });
        }

        [Fact]
        public void WhileBodyEndingInArrayElementLoad_SpillsTypedTemps()
        {
            AssertSuspendsAndMatches("Methods", "WhileElem", new object[] { new[] { 3, 1, 4, 1, 5 } });
        }

        [Theory]
        [InlineData("WhileCompare")]
        [InlineData("WhileConv")]
        [InlineData("WhileMerge")]
        [InlineData("WhileNull")]
        public void WhileBodyEndingInModelledOpcode_SpillsTypedTemps(string name)
        {
            AssertSuspendsAndMatches("Methods", name, new object[] { 6 });
        }

        [Fact]
        public void WhileBodyEndingInIsinst_SpillsTypedTemps()
        {
            AssertSuspendsAndMatches(
                "Methods", "WhileIsinst", new object[] { new object[] { "ab", 1, "cde", null!, "f" } }, serialize: false);
        }

        [Fact]
        public void UntypableStackSlot_SkipsThatYieldPointAndReportsIt()
        {
            var rewriter = Rewritten.Value.Rewriter;
            Assert.Single(rewriter.SkippedYieldPoints, s =>
                s.Method.Contains("::UntypedMerge(") && s.Reason.Contains("could not be typed"));
            Assert.DoesNotContain(rewriter.SkippedMethods, s => s.Method.Contains("::UntypedMerge("));

            AssertSuspendsAndMatches("Methods", "UntypedMerge", new object[] { 5 });
        }

        // -- 3b --------------------------------------------------------------------------

        [Fact]
        public void DictionaryIndexer_ResultTypeIsInstantiated()
        {
            AssertSuspendsAndMatches("Methods", "DictLoop", new object[] { 5 }, serialize: false);
        }

        [Fact]
        public void ListIndexer_ResultTypeIsInstantiated()
        {
            AssertSuspendsAndMatches("Methods", "ListLoop", new object[] { 5 }, serialize: false);
        }

        // -- 3c --------------------------------------------------------------------------

        [Fact]
        public void GenericDeclaringType_ThisIsSelfInstantiated()
        {
            AssertSuspendsAndMatches("G`1", "Run", new object[] { 5 }, typeArgs: new[] { typeof(int) }, serialize: false);
        }

        [Theory]
        [InlineData(typeof(int), 42)]
        [InlineData(typeof(string), "tag")]
        public void GenericDeclaringType_FieldOfTypeParameter(Type typeArg, object value)
        {
            AssertSuspendsAndMatches("G`1", "LastTag", new[] { value, 4 }, typeArgs: new[] { typeArg }, serialize: false);
        }

        // -- 4 ---------------------------------------------------------------------------

        [Fact]
        public void VolatileStore_YieldCheckGoesBeforeThePrefix()
        {
            AssertSuspendsAndMatches("Methods", "VolatileStore", new object[] { 5 });
        }

        // -- 5 ---------------------------------------------------------------------------

        [Fact]
        public void MethodLevelAttribute_IsEnoughWithoutTypeLevelAttribute()
        {
            var rewriter = Rewritten.Value.Rewriter;
            Assert.DoesNotContain(rewriter.SkippedMethods, s => s.Method.Contains("::MutatedArg("));
            AssertSuspendsAndMatches("Methods", "MutatedArg", new object[] { 3 });
        }

        [Fact]
        public void MethodInTypeNestedInUnattributedType_IsTransformed()
        {
            AssertSuspendsAndMatches("Outer+Inner", "Nested", new object[] { 6 });
        }

        [Fact]
        public void StaticMethodOnStruct_IsTransformed()
        {
            AssertSuspendsAndMatches("Counter", "StaticSum", new object[] { 6 });
        }

        [Fact]
        public void InstanceMethodOnStruct_IsSkippedWithReason()
        {
            var rewriter = Rewritten.Value.Rewriter;
            Assert.Contains(rewriter.SkippedMethods, s =>
                s.Method.Contains("SoundFixtures.Counter::Add(") && s.Reason.Contains("value type"));

            // Left untransformed, it runs to completion even with a yield requested.
            var counter = Activator.CreateInstance(Rewritten.Value.Assembly.GetType("SoundFixtures.Counter")!)!;
            var method = counter.GetType().GetMethod("Add")!;
            var previous = ScriptContext.Current;
            try
            {
                var state = SuspendOnce(() => method.Invoke(counter, new object[] { 4 }), out var result);
                Assert.Null(state);
                Assert.Equal(10, result);
            }
            finally
            {
                ScriptContext.Current = previous;
            }
        }

        [Fact]
        public void AbstractMethodWithAttribute_IsReportedAsSkipped()
        {
            Assert.Contains(Rewritten.Value.Rewriter.SkippedMethods, s =>
                s.Method.Contains("SoundFixtures.StepBase::Step(") && s.Reason.Contains("no body"));
        }

        // -- 6 ---------------------------------------------------------------------------

        [Fact]
        public void InterfaceCall_ToTransformedImplementation_IsAResumePoint()
        {
            var impl = Activator.CreateInstance(Original.Value.GetType("SoundFixtures.StepImpl")!)!;
            var implT = Activator.CreateInstance(Rewritten.Value.Assembly.GetType("SoundFixtures.StepImpl")!)!;
            AssertSuspendsAndMatches("Methods", "ViaInterface", new object[] { impl, 5 },
                transformedArgs: new object[] { implT, 5 }, serialize: false);
        }

        [Fact]
        public void AbstractCall_ToTransformedOverride_IsAResumePoint()
        {
            var impl = Activator.CreateInstance(Original.Value.GetType("SoundFixtures.StepDerived")!)!;
            var implT = Activator.CreateInstance(Rewritten.Value.Assembly.GetType("SoundFixtures.StepDerived")!)!;
            AssertSuspendsAndMatches("Methods", "ViaAbstract", new object[] { impl, 5 },
                transformedArgs: new object[] { implT, 5 }, serialize: false);
        }

        [Fact]
        public void SkippedContinuableCallee_IsNotAResumePoint()
        {
            var rewriter = Rewritten.Value.Rewriter;
            Assert.Contains(rewriter.SkippedMethods, s => s.Method.Contains("::WithRefLocal("));
        }

        // A suspension that reaches a transformed frame through a call that is not a resume
        // point (a delegate, an untransformed [Continuable] method, a call in a catch
        // handler) cannot be resumed correctly. It must fail when it suspends rather than
        // record a stale yield point and resume in the wrong place.
        [Theory]
        [InlineData("ViaDelegate")]
        [InlineData("CallsSkipped")]
        [InlineData("CallInCatch")]
        public void SuspensionThroughNonResumePoint_FailsLoudly(string name)
        {
            var method = FindMethod(Rewritten.Value.Assembly, "Methods", name, null);
            var args = new object[] { 4 };

            // Uninterrupted, the method still works.
            var previous = ScriptContext.Current;
            try
            {
                ScriptContext.Current = new ScriptContext();
                Assert.Equal(InvokeOriginal("Methods", name, args, null), method.Invoke(null, args));

                ContinuationState? state;
                object? result;
                int suspensions = 0;
                ScriptContext? context = null;
                try
                {
                    while ((state = SuspendOnce(() => method.Invoke(null, args), out result, context)) != null)
                    {
                        Assert.True(++suspensions < 1000, "Resume loop did not terminate");
                        context = new ScriptContext(state, null);
                    }
                }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException ioe)
                {
                    Assert.Contains("not a resume point", ioe.Message);
                    return;
                }

                Assert.Fail($"Expected the suspension to fail loudly; it completed with {result} " +
                            $"(the original returns {InvokeOriginal("Methods", name, args, null)})");
            }
            finally
            {
                ScriptContext.Current = previous;
            }
        }

        // -- verification ----------------------------------------------------------------

        [Fact]
        public void Transform_SoundnessFixtures_ProduceVerifiableIl()
        {
            var result = IlVerificationTests.RunIlVerify(Rewritten.Value.Path);
            if (!result.ToolAvailable)
            {
                Assert.Fail("ilverify tool unavailable: " + result.Diagnostics);
            }
            Assert.True(
                result.Errors.Count == 0,
                "Rewritten IL failed verification:\n" + string.Join("\n", result.Errors));
        }

        [Fact]
        public void Transform_OnlyExpectedMethodsAreSkipped()
        {
            var skipped = Rewritten.Value.Rewriter.SkippedMethods
                .Select(s => s.Method.Substring(s.Method.IndexOf(' ') + 1))
                .Select(m => m.Substring(0, m.IndexOf('(')))
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(new[]
            {
                "SoundFixtures.Counter::Add",
                "SoundFixtures.Methods::WithRefLocal",
                "SoundFixtures.StepBase::Step",
            }, skipped);
        }

        // -- harness ---------------------------------------------------------------------

        /// <summary>
        /// Runs a fixture method uninterrupted and then suspended at every yield check it
        /// reaches (resuming each time, optionally through JSON), and checks both against
        /// the original method.
        /// </summary>
        private static void AssertSuspendsAndMatches(
            string typeName,
            string name,
            object[] args,
            Type[]? typeArgs = null,
            object[]? transformedArgs = null,
            bool serialize = true)
        {
            var expected = InvokeOriginal(typeName, name, args, typeArgs);
            var targs = transformedArgs ?? args;
            var method = FindMethod(Rewritten.Value.Assembly, typeName, name, typeArgs);
            var instance = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!);
            var serializer = new JsonContinuationSerializer();

            var previous = ScriptContext.Current;
            try
            {
                ScriptContext.Current = new ScriptContext();
                Assert.Equal(expected, method.Invoke(instance, targs));

                var state = SuspendOnce(() => method.Invoke(instance, targs), out var result);
                int suspensions = 0;
                while (state != null)
                {
                    suspensions++;
                    Assert.True(suspensions < 1000, "Resume loop did not terminate");
                    var restored = serialize ? serializer.Deserialize(serializer.Serialize(state)) : state;
                    state = SuspendOnce(
                        () => method.Invoke(instance, targs), out result, new ScriptContext(restored, null));
                }

                Assert.True(suspensions > 1, $"Expected repeated suspensions, saw {suspensions}");
                Assert.Equal(expected, result);
            }
            finally
            {
                ScriptContext.Current = previous;
            }
        }

        private static ContinuationState? SuspendOnce(
            Func<object?> invoke, out object? result, ScriptContext? context = null)
        {
            var ctx = context ?? new ScriptContext();
            ctx.RequestYield();
            ScriptContext.Current = ctx;

            try
            {
                result = invoke();
                return null;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is SuspendException suspend)
            {
                result = null;
                return suspend.BuildContinuationState();
            }
        }

        private static object? InvokeOriginal(string typeName, string name, object[] args, Type[]? typeArgs)
        {
            var method = FindMethod(Original.Value, typeName, name, typeArgs);
            var instance = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!);
            return method.Invoke(instance, args);
        }

        private static MethodInfo FindMethod(Assembly assembly, string typeName, string name, Type[]? typeArgs)
        {
            var type = assembly.GetType("SoundFixtures." + typeName)!;
            if (type.IsGenericTypeDefinition)
            {
                type = type.MakeGenericType(typeArgs!);
                return type.GetMethod(name)!;
            }
            var method = type.GetMethod(name)!;
            return method.IsGenericMethodDefinition ? method.MakeGenericMethod(typeArgs!) : method;
        }

        private static Transformed Transform()
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(AppContext.BaseDirectory);
            resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);

            using var input = new MemoryStream(OriginalBytes.Value);
            using var assembly = AssemblyDefinition.ReadAssembly(
                input, new ReaderParameters { AssemblyResolver = resolver });

            var rewriter = new AssemblyRewriter();
            rewriter.Transform(assembly);

            var dir = Path.Combine(Path.GetTempPath(), $"PrimSoundness_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "SoundFixtures.dll");
            assembly.Write(path);

            return new Transformed
            {
                Path = path,
                Assembly = Assembly.Load(File.ReadAllBytes(path)),
                Rewriter = rewriter,
            };
        }

        private static byte[] CompileFixture()
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Where(p => p.Length > 0)
                .Append(typeof(ContinuableAttribute).Assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => MetadataReference.CreateFromFile(p))
                .ToList();

            var compilation = CSharpCompilation.Create(
                "SoundFixtures",
                new[] { CSharpSyntaxTree.ParseText(FixtureSource) },
                references,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release));

            using var stream = new MemoryStream();
            var emit = compilation.Emit(stream);
            Assert.True(emit.Success, "Fixture failed to compile:\n" +
                string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            return stream.ToArray();
        }
    }
}
