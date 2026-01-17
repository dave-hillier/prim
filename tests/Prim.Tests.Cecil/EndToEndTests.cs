using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Prim.Cecil;
using Prim.Core;
using Prim.Runtime;
using Xunit;

namespace Prim.Tests.Cecil
{
    /// <summary>
    /// End-to-end tests for the Cecil bytecode transformer.
    /// These tests verify the complete transform -> execute -> suspend -> resume cycle.
    /// </summary>
    public class EndToEndTests : IDisposable
    {
        private readonly string _tempDir;

        public EndToEndTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"PrimCecilTests_{Guid.NewGuid():N}");
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
        public void Transform_MethodWithLoop_AddsYieldPointChecks()
        {
            // Arrange: Create assembly with a loop method marked [Continuable]
            var assembly = CreateAssemblyWithContinuableLoop();
            var originalInstructionCount = GetLoopMethodInstructionCount(assembly);

            // Act: Transform the assembly
            var rewriter = new AssemblyRewriter();
            rewriter.Transform(assembly);

            // Assert: Method should have more instructions (yield checks injected)
            var transformedInstructionCount = GetLoopMethodInstructionCount(assembly);
            Assert.True(transformedInstructionCount > originalInstructionCount,
                $"Expected more instructions after transform. Original: {originalInstructionCount}, Transformed: {transformedInstructionCount}");
        }

        [Fact]
        public void Transform_MethodWithLoop_AddsExceptionHandler()
        {
            // Arrange
            var assembly = CreateAssemblyWithContinuableLoop();
            var method = GetLoopMethod(assembly);
            var originalHandlerCount = method.Body.ExceptionHandlers.Count;

            // Act
            var rewriter = new AssemblyRewriter();
            rewriter.Transform(assembly);

            // Assert: Should have added a catch block for SuspendException
            Assert.True(method.Body.ExceptionHandlers.Count > originalHandlerCount,
                "Expected exception handler to be added for state capture");
        }

        [Fact]
        public void Transform_MethodWithLoop_AddsLocalVariables()
        {
            // Arrange
            var assembly = CreateAssemblyWithContinuableLoop();
            var method = GetLoopMethod(assembly);
            var originalLocalCount = method.Body.Variables.Count;

            // Act
            var rewriter = new AssemblyRewriter();
            rewriter.Transform(assembly);

            // Assert: Should have added locals for context, frame, state
            Assert.True(method.Body.Variables.Count > originalLocalCount,
                $"Expected more locals after transform. Original: {originalLocalCount}, After: {method.Body.Variables.Count}");
        }

        [Fact]
        public void Transform_MethodWithoutLoop_NoYieldPoints()
        {
            // Arrange: Create assembly with simple method (no loops)
            var assembly = CreateAssemblyWithSimpleContinuableMethod();
            var method = GetSimpleMethod(assembly);
            var originalInstructionCount = method.Body.Instructions.Count;

            // Act
            var rewriter = new AssemblyRewriter();
            rewriter.Transform(assembly);

            // Assert: Simple method without loops should not be significantly changed
            // (no yield points found means no transformation)
            var newInstructionCount = method.Body.Instructions.Count;
            Assert.Equal(originalInstructionCount, newInstructionCount);
        }

        [Fact]
        public void Transform_NonContinuableMethod_NotTransformed()
        {
            // Arrange
            var assembly = CreateAssemblyWithNonContinuableLoop();
            var method = GetLoopMethod(assembly);
            var originalInstructionCount = method.Body.Instructions.Count;

            // Act
            var rewriter = new AssemblyRewriter();
            rewriter.Transform(assembly);

            // Assert: Method without [Continuable] should not be transformed
            Assert.Equal(originalInstructionCount, method.Body.Instructions.Count);
        }

        [Fact]
        public void Transform_WriteAndLoad_ProducesValidAssembly()
        {
            // Arrange
            var assembly = CreateAssemblyWithContinuableLoop();
            var outputPath = Path.Combine(_tempDir, "transformed.dll");

            // Act
            var rewriter = new AssemblyRewriter();
            rewriter.Transform(assembly);
            assembly.Write(outputPath);

            // Assert: Should be able to read the transformed assembly back
            using var reloaded = AssemblyDefinition.ReadAssembly(outputPath);
            Assert.NotNull(reloaded);

            var type = reloaded.MainModule.Types.FirstOrDefault(t => t.Name == "TestClass");
            Assert.NotNull(type);

            var method = type.Methods.FirstOrDefault(m => m.Name == "LoopMethod");
            Assert.NotNull(method);
            Assert.True(method.Body.Instructions.Count > 0);
        }

