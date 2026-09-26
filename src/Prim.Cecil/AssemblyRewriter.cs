using System;
using System.Collections.Generic;
using System.IO;
using Prim.Analysis;
using Mono.Cecil;

namespace Prim.Cecil
{
    /// <summary>
    /// Entry point for bytecode rewriting to add continuation support.
    /// Transforms assemblies to add yield points, state capture, and restoration.
    /// </summary>
    public sealed class AssemblyRewriter
    {
        private readonly RewriterOptions _options;

        /// <summary>
        /// Methods that were eligible for transformation (had [Continuable] and yield
        /// points) but were skipped because their state could not be safely captured
        /// (e.g. byref/pinned/pointer locals or ref/out parameters; see #38). Each entry
        /// is (method full name, reason). A skipped method is left untransformed rather
        /// than producing invalid IL.
        /// </summary>
        public List<(string Method, string Reason)> SkippedMethods { get; } =
            new List<(string, string)>();

        public AssemblyRewriter() : this(RewriterOptions.Default)
        {
        }

        public AssemblyRewriter(RewriterOptions options)
        {
            _options = options ?? RewriterOptions.Default;
        }

        /// <summary>
        /// Transforms an assembly to add continuation support.
        /// </summary>
        /// <param name="assembly">The assembly to transform.</param>
        /// <returns>The transformed assembly.</returns>
        public AssemblyDefinition Transform(AssemblyDefinition assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));

            // Collect every candidate method up front so each transformer knows which call
            // targets are themselves transformed: those call sites become resume points.
            var candidates = new List<MethodDefinition>();
            foreach (var module in assembly.Modules)
            {
                foreach (var type in module.Types)
                {
                    CollectCandidates(type, candidates);
                }
            }

            var candidateSet = new HashSet<MethodDefinition>(candidates);
            bool IsContinuableCall(MethodReference called)
            {
                MethodDefinition resolved;
                try
                {
                    resolved = called.Resolve();
                }
                catch (AssemblyResolutionException)
                {
                    return false;
                }
                return resolved != null && candidateSet.Contains(resolved);
            }

            foreach (var method in candidates)
            {
                var transformer = new MethodTransformer(method, _options, IsContinuableCall);
                transformer.Transform();
                if (!transformer.WasTransformed && transformer.SkipReason != null)
                {
                    SkippedMethods.Add((method.FullName, transformer.SkipReason));
                }
            }

            return assembly;
        }

        /// <summary>
        /// Transforms an assembly file.
        /// </summary>
        /// <param name="inputPath">Path to the input assembly.</param>
        /// <param name="outputPath">Path for the output assembly.</param>
        public void Transform(string inputPath, string outputPath)
        {
            if (string.IsNullOrEmpty(inputPath)) throw new ArgumentNullException(nameof(inputPath));
            if (string.IsNullOrEmpty(outputPath)) throw new ArgumentNullException(nameof(outputPath));

            var readerParams = new ReaderParameters
            {
                ReadSymbols = _options.PreserveDebugSymbols && File.Exists(Path.ChangeExtension(inputPath, ".pdb"))
            };

            using (var assembly = AssemblyDefinition.ReadAssembly(inputPath, readerParams))
            {
                Transform(assembly);

                var writerParams = new WriterParameters
                {
                    WriteSymbols = _options.PreserveDebugSymbols && readerParams.ReadSymbols
                };

                assembly.Write(outputPath, writerParams);
            }
        }

        private void CollectCandidates(TypeDefinition type, List<MethodDefinition> candidates)
        {
            // Check if type has [Continuable] attribute
            if (!ShouldTransformType(type)) return;

            foreach (var method in type.Methods)
            {
                if (ShouldTransformMethod(method))
                {
                    candidates.Add(method);
                }
            }

            // Process nested types
            foreach (var nestedType in type.NestedTypes)
            {
                CollectCandidates(nestedType, candidates);
            }
        }

        private bool ShouldTransformType(TypeDefinition type)
        {
            // Check for [Continuable] attribute on type
            foreach (var attr in type.CustomAttributes)
            {
                if (attr.AttributeType.Name == "ContinuableAttribute" ||
                    attr.AttributeType.FullName == "Prim.Core.ContinuableAttribute")
                {
                    return true;
                }
            }
            return false;
        }

        private bool ShouldTransformMethod(MethodDefinition method)
        {
            // Skip abstract, extern, pinvoke
            if (!method.HasBody) return false;
            if (method.IsAbstract) return false;

            // Check for [Continuable] attribute on method
            foreach (var attr in method.CustomAttributes)
            {
                if (attr.AttributeType.Name == "ContinuableAttribute" ||
                    attr.AttributeType.FullName == "Prim.Core.ContinuableAttribute")
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Options for the assembly rewriter.
    /// </summary>
    public sealed class RewriterOptions
    {
        /// <summary>
        /// Default options.
        /// </summary>
        public static readonly RewriterOptions Default = new RewriterOptions();

        /// <summary>
        /// Whether to preserve debug symbols.
        /// </summary>
        public bool PreserveDebugSymbols { get; set; } = true;

        /// <summary>
        /// Whether to add yield points at backward branches (loops).
        /// Default: true
        /// </summary>
        public bool IncludeBackwardBranches { get; set; } = true;

        /// <summary>
        /// Whether to add yield points at external method calls.
        /// Enables full Second Life-style behavior.
        /// Default: false
        /// </summary>
        public bool IncludeExternalCalls { get; set; } = false;

        /// <summary>
        /// Assemblies to consider as "internal" when detecting external calls.
        /// Calls to methods in these assemblies won't be yield points.
        /// </summary>
        public HashSet<string> InternalAssemblies { get; set; } = new HashSet<string>();

        /// <summary>
        /// Whether to inject instruction counting for preemptive scheduling.
        /// When enabled, yield points decrement a budget and suspend when exhausted.
        /// Default: true
        /// </summary>
        public bool EnableInstructionCounting { get; set; } = true;

        /// <summary>
        /// Creates YieldPointOptions from these rewriter options.
        /// </summary>
        internal YieldPointOptions ToYieldPointOptions()
        {
            return new YieldPointOptions
            {
                IncludeBackwardBranches = IncludeBackwardBranches,
                IncludeExternalCalls = IncludeExternalCalls,
                InternalAssemblies = InternalAssemblies
            };
        }
    }
}
