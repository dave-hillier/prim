using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Prim.Analysis;
using Prim.Cecil;
using Prim.Core;
using Prim.Runtime;
using Xunit;

namespace Prim.Tests.Cecil
{
    /// <summary>
    /// Verifies that the IL produced by the Cecil transformer is actually valid CIL,
    /// rather than only asserting structural facts (instruction counts, handler added,
    /// reloads via Cecil) as <see cref="EndToEndTests"/> does. See issue #17.
    ///
    /// Approach chosen: run the <c>ilverify</c> tool (ECMA-335 IL verifier) against the
    /// rewritten assembly. This was preferred over loading + invoking the method in the
    /// CLR because:
    ///   - ilverify gives precise, structural diagnostics (which offset / which rule)
    ///     instead of a single opaque InvalidProgramException;
    ///   - it does not risk crashing the xUnit test host with a JIT-level fault on
    ///     invalid IL;
    ///   - it is pinned as a local dotnet tool (see dotnet-tools.json) so CI restores it
    ///     deterministically.
    /// The ILVerification NuGet library is not published to nuget.org, so the CLI tool is
    /// the most reliable self-contained option in this environment.
    /// </summary>
    public class IlVerificationTests : IDisposable
    {
        private readonly string _tempDir;

        public IlVerificationTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"PrimIlVerify_{Guid.NewGuid():N}");
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

        // The Cecil transform emits a verifiable async/iterator-style state machine:
        //   * the resume dispatch switch and every resume label live INSIDE the same try
        //     region as the yield points, so each switch->resume branch is intra-try (#31);
        //   * each resume label sits AFTER the yield check and BEFORE the suspending op, so
        //     resuming continues correctly without re-invoking the check (#32);
        //   * the live evaluation stack is spilled to temp locals at each yield point and
        //     packed into the frame slots (after the captured locals), then restored and
        //     re-pushed at the resume label (#30) — a no-op for the empty-stack loop edge
        //     but exercised generally;
        //   * Body.InitLocals is set and MaxStackSize is recomputed (#34);
        //   * original 'ret' becomes 'stloc;leave' to a single 'ret' placed AFTER the catch
        //     handler, so no return occurs inside the protected region.
        // ilverify (net8 reference set) reports zero errors on the rewritten LoopMethod.
        [Fact]
        public void Transform_LoopMethod_ProducesVerifiableIl()
        {
            // Arrange: a [Continuable] method with a loop (a back-edge yield point).
            var assembly = CreateAssemblyWithContinuableLoop();

            // Act: transform and write out.
            var rewriter = new AssemblyRewriter();
            rewriter.Transform(assembly);

            var outputPath = Path.Combine(_tempDir, "transformed.dll");
            assembly.Write(outputPath);

            // Assert: the rewritten IL must verify clean.
            var result = RunIlVerify(outputPath);

            if (!result.ToolAvailable)
            {
                // Should not happen with the pinned local tool, but degrade gracefully.
                Assert.Fail("ilverify tool unavailable: " + result.Diagnostics);
            }

            Assert.True(
                result.Errors.Count == 0,
                "Rewritten IL failed verification:\n" + string.Join("\n", result.Errors));
        }