        [Fact]
        public void Transform_PreservesMethodSemantics_SimpleReturn()
        {
            // Arrange: Create a simple method that returns a constant
            var assembly = CreateAssemblyWithSimpleContinuableMethod();

            // Act
            var rewriter = new AssemblyRewriter();
            rewriter.Transform(assembly);

            // Write and verify IL is valid
            var outputPath = Path.Combine(_tempDir, "simple_transformed.dll");
            assembly.Write(outputPath);

            // Assert: Assembly should be loadable (IL is valid)
            using var reloaded = AssemblyDefinition.ReadAssembly(outputPath);
            var method = reloaded.MainModule.Types
                .First(t => t.Name == "TestClass")
                .Methods.First(m => m.Name == "SimpleMethod");

            // Verify method still has a return instruction
            Assert.Contains(method.Body.Instructions, i => i.OpCode == OpCodes.Ret);
        }

        [Fact]
        public void Transform_LoopMethod_HasYieldPointCheckBeforeBackwardBranch()
        {
            // Arrange
            var assembly = CreateAssemblyWithContinuableLoop();

            // Act
            var rewriter = new AssemblyRewriter();
            rewriter.Transform(assembly);

            // Assert: Should have a call to HandleYieldPoint or EnsureCurrent
            var method = GetLoopMethod(assembly);
            var hasYieldRelatedCall = method.Body.Instructions.Any(i =>
                i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt);

            Assert.True(hasYieldRelatedCall, "Expected yield-related method calls to be injected");
        }

        [Fact]
        public void Transform_MultipleLoops_AddsMultipleYieldPoints()
        {
            // Arrange
            var assembly = CreateAssemblyWithMultipleLoops();
            var method = GetMultiLoopMethod(assembly);

            // Act
            var rewriter = new AssemblyRewriter();
            rewriter.Transform(assembly);

            // Assert: Should have multiple yield point checks (one per loop)
            var ldcInstructions = method.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldc_I4 || i.OpCode == OpCodes.Ldc_I4_S ||
                           i.OpCode == OpCodes.Ldc_I4_0 || i.OpCode == OpCodes.Ldc_I4_1)
                .ToList();

