using System;
using System.Collections.Generic;
using Prim.Analysis;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Prim.Cecil
{
    /// <summary>
    /// Transforms a single method to add continuation support.
    /// </summary>
    internal sealed class MethodTransformer
    {
        private readonly MethodDefinition _method;
        private readonly RewriterOptions _options;
        private readonly YieldPointIdentifier _yieldPointIdentifier;

        public MethodTransformer(MethodDefinition method, RewriterOptions options)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _yieldPointIdentifier = new YieldPointIdentifier(method);
        }

        /// <summary>
        /// Transforms the method.
        /// </summary>
        public void Transform()
        {
            var yieldPoints = _yieldPointIdentifier.FindYieldPoints();
            if (yieldPoints.Count == 0) return; // Nothing to transform

            var il = _method.Body.GetILProcessor();

            // Compute method token
            var methodToken = GenerateMethodToken();

            // Step 1: Inject yield point checks
            InjectYieldPointChecks(il, yieldPoints);

            // Step 2: Wrap body in try-catch for state capture
            WrapInTryCatch(il, yieldPoints);

            // Step 3: Add restore block at method entry
            AddRestoreBlock(il, yieldPoints, methodToken);

            // Step 4: Fix branch targets after modification
            FixBranches(il);
        }

        private void InjectYieldPointChecks(ILProcessor il, List<ILYieldPoint> yieldPoints)
        {
            // For each yield point, inject a call to HandleYieldPoint
            // This is simplified - a full implementation would need careful
            // instruction insertion without breaking branch targets

            // Note: Modifying IL while iterating requires careful offset management
            // This is a simplified demonstration
        }

        private void WrapInTryCatch(ILProcessor il, List<ILYieldPoint> yieldPoints)
        {
            // Wrap the method body in:
            // try { <original body> }
            // catch (SuspendException ex) { <capture state, rethrow> }

            // This requires:
            // 1. Finding the first and last instructions of the body
            // 2. Creating exception handler entries
            // 3. Inserting catch block code

            // Simplified - full implementation would be much more complex
        }

        private void AddRestoreBlock(ILProcessor il, List<ILYieldPoint> yieldPoints, int methodToken)
        {
            // Add at method entry:
            // if (ScriptContext.Current.IsRestoring && ...)
            // {
            //     <restore locals>
            //     <switch to yield point>
            // }

            // This requires:
            // 1. Inserting instructions at the start
            // 2. Updating all branch targets that pointed to the old first instruction

            // Simplified - full implementation would need careful branch fixup
        }

        private void FixBranches(ILProcessor il)
        {
            // After modifying IL, branch targets may need to be updated
            // Cecil's SimplifyMacros/OptimizeMacros can help

            il.Body.SimplifyMacros();
            il.Body.OptimizeMacros();
        }

        private int GenerateMethodToken()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (_method.DeclaringType.FullName?.GetHashCode() ?? 0);
                hash = hash * 31 + (_method.Name?.GetHashCode() ?? 0);
                foreach (var param in _method.Parameters)
                {
                    hash = hash * 31 + (param.ParameterType.FullName?.GetHashCode() ?? 0);
                }
                return hash;
            }
        }
    }
}
