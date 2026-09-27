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
    /// Yield points inside user <c>try</c> blocks (whitepaper §4.2, §10.2).
    ///
    /// The fixtures are C# compiled with Roslyn at test time, so the rewriter sees the
    /// exception-handler layouts the C# compiler really emits: several catch clauses on one
    /// try, try/catch/finally as a try/catch nested in a try/finally with the same first
    /// instruction, filters for <c>when</c>, and a bare <c>catch</c> as <c>catch (object)</c>.
    ///
    /// Each fixture method is run three ways: the original compiled method (the oracle),
    /// the rewritten method without suspending, and the rewritten method suspended at every
    /// yield check it reaches, with the state serialized to JSON and back between runs.
    /// All three must agree. The suspending run is what checks that resume enters each try
    /// block through its nested dispatch with locals and the eval stack restored, that user
    /// catch clauses do not swallow <c>SuspendException</c>, and that finally blocks do not
    /// run during a suspension.
    /// </summary>
    public class TryBlockYieldTests : IDisposable
    {
        private const string FixtureSource = @"
using System;
using Prim.Core;

namespace TryFixtures
{
    [Continuable]
    public static class Methods
    {
        [Continuable]
        public static int LoopInTry(int n)
        {
            int sum = 0;
            try
            {
                for (int i = 1; i <= n; i++) sum += i;
            }
            catch (InvalidOperationException)
            {
                sum = -1;
            }
            return sum;
        }

        // Three nested try blocks; the innermost two share the loop, and each level also
        // has a loop of its own directly in its try block.
        [Continuable]
        public static int LoopInNestedTry(int n)
        {
            int total = 0;
            try
            {
                for (int a = 0; a < n; a++) total += 100;
                try
                {
                    try
                    {
                        for (int i = 1; i <= n; i++) total += i;
                    }
                    catch (ArgumentException)
                    {
                        total = -3;
                    }
                    catch (NotSupportedException)
                    {
                        total = -4;
                    }
                    for (int b = 0; b < n; b++) total += 10000;
                }
                catch (NotImplementedException)
                {
                    total = -2;
                }
            }
            catch (InvalidOperationException)
            {
                total = -1;
            }
            return total;
        }

        [Continuable]
        public static int CatchAllAroundLoop(int n, int throwAt)
        {
            int sum = 0;
            try
            {
                for (int i = 1; i <= n; i++)
                {
                    if (i == throwAt) throw new InvalidOperationException(""boom"");
                    sum += i;
                }
            }
            catch (Exception)
            {
                sum = -sum;
            }
            return sum;
        }

        [Continuable]
        public static int BareCatchAroundLoop(int n, int throwAt)
        {
            int sum = 0;
            try
            {
                for (int i = 1; i <= n; i++)
                {
                    if (i == throwAt) throw new InvalidOperationException(""boom"");
                    sum += i;
                }
            }
            catch
            {
                sum = -sum;
            }
            return sum;
        }

        [Continuable]
        public static int FilteredCatchAroundLoop(int n, int throwAt)
        {
            int sum = 0;
            try
            {
                for (int i = 1; i <= n; i++)
                {
                    if (i == throwAt) throw new InvalidOperationException(""boom"");
                    sum += i;
                }
            }
            catch (Exception e) when (e.Message.Length > 0)
            {
                sum = -sum;
            }
            return sum;
        }

        [Continuable]
        public static int LoopInTryFinally(int n)
        {
            int sum = 0;
            int finallyRuns = 0;
            try
            {
                for (int i = 1; i <= n; i++) sum += i;
            }
            finally
            {
                finallyRuns++;
            }
            return sum * 100 + finallyRuns;
        }

        [Continuable]
        public static int FinallyOnRealException(int n, int throwAt)
        {
            int sum = 0;
            int finallyRuns = 0;
            try
            {
                try
                {
                    for (int i = 1; i <= n; i++)
                    {
                        if (i == throwAt) throw new InvalidOperationException();
                        sum += i;
                    }
                }
                finally
                {
                    finallyRuns++;
                }
            }
            catch (InvalidOperationException)
            {
                sum = -sum;
            }
            return sum * 100 + finallyRuns;
        }

        // The try block is re-entered on every iteration of the outer loop, after a
        // resume into it on an earlier iteration.
        [Continuable]
        public static int TryCatchFinallyInLoop(int n)
        {
            int total = 0;
            int finallyRuns = 0;
            for (int k = 0; k < 3; k++)
            {
                try
                {
                    for (int i = 1; i <= n; i++) total += i;
                    if (k == 1) throw new InvalidOperationException();
                }
                catch (Exception)
                {
                    total += 1000;
                }
                finally
                {
                    finallyRuns++;
                }
            }
            return total * 100 + finallyRuns;
        }

        // Calls to another transformed method inside try/catch/finally; the first call's
        // result is live on the eval stack across the second.
        [Continuable]
        public static int CallInTryFinally(int n)
        {
            int result = 0;
            int finallyRuns = 0;
            try
            {
                result = Sum(n) + Sum(n + 1);
            }
            catch (Exception)
            {
                result = -1;
            }
            finally
            {
                finallyRuns++;
            }
            return result * 100 + finallyRuns;
        }

        // The try block is the method's first instruction, and returns from inside it.
        [Continuable]
        public static int TryAtMethodStart(int n)
        {
            try
            {
                int s = 0;
                for (int i = 1; i <= n; i++) s += i;
                return s;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }

        // The try block's first instruction is itself a resume point (a call to another
        // transformed method with no arguments to push first).
        [Continuable]
        public static int CallFirstInTry(int n)
        {
            int total = n;
            try
            {
                Ten();
                total += Ten();
            }
            finally
            {
                total += 1;
            }
            return total;
        }

        [Continuable]
        public static int Ten()
        {
            int s = 0;
            for (int i = 0; i < 10; i++) s += 1;
            return s;
        }

        [Continuable]
        public static int Sum(int n)
        {
            int s = 0;
            for (int i = 1; i <= n; i++) s += i;
            return s;
        }

        [Continuable]
        public static int LoopInCatch(int n)
        {
            int sum = 0;
            try
            {
                throw new InvalidOperationException();
            }
            catch (InvalidOperationException)
            {
                for (int i = 1; i <= n; i++) sum += i;
            }
            return sum;
        }

        [Continuable]
        public static int LoopInFinally(int n)
        {
            int sum = 0;
            try
            {
                sum = 1;
            }
            finally
            {
                for (int i = 1; i <= n; i++) sum += i;
            }
            return sum;
        }

        private static readonly object Gate = new object();

        [Continuable]
        public static int LoopInLock(int n)
        {
            int sum = 0;
            lock (Gate)
            {
                for (int i = 1; i <= n; i++) sum += i;
            }
            return sum;
        }
    }
}
";

        private static readonly Lazy<byte[]> OriginalFixture = new Lazy<byte[]>(CompileFixture);

        private readonly string _tempDir;

        public TryBlockYieldTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"PrimTryBlock_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch
            {
                // Ignore cleanup failures in tests
            }
        }

        [Fact]
        public void Transform_TryBlockFixtures_ProduceVerifiableIl()
        {
            var outputPath = WriteTransformedFixture("try_verify.dll", out var rewriter);

            Assert.Empty(rewriter.SkippedMethods);

            var result = IlVerificationTests.RunIlVerify(outputPath);

            if (!result.ToolAvailable)
            {
                Assert.Fail("ilverify tool unavailable: " + result.Diagnostics);
            }

            Assert.True(
                result.Errors.Count == 0,
                "Rewritten IL failed verification:\n" + string.Join("\n", result.Errors));
        }

        [Fact]
        public void Transform_YieldPointsInsideTryBlocks_AreNotSkipped()
        {
            WriteTransformedFixture("try_skips.dll", out var rewriter);

            // Only the three fixtures with a loop in a catch handler, a finally or a lock
            // body lose a yield point.
            var skippedMethods = rewriter.SkippedYieldPoints
                .Select(s => MethodName(s.Method))
                .Distinct()
                .OrderBy(m => m)
                .ToList();
            Assert.Equal(new[] { "LoopInCatch", "LoopInFinally", "LoopInLock" }, skippedMethods);
        }

        [Theory]
        [InlineData("LoopInTry", 10)]
        [InlineData("LoopInNestedTry", 6)]
        [InlineData("LoopInTryFinally", 10)]
        [InlineData("TryCatchFinallyInLoop", 4)]
        [InlineData("CallInTryFinally", 5)]
        [InlineData("TryAtMethodStart", 10)]
        [InlineData("CallFirstInTry", 3)]
        public void SuspendInsideTryBlock_ResumesToOriginalResult(string name, int n)
        {
            AssertSuspendsAndMatchesOriginal(name, n);
        }

        // Suspends at the first yield check, then resumes with no further yield request, so
        // after the resume execution leaves the try block and (in the loop fixtures)
        // enters it again without passing through a suspension. A resume that left the
        // dispatch state set would jump back to the old yield point on that re-entry.
        [Theory]
        [InlineData("LoopInTry", 10)]
        [InlineData("LoopInNestedTry", 6)]
        [InlineData("LoopInTryFinally", 10)]
        [InlineData("TryCatchFinallyInLoop", 4)]
        [InlineData("CallInTryFinally", 5)]
        [InlineData("TryAtMethodStart", 10)]
        [InlineData("CallFirstInTry", 3)]
        public void SuspendOnceInsideTryBlock_ThenResumeToCompletion_MatchesOriginal(string name, int n)
        {
            var args = new object[] { n };
            var expected = InvokeOriginal(name, args);
            var method = LoadTransformed("try_once_" + name + ".dll", name);
            var serializer = new JsonContinuationSerializer();

            var previous = ScriptContext.Current;
            try
            {
                var state = SuspendOnce(() => method.Invoke(null, args), out _);
                Assert.NotNull(state);

                var restored = serializer.Deserialize(serializer.Serialize(state!));
                ScriptContext.Current = new ScriptContext(restored, null);
                Assert.Equal(expected, method.Invoke(null, args));
            }
            finally
            {
                ScriptContext.Current = previous;
            }
        }

        [Theory]
        [InlineData("CatchAllAroundLoop")]
        [InlineData("BareCatchAroundLoop")]
        [InlineData("FilteredCatchAroundLoop")]
        public void UserCatchAroundYieldingLoop_DoesNotCatchSuspension(string name)
        {
            // No real exception: the user's catch must let every suspension through.
            // Were it to catch one, the method would return -sum instead of suspending.
            var result = AssertSuspendsAndMatchesOriginal(name, 10, 0);
            Assert.Equal(55, result);
        }

        [Theory]
        [InlineData("CatchAllAroundLoop")]
        [InlineData("BareCatchAroundLoop")]
        [InlineData("FilteredCatchAroundLoop")]
        public void UserCatchAroundYieldingLoop_StillCatchesRealException(string name)
        {
            // Suspends on the first iterations, then throws at i == 5 after a resume; the
            // user's catch handles that: -(1 + 2 + 3 + 4).
            var result = AssertSuspendsAndMatchesOriginal(name, 10, 5);
            Assert.Equal(-10, result);
        }

        [Fact]
        public void TryFinally_FinallyRunsOnceAcrossSuspensions()
        {
            // sum * 100 + finallyRuns: the finally does not run while the frame unwinds
            // to suspend, only when the method really leaves the try block.
            var result = AssertSuspendsAndMatchesOriginal("LoopInTryFinally", 10);
            Assert.Equal(55 * 100 + 1, result);
        }

        [Fact]
        public void TryFinally_FinallyStillRunsOnRealExceptionAfterResume()
        {
            var result = AssertSuspendsAndMatchesOriginal("FinallyOnRealException", 10, 5);
            Assert.Equal(-10 * 100 + 1, result);
        }

        [Fact]
        public void SuspendInsideTryBlock_CapturesLocalsAtTheYieldPoint()
        {
            var method = LoadTransformed("try_state.dll", "LoopInTry");

            var previous = ScriptContext.Current;
            try
            {
                var state = SuspendOnce(() => method.Invoke(null, new object[] { 10 }), out _);

                Assert.NotNull(state);
                var frame = state!.StackHead;
                Assert.Null(frame.Caller);
                // Roslyn puts the loop condition after the body, so the back-edge the
                // analysis finds is the body's fall-through into the condition, at the
                // store of i + 1. One iteration ran: sum == 1, i == 1, and the pending
                // i + 1 == 2 is live on the eval stack, spilled into the slot after them.
                Assert.Equal(new object[] { 1, 1, 2 }, frame.Slots);
            }
            finally
            {
                ScriptContext.Current = previous;
            }
        }

        [Theory]
        [InlineData("LoopInCatch")]
        [InlineData("LoopInFinally")]
        [InlineData("LoopInLock")]
        public void YieldPointInHandlerOrLock_IsSkippedAndMethodStillWorks(string name)
        {
            WriteTransformedFixture("try_skipped_" + name + ".dll", out var rewriter);

            var expectedReason = name switch
            {
                "LoopInCatch" => "catch handler",
                "LoopInFinally" => "finally block",
                _ => "lock statement",
            };
            Assert.Contains(rewriter.SkippedYieldPoints, s =>
                MethodName(s.Method) == name && s.Reason.Contains(expectedReason));

            // The method has no other yield point, so it runs to completion even with a
            // yield requested.
            var method = LoadTransformed("try_skipped_run_" + name + ".dll", name);
            var previous = ScriptContext.Current;
            try
            {
                var state = SuspendOnce(() => method.Invoke(null, new object[] { 10 }), out var result);
                Assert.Null(state);
                Assert.Equal(InvokeOriginal(name, 10), result);
            }
            finally
            {
                ScriptContext.Current = previous;
            }
        }

        /// <summary>
        /// Runs <paramref name="name"/> uninterrupted and then suspended at every yield
        /// check it reaches, and checks both against the original method. Returns the result.
        /// </summary>
        private object? AssertSuspendsAndMatchesOriginal(string name, params object[] args)
        {
            var expected = InvokeOriginal(name, args);
            var method = LoadTransformed("try_exec_" + name + ".dll", name);
            var serializer = new JsonContinuationSerializer();

            var previous = ScriptContext.Current;
            try
            {
                ScriptContext.Current = new ScriptContext();
                Assert.Equal(expected, method.Invoke(null, args));

                var state = SuspendOnce(() => method.Invoke(null, args), out var result);
                int suspensions = 0;
                while (state != null)
                {
                    suspensions++;
                    Assert.True(suspensions < 1000, "Resume loop did not terminate");

                    var restored = serializer.Deserialize(serializer.Serialize(state));
                    state = SuspendOnce(
                        () => method.Invoke(null, args), out result, new ScriptContext(restored, null));
                }

                Assert.True(suspensions > 1, $"Expected repeated suspensions, saw {suspensions}");
                Assert.Equal(expected, result);
                return result;
            }
            finally
            {
                ScriptContext.Current = previous;
            }
        }

        /// <summary>
        /// Runs <paramref name="invoke"/> with a yield requested. Returns the captured
        /// state if it suspended, or null (with <paramref name="result"/> set) if it ran
        /// to completion.
        /// </summary>
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

        private static string MethodName(string fullName)
        {
            // "System.Int32 TryFixtures.Methods::LoopInCatch(System.Int32)"
            var start = fullName.IndexOf("::", StringComparison.Ordinal) + 2;
            return fullName.Substring(start, fullName.IndexOf('(') - start);
        }

        private static object? InvokeOriginal(string name, params object[] args)
        {
            var type = Assembly.Load(OriginalFixture.Value).GetType("TryFixtures.Methods")!;
            return type.GetMethod(name)!.Invoke(null, args);
        }

        private MethodInfo LoadTransformed(string fileName, string name)
        {
            var path = WriteTransformedFixture(fileName, out _);
            var type = Assembly.Load(File.ReadAllBytes(path)).GetType("TryFixtures.Methods")!;
            return type.GetMethod(name)!;
        }

        private string WriteTransformedFixture(string fileName, out AssemblyRewriter rewriter)
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(AppContext.BaseDirectory);
            resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);

            using var input = new MemoryStream(OriginalFixture.Value);
            using var assembly = AssemblyDefinition.ReadAssembly(
                input, new ReaderParameters { AssemblyResolver = resolver });

            rewriter = new AssemblyRewriter();
            rewriter.Transform(assembly);

            var outputPath = Path.Combine(_tempDir, fileName);
            assembly.Write(outputPath);
            return outputPath;
        }

        private static byte[] CompileFixture()
        {
            // The trusted platform assemblies are the runtime plus this test's own
            // dependencies, Prim.Core among them.
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Where(p => p.Length > 0)
                .Append(typeof(ContinuableAttribute).Assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => MetadataReference.CreateFromFile(p))
                .ToList();

            var compilation = CSharpCompilation.Create(
                "TryFixtures",
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
