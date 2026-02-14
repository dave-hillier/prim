using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Prim.Roslyn
{
    /// <summary>
    /// Represents a state in the generated state machine.
    /// Each yield point results in a distinct state.
    /// </summary>
    internal class StateInfo
    {
        public int StateId { get; set; }
        public string Label { get; set; }
        public YieldPointKind Kind { get; set; }
        public SyntaxNode OriginalNode { get; set; }
        public List<string> StatementsAfterYield { get; set; } = new List<string>();
    }

    /// <summary>
    /// Information about a hoisted local variable.
    /// </summary>
    internal class HoistedLocal
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public int SlotIndex { get; set; }
        public bool IsParameter { get; set; }
    }

    /// <summary>
    /// Context for try-catch-finally block transformation.
    /// </summary>
    internal class TryBlockContext
    {
        public int TryStateStart { get; set; }
        public int TryStateEnd { get; set; }
        public List<CatchClauseInfo> CatchClauses { get; set; } = new List<CatchClauseInfo>();
        public int? FinallyStateStart { get; set; }
        public int? FinallyStateEnd { get; set; }
        public int ExitState { get; set; }
    }

    internal class CatchClauseInfo
    {
        public string ExceptionType { get; set; }
        public string ExceptionVariableName { get; set; }
        public int StateStart { get; set; }
        public int StateEnd { get; set; }
    }

    /// <summary>
    /// Rewrites a method body into a state machine that supports suspension and resumption.
    ///
    /// The transformation converts control flow constructs (loops, try-catch, switch) into
    /// explicit state transitions. Each yield point becomes a distinct state, and locals
    /// are hoisted to persist across suspension.
    ///
    /// This is similar to how C#'s async/await transformation works, but designed for
    /// serializable continuations rather than async tasks.
    /// </summary>
    internal class StateMachineRewriter
    {
        private readonly MethodDeclarationSyntax _method;
        private readonly SemanticModel _semanticModel;
        private readonly int _methodToken;

        private readonly List<StateInfo> _states = new List<StateInfo>();
        private readonly List<HoistedLocal> _hoistedLocals = new List<HoistedLocal>();
        private readonly List<TryBlockContext> _tryContexts = new List<TryBlockContext>();
        private readonly StringBuilder _output = new StringBuilder();

        private int _nextStateId = 0;
        private int _indentLevel = 0;
        private int _nextLocalSlot = 0;

        public StateMachineRewriter(MethodDeclarationSyntax method, SemanticModel semanticModel, int methodToken)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
            _semanticModel = semanticModel;
            _methodToken = methodToken;
        }

        /// <summary>
        /// Generates the state machine body for the method.
        /// </summary>
        public string GenerateStateMachineBody(string returnType, bool isVoid)
        {
            _output.Clear();
            _states.Clear();
            _hoistedLocals.Clear();
            _nextStateId = 0;
            _nextLocalSlot = 0;

            // Analyze the method to find all locals and yield points
            AnalyzeMethod();

            // Generate prologue
            GeneratePrologue(returnType, isVoid);

            // Generate state machine loop
            GenerateStateMachineLoop(returnType, isVoid);

            // Generate epilogue
            GenerateEpilogue(returnType, isVoid);

            return _output.ToString();
        }

        private void AnalyzeMethod()
        {
            // First pass: collect all local variables that need to be hoisted
            CollectLocals(_method.Body);

            // Also hoist parameters
            foreach (var param in _method.ParameterList.Parameters)
            {
                var paramName = param.Identifier.Text;
                var paramType = param.Type?.ToString() ?? "object";

                _hoistedLocals.Add(new HoistedLocal
                {
                    Name = $"__param_{paramName}",
                    Type = paramType,
                    SlotIndex = _nextLocalSlot++,
                    IsParameter = true
                });
            }
        }

        private void CollectLocals(SyntaxNode node)
        {
            if (node == null) return;

            if (node is LocalDeclarationStatementSyntax localDecl)
            {
                var typeName = localDecl.Declaration.Type.ToString();
                // Handle var keyword - default to object for simplicity
                // A full implementation would use semantic model to infer type
                if (typeName == "var")
                {
                    typeName = InferType(localDecl);
                }

                foreach (var variable in localDecl.Declaration.Variables)
                {
                    _hoistedLocals.Add(new HoistedLocal
                    {
                        Name = variable.Identifier.Text,
                        Type = typeName,
                        SlotIndex = _nextLocalSlot++,
                        IsParameter = false
                    });
                }
            }
            else if (node is ForStatementSyntax forStmt)
            {
                // Handle loop variable declaration
                if (forStmt.Declaration != null)
                {
                    var typeName = forStmt.Declaration.Type.ToString();
                    if (typeName == "var") typeName = "int"; // Common case

                    foreach (var variable in forStmt.Declaration.Variables)
                    {
                        if (!_hoistedLocals.Any(l => l.Name == variable.Identifier.Text))
                        {
                            _hoistedLocals.Add(new HoistedLocal
                            {
                                Name = variable.Identifier.Text,
                                Type = typeName,
                                SlotIndex = _nextLocalSlot++,
                                IsParameter = false
                            });
                        }
                    }
                }
            }
            else if (node is ForEachStatementSyntax foreachStmt)
            {
                var typeName = foreachStmt.Type.ToString();
                if (typeName == "var") typeName = "object";

                var varName = foreachStmt.Identifier.Text;
                if (!_hoistedLocals.Any(l => l.Name == varName))
                {
                    _hoistedLocals.Add(new HoistedLocal
                    {
                        Name = varName,
                        Type = typeName,
                        SlotIndex = _nextLocalSlot++,
                        IsParameter = false
                    });
                }

                // Add enumerator variable
                _hoistedLocals.Add(new HoistedLocal
                {
                    Name = $"__enumerator_{varName}",
                    Type = "System.Collections.IEnumerator",
                    SlotIndex = _nextLocalSlot++,
                    IsParameter = false
                });
            }
            else if (node is CatchClauseSyntax catchClause)
            {
                if (catchClause.Declaration != null)
                {
                    var exName = catchClause.Declaration.Identifier.Text;
                    var exType = catchClause.Declaration.Type.ToString();

                    if (!string.IsNullOrEmpty(exName) && !_hoistedLocals.Any(l => l.Name == exName))
                    {
                        _hoistedLocals.Add(new HoistedLocal
                        {
                            Name = exName,
                            Type = exType,
                            SlotIndex = _nextLocalSlot++,
                            IsParameter = false
                        });
                    }
                }
            }

            foreach (var child in node.ChildNodes())
            {
                CollectLocals(child);
            }
        }

        private string InferType(LocalDeclarationStatementSyntax localDecl)
        {
            // Try to infer type from initializer
            var variable = localDecl.Declaration.Variables.FirstOrDefault();
            if (variable?.Initializer?.Value != null)
            {
                var init = variable.Initializer.Value;

                // Handle common literal types
                if (init is LiteralExpressionSyntax literal)
                {
                    return literal.Kind() switch
                    {
                        SyntaxKind.NumericLiteralExpression => init.ToString().Contains('.') ? "double" : "int",
                        SyntaxKind.StringLiteralExpression => "string",
                        SyntaxKind.TrueLiteralExpression => "bool",
                        SyntaxKind.FalseLiteralExpression => "bool",
                        SyntaxKind.NullLiteralExpression => "object",
                        SyntaxKind.CharacterLiteralExpression => "char",
                        _ => "object"
                    };
                }

                // Handle object creation
                if (init is ObjectCreationExpressionSyntax objCreate)
                {
                    return objCreate.Type.ToString();
                }

                // Handle array creation
                if (init is ArrayCreationExpressionSyntax arrayCreate)
                {
                    return arrayCreate.Type.ToString();
                }

                // Handle implicit array
                if (init is ImplicitArrayCreationExpressionSyntax)
                {
                    return "object[]";
                }
            }

            return "object";
        }

        private void GeneratePrologue(string returnType, bool isVoid)
        {
            WriteLine("var __context = ScriptContext.EnsureCurrent();");
            WriteLine($"const int __methodToken = {_methodToken};");
            WriteLine("int __state = 0;");
            WriteLine($"{(isVoid ? "object" : returnType)} __result = default;");
            WriteLine("Exception __pendingException = null;");
            WriteLine();

            // Declare all hoisted locals
            foreach (var local in _hoistedLocals)
            {
                if (!local.IsParameter)
                {
                    WriteLine($"{local.Type} {local.Name} = default({local.Type});");
                }
                else
                {
                    // Copy parameter to hoisted local
                    var originalParamName = local.Name.Substring("__param_".Length);
                    WriteLine($"{local.Type} {local.Name} = {originalParamName};");
                }
            }
            WriteLine();

            // Restore block - restore state from frame chain
            WriteLine("// Restore state if resuming");
            WriteLine("if (__context.IsRestoring && __context.FrameChain?.MethodToken == __methodToken)");
            WriteLine("{");
            _indentLevel++;

            WriteLine("var __frame = __context.FrameChain;");
            WriteLine("__context.FrameChain = __frame.Caller;");
            WriteLine("__state = __frame.YieldPointId + 1;");
            WriteLine();

            // Restore hoisted locals from slots
            for (int i = 0; i < _hoistedLocals.Count; i++)
            {
                var local = _hoistedLocals[i];
                WriteLine($"{local.Name} = FrameCapture.GetSlot<{local.Type}>(__frame.Slots, {i});");
            }
            WriteLine();

            WriteLine("if (__context.FrameChain == null)");
            WriteLine("    __context.IsRestoring = false;");

            _indentLevel--;
            WriteLine("}");
            WriteLine();
        }

        private void GenerateStateMachineLoop(string returnType, bool isVoid)
        {
            WriteLine("// State machine");
            WriteLine("try");
            WriteLine("{");
            _indentLevel++;

            WriteLine("while (true)");
            WriteLine("{");
            _indentLevel++;

            WriteLine("switch (__state)");
            WriteLine("{");
            _indentLevel++;

            // Generate state 0: initial state
            WriteLine("case 0:");
            _indentLevel++;

            // Transform the method body
            if (_method.Body != null)
            {
                TransformBlock(_method.Body, isVoid, returnType);
            }
            else if (_method.ExpressionBody != null)
            {
                var expr = _method.ExpressionBody.Expression.ToString();
                if (isVoid)
                {
                    WriteLine($"{expr};");
                    WriteLine("goto __exit;");
                }
                else
                {
                    WriteLine($"__result = {expr};");
                    WriteLine("goto __exit;");
                }
            }

            _indentLevel--;

            // Generate additional states for yield points
            GenerateYieldPointStates(isVoid, returnType);

            // Default case - should never happen
            WriteLine("default:");
            _indentLevel++;
            WriteLine("goto __exit;");
            _indentLevel--;

            _indentLevel--;
            WriteLine("}"); // end switch

            _indentLevel--;
            WriteLine("}"); // end while

            WriteLine("__exit:;");

            _indentLevel--;
            WriteLine("}"); // end try

            // Catch block for state capture
            GenerateCatchBlock();
        }

        private void GenerateCatchBlock()
        {
            WriteLine("catch (SuspendException __suspendEx)");
            WriteLine("{");
            _indentLevel++;

            // Pack all hoisted locals into slots array
            Write("var __slots = FrameCapture.PackSlots(");
            if (_hoistedLocals.Count > 0)
            {
                Write(string.Join(", ", _hoistedLocals.Select(l => l.Name)));
            }
            WriteLine(");");

            WriteLine("var __record = FrameCapture.CaptureFrame(__methodToken, __suspendEx.YieldPointId, __slots, __suspendEx.FrameChain);");
            WriteLine("__suspendEx.FrameChain = __record;");
            WriteLine("throw;");

            _indentLevel--;
            WriteLine("}");
        }

        private void GenerateEpilogue(string returnType, bool isVoid)
        {
            WriteLine();
            if (isVoid)
            {
                WriteLine("return;");
            }
            else
            {
                WriteLine("return __result;");
            }
        }

        private void GenerateYieldPointStates(bool isVoid, string returnType)
        {
            foreach (var state in _states)
            {
                WriteLine($"case {state.StateId}: // {state.Kind} - resume after yield point {state.StateId - 1}");
                _indentLevel++;

                foreach (var stmt in state.StatementsAfterYield)
                {
                    WriteLine(stmt);
                }

                _indentLevel--;
            }
        }

        private void TransformBlock(BlockSyntax block, bool isVoid, string returnType)
        {
            foreach (var statement in block.Statements)
            {
                TransformStatement(statement, isVoid, returnType);
            }

            // If block doesn't end with return, add goto exit
            var lastStatement = block.Statements.LastOrDefault();
            if (lastStatement != null &&
                !(lastStatement is ReturnStatementSyntax) &&
                !(lastStatement is ThrowStatementSyntax))
            {
                WriteLine("goto __exit;");
            }
        }

        private void TransformStatement(StatementSyntax statement, bool isVoid, string returnType)
        {
            switch (statement)
            {
                case LocalDeclarationStatementSyntax localDecl:
                    TransformLocalDeclaration(localDecl);
                    break;

                case ExpressionStatementSyntax exprStmt:
                    TransformExpressionStatement(exprStmt);
                    break;

                case ReturnStatementSyntax returnStmt:
                    TransformReturnStatement(returnStmt, isVoid);
                    break;

                case IfStatementSyntax ifStmt:
                    TransformIfStatement(ifStmt, isVoid, returnType);
                    break;

                case WhileStatementSyntax whileStmt:
                    TransformWhileStatement(whileStmt, isVoid, returnType);
                    break;

                case DoStatementSyntax doStmt:
                    TransformDoStatement(doStmt, isVoid, returnType);
                    break;

                case ForStatementSyntax forStmt:
                    TransformForStatement(forStmt, isVoid, returnType);
                    break;

                case ForEachStatementSyntax foreachStmt:
                    TransformForEachStatement(foreachStmt, isVoid, returnType);
                    break;

                case TryStatementSyntax tryStmt:
                    TransformTryStatement(tryStmt, isVoid, returnType);
                    break;

                case SwitchStatementSyntax switchStmt:
                    TransformSwitchStatement(switchStmt, isVoid, returnType);
                    break;

                case BlockSyntax blockStmt:
                    TransformBlock(blockStmt, isVoid, returnType);
                    break;

                case BreakStatementSyntax:
                    WriteLine("break;");
                    break;

                case ContinueStatementSyntax:
                    WriteLine("continue;");
                    break;

                case ThrowStatementSyntax throwStmt:
                    WriteLine(throwStmt.ToFullString().TrimEnd());
                    break;

                case EmptyStatementSyntax:
                    // Skip empty statements
                    break;

                case LabeledStatementSyntax labelStmt:
                    WriteLine($"{labelStmt.Identifier.Text}:");
                    TransformStatement(labelStmt.Statement, isVoid, returnType);
                    break;

                case GotoStatementSyntax gotoStmt:
                    WriteLine(gotoStmt.ToFullString().TrimEnd());
                    break;

                case LockStatementSyntax lockStmt:
                    TransformLockStatement(lockStmt, isVoid, returnType);
                    break;

                case UsingStatementSyntax usingStmt:
                    TransformUsingStatement(usingStmt, isVoid, returnType);
                    break;

                default:
                    // For unsupported statements, emit as-is
                    WriteLine(statement.ToFullString().TrimEnd());
                    break;
            }
        }

        private void TransformLocalDeclaration(LocalDeclarationStatementSyntax localDecl)
        {
            // Locals are hoisted, so just emit assignments
            foreach (var variable in localDecl.Declaration.Variables)
            {
                if (variable.Initializer != null)
                {
                    WriteLine($"{variable.Identifier.Text} = {variable.Initializer.Value};");
                }
            }
        }

        private void TransformExpressionStatement(ExpressionStatementSyntax exprStmt)
        {
            // Check if this is a call to a continuable method
            var expr = exprStmt.Expression;

            if (IsYieldCall(expr))
            {
                EmitYieldPoint(YieldPointKind.ExplicitYield, exprStmt);
            }
            else
            {
                WriteLine(exprStmt.ToFullString().TrimEnd());
            }
        }

        private bool IsYieldCall(ExpressionSyntax expr)
        {
            if (expr is InvocationExpressionSyntax invocation)
            {
                var methodName = GetMethodName(invocation);
                return methodName == "Yield" ||
                       methodName == "CheckYield" ||
                       methodName.EndsWith(".Yield") ||
                       methodName.EndsWith(".CheckYield") ||
                       methodName == "ScriptContext.Yield";
            }
            return false;
        }

        private string GetMethodName(InvocationExpressionSyntax invocation)
        {
            return invocation.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax ma => ma.ToString(),
                _ => ""
            };
        }

        private void TransformReturnStatement(ReturnStatementSyntax returnStmt, bool isVoid)
        {
            if (returnStmt.Expression != null)
            {
                WriteLine($"__result = {returnStmt.Expression};");
            }
            WriteLine("goto __exit;");
        }

        private void TransformIfStatement(IfStatementSyntax ifStmt, bool isVoid, string returnType)
        {
            WriteLine($"if ({ifStmt.Condition})");
            WriteLine("{");
            _indentLevel++;

            if (ifStmt.Statement is BlockSyntax block)
            {
                foreach (var stmt in block.Statements)
                {
                    TransformStatement(stmt, isVoid, returnType);
                }
            }
            else
            {
                TransformStatement(ifStmt.Statement, isVoid, returnType);
            }

            _indentLevel--;
            WriteLine("}");

            if (ifStmt.Else != null)
            {
                WriteLine("else");
                WriteLine("{");
                _indentLevel++;

                if (ifStmt.Else.Statement is BlockSyntax elseBlock)
                {
                    foreach (var stmt in elseBlock.Statements)
                    {
                        TransformStatement(stmt, isVoid, returnType);
                    }
                }
                else
                {
                    TransformStatement(ifStmt.Else.Statement, isVoid, returnType);
                }

                _indentLevel--;
                WriteLine("}");
            }
        }

        private void TransformWhileStatement(WhileStatementSyntax whileStmt, bool isVoid, string returnType)
        {
            var loopStartLabel = $"__while_{_nextStateId}";
            var loopEndLabel = $"__whileEnd_{_nextStateId}";

            WriteLine($"{loopStartLabel}:");

            // Emit yield point at loop back-edge
            EmitYieldPoint(YieldPointKind.LoopBackEdge, whileStmt);

            WriteLine($"if (!({whileStmt.Condition}))");
            WriteLine($"    goto {loopEndLabel};");

            // Transform loop body
            if (whileStmt.Statement is BlockSyntax block)
            {
                foreach (var stmt in block.Statements)
                {
                    TransformStatement(stmt, isVoid, returnType);
                }
            }
            else
            {
                TransformStatement(whileStmt.Statement, isVoid, returnType);
            }

            WriteLine($"goto {loopStartLabel};");
            WriteLine($"{loopEndLabel}:;");
        }

        private void TransformDoStatement(DoStatementSyntax doStmt, bool isVoid, string returnType)
        {
            var loopStartLabel = $"__do_{_nextStateId}";
            var loopCheckLabel = $"__doCheck_{_nextStateId}";

            WriteLine($"{loopStartLabel}:");

            // Transform loop body
            if (doStmt.Statement is BlockSyntax block)
            {
                foreach (var stmt in block.Statements)
                {
                    TransformStatement(stmt, isVoid, returnType);
                }
            }
            else
            {
                TransformStatement(doStmt.Statement, isVoid, returnType);
            }

            WriteLine($"{loopCheckLabel}:");

            // Emit yield point at loop back-edge
            EmitYieldPoint(YieldPointKind.LoopBackEdge, doStmt);

            WriteLine($"if ({doStmt.Condition})");
            WriteLine($"    goto {loopStartLabel};");
        }

        private void TransformForStatement(ForStatementSyntax forStmt, bool isVoid, string returnType)
        {
            var loopStartLabel = $"__for_{_nextStateId}";
            var loopEndLabel = $"__forEnd_{_nextStateId}";

            // Emit initializers (locals are already hoisted)
            if (forStmt.Declaration != null)
            {
                foreach (var variable in forStmt.Declaration.Variables)
                {
                    if (variable.Initializer != null)
                    {
                        WriteLine($"{variable.Identifier.Text} = {variable.Initializer.Value};");
                    }
                }
            }
            foreach (var initializer in forStmt.Initializers)
            {
                WriteLine($"{initializer};");
            }

            WriteLine($"{loopStartLabel}:");

            // Emit yield point at loop back-edge
            EmitYieldPoint(YieldPointKind.LoopBackEdge, forStmt);

            // Emit condition check
            if (forStmt.Condition != null)
            {
                WriteLine($"if (!({forStmt.Condition}))");
                WriteLine($"    goto {loopEndLabel};");
            }

            // Transform loop body
            if (forStmt.Statement is BlockSyntax block)
            {
                foreach (var stmt in block.Statements)
                {
                    TransformStatement(stmt, isVoid, returnType);
                }
            }
            else
            {
                TransformStatement(forStmt.Statement, isVoid, returnType);
            }

            // Emit incrementors
            foreach (var incrementor in forStmt.Incrementors)
            {
                WriteLine($"{incrementor};");
            }

            WriteLine($"goto {loopStartLabel};");
            WriteLine($"{loopEndLabel}:;");
        }

        private void TransformForEachStatement(ForEachStatementSyntax foreachStmt, bool isVoid, string returnType)
        {
            var iterVar = foreachStmt.Identifier.Text;
            var enumeratorVar = $"__enumerator_{iterVar}";
            var loopStartLabel = $"__foreach_{_nextStateId}";
            var loopEndLabel = $"__foreachEnd_{_nextStateId}";

            // Get enumerator
            WriteLine($"{enumeratorVar} = ({foreachStmt.Expression}).GetEnumerator();");
            WriteLine("try");
            WriteLine("{");
            _indentLevel++;

            WriteLine($"{loopStartLabel}:");

            // Emit yield point at loop back-edge
            EmitYieldPoint(YieldPointKind.LoopBackEdge, foreachStmt);

            WriteLine($"if (!{enumeratorVar}.MoveNext())");
            WriteLine($"    goto {loopEndLabel};");

            var elemType = foreachStmt.Type.ToString();
            if (elemType == "var")
            {
                WriteLine($"{iterVar} = ({_hoistedLocals.FirstOrDefault(l => l.Name == iterVar)?.Type ?? "object"}){enumeratorVar}.Current;");
            }
            else
            {
                WriteLine($"{iterVar} = ({elemType}){enumeratorVar}.Current;");
            }

            // Transform loop body
            if (foreachStmt.Statement is BlockSyntax block)
            {
                foreach (var stmt in block.Statements)
                {
                    TransformStatement(stmt, isVoid, returnType);
                }
            }
            else
            {
                TransformStatement(foreachStmt.Statement, isVoid, returnType);
            }

            WriteLine($"goto {loopStartLabel};");
            WriteLine($"{loopEndLabel}:;");

            _indentLevel--;
            WriteLine("}");
            WriteLine("finally");
            WriteLine("{");
            _indentLevel++;
            WriteLine($"if ({enumeratorVar} is System.IDisposable __disposable)");
            WriteLine("    __disposable.Dispose();");
            _indentLevel--;
            WriteLine("}");
        }

        private void TransformTryStatement(TryStatementSyntax tryStmt, bool isVoid, string returnType)
        {
            // For try-catch-finally, we need to be careful about yielding inside try blocks.
            // The CLR doesn't allow yield inside finally blocks, so we'll emit a warning/error
            // if we detect a yield point inside finally.

            WriteLine("try");
            WriteLine("{");
            _indentLevel++;

            // Transform try block
            foreach (var stmt in tryStmt.Block.Statements)
            {
                TransformStatement(stmt, isVoid, returnType);
            }

            _indentLevel--;
            WriteLine("}");

            // Transform catch clauses
            foreach (var catchClause in tryStmt.Catches)
            {
                var exType = catchClause.Declaration?.Type.ToString() ?? "Exception";
                var exVar = catchClause.Declaration?.Identifier.Text;

                if (!string.IsNullOrEmpty(exVar))
                {
                    WriteLine($"catch ({exType} {exVar})");
                }
                else if (catchClause.Declaration != null)
                {
                    WriteLine($"catch ({exType})");
                }
                else
                {
                    WriteLine("catch");
                }

                if (catchClause.Filter != null)
                {
                    WriteLine($"when ({catchClause.Filter.FilterExpression})");
                }

                WriteLine("{");
                _indentLevel++;

                // Transform catch body
                foreach (var stmt in catchClause.Block.Statements)
                {
                    TransformStatement(stmt, isVoid, returnType);
                }

                _indentLevel--;
                WriteLine("}");
            }

            // Transform finally block
            if (tryStmt.Finally != null)
            {
                WriteLine("finally");
                WriteLine("{");
                _indentLevel++;

                // NOTE: Yielding inside finally is not supported - just emit the code as-is
                foreach (var stmt in tryStmt.Finally.Block.Statements)
                {
                    // Check for yield points - emit warning
                    if (ContainsYieldPoint(stmt))
                    {
                        WriteLine("// WARNING: Yielding inside finally block is not supported");
                    }
                    WriteLine(stmt.ToFullString().TrimEnd());
                }

                _indentLevel--;
                WriteLine("}");
            }
        }

        private bool ContainsYieldPoint(StatementSyntax statement)
        {
            // Check if statement contains loops or explicit yield calls
            return statement.DescendantNodes().Any(n =>
                n is WhileStatementSyntax ||
                n is ForStatementSyntax ||
                n is ForEachStatementSyntax ||
                n is DoStatementSyntax ||
                (n is InvocationExpressionSyntax inv && IsYieldCall(inv)));
        }

        private void TransformSwitchStatement(SwitchStatementSyntax switchStmt, bool isVoid, string returnType)
        {
            WriteLine($"switch ({switchStmt.Expression})");
            WriteLine("{");
            _indentLevel++;

            foreach (var section in switchStmt.Sections)
            {
                foreach (var label in section.Labels)
                {
                    if (label is CaseSwitchLabelSyntax caseLabel)
                    {
                        WriteLine($"case {caseLabel.Value}:");
                    }
                    else if (label is CasePatternSwitchLabelSyntax patternLabel)
                    {
                        var pattern = patternLabel.Pattern.ToString();
                        if (patternLabel.WhenClause != null)
                        {
                            WriteLine($"case {pattern} when {patternLabel.WhenClause.Condition}:");
                        }
                        else
                        {
                            WriteLine($"case {pattern}:");
                        }
                    }
                    else if (label is DefaultSwitchLabelSyntax)
                    {
                        WriteLine("default:");
                    }
                }

                _indentLevel++;
                foreach (var stmt in section.Statements)
                {
                    TransformStatement(stmt, isVoid, returnType);
                }
                _indentLevel--;
            }

            _indentLevel--;
            WriteLine("}");
        }

        private void TransformLockStatement(LockStatementSyntax lockStmt, bool isVoid, string returnType)
        {
            // Lock statements cannot have yield points inside them safely
            // We emit as-is with a warning if yield points are detected
            if (ContainsYieldPoint(lockStmt.Statement))
            {
                WriteLine("// WARNING: Yielding inside lock block may cause issues");
            }

            WriteLine($"lock ({lockStmt.Expression})");
            WriteLine("{");
            _indentLevel++;

            if (lockStmt.Statement is BlockSyntax block)
            {
                foreach (var stmt in block.Statements)
                {
                    TransformStatement(stmt, isVoid, returnType);
                }
            }
            else
            {
                TransformStatement(lockStmt.Statement, isVoid, returnType);
            }

            _indentLevel--;
            WriteLine("}");
        }

        private void TransformUsingStatement(UsingStatementSyntax usingStmt, bool isVoid, string returnType)
        {
            // Using statements with yield points need special handling
            if (usingStmt.Declaration != null)
            {
                foreach (var variable in usingStmt.Declaration.Variables)
                {
                    var varName = variable.Identifier.Text;
                    var init = variable.Initializer?.Value.ToString() ?? "null";

                    // Add to hoisted locals if not already present
                    if (!_hoistedLocals.Any(l => l.Name == varName))
                    {
                        var typeName = usingStmt.Declaration.Type.ToString();
                        _hoistedLocals.Add(new HoistedLocal
                        {
                            Name = varName,
                            Type = typeName,
                            SlotIndex = _nextLocalSlot++,
                            IsParameter = false
                        });
                    }

                    WriteLine($"{varName} = {init};");
                }
            }
            else if (usingStmt.Expression != null)
            {
                var tempVar = $"__using_{_nextStateId}";
                WriteLine($"var {tempVar} = {usingStmt.Expression};");
            }

            WriteLine("try");
            WriteLine("{");
            _indentLevel++;

            if (usingStmt.Statement is BlockSyntax block)
            {
                foreach (var stmt in block.Statements)
                {
                    TransformStatement(stmt, isVoid, returnType);
                }
            }
            else
            {
                TransformStatement(usingStmt.Statement, isVoid, returnType);
            }

            _indentLevel--;
            WriteLine("}");
            WriteLine("finally");
            WriteLine("{");
            _indentLevel++;

            if (usingStmt.Declaration != null)
            {
                foreach (var variable in usingStmt.Declaration.Variables)
                {
                    var varName = variable.Identifier.Text;
                    WriteLine($"if ({varName} != null) ((System.IDisposable){varName}).Dispose();");
                }
            }
            else if (usingStmt.Expression != null)
            {
                var tempVar = $"__using_{_nextStateId}";
                WriteLine($"if ({tempVar} != null) ((System.IDisposable){tempVar}).Dispose();");
            }

            _indentLevel--;
            WriteLine("}");
        }

        private void EmitYieldPoint(YieldPointKind kind, SyntaxNode node)
        {
            var yieldPointId = _nextStateId++;
            var cost = EstimateNodeCost(node);

            WriteLine($"// Yield point {yieldPointId} ({kind})");
            WriteLine($"__context.HandleYieldPointWithBudget({yieldPointId}, {cost});");

            // Create a state for resuming after this yield point
            var state = new StateInfo
            {
                StateId = yieldPointId + 1,
                Label = $"__resume_{yieldPointId}",
                Kind = kind,
                OriginalNode = node
            };
            _states.Add(state);
        }

        private int EstimateNodeCost(SyntaxNode node)
        {
            // Estimate the instruction cost based on node complexity
            var descendantCount = node.DescendantNodes().Count();
            return Math.Max(1, Math.Min(descendantCount, 100));
        }

        private void Write(string text)
        {
            _output.Append(text);
        }

        private void WriteLine(string line = "")
        {
            if (string.IsNullOrEmpty(line))
            {
                _output.AppendLine();
            }
            else
            {
                _output.Append(new string(' ', _indentLevel * 4));
                _output.AppendLine(line);
            }
        }
    }
}