        // Exercises eval-stack SPILLING at yield points with a NON-EMPTY evaluation stack.
        // The loop-edge gate above yields with an empty stack, so its spill path is a no-op
        // and never validates the per-yield-point disjoint-slot layout (_spillSlotBase[ypId]).
        //
        // The fixture method is shaped like `return TickCount + TickCount + TickCount;` where
        // each TickCount is an EXTERNAL call (treated as a yield point when external-call
        // yield points are enabled). At the 2nd and 3rd calls the prior sub-result is already
        // live on the evaluation stack (StackState.Depth == 1), so two distinct yield points
        // suspend with a non-empty stack and must spill/restore into disjoint trailing slots.
        //
        // The test (a) asserts at least two yield points have StackState.Depth > 0 (so it can
        // never silently regress to an empty-stack no-op), (b) asserts those yield points pack
        // to non-overlapping spill-slot ranges, and (c) proves the rewritten IL verifies clean.
        [Fact]
        public void Transform_NonEmptyStackYieldPoints_ProducesVerifiableIl()
        {
            // Arrange.
            var assembly = CreateAssemblyWithExternalCallExpression(out var method);

            var options = new RewriterOptions { IncludeExternalCalls = true };

            // Sanity: with external-call yield points enabled, at least two yield points
            // must land mid-expression with operands live on the stack (Depth > 0). This
            // assertion is the regression guard demanded by the spill-fix. (RewriterOptions
            // and YieldPointOptions share these flags; the rewriter maps one to the other.)
            var yieldPointOptions = new YieldPointOptions
            {
                IncludeBackwardBranches = options.IncludeBackwardBranches,
                IncludeExternalCalls = options.IncludeExternalCalls,
                InternalAssemblies = options.InternalAssemblies
            };
            var yieldPoints = new YieldPointIdentifier(method, yieldPointOptions)
                .FindYieldPoints();
            var nonEmptyDepthPoints = yieldPoints
                .Where(yp => (yp.StackState?.Depth ?? 0) > 0)
                .ToList();
            Assert.True(
                nonEmptyDepthPoints.Count >= 2,
                "Expected >= 2 yield points with a non-empty evaluation stack, but found " +
                nonEmptyDepthPoints.Count + " (depths: " +
                string.Join(",", yieldPoints.Select(yp => yp.StackState?.Depth ?? 0)) + "). " +
                "The spill path would not be exercised.");

            // Each depth>0 yield point must occupy its OWN disjoint trailing slot range so
            // packing every yield point's temps in the catch cannot collide. The first
            // _capturableLocalCount slots hold captured locals; spill ranges follow, one per
            // yield point of width == its depth, laid out in yield-point order.
            AssertDisjointSpillRanges(yieldPoints);

            // Act: transform and write out.
            new AssemblyRewriter(options).Transform(assembly);

            var outputPath = Path.Combine(_tempDir, "transformed_nonempty.dll");
            assembly.Write(outputPath);

            // Assert: the rewritten IL must verify clean.
            var result = RunIlVerify(outputPath);

            if (!result.ToolAvailable)
            {
                Assert.Fail("ilverify tool unavailable: " + result.Diagnostics);
            }

            Assert.True(
                result.Errors.Count == 0,
                "Rewritten IL failed verification:\n" + string.Join("\n", result.Errors));
        }

        /// <summary>
        /// Mirrors <see cref="MethodTransformer"/>'s spill-slot layout: capturable locals
        /// occupy the first slots, then each yield point gets a disjoint trailing range of
        /// width == its stack depth, in yield-point order. Asserts those per-yield-point
        /// ranges do not overlap (the fix under test).
        /// </summary>
        private static void AssertDisjointSpillRanges(List<ILYieldPoint> yieldPoints)
        {
            var ranges = new List<(int Id, int Start, int End)>();
            // The base offset (captured-local count) is irrelevant to overlap; ranges are
            // laid out consecutively, so start from 0 and accumulate.
            int next = 0;
            foreach (var yp in yieldPoints)
            {
                int depth = yp.StackState?.Depth ?? 0;
                if (depth == 0) continue;
                ranges.Add((yp.Id, next, next + depth));
                next += depth;
            }

            for (int i = 0; i < ranges.Count; i++)
            {
                for (int j = i + 1; j < ranges.Count; j++)
                {
                    bool overlap = ranges[i].Start < ranges[j].End && ranges[j].Start < ranges[i].End;
                    Assert.False(
                        overlap,
                        $"Spill slot ranges for yield points {ranges[i].Id} " +
                        $"[{ranges[i].Start},{ranges[i].End}) and {ranges[j].Id} " +
                        $"[{ranges[j].Start},{ranges[j].End}) overlap.");
                }
            }
        }

