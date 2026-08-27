using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Disruptor.Analyzers;

/// <summary>
/// DISR001: Disruptor 핸들러 메서드를 <c>async</c> 로 선언하는 것을 금지합니다.
/// </summary>
/// <remarks>
/// <c>async void OnEvent</c> 는 첫 <c>await</c> 에서 즉시 반환되므로, 프레임워크는 이벤트가
/// 처리되었다고 보고 시퀀스를 전진시킵니다. 그 결과 <b>순서 보장이 깨지고 슬롯이 처리 도중
/// 재사용</b>됩니다. 런타임 DEBUG 검사도 있지만, 컴파일 타임에 잡는 것이 훨씬 낫습니다.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AsyncHandlerAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticIds.AsyncHandler,
        title: "Disruptor 핸들러는 async 로 선언할 수 없습니다",
        messageFormat: "'{0}.{1}' 은(는) async 로 선언된 Disruptor 핸들러입니다. " +
                       "첫 await 에서 반환되어 이벤트 순서 보장이 깨지고 슬롯이 처리 도중 재사용됩니다. " +
                       "느린 I/O 는 WorkerPool(IWorkHandler)로 분리하세요.",
        category: DiagnosticIds.Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Disruptor 컨슈머는 동기 블로킹 스레드입니다. 핸들러가 async 면 " +
                     "프레임워크가 이벤트를 처리 완료로 오인해 시퀀스를 전진시킵니다.",
        helpLinkUri: DiagnosticIds.HelpUriBase + "DISR001.md");

    /// <summary>핸들러 인터페이스의 정규화된 메타데이터 이름.</summary>
    private static readonly ImmutableHashSet<string> HandlerInterfaces = ImmutableHashSet.Create(
        "Disruptor.IEventHandler`1",
        "Disruptor.IValueEventHandler`1",
        "Disruptor.IBatchEventHandler`1",
        "Disruptor.IValueBatchEventHandler`1",
        "Disruptor.Work.IWorkHandler`1");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;

        if (!method.IsAsync)
            return;

        INamedTypeSymbol? containing = method.ContainingType;
        if (containing is null)
            return;

        foreach (INamedTypeSymbol iface in containing.AllInterfaces)
        {
            string name = GetMetadataName(iface.OriginalDefinition);
            if (!HandlerInterfaces.Contains(name))
                continue;

            if (!ImplementsAnyMemberOf(containing, iface, method))
                continue;

            foreach (Location location in method.Locations)
            {
                if (!location.IsInSource)
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    Rule, location, containing.Name, method.Name));
            }

            return;
        }
    }

    /// <summary>
    /// <paramref name="method"/> 가 <paramref name="iface"/> 의 멤버 구현인지 확인합니다.
    /// </summary>
    private static bool ImplementsAnyMemberOf(
        INamedTypeSymbol containing, INamedTypeSymbol iface, IMethodSymbol method)
    {
        foreach (ISymbol member in iface.GetMembers())
        {
            if (member is not IMethodSymbol interfaceMethod)
                continue;

            ISymbol? impl = containing.FindImplementationForInterfaceMember(interfaceMethod);
            if (SymbolEqualityComparer.Default.Equals(impl, method))
                return true;
        }

        return false;
    }

    /// <summary>"Namespace.Type`arity" 형태의 이름을 만듭니다.</summary>
    private static string GetMetadataName(INamedTypeSymbol type)
    {
        string ns = type.ContainingNamespace is { IsGlobalNamespace: false } n
            ? n.ToDisplayString()
            : string.Empty;

        return ns.Length == 0 ? type.MetadataName : ns + "." + type.MetadataName;
    }
}
