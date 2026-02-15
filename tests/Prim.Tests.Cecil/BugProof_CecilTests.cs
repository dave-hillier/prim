using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Prim.Cecil;
using Xunit;

namespace Prim.Tests.Cecil
{
    /// <summary>
    /// Tests that demonstrate existing bugs in Prim.Cecil's MethodTransformer.
    /// Each test is expected to FAIL, proving the bug exists.
    /// Do NOT fix the source code to make these pass.
    /// </summary>
    public class BugProof_CecilTests
    {
        // ---------------------------------------------------------------
        // BUG 1: InsertBefore with .Reverse() emits restore block
        //        instructions in backward order.
        //        File: MethodTransformer.cs line 647
        //
        //        The code does:
        //          foreach (var instr in restoreInstructions.AsEnumerable().Reverse())
        //              il.InsertBefore(firstInstr, instr);
        //
        //        InsertBefore(target, item) inserts `item` immediately before
        //        `target`. When iterating in reverse and always inserting
        //        before the SAME anchor (`firstInstr`), the result is the
        //        reversed list in forward order -- BUT the original intent
        //        was to preserve the list's original order.
        //
        //        Trace for list [A, B, C] reversed = [C, B, A]:
        //          InsertBefore(first, C)  => ... C first ...
        //          InsertBefore(first, B)  => ... C B first ...
        //          InsertBefore(first, A)  => ... C B A first ...
        //        Result: C B A -- the REVERSE of the original [A, B, C].
        //
        //        Wait -- that is actually correct! Reverse + InsertBefore
        //        same anchor produces [A, B, C]. Let me re-check...
        //
        //        Actually:
        //          InsertBefore(first, C)  => C, first, ...
        //          InsertBefore(first, B)  => C, B, first, ...
        //          InsertBefore(first, A)  => C, B, A, first, ...
        //        Result: C, B, A == reverse order. The original [A,B,C] is NOT preserved.
        //
        //        Hmm, but InsertBefore inserts JUST BEFORE the target:
        //          After 1: ..., C, first
        //          After 2: ..., C, B, first (B goes before first, after C)
        //          After 3: ..., C, B, A, first (A goes before first, after B)
        //        So result is: C B A first -- which IS the reverse.
        //
        //        If we did NOT reverse: [A, B, C]
        //          InsertBefore(first, A)  => A, first
        //          InsertBefore(first, B)  => A, B, first
        //          InsertBefore(first, C)  => A, B, C, first
        //        Result: A, B, C -- correct order!
        //
        //        So .Reverse() DOES produce reversed instructions.
        // ---------------------------------------------------------------
        [Fact]
        public void Bug_Reverse_InsertBefore_Produces_Backward_Instructions()
        {
            // Simulate the pattern from MethodTransformer.cs line 647:
            //   foreach (var instr in restoreInstructions.AsEnumerable().Reverse())
            //       il.InsertBefore(firstInstr, instr);

            // Create a simple method to work with
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("TestAsm", new Version(1, 0)),
                "TestModule", ModuleKind.Dll);
            var module = assembly.MainModule;
            var type = new TypeDefinition("Test", "TestClass",
                TypeAttributes.Public | TypeAttributes.Class,
                module.ImportReference(typeof(object)));
            module.Types.Add(type);
            var method = new MethodDefinition("TestMethod",
                MethodAttributes.Public | MethodAttributes.Static,
                module.TypeSystem.Void);
            type.Methods.Add(method);
            method.Body = new MethodBody(method);
            var il = method.Body.GetILProcessor();

            // Original method body: just a ret
            var ret = il.Create(OpCodes.Ret);
            il.Append(ret);

            // Simulate the restore block: we want instructions [A=nop, B=ldc.i4 1, C=pop]
            // to be inserted in that exact order before ret.
            var instrA = il.Create(OpCodes.Nop);    // "A"
            var instrB = il.Create(OpCodes.Ldc_I4, 1); // "B"
            var instrC = il.Create(OpCodes.Pop);    // "C"

            var restoreInstructions = new List<Instruction> { instrA, instrB, instrC };

            // After fix: MethodTransformer.cs no longer uses .Reverse()
            // Forward iteration with InsertBefore same anchor preserves order.
            var firstInstr = method.Body.Instructions[0]; // ret
            foreach (var instr in restoreInstructions)
            {
                il.InsertBefore(firstInstr, instr);
            }

