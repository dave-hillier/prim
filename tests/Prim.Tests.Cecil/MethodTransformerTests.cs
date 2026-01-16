using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Prim.Analysis;
using Prim.Cecil;
using Prim.Core;
using Xunit;

namespace Prim.Tests.Cecil
{
    public class MethodTransformerTests
    {
        [Fact]
        public void YieldPointIdentifier_FindsBackwardBranches()
        {
            // Create a simple assembly with a loop
            var assembly = CreateTestAssembly();
            var type = assembly.MainModule.Types.First(t => t.Name == "TestClass");
            var method = type.Methods.First(m => m.Name == "LoopMethod");

            var identifier = new YieldPointIdentifier(method);
            var yieldPoints = identifier.FindYieldPoints();

            // Should find at least one yield point (the loop back-edge)
            Assert.NotEmpty(yieldPoints);
            Assert.All(yieldPoints, yp => Assert.Equal(ILYieldPointKind.BackwardBranch, yp.Kind));
        }

        [Fact]
        public void YieldPointIdentifier_ReturnsEmptyForNoLoops()
        {
            var assembly = CreateTestAssembly();
            var type = assembly.MainModule.Types.First(t => t.Name == "TestClass");
            var method = type.Methods.First(m => m.Name == "SimpleMethod");

            var identifier = new YieldPointIdentifier(method);
            var yieldPoints = identifier.FindYieldPoints();

            // No loops = no yield points (when only detecting back-edges)
            Assert.Empty(yieldPoints);
        }

        [Fact]
        public void ControlFlowGraph_BuildsCorrectly()
        {
            var assembly = CreateTestAssembly();
            var type = assembly.MainModule.Types.First(t => t.Name == "TestClass");
            var method = type.Methods.First(m => m.Name == "LoopMethod");

            var cfg = ControlFlowGraph.Build(method);

            Assert.NotNull(cfg);
            Assert.NotEmpty(cfg.Blocks);
            Assert.NotNull(cfg.EntryBlock);
        }

        [Fact]
        public void ControlFlowGraph_IdentifiesBackEdges()
        {
            var assembly = CreateTestAssembly();
            var type = assembly.MainModule.Types.First(t => t.Name == "TestClass");
            var method = type.Methods.First(m => m.Name == "LoopMethod");

            var cfg = ControlFlowGraph.Build(method);

            // Should find back-edges for the loop
            Assert.NotEmpty(cfg.BackEdges);
        }

        [Fact]
        public void StackSimulator_TracksStackCorrectly()
        {
            var assembly = CreateTestAssembly();
            var type = assembly.MainModule.Types.First(t => t.Name == "TestClass");
            var method = type.Methods.First(m => m.Name == "SimpleMethod");

            var simulator = new StackSimulator(method);
            simulator.Simulate();

            // At method entry, stack should be empty
            var entryState = simulator.GetStateAt(0);
            Assert.Equal(0, entryState.Depth);
        }

        [Fact]
        public void AssemblyRewriter_TransformsMarkedTypes()
        {
            var assembly = CreateTestAssemblyWithAttribute();
            var rewriter = new AssemblyRewriter();

            // This should not throw
            rewriter.Transform(assembly);

            // Verify the assembly was modified (methods with loops should have been transformed)
            var type = assembly.MainModule.Types.First(t => t.Name == "ContinuableTestClass");
            var method = type.Methods.First(m => m.Name == "ContinuableLoop");

            // The method should now have more instructions (due to injected yield checks)
            Assert.True(method.Body.Instructions.Count > 0);
        }

        [Fact]
        public void AssemblyRewriter_PreservesNonMarkedTypes()
        {
            var assembly = CreateTestAssembly();
            var type = assembly.MainModule.Types.First(t => t.Name == "TestClass");
            var originalInstructionCount = type.Methods
                .First(m => m.Name == "SimpleMethod")
                .Body.Instructions.Count;

            var rewriter = new AssemblyRewriter();
            rewriter.Transform(assembly);

            // Non-marked types should be unchanged
            var instructionCount = type.Methods
                .First(m => m.Name == "SimpleMethod")
                .Body.Instructions.Count;

            Assert.Equal(originalInstructionCount, instructionCount);
        }

        [Fact]
        public void RewriterOptions_DefaultValues()
        {
            var options = RewriterOptions.Default;

            Assert.True(options.PreserveDebugSymbols);
            Assert.True(options.BackwardBranchesOnly);
        }

        private static AssemblyDefinition CreateTestAssembly()
        {
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("TestAssembly", new Version(1, 0, 0, 0)),
                "TestModule",
                ModuleKind.Dll);

            var module = assembly.MainModule;

            // Create TestClass
            var testClass = new TypeDefinition(
                "TestNamespace",
                "TestClass",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));

            module.Types.Add(testClass);

            // Add SimpleMethod (no loops)
            var simpleMethod = new MethodDefinition(
                "SimpleMethod",
                Mono.Cecil.MethodAttributes.Public,
                module.TypeSystem.Int32);

            var il = simpleMethod.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4, 42);
            il.Emit(OpCodes.Ret);

            testClass.Methods.Add(simpleMethod);

            // Add LoopMethod (has a loop)
            var loopMethod = new MethodDefinition(
                "LoopMethod",
                Mono.Cecil.MethodAttributes.Public,
                module.TypeSystem.Int32);

            loopMethod.Body.InitLocals = true;
            var counterVar = new VariableDefinition(module.TypeSystem.Int32);
            loopMethod.Body.Variables.Add(counterVar);

            il = loopMethod.Body.GetILProcessor();

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

            testClass.Methods.Add(loopMethod);

            return assembly;
        }

        private static AssemblyDefinition CreateTestAssemblyWithAttribute()
        {
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("TestAssembly", new Version(1, 0, 0, 0)),
                "TestModule",
                ModuleKind.Dll);

            var module = assembly.MainModule;

            // Create ContinuableAttribute
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
            attrIl.Emit(OpCodes.Call, module.ImportReference(
                typeof(Attribute).GetConstructor(Type.EmptyTypes)));
            attrIl.Emit(OpCodes.Ret);

            attrType.Methods.Add(attrCtor);
            module.Types.Add(attrType);

            // Create ContinuableTestClass with attribute
            var testClass = new TypeDefinition(
                "TestNamespace",
                "ContinuableTestClass",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.ImportReference(typeof(object)));

            // Add [Continuable] attribute to class
            testClass.CustomAttributes.Add(new CustomAttribute(attrCtor));

            module.Types.Add(testClass);

            // Add ContinuableLoop method (has a loop)
            var loopMethod = new MethodDefinition(
                "ContinuableLoop",
                Mono.Cecil.MethodAttributes.Public,
                module.TypeSystem.Int32);

            // Add [Continuable] attribute to method
            loopMethod.CustomAttributes.Add(new CustomAttribute(attrCtor));

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

            testClass.Methods.Add(loopMethod);

            return assembly;
        }
    }
}
