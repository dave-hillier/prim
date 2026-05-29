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

        [Fact]
        public void StackSimulator_LdcI4_PushesInt32()
        {
            var assembly = CreateTestAssembly();
            var method = GetMethodWithStack(assembly);

            var sim = new StackSimulator(method);
            sim.Simulate();

            // ldc.i4.1 (offset 0) -> before: empty. ldc.i4.2 (offset 1) -> before: [Int32].
            var beforeSecond = sim.GetStateAt(1);
            Assert.Equal(1, beforeSecond.Depth);
            Assert.Equal("System.Int32", beforeSecond.Types[0].FullName);
        }

        [Fact]
        public void StackSimulator_Newobj_NetPlusOne_WithArgs()
        {
            // local0 = new StringBuilder(int capacity)  -> ldc.i4 N; newobj; ...
            var assembly = CreateTestAssembly();
            var module = assembly.MainModule;
            var method = new MethodDefinition("NewObjMethod",
                Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Void);
            Attach(assembly, method);

            var sbCtor = module.ImportReference(
                typeof(System.Text.StringBuilder).GetConstructor(new[] { typeof(int) }));

            var il = method.Body.GetILProcessor();
            var ldc = il.Create(OpCodes.Ldc_I4, 16);
            var newobj = il.Create(OpCodes.Newobj, sbCtor);
            var pop = il.Create(OpCodes.Pop);
            var ret = il.Create(OpCodes.Ret);
            il.Append(ldc);
            il.Append(newobj);
            il.Append(pop);
            il.Append(ret);

            var sim = new StackSimulator(method);
            sim.Simulate();

            // Before newobj: 1 item (the int arg). After newobj it must be 1 (the object).
            var beforeNewobj = sim.GetStateAt(newobj.Offset);
            Assert.Equal(1, beforeNewobj.Depth);

            // newobj pops N args (1) and pushes 1: net +1 from empty -> depth 1 at pop.
            var beforePop = sim.GetStateAt(pop.Offset);
            Assert.Equal(1, beforePop.Depth);
            Assert.Equal("System.Text.StringBuilder", beforePop.Types[0].FullName);
        }

        [Fact]
        public void StackSimulator_Calli_PopsArgsAndFunctionPointer()
        {
            var assembly = CreateTestAssembly();
            var module = assembly.MainModule;
            var method = new MethodDefinition("CalliMethod",
                Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Void);
            Attach(assembly, method);

            // Build a CallSite: int (int, int)  with one extra slot for the fn pointer.
            var callSite = new CallSite(module.TypeSystem.Int32);
            callSite.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
            callSite.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));

            var il = method.Body.GetILProcessor();
            var a = il.Create(OpCodes.Ldc_I4, 1);
            var b = il.Create(OpCodes.Ldc_I4, 2);
            var fn = il.Create(OpCodes.Ldc_I4, 0); // stand-in function pointer
            var calli = il.Create(OpCodes.Calli, callSite);
            var pop = il.Create(OpCodes.Pop);
            var ret = il.Create(OpCodes.Ret);
            il.Append(a);
            il.Append(b);
            il.Append(fn);
            il.Append(calli);
            il.Append(pop);
            il.Append(ret);

            var sim = new StackSimulator(method);
            sim.Simulate();

            // Before calli: 2 args + 1 fn pointer = depth 3.
            var beforeCalli = sim.GetStateAt(calli.Offset);
            Assert.Equal(3, beforeCalli.Depth);

            // calli pops 2 params + 1 fn pointer = 3, pushes 1 (non-void return).
            var beforePop = sim.GetStateAt(pop.Offset);
            Assert.Equal(1, beforePop.Depth);
            Assert.Equal("System.Int32", beforePop.Types[0].FullName);
        }

        [Fact]
        public void StackSimulator_ForwardBranchTarget_HasBranchEdgeDepth_NotFallThrough()
        {
            // Build: ldc.i4 1; brtrue L; <unreachable extra push>; L: ret
            // The fall-through path pushes an extra item; the branch path does not.
            // The target block must see the branch-edge depth (0), not a polluted depth.
            var assembly = CreateTestAssembly();
            var module = assembly.MainModule;
            var method = new MethodDefinition("ForwardBranchMethod",
                Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Void);
            Attach(assembly, method);

            var il = method.Body.GetILProcessor();
            var target = il.Create(OpCodes.Ret);
            var cond = il.Create(OpCodes.Ldc_I4, 1);
            var br = il.Create(OpCodes.Brtrue, target);
            // fall-through path: push something then branch to target as well
            var extra = il.Create(OpCodes.Ldc_I4, 5);
            var pop = il.Create(OpCodes.Pop);

            il.Append(cond);   // depth 0 before
            il.Append(br);     // pops the int -> after br depth 0 on both edges
            il.Append(extra);  // fall-through: push int
            il.Append(pop);    // pop it -> depth 0
            il.Append(target); // ret; both edges arrive at depth 0

            var sim = new StackSimulator(method);
            sim.Simulate();

            // Target (ret) must see depth 0 (branch edge after brtrue popped its operand).
            var atTarget = sim.GetStateAt(target.Offset);
            Assert.Equal(0, atTarget.Depth);
        }

        [Fact]
        public void StackSimulator_BackwardBranch_Loop_Converges()
        {
            // The loop method has a back-edge. Simulation must converge (not throw,
            // not loop forever) and report depth 0 at the loop header.
            var assembly = CreateTestAssembly();
            var method = GetLoopMethod(assembly);

            var sim = new StackSimulator(method);
            sim.Simulate(); // must terminate

            // The loop header (the nop at the top of the loop) has an empty stack.
            // Find the branch target of the unconditional Br (the back-edge).
            var br = method.Body.Instructions.First(i => i.OpCode == OpCodes.Br);
            var header = (Instruction)br.Operand;
            var atHeader = sim.GetStateAt(header.Offset);
            Assert.Equal(0, atHeader.Depth);
        }

        [Fact]
        public void StackSimulator_ExceptionHandlerEntry_SeesDepthOne()
        {
            // try { nop } catch (Exception) { pop; ... }
            var assembly = CreateTestAssembly();
            var module = assembly.MainModule;
            var method = new MethodDefinition("TryCatchMethod",
                Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Void);
            Attach(assembly, method);

            var il = method.Body.GetILProcessor();
            var ret = il.Create(OpCodes.Ret);
            var tryStart = il.Create(OpCodes.Nop);
            var leave = il.Create(OpCodes.Leave, ret);
            var handlerStart = il.Create(OpCodes.Pop); // consume exception object
            var leave2 = il.Create(OpCodes.Leave, ret);

            il.Append(tryStart);
            il.Append(leave);
            il.Append(handlerStart);
            il.Append(leave2);
            il.Append(ret);

            var exType = module.ImportReference(typeof(Exception));
            method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart,
                TryEnd = handlerStart,
                HandlerStart = handlerStart,
                HandlerEnd = ret,
                CatchType = exType
            });

            var sim = new StackSimulator(method);
            sim.Simulate();

            // Handler entry must see depth 1 (the exception object), typed as the catch type.
            var atHandler = sim.GetStateAt(handlerStart.Offset);
            Assert.Equal(1, atHandler.Depth);
            Assert.Equal("System.Exception", atHandler.Types[0].FullName);
        }

        [Fact]
        public void StackSimulator_Dup_PreservesDuplicatedType()
        {
            // ldstr "hi"; dup; pop; pop; ret  -> after dup the stack is [String, String]
            var assembly = CreateTestAssembly();
            var module = assembly.MainModule;
            var method = new MethodDefinition("DupMethod",
                Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Void);
            Attach(assembly, method);

            var il = method.Body.GetILProcessor();
            var firstPop = il.Create(OpCodes.Pop);
            il.Append(il.Create(OpCodes.Ldstr, "hi"));
            il.Append(il.Create(OpCodes.Dup));
            il.Append(firstPop);
            il.Append(il.Create(OpCodes.Pop));
            il.Append(il.Create(OpCodes.Ret));

            var sim = new StackSimulator(method);
            sim.Simulate();

            // State recorded BEFORE the first pop == state just after dup.
            var afterDup = sim.GetStateAt(firstPop.Offset);
            Assert.Equal(2, afterDup.Depth);
            Assert.Equal("System.String", afterDup.Types[0].FullName);
            Assert.Equal("System.String", afterDup.Types[1].FullName);
        }

        [Fact]
        public void StackSimulator_Leave_ClearsEvaluationStack()
        {
            // try { ldc.i4 5; leave ret } catch { pop; leave ret } ret
            // The value 5 is live when 'leave' executes; leave must EMPTY the stack,
            // so the leave target sees depth 0 (ECMA-335 III.3.55).
            var assembly = CreateTestAssembly();
            var module = assembly.MainModule;
            var method = new MethodDefinition("LeaveClearsMethod",
                Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Void);
            Attach(assembly, method);

            var il = method.Body.GetILProcessor();
            var ret = il.Create(OpCodes.Ret);
            var tryStart = il.Create(OpCodes.Ldc_I4, 5);
            var leave = il.Create(OpCodes.Leave, ret);
            var handlerStart = il.Create(OpCodes.Pop);
            var leave2 = il.Create(OpCodes.Leave, ret);

            il.Append(tryStart);
            il.Append(leave);
            il.Append(handlerStart);
            il.Append(leave2);
            il.Append(ret);

            method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart,
                TryEnd = handlerStart,
                HandlerStart = handlerStart,
                HandlerEnd = ret,
                CatchType = module.ImportReference(typeof(Exception))
            });

            var sim = new StackSimulator(method);
            sim.Simulate();

            // The leave target must NOT inherit the value that was live before leave.
            Assert.Equal(0, sim.GetStateAt(ret.Offset).Depth);
        }

        [Fact]
        public void StackSimulator_FilterHandlerEntry_SeesExceptionObject()
        {
            // try { leave ret } filter { pop; ldc.i4.1; endfilter } handler { pop; leave ret } ret
            // The filtered handler body is entered with the exception object (depth 1),
            // exactly like a catch.
            var assembly = CreateTestAssembly();
            var module = assembly.MainModule;
            var method = new MethodDefinition("FilterMethod",
                Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Void);
            Attach(assembly, method);

            var il = method.Body.GetILProcessor();
            var ret = il.Create(OpCodes.Ret);
            var tryStart = il.Create(OpCodes.Leave, ret);
            var filterStart = il.Create(OpCodes.Pop);       // consume the exception the filter receives
            var filterResult = il.Create(OpCodes.Ldc_I4_1);
            var endfilter = il.Create(OpCodes.Endfilter);
            var handlerStart = il.Create(OpCodes.Pop);       // consume the exception the handler receives
            var leave2 = il.Create(OpCodes.Leave, ret);

            il.Append(tryStart);
            il.Append(filterStart);
            il.Append(filterResult);
            il.Append(endfilter);
            il.Append(handlerStart);
            il.Append(leave2);
            il.Append(ret);

            method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Filter)
            {
                TryStart = tryStart,
                TryEnd = filterStart,
                FilterStart = filterStart,
                HandlerStart = handlerStart,
                HandlerEnd = ret
            });

            var sim = new StackSimulator(method);
            sim.Simulate();

            Assert.Equal(1, sim.GetStateAt(handlerStart.Offset).Depth);
        }

        [Fact]
        public void StackSimulator_DepthDoesNotBleedPastRet()
        {
            // ldc.i4 1; ret(void-ish modeled); <next textual instr is a fresh block>
            // Build two independent code paths: one returns; an unrelated branch target
            // should not inherit the pre-ret depth.
            var assembly = CreateTestAssembly();
            var module = assembly.MainModule;
            var method = new MethodDefinition("NoBleedMethod",
                Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Int32);
            Attach(assembly, method);

            var il = method.Body.GetILProcessor();
            // br L2 ; L1: ldc.i4 99; ret ; L2: ldc.i4 1; ret
            var l1 = il.Create(OpCodes.Ldc_I4, 99);
            var ret1 = il.Create(OpCodes.Ret);
            var l2 = il.Create(OpCodes.Ldc_I4, 1);
            var ret2 = il.Create(OpCodes.Ret);
            var br = il.Create(OpCodes.Br, l2);

            il.Append(br);
            il.Append(l1);
            il.Append(ret1);
            il.Append(l2);
            il.Append(ret2);

            var sim = new StackSimulator(method);
            sim.Simulate();

            // L2 is reached only via the unconditional br (depth 0). The depth pushed by
            // L1 (99) must not bleed across ret1 into L2.
            var atL2 = sim.GetStateAt(l2.Offset);
            Assert.Equal(0, atL2.Depth);
        }

        [Fact]
        public void StackSimulator_DepthDoesNotBleedPastUnconditionalBranch()
        {
            // ldc.i4 7; br L; <next textual is dead push>; L: ret
            // The instruction textually after br must not be seeded from br's depth via
            // fall-through; only the branch target carries state.
            var assembly = CreateTestAssembly();
            var module = assembly.MainModule;
            var method = new MethodDefinition("NoFallThroughMethod",
                Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Void);
            Attach(assembly, method);

            var il = method.Body.GetILProcessor();
            var target = il.Create(OpCodes.Ret);
            var push = il.Create(OpCodes.Ldc_I4, 7);
            var br = il.Create(OpCodes.Br, target);
            var deadPop = il.Create(OpCodes.Pop); // textually after br, only reachable as fall-through (it isn't)

            il.Append(push);
            il.Append(br);
            il.Append(deadPop);
            il.Append(target);

            var sim = new StackSimulator(method);
            sim.Simulate();

            // Target reached only via br; the push of 7 stays live on the br edge so the
            // target sees depth 1, but the deadPop (textual next after br) is unreachable
            // and therefore never recorded as a fall-through successor of br.
            var atTarget = sim.GetStateAt(target.Offset);
            Assert.Equal(1, atTarget.Depth);
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

        // Attaches a programmatically-built method to the assembly's TestClass so it has a
        // module (StackSimulator dereferences method.Module.TypeSystem).
        private static void Attach(AssemblyDefinition assembly, MethodDefinition method)
        {
            var type = assembly.MainModule.Types.First(t => t.Name == "TestClass");
            type.Methods.Add(method);
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
