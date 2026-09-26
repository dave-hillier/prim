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
    ///
    /// <para>
    /// The transformed method follows the proven async/iterator state-machine shape so
    /// that the emitted IL is verifiable (ECMA-335) and accepted by ilverify:
    /// </para>
    ///
    /// <code>
    ///   // PRE-CHECK (outside the protected region):
    ///   __context = ScriptContext.EnsureCurrent();
    ///   if (!__context.IsRestoring)                      goto enterTry;
    ///   __frame = __context.FrameChain;
    ///   if (__frame == null)                             goto enterTry;
    ///   if (__frame.MethodToken != methodToken)          goto enterTry;
    ///   __context.FrameChain = __frame.Caller;
    ///   __state = __frame.YieldPointId + 1;
    ///   // restore locals + spilled eval-stack temps from __frame.Slots
    ///   if (__context.FrameChain == null) __context.IsRestoring = false;
    ///   // fall through into the try, where the dispatch switch reads __state
    ///
    ///   .try {
    ///   enterTry:                                        // TryStart
    ///       switch (__state) { 1: goto resume_0; 2: goto resume_1; ... }
    ///       ... original body ...
    ///       // at each yield point:
    ///       //   spill live eval stack -> temp locals
    ///       //   __yieldPoint = id;
    ///       //   EnsureCurrent(); HandleYieldPoint(id)   // may throw SuspendException
    ///       // resume_i:                                  // Nop; resume lands here (after
    ///       //                                            //   the check, before the op)
    ///       //   reload spilled eval-stack temps and push them back
    ///       //   <suspending op>                          // re-executed once on resume
    ///       //                                            //   (e.g. the back-edge branch)
    ///       // at each call to another transformed method (resume point, no check):
    ///       //   spill live eval stack (incl. the call's arguments) -> temp locals
    ///       // resume_i:
    ///       //   __yieldPoint = id;
    ///       //   reload spilled temps; call Callee(...)  // re-executed on resume; the
    ///       //                                            //   callee restores itself
    ///       ... original 'ret' rewritten to: (stloc __ret;) leave realRet ...
    ///   }
    ///   catch (Prim.Core.SuspendException __ex) {
    ///       // pack locals + spilled temps into object[] slots
    ///       // __record = FrameCapture.CaptureFrame(methodToken, __yieldPoint, slots, __ex.FrameChain);
    ///       // __ex.FrameChain = __record;
    ///       rethrow;
    ///   }
    ///   realRet:
    ///       (ldloc __ret;) ret;
    /// </code>
    ///
    /// <para>
    /// The dispatch switch and every resume label live INSIDE the same try region as the
    /// yield points, so each switch -> resume-label branch is intra-try (legal). The
    /// pre-check sits outside the try and only branches to the START of the try region
    /// (entering it normally), which is also legal.
    /// </para>
    ///
    /// <para>
    /// The catch records <c>__yieldPoint</c>, not <c>__ex.YieldPointId</c>: the exception
    /// carries the ID of the yield point that threw, which belongs to the innermost frame
    /// only. Every outer frame is suspended at a call to a transformed method, and must
    /// resume at that call site (whitepaper §3.2, §4.5).
    /// </para>
    /// </summary>
    internal sealed class MethodTransformer
    {
        private readonly MethodDefinition _method;
        private readonly RewriterOptions _options;
        private readonly YieldPointIdentifier _yieldPointIdentifier;
        private readonly ModuleDefinition _module;

        // Frame-local "current yield point": written before every yield check and at every
        // continuable call site, read by the catch block when recording this frame.
        private VariableDefinition _yieldPointLocal;

        // Number of locals present in the method before any synthetic locals are
        // injected. Captured before we add synthetic/spill locals so slot indexing
        // stays order-independent and does not rely on hard-coded offsets.
        private int _originalLocalCount;

        // Original locals that are eligible for capture (value/ref types). Byref,
        // pinned and pointer locals are excluded (see #38) and these are captured by
        // INDEX so capture and restore agree exactly.
        private List<int> _capturableLocalIndices;

        // Per-yield-point spill temps (one list per yield point, in bottom-to-top
        // stack order). These temp locals are appended after the captured locals in
        // the packed slots array. Indexed by yield-point Id.
        private Dictionary<int, List<VariableDefinition>> _spillTemps;

        // The slot index at which the spilled stack temps begin. The first N slots hold
        // the capturable locals; spill temps follow.
        private int _stackSlotBase;

        // Per-yield-point base slot index for that yield point's spill temps, keyed by
        // yield-point Id. Each yield point gets its OWN distinct trailing slot range
        // [base .. base+depth) so packing every yield point's temps in the catch block
        // cannot collide: at suspension only the active yield point's temps hold real
        // values, but each range is disjoint, so restore reads the active range and the
        // others (holding defaults) are simply never consumed. (A single shared base
        // would let an inactive yield point's default temps clobber the active one's.)
        private Dictionary<int, int> _spillSlotBase;

        // Total number of spill slots across all yield points (sum of per-yield-point
        // depths). slots array length = _stackSlotBase + _totalSpillSlots.
        private int _totalSpillSlots;

        // Cached type/method references
        private TypeReference _scriptContextType;
        private MethodReference _ensureCurrentMethod;
        private MethodReference _handleYieldPointMethod;
        private MethodReference _handleYieldPointWithBudgetMethod;
        private TypeReference _suspendExceptionType;
        private MethodReference _frameCaptureCaptureFrame;
        private MethodReference _frameCaptureGetSlot;
        private TypeReference _hostFrameRecordType;
        private FieldReference _frameChainField;
        private FieldReference _isRestoringField;
        private MethodReference _methodTokenGetter;
        private MethodReference _yieldPointIdGetter;
        private MethodReference _slotsGetter;
        private MethodReference _callerGetter;
        private MethodReference _suspendFrameChainGetter;
        private MethodReference _suspendFrameChainSetter;

        // Diagnostics surfaced to the rewriter when a method cannot be transformed.
        public string SkipReason { get; private set; }
        public bool WasTransformed { get; private set; }

        public MethodTransformer(MethodDefinition method, RewriterOptions options)
            : this(method, options, null)
        {
        }

        /// <param name="isContinuableCall">
        /// Identifies calls to other transformed methods; each becomes a resume point.
        /// </param>
        public MethodTransformer(
            MethodDefinition method, RewriterOptions options, Func<MethodReference, bool> isContinuableCall)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            var yieldPointOptions = options.ToYieldPointOptions();
            yieldPointOptions.IsContinuableCall = isContinuableCall;
            _yieldPointIdentifier = new YieldPointIdentifier(method, yieldPointOptions);
            _module = method.Module;
        }

        /// <summary>
        /// Transforms the method to support continuations.
        /// </summary>
        public void Transform()
        {
            var yieldPoints = _yieldPointIdentifier.FindYieldPoints();
            if (yieldPoints.Count == 0) return;

            // #38: byref / pinned / pointer locals and ref/out parameters cannot be
            // round-tripped through object[] / GetSlot<T>. Rather than emit invalid IL,
            // skip the method and surface a diagnostic.
            if (!CanCaptureState(yieldPoints, out var reason))
            {
                SkipReason = reason;
                return;
            }

            ImportReferences();

            // We need the runtime hooks; if they could not be resolved, skip rather than
            // emit half-wired (and likely invalid) IL.
            if (_ensureCurrentMethod == null || _handleYieldPointMethod == null ||
                _suspendExceptionType == null || _frameCaptureCaptureFrame == null ||
                _frameCaptureGetSlot == null || _hostFrameRecordType == null)
            {
                SkipReason = "required Prim runtime references could not be resolved";
                return;
            }

            var body = _method.Body;
            var il = body.GetILProcessor();
            var methodToken = GenerateMethodToken();

            // Record the original local count and which locals are capturable BEFORE
            // injecting synthetic locals.
            _originalLocalCount = body.Variables.Count;
            _capturableLocalIndices = Enumerable.Range(0, _originalLocalCount)
                .Where(i => IsCapturableLocal(body.Variables[i].VariableType))
                .ToList();

            // Synthetic locals.
            var contextLocal = AddLocal(_scriptContextType);
            var frameLocal = AddLocal(_hostFrameRecordType);
            var stateLocal = AddLocal(_module.TypeSystem.Int32);
            var exLocal = AddLocal(_suspendExceptionType);
            var recordLocal = AddLocal(_hostFrameRecordType);
            _yieldPointLocal = AddLocal(_module.TypeSystem.Int32);
            VariableDefinition retLocal = null;
            bool hasReturnValue = _method.ReturnType.FullName != "System.Void";
            if (hasReturnValue)
            {
                retLocal = AddLocal(_method.ReturnType);
            }

            // Allocate spill temps for each yield point's live eval stack and lay out
            // the trailing slot range.
            AllocateSpillTemps(yieldPoints);

            var originalFirst = body.Instructions[0];

            // Inject yield checks + spill/restore around each yield point; collect the
            // resume label (the Nop placed AFTER each suspending op).
            var resumeLabels = InjectYieldPointChecks(il, yieldPoints);

            // Rewrite all original 'ret' into store-return + leave to a single real
            // return. The real return is created here but NOT appended yet; it must sit
            // OUTSIDE the try region (after the catch handler), so we append it last.
            var realRetEntry = RewriteReturns(il, retLocal, hasReturnValue, out var realRet);

            // Build the dispatch switch at the top of the try and wire resume labels.
            var dispatchStart = BuildDispatch(il, originalFirst, stateLocal, resumeLabels);

            // Build the catch block (appended after the body, before the real return).
            var (catchStart, catchEnd) = BuildCatchBlock(
                il, methodToken, exLocal, recordLocal);

            // Now append the real return AFTER the catch handler so it lives outside the
            // try region (a 'ret' inside a protected region is illegal CIL; the body
            // leaves to here instead). HandlerEnd == realRetEntry closes the handler.
            il.Append(realRetEntry);
            if (hasReturnValue)
            {
                il.Append(realRet);
            }

            // Register the try/catch: try = [dispatchStart, catchStart), handler = catch.
            var handler = new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = dispatchStart,
                TryEnd = catchStart,
                HandlerStart = catchStart,
                // HandlerEnd is exclusive: the first instruction after the handler, i.e.
                // the real return we appended just past the catch's trailing nop.
                HandlerEnd = realRetEntry,
                CatchType = _suspendExceptionType
            };
            body.ExceptionHandlers.Add(handler);

            // Build the pre-check/restore block at method entry (OUTSIDE the try). It
            // either falls through into dispatchStart (TryStart) or branches to it.
            BuildRestorePreCheck(
                il, methodToken, contextLocal, frameLocal, stateLocal, dispatchStart);

            // InitLocals is mandatory: we added ref-typed and value-typed locals that may
            // be observed before definite assignment along some paths (#34).
            body.InitLocals = true;

            // Normalize then recompute offsets/branch sizes.
            body.SimplifyMacros();
            body.OptimizeMacros();

            // OptimizeMacros does NOT recompute MaxStackSize; set a correct over-estimate (#34).
            body.MaxStackSize = ComputeMaxStack();

            WasTransformed = true;
        }

        // -- #38: capture eligibility -------------------------------------------------

        private bool CanCaptureState(List<ILYieldPoint> yieldPoints, out string reason)
        {
            reason = null;

            // A managed pointer live on the evaluation stack at a yield point (e.g. the
            // 'ldloca' receiver of a struct method call) would have to be spilled into the
            // object[] frame record, which is impossible.
            foreach (var yp in yieldPoints)
            {
                var types = yp.StackState?.Types;
                if (types == null) continue;
                foreach (var t in types)
                {
                    if (t != null && (t.IsByReference || t.IsPointer || t.IsPinned || t.IsFunctionPointer))
                    {
                        reason = $"a managed or unmanaged pointer is live on the evaluation stack at {yp}; state capture unsupported";
                        return false;
                    }
                }
            }

            // ref/out parameters are byref and cannot be boxed/round-tripped.
            foreach (var p in _method.Parameters)
            {
                if (p.ParameterType.IsByReference)
                {
                    reason = $"parameter '{p.Name}' is byref (ref/out); state capture unsupported";
                    return false;
                }
                if (p.ParameterType.IsPointer || p.ParameterType.IsPinned || p.ParameterType.IsFunctionPointer)
                {
                    reason = $"parameter '{p.Name}' is a pointer/pinned/function-pointer type; state capture unsupported";
                    return false;
                }
            }

            // byref / pinned / pointer locals cannot be captured. We could exclude them
            // individually, but a live byref/pointer at a yield point would silently lose
            // state, so we skip the whole method with a clear diagnostic.
            foreach (var v in _method.Body.Variables)
            {
                // Check pinned first: a pinned local may wrap a byref/pointer element, and
                // pinning is the more specific (and more dangerous to relocate) property.
                if (v.IsPinned || v.VariableType.IsPinned)
                {
                    reason = "method has a pinned local; state capture unsupported";
                    return false;
                }
                if (v.VariableType.IsByReference)
                {
                    reason = "method has a byref (&T) local; state capture unsupported";
                    return false;
                }
                if (v.VariableType.IsPointer || v.VariableType.IsFunctionPointer)
                {
                    reason = "method has a pointer local; state capture unsupported";
                    return false;
                }
            }

            return true;
        }

        private static bool IsCapturableLocal(TypeReference t)
        {
            if (t == null) return false;
            if (t.IsByReference || t.IsPointer || t.IsPinned || t.IsFunctionPointer) return false;
            return true;
        }

        // -- spill temp allocation ----------------------------------------------------

        private void AllocateSpillTemps(List<ILYieldPoint> yieldPoints)
        {
            _spillTemps = new Dictionary<int, List<VariableDefinition>>();
            _spillSlotBase = new Dictionary<int, int>();
            _stackSlotBase = _capturableLocalIndices.Count;

            // Assign each yield point its own disjoint trailing slot range so the catch
            // block can pack every yield point's temps without collision.
            int nextBase = _stackSlotBase;
            foreach (var yp in yieldPoints)
            {
                var state = yp.StackState;
                int depth = state?.Depth ?? 0;
                var argumentTypes = yp.Kind == ILYieldPointKind.ContinuableCall
                    ? CallArgumentTypes((MethodReference)yp.Instruction.Operand)
                    : new List<TypeReference>();
                var temps = new List<VariableDefinition>(depth);
                for (int i = 0; i < depth; i++)
                {
                    // Types[] is bottom-to-top; pick the slot's type (widened to object
                    // where the simulator could not refine it). The top slots of a call
                    // site are the call's arguments; their declared types are exact.
                    int argumentIndex = i - (depth - argumentTypes.Count);
                    var slotType = argumentIndex >= 0 && argumentTypes[argumentIndex] != null
                        ? argumentTypes[argumentIndex]
                        : (state.Types != null && i < state.Types.Length && state.Types[i] != null)
                            ? state.Types[i]
                            : _module.TypeSystem.Object;
                    temps.Add(AddLocal(slotType));
                }
                _spillTemps[yp.Id] = temps;
                _spillSlotBase[yp.Id] = nextBase;
                nextBase += depth;
            }
            _totalSpillSlots = nextBase - _stackSlotBase;
        }

        /// <summary>
        /// Declared types of the operands a call pops (receiver first, then parameters),
        /// or null for an operand whose declared type is generic and so cannot be named
        /// here without inflating the signature.
        /// </summary>
        private List<TypeReference> CallArgumentTypes(MethodReference called)
        {
            var types = new List<TypeReference>();
            if (called.HasThis)
            {
                types.Add(called.DeclaringType.IsValueType || called.DeclaringType.ContainsGenericParameter
                    ? null
                    : _module.ImportReference(called.DeclaringType));
            }
            foreach (var p in called.Parameters)
            {
                types.Add(p.ParameterType.ContainsGenericParameter
                    ? null
                    : _module.ImportReference(p.ParameterType));
            }
            return types;
        }

        private void ImportReferences()
        {
            _scriptContextType = _module.ImportReference(typeof(object)); // placeholder
            var primRuntime = FindOrLoadAssembly("Prim.Runtime");
            var primCore = FindOrLoadAssembly("Prim.Core");

            if (primRuntime != null)
            {
                var scriptContextDef = primRuntime.MainModule.Types.FirstOrDefault(t => t.Name == "ScriptContext");
                if (scriptContextDef != null)
                {
                    _scriptContextType = _module.ImportReference(scriptContextDef);
                    _ensureCurrentMethod = TryImport(scriptContextDef.Methods.FirstOrDefault(m => m.Name == "EnsureCurrent"));
                    _handleYieldPointMethod = TryImport(scriptContextDef.Methods.FirstOrDefault(m =>
                        m.Name == "HandleYieldPoint" && m.Parameters.Count == 1));
                    _handleYieldPointWithBudgetMethod = TryImport(scriptContextDef.Methods.FirstOrDefault(m =>
                        m.Name == "HandleYieldPointWithBudget" && m.Parameters.Count == 2));
                    _isRestoringField = TryImport(scriptContextDef.Fields.FirstOrDefault(f => f.Name == "IsRestoring"));
                    _frameChainField = TryImport(scriptContextDef.Fields.FirstOrDefault(f => f.Name == "FrameChain"));
                }

                var frameCaptureClass = primRuntime.MainModule.Types.FirstOrDefault(t => t.Name == "FrameCapture");
                if (frameCaptureClass != null)
                {
                    _frameCaptureCaptureFrame = TryImport(frameCaptureClass.Methods.FirstOrDefault(m => m.Name == "CaptureFrame"));
                    _frameCaptureGetSlot = TryImport(frameCaptureClass.Methods.FirstOrDefault(m => m.Name == "GetSlot"));
                }
            }

            if (primCore != null)
            {
                var suspendExDef = primCore.MainModule.Types.FirstOrDefault(t => t.Name == "SuspendException");
                if (suspendExDef != null)
                {
                    _suspendExceptionType = _module.ImportReference(suspendExDef);
                    _suspendFrameChainGetter = TryImport(suspendExDef.Properties
                        .FirstOrDefault(p => p.Name == "FrameChain")?.GetMethod);
                    _suspendFrameChainSetter = TryImport(suspendExDef.Properties
                        .FirstOrDefault(p => p.Name == "FrameChain")?.SetMethod);
                }

                var frameRecordDef = primCore.MainModule.Types.FirstOrDefault(t => t.Name == "HostFrameRecord");
                if (frameRecordDef != null)
                {
                    _hostFrameRecordType = _module.ImportReference(frameRecordDef);
                    _methodTokenGetter = TryImport(frameRecordDef.Properties.FirstOrDefault(p => p.Name == "MethodToken")?.GetMethod);
                    _yieldPointIdGetter = TryImport(frameRecordDef.Properties.FirstOrDefault(p => p.Name == "YieldPointId")?.GetMethod);
                    _slotsGetter = TryImport(frameRecordDef.Properties.FirstOrDefault(p => p.Name == "Slots")?.GetMethod);
                    _callerGetter = TryImport(frameRecordDef.Properties.FirstOrDefault(p => p.Name == "Caller")?.GetMethod);
                }
            }
        }

        private MethodReference TryImport(MethodReference m) => m == null ? null : _module.ImportReference(m);
        private FieldReference TryImport(FieldReference f) => f == null ? null : _module.ImportReference(f);

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
                return resolver.Resolve(new AssemblyNameReference(name, new Version(1, 0, 0, 0)));
            }
            catch
            {
                return null;
            }
        }

        private VariableDefinition AddLocal(TypeReference type)
        {
            var local = new VariableDefinition(type);
            _method.Body.Variables.Add(local);
            return local;
        }

        // -- yield-point injection (spill + check + resume label) ---------------------

        /// <summary>
        /// For each yield point, rewrites the region around the suspending op into:
        /// <code>
        ///   [spill: stloc temps...]                  // drain live eval stack into temps
        ///   EnsureCurrent(); ldc id [cost]; Handle*  // yield check; may throw SuspendException
        ///   resume_i:                                // Nop; dispatch switch targets here
        ///   [reload: ldloc temps...]                 // re-push operands for the suspending op
        ///   &lt;suspending op&gt;                          // br (back-edge) / call ...
        /// </code>
        /// The yield check runs with an empty live-operand stack (operands are in temps),
        /// so the SuspendException unwind point has nothing extra to capture beyond the
        /// temps. The resume label sits AFTER the check and BEFORE the suspending op, so
        /// resuming does NOT re-invoke the check (avoids #32's re-invoke / budget thrash)
        /// yet still executes the suspending op exactly once. The reload re-pushes the
        /// operands on BOTH the normal and the resume path (on resume the temps have been
        /// repopulated from the frame slots by the pre-check). Returns a map from
        /// yield-point Id to its resume label.
        ///
        /// A call to another transformed method gets no yield check, only a resume label:
        /// <code>
        ///   [spill: stloc temps...]                  // includes the call's arguments
        ///   resume_i:
        ///   ldc id; stloc __yieldPoint               // after the label, so it also runs on resume
        ///   [reload: ldloc temps...]
        ///   call Callee                              // re-executed on resume
        /// </code>
        /// </summary>
        private Dictionary<int, Instruction> InjectYieldPointChecks(ILProcessor il, List<ILYieldPoint> yieldPoints)
        {
            var resumeLabels = new Dictionary<int, Instruction>();
            var costs = CalculateInstructionCosts(yieldPoints);

            var instructions = _method.Body.Instructions;
            var sortedYieldPoints = yieldPoints
                .OrderByDescending(yp => instructions.IndexOf(yp.Instruction))
                .ToList();

            foreach (var yp in sortedYieldPoints)
            {
                var suspendOp = yp.Instruction;
                var cost = costs.TryGetValue(yp.Id, out var c) ? c : 1;
                var temps = _spillTemps[yp.Id];

                var resumeLabel = il.Create(OpCodes.Nop);
                resumeLabels[yp.Id] = resumeLabel;

                var seq = new List<Instruction>();

                // Spill: drain the live eval stack top-first into temps. After this the
                // operand stack is empty so the yield check throws from an empty stack,
                // and the dispatch switch can branch to the resume label.
                for (int i = temps.Count - 1; i >= 0; i--)
                {
                    seq.Add(il.Create(OpCodes.Stloc, temps[i]));
                }

                if (yp.Kind == ILYieldPointKind.ContinuableCall)
                {
                    seq.Add(resumeLabel);
                    seq.Add(il.Create(OpCodes.Ldc_I4, yp.Id));
                    seq.Add(il.Create(OpCodes.Stloc, _yieldPointLocal));
                }
                else
                {
                    seq.Add(il.Create(OpCodes.Ldc_I4, yp.Id));
                    seq.Add(il.Create(OpCodes.Stloc, _yieldPointLocal));

                    // Yield check.
                    seq.Add(il.Create(OpCodes.Call, _ensureCurrentMethod));
                    seq.Add(il.Create(OpCodes.Ldc_I4, yp.Id));
                    if (_options.EnableInstructionCounting && _handleYieldPointWithBudgetMethod != null)
                    {
                        seq.Add(il.Create(OpCodes.Ldc_I4, cost));
                        seq.Add(il.Create(OpCodes.Callvirt, _handleYieldPointWithBudgetMethod));
                    }
                    else
                    {
                        seq.Add(il.Create(OpCodes.Callvirt, _handleYieldPointMethod));
                    }

                    seq.Add(resumeLabel);
                }

                // Reload operands for the suspending op.
                for (int i = 0; i < temps.Count; i++)
                {
                    seq.Add(il.Create(OpCodes.Ldloc, temps[i]));
                }

                var firstSeq = seq[0];
                foreach (var instr in seq)
                {
                    il.InsertBefore(suspendOp, instr);
                }

                // Incoming branches that targeted the suspending op must now enter at the
                // start of the spill/check sequence.
                UpdateBranchTargets(suspendOp, firstSeq);
            }

            return resumeLabels;
        }

        private Dictionary<int, int> CalculateInstructionCosts(List<ILYieldPoint> yieldPoints)
        {
            var costs = new Dictionary<int, int>();
            var instructions = _method.Body.Instructions;
            if (yieldPoints.Count == 0 || instructions.Count == 0) return costs;

            // Continuable call sites have no yield check, so they do not delimit budget
            // intervals.
            var sortedYieldPoints = yieldPoints
                .Where(yp => yp.Kind != ILYieldPointKind.ContinuableCall)
                .OrderBy(yp => instructions.IndexOf(yp.Instruction))
                .ToList();

            int previousIndex = 0;
            foreach (var yp in sortedYieldPoints)
            {
                int ypIndex = instructions.IndexOf(yp.Instruction);
                int count = ypIndex - previousIndex;
                costs[yp.Id] = Math.Max(1, count);
                previousIndex = ypIndex;
            }
            return costs;
        }

        // -- return rewriting (ret -> leave) ------------------------------------------

        /// <summary>
        /// Rewrites every original 'ret' to store the return value (if any) and 'leave'
        /// to a single real return. The real return instructions are CREATED here but NOT
        /// appended (the caller appends them after the catch handler so they live outside
        /// the try region). 'ret' inside a protected region is illegal CIL; 'leave' is the
        /// verifiable way out. Returns the entry instruction of the real-return sequence
        /// (the 'ldloc retLocal' for value-returning methods, else the 'ret' itself);
        /// <paramref name="realRet"/> receives the trailing 'ret'.
        /// </summary>
        private Instruction RewriteReturns(
            ILProcessor il, VariableDefinition retLocal, bool hasReturnValue, out Instruction realRet)
        {
            var body = _method.Body;
            var rets = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();

            realRet = il.Create(OpCodes.Ret);
            Instruction realRetEntry = hasReturnValue
                ? il.Create(OpCodes.Ldloc, retLocal)
                : realRet;

            foreach (var ret in rets)
            {
                var leave = il.Create(OpCodes.Leave, realRetEntry);
                if (hasReturnValue)
                {
                    // ret -> stloc retLocal; leave realRetEntry
                    var store = il.Create(OpCodes.Stloc, retLocal);
                    il.InsertBefore(ret, store);
                    UpdateBranchTargets(ret, store);
                }
                il.Replace(ret, leave);
                UpdateBranchTargets(ret, leave);
            }

            return realRetEntry;
        }

        // -- dispatch switch (top of try) ---------------------------------------------

        /// <summary>
        /// Inserts, immediately before the original first instruction, the dispatch
        /// sequence: ldloc __state; switch(resume labels). Falls through to the original
        /// body (state 0). The returned instruction is the start of the try region.
        /// </summary>
        private Instruction BuildDispatch(
            ILProcessor il,
            Instruction originalFirst,
            VariableDefinition stateLocal,
            Dictionary<int, Instruction> resumeLabels)
        {
            // Build the switch targets ordered by state value. __state = yieldPointId + 1,
            // so switch index s maps to yield point id == s (0-based switch operand index
            // equals state-1). switch(__state): index 0 corresponds to __state==0 (normal),
            // so we place a dummy that just falls through. We instead subtract 1.
            //
            // Simpler: switch on (__state - 1). When __state==0 the value is -1 and switch
            // ignores it (falls through). For __state==k (k>=1) it selects targets[k-1].
            int maxId = resumeLabels.Count == 0 ? -1 : resumeLabels.Keys.Max();
            var targets = new Instruction[maxId + 1];
            for (int id = 0; id <= maxId; id++)
            {
                targets[id] = resumeLabels.TryGetValue(id, out var lbl)
                    ? lbl
                    : originalFirst; // unused id -> harmless fall-to-body target
            }

            var seq = new List<Instruction>
            {
                il.Create(OpCodes.Ldloc, stateLocal),
                il.Create(OpCodes.Ldc_I4_1),
                il.Create(OpCodes.Sub),
                il.Create(OpCodes.Switch, targets),
            };

            // Insert the dispatch before the CURRENT first instruction of the body, not
            // before originalFirst. When yield point 0 sits at the original first
            // instruction, InjectYieldPointChecks has already inserted its spill/check/
            // resume sequence ahead of originalFirst; the dispatch (and therefore the try
            // region that begins at it) must precede that sequence so every resume label —
            // including yield point 0's — lives INSIDE the try. Otherwise the switch (inside
            // the try) would branch to a resume label sitting outside it (BranchOutOfTry).
            var bodyFirst = _method.Body.Instructions[0];
            var dispatchStart = seq[0];
            foreach (var instr in seq)
            {
                il.InsertBefore(bodyFirst, instr);
            }

            return dispatchStart;
        }

        // -- catch block --------------------------------------------------------------

        /// <summary>
        /// Builds the SuspendException catch handler that packs locals + spilled temps
        /// into the slots array, calls CaptureFrame, links the new record into the
        /// exception's FrameChain, then rethrows. Returns (handlerStart, trailingNop).
        /// The handler is appended right after the rewritten body; the caller appends the
        /// real return after it so the return lives outside the try region.
        /// </summary>
        private (Instruction catchStart, Instruction catchEnd) BuildCatchBlock(
            ILProcessor il,
            int methodToken,
            VariableDefinition exLocal,
            VariableDefinition recordLocal)
        {
            var body = _method.Body;
            var catchStart = il.Create(OpCodes.Stloc, exLocal); // exception is on stack

            var instrs = new List<Instruction> { catchStart };

            // Build slots: capturable locals first, then each yield point's spill temps
            // in its own disjoint trailing range.
            int slotCount = _stackSlotBase + _totalSpillSlots;

            // new object[slotCount]
            instrs.Add(il.Create(OpCodes.Ldc_I4, slotCount));
            instrs.Add(il.Create(OpCodes.Newarr, _module.TypeSystem.Object));

            // Store capturable locals into slots[0.._stackSlotBase).
            for (int s = 0; s < _capturableLocalIndices.Count; s++)
            {
                var local = body.Variables[_capturableLocalIndices[s]];
                instrs.Add(il.Create(OpCodes.Dup));
                instrs.Add(il.Create(OpCodes.Ldc_I4, s));
                instrs.Add(il.Create(OpCodes.Ldloc, local));
                if (local.VariableType.IsValueType)
                {
                    instrs.Add(il.Create(OpCodes.Box, local.VariableType));
                }
                instrs.Add(il.Create(OpCodes.Stelem_Ref));
            }

            // Store each yield point's spill temps into its OWN disjoint slot range.
            // Only one yield point is active at suspension time, so packing all of them
            // is harmless (the inactive ones hold default/zero values), and because each
            // range is disjoint, restore can read the active yield point's range without
            // an inactive yield point's defaults clobbering it. Temp k of yield point Y
            // goes to slot _spillSlotBase[Y] + k.
            foreach (var kvp in _spillTemps)
            {
                var temps = kvp.Value;
                var ypBase = _spillSlotBase[kvp.Key];
                for (int k = 0; k < temps.Count; k++)
                {
                    var temp = temps[k];
                    instrs.Add(il.Create(OpCodes.Dup));
                    instrs.Add(il.Create(OpCodes.Ldc_I4, ypBase + k));
                    instrs.Add(il.Create(OpCodes.Ldloc, temp));
                    if (temp.VariableType.IsValueType)
                    {
                        instrs.Add(il.Create(OpCodes.Box, temp.VariableType));
                    }
                    instrs.Add(il.Create(OpCodes.Stelem_Ref));
                }
            }

            // slots array is now on the stack. Build the CaptureFrame call:
            // CaptureFrame(methodToken, __yieldPoint, slots, __ex.FrameChain)
            // __yieldPoint is this frame's own yield point: the check that threw if this is
            // the innermost frame, otherwise the call site of the suspended callee.
            // Stack currently: [slots]. We need [methodToken, yieldPointId, slots, caller].
            // Stash slots into recordLocal-typed temp? Easier: reorder using locals is
            // messy; instead push the other args around it. We have slots on top; store
            // it temporarily.
            var slotsLocal = AddLocal(_module.TypeSystem.Object.MakeArrayType());
            instrs.Add(il.Create(OpCodes.Stloc, slotsLocal));

            instrs.Add(il.Create(OpCodes.Ldc_I4, methodToken));

            instrs.Add(il.Create(OpCodes.Ldloc, _yieldPointLocal));

            instrs.Add(il.Create(OpCodes.Ldloc, slotsLocal));

            instrs.Add(il.Create(OpCodes.Ldloc, exLocal));
            instrs.Add(il.Create(OpCodes.Callvirt, _suspendFrameChainGetter));

            instrs.Add(il.Create(OpCodes.Call, _frameCaptureCaptureFrame));
            instrs.Add(il.Create(OpCodes.Stloc, recordLocal));

            // __ex.FrameChain = __record;
            instrs.Add(il.Create(OpCodes.Ldloc, exLocal));
            instrs.Add(il.Create(OpCodes.Ldloc, recordLocal));
            instrs.Add(il.Create(OpCodes.Callvirt, _suspendFrameChainSetter));

            // rethrow (unconditional; ends the handler)
            instrs.Add(il.Create(OpCodes.Rethrow));

            // Append the handler right after the rewritten body.
            foreach (var instr in instrs)
            {
                il.Append(instr);
            }

            // The returned catchEnd is the rethrow; the real handler-end boundary is set
            // by the caller to the real-return instruction it appends next.
            return (catchStart, instrs[instrs.Count - 1]);
        }

        // -- restore pre-check (outside the try) --------------------------------------

        private void BuildRestorePreCheck(
            ILProcessor il,
            int methodToken,
            VariableDefinition contextLocal,
            VariableDefinition frameLocal,
            VariableDefinition stateLocal,
            Instruction dispatchStart)
        {
            var body = _method.Body;
            var pre = new List<Instruction>();

            // __context = ScriptContext.EnsureCurrent();
            pre.Add(il.Create(OpCodes.Call, _ensureCurrentMethod));
            pre.Add(il.Create(OpCodes.Stloc, contextLocal));

            // enterTry == dispatchStart (TryStart). Branching to it enters the try normally.
            // if (!__context.IsRestoring) goto enterTry;
            pre.Add(il.Create(OpCodes.Ldloc, contextLocal));
            pre.Add(il.Create(OpCodes.Ldfld, _isRestoringField));
            pre.Add(il.Create(OpCodes.Brfalse, dispatchStart));

            // __frame = __context.FrameChain; if (__frame == null) goto enterTry;
            pre.Add(il.Create(OpCodes.Ldloc, contextLocal));
            pre.Add(il.Create(OpCodes.Ldfld, _frameChainField));
            pre.Add(il.Create(OpCodes.Dup));
            pre.Add(il.Create(OpCodes.Stloc, frameLocal));
            pre.Add(il.Create(OpCodes.Brfalse, dispatchStart));

            // if (__frame.MethodToken != methodToken) goto enterTry;
            pre.Add(il.Create(OpCodes.Ldloc, frameLocal));
            pre.Add(il.Create(OpCodes.Callvirt, _methodTokenGetter));
            pre.Add(il.Create(OpCodes.Ldc_I4, methodToken));
            pre.Add(il.Create(OpCodes.Bne_Un, dispatchStart));

            // __context.FrameChain = __frame.Caller;
            pre.Add(il.Create(OpCodes.Ldloc, contextLocal));
            pre.Add(il.Create(OpCodes.Ldloc, frameLocal));
            pre.Add(il.Create(OpCodes.Callvirt, _callerGetter));
            pre.Add(il.Create(OpCodes.Stfld, _frameChainField));

            // __state = __frame.YieldPointId + 1;
            pre.Add(il.Create(OpCodes.Ldloc, frameLocal));
            pre.Add(il.Create(OpCodes.Callvirt, _yieldPointIdGetter));
            pre.Add(il.Create(OpCodes.Ldc_I4_1));
            pre.Add(il.Create(OpCodes.Add));
            pre.Add(il.Create(OpCodes.Stloc, stateLocal));

            // Restore capturable locals from slots[0.._stackSlotBase).
            for (int s = 0; s < _capturableLocalIndices.Count; s++)
            {
                var local = body.Variables[_capturableLocalIndices[s]];
                pre.Add(il.Create(OpCodes.Ldloc, frameLocal));
                pre.Add(il.Create(OpCodes.Callvirt, _slotsGetter));
                pre.Add(il.Create(OpCodes.Ldc_I4, s));
                var getSlot = new GenericInstanceMethod(_frameCaptureGetSlot);
                getSlot.GenericArguments.Add(local.VariableType);
                pre.Add(il.Create(OpCodes.Call, getSlot));
                pre.Add(il.Create(OpCodes.Stloc, local));
            }

            // Restore spilled temps for each yield point from its own disjoint slot range.
            // (Only the active yield point's temps will actually be consumed at its
            // resume label, but restoring all keeps indexing identical to capture.)
            foreach (var kvp in _spillTemps)
            {
                var temps = kvp.Value;
                var ypBase = _spillSlotBase[kvp.Key];
                for (int k = 0; k < temps.Count; k++)
                {
                    var temp = temps[k];
                    pre.Add(il.Create(OpCodes.Ldloc, frameLocal));
                    pre.Add(il.Create(OpCodes.Callvirt, _slotsGetter));
                    pre.Add(il.Create(OpCodes.Ldc_I4, ypBase + k));
                    var getSlot = new GenericInstanceMethod(_frameCaptureGetSlot);
                    getSlot.GenericArguments.Add(temp.VariableType);
                    pre.Add(il.Create(OpCodes.Call, getSlot));
                    pre.Add(il.Create(OpCodes.Stloc, temp));
                }
            }

            // if (__context.FrameChain != null) goto enterTry; else __context.IsRestoring = false;
            var afterClear = dispatchStart;
            pre.Add(il.Create(OpCodes.Ldloc, contextLocal));
            pre.Add(il.Create(OpCodes.Ldfld, _frameChainField));
            pre.Add(il.Create(OpCodes.Brtrue, afterClear));

            pre.Add(il.Create(OpCodes.Ldloc, contextLocal));
            pre.Add(il.Create(OpCodes.Ldc_I4_0));
            pre.Add(il.Create(OpCodes.Stfld, _isRestoringField));

            // Fall through into dispatchStart (the next instruction).
            var firstOriginal = dispatchStart;
            foreach (var instr in pre)
            {
                il.InsertBefore(firstOriginal, instr);
            }
        }

        // -- helpers ------------------------------------------------------------------

        private void UpdateBranchTargets(Instruction oldTarget, Instruction newTarget)
        {
            foreach (var instruction in _method.Body.Instructions)
            {
                if (ReferenceEquals(instruction.Operand, oldTarget))
                {
                    instruction.Operand = newTarget;
                }
                else if (instruction.Operand is Instruction[] targets)
                {
                    for (int i = 0; i < targets.Length; i++)
                    {
                        if (ReferenceEquals(targets[i], oldTarget))
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

        /// <summary>
        /// Computes a safe over-estimate of the max evaluation-stack depth by linearly
        /// summing per-instruction pushes/pops with handler-entry seeding, then padding.
        /// OptimizeMacros does not recompute MaxStackSize (#34).
        /// </summary>
        private int ComputeMaxStack()
        {
            int max = 0;
            int cur = 0;
            foreach (var instr in _method.Body.Instructions)
            {
                cur -= GetPopCount(instr);
                if (cur < 0) cur = 0;
                cur += GetPushCount(instr);
                if (cur > max) max = cur;
            }
            // Exception handler entry pushes the exception object (depth 1) which the
            // linear walk above does not model. Pad generously to stay a safe over-estimate.
            return max + 8;
        }

        private int GetPushCount(Instruction instr)
        {
            switch (instr.OpCode.StackBehaviourPush)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1: return 1;
                case StackBehaviour.Push1_push1: return 2;
                case StackBehaviour.Pushi:
                case StackBehaviour.Pushi8:
                case StackBehaviour.Pushr4:
                case StackBehaviour.Pushr8:
                case StackBehaviour.Pushref: return 1;
                case StackBehaviour.Varpush:
                    // newobj always pushes the constructed object (1), even though its
                    // ctor MethodReference has a void return type (#34 undercount).
                    if (instr.OpCode.Code == Code.Newobj)
                        return 1;
                    if (instr.OpCode.Code == Code.Calli)
                        return instr.Operand is CallSite cs && cs.ReturnType.FullName != "System.Void" ? 1 : 0;
                    if (instr.Operand is MethodReference m)
                        return m.ReturnType.FullName != "System.Void" ? 1 : 0;
                    return 1;
                default: return 0;
            }
        }

        private int GetPopCount(Instruction instr)
        {
            switch (instr.OpCode.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1:
                case StackBehaviour.Popi:
                case StackBehaviour.Popref: return 1;
                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Popi_pop1:
                case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4:
                case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1:
                case StackBehaviour.Popref_popi: return 2;
                case StackBehaviour.Popi_popi_popi:
                case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8:
                case StackBehaviour.Popref_popi_popref: return 3;
                case StackBehaviour.PopAll: return 0;
                case StackBehaviour.Varpop:
                    if (instr.OpCode.Code == Code.Ret)
                        return _method.ReturnType.FullName == "System.Void" ? 0 : 1;
                    if (instr.Operand is MethodReference m)
                    {
                        int count = m.Parameters.Count;
                        if (m.HasThis && instr.OpCode.Code != Code.Newobj) count++;
                        return count;
                    }
                    return 0;
                default: return 0;
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
