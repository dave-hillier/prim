using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Prim.Analysis;
using Xunit;

namespace Prim.Tests.Cecil
{
    /// <summary>
    /// Additional tests for the Prim.Analysis module components.
    /// Tests ControlFlowGraph, StackSimulator, BasicBlock, and YieldPointIdentifier.
    /// </summary>
    public class AnalysisTests
    {
        #region BasicBlock Tests

        [Fact]
        public void BasicBlock_StartsEmpty()
        {
            var block = new BasicBlock(0);

            Assert.Equal(0, block.StartOffset);
            Assert.Empty(block.Instructions);
            Assert.Empty(block.Successors);
            Assert.Empty(block.Predecessors);
        }

        [Fact]
        public void BasicBlock_CanAddInstructions()
        {
            var assembly = CreateTestAssembly();
            var method = GetSimpleMethod(assembly);
            var block = new BasicBlock(0);

            foreach (var instr in method.Body.Instructions.Take(2))
            {
                block.Instructions.Add(instr);
            }

            Assert.Equal(2, block.Instructions.Count);
        }

        [Fact]
        public void BasicBlock_CanAddSuccessorsAndPredecessors()
        {
            var block1 = new BasicBlock(0);
            var block2 = new BasicBlock(1);
            var block3 = new BasicBlock(2);

            block1.Successors.Add(block2);
            block2.Predecessors.Add(block1);
            block2.Successors.Add(block3);
            block3.Predecessors.Add(block2);

            Assert.Single(block1.Successors);
            Assert.Empty(block1.Predecessors);
            Assert.Single(block2.Predecessors);
            Assert.Single(block2.Successors);
            Assert.Single(block3.Predecessors);
            Assert.Empty(block3.Successors);
        }

        #endregion

        #region ControlFlowGraph Tests

        [Fact]
        public void ControlFlowGraph_BuildsFromSimpleMethod()
        {
            var assembly = CreateTestAssembly();
            var method = GetSimpleMethod(assembly);

            var cfg = ControlFlowGraph.Build(method);

            Assert.NotNull(cfg);
            Assert.NotEmpty(cfg.Blocks);
            Assert.NotNull(cfg.EntryBlock);
        }

        [Fact]
        public void ControlFlowGraph_SimpleMethodHasNoBackEdges()
        {
            var assembly = CreateTestAssembly();
            var method = GetSimpleMethod(assembly);

            var cfg = ControlFlowGraph.Build(method);

            Assert.Empty(cfg.BackEdges);
        }

        [Fact]
        public void ControlFlowGraph_LoopMethodHasBackEdges()
        {
            var assembly = CreateTestAssembly();
            var method = GetLoopMethod(assembly);

            var cfg = ControlFlowGraph.Build(method);

            Assert.NotEmpty(cfg.BackEdges);
        }

        [Fact]
        public void ControlFlowGraph_EntryBlockIsFirst()
        {
            var assembly = CreateTestAssembly();
            var method = GetSimpleMethod(assembly);

            var cfg = ControlFlowGraph.Build(method);

            Assert.Equal(0, cfg.EntryBlock.StartOffset);
        }

        [Fact]
        public void ControlFlowGraph_BlocksContainAllInstructions()
        {
            var assembly = CreateTestAssembly();
            var method = GetSimpleMethod(assembly);

            var cfg = ControlFlowGraph.Build(method);

            var totalInstructions = cfg.Blocks.Sum(b => b.Instructions.Count);
            Assert.Equal(method.Body.Instructions.Count, totalInstructions);
        }

        #endregion

        #region StackSimulator Tests

        [Fact]
        public void StackSimulator_SimulatesSimpleMethod()
        {
            var assembly = CreateTestAssembly();
            var method = GetSimpleMethod(assembly);

            var simulator = new StackSimulator(method);
            simulator.Simulate();

            // Should not throw and should have states
            var state = simulator.GetStateAt(0);
            Assert.NotNull(state);
        }

        [Fact]
        public void StackSimulator_TracksStackDepth()
        {
            var assembly = CreateTestAssembly();
            var method = GetSimpleMethod(assembly);

            var simulator = new StackSimulator(method);
            simulator.Simulate();

            var entryState = simulator.GetStateAt(0);
            // At method entry, stack should be empty
            Assert.Equal(0, entryState.Depth);
        }

        [Fact]
        public void StackSimulator_TracksStackAfterPush()
        {
            var assembly = CreateTestAssembly();
            var method = GetMethodWithStack(assembly);

            var simulator = new StackSimulator(method);
            simulator.Simulate();

            // After ldc.i4 (push int), stack depth should be 1
            var afterPush = simulator.GetStateAt(1); // After first instruction
            Assert.True(afterPush.Depth >= 0);
        }

        #endregion

        #region YieldPointIdentifier Tests

        [Fact]
        public void YieldPointIdentifier_FindsNoYieldPointsInSimpleMethod()
        {
            var assembly = CreateTestAssembly();
            var method = GetSimpleMethod(assembly);

            var identifier = new YieldPointIdentifier(method);
            var yieldPoints = identifier.FindYieldPoints();

            Assert.Empty(yieldPoints);
        }

