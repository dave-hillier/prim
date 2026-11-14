using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Prim.Core;
using Prim.Roslyn;
using Prim.Runtime;
using Prim.Serialization;
using Xunit;

namespace Prim.Tests.Roslyn
{
    /// <summary>
    /// Runs <see cref="ContinuationGenerator"/> over a source snippet, checks that the
    /// result compiles, and can load the compiled snippet to drive the generated
    /// methods through suspend/resume cycles.
    /// </summary>
    internal static class GeneratorHarness
    {
        private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
        {
            var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(p =>
                {
                    var name = Path.GetFileNameWithoutExtension(p);
                    return name.StartsWith("System", StringComparison.Ordinal) ||
                           name == "mscorlib" || name == "netstandard";
                });

            return tpa
                .Concat(new[]
                {
                    typeof(ContinuableAttribute).Assembly.Location,
                    typeof(ScriptContext).Assembly.Location,
                })
                .Distinct()
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                .ToImmutableArray();
        });

        internal sealed class Result
        {
            public Result(GeneratorDriverRunResult run, Compilation output)
            {
                Run = run;
                Output = output;
            }

            public GeneratorDriverRunResult Run { get; }
            public Compilation Output { get; }

            public IReadOnlyList<Diagnostic> GeneratorDiagnostics =>
                Run.Results.SelectMany(r => r.Diagnostics).ToList();

            public string GeneratedText =>
                string.Join("\n// ---- next file ----\n", Run.GeneratedTrees.Select(t => t.ToString()));

            public IReadOnlyList<Diagnostic> CompileErrors =>
                Output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