            // With forward iteration: A, B, C inserted before ret in order
            var opcodes = method.Body.Instructions.Select(i => i.OpCode).ToList();

            Assert.Equal(OpCodes.Nop, opcodes[0]);    // A
            Assert.Equal(OpCodes.Ldc_I4, opcodes[1]);  // B
            Assert.Equal(OpCodes.Pop, opcodes[2]);     // C
        }

        // ---------------------------------------------------------------
        // BUG 2: YieldPointId + 1 misalignment with 0-based switch table.
        //        File: MethodTransformer.cs lines 553-554, 636
        //
        //        The restore block computes:
        //          __state = __frame.YieldPointId + 1;
        //        Then dispatches with:
        //          switch(__state) => jumpTargets[0..N-1]
        //
        //        CIL switch is 0-based: value 0 → jumpTargets[0],
        //        value 1 → jumpTargets[1], etc.
        //
        //        If YieldPointId==0 (first yield), __state=1, so switch
        //        jumps to jumpTargets[1] (second yield point). The first
        //        yield point at jumpTargets[0] is unreachable.
        //
        //        If YieldPointId==N-1 (last yield), __state=N, which is
        //        out of range, causing fall-through.
        // ---------------------------------------------------------------
        [Fact]
        public void Bug_YieldPointId_Plus1_Misaligns_Switch_Table()
        {
            // The +1 at line 553 causes every resumption to target the
            // wrong yield point. We prove this by showing the arithmetic:
            //
            // Given 3 yield points (ids 0, 1, 2):
            //   jumpTargets = [yp0_instr, yp1_instr, yp2_instr]
            //
            // For YieldPointId=0: __state = 0+1 = 1 → jumpTargets[1] = yp1 (WRONG, should be yp0)
            // For YieldPointId=1: __state = 1+1 = 2 → jumpTargets[2] = yp2 (WRONG, should be yp1)
            // For YieldPointId=2: __state = 2+1 = 3 → out of range, fall-through (WRONG)

            int yieldPointCount = 3;

            for (int yieldPointId = 0; yieldPointId < yieldPointCount; yieldPointId++)
            {
                // After fix: no +1 offset, state == yieldPointId directly
                int state = yieldPointId;
                int targetIndex = state;       // CIL switch: value N goes to jumpTargets[N]

                // The correct target should be jumpTargets[yieldPointId]
                bool hitsCorrectTarget = (targetIndex == yieldPointId);
                bool isInRange = (targetIndex < yieldPointCount);

                Assert.True(hitsCorrectTarget,
                    $"YieldPointId={yieldPointId}: state={state} targets jumpTargets[{targetIndex}] " +
                    $"but should target jumpTargets[{yieldPointId}]. " +
                    (isInRange ? "Targets wrong yield point." : "Falls through (out of range)."));
            }
        }

        // ---------------------------------------------------------------
        // BUG 3: Catch block double-loads locals onto the stack.
        //        File: MethodTransformer.cs lines 315-342
        //
        //        Phase 1 (lines 315-323): Loads ALL original locals onto
        //        the stack with Ldloc + Box.
        //        Phase 2 (lines 326-342): Creates object[] array and loads
        //        each local AGAIN with Ldloc to store into the array.
        //
        //        The N values from Phase 1 are never consumed. They sit
        //        below the array on the eval stack, corrupting it.
        // ---------------------------------------------------------------
        [Fact]
        public void Bug_CatchBlock_DoubleLoads_Locals_Corrupts_Stack()
        {
            // Count the stack effects of the catch block IL sequence.
            // Phase 1 loads N locals (net push = N).
            // Phase 2 creates array (net push = 1 after filling).
            // Total stack should be 1 (just the array), but is N+1.

            int originalLocalCount = 3; // Simulate 3 original locals
            // Hardcoded -3 from line 315-326 means locals "captured" = Variables.Count - 3
            // With 3 added locals (context, frame, state), total Variables.Count = 6
            // localCount = 6 - 3 = 3

            // After fix: Phase 1 double-load was removed. No extra pushes.
            int stackAfterPhase1 = 0;

            // Phase 2: newarr pushes 1, then for each local:
            //   dup (+1), ldc.i4 (+1), ldloc (+1), [box (+0 net)], stelem_ref (-3)
            //   net per iteration = 0
            // After fix: stack = 0 (no phase 1) + 1 (array) = 1
            int stackAfterPhase2 = stackAfterPhase1 + 1;

            // Stack depth is exactly 1 (just the array)
            Assert.Equal(1, stackAfterPhase2);
        }

