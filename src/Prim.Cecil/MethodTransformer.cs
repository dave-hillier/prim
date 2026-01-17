using System;
using System.Collections.Generic;
using System.Linq;
using Prim.Analysis;
using Prim.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Prim.Cecil
{
    /// <summary>
    /// Transforms a single method to add continuation support.
    /// Injects yield point checks, state capture, and restoration logic.
    /// </summary>
    internal sealed class MethodTransformer
    {
        private readonly MethodDefinition _method;
        private readonly RewriterOptions _options;
        private readonly YieldPointIdentifier _yieldPointIdentifier;
        private readonly ModuleDefinition _module;

        // Cached type/method references
        private TypeReference _scriptContextType;
        private MethodReference _ensureCurrentMethod;
        private MethodReference _handleYieldPointMethod;
        private MethodReference _handleYieldPointWithBudgetMethod;
        private TypeReference _suspendExceptionType;
        private MethodReference _frameCapturePackSlots;
        private MethodReference _frameCaptureCaptureFrame;
        private MethodReference _frameCaptureGetSlot;
        private TypeReference _hostFrameRecordType;
        private FieldReference _frameChainField;
        private FieldReference _isRestoringField;
        private MethodReference _methodTokenField;
        private MethodReference _yieldPointIdField;
        private MethodReference _slotsField;
        private MethodReference _callerField;

        public MethodTransformer(MethodDefinition method, RewriterOptions options)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _yieldPointIdentifier = new YieldPointIdentifier(method, options.ToYieldPointOptions());
            _module = method.Module;
        }

        /// <summary>
        /// Transforms the method to support continuations.
        /// </summary>
        public void Transform()
        {
            var yieldPoints = _yieldPointIdentifier.FindYieldPoints();
            if (yieldPoints.Count == 0) return;

            // Import required types and methods
            ImportReferences();

            var il = _method.Body.GetILProcessor();
            var methodToken = GenerateMethodToken();

            // Step 1: Add local variables we need
            var contextLocal = AddLocal(_scriptContextType, "__context");
            var frameLocal = AddLocal(_hostFrameRecordType, "__frame");
            var stateLocal = AddLocal(_module.TypeSystem.Int32, "__state");

            // Step 2: Store original first instruction for branch fixup
            var originalFirst = _method.Body.Instructions[0];

            // Step 3: Inject yield point checks before backward branches
            InjectYieldPointChecks(il, yieldPoints);

            // Step 4: Wrap body in try-catch for state capture
            WrapInTryCatch(il, yieldPoints, methodToken, contextLocal);

            // Step 5: Add restore block at method entry
            AddRestoreBlock(il, yieldPoints, methodToken, contextLocal, frameLocal, stateLocal, originalFirst);

            // Step 6: Optimize IL
            _method.Body.SimplifyMacros();
            _method.Body.OptimizeMacros();
        }

        private void ImportReferences()
        {
            // Import Prim.Runtime.ScriptContext
            _scriptContextType = _module.ImportReference(typeof(object)); // Placeholder
            var primRuntime = FindOrLoadAssembly("Prim.Runtime");
            var primCore = FindOrLoadAssembly("Prim.Core");

            if (primRuntime != null)
            {
                var scriptContextDef = primRuntime.MainModule.Types.FirstOrDefault(t => t.Name == "ScriptContext");
                if (scriptContextDef != null)
                {
                    _scriptContextType = _module.ImportReference(scriptContextDef);
                    _ensureCurrentMethod = _module.ImportReference(
                        scriptContextDef.Methods.FirstOrDefault(m => m.Name == "EnsureCurrent"));
                    _handleYieldPointMethod = _module.ImportReference(
                        scriptContextDef.Methods.FirstOrDefault(m =>
                            m.Name == "HandleYieldPoint" && m.Parameters.Count == 1));
                    _handleYieldPointWithBudgetMethod = _module.ImportReference(
                        scriptContextDef.Methods.FirstOrDefault(m =>
                            m.Name == "HandleYieldPointWithBudget" && m.Parameters.Count == 2));
                    _isRestoringField = _module.ImportReference(
                        scriptContextDef.Fields.FirstOrDefault(f => f.Name == "IsRestoring"));
                    _frameChainField = _module.ImportReference(
                        scriptContextDef.Fields.FirstOrDefault(f => f.Name == "FrameChain"));
                }

                var frameCaptureClass = primRuntime.MainModule.Types.FirstOrDefault(t => t.Name == "FrameCapture");
                if (frameCaptureClass != null)
                {
                    _frameCapturePackSlots = _module.ImportReference(
                        frameCaptureClass.Methods.FirstOrDefault(m => m.Name == "PackSlots"));
                    _frameCaptureCaptureFrame = _module.ImportReference(
                        frameCaptureClass.Methods.FirstOrDefault(m => m.Name == "CaptureFrame"));
                    _frameCaptureGetSlot = _module.ImportReference(
                        frameCaptureClass.Methods.FirstOrDefault(m => m.Name == "GetSlot"));
                }
            }

            if (primCore != null)
            {
                var suspendExDef = primCore.MainModule.Types.FirstOrDefault(t => t.Name == "SuspendException");
                if (suspendExDef != null)
                {
                    _suspendExceptionType = _module.ImportReference(suspendExDef);
                }

                var frameRecordDef = primCore.MainModule.Types.FirstOrDefault(t => t.Name == "HostFrameRecord");
                if (frameRecordDef != null)
                {
                    _hostFrameRecordType = _module.ImportReference(frameRecordDef);
                    _methodTokenField = _module.ImportReference(
                        frameRecordDef.Properties.FirstOrDefault(p => p.Name == "MethodToken")?.GetMethod);
                    _yieldPointIdField = _module.ImportReference(
                        frameRecordDef.Properties.FirstOrDefault(p => p.Name == "YieldPointId")?.GetMethod);
                    _slotsField = _module.ImportReference(
                        frameRecordDef.Properties.FirstOrDefault(p => p.Name == "Slots")?.GetMethod);
                    _callerField = _module.ImportReference(
                        frameRecordDef.Properties.FirstOrDefault(p => p.Name == "Caller")?.GetMethod);
                }
            }

            // Fallback to object if types not found
            _scriptContextType ??= _module.TypeSystem.Object;
            _hostFrameRecordType ??= _module.TypeSystem.Object;
            _suspendExceptionType ??= _module.ImportReference(typeof(Exception));
        }

        private AssemblyDefinition FindOrLoadAssembly(string name)
        {
            try
            {
                var resolver = _module.AssemblyResolver;
                var reference = _module.AssemblyReferences.FirstOrDefault(r => r.Name == name);
                if (reference != null)
                {
                    return resolver.Resolve(reference);
                }

                // Try loading by name
                return resolver.Resolve(new AssemblyNameReference(name, new Version(1, 0, 0, 0)));
            }
            catch
            {
                return null;
            }
        }

        private VariableDefinition AddLocal(TypeReference type, string debugName)
        {
            var local = new VariableDefinition(type);
            _method.Body.Variables.Add(local);
            return local;
        }

        private void InjectYieldPointChecks(ILProcessor il, List<ILYieldPoint> yieldPoints)
        {
            // For each yield point (backward branch), inject a call to HandleYieldPoint
            // We need to insert the check BEFORE the branch instruction

            if (_handleYieldPointMethod == null) return;

            // Process in reverse order to maintain correct offsets
            var sortedYieldPoints = yieldPoints.OrderByDescending(yp => yp.Instruction.Offset).ToList();

            // Calculate instruction costs between yield points
            var costs = CalculateInstructionCosts(yieldPoints);

            foreach (var yp in sortedYieldPoints)
            {
                var branchInstruction = yp.Instruction;
                var cost = costs.TryGetValue(yp.Id, out var c) ? c : 1;

                List<Instruction> checkSequence;

                if (_options.EnableInstructionCounting && _handleYieldPointWithBudgetMethod != null)
                {
                    // Create yield check sequence with budget:
                    // call ScriptContext.EnsureCurrent()
                    // ldc.i4 <yieldPointId>
                    // ldc.i4 <cost>
                    // callvirt HandleYieldPointWithBudget(int, int)
                    checkSequence = new List<Instruction>
                    {
                        il.Create(OpCodes.Call, _ensureCurrentMethod),
                        il.Create(OpCodes.Ldc_I4, yp.Id),
                        il.Create(OpCodes.Ldc_I4, cost),
                        il.Create(OpCodes.Callvirt, _handleYieldPointWithBudgetMethod)
                    };
                }
                else
                {
                    // Create simple yield check sequence:
                    // call ScriptContext.EnsureCurrent()
                    // ldc.i4 <yieldPointId>
                    // callvirt HandleYieldPoint(int)
                    checkSequence = new List<Instruction>
                    {
                        il.Create(OpCodes.Call, _ensureCurrentMethod),
                        il.Create(OpCodes.Ldc_I4, yp.Id),
                        il.Create(OpCodes.Callvirt, _handleYieldPointMethod)
                    };
                }

                // Insert before the branch
                foreach (var instr in checkSequence)
                {
                    il.InsertBefore(branchInstruction, instr);
                }

                // Update any branch targets pointing to the branch instruction
                // to point to our first injected instruction
                UpdateBranchTargets(il, branchInstruction, checkSequence[0]);
            }
        }

        /// <summary>
        /// Calculates the instruction cost for each yield point.
        /// Cost is the number of IL instructions between this yield point and the previous one.
        /// </summary>
        private Dictionary<int, int> CalculateInstructionCosts(List<ILYieldPoint> yieldPoints)
        {
            var costs = new Dictionary<int, int>();
            var instructions = _method.Body.Instructions;

            if (yieldPoints.Count == 0 || instructions.Count == 0)
                return costs;

            // Sort yield points by offset
            var sortedYieldPoints = yieldPoints.OrderBy(yp => yp.Instruction.Offset).ToList();

            // Cost for first yield point is from method start
            int previousOffset = 0;
            foreach (var yp in sortedYieldPoints)
            {
                // Count instructions between previous offset and this yield point
                int count = 0;
                foreach (var instr in instructions)
                {
                    if (instr.Offset >= previousOffset && instr.Offset < yp.Instruction.Offset)
                    {
                        count++;
                    }
                }

                // Minimum cost of 1 to ensure progress
                costs[yp.Id] = Math.Max(1, count);
                previousOffset = yp.Instruction.Offset;
            }

            return costs;
        }

        private void WrapInTryCatch(ILProcessor il, List<ILYieldPoint> yieldPoints, int methodToken, VariableDefinition contextLocal)
        {
            if (_suspendExceptionType == null) return;

            var body = _method.Body;
            var instructions = body.Instructions;

            // Find or create the return instruction(s)
            var returnInstructions = instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
            if (returnInstructions.Count == 0) return;

            // Create end-of-try label (jump target after catch)
            var endLabel = il.Create(OpCodes.Nop);
            il.Append(endLabel);

            // Create catch handler
            var catchStart = il.Create(OpCodes.Nop);
            var catchEnd = il.Create(OpCodes.Nop);

            // Build catch block instructions:
            // <exception on stack>
            // stloc exceptionLocal
            // ldloc exceptionLocal
            // ... capture frame ...
            // rethrow

            var exLocal = AddLocal(_suspendExceptionType, "__ex");

            var catchInstructions = new List<Instruction>
            {
                catchStart,
                il.Create(OpCodes.Stloc, exLocal),
            };

            // Build slots array from locals
            if (_frameCapturePackSlots != null)
            {
                // Load all locals onto stack
                foreach (var local in body.Variables.Take(body.Variables.Count - 3)) // Exclude our added locals
                {
                    catchInstructions.Add(il.Create(OpCodes.Ldloc, local));
                    // Box value types
                    if (local.VariableType.IsValueType)
                    {
                        catchInstructions.Add(il.Create(OpCodes.Box, local.VariableType));
                    }
                }

                // Create array for PackSlots
                var localCount = Math.Max(0, body.Variables.Count - 3);
                catchInstructions.Add(il.Create(OpCodes.Ldc_I4, localCount));
                catchInstructions.Add(il.Create(OpCodes.Newarr, _module.TypeSystem.Object));

                // Store locals into array (reverse order since they're on stack)
                for (int i = localCount - 1; i >= 0; i--)
                {
                    catchInstructions.Add(il.Create(OpCodes.Dup));
                    catchInstructions.Add(il.Create(OpCodes.Ldc_I4, i));
                    // Load from stack position
                    catchInstructions.Add(il.Create(OpCodes.Ldloc, body.Variables[i]));
                    if (body.Variables[i].VariableType.IsValueType)
                    {
                        catchInstructions.Add(il.Create(OpCodes.Box, body.Variables[i].VariableType));
                    }
                    catchInstructions.Add(il.Create(OpCodes.Stelem_Ref));
                }
            }

            // Create HostFrameRecord
            if (_frameCaptureCaptureFrame != null)
            {
                // Load methodToken
                catchInstructions.Add(il.Create(OpCodes.Ldc_I4, methodToken));

                // Load yieldPointId from exception
                catchInstructions.Add(il.Create(OpCodes.Ldloc, exLocal));
                if (_yieldPointIdField != null)
                {
                    catchInstructions.Add(il.Create(OpCodes.Callvirt, _yieldPointIdField));
                }
                else
                {
                    catchInstructions.Add(il.Create(OpCodes.Pop));
                    catchInstructions.Add(il.Create(OpCodes.Ldc_I4_0));
                }

                // slots array already on stack from above or create empty
                if (_frameCapturePackSlots == null)
                {
                    catchInstructions.Add(il.Create(OpCodes.Ldnull));
                }

                // Load FrameChain from exception
                catchInstructions.Add(il.Create(OpCodes.Ldloc, exLocal));
                var frameChainGetter = _suspendExceptionType.Resolve()?.Properties
                    .FirstOrDefault(p => p.Name == "FrameChain")?.GetMethod;
                if (frameChainGetter != null)
                {
                    catchInstructions.Add(il.Create(OpCodes.Callvirt, _module.ImportReference(frameChainGetter)));
                }
                else
                {
                    catchInstructions.Add(il.Create(OpCodes.Pop));
                    catchInstructions.Add(il.Create(OpCodes.Ldnull));
                }

                // Call CaptureFrame
                catchInstructions.Add(il.Create(OpCodes.Call, _frameCaptureCaptureFrame));

                // Store into exception's FrameChain
                var slotsLocal = AddLocal(_hostFrameRecordType, "__record");
                catchInstructions.Add(il.Create(OpCodes.Stloc, slotsLocal));
                catchInstructions.Add(il.Create(OpCodes.Ldloc, exLocal));
                catchInstructions.Add(il.Create(OpCodes.Ldloc, slotsLocal));

                var frameChainSetter = _suspendExceptionType.Resolve()?.Properties
                    .FirstOrDefault(p => p.Name == "FrameChain")?.SetMethod;
                if (frameChainSetter != null)
                {
                    catchInstructions.Add(il.Create(OpCodes.Callvirt, _module.ImportReference(frameChainSetter)));
                }
                else
                {
                    catchInstructions.Add(il.Create(OpCodes.Pop));
                    catchInstructions.Add(il.Create(OpCodes.Pop));
                }
            }

            // Rethrow
            catchInstructions.Add(il.Create(OpCodes.Rethrow));
            catchInstructions.Add(catchEnd);

            // Append catch block
            foreach (var instr in catchInstructions)
            {
                il.Append(instr);
            }

            // Find try start (after our restore block, which will be added later)
            var tryStart = instructions[0];

            // Find try end - just before our catch
            var tryEnd = catchStart;

            // Add exception handler
            var handler = new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart,
                TryEnd = tryEnd,
                HandlerStart = catchStart,
                HandlerEnd = catchEnd,
                CatchType = _suspendExceptionType
            };

            body.ExceptionHandlers.Add(handler);
        }

        private void AddRestoreBlock(
            ILProcessor il,
            List<ILYieldPoint> yieldPoints,
            int methodToken,
            VariableDefinition contextLocal,
            VariableDefinition frameLocal,
            VariableDefinition stateLocal,
            Instruction originalFirst)
        {
            if (_ensureCurrentMethod == null) return;

            // Create restore block at method entry:
            // var __context = ScriptContext.EnsureCurrent();
            // if (__context.IsRestoring && __context.FrameChain?.MethodToken == methodToken)
            // {
            //     var __frame = __context.FrameChain;
            //     __context.FrameChain = __frame.Caller;
            //     __state = __frame.YieldPointId + 1;
            //     // restore locals from slots
            //     if (__context.FrameChain == null)
            //         __context.IsRestoring = false;
            //     switch(__state) { goto yield_point_N; ... }
            // }

            var restoreInstructions = new List<Instruction>();

            // var __context = ScriptContext.EnsureCurrent();
            restoreInstructions.Add(il.Create(OpCodes.Call, _ensureCurrentMethod));
            restoreInstructions.Add(il.Create(OpCodes.Stloc, contextLocal));

            // Create labels for jumps
            var skipRestoreLabel = il.Create(OpCodes.Nop);
            var afterRestoreLabel = originalFirst;

            // if (!__context.IsRestoring) goto skip
            restoreInstructions.Add(il.Create(OpCodes.Ldloc, contextLocal));
            if (_isRestoringField != null)
            {
                restoreInstructions.Add(il.Create(OpCodes.Ldfld, _isRestoringField));
            }
            else
            {
                restoreInstructions.Add(il.Create(OpCodes.Pop));
                restoreInstructions.Add(il.Create(OpCodes.Ldc_I4_0));
            }
            restoreInstructions.Add(il.Create(OpCodes.Brfalse, skipRestoreLabel));

            // if (__context.FrameChain == null) goto skip
            restoreInstructions.Add(il.Create(OpCodes.Ldloc, contextLocal));
            if (_frameChainField != null)
            {
                restoreInstructions.Add(il.Create(OpCodes.Ldfld, _frameChainField));
            }
            else
            {
                restoreInstructions.Add(il.Create(OpCodes.Pop));
                restoreInstructions.Add(il.Create(OpCodes.Ldnull));
            }
            restoreInstructions.Add(il.Create(OpCodes.Dup));
            restoreInstructions.Add(il.Create(OpCodes.Stloc, frameLocal));
            restoreInstructions.Add(il.Create(OpCodes.Brfalse, skipRestoreLabel));

            // if (__frame.MethodToken != methodToken) goto skip
            restoreInstructions.Add(il.Create(OpCodes.Ldloc, frameLocal));
            if (_methodTokenField != null)
            {
                restoreInstructions.Add(il.Create(OpCodes.Callvirt, _methodTokenField));
            }
            else
            {
                restoreInstructions.Add(il.Create(OpCodes.Pop));
                restoreInstructions.Add(il.Create(OpCodes.Ldc_I4_0));
            }
            restoreInstructions.Add(il.Create(OpCodes.Ldc_I4, methodToken));
            restoreInstructions.Add(il.Create(OpCodes.Bne_Un, skipRestoreLabel));

            // __context.FrameChain = __frame.Caller;
            restoreInstructions.Add(il.Create(OpCodes.Ldloc, contextLocal));
            restoreInstructions.Add(il.Create(OpCodes.Ldloc, frameLocal));
            if (_callerField != null)
            {
                restoreInstructions.Add(il.Create(OpCodes.Callvirt, _callerField));
            }
            else
            {
                restoreInstructions.Add(il.Create(OpCodes.Pop));
                restoreInstructions.Add(il.Create(OpCodes.Ldnull));
            }
            if (_frameChainField != null)
            {
                var frameChainSetter = _scriptContextType.Resolve()?.Fields
                    .FirstOrDefault(f => f.Name == "FrameChain");
                if (frameChainSetter != null)
                {
                    restoreInstructions.Add(il.Create(OpCodes.Stfld, _module.ImportReference(frameChainSetter)));
                }
                else
                {
                    restoreInstructions.Add(il.Create(OpCodes.Pop));
                    restoreInstructions.Add(il.Create(OpCodes.Pop));
                }
            }
            else
            {
                restoreInstructions.Add(il.Create(OpCodes.Pop));
                restoreInstructions.Add(il.Create(OpCodes.Pop));
            }

            // __state = __frame.YieldPointId + 1;
            restoreInstructions.Add(il.Create(OpCodes.Ldloc, frameLocal));
            if (_yieldPointIdField != null)
            {
                restoreInstructions.Add(il.Create(OpCodes.Callvirt, _yieldPointIdField));
            }
            else
            {
                restoreInstructions.Add(il.Create(OpCodes.Pop));
                restoreInstructions.Add(il.Create(OpCodes.Ldc_I4_0));
            }
            restoreInstructions.Add(il.Create(OpCodes.Ldc_I4_1));
            restoreInstructions.Add(il.Create(OpCodes.Add));
            restoreInstructions.Add(il.Create(OpCodes.Stloc, stateLocal));

            // Restore locals from slots (simplified - just restore first few)
            if (_frameCaptureGetSlot != null && _slotsField != null)
            {
                var originalLocals = _method.Body.Variables.Take(
                    Math.Max(0, _method.Body.Variables.Count - 4)).ToList();

                for (int i = 0; i < originalLocals.Count; i++)
                {
                    var local = originalLocals[i];

                    // slots = __frame.Slots
                    restoreInstructions.Add(il.Create(OpCodes.Ldloc, frameLocal));
                    restoreInstructions.Add(il.Create(OpCodes.Callvirt, _slotsField));

                    // index
                    restoreInstructions.Add(il.Create(OpCodes.Ldc_I4, i));

                    // Call GetSlot<T>
                    var getSlotGeneric = new GenericInstanceMethod(_frameCaptureGetSlot);
                    getSlotGeneric.GenericArguments.Add(local.VariableType);
                    restoreInstructions.Add(il.Create(OpCodes.Call, getSlotGeneric));

                    // Store
                    restoreInstructions.Add(il.Create(OpCodes.Stloc, local));
                }
            }

            // Check if last frame - clear IsRestoring
            // if (__context.FrameChain == null) __context.IsRestoring = false;
            var afterIsRestoringCheck = il.Create(OpCodes.Nop);
            restoreInstructions.Add(il.Create(OpCodes.Ldloc, contextLocal));
            if (_frameChainField != null)
            {
                restoreInstructions.Add(il.Create(OpCodes.Ldfld, _frameChainField));
            }
            else
            {
                restoreInstructions.Add(il.Create(OpCodes.Pop));
                restoreInstructions.Add(il.Create(OpCodes.Ldnull));
            }
            restoreInstructions.Add(il.Create(OpCodes.Brtrue, afterIsRestoringCheck));

            restoreInstructions.Add(il.Create(OpCodes.Ldloc, contextLocal));
            restoreInstructions.Add(il.Create(OpCodes.Ldc_I4_0));
            if (_isRestoringField != null)
            {
                var isRestoringFieldDef = _scriptContextType.Resolve()?.Fields
                    .FirstOrDefault(f => f.Name == "IsRestoring");
                if (isRestoringFieldDef != null)
                {
                    restoreInstructions.Add(il.Create(OpCodes.Stfld, _module.ImportReference(isRestoringFieldDef)));
                }
                else
                {
                    restoreInstructions.Add(il.Create(OpCodes.Pop));
                    restoreInstructions.Add(il.Create(OpCodes.Pop));
                }
            }
            else
            {
                restoreInstructions.Add(il.Create(OpCodes.Pop));
                restoreInstructions.Add(il.Create(OpCodes.Pop));
            }

            restoreInstructions.Add(afterIsRestoringCheck);

            // Build switch for yield points
            if (yieldPoints.Count > 0)
            {
                // Create jump targets for each yield point
                var jumpTargets = new Instruction[yieldPoints.Count];
                for (int i = 0; i < yieldPoints.Count; i++)
                {
                    // Find the instruction after the yield point check we injected
                    jumpTargets[i] = yieldPoints[i].Instruction; // Jump to where yield point was
                }

                // switch(__state) { 1: goto yp0; 2: goto yp1; ... }
                restoreInstructions.Add(il.Create(OpCodes.Ldloc, stateLocal));
                restoreInstructions.Add(il.Create(OpCodes.Switch, jumpTargets));
            }

            // Goto original code (state == 0 or unknown)
            restoreInstructions.Add(il.Create(OpCodes.Br, afterRestoreLabel));

            // Skip restore label
            restoreInstructions.Add(skipRestoreLabel);

            // Insert all at beginning
            var firstInstr = _method.Body.Instructions[0];
            foreach (var instr in restoreInstructions.AsEnumerable().Reverse())
            {
                il.InsertBefore(firstInstr, instr);
            }

            // Update exception handler try start
            foreach (var handler in _method.Body.ExceptionHandlers)
            {
                if (handler.TryStart == originalFirst)
                {
                    handler.TryStart = skipRestoreLabel.Next ?? originalFirst;
                }
            }
        }

        private void UpdateBranchTargets(ILProcessor il, Instruction oldTarget, Instruction newTarget)
        {
            foreach (var instruction in _method.Body.Instructions)
            {
                if (instruction.Operand == oldTarget)
                {
                    instruction.Operand = newTarget;
                }
                else if (instruction.Operand is Instruction[] targets)
                {
                    for (int i = 0; i < targets.Length; i++)
                    {
                        if (targets[i] == oldTarget)
                        {
                            targets[i] = newTarget;
                        }
                    }
                }
            }

            foreach (var handler in _method.Body.ExceptionHandlers)
            {
                if (handler.TryStart == oldTarget) handler.TryStart = newTarget;
                if (handler.TryEnd == oldTarget) handler.TryEnd = newTarget;
                if (handler.HandlerStart == oldTarget) handler.HandlerStart = newTarget;
                if (handler.HandlerEnd == oldTarget) handler.HandlerEnd = newTarget;
                if (handler.FilterStart == oldTarget) handler.FilterStart = newTarget;
            }
        }

        private int GenerateMethodToken()
        {
            var typeName = _method.DeclaringType.FullName;
            var methodName = _method.Name;
            var paramTypes = _method.Parameters.Select(p => p.ParameterType.FullName).ToArray();

            return StableHash.GenerateMethodToken(typeName, methodName, paramTypes);
        }
    }
}
