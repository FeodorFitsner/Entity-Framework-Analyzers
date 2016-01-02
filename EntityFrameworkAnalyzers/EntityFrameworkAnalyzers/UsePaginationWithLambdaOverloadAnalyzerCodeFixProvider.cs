﻿using System;
using System.Composition;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;

namespace EntityFrameworkAnalyzers
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UsePaginationWithLambdaOverloadAnalyzerCodeFixProvider)), Shared]
    public class UsePaginationWithLambdaOverloadAnalyzerCodeFixProvider : CodeFixProvider
    {
        public sealed override ImmutableArray<string> FixableDiagnosticIds
        {
            get { return ImmutableArray.Create(UsePaginationWithLambdaOverloadAnalyzer.DiagnosticId); }
        }

        public sealed override FixAllProvider GetFixAllProvider()
        {
            return WellKnownFixAllProviders.BatchFixer;
        }

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

            var diagnostic = context.Diagnostics.First();
            var diagnosticSpan = diagnostic.Location.SourceSpan;


            var semanticModelAsync = await context.Document.GetSemanticModelAsync(context.CancellationToken);

            // Find the type declaration identified by the diagnostic.
            var invocations = root.FindToken(diagnosticSpan.Start).Parent.AncestorsAndSelf().OfType<InvocationExpressionSyntax>();

            var declaration = invocations.First(syntax =>
            {
                var name = semanticModelAsync.GetSymbolInfo(syntax).Symbol?.Name;
                return name == "Take" || name == "Skip";
            });

            // Register a code action that will invoke the fix.
            context.RegisterCodeFix(CodeAction.Create(Resources.ChangeWithLambda, c => LiteralToLambdaAsync(context.Document, declaration, c), "EF1002CodeFixProvider"), diagnostic);
        }

        private async Task<Document> LiteralToLambdaAsync(Document document, InvocationExpressionSyntax invocationExpr, CancellationToken cancellationToken)
        {
            var documentEditor = await DocumentEditor.CreateAsync(document, cancellationToken);

            var variableName = await document.FindAvailabeVariableName(invocationExpr, cancellationToken);

            var expressionSyntax = invocationExpr.ArgumentList.Arguments[0].Expression;

            var identifierNameSyntax = expressionSyntax as IdentifierNameSyntax;

            if (identifierNameSyntax == null)
            {
                var literalExpression = expressionSyntax as LiteralExpressionSyntax;

                var skipExpression = literalExpression != null ? literalExpression.Token.ValueText : expressionSyntax.ToString();

                var lambda = SyntaxFactory.ParseStatement($"var {variableName} = {skipExpression};")
                                              .WithAdditionalAnnotations(Formatter.Annotation).WithTrailingTrivia(SyntaxFactory.EndOfLine("\r"));

                var node = invocationExpr.Parent;

                while (!node.Parent.IsKind(SyntaxKind.Block))
                {
                    node = node.Parent;
                }

                documentEditor.InsertBefore(node, lambda);

                documentEditor.ReplaceNode(expressionSyntax, SyntaxFactory.ParseExpression($"() => {variableName}"));
            }
            else
            {
                documentEditor.ReplaceNode(expressionSyntax, SyntaxFactory.ParseExpression($"() => {identifierNameSyntax.Identifier.Text}"));
            }

            var newRoot = documentEditor.GetChangedRoot() as CompilationUnitSyntax;

            newRoot = newRoot.AddUsings("System.Data.Entity");

            return document.WithSyntaxRoot(newRoot);
        }
    }
}