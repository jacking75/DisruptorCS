using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Disruptor.Analyzers;

/// <summary>
/// DISR005: 배치 핸들러가 받은 span 의 슬롯 참조를 호출 범위 밖으로 유출하는 것을 경고합니다.
/// </summary>
/// <remarks>
/// <para>
/// <c>OnBatch</c> 가 받는 span 은 <b>링 버퍼의 슬롯을 직접 가리킵니다.</b> 호출이 끝나면 프로세서가
/// 시퀀스를 전진시키고, 프로듀서가 그 슬롯을 즉시 재사용합니다. 따라서 보관한 참조는
/// <b>다른 이벤트의 데이터를 가리키게 됩니다</b> — 예외도 나지 않고 조용히 값이 바뀝니다.
/// </para>
/// <para>
/// span 자체는 <c>ref struct</c> 라 필드에 담을 수 없지만, <b>원소</b>는 일반 참조 타입이므로
/// 컴파일러가 막아주지 않습니다. 이 분석기가 그 빈틈을 메웁니다.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BatchSpanEscapeAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.BatchSpanEscape,
        title: "배치 span 의 슬롯 참조를 호출 범위 밖으로 유출하지 마세요",
        messageFormat: "'{0}' 이(가) 배치 span 의 슬롯 참조를 보관합니다. 호출이 끝나면 그 슬롯은 " +
                       "프로듀서가 재사용하므로, 보관한 참조는 다른 이벤트의 데이터를 가리키게 됩니다. " +
                       "필요한 값을 복사하거나 불변 스냅샷을 만드세요.",
        category: DiagnosticIds.Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "링 버퍼 슬롯은 배치 처리가 끝나는 즉시 재사용됩니다. " +
                     "슬롯 참조를 필드·정적 필드·컬렉션에 보관하면 조용히 데이터가 바뀝니다.",
        helpLinkUri: DiagnosticIds.HelpUriBase + "DISR005.md");

    private static readonly ImmutableHashSet<string> BatchHandlerInterfaces = ImmutableHashSet.Create(
        "Disruptor.IBatchEventHandler`1",
        "Disruptor.IValueBatchEventHandler`1");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationBlockStartAction(OnOperationBlockStart);
    }

    private static void OnOperationBlockStart(OperationBlockStartAnalysisContext context)
    {
        if (context.OwningSymbol is not IMethodSymbol method)
            return;

        if (!IsBatchHandlerOnBatch(method, out IParameterSymbol? spanParameter))
            return;

        // span 매개변수에서 흘러나온 값을 담은 지역 변수까지 추적한다.
        var tainted = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);

        context.RegisterOperationAction(
            ctx => TrackLocalAssignment(ctx, spanParameter!, tainted),
            OperationKind.VariableDeclarator,
            OperationKind.SimpleAssignment);

        context.RegisterOperationAction(
            ctx => CheckEscape(ctx, spanParameter!, tainted, method),
            OperationKind.SimpleAssignment,
            OperationKind.Invocation);
    }

    /// <summary>
    /// <c>var e = events[i];</c> 처럼 span 원소를 지역 변수로 받은 경우를 추적 대상에 넣습니다.
    /// </summary>
    private static void TrackLocalAssignment(
        OperationAnalysisContext context,
        IParameterSymbol spanParameter,
        ImmutableHashSet<ISymbol>.Builder tainted)
    {
        (ISymbol? target, IOperation? value) = context.Operation switch
        {
            IVariableDeclaratorOperation declarator =>
                ((ISymbol?)declarator.Symbol, (IOperation?)declarator.Initializer?.Value),

            ISimpleAssignmentOperation { Target: ILocalReferenceOperation local } assignment =>
                (local.Local, assignment.Value),

            _ => (null, null),
        };

        if (target is null || value is null)
            return;

        if (IsSpanElement(value, spanParameter, tainted))
            tainted.Add(target);
    }

    private static void CheckEscape(
        OperationAnalysisContext context,
        IParameterSymbol spanParameter,
        ImmutableHashSet<ISymbol>.Builder tainted,
        IMethodSymbol owningMethod)
    {
        switch (context.Operation)
        {
            // 필드/속성/배열에 대입 -> 호출 범위를 벗어난다.
            case ISimpleAssignmentOperation assignment
                when IsEscapingTarget(assignment.Target)
                     && IsSpanElement(assignment.Value, spanParameter, tainted):
                Report(context, assignment.Syntax, owningMethod);
                break;

            // 컬렉션에 추가 -> 호출 범위를 벗어난다.
            case IInvocationOperation invocation when IsCollectionMutator(invocation):
                foreach (IArgumentOperation argument in invocation.Arguments)
                {
                    if (IsSpanElement(argument.Value, spanParameter, tainted))
                    {
                        Report(context, invocation.Syntax, owningMethod);
                        return;
                    }
                }
                break;
        }
    }

    private static void Report(OperationAnalysisContext context, SyntaxNode syntax, IMethodSymbol method)
        => context.ReportDiagnostic(Diagnostic.Create(
            Rule, syntax.GetLocation(), $"{method.ContainingType.Name}.{method.Name}"));

    /// <summary>대입 대상이 호출 범위 밖까지 살아남는가 (필드 · 속성 · 배열 원소).</summary>
    private static bool IsEscapingTarget(IOperation target) => target switch
    {
        IFieldReferenceOperation => true,
        IPropertyReferenceOperation => true,
        IArrayElementReferenceOperation => true,
        _ => false,
    };

    /// <summary>컬렉션에 원소를 넣는 호출인가.</summary>
    private static bool IsCollectionMutator(IInvocationOperation invocation)
    {
        string name = invocation.TargetMethod.Name;

        if (name is not ("Add" or "AddLast" or "AddFirst" or "Enqueue" or "Push" or "Insert" or "TryAdd"))
            return false;

        // 지역 컬렉션이라도 필드에 보관될 수 있으므로 수신자 종류로 걸러내지 않는다.
        return invocation.Instance is not null;
    }

    /// <summary>
    /// 이 값이 배치 span 의 원소(또는 그것을 담은 지역 변수)에서 유래했는가.
    /// </summary>
    private static bool IsSpanElement(
        IOperation? value,
        IParameterSymbol spanParameter,
        ImmutableHashSet<ISymbol>.Builder tainted)
    {
        value = Unwrap(value);

        return value switch
        {
            // events[i]
            IPropertyReferenceOperation { Instance: IParameterReferenceOperation p }
                when SymbolEqualityComparer.Default.Equals(p.Parameter, spanParameter) => true,

            IArrayElementReferenceOperation { ArrayReference: IParameterReferenceOperation p }
                when SymbolEqualityComparer.Default.Equals(p.Parameter, spanParameter) => true,

            // var e = events[i]; ... field = e;
            ILocalReferenceOperation local => tainted.Contains(local.Local),

            _ => false,
        };
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
            operation = conversion.Operand;

        return operation;
    }

    /// <summary>배치 핸들러 인터페이스의 <c>OnBatch</c> 구현인지 확인하고 span 매개변수를 찾습니다.</summary>
    private static bool IsBatchHandlerOnBatch(IMethodSymbol method, out IParameterSymbol? spanParameter)
    {
        spanParameter = null;

        if (method.Name != "OnBatch" && !method.Name.EndsWith(".OnBatch", System.StringComparison.Ordinal))
            return false;

        INamedTypeSymbol? containing = method.ContainingType;
        if (containing is null)
            return false;

        bool implementsBatchHandler = containing.AllInterfaces.Any(
            iface => BatchHandlerInterfaces.Contains(GetMetadataName(iface.OriginalDefinition)));

        if (!implementsBatchHandler)
            return false;

        // 첫 매개변수가 Span<T> / ReadOnlySpan<T> 이다.
        if (method.Parameters.Length == 0)
            return false;

        IParameterSymbol first = method.Parameters[0];
        string typeName = first.Type.OriginalDefinition.MetadataName;

        if (typeName is not ("Span`1" or "ReadOnlySpan`1"))
            return false;

        spanParameter = first;
        return true;
    }

    private static string GetMetadataName(INamedTypeSymbol type)
    {
        string ns = type.ContainingNamespace is { IsGlobalNamespace: false } n
            ? n.ToDisplayString()
            : string.Empty;

        return ns.Length == 0 ? type.MetadataName : ns + "." + type.MetadataName;
    }
}