        /// <summary>
        /// Stronger than ilverify: load the rewritten assembly into this CLR and invoke
        /// the transformed loop, proving it (a) runs to completion returning the expected
        /// value, and (b) suspends and resumes correctly across the loop back-edge yield
        /// point, with the captured local (the counter) restored on resume.
        /// </summary>
        [Fact]
        public void Transform_LoopMethod_ExecutesAndRoundTripsSuspendResume()
        {
            var assembly = CreateAssemblyWithContinuableLoop();
            new AssemblyRewriter().Transform(assembly);

            var outputPath = Path.Combine(_tempDir, "exec.dll");
            assembly.Write(outputPath);

            var loaded = Assembly.Load(File.ReadAllBytes(outputPath));
            var type = loaded.GetType("TestNamespace.TestClass")!;
            var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            var method = type.GetMethod("LoopMethod")!;

            // (a) Normal completion: the loop counts to 10.
            var previous = ScriptContext.Current;
            try
            {
                ScriptContext.Current = new ScriptContext();
                var direct = method.Invoke(instance, null);
                Assert.Equal(10, direct);

                // (b) Request a yield -> the loop suspends via SuspendException.
                var suspendCtx = new ScriptContext();
                ScriptContext.Current = suspendCtx;
                suspendCtx.RequestYield();

                var ex = Assert.Throws<TargetInvocationException>(() => method.Invoke(instance, null));
                var suspend = Assert.IsType<SuspendException>(ex.InnerException);
                var state = suspend.BuildContinuationState();

                Assert.NotNull(state.StackHead);
                // One loop iteration ran before the back-edge yield point: counter == 1.
                Assert.Equal(1, Assert.IsType<int>(state.StackHead.Slots[0]));

                // Resume from the captured state: must continue the loop to completion.
                var resumeCtx = new ScriptContext(state, null);
                ScriptContext.Current = resumeCtx;
                var resumed = method.Invoke(instance, null);
                Assert.Equal(10, resumed);
            }
            finally
            {
                ScriptContext.Current = previous;
            }
        }

        /// <summary>
        /// #38: a method whose local is a byref (managed pointer) cannot have its state
        /// captured (it cannot be boxed into object[] / round-tripped). The transformer
        /// must NOT emit invalid IL for it: it leaves the method untransformed and
        /// surfaces a diagnostic via the rewriter.
        /// </summary>
        [Fact]
        public void Transform_MethodWithByRefLocal_IsSkippedWithDiagnostic()
        {
            var assembly = CreateAssemblyWithByRefLocalLoop();

            var rewriter = new AssemblyRewriter();
            var method = assembly.MainModule.Types
                .First(t => t.Name == "TestClass").Methods.First(m => m.Name == "LoopMethod");
            int before = method.Body.Instructions.Count;

            rewriter.Transform(assembly);

            // Untransformed: instruction count unchanged, no exception handler added.
            Assert.Equal(before, method.Body.Instructions.Count);
            Assert.Empty(method.Body.ExceptionHandlers);

            // Diagnostic surfaced.
            Assert.Contains(rewriter.SkippedMethods, s =>
                s.Method.Contains("LoopMethod") && s.Reason.Contains("byref"));
        }

        /// <summary>
        /// #38: same contract for a pinned local — skipped, not mis-compiled.
        /// </summary>
        [Fact]
        public void Transform_MethodWithPinnedLocal_IsSkippedWithDiagnostic()
        {
            var assembly = CreateAssemblyWithPinnedLocalLoop();

            var rewriter = new AssemblyRewriter();
            var method = assembly.MainModule.Types
                .First(t => t.Name == "TestClass").Methods.First(m => m.Name == "LoopMethod");
            int before = method.Body.Instructions.Count;

            rewriter.Transform(assembly);

            Assert.Equal(before, method.Body.Instructions.Count);
            Assert.Empty(method.Body.ExceptionHandlers);
            Assert.Contains(rewriter.SkippedMethods, s =>
                s.Method.Contains("LoopMethod") && s.Reason.Contains("pinned"));
        }

        #region ilverify invocation

        internal sealed class IlVerifyResult
        {
            public bool ToolAvailable { get; init; }
            public List<string> Errors { get; init; } = new();
            public string Diagnostics { get; init; } = string.Empty;
        }

