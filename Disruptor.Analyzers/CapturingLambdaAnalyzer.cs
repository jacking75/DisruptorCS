using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Disruptor.Analyzers;

/// <summary>
/// DISR003: <c>PublishEvent</c> 에 캡처하는 람다를 넘기면 게시마다 할당이 발생함을 알립니다.
/// </summary>
/// <remarks>
/// 이 라이브러리의 핵심 가치인 "정상 경로 할당 0"이 사용자 코드 한 줄로 무너지는 지점입니다.
/// <c>static</c> 람다 + <c>TArg</c> 오버로드나 <c>IEventTranslator</c> 구조체를 쓰면 할당이 사라집니다.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CapturingLambdaAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.CapturingLambdaInPublish,
        title: "PublishEvent 에 캡처하는 람다를 넘기면 게시마다 할당이 발생합니다",
        messageFormat: "'{0}' 에 넘긴 람다가 바깥 변수를 캡처합니다. 게시 1건마다 클로저와 델리게이트가 " +
                       "할당되어 \"정상 경로 할당 0\" 계약이 깨집니다. " +
                       "static 람다 + TArg 오버로드나 IEventTranslator 구조체를 사용하세요.",
        category: "Disruptor.Performance",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "게시 경로는 이 라이브러리에서 가장 뜨거운 경로입니다. " +
                     "캡처하는 람다는 매 호출마다 힙 할당을 만듭니다.",
        helpLinkUri: DiagnosticIds.HelpUriBase + "DISR003.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol target = invocation.TargetMethod;

        if (!target.Name.StartsWith("PublishEvent", System.StringComparison.Ordinal))
            return;

        INamedTypeSymbol? owner = target.ContainingType?.OriginalDefinition;
        if (owner is null || owner.ContainingNamespace?.ToDisplayString() != "Disruptor")
            return;

        string ownerName = owner.MetadataName;
        if (ownerName != "RingBuffer`1" && ownerName != "ValueRingBuffer`1")
            return;

        foreach (IArgumentOperation argument in invocation.Arguments)
        {
            IOperation value = Unwrap(argument.Value);

            if (value is not IAnonymousFunctionOperation lambda)
                continue;

            if (!CapturesOuterVariables(lambda))
                continue;

            Location location = lambda.Syntax.GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(Rule, location, target.Name));
        }
    }

    private static IOperation Unwrap(IOperation operation)
        => operation is IDelegateCreationOperation delegateCreation
            ? delegateCreation.Target
            : operation;

    /// <summary>
    /// 람다가 자기 매개변수/지역변수 밖의 것을 참조하는지 검사합니다.
    /// </summary>
    private static bool CapturesOuterVariables(IAnonymousFunctionOperation lambda)
    {
        // static 람다는 애초에 캡처할 수 없다.
        if (lambda.Symbol.IsStatic)
            return false;

        var declared = lambda.Symbol.Parameters.ToImmutableHashSet(SymbolEqualityComparer.Default);

        foreach (IOperation descendant in lambda.Body.Descendants())
        {
            switch (descendant)
            {
                case ILocalReferenceOperation local:
                    // 람다 안에서 선언된 지역변수는 캡처가 아니다.
                    if (!IsDeclaredInside(local.Local, lambda))
                        return true;
                    break;

                case IParameterReferenceOperation parameter:
                    if (!declared.Contains(parameter.Parameter))
                        return true;
                    break;

                case IInstanceReferenceOperation:
                    // this 캡처도 클로저를 만든다.
                    return true;
            }
        }

        return false;
    }

    private static bool IsDeclaredInside(ILocalSymbol local, IAnonymousFunctionOperation lambda)
    {
        foreach (SyntaxReference reference in local.DeclaringSyntaxReferences)
        {
            SyntaxNode declaration = reference.GetSyntax();
            if (lambda.Syntax.Contains(declaration))
                return true;
        }

        return false;
    }
}

/// <summary>
/// DISR004: 링 버퍼 크기 상수가 2의 거듭제곱이 아닌 경우를 컴파일 타임에 잡습니다.
/// </summary>
/// <remarks>
/// 런타임 예외로도 잡히지만, 그때는 이미 서버가 시작 중입니다.
/// 인덱스 계산이 <c>sequence &amp; (bufferSize - 1)</c> 이라 2의 거듭제곱이 필수입니다.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RingSizeAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.RingSizeNotPowerOfTwo,
        title: "링 버퍼 크기는 2의 거듭제곱이어야 합니다",
        messageFormat: "링 버퍼 크기 {0} 은(는) 2의 거듭제곱이 아닙니다. " +
                       "인덱스 계산이 비트 마스크라 2의 거듭제곱만 허용됩니다 (예: {1}).",
        category: DiagnosticIds.Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: DiagnosticIds.HelpUriBase + "DISR004.md");

    private static readonly ImmutableHashSet<string> RingSizeParameterNames = ImmutableHashSet.Create(
        "bufferSize", "ringBufferSize", "ringSizePerShard");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.Invocation, OperationKind.ObjectCreation);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        ImmutableArray<IArgumentOperation> arguments;
        INamedTypeSymbol? owner;

        switch (context.Operation)
        {
            case IInvocationOperation invocation:
                arguments = invocation.Arguments;
                owner = invocation.TargetMethod.ContainingType?.OriginalDefinition;
                break;

            case IObjectCreationOperation creation:
                arguments = creation.Arguments;
                owner = creation.Constructor?.ContainingType?.OriginalDefinition;
                break;

            default:
                return;
        }

        if (owner?.ContainingNamespace?.ToDisplayString() is not { } ns)
            return;

        if (!ns.StartsWith("Disruptor", System.StringComparison.Ordinal))
            return;

        foreach (IArgumentOperation argument in arguments)
        {
            if (argument.Parameter is null)
                continue;

            if (!RingSizeParameterNames.Contains(argument.Parameter.Name))
                continue;

            if (!argument.Value.ConstantValue.HasValue)
                continue;

            if (argument.Value.ConstantValue.Value is not int size)
                continue;

            if (size >= 1 && (size & (size - 1)) == 0)
                continue;

            int suggestion = NextPowerOfTwo(size);
            context.ReportDiagnostic(Diagnostic.Create(
                Rule, argument.Value.Syntax.GetLocation(), size, suggestion));
        }
    }

    private static int NextPowerOfTwo(int value)
    {
        if (value < 1)
            return 1;

        int result = 1;
        while (result < value && result < (1 << 30))
            result <<= 1;

        return result;
    }
}