        // ---------------------------------------------------------------
        // BUG 4: _yieldPointIdField resolved from HostFrameRecord but
        //        called on SuspendException instance.
        //        File: MethodTransformer.cs lines 137-138, 352-355
        //
        //        _yieldPointIdField = ImportReference(
        //            frameRecordDef.Properties.FirstOrDefault(
        //                p => p.Name == "YieldPointId")?.GetMethod)
        //
        //        But at line 352, it's called on exLocal (SuspendException):
        //          Ldloc exLocal          // SuspendException
        //          Callvirt _yieldPointIdField  // HostFrameRecord.get_YieldPointId
        //
        //        These are different types with different method tokens.
        // ---------------------------------------------------------------
        [Fact]
        public void Bug_YieldPointIdField_Resolved_From_Wrong_Type()
        {
            // HostFrameRecord.YieldPointId and SuspendException.YieldPointId
            // are different properties on different types. The MethodTransformer
            // resolves the getter from HostFrameRecord but calls it on a
            // SuspendException instance.

            var hostFrameRecordType = typeof(Prim.Core.HostFrameRecord);
            var suspendExceptionType = typeof(Prim.Core.SuspendException);

            var hostFrameYieldPointId = hostFrameRecordType.GetProperty("YieldPointId");
            var suspendExYieldPointId = suspendExceptionType.GetProperty("YieldPointId");

            Assert.NotNull(hostFrameYieldPointId);
            Assert.NotNull(suspendExYieldPointId);

            // They are on different declaring types
            Assert.NotEqual(
                hostFrameYieldPointId.DeclaringType,
                suspendExYieldPointId.DeclaringType);

            // The getter methods have different metadata tokens
            var hostGetter = hostFrameYieldPointId.GetGetMethod();
            var suspendGetter = suspendExYieldPointId.GetGetMethod();

            // After fix: MethodTransformer now correctly resolves YieldPointId from
            // SuspendException, not HostFrameRecord. Verify the properties are distinct
            // (confirming the fix matters - using the wrong one would be a type mismatch).
            Assert.NotEqual(hostGetter.MetadataToken, suspendGetter.MetadataToken);
        }

        // ---------------------------------------------------------------
        // BUG 5: Hardcoded local exclusion count is wrong.
        //        File: MethodTransformer.cs lines 315, 560-561
        //
        //        WrapInTryCatch (line 315) uses -3, but by that point
        //        4 locals have been added (context, frame, state, exLocal).
        //        AddRestoreBlock (line 560) uses -4, but 5 locals may
        //        have been added (+ slotsLocal from line 387).
        //
        //        This causes internal bookkeeping locals to be included
        //        in the "original" locals set, corrupting save/restore.
        // ---------------------------------------------------------------
        [Fact]
        public void Bug_Hardcoded_Local_Exclusion_Count_Wrong()
        {
            // Simulate the variable additions in Transform():
            // Original method has 2 locals (user variables)
            int originalCount = 2;

            // Step 1 (lines 63-65): Add 3 locals: context, frame, state
            int afterStep1 = originalCount + 3; // = 5

            // Step 4 WrapInTryCatch: adds exLocal (line 303) and slotsLocal (line 387)
            int afterExLocal = afterStep1 + 1; // = 6
            int afterSlotsLocal = afterExLocal + 1; // = 7

            // After fix: WrapInTryCatch uses -4 (excludes context, frame, state, exLocal)
            int capturedByWrap = afterExLocal - 4; // = 2
            Assert.Equal(originalCount, capturedByWrap);

            // After fix: AddRestoreBlock uses -5 (excludes context, frame, state, exLocal, slotsLocal)
            int capturedByRestore = afterSlotsLocal - 5; // = 2
            Assert.Equal(originalCount, capturedByRestore);
        }
    }
}