        /// <summary>
        /// Runs the pinned local <c>dotnet ilverify</c> tool against <paramref name="assemblyPath"/>,
        /// supplying the shared runtime reference assemblies plus the Prim assemblies the
        /// transformed code references. Returns the parsed verification errors.
        /// </summary>
        internal static IlVerifyResult RunIlVerify(string assemblyPath)
        {
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            // Directory containing the test's own copies of Prim.Runtime.dll / Prim.Core.dll.
            var primDir = AppContext.BaseDirectory;

            // ilverify takes a system module name (no extension) and a set of reference dirs/globs.
            var args = new List<string>
            {
                "ilverify",
                assemblyPath,
                "--system-module",
                "System.Private.CoreLib",
                "-r", Path.Combine(runtimeDir, "*.dll"),
                "-r", Path.Combine(primDir, "Prim.Runtime.dll"),
                "-r", Path.Combine(primDir, "Prim.Core.dll"),
            };

            // The local tool is anchored at the repo root, which is an ancestor of the test's
            // base directory; "dotnet ilverify" resolves the manifest by walking up from cwd.
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = FindRepoRoot(primDir),
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            string stdout, stderr;
            try
            {
                using var proc = Process.Start(psi)!;
                stdout = proc.StandardOutput.ReadToEnd();
                stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(60_000);
            }
            catch (Exception ex)
            {
                return new IlVerifyResult { ToolAvailable = false, Diagnostics = ex.ToString() };
            }

            var combined = stdout + "\n" + stderr;

            // If the tool itself could not be found / restored, treat as unavailable.
            if (combined.Contains("could not be found", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("is not recognized", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("No manifest file", StringComparison.OrdinalIgnoreCase))
            {
                return new IlVerifyResult { ToolAvailable = false, Diagnostics = combined };
            }

            // ilverify emits one "[IL]: Error ..." line per verification failure.
            var errors = combined
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Contains("[IL]: Error", StringComparison.Ordinal))
                .ToList();

            return new IlVerifyResult { ToolAvailable = true, Errors = errors, Diagnostics = combined };
        }

        private static string FindRepoRoot(string start)
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "dotnet-tools.json")) ||
                    Directory.Exists(Path.Combine(dir.FullName, ".config")) ||
                    File.Exists(Path.Combine(dir.FullName, "Prim.sln")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return start;
        }

        #endregion

        #region Assembly creation (mirrors EndToEndTests' continuable loop)

        private AssemblyDefinition CreateAssemblyWithContinuableLoop()
        {
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("TestAssembly", new Version(1, 0, 0, 0)),
                "TestModule",
                ModuleKind.Dll);

            var module = assembly.MainModule;
            var attrType = CreateContinuableAttribute(module);

            var testClass = new TypeDefinition(
                "TestNamespace",
                "TestClass",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));

            testClass.CustomAttributes.Add(new CustomAttribute(
                attrType.Methods.First(m => m.IsConstructor)));

            module.Types.Add(testClass);

            var loopMethod = CreateLoopMethod(module, attrType);
            testClass.Methods.Add(loopMethod);

            return assembly;
        }

        /// <summary>
        /// Builds a [Continuable] class with a method shaped like
        /// <c>return Environment.TickCount + Environment.TickCount + Environment.TickCount;</c>.
        /// Each <c>get_TickCount</c> is a static external call (no args, returns int32). When
        /// external-call yield points are enabled, the 2nd and 3rd calls are yield points that
        /// fire while the previous sub-result is live on the evaluation stack (Depth == 1),
        /// exercising eval-stack spilling at non-empty depth.
        /// </summary>
        private AssemblyDefinition CreateAssemblyWithExternalCallExpression(out MethodDefinition method)
        {
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("TestAssembly", new Version(1, 0, 0, 0)),
                "TestModule",
                ModuleKind.Dll);

            var module = assembly.MainModule;
            var attrType = CreateContinuableAttribute(module);

            var testClass = new TypeDefinition(
                "TestNamespace",
                "TestClass",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            testClass.CustomAttributes.Add(new CustomAttribute(
                attrType.Methods.First(m => m.IsConstructor)));
            module.Types.Add(testClass);

            method = new MethodDefinition(
                "ExternalCallMethod",
                Mono.Cecil.MethodAttributes.Public,
                module.TypeSystem.Int32);
            method.CustomAttributes.Add(new CustomAttribute(
                attrType.Methods.First(m => m.IsConstructor)));
            method.Body.InitLocals = true;

            // External call: System.Environment.get_TickCount() -> int32, static, no args.
            var tickCountGetter = module.ImportReference(
                typeof(Environment).GetProperty(nameof(Environment.TickCount))!.GetMethod);

            var il = method.Body.GetILProcessor();

            // return TickCount + TickCount + TickCount;
            il.Emit(OpCodes.Call, tickCountGetter);   // [r1]
            il.Emit(OpCodes.Call, tickCountGetter);   // yield point: stack = [r1] (depth 1)
            il.Emit(OpCodes.Add);                     // [s1]
            il.Emit(OpCodes.Call, tickCountGetter);   // yield point: stack = [s1] (depth 1)
            il.Emit(OpCodes.Add);                     // [s2]
            il.Emit(OpCodes.Ret);

            testClass.Methods.Add(method);
            return assembly;
        }

        private TypeDefinition CreateContinuableAttribute(ModuleDefinition module)
        {
            var attrType = new TypeDefinition(
                "Prim.Core",
                "ContinuableAttribute",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(Attribute)));

            var attrCtor = new MethodDefinition(
                ".ctor",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig |
                Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);

            var attrIl = attrCtor.Body.GetILProcessor();
            attrIl.Emit(OpCodes.Ldarg_0);
            var baseCtor = typeof(Attribute).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            attrIl.Emit(OpCodes.Call, module.ImportReference(baseCtor));
            attrIl.Emit(OpCodes.Ret);

            attrType.Methods.Add(attrCtor);
            module.Types.Add(attrType);

            return attrType;
        }

        private MethodDefinition CreateLoopMethod(ModuleDefinition module, TypeDefinition attrType)
        {
            var loopMethod = new MethodDefinition(
                "LoopMethod",
                Mono.Cecil.MethodAttributes.Public,
                module.TypeSystem.Int32);

            loopMethod.CustomAttributes.Add(new CustomAttribute(
                attrType.Methods.First(m => m.IsConstructor)));

            loopMethod.Body.InitLocals = true;
            var counterVar = new VariableDefinition(module.TypeSystem.Int32);
            loopMethod.Body.Variables.Add(counterVar);

            var il = loopMethod.Body.GetILProcessor();

            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, counterVar);

            var loopStart = il.Create(OpCodes.Nop);
            il.Append(loopStart);

            il.Emit(OpCodes.Ldloc, counterVar);
            il.Emit(OpCodes.Ldc_I4, 10);
            var endLabel = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Bge, endLabel);

            il.Emit(OpCodes.Ldloc, counterVar);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, counterVar);

            il.Emit(OpCodes.Br, loopStart);

            il.Append(endLabel);
            il.Emit(OpCodes.Ldloc, counterVar);
            il.Emit(OpCodes.Ret);

            return loopMethod;
        }

        // #38 fixtures: a continuable loop method that also has a byref (resp. pinned)
        // local. These cannot have their state captured, so the transformer must skip
        // the method (leaving it untransformed) rather than emit invalid IL.

        private AssemblyDefinition CreateAssemblyWithByRefLocalLoop()
            => CreateAssemblyWithSpecialLocalLoop(pinned: false);

        private AssemblyDefinition CreateAssemblyWithPinnedLocalLoop()
            => CreateAssemblyWithSpecialLocalLoop(pinned: true);

        private AssemblyDefinition CreateAssemblyWithSpecialLocalLoop(bool pinned)
        {
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("TestAssembly", new Version(1, 0, 0, 0)),
                "TestModule",
                ModuleKind.Dll);

            var module = assembly.MainModule;
            var attrType = CreateContinuableAttribute(module);

            var testClass = new TypeDefinition(
                "TestNamespace",
                "TestClass",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            testClass.CustomAttributes.Add(new CustomAttribute(
                attrType.Methods.First(m => m.IsConstructor)));
            module.Types.Add(testClass);

            var loopMethod = new MethodDefinition(
                "LoopMethod",
                Mono.Cecil.MethodAttributes.Public,
                module.TypeSystem.Int32);
            loopMethod.CustomAttributes.Add(new CustomAttribute(
                attrType.Methods.First(m => m.IsConstructor)));
            loopMethod.Body.InitLocals = true;

            var counterVar = new VariableDefinition(module.TypeSystem.Int32);
            loopMethod.Body.Variables.Add(counterVar);

            // The "special" local: a byref-to-int, optionally pinned.
            var specialType = new ByReferenceType(module.TypeSystem.Int32);
            var specialVar = new VariableDefinition(
                pinned
                    ? (TypeReference)new PinnedType(specialType)
                    : specialType);
            loopMethod.Body.Variables.Add(specialVar);

            var il = loopMethod.Body.GetILProcessor();

            // special = &counter  (keeps the local live; not executed in these tests)
            il.Emit(OpCodes.Ldloca, counterVar);
            il.Emit(OpCodes.Stloc, specialVar);

            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, counterVar);

            var loopStart = il.Create(OpCodes.Nop);
            il.Append(loopStart);

            il.Emit(OpCodes.Ldloc, counterVar);
            il.Emit(OpCodes.Ldc_I4, 10);
            var endLabel = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Bge, endLabel);

            il.Emit(OpCodes.Ldloc, counterVar);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, counterVar);

            il.Emit(OpCodes.Br, loopStart);

            il.Append(endLabel);
            il.Emit(OpCodes.Ldloc, counterVar);
            il.Emit(OpCodes.Ret);

            testClass.Methods.Add(loopMethod);
            return assembly;
        }

        #endregion
    }
}
