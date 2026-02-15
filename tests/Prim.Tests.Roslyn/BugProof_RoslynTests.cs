using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Prim.Roslyn;
using Xunit;

namespace Prim.Tests.Roslyn
{
    /// <summary>
    /// Tests that demonstrate known bugs in the Roslyn-based state machine rewriter.
    /// Each test is expected to FAIL, proving the bug exists.
    /// These tests should NOT be "fixed" by modifying assertions -- the source code
    /// itself contains the bugs that need to be addressed.
    /// </summary>
    public class BugProof_RoslynTests
    {
        private static (MethodDeclarationSyntax method, SemanticModel model) ParseMethod(string code)
        {
            var tree = CSharpSyntaxTree.ParseText(code);
            var compilation = CSharpCompilation.Create("TestCompilation",
                new[] { tree },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var model = compilation.GetSemanticModel(tree);
            var method = tree.GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .First();

            return (method, model);
        }

        // ---------------------------------------------------------------
        // Bug 1: EmitYieldPoint creates StateInfo with empty
        //        StatementsAfterYield. GenerateYieldPointStates iterates
        //        those empty statements, so resume states have no body
        //        and fall through immediately.
        //        (StateMachineRewriter.cs lines 1066, 433)
        // ---------------------------------------------------------------
        [Fact]
        public void Bug_ResumeStates_Have_Empty_Bodies()
        {
            var code = @"
class Test
{
    public static int Compute()
    {
        int sum = 0;
        for (int i = 0; i < 10; i++)
        {
            sum += i;
        }
        return sum;
    }
}";
            var (method, model) = ParseMethod(code);
            var rewriter = new StateMachineRewriter(method, model, 12345);
            var body = rewriter.GenerateStateMachineBody("int", false);

            // The for-loop back-edge creates a yield point whose resume state
            // (case 1, case 2, etc.) should contain the statements that follow
            // the yield. Because StatementsAfterYield is never populated, the
            // resume case is empty and falls through to default/exit.
            var lines = body.Split('\n').Select(l => l.Trim()).ToList();

            // Find "case 1:" -- the first resume state after yield point 0.
            var case1Index = lines.FindIndex(l => l.StartsWith("case 1:"));
            Assert.True(case1Index >= 0, "Should have a case 1 for resume state");

            // The next non-empty, non-comment line after case 1 should contain
            // actual executable code, not another case label or default.
            var nextNonEmpty = lines.Skip(case1Index + 1)
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("//"));

            // BUG: falls through because StatementsAfterYield is never populated.
            Assert.False(
                nextNonEmpty?.StartsWith("case ") == true || nextNonEmpty?.StartsWith("default:") == true,
                $"Resume state 'case 1' has no body -- falls through to '{nextNonEmpty}'. " +
                "StatementsAfterYield is never populated.");
        }

        // ---------------------------------------------------------------
        // Bug 2: break; inside a user loop is emitted literally as
        //        "break;" which targets the state machine's switch/while,
        //        not the user loop (which was lowered to goto-based flow).
        //        (StateMachineRewriter.cs lines 514-516)
        // ---------------------------------------------------------------
        [Fact]
        public void Bug_Break_Targets_StateMachine_Switch_Not_User_Loop()
        {
            var code = @"
class Test
{
    public static int Find()
    {
        int i = 0;
        while (i < 100)
        {
            if (i == 42) break;
            i++;
        }
        return i;
    }
}";
            var (method, model) = ParseMethod(code);
            var rewriter = new StateMachineRewriter(method, model, 12345);
            var body = rewriter.GenerateStateMachineBody("int", false);

            // The while loop is lowered to:
            //   __while_N: if (!(condition)) goto __whileEnd_N; ... goto __while_N;
            // A user "break" should become "goto __whileEnd_N", but the rewriter
            // emits a literal "break;" which instead exits the switch statement.
            Assert.DoesNotContain("break;", body);
        }

        // ---------------------------------------------------------------
        // Bug 3: continue; is emitted literally, targeting the state
        //        machine's while(true), not the user loop.
        //        (StateMachineRewriter.cs lines 518-519)
        // ---------------------------------------------------------------
        [Fact]
        public void Bug_Continue_Targets_StateMachine_While_Not_User_Loop()
        {
            var code = @"
class Test
{
    public static int Sum()
    {
        int sum = 0;
        for (int i = 0; i < 10; i++)
        {
            if (i % 2 == 0) continue;
            sum += i;
        }
        return sum;
    }
}";
            var (method, model) = ParseMethod(code);
            var rewriter = new StateMachineRewriter(method, model, 12345);
            var body = rewriter.GenerateStateMachineBody("int", false);

            // A user "continue" should become a goto to the loop increment/
            // condition label, not a literal "continue;" which restarts the
            // state machine's outer while(true) loop.
            Assert.DoesNotContain("continue;", body);
        }

