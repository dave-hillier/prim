using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Prim.Analysis;
using Xunit;

namespace Prim.Tests.Cecil
{
    /// <summary>
    /// Tests that demonstrate existing bugs in Prim.Analysis.
    /// These tests are expected to FAIL, proving the bugs exist.
    /// Do NOT fix the source code to make these pass.
    /// </summary>
    public class BugProof_AnalysisTests
    {
        private static (AssemblyDefinition, MethodDefinition) CreateTestMethod(bool hasReturnValue = false)
        {
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("TestAssembly", new Version(1, 0)),
                "TestModule", ModuleKind.Dll);
            var module = assembly.MainModule;

            var type = new TypeDefinition("Test", "TestClass",
                TypeAttributes.Public | TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(type);

            var returnType = hasReturnValue ? module.TypeSystem.Int32 : module.TypeSystem.Void;
            var method = new MethodDefinition("TestMethod",
                MethodAttributes.Public | MethodAttributes.Static,
                returnType);
            type.Methods.Add(method);
            method.Body = new MethodBody(method);

            return (assembly, method);
        }

        /// <summary>
        /// BUG: ControlFlowGraph.IsBranch (lines 242-259) does not include Throw.
        /// When a method contains "throw; nop; ret", the throw should terminate its
        /// basic block and nop should start a new block. Because Throw is missing from
        /// IsBranch, FindLeaders never marks the instruction after throw as a leader,
        /// so all instructions end up in a single block.
        /// </summary>
        [Fact]
        public void Bug_CFG_IsBranch_Missing_Throw_As_Terminator()
        {
            var (assembly, method) = CreateTestMethod();
            var il = method.Body.GetILProcessor();

            var exCtor = assembly.MainModule.ImportReference(
                typeof(Exception).GetConstructor(Type.EmptyTypes));

            il.Append(il.Create(OpCodes.Newobj, exCtor));
            il.Append(il.Create(OpCodes.Throw));
            il.Append(il.Create(OpCodes.Nop)); // dead code after throw
            il.Append(il.Create(OpCodes.Ret));

            var cfg = ControlFlowGraph.Build(method);

            // The throw should terminate a block, and nop should start a new block.
            // BUG: throw is not in IsBranch, so no split happens after it.
            // Expected: at least 2 blocks (before+including throw, after throw).
            // Actual: only 1 block containing all instructions.
            Assert.True(cfg.Blocks.Count >= 2,
                $"Expected at least 2 blocks (throw should terminate a block), got {cfg.Blocks.Count}");
        }

        /// <summary>
        /// BUG: StackSimulator.GetVarPop (lines 148-161) returns 0 for the ret
        /// instruction. The ret opcode has Varpop stack behavior and should pop 1 item
        /// (the return value) for non-void methods, but GetVarPop only handles
        /// MethodReference operands and falls through to "return 0" for ret.
        ///
        /// This is demonstrated by observing that the simulator's tracked stack depth
        /// after processing a ret instruction in a non-void method does not decrease.
        /// Dead code after ret will see depth 1 instead of 0.
        /// </summary>
        [Fact]
        public void Bug_StackSimulator_Ret_Pops_Zero_For_NonVoid()
        {
            var (assembly, method) = CreateTestMethod(hasReturnValue: true);
            var il = method.Body.GetILProcessor();

            il.Append(il.Create(OpCodes.Ldc_I4, 42)); // push int, depth 0 -> 1
            il.Append(il.Create(OpCodes.Ret));          // should pop return value, depth 1 -> 0
            il.Append(il.Create(OpCodes.Nop));          // dead code after ret
            il.Append(il.Create(OpCodes.Ret));          // dead code

            var sim = new StackSimulator(method);
            sim.Simulate();

            // The simulator processes instructions linearly in a forward pass.
            // After ldc.i4 pushes 1 item (depth=1), ret should pop 1 (the return value).
            // So at the nop (dead code), the recorded stack depth should be 0.
            // BUG: GetVarPop returns 0 for ret, so depth stays at 1 after ret executes.
            var nopInstruction = method.Body.Instructions[2];
            var stateAtDeadCode = sim.GetStateAt(nopInstruction.Offset);

            Assert.Equal(0, stateAtDeadCode.Depth);
        }

