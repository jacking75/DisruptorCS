using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Disruptor.Analyzers.Tests;

/// <summary>
/// 분석기를 실제 컴파일에 붙여 진단을 수집하는 헬퍼.
/// </summary>
/// <remarks>
/// 테스트 소스는 실제 <c>Disruptor</c> 어셈블리를 참조하므로,
/// 심볼 매칭(네임스페이스·제네릭 arity)이 실제 API 와 어긋나면 바로 드러납니다.
/// </remarks>
internal static class AnalyzerVerifier
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();

        // 런타임 어셈블리 전체를 참조에 넣는다 (netstandard 파사드 포함).
        string trustedAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        foreach (string path in trustedAssemblies.Split(Path.PathSeparator))
        {
            if (path.Length > 0 && File.Exists(path))
                builder.Add(MetadataReference.CreateFromFile(path));
        }

        // Disruptor 어셈블리
        builder.Add(MetadataReference.CreateFromFile(typeof(IEventHandler<>).Assembly.Location));

        return builder.ToImmutable();
    }

    /// <summary>소스를 컴파일하고 분석기가 낸 진단을 반환합니다.</summary>
    public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(
        DiagnosticAnalyzer analyzer, string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Latest));

        var compilation = CSharpCompilation.Create(
            "AnalyzerTestAssembly",
            [tree],
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        // 소스 자체의 컴파일 오류가 있으면 분석 결과를 신뢰할 수 없다.
        ImmutableArray<Diagnostic> compileErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        if (compileErrors.Length > 0)
        {
            throw new InvalidOperationException(
                "테스트 소스에 컴파일 오류가 있습니다:\n" +
                string.Join("\n", compileErrors.Select(d => d.ToString())));
        }

        CompilationWithAnalyzers withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create(analyzer));

        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    /// <summary>지정한 ID 의 진단이 정확히 <paramref name="expectedCount"/> 개 나오는지 확인합니다.</summary>
    public static async Task VerifyAsync(
        DiagnosticAnalyzer analyzer, string source, string expectedId, int expectedCount)
    {
        ImmutableArray<Diagnostic> diagnostics = await GetDiagnosticsAsync(analyzer, source);
        var matching = diagnostics.Where(d => d.Id == expectedId).ToList();

        if (matching.Count != expectedCount)
        {
            throw new Xunit.Sdk.XunitException(
                $"{expectedId} 진단이 {expectedCount}개 기대되었으나 {matching.Count}개 나왔습니다.\n" +
                string.Join("\n", diagnostics.Select(d => $"  {d.Id}: {d.GetMessage()} @ {d.Location}")));
        }
    }

    /// <summary>지정한 ID 의 진단이 없는지 확인합니다.</summary>
    public static Task VerifyNoDiagnosticsAsync(DiagnosticAnalyzer analyzer, string source, string id)
        => VerifyAsync(analyzer, source, id, 0);
}