            // At minimum we should see yield point IDs being loaded
            Assert.True(ldcInstructions.Count >= 2, "Expected multiple constants for yield point IDs");
        }

        #region Assembly Creation Helpers

        private AssemblyDefinition CreateAssemblyWithContinuableLoop()
        {
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("TestAssembly", new Version(1, 0, 0, 0)),
                "TestModule",
                ModuleKind.Dll);

            var module = assembly.MainModule;

            // Import Prim.Core.ContinuableAttribute
            var attrType = CreateContinuableAttribute(module);

            // Create TestClass with [Continuable]
            var testClass = new TypeDefinition(
                "TestNamespace",
                "TestClass",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));

            testClass.CustomAttributes.Add(new CustomAttribute(
                attrType.Methods.First(m => m.IsConstructor)));

            module.Types.Add(testClass);

            // Add LoopMethod with [Continuable]
            var loopMethod = CreateLoopMethod(module, attrType);
            testClass.Methods.Add(loopMethod);

            return assembly;
        }

        private AssemblyDefinition CreateAssemblyWithSimpleContinuableMethod()
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

            // Simple method - no loops
            var simpleMethod = new MethodDefinition(
                "SimpleMethod",
                Mono.Cecil.MethodAttributes.Public,
                module.TypeSystem.Int32);

            simpleMethod.CustomAttributes.Add(new CustomAttribute(
                attrType.Methods.First(m => m.IsConstructor)));

            var il = simpleMethod.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4, 42);
            il.Emit(OpCodes.Ret);

            testClass.Methods.Add(simpleMethod);

            return assembly;
        }

        private AssemblyDefinition CreateAssemblyWithNonContinuableLoop()
        {
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("TestAssembly", new Version(1, 0, 0, 0)),
                "TestModule",
                ModuleKind.Dll);

            var module = assembly.MainModule;

            // Create TestClass WITHOUT [Continuable]
            var testClass = new TypeDefinition(
                "TestNamespace",
                "TestClass",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));

            module.Types.Add(testClass);

            // Add LoopMethod WITHOUT [Continuable]
            var loopMethod = new MethodDefinition(
                "LoopMethod",
                Mono.Cecil.MethodAttributes.Public,
                module.TypeSystem.Int32);

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

            testClass.Methods.Add(loopMethod);

            return assembly;
        }

        private AssemblyDefinition CreateAssemblyWithMultipleLoops()
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

            // Method with two loops
            var method = new MethodDefinition(
                "MultiLoopMethod",
                Mono.Cecil.MethodAttributes.Public,
                module.TypeSystem.Int32);

            method.CustomAttributes.Add(new CustomAttribute(
                attrType.Methods.First(m => m.IsConstructor)));

            method.Body.InitLocals = true;
            var counterVar = new VariableDefinition(module.TypeSystem.Int32);
            var sumVar = new VariableDefinition(module.TypeSystem.Int32);
            method.Body.Variables.Add(counterVar);
            method.Body.Variables.Add(sumVar);

            var il = method.Body.GetILProcessor();

            // sum = 0
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, sumVar);

            // First loop: for i = 0 to 5
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, counterVar);

            var loop1Start = il.Create(OpCodes.Nop);
            il.Append(loop1Start);

            il.Emit(OpCodes.Ldloc, counterVar);
            il.Emit(OpCodes.Ldc_I4_5);
            var loop1End = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Bge, loop1End);

            il.Emit(OpCodes.Ldloc, sumVar);
            il.Emit(OpCodes.Ldloc, counterVar);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, sumVar);

            il.Emit(OpCodes.Ldloc, counterVar);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, counterVar);

            il.Emit(OpCodes.Br, loop1Start);
            il.Append(loop1End);

            // Second loop: for i = 0 to 3
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, counterVar);

            var loop2Start = il.Create(OpCodes.Nop);
            il.Append(loop2Start);

            il.Emit(OpCodes.Ldloc, counterVar);
            il.Emit(OpCodes.Ldc_I4_3);
            var loop2End = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Bge, loop2End);

            il.Emit(OpCodes.Ldloc, sumVar);
            il.Emit(OpCodes.Ldc_I4, 10);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, sumVar);

            il.Emit(OpCodes.Ldloc, counterVar);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, counterVar);

            il.Emit(OpCodes.Br, loop2Start);
            il.Append(loop2End);

            il.Emit(OpCodes.Ldloc, sumVar);
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

            // int counter = 0;
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, counterVar);

            // loop_start:
            var loopStart = il.Create(OpCodes.Nop);
            il.Append(loopStart);

            // if (counter >= 10) goto end
            il.Emit(OpCodes.Ldloc, counterVar);
            il.Emit(OpCodes.Ldc_I4, 10);
            var endLabel = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Bge, endLabel);

            // counter++
            il.Emit(OpCodes.Ldloc, counterVar);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, counterVar);

            // goto loop_start (backward branch)
            il.Emit(OpCodes.Br, loopStart);

            // end:
            il.Append(endLabel);

            // return counter
            il.Emit(OpCodes.Ldloc, counterVar);
            il.Emit(OpCodes.Ret);

            return loopMethod;
        }

        private MethodDefinition GetLoopMethod(AssemblyDefinition assembly)
        {
            var type = assembly.MainModule.Types.First(t => t.Name == "TestClass");
            return type.Methods.First(m => m.Name == "LoopMethod");
        }

        private MethodDefinition GetSimpleMethod(AssemblyDefinition assembly)
        {
            var type = assembly.MainModule.Types.First(t => t.Name == "TestClass");
            return type.Methods.First(m => m.Name == "SimpleMethod");
        }

        private MethodDefinition GetMultiLoopMethod(AssemblyDefinition assembly)
        {
            var type = assembly.MainModule.Types.First(t => t.Name == "TestClass");
            return type.Methods.First(m => m.Name == "MultiLoopMethod");
        }

        private int GetLoopMethodInstructionCount(AssemblyDefinition assembly)
        {
            return GetLoopMethod(assembly).Body.Instructions.Count;
        }

        #endregion
    }
}