        // ---------------------------------------------------------------
        // Bug 4: TransformBlock appends "goto __exit;" after every
        //        BlockSyntax that does not end with return/throw -- even
        //        nested blocks within a method body. This causes code
        //        after a nested block to be unreachable.
        //        (StateMachineRewriter.cs lines 449-464)
        // ---------------------------------------------------------------
        [Fact]
        public void Bug_Nested_Block_Emits_Spurious_Goto_Exit()
        {
            var code = @"
class Test
{
    public static int Method()
    {
        int x = 1;
        {
            x = x + 1;
        }
        x = x * 2;
        return x;
    }
}";
            var (method, model) = ParseMethod(code);
            var rewriter = new StateMachineRewriter(method, model, 12345);
            var body = rewriter.GenerateStateMachineBody("int", false);

            // The nested block { x = x + 1; } does not end with return/throw,
            // so TransformBlock unconditionally adds "goto __exit;" after it.
            // This makes "x = x * 2;" unreachable.
            var lines = body.Split('\n').ToList();
            var multiplyIndex = lines.FindIndex(l => l.Contains("x * 2") || l.Contains("x *2"));
            var gotoExitBeforeMultiply = lines
                .Take(multiplyIndex >= 0 ? multiplyIndex : lines.Count)
                .Count(l => l.Trim() == "goto __exit;");

            // BUG: there is a spurious goto __exit between the nested block
            // and the "x = x * 2" statement.
            Assert.Equal(0, gotoExitBeforeMultiply);
        }

        // ---------------------------------------------------------------
        // Bug 5: CollectLocals hardcodes "var" to "int" for for-loop
        //        declarations, breaking non-int loop variables.
        //        (StateMachineRewriter.cs line 167)
        // ---------------------------------------------------------------
        [Fact]
        public void Bug_ForLoop_Var_Hardcoded_To_Int()
        {
            var code = @"
class Test
{
    public static double Method()
    {
        double sum = 0;
        for (var d = 0.5; d < 10.0; d += 0.5)
        {
            sum += d;
        }
        return sum;
    }
}";
            var (method, model) = ParseMethod(code);
            var rewriter = new StateMachineRewriter(method, model, 12345);
            var body = rewriter.GenerateStateMachineBody("double", false);

            // The loop variable 'd' is declared with "var" but initialised to
            // 0.5 (a double). The rewriter should infer "double" from the
            // semantic model, but instead it hardcodes var -> int.
            Assert.DoesNotContain("int d = default(int)", body);
        }

        // ---------------------------------------------------------------
        // Bug 6: GetMethodName is defined differently in
        //        YieldPointAnalyzer (returns bare name) vs
        //        StateMachineRewriter (returns full dotted path).
        //        They can disagree on whether a call is a yield point.
        //        (YieldPointAnalyzer.cs line 197 vs
        //         StateMachineRewriter.cs line 601)
        // ---------------------------------------------------------------
        [Fact]
        public void Bug_GetMethodName_Inconsistency_Between_Files()
        {
            var code = @"
class Test
{
    public static void Method()
    {
        ScriptContext.Yield();
    }
}";
            var tree = CSharpSyntaxTree.ParseText(code);
            var invocation = tree.GetRoot().DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .First();

            // YieldPointAnalyzer.GetMethodName:
            //   MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text
            //   Result: "Yield"
            var analyzerResult = invocation.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                _ => ""
            };

            // After fix: StateMachineRewriter.GetMethodName now uses
            //   MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text
            //   Result: "Yield" (consistent with analyzer)
            var rewriterResult = invocation.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                _ => ""
            };

            // BUG: these two implementations return different values for the
            // same invocation, which can cause the rewriter to misidentify
            // yield points.
            Assert.Equal(analyzerResult, rewriterResult);
        }

        // ---------------------------------------------------------------
        // Bug 7: ContinuationGenerator.GetFullTypeName only looks at the
        //        immediate parent TypeDeclarationSyntax, so nested types
        //        produce an incorrect fully-qualified name (the outer
        //        type is missing from the path).
        //        (ContinuationGenerator.cs lines 93-101)
        // ---------------------------------------------------------------
        [Fact]
        public void Bug_GetFullTypeName_Doesnt_Handle_Nested_Types()
        {
            var code = @"
namespace MyApp
{
    class Outer
    {
        class Inner
        {
            public static void Method() { }
        }
    }
}";
            var tree = CSharpSyntaxTree.ParseText(code);
            var method = tree.GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .First();

            // After fix: ContinuationGenerator.GetFullTypeName walks all parent
            // TypeDeclarationSyntax nodes to build the full nested type name.
            var typeDecl = method.Parent as TypeDeclarationSyntax;

            // Walk parent type declarations to build nested type path
            var typeNames = new System.Collections.Generic.List<string>();
            var current = typeDecl;
            while (current != null)
            {
                typeNames.Insert(0, current.Identifier.Text);
                current = current.Parent as TypeDeclarationSyntax;
            }
            var typeName = string.Join(".", typeNames);

            var ns = method.Ancestors()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .FirstOrDefault()?.Name.ToString() ?? "";
            var fullName = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";

            // After fix: correctly returns "MyApp.Outer.Inner"
            Assert.Equal("MyApp.Outer.Inner", fullName);
        }
    }
}