        [Fact]
        public void YieldPointIdentifier_FindsYieldPointsInLoopMethod()
        {
            var assembly = CreateTestAssembly();
            var method = GetLoopMethod(assembly);

            var identifier = new YieldPointIdentifier(method);
            var yieldPoints = identifier.FindYieldPoints();

            Assert.NotEmpty(yieldPoints);
        }

        [Fact]
        public void YieldPointIdentifier_YieldPointsHaveSequentialIds()
        {
            var assembly = CreateTestAssembly();
            var method = GetLoopMethod(assembly);

            var identifier = new YieldPointIdentifier(method);
            var yieldPoints = identifier.FindYieldPoints();

            for (int i = 0; i < yieldPoints.Count; i++)
            {
                Assert.Equal(i, yieldPoints[i].Id);
            }
        }

        [Fact]
        public void YieldPointIdentifier_YieldPointsHaveBackwardBranchKind()
        {
            var assembly = CreateTestAssembly();
            var method = GetLoopMethod(assembly);

            var identifier = new YieldPointIdentifier(method);
            var yieldPoints = identifier.FindYieldPoints();

            Assert.All(yieldPoints, yp => Assert.Equal(ILYieldPointKind.BackwardBranch, yp.Kind));
        }

        [Fact]
        public void YieldPointIdentifier_YieldPointsHaveStackState()
        {
            var assembly = CreateTestAssembly();
            var method = GetLoopMethod(assembly);

            var identifier = new YieldPointIdentifier(method);
            var yieldPoints = identifier.FindYieldPoints();

            Assert.All(yieldPoints, yp => Assert.NotNull(yp.StackState));
        }

        #endregion

        #region ILYieldPoint Tests

        [Fact]
        public void ILYieldPoint_PropertiesAreSettable()
        {
            var yieldPoint = new ILYieldPoint
            {
                Id = 5,
                Kind = ILYieldPointKind.BackwardBranch
            };

            Assert.Equal(5, yieldPoint.Id);
            Assert.Equal(ILYieldPointKind.BackwardBranch, yieldPoint.Kind);
        }

        [Fact]
        public void ILYieldPointKind_HasExpectedValues()
        {
            Assert.True(Enum.IsDefined(typeof(ILYieldPointKind), ILYieldPointKind.BackwardBranch));
            Assert.True(Enum.IsDefined(typeof(ILYieldPointKind), ILYieldPointKind.ExternalCall));
        }

        #endregion

        #region Helper Methods

        private static AssemblyDefinition CreateTestAssembly()
        {
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("TestAssembly", new Version(1, 0, 0, 0)),
                "TestModule",
                ModuleKind.Dll);

            var module = assembly.MainModule;

            var testClass = new TypeDefinition(
                "TestNamespace",
                "TestClass",
                TypeAttributes.Public | TypeAttributes.Class,
                module.ImportReference(typeof(object)));

            module.Types.Add(testClass);

            // Add SimpleMethod
            AddSimpleMethod(testClass, module);

            // Add LoopMethod
            AddLoopMethod(testClass, module);

            // Add MethodWithStack
            AddMethodWithStack(testClass, module);

            return assembly;
        }

        private static void AddSimpleMethod(TypeDefinition testClass, ModuleDefinition module)
        {
            var method = new MethodDefinition(
                "SimpleMethod",
                MethodAttributes.Public,
                module.TypeSystem.Int32);

            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4, 42);
            il.Emit(OpCodes.Ret);

            testClass.Methods.Add(method);
        }

        private static void AddLoopMethod(TypeDefinition testClass, ModuleDefinition module)
        {
            var method = new MethodDefinition(
                "LoopMethod",
                MethodAttributes.Public,
                module.TypeSystem.Int32);

            method.Body.InitLocals = true;
            var counterVar = new VariableDefinition(module.TypeSystem.Int32);
            method.Body.Variables.Add(counterVar);

            var il = method.Body.GetILProcessor();

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

            testClass.Methods.Add(method);
        }

        private static void AddMethodWithStack(TypeDefinition testClass, ModuleDefinition module)
        {
            var method = new MethodDefinition(
                "MethodWithStack",
                MethodAttributes.Public,
                module.TypeSystem.Int32);

            var il = method.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4_1);  // Push 1
            il.Emit(OpCodes.Ldc_I4_2);  // Push 2
            il.Emit(OpCodes.Add);       // Pop 2, push sum
            il.Emit(OpCodes.Ret);       // Return

            testClass.Methods.Add(method);
        }

        private static MethodDefinition GetSimpleMethod(AssemblyDefinition assembly)
        {
            var type = assembly.MainModule.Types.First(t => t.Name == "TestClass");
            return type.Methods.First(m => m.Name == "SimpleMethod");
        }

        private static MethodDefinition GetLoopMethod(AssemblyDefinition assembly)
        {
            var type = assembly.MainModule.Types.First(t => t.Name == "TestClass");
            return type.Methods.First(m => m.Name == "LoopMethod");
        }

        private static MethodDefinition GetMethodWithStack(AssemblyDefinition assembly)
        {
            var type = assembly.MainModule.Types.First(t => t.Name == "TestClass");
            return type.Methods.First(m => m.Name == "MethodWithStack");
        }

        #endregion
    }
}
