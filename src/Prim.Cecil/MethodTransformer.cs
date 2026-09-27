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
    /// A yield point inside a user <c>try</c> block cannot be reached that way: the
    /// dispatch sits outside the user's block. Each user try block that encloses a yield
    /// point therefore gets its own nested dispatch at its first instruction, and resume
    /// hops from dispatch to dispatch, entering each block at its start
    /// (<see cref="BuildTryDispatches"/>). The user's handlers on those blocks are rewritten
    /// so they never observe a suspension (<see cref="ProtectUserHandlers"/>). Yield points
    /// inside handlers, filters and lock bodies are not placed (see
    /// <see cref="YieldPointIdentifier"/>).
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

        // Frame-local "suspending" flag: set by a filter when a SuspendException unwinds
        // through a user try/finally, read by the guard at the top of the finally so the
        // finally does not run during a suspension. Null when no such try/finally encloses
        // a yield point.
        private VariableDefinition _suspendingLocal;

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

        // Candidate yield points that were not placed (inside a catch handler, finally,
        // fault, filter or lock body), described with their original IL offsets.
        public IReadOnlyList<string> SkippedYieldPoints { get; private set; } = Array.Empty<string>();

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
            SkippedYieldPoints = _yieldPointIdentifier.SkippedYieldPoints
                .Select(s => s.ToString())
                .ToList();
            if (yieldPoints.Count == 0) return;

            // #38: byref / pinned / pointer locals and ref/out parameters cannot be
            // round-tripped through object[] / GetSlot<T>. Rather than emit invalid IL,
            // skip the method and surface a diagnostic.
            if (!CanCaptureState(yieldPoints, out var reason))
            {
                SkipReason = reason;
                return;
            }

            // User try blocks that enclose a yield point, and each yield point's chain of
            // enclosing try blocks (outermost first). Computed on the original offsets,
            // before any instruction is inserted.
            var tryRegions = FindTryRegions(yieldPoints, out var tryChains);
            if (!CanProtectTryRegions(tryRegions, out reason))
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

            // A resume point inside a user try block cannot be reached by a branch from
            // outside it, so each such block gets a nested dispatch at its first
            // instruction. The method's dispatch jumps to the outermost enclosing block's
            // dispatch instead of to the resume label.
            var entryLabels = BuildTryDispatches(il, tryRegions, tryChains, stateLocal, resumeLabels);

            // Keep the user's own handlers from observing a suspension: catch clauses
            // that could catch SuspendException reject it, and finally/fault blocks do
            // not run while the frame is unwinding to suspend.
            ProtectUserHandlers(il, tryRegions);

            // Build the dispatch switch at the top of the try and wire resume labels.
            var dispatchStart = BuildDispatch(il, originalFirst, stateLocal, entryLabels);

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
        /// body (state 0). The returned instruction is the start of the try region. For a
        /// yield point inside a user try block, <paramref name="resumeLabels"/> holds the
        /// nested dispatch at the start of its outermost enclosing block rather than the
        /// resume label itself (see <see cref="BuildTryDispatches"/>).
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

        // -- user try blocks ----------------------------------------------------------

        /// <summary>
        /// A user try block that encloses at least one yield point. The handlers of a try
        /// with several catch clauses share one try range and form one region. C#
        /// compiles <c>try/catch/finally</c> to two regions: an inner try/catch nested in
        /// an outer try/finally, both starting at the same instruction.
        /// </summary>
        private sealed class TryRegion
        {
            public List<ExceptionHandler> Handlers { get; } = new List<ExceptionHandler>();

            // Original IL offsets of the try range [StartOffset, EndOffset).
            public int StartOffset { get; set; }
            public int EndOffset { get; set; }

            // First instruction of this region's nested dispatch (its new TryStart).
            public Instruction Dispatch { get; set; }
        }

        /// <summary>
        /// Finds the user try blocks that enclose a yield point, and for each such yield
        /// point the chain of enclosing blocks, outermost first. Yield points in handlers,
        /// filters and lock bodies were already rejected by the identifier, so every
        /// region here is entered only through its try block.
        /// </summary>
        private List<TryRegion> FindTryRegions(
            List<ILYieldPoint> yieldPoints, out Dictionary<int, List<TryRegion>> chains)
        {
            var all = new List<TryRegion>();
            foreach (var handler in _method.Body.ExceptionHandlers)
            {
                int start = handler.TryStart.Offset;
                int end = handler.TryEnd?.Offset ?? int.MaxValue;
                var region = all.FirstOrDefault(r => r.StartOffset == start && r.EndOffset == end);
                if (region == null)
                {
                    region = new TryRegion { StartOffset = start, EndOffset = end };
                    all.Add(region);
                }
                region.Handlers.Add(handler);
            }

            chains = new Dictionary<int, List<TryRegion>>();
            var used = new HashSet<TryRegion>();
            foreach (var yp in yieldPoints)
            {
                int offset = yp.Instruction.Offset;
                // Enclosing regions are properly nested, so ordering by start (then by
                // end, descending, for blocks sharing a start) gives outermost first.
                var chain = all
                    .Where(r => offset >= r.StartOffset && offset < r.EndOffset)
                    .OrderBy(r => r.StartOffset)
                    .ThenByDescending(r => r.EndOffset)
                    .ToList();
                if (chain.Count == 0) continue;

                chains[yp.Id] = chain;
                used.UnionWith(chain);
            }

            return all.Where(used.Contains)
                .OrderBy(r => r.StartOffset)
                .ThenByDescending(r => r.EndOffset)
                .ToList();
        }

        private static bool CanProtectTryRegions(List<TryRegion> regions, out string reason)
        {
            reason = null;
            foreach (var handler in regions.SelectMany(r => r.Handlers))
            {
                if (handler.TryEnd == null || handler.HandlerStart == null)
                {
                    reason = "a try block enclosing a yield point has no explicit end; transformation unsupported";
                    return false;
                }

                // ProtectUserHandlers branches to a filter's closing endfilter, which
                // ECMA-335 requires to be the filter's last instruction.
                if (handler.HandlerType == ExceptionHandlerType.Filter &&
                    handler.HandlerStart.Previous?.OpCode.Code != Code.Endfilter)
                {
                    reason = "an exception filter enclosing a yield point does not end with endfilter; transformation unsupported";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Inserts a nested dispatch at the first instruction of every user try block that
        /// encloses a yield point. ECMA-335 allows a protected block to be entered only at
        /// its first instruction, so resume reaches a yield point inside nested try blocks
        /// by hopping from dispatch to dispatch, one block at a time:
        /// <code>
        ///   .try {
        ///   dispatch_R:                                  // new TryStart of R
        ///       switch (__state - 1) {                   // one target per yield point id:
        ///           id in a try nested in R:  goto dispatch_child;   // enter at its start
        ///           id directly in R:         goto enter_id;
        ///           otherwise:                goto body; // not in R: normal entry
        ///       }
        ///       br body                                  // __state == 0: normal entry
        ///   enter_id:
        ///       __state = 0; br resume_id                // clear so a later entry into
        ///                                                //   any try block runs normally
        ///   body:                                        // original first instruction
        ///       ...
        ///   }
        /// </code>
        /// On the normal path the dispatch costs one switch and one branch per entry into
        /// the block. Clearing <c>__state</c> matters when the block is re-entered, e.g.
        /// on the next iteration of an enclosing loop. Returns, per yield point, the
        /// target for the method-level dispatch: the outermost enclosing block's dispatch,
        /// or the resume label itself for a yield point outside any user try block.
        /// </summary>
        private Dictionary<int, Instruction> BuildTryDispatches(
            ILProcessor il,
            List<TryRegion> regions,
            Dictionary<int, List<TryRegion>> chains,
            VariableDefinition stateLocal,
            Dictionary<int, Instruction> resumeLabels)
        {
            var entryLabels = new Dictionary<int, Instruction>(resumeLabels);
            if (regions.Count == 0) return entryLabels;

            int maxId = resumeLabels.Keys.Max();

            // Create every dispatch head first so an outer dispatch can target an inner one.
            foreach (var region in regions)
            {
                region.Dispatch = il.Create(OpCodes.Ldloc, stateLocal);
            }

            // Regions are ordered outermost first. Blocks that share a first instruction
            // then stack up as [dispatch_outer][dispatch_inner][body], and each inner
            // block starts at its own dispatch, inside the outer one.
            foreach (var region in regions)
            {
                var body = region.Handlers[0].TryStart;
                var targets = new Instruction[maxId + 1];
                var trampolines = new List<Instruction>();

                for (int id = 0; id <= maxId; id++)
                {
                    targets[id] = body;
                    if (!chains.TryGetValue(id, out var chain)) continue;

                    int depth = chain.IndexOf(region);
                    if (depth < 0) continue;

                    if (depth < chain.Count - 1)
                    {
                        targets[id] = chain[depth + 1].Dispatch;
                    }
                    else
                    {
                        var enter = il.Create(OpCodes.Ldc_I4_0);
                        trampolines.Add(enter);
                        trampolines.Add(il.Create(OpCodes.Stloc, stateLocal));
                        trampolines.Add(il.Create(OpCodes.Br, resumeLabels[id]));
                        targets[id] = enter;
                    }
                }

                var seq = new List<Instruction>
                {
                    region.Dispatch,
                    il.Create(OpCodes.Ldc_I4_1),
                    il.Create(OpCodes.Sub),
                    il.Create(OpCodes.Switch, targets),
                };
                if (trampolines.Count > 0)
                {
                    seq.Add(il.Create(OpCodes.Br, body));
                    seq.AddRange(trampolines);
                }

                foreach (var instr in seq)
                {
                    il.InsertBefore(body, instr);
                }

                // A region that ended at the old first instruction (a preceding sibling
                // try or handler) now ends at the dispatch, which belongs to this block.
                ReplaceRegionEnds(body, region.Dispatch);
                foreach (var handler in region.Handlers)
                {
                    handler.TryStart = region.Dispatch;
                }

                // Branches from outside the block that entered it at its first instruction
                // must now enter at the dispatch. Branches from inside the block (a loop
                // back to its first instruction) keep their target and skip the dispatch.
                RetargetEntryBranches(body, region.Dispatch, region.Handlers[0].TryEnd);
            }

            foreach (var kvp in chains)
            {
                entryLabels[kvp.Key] = kvp.Value[0].Dispatch;
            }
            return entryLabels;
        }

        /// <summary>
        /// Redirects every branch to <paramref name="oldTarget"/> whose source lies outside
        /// the block [<paramref name="blockStart"/>, <paramref name="blockEnd"/>).
        /// </summary>
        private void RetargetEntryBranches(Instruction oldTarget, Instruction blockStart, Instruction blockEnd)
        {
            var instructions = _method.Body.Instructions;
            int start = instructions.IndexOf(blockStart);
            int end = instructions.IndexOf(blockEnd);

            for (int i = 0; i < instructions.Count; i++)
            {
                if (i >= start && i < end) continue;

                var instruction = instructions[i];
                if (ReferenceEquals(instruction.Operand, oldTarget))
                {
                    instruction.Operand = blockStart;
                }
                else if (instruction.Operand is Instruction[] targets)
                {
                    for (int t = 0; t < targets.Length; t++)
                    {
                        if (ReferenceEquals(targets[t], oldTarget))
                        {
                            targets[t] = blockStart;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Moves every region END (TryEnd, HandlerEnd) at <paramref name="oldEnd"/> to
        /// <paramref name="newEnd"/>, after code has been inserted before
        /// <paramref name="oldEnd"/> that must not belong to those regions. Region starts
        /// are left alone.
        /// </summary>
        private void ReplaceRegionEnds(Instruction oldEnd, Instruction newEnd)
        {
            foreach (var handler in _method.Body.ExceptionHandlers)
            {
                if (handler.TryEnd == oldEnd) handler.TryEnd = newEnd;
                if (handler.HandlerEnd == oldEnd) handler.HandlerEnd = newEnd;
            }
        }

        /// <summary>
        /// Keeps the handlers of every user try block that encloses a yield point from
        /// observing a suspension, so that a suspended-then-resumed method behaves as if it
        /// had never suspended:
        /// <list type="bullet">
        /// <item>A catch clause whose type could catch <c>SuspendException</c>
        /// (<c>SuspendException</c>, <c>Exception</c> or <c>object</c>, i.e. a bare catch)
        /// becomes a filter that rejects it. A filter runs in the first pass of exception
        /// dispatch, so the handler never runs and the exception travels on to the
        /// method-level capture catch. A rethrowing <c>catch (SuspendException)</c> added
        /// inside the user's try would not work: its rethrow would still be inside the
        /// user's try, and the user's catch would take it from there.</item>
        /// <item>A user filter gets a prefix that rejects <c>SuspendException</c> before
        /// the user's filter code runs.</item>
        /// <item>A finally or fault block gets a guard that skips it while the frame is
        /// unwinding to suspend. The flag it reads is set by a filter wrapped around the
        /// try block. Filters run before any finally in the second pass, so the flag is
        /// set in time. The finally runs once, when the method really leaves the block
        /// after resuming (or on a real exception).</item>
        /// </list>
        /// </summary>
        private void ProtectUserHandlers(ILProcessor il, List<TryRegion> regions)
        {
            foreach (var handler in regions.SelectMany(r => r.Handlers).ToList())
            {
                switch (handler.HandlerType)
                {
                    case ExceptionHandlerType.Catch:
                        if (CanCatchSuspendException(handler.CatchType))
                        {
                            ConvertCatchToFilter(il, handler);
                        }
                        break;

                    case ExceptionHandlerType.Filter:
                        PrependSuspendRejection(il, handler);
                        break;

                    case ExceptionHandlerType.Finally:
                    case ExceptionHandlerType.Fault:
                        GuardFinally(il, handler);
                        break;
                }
            }
        }

        private bool CanCatchSuspendException(TypeReference catchType)
        {
            if (catchType == null) return false;
            var name = catchType.FullName;
            return name == "System.Object" ||
                   name == "System.Exception" ||
                   name == _suspendExceptionType.FullName;
        }

        /// <summary>
        /// Rewrites <c>catch (T)</c> into a filter that accepts T except SuspendException:
        /// <code>
        ///   filter {
        ///       dup; isinst SuspendException; brtrue reject
        ///       isinst T; ldnull; cgt.un; br done        // (pop; ldc.i4.1 when T is object)
        ///   reject:
        ///       pop; ldc.i4.0
        ///   done:
        ///       endfilter
        ///   }
        ///   { castclass T; ...original handler... }      // a filter's handler receives object
        /// </code>
        /// </summary>
        private void ConvertCatchToFilter(ILProcessor il, ExceptionHandler handler)
        {
            var catchType = handler.CatchType;
            var handlerStart = handler.HandlerStart;
            bool catchesObject = catchType.FullName == "System.Object";

            var reject = il.Create(OpCodes.Pop);
            var done = il.Create(OpCodes.Endfilter);
            var filter = new List<Instruction>
            {
                il.Create(OpCodes.Dup),
                il.Create(OpCodes.Isinst, _suspendExceptionType),
                il.Create(OpCodes.Brtrue, reject),
            };
            if (catchesObject)
            {
                filter.Add(il.Create(OpCodes.Pop));
                filter.Add(il.Create(OpCodes.Ldc_I4_1));
            }
            else
            {
                filter.Add(il.Create(OpCodes.Isinst, catchType));
                filter.Add(il.Create(OpCodes.Ldnull));
                filter.Add(il.Create(OpCodes.Cgt_Un));
            }
            filter.Add(il.Create(OpCodes.Br, done));
            filter.Add(reject);
            filter.Add(il.Create(OpCodes.Ldc_I4_0));
            filter.Add(done);

            foreach (var instr in filter)
            {
                il.InsertBefore(handlerStart, instr);
            }
            ReplaceRegionEnds(handlerStart, filter[0]);

            handler.HandlerType = ExceptionHandlerType.Filter;
            handler.FilterStart = filter[0];
            handler.CatchType = null;

            if (!catchesObject)
            {
                var cast = il.Create(OpCodes.Castclass, catchType);
                il.InsertBefore(handlerStart, cast);
                handler.HandlerStart = cast;
            }
        }

        /// <summary>
        /// Prefixes a user filter with a SuspendException rejection:
        /// <code>
        ///   dup; isinst SuspendException; brfalse userFilter
        ///   pop; ldc.i4.0; br endfilter                  // the filter's own endfilter
        ///   userFilter: ...original filter...
        /// </code>
        /// </summary>
        private void PrependSuspendRejection(ILProcessor il, ExceptionHandler handler)
        {
            var userFilter = handler.FilterStart;
            var endFilter = handler.HandlerStart.Previous;

            var prefix = new List<Instruction>
            {
                il.Create(OpCodes.Dup),
                il.Create(OpCodes.Isinst, _suspendExceptionType),
                il.Create(OpCodes.Brfalse, userFilter),
                il.Create(OpCodes.Pop),
                il.Create(OpCodes.Ldc_I4_0),
                il.Create(OpCodes.Br, endFilter),
            };
            foreach (var instr in prefix)
            {
                il.InsertBefore(userFilter, instr);
            }
            ReplaceRegionEnds(userFilter, prefix[0]);
            handler.FilterStart = prefix[0];
        }

        /// <summary>
        /// Stops a finally (or fault) block from running while the frame unwinds to
        /// suspend. The try block is wrapped in a filter clause that never accepts but
        /// records that a SuspendException is passing, and the finally checks that flag
        /// first:
        /// <code>
        ///   .try {                                       // user try/finally
        ///     .try {                                     // added, same first instruction
        ///       ...user try block...
        ///     }
        ///     filter {
        ///       isinst SuspendException; brfalse no
        ///       ldc.i4.1; stloc __suspending
        ///     no:
        ///       ldc.i4.0; endfilter                      // never handles
        ///     } { pop; rethrow }                         // unreachable
        ///   }
        ///   finally {
        ///     ldloc __suspending; brfalse body; endfinally
        ///   body:
        ///     ...user finally...
        ///   }
        /// </code>
        /// The flag is never cleared: once set, the frame is on its way out, and a resumed
        /// invocation starts with it false (InitLocals).
        /// </summary>
        private void GuardFinally(ILProcessor il, ExceptionHandler handler)
        {
            var body = _method.Body;
            if (_suspendingLocal == null)
            {
                _suspendingLocal = AddLocal(_module.TypeSystem.Boolean);
            }

            // Guard at the top of the finally.
            var finallyBody = handler.HandlerStart;
            var guard = new List<Instruction>
            {
                il.Create(OpCodes.Ldloc, _suspendingLocal),
                il.Create(OpCodes.Brfalse, finallyBody),
                il.Create(OpCodes.Endfinally),
            };
            foreach (var instr in guard)
            {
                il.InsertBefore(finallyBody, instr);
            }
            ReplaceRegionEnds(finallyBody, guard[0]);
            handler.HandlerStart = guard[0];

            // The flag-setting filter clause, placed at the end of the user's try block
            // (which ends in an unconditional transfer, so nothing falls into it).
            var finallyStart = handler.HandlerStart;
            var noSuspend = il.Create(OpCodes.Ldc_I4_0);
            var filterHandler = il.Create(OpCodes.Pop);
            var clause = new List<Instruction>
            {
                il.Create(OpCodes.Isinst, _suspendExceptionType),
                il.Create(OpCodes.Brfalse, noSuspend),
                il.Create(OpCodes.Ldc_I4_1),
                il.Create(OpCodes.Stloc, _suspendingLocal),
                noSuspend,
                il.Create(OpCodes.Endfilter),
                filterHandler,
                il.Create(OpCodes.Rethrow),
            };
            foreach (var instr in clause)
            {
                il.InsertBefore(finallyStart, instr);
            }

            // Regions nested in the user's try that ended where the finally begins now end
            // before the filter clause; the user's try itself still ends at the finally.
            ReplaceRegionEnds(finallyStart, clause[0]);
            handler.TryEnd = finallyStart;

            var flagClause = new ExceptionHandler(ExceptionHandlerType.Filter)
            {
                TryStart = handler.TryStart,
                TryEnd = clause[0],
                FilterStart = clause[0],
                HandlerStart = filterHandler,
                HandlerEnd = finallyStart,
            };

            // The exception table lists inner clauses before outer ones.
            body.ExceptionHandlers.Insert(body.ExceptionHandlers.IndexOf(handler), flagClause);
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
