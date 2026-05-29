using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Prim.Core;
using Prim.Roslyn;
using Xunit;

namespace Prim.Tests.Roslyn
{
    /// <summary>
    /// Drives the source generator directly via a CSharpGeneratorDriver to assert that
    /// unsupported [Continuable] members are DIAGNOSED and SKIPPED rather than producing
    /// non-compiling output (#26). Also asserts supported members produce no diagnostics.
    /// </summary>
    public class GeneratorDiagnosticTests
    {
        private static (GeneratorDriverRunResult RunResult, Compilation OutputCompilation) Run(string source)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source);

            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.IEnumerable<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ContinuableAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Prim.Runtime.ScriptContext).Assembly.Location),
                MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location),
                MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("netstandard").Location),
            };

            var compilation = CSharpCompilation.Create(
                "GeneratorDiagnosticTestAssembly",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var generator = new ContinuationGenerator();
            GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

            return (driver.GetRunResult(), outputCompilation);
        }

        private const string Prologue = @"
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Prim.Core;
namespace TestNs
{
    public partial class Sample
    {
";
        private const string Epilogue = @"
    }
}";

        [Fact]
        public void AsyncMethod_IsDiagnosedAndSkipped()
        {
            var src = Prologue + @"
        [Continuable]
        public async Task<int> Doit()
        {
            await Task.Yield();
            return 1;
        }
" + Epilogue;

            var (result, _) = Run(src);

            var diag = Assert.Single(result.Results.SelectMany(r => r.Diagnostics));
            Assert.Equal("PRIM002", diag.Id);
            Assert.Contains("async", diag.GetMessage());

            // No source was generated for the skipped method.
            Assert.DoesNotContain(
                result.GeneratedTrees,
                t => t.ToString().Contains("Doit_Continuable"));
        }

        [Fact]
        public void IteratorMethod_IsDiagnosedAndSkipped()
        {
            var src = Prologue + @"
        [Continuable]
        public IEnumerable<int> Doit()
        {
            yield return 1;
        }
" + Epilogue;

            var (result, _) = Run(src);

            var diag = Assert.Single(result.Results.SelectMany(r => r.Diagnostics));
            Assert.Equal("PRIM002", diag.Id);
            Assert.Contains("iterator", diag.GetMessage());
            Assert.DoesNotContain(result.GeneratedTrees, t => t.ToString().Contains("Doit_Continuable"));
        }

        [Fact]
        public void GenericMethod_IsDiagnosedAndSkipped()
        {
            var src = Prologue + @"
        [Continuable]
        public T Doit<T>(T value)
        {
            return value;
        }
" + Epilogue;

            var (result, _) = Run(src);

            var diag = Assert.Single(result.Results.SelectMany(r => r.Diagnostics));
            Assert.Equal("PRIM002", diag.Id);
            Assert.Contains("generic", diag.GetMessage());
        }

        [Fact]
        public void ExpressionBodiedMethod_IsDiagnosedAndSkipped()
        {
            var src = Prologue + @"
        [Continuable]
        public int Doit() => 7;
" + Epilogue;

            var (result, _) = Run(src);

            var diag = Assert.Single(result.Results.SelectMany(r => r.Diagnostics));
            Assert.Equal("PRIM002", diag.Id);
            Assert.Contains("expression-bodied", diag.GetMessage());
        }

        [Fact]
        public void YieldInFinally_IsDiagnosedAsErrorAndSkipped()
        {
            var src = Prologue + @"
        [Continuable]
        public int Doit()
        {
            int result = 0;
            try
            {
                result = 10;
            }
            finally
            {
                for (int i = 0; i < 3; i++) { result += i; }
            }
            return result;
        }
" + Epilogue;

            var (result, _) = Run(src);

            var diag = Assert.Single(result.Results.SelectMany(r => r.Diagnostics));
            Assert.Equal("PRIM003", diag.Id);
            Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
            Assert.Contains("finally", diag.GetMessage());

            // No (broken/partial) source was generated for the skipped method.
            Assert.DoesNotContain(
                result.GeneratedTrees,
                t => t.ToString().Contains("Doit_Continuable"));
        }

        [Fact]
        public void YieldInLock_IsDiagnosedAsErrorAndSkipped()
        {
            var src = Prologue + @"
        private readonly object _gate = new object();

        [Continuable]
        public int Doit()
        {
            int result = 0;
            lock (_gate)
            {
                for (int i = 0; i < 3; i++) { result += i; }
            }
            return result;
        }
" + Epilogue;

            var (result, _) = Run(src);

            var diag = Assert.Single(result.Results.SelectMany(r => r.Diagnostics));
            Assert.Equal("PRIM003", diag.Id);
            Assert.Equal(DiagnosticSeverity.Error, diag.Severity);
            Assert.Contains("lock", diag.GetMessage());
            Assert.DoesNotContain(result.GeneratedTrees, t => t.ToString().Contains("Doit_Continuable"));
        }

        [Fact]
        public void NestedContinuableCallInFinally_IsDiagnosedAsErrorAndSkipped()
        {
            // A continuable CALL (not just a loop) inside a finally must also be caught:
            // it is a yield point too. Helper() is continuable; calling it in Doit's
            // finally is illegal.
            var src = Prologue + @"
        [Continuable]
        public int Helper()
        {
            int s = 0;
            for (int i = 0; i < 3; i++) { s += i; }
            return s;
        }

        [Continuable]
        public int Doit()
        {
            int result = 0;
            try { result = 1; }
            finally { result += Helper(); }
            return result;
        }
" + Epilogue;

            var (result, _) = Run(src);

            // Helper itself is fine; only Doit is diagnosed.
            var diag = Assert.Single(result.Results.SelectMany(r => r.Diagnostics));
            Assert.Equal("PRIM003", diag.Id);
            Assert.Contains("finally", diag.GetMessage());
            Assert.Contains("'Doit'", diag.GetMessage());

            // Helper is still generated; Doit is not.
            Assert.Contains(result.GeneratedTrees, t => t.ToString().Contains("Helper_Continuable"));
            Assert.DoesNotContain(result.GeneratedTrees, t => t.ToString().Contains("Doit_Continuable"));
        }

        [Fact]
        public void SupportedMethod_ProducesNoDiagnostics_AndGeneratesCode()
        {
            var src = Prologue + @"
        [Continuable]
        public int Doit()
        {
            int sum = 0;
            for (int i = 0; i < 3; i++) { sum += i; }
            return sum;
        }
" + Epilogue;

            var (result, output) = Run(src);

            Assert.Empty(result.Results.SelectMany(r => r.Diagnostics));
            Assert.Contains(result.GeneratedTrees, t => t.ToString().Contains("Doit_Continuable"));

            // The generated code must COMPILE: no errors in the output compilation
            // beyond what the (deliberately minimal) test harness reference set allows.
            var errors = output.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            Assert.Empty(errors);
        }
    }
}
