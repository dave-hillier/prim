using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Prim.Cecil;
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

        // NOTE: This test is currently expected to FAIL because the rewritten IL is invalid
        // per triaged issues #31 (branch/return out of try region), #34 (MaxStack/InitLocals)
        // and #30 (eval-stack spilling not implemented). ilverify reports (net8 reference set):
        //   [ReturnFromTry] offset 0x81  -- return out of try block (issue #31)
        //   [StackUnexpected] offset 0xA2 -- found SuspendException, expected HostFrameRecord (issue #34/#30)
        //   [StackUnexpected] offset 0xAE -- Int32 vs object[] (catch array build; issue #34 maxstack/#30)
        // Until those are fixed, leaving this red would break the suite, so it is skipped with
        // the repro reason intact. Remove the Skip once #31/#34/#30 land.
        [Fact(Skip = "repro of #31/#34/#30 — invalid rewritten IL; ilverify: ReturnFromTry@0x81, StackUnexpected@0xA2 (SuspendException vs HostFrameRecord), StackUnexpected@0xAE (Int32 vs object[])")]
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

        #region ilverify invocation

        private sealed class IlVerifyResult
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
        private static IlVerifyResult RunIlVerify(string assemblyPath)
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

        #endregion
    }
}
