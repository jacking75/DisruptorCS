namespace Disruptor.Analyzers;

/// <summary>
/// DisruptorCS 진단 ID.
/// </summary>
/// <remarks>
/// 이 라이브러리의 위험한 오용은 대부분 <b>컴파일은 되지만 런타임에 조용히 데이터를 깨뜨립니다</b>.
/// 컴파일 타임 진단은 그래서 문서보다 강력한 안정성 장치입니다.
/// </remarks>
internal static class DiagnosticIds
{
    /// <summary>핸들러 메서드를 async 로 선언 (Error).</summary>
    public const string AsyncHandler = "DISR001";

    /// <summary>Next() 후 Publish() 가 finally 에 없음 (Warning).</summary>
    public const string PublishNotInFinally = "DISR002";

    /// <summary>PublishEvent 에 캡처하는 람다 전달 (Info).</summary>
    public const string CapturingLambdaInPublish = "DISR003";

    /// <summary>링 버퍼 크기가 2의 거듭제곱이 아님 (Error).</summary>
    public const string RingSizeNotPowerOfTwo = "DISR004";

    /// <summary>배치 span 의 슬롯 참조를 필드에 보관 (Warning).</summary>
    public const string BatchSpanEscape = "DISR005";

    public const string Category = "Disruptor.Correctness";

    public const string HelpUriBase = "https://github.com/jacking75/DisruptorCS/blob/main/docs/analyzers/";
}