            /// <summary>Warnings reported inside generated files.</summary>
            public IReadOnlyList<Diagnostic> GeneratedWarnings =>
                Output.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Warning &&
                                d.Location.SourceTree != null &&
                                Run.GeneratedTrees.Contains(d.Location.SourceTree))
                    .ToList();
        }

        public static Result Run(params string[] sources)
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
            var trees = sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, parseOptions, path: $"Snippet{i}.cs"));
            var compilation = CSharpCompilation.Create(
                "Snippet_" + Guid.NewGuid().ToString("N"),
                trees,
                References.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { new ContinuationGenerator().AsSourceGenerator() },
                parseOptions: parseOptions);
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
            return new Result(driver.GetRunResult(), output);
        }

        /// <summary>
        /// Runs the generator and asserts: no generator diagnostics, the expected
        /// *_Continuable methods were generated, and the whole compilation (snippet plus
        /// generated code) has no errors and the generated files no warnings.
        /// </summary>
        public static Result AssertCompiles(string source, params string[] expectedGeneratedMethods)
        {
            return AssertCompiles(new[] { source }, expectedGeneratedMethods);
        }

        public static Result AssertCompiles(string[] sources, params string[] expectedGeneratedMethods)
        {
            var result = Run(sources);
            var context = "Generated code:\n" + result.GeneratedText;

            Assert.True(result.GeneratorDiagnostics.Count == 0,
                "Unexpected generator diagnostics: " +
                string.Join("; ", result.GeneratorDiagnostics.Select(d => d.ToString())) + "\n" + context);

            foreach (var name in expectedGeneratedMethods)
            {
                Assert.True(result.GeneratedText.Contains(name + "_Continuable("),
                    $"Expected {name}_Continuable to be generated.\n" + context);
            }

            Assert.True(result.CompileErrors.Count == 0,
                "Compilation errors: " + string.Join("\n", result.CompileErrors.Select(d => d.ToString())) + "\n" + context);
            Assert.True(result.GeneratedWarnings.Count == 0,
                "Warnings in generated code: " + string.Join("\n", result.GeneratedWarnings.Select(d => d.ToString())) + "\n" + context);
            return result;
        }

        /// <summary>
        /// Asserts the generator reported exactly one diagnostic with <paramref name="id"/>
        /// for <paramref name="methodName"/>, generated no code for it, and that the rest
        /// of the compilation still has no errors.
        /// </summary>
        public static Diagnostic AssertRejected(string source, string id, string methodName)
        {
            var result = Run(source);
            var context = "Generated code:\n" + result.GeneratedText;
            var diag = Assert.Single(result.GeneratorDiagnostics);
            Assert.Equal(id, diag.Id);
            Assert.Contains($"'{methodName}'", diag.GetMessage());
            Assert.DoesNotContain(methodName + "_Continuable(", result.GeneratedText);
            Assert.True(result.CompileErrors.Count == 0,
                "Compilation errors: " + string.Join("\n", result.CompileErrors.Select(d => d.ToString())) + "\n" + context);
            return diag;
        }

        /// <summary>Emits and loads the compiled snippet.</summary>
        public static Assembly Load(Result result)
        {
            using var stream = new MemoryStream();
            var emit = result.Output.Emit(stream);
            Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            return Assembly.Load(stream.ToArray());
        }

        /// <summary>
        /// Compiles the snippet (asserting it compiles cleanly) and returns the static
        /// parameterless method <c>Entry.Go</c> as a delegate. Snippets use it to call
        /// the generated method under test. A delegate (not MethodInfo.Invoke) is used so
        /// SuspendException is not wrapped in a TargetInvocationException.
        /// </summary>
        public static Func<T> CompileEntry<T>(string source, params string[] expectedGeneratedMethods)
        {
            var result = AssertCompiles(source, expectedGeneratedMethods);
            var assembly = Load(result);
            var entry = assembly.GetTypes().Single(t => t.Name == "Entry");
            var go = entry.GetMethod("Go", BindingFlags.Public | BindingFlags.Static)!;
            return (Func<T>)Delegate.CreateDelegate(typeof(Func<T>), go);
        }

        internal sealed class Outcome<T>
        {
            public T Value { get; set; } = default!;
            public int Suspensions { get; set; }

            /// <summary>Yield point id of the innermost frame at each suspension.</summary>
            public List<int> InnermostYieldPoints { get; } = new();

            /// <summary>Frame depth at each suspension.</summary>
            public List<int> Depths { get; } = new();
        }

        /// <summary>
        /// Runs <paramref name="call"/> with the given instruction budget per slice and
        /// resumes it (optionally through JSON) until it completes. Fails if it does not
        /// complete within <paramref name="maxSuspensions"/> suspensions (no progress).
        /// </summary>
        public static Outcome<T> RunToCompletion<T>(
            Func<T> call, int budget, bool serialize = false, int maxSuspensions = 5000, Action? prepareThread = null)
        {
            return OnFreshThread(() =>
            {
                prepareThread?.Invoke();
                return RunToCompletionCore(call, budget, serialize, maxSuspensions);
            });
        }

        /// <summary>
        /// Runs <paramref name="work"/> on a new thread. ContinuationRunner.Run reuses a
        /// thread's existing ScriptContext (left behind by ScriptContext.EnsureCurrent in
        /// other tests), and a reused context makes the first resume replay less
        /// precisely, so suspension counts would depend on which thread a test lands on.
        /// </summary>
        private static TResult OnFreshThread<TResult>(Func<TResult> work)
        {
            TResult result = default!;
            Exception? error = null;
            var thread = new System.Threading.Thread(() =>
            {
                try { result = work(); }
                catch (Exception ex) { error = ex; }
            }, 16 * 1024 * 1024);
            thread.Start();
            thread.Join();
            if (error != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }

        private static Outcome<T> RunToCompletionCore<T>(Func<T> call, int budget, bool serialize, int maxSuspensions)
        {
            var runner = new ContinuationRunner();
            var serializer = new JsonContinuationSerializer();
            var outcome = new Outcome<T>();

            T Entry()
            {
                ScriptContext.Current!.ResetBudget(budget);
                return call();
            }

            var result = runner.Run(Entry);
            while (result is ContinuationResult<T>.Suspended suspended)
            {
                outcome.Suspensions++;
                Assert.True(outcome.Suspensions <= maxSuspensions,
                    $"No completion after {maxSuspensions} suspensions: the resumed method is not making progress.");

                var state = suspended.State;
                var frame = state.StackHead;
                var depth = 1;
                while (frame.Caller != null)
                {
                    frame = frame.Caller;
                    depth++;
                }
                outcome.InnermostYieldPoints.Add(frame.YieldPointId);
                outcome.Depths.Add(depth);

                if (serialize)
                {
                    state = serializer.DeserializeFromString(serializer.SerializeToString(state));
                }

                result = runner.Resume(state, null, Entry);
            }

            outcome.Value = ((ContinuationResult<T>.Completed)result).Value;
            return outcome;
        }

        /// <summary>
        /// Requests a yield before calling, so the method suspends at its first yield
        /// site, then resumes once. Returns the resumed result (or the result of the
        /// first run if no yield site was reached).
        /// </summary>
        public static T SuspendAtFirstYieldThenResume<T>(Func<T> call)
        {
            return OnFreshThread(() => SuspendAtFirstYieldThenResumeCore(call));
        }

        private static T SuspendAtFirstYieldThenResumeCore<T>(Func<T> call)
        {
            var runner = new ContinuationRunner();
            var first = runner.Run(() =>
            {
                ScriptContext.Current!.RequestYield();
                return call();
            });
            if (first is ContinuationResult<T>.Completed done)
            {
                ScriptContext.Current?.ClearYieldRequest();
                return done.Value;
            }
            var suspended = (ContinuationResult<T>.Suspended)first;
            var resumed = runner.Resume(suspended.State, null, call);
            Assert.True(resumed.IsCompleted, "Expected the resumed method to complete.");
            return ((ContinuationResult<T>.Completed)resumed).Value;
        }
    }
}
