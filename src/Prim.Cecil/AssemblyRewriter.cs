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
        /// Methods carrying <c>[Continuable]</c> that were not transformed: methods with no
        /// body, instance methods on value types, and methods whose state could not be
        /// safely captured (byref/pinned/pointer locals, ref/out parameters, a managed
        /// pointer live at a yield point, writes to <c>this</c>; see #38). Each entry is
        /// (method full name, reason). A skipped method is left untransformed rather than
        /// producing invalid IL, and calls to it are not resume points. A method that has
        /// no yield point at all is not listed; one whose yield points were all skipped is
        /// listed only in <see cref="SkippedYieldPoints"/>.
        /// </summary>
        public List<(string Method, string Reason)> SkippedMethods { get; } =
            new List<(string, string)>();

        /// <summary>
        /// Candidate yield points that were not placed. A yield point inside a user
        /// <c>try</c> block is supported; one inside a catch handler, a <c>finally</c> or
        /// <c>fault</c> block, an exception filter or a <c>lock</c> body is skipped, because
        /// there is no way to resume there. So is one where a value live on the evaluation
        /// stack cannot be typed with confidence, since spilling it into a temp of the wrong
        /// type would be invalid IL. Each entry is (method full name, description
        /// with the original IL offset and the reason). The rest of the method is still
        /// transformed.
        /// </summary>
        public List<(string Method, string Reason)> SkippedYieldPoints { get; } =
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
        ///
        /// <para>
        /// A method is a candidate when it carries <c>[Continuable]</c> itself, whatever
        /// type it is declared in (nested, unattributed, a struct). A <c>[Continuable]</c>
        /// on a type does not select its methods. A candidate that cannot be transformed
        /// is listed in <see cref="SkippedMethods"/>; one whose yield points were all
        /// skipped is listed only in <see cref="SkippedYieldPoints"/>.
        /// </para>
        ///
        /// <para>
        /// A call is a resume point when its target may be a transformed method: a
        /// transformed method itself, or a virtual or interface method that a transformed
        /// method overrides or implements. Candidates that will not be transformed are
        /// removed before any method is rewritten, repeatedly until no more drop out, so
        /// that no caller treats a call to an untransformed method as a resume point.
        /// </para>
        /// </summary>
        /// <param name="assembly">The assembly to transform.</param>
        /// <returns>The transformed assembly.</returns>
        public AssemblyDefinition Transform(AssemblyDefinition assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));

            var candidates = new List<MethodDefinition>();
            foreach (var module in assembly.Modules)
            {
                foreach (var type in module.Types)
                {
                    CollectCandidates(type, candidates);
                }
            }

            var transformed = new HashSet<MethodDefinition>(candidates);
            var overridden = new HashSet<MethodDefinition>();

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
                if (resolved == null) return false;
                if (transformed.Contains(resolved)) return true;

                // A virtual or interface call may dispatch to a transformed override.
                return (resolved.IsVirtual || resolved.IsAbstract) && overridden.Contains(resolved);
            }

            // Drop candidates that will not be transformed until the set is stable:
            // removing a callee changes its callers' resume points.
            bool changed = true;
            while (changed)
            {
                changed = false;
                ComputeOverridden(transformed, overridden);
                foreach (var method in candidates)
                {
                    if (!transformed.Contains(method)) continue;

                    var transformer = new MethodTransformer(method, _options, IsContinuableCall);
                    if (transformer.Prepare()) continue;

                    transformed.Remove(method);
                    changed = true;
                    Report(method, transformer);
                }
            }

            foreach (var method in candidates)
            {
                if (!transformed.Contains(method)) continue;

                var transformer = new MethodTransformer(method, _options, IsContinuableCall);
                transformer.Transform();
                Report(method, transformer);
            }

            return assembly;
        }

        private void Report(MethodDefinition method, MethodTransformer transformer)
        {
            foreach (var skipped in transformer.SkippedYieldPoints)
            {
                SkippedYieldPoints.Add((method.FullName, skipped));
            }
            if (!transformer.WasTransformed && transformer.SkipReason != null)
            {
                SkippedMethods.Add((method.FullName, transformer.SkipReason));
            }
        }

        /// <summary>
        /// Collects into <paramref name="overridden"/> every method that a method in
        /// <paramref name="transformed"/> overrides or implements: base-class virtuals with
        /// the same name and parameter count, explicitly overridden methods, and interface
        /// methods with the same name and parameter count. The match is deliberately loose:
        /// treating an extra call as a resume point is harmless, missing one is not.
        /// </summary>
        private static void ComputeOverridden(
            HashSet<MethodDefinition> transformed, HashSet<MethodDefinition> overridden)
        {
            overridden.Clear();
            foreach (var method in transformed)
            {
                if (!method.IsVirtual) continue;

                foreach (var explicitOverride in method.Overrides)
                {
                    var target = TryResolve(explicitOverride);
                    if (target != null) overridden.Add(target);
                }

                for (var type = TryResolve(method.DeclaringType); type != null; type = TryResolve(type.BaseType))
                {
                    if (type != method.DeclaringType)
                    {
                        foreach (var candidate in type.Methods)
                        {
                            if (candidate.IsVirtual && Matches(candidate, method)) overridden.Add(candidate);
                        }
                    }

                    foreach (var implementation in type.Interfaces)
                    {
                        var interfaceType = TryResolve(implementation.InterfaceType);
                        if (interfaceType == null) continue;
                        foreach (var candidate in interfaceType.Methods)
                        {
                            if (Matches(candidate, method)) overridden.Add(candidate);
                        }
                    }
                }
            }
        }

        private static bool Matches(MethodDefinition a, MethodDefinition b)
        {
            return a.Name == b.Name && a.Parameters.Count == b.Parameters.Count;
        }

        private static TypeDefinition TryResolve(TypeReference type)
        {
            if (type == null) return null;
            try
            {
                return type.Resolve();
            }
            catch (AssemblyResolutionException)
            {
                return null;
            }
        }

        private static MethodDefinition TryResolve(MethodReference method)
        {
            try
            {
                return method.Resolve();
            }
            catch (AssemblyResolutionException)
            {
                return null;
            }
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
            foreach (var method in type.Methods)
            {
                if (!HasContinuableAttribute(method.CustomAttributes)) continue;

                if (!method.HasBody)
                {
                    SkippedMethods.Add((method.FullName,
                        "method has no body (abstract, extern or an interface method); " +
                        "mark the implementing methods [Continuable] instead"));
                    continue;
                }

                candidates.Add(method);
            }

            foreach (var nestedType in type.NestedTypes)
            {
                CollectCandidates(nestedType, candidates);
            }
        }

        private static bool HasContinuableAttribute(IEnumerable<CustomAttribute> attributes)
        {
            foreach (var attr in attributes)
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
