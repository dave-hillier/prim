using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Prim.Cecil;
using Prim.Core;
using Prim.Runtime;
using Prim.Serialization;
using Xunit;

namespace Prim.Tests.Cecil
{
    /// <summary>
    /// Suspension across a chain of rewritten methods (whitepaper §3.2, §4.5, §6.1).
    ///
    /// The fixture is equivalent to:
    /// <code>
    ///   static int Outer()
    ///   {
    ///       int total = 0;
    ///       int k = 0;
    ///       while (k &lt; 3)
    ///       {
    ///           k = k + 1;
    ///           total = total + Inner(k + 1);   // 'total' is live on the eval stack across the call
    ///       }
    ///       return total * 100 + k;             // 1903
    ///   }
    ///
    ///   static int Inner(int n)
    ///   {
    ///       int sum = 0;
    ///       int i = 0;
    ///       while (i &lt; n) { i = i + 1; sum = sum + i; }
    ///       return sum;
    ///   }
    /// </code>
    /// When Inner suspends, Outer is suspended at its call to Inner. Outer's frame must
    /// record that call site, not Inner's yield point, so that on resume Outer re-executes
    /// the call (rather than, say, jumping to its own loop back-edge and re-incrementing k).
    /// </summary>
    public class NestedCallResumeTests : IDisposable
    {
        private const int ExpectedResult = 1903;

        private readonly string _tempDir;