        /// <summary>
        /// BUG: YieldPointIdentifier.IsExternalCall (lines 187-190) compares
        /// method.DeclaringType.Scope.Name with _method.Module.Assembly.Name.Name.
        /// For programmatically created assemblies, Scope.Name is the module name
        /// (e.g., "TestModule") while Assembly.Name.Name is the assembly name
        /// (e.g., "TestAssembly"). These are different strings, so calls within
        /// the same assembly are incorrectly classified as external.
        /// </summary>
        [Fact]
        public void Bug_YieldPointIdentifier_SameAssembly_Name_Mismatch()
        {
            var (assembly, method) = CreateTestMethod();
            var module = assembly.MainModule;
            var il = method.Body.GetILProcessor();

            // Create another method in the SAME assembly
            var type = module.Types.First(t => t.Name == "TestClass");
            var helperMethod = new MethodDefinition("Helper",
                MethodAttributes.Public | MethodAttributes.Static,
                module.TypeSystem.Void);
            type.Methods.Add(helperMethod);
            helperMethod.Body = new MethodBody(helperMethod);
            helperMethod.Body.GetILProcessor().Append(
                helperMethod.Body.GetILProcessor().Create(OpCodes.Ret));

            // In the main method: call Helper, then ret
            il.Append(il.Create(OpCodes.Call, helperMethod));
            il.Append(il.Create(OpCodes.Ret));

            // Now check what IsExternalCall would compare:
            // method.DeclaringType.Scope.Name -> module name
            // _method.Module.Assembly.Name.Name -> assembly name
            var calledAssemblyName = helperMethod.DeclaringType.Scope.Name;
            var thisAssemblyName = method.Module.Assembly.Name.Name;

            // BUG: These should match for same-assembly calls, but Scope.Name returns
            // the module name ("TestModule") while Assembly.Name.Name returns the
            // assembly name ("TestAssembly"). They will never be equal.
            Assert.Equal(thisAssemblyName, calledAssemblyName);
        }

        /// <summary>
        /// BUG: ControlFlowGraph.IdentifyBackEdges (lines 208-239) performs DFS
        /// only starting from EntryBlock. Exception handler blocks that are not
        /// reachable from the entry block via normal control flow edges are never
        /// visited, so loops within catch/finally handlers are not detected.
        ///
        /// In ConnectBlocks (line 191), the comment says "Don't add edge here -
        /// exception flow is implicit", so handler blocks have no incoming edges
        /// from the try block. This means the DFS from EntryBlock cannot reach them.
        /// </summary>
        [Fact]
        public void Bug_CFG_BackEdges_Not_Found_In_Exception_Handlers()
        {
            var (assembly, method) = CreateTestMethod();
            var module = assembly.MainModule;
            var il = method.Body.GetILProcessor();

            // Build: try { ret } catch(Exception) { pop; loop: nop; br loop; } handlerEnd: nop
            var tryStart = il.Create(OpCodes.Nop);
            var tryRet = il.Create(OpCodes.Ret);
            var handlerStart = il.Create(OpCodes.Pop); // pop exception from stack
            var loopBody = il.Create(OpCodes.Nop);
            var loopBranch = il.Create(OpCodes.Br, loopBody); // back-edge to loopBody
            var handlerEnd = il.Create(OpCodes.Nop);

            il.Append(tryStart);
            il.Append(tryRet);
            il.Append(handlerStart);
            il.Append(loopBody);
            il.Append(loopBranch);
            il.Append(handlerEnd);

            var exType = module.ImportReference(typeof(Exception));
            method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart,
                TryEnd = handlerStart,
                HandlerStart = handlerStart,
                HandlerEnd = handlerEnd,
                CatchType = exType
            });

            var cfg = ControlFlowGraph.Build(method);

            // The catch handler contains a loop (br -> loopBody), which creates a
            // back-edge. However, the DFS in IdentifyBackEdges only starts from
            // EntryBlock. The handler blocks are NOT connected via normal edges
            // from the entry (ConnectBlocks explicitly skips adding edges for
            // exception handlers). So the DFS never visits the handler blocks,
            // and the back-edge is never discovered.
            Assert.True(cfg.BackEdges.Count > 0,
                "Expected back-edge in catch handler loop, but DFS never reached handler blocks");
        }
    }
}
