using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Disruptor.Analyzers;

/// <summary>
/// DISR002: <c>Next()</c> 로 시퀀스를 점유한 뒤 <c>Publish()</c> 가 <c>finally</c> 에 없는 경우를 경고합니다.
/// </summary>
/// <remarks>
/// 점유한 시퀀스를 게시하지 않으면 그 슬롯에서 <b>링 버퍼가 영구히 막힙니다</b> —
/// 게이팅이 전진하지 못해 모든 프로듀서가 결국 블록됩니다. 예외 경로에서도 반드시 게시해야 합니다.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PublishInFinallyAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.PublishNotInFinally,
        title: "Next() 후 Publish() 는 finally 에 있어야 합니다",
        messageFormat: "'{0}' 로 점유한 시퀀스가 finally 블록에서 게시되지 않습니다. " +
                       "예외가 발생하면 그 슬롯에서 링 버퍼가 영구히 막힙니다. " +
                       "try/finally 로 감싸거나 PublishEventScope() 를 사용하세요.",
        category: DiagnosticIds.Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "링 버퍼에서 점유한 시퀀스는 반드시 게시되어야 합니다. " +
                     "게시되지 않은 슬롯이 하나라도 있으면 게이팅이 전진하지 못합니다.",
        helpLinkUri: DiagnosticIds.HelpUriBase + "DISR002.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (!IsRingBufferMethod(context, invocation, "Next", out ITypeSymbol? ringType))
            return;

        // TryNext 는 실패를 반환값으로 알리므로 이 규칙의 대상이 아니다.
        // PublishEvent* / PublishEventScope 는 내부에서 finally 게시를 보장한다.

        // 같은 메서드 본문 안에 Publish 호출이 finally 블록에 있는지 확인한다.
        SyntaxNode? body = FindEnclosingBody(invocation);
        if (body is null)
            return;

        if (HasPublishInFinally(context, body, ringType!))
            return;

        // finally 가 아니어도 Publish 가 아예 없으면 더 명백한 문제지만,
        // 헬퍼 메서드로 위임하는 정상 패턴도 있으므로 "본문에 Publish 가 있는데 finally 가 아닌" 경우만 경고한다.
        if (!HasAnyPublish(context, body, ringType!))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule, invocation.GetLocation(), invocation.ToString()));
    }

    private static bool IsRingBufferMethod(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        string methodName,
        out ITypeSymbol? containingType)
    {
        containingType = null;

        if (invocation.Expression is not MemberAccessExpressionSyntax member)
            return false;

        if (member.Name.Identifier.ValueText != methodName)
            return false;

        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol;
        if (symbol is not IMethodSymbol methodSymbol)
            return false;

        INamedTypeSymbol? owner = methodSymbol.ContainingType?.OriginalDefinition;
        if (owner is null)
            return false;

        string name = owner.MetadataName;
        if (name != "RingBuffer`1" && name != "ValueRingBuffer`1")
            return false;

        if (owner.ContainingNamespace?.ToDisplayString() != "Disruptor")
            return false;

        containingType = owner;
        return true;
    }

    private static SyntaxNode? FindEnclosingBody(SyntaxNode node)
    {
        for (SyntaxNode? current = node; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case MethodDeclarationSyntax m:
                    return (SyntaxNode?)m.Body ?? m.ExpressionBody;
                case LocalFunctionStatementSyntax f:
                    return (SyntaxNode?)f.Body ?? f.ExpressionBody;
                case AnonymousFunctionExpressionSyntax a:
                    return a.Body;
                case ConstructorDeclarationSyntax c:
                    return (SyntaxNode?)c.Body ?? c.ExpressionBody;
            }
        }

        return null;
    }

    private static bool HasPublishInFinally(
        SyntaxNodeAnalysisContext context, SyntaxNode body, ITypeSymbol ringType)
    {
        foreach (FinallyClauseSyntax finallyClause in body.DescendantNodes().OfType<FinallyClauseSyntax>())
        {
            if (ContainsPublish(context, finallyClause, ringType))
                return true;
        }

        // using 스코프(PublishEventScope) 도 finally 와 동등하다.
        foreach (SyntaxNode node in body.DescendantNodes())
        {
            if (node is UsingStatementSyntax or LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 })
                return true;
        }

        return false;
    }

    private static bool HasAnyPublish(
        SyntaxNodeAnalysisContext context, SyntaxNode body, ITypeSymbol ringType)
        => ContainsPublish(context, body, ringType);

    private static bool ContainsPublish(
        SyntaxNodeAnalysisContext context, SyntaxNode scope, ITypeSymbol ringType)
    {
        foreach (InvocationExpressionSyntax invocation in
                 scope.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member)
                continue;

            if (!member.Name.Identifier.ValueText.StartsWith("Publish", System.StringComparison.Ordinal))
                continue;

            var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol;
            if (symbol is not IMethodSymbol methodSymbol)
                continue;

            if (SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingType?.OriginalDefinition, ringType))
                return true;
        }

        return false;
    }
}