        public NestedCallResumeTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"PrimNestedCall_{Guid.NewGuid():N}");
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
        public void Transform_NestedCall_ProducesVerifiableIl()
        {
            var outputPath = WriteTransformedFixture("nested_verify.dll");

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
        public void SuspendInsideInnerLoop_CapturesBothFrames_AndResumesToCorrectResult()
        {
            var outer = LoadOuter(WriteTransformedFixture("nested_exec.dll"));
            var serializer = new JsonContinuationSerializer();

            var previous = ScriptContext.Current;
            try
            {
                // Uninterrupted run.
                ScriptContext.Current = new ScriptContext();
                Assert.Equal(ExpectedResult, outer.Invoke(null, null));

                // Suspend at the first yield check reached: Inner's loop back-edge, during
                // Outer's first iteration (k == 1) after one Inner iteration (i == 1).
                var state = SuspendOnce(() => outer.Invoke(null, null), out _);
                Assert.NotNull(state);

                var outerFrame = state!.StackHead;
                Assert.Equal(
                    StableHash.GenerateMethodToken("TestNamespace.TestClass", "Outer"),
                    outerFrame.MethodToken);
                Assert.Equal(0, Assert.IsType<int>(outerFrame.Slots[0])); // total
                Assert.Equal(1, Assert.IsType<int>(outerFrame.Slots[1])); // k

                var innerFrame = outerFrame.Caller;
                Assert.NotNull(innerFrame);
                Assert.Equal(
                    StableHash.GenerateMethodToken("TestNamespace.TestClass", "Inner", "System.Int32"),
                    innerFrame.MethodToken);
                Assert.Equal(1, Assert.IsType<int>(innerFrame.Slots[0])); // sum
                Assert.Equal(1, Assert.IsType<int>(innerFrame.Slots[1])); // i
                Assert.Null(innerFrame.Caller);

                // The outer frame is suspended at a different yield point from the inner one:
                // its call site, not Inner's back-edge.
                Assert.NotEqual(innerFrame.YieldPointId, outerFrame.YieldPointId);

                // Serialize, deserialize and resume; keep suspending at every yield check
                // until the computation completes.
                object? result = null;
                int suspensions = 1;
                int twoFrameSuspensions = 1;
                while (state != null)
                {
                    var restored = serializer.Deserialize(serializer.Serialize(state));
                    var resumeCtx = new ScriptContext(restored, null);
                    state = SuspendOnce(() => outer.Invoke(null, null), out result, resumeCtx);

                    if (state != null)
                    {
                        suspensions++;
                        if (state.StackHead.Caller != null) twoFrameSuspensions++;
                        Assert.True(suspensions < 1000, "Resume loop did not terminate");
                    }
                }

                Assert.Equal(ExpectedResult, result);
                Assert.True(twoFrameSuspensions > 1,
                    $"Expected repeated suspensions inside Inner, saw {twoFrameSuspensions}");
                Assert.True(suspensions > twoFrameSuspensions,
                    "Expected suspensions at Outer's own back-edge as well");
            }
            finally
            {
                ScriptContext.Current = previous;
            }
        }

        [Fact]
        public void Transform_ByRefArgumentAtContinuableCall_IsSkippedWithDiagnostic()
        {
            // Outer passes 'ref total' to a transformed callee: the managed pointer on the
            // eval stack at the call site cannot be spilled into the frame record.
            var assembly = CreateAssemblyWithByRefCall();
            var rewriter = new AssemblyRewriter();

            rewriter.Transform(assembly);

            Assert.Contains(rewriter.SkippedMethods, s =>
                s.Method.Contains("Outer") && s.Reason.Contains("pointer"));
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

        private string WriteTransformedFixture(string fileName)
        {
            var assembly = CreateAssemblyWithNestedCall();
            var rewriter = new AssemblyRewriter();
            rewriter.Transform(assembly);
            Assert.Empty(rewriter.SkippedMethods);

            var outputPath = Path.Combine(_tempDir, fileName);
            assembly.Write(outputPath);
            return outputPath;
        }

        private static MethodInfo LoadOuter(string path)
        {
            var loaded = Assembly.Load(File.ReadAllBytes(path));
            return loaded.GetType("TestNamespace.TestClass")!.GetMethod("Outer")!;
        }

        #region Fixture

        private static AssemblyDefinition CreateAssemblyWithNestedCall()
        {
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("NestedCallAssembly", new Version(1, 0, 0, 0)),
                "NestedCallModule",
                ModuleKind.Dll);

            var module = assembly.MainModule;
            var attrCtor = CreateContinuableAttribute(module);

            var testClass = new TypeDefinition(
                "TestNamespace",
                "TestClass",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            testClass.CustomAttributes.Add(new CustomAttribute(attrCtor));
            module.Types.Add(testClass);

            var inner = CreateInner(module, attrCtor);
            testClass.Methods.Add(inner);
            testClass.Methods.Add(CreateOuter(module, attrCtor, inner));

            return assembly;
        }

        private static MethodDefinition CreateInner(ModuleDefinition module, MethodDefinition attrCtor)
        {
            var method = new MethodDefinition(
                "Inner",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                module.TypeSystem.Int32);
            method.Parameters.Add(new ParameterDefinition("n", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Int32));
            method.CustomAttributes.Add(new CustomAttribute(attrCtor));

            method.Body.InitLocals = true;
            var sum = new VariableDefinition(module.TypeSystem.Int32);
            var i = new VariableDefinition(module.TypeSystem.Int32);
            method.Body.Variables.Add(sum);
            method.Body.Variables.Add(i);

            var il = method.Body.GetILProcessor();
            var loopStart = il.Create(OpCodes.Nop);
            var end = il.Create(OpCodes.Nop);

            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, sum);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, i);

            il.Append(loopStart);
            il.Emit(OpCodes.Ldloc, i);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Bge, end);

            il.Emit(OpCodes.Ldloc, i);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, i);

            il.Emit(OpCodes.Ldloc, sum);
            il.Emit(OpCodes.Ldloc, i);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, sum);

            il.Emit(OpCodes.Br, loopStart);

            il.Append(end);
            il.Emit(OpCodes.Ldloc, sum);
            il.Emit(OpCodes.Ret);

            return method;
        }

        private static MethodDefinition CreateOuter(
            ModuleDefinition module, MethodDefinition attrCtor, MethodDefinition inner)
        {
            var method = new MethodDefinition(
                "Outer",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                module.TypeSystem.Int32);
            method.CustomAttributes.Add(new CustomAttribute(attrCtor));

            method.Body.InitLocals = true;
            var total = new VariableDefinition(module.TypeSystem.Int32);
            var k = new VariableDefinition(module.TypeSystem.Int32);
            method.Body.Variables.Add(total);
            method.Body.Variables.Add(k);

            var il = method.Body.GetILProcessor();
            var loopStart = il.Create(OpCodes.Nop);
            var end = il.Create(OpCodes.Nop);

            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, total);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, k);

            il.Append(loopStart);
            il.Emit(OpCodes.Ldloc, k);
            il.Emit(OpCodes.Ldc_I4_3);
            il.Emit(OpCodes.Bge, end);

            // k = k + 1
            il.Emit(OpCodes.Ldloc, k);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, k);

            // total = total + Inner(k + 1)
            il.Emit(OpCodes.Ldloc, total);
            il.Emit(OpCodes.Ldloc, k);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Call, inner);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, total);

            il.Emit(OpCodes.Br, loopStart);

            // return total * 100 + k
            il.Append(end);
            il.Emit(OpCodes.Ldloc, total);
            il.Emit(OpCodes.Ldc_I4, 100);
            il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Ldloc, k);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ret);

            return method;
        }

        private static AssemblyDefinition CreateAssemblyWithByRefCall()
        {
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("ByRefCallAssembly", new Version(1, 0, 0, 0)),
                "ByRefCallModule",
                ModuleKind.Dll);

            var module = assembly.MainModule;
            var attrCtor = CreateContinuableAttribute(module);

            var testClass = new TypeDefinition(
                "TestNamespace",
                "TestClass",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            testClass.CustomAttributes.Add(new CustomAttribute(attrCtor));
            module.Types.Add(testClass);

            // static void Bump(ref int x) { x = x + 1; }
            var bump = new MethodDefinition(
                "Bump",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                module.TypeSystem.Void);
            bump.Parameters.Add(new ParameterDefinition(
                "x", Mono.Cecil.ParameterAttributes.None, new ByReferenceType(module.TypeSystem.Int32)));
            bump.CustomAttributes.Add(new CustomAttribute(attrCtor));
            var bil = bump.Body.GetILProcessor();
            bil.Emit(OpCodes.Ldarg_0);
            bil.Emit(OpCodes.Ldarg_0);
            bil.Emit(OpCodes.Ldind_I4);
            bil.Emit(OpCodes.Ldc_I4_1);
            bil.Emit(OpCodes.Add);
            bil.Emit(OpCodes.Stind_I4);
            bil.Emit(OpCodes.Ret);
            testClass.Methods.Add(bump);

            // static int Outer() { int total = 0; Bump(ref total); return total; }
            var outer = new MethodDefinition(
                "Outer",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                module.TypeSystem.Int32);
            outer.CustomAttributes.Add(new CustomAttribute(attrCtor));
            outer.Body.InitLocals = true;
            var total = new VariableDefinition(module.TypeSystem.Int32);
            outer.Body.Variables.Add(total);
            var oil = outer.Body.GetILProcessor();
            oil.Emit(OpCodes.Ldc_I4_0);
            oil.Emit(OpCodes.Stloc, total);
            oil.Emit(OpCodes.Ldloca, total);
            oil.Emit(OpCodes.Call, bump);
            oil.Emit(OpCodes.Ldloc, total);
            oil.Emit(OpCodes.Ret);
            testClass.Methods.Add(outer);

            return assembly;
        }

        private static MethodDefinition CreateContinuableAttribute(ModuleDefinition module)
        {
            var attrType = new TypeDefinition(
                "Prim.Core",
                "ContinuableAttribute",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(Attribute)));

            var ctor = new MethodDefinition(
                ".ctor",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig |
                Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);

            var il = ctor.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            var baseCtor = typeof(Attribute).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            il.Emit(OpCodes.Call, module.ImportReference(baseCtor));
            il.Emit(OpCodes.Ret);

            attrType.Methods.Add(ctor);
            module.Types.Add(attrType);
            return ctor;
        }

        #endregion
    }
}
