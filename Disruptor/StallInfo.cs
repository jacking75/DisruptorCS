namespace Disruptor;

/// <summary>
/// 프로듀서가 백프레셔로 오래 블록되었을 때 전달되는 진단 정보.
/// </summary>
/// <remarks>
/// 이 정보가 없으면 "서버가 멈췄는데 어느 핸들러 때문인지 모른다"는 최악의 상황이 발생합니다.
/// <example>
/// <code>
/// [WARN] Disruptor 'packet-pipeline' 프로듀서가 250ms 블록됨.
///        가장 느린 핸들러 = 'DbJournalHandler' (seq 1,204,331 / cursor 1,270,867, lag 66,536)
/// </code>
/// </example>
/// </remarks>
/// <param name="PipelineName">파이프라인 이름</param>
/// <param name="ElapsedMs">프로듀서가 블록된 시간(ms)</param>
/// <param name="Cursor">현재 커서</param>
/// <param name="SlowestSequence">가장 뒤처진 게이팅 시퀀스 값</param>
/// <param name="SlowestHandler">가장 뒤처진 핸들러 이름</param>
public readonly record struct StallInfo(
    string PipelineName,
    double ElapsedMs,
    long Cursor,
    long SlowestSequence,
    string SlowestHandler)
{
    /// <summary>가장 느린 핸들러가 커서로부터 뒤처진 정도.</summary>
    public long Lag => Cursor - SlowestSequence;

    public override string ToString()
        => $"Disruptor '{PipelineName}' 프로듀서가 {ElapsedMs:F0}ms 블록됨. " +
           $"가장 느린 핸들러 = '{SlowestHandler}' " +
           $"(seq {SlowestSequence:N0} / cursor {Cursor:N0}, lag {Lag:N0})";
}

/// <summary>
/// 시퀀서 동작 옵션.
/// </summary>
public sealed record SequencerOptions
{
    public static SequencerOptions Default { get; } = new();

    /// <summary>파이프라인 이름. 진단 메시지와 메트릭 태그에 사용됩니다.</summary>
    public string PipelineName { get; init; } = "default";

    /// <summary>
    /// 링 버퍼가 가득 찼을 때의 대기 정책. 기본값은 <see cref="SpinOnlyBackoff"/>입니다.
    /// </summary>
    public IProducerBackoff Backoff { get; init; } = SpinOnlyBackoff.Instance;

    /// <summary>이 시간을 넘게 블록되면 <see cref="OnStall"/>이 호출됩니다.</summary>
    public TimeSpan StallThreshold { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>스톨 감지 콜백. 블록된 프로듀서 스레드에서 1회만 호출됩니다.</summary>
    public Action<StallInfo>? OnStall { get; init; }

    /// <summary>
    /// DEBUG 빌드에서 <c>ProducerType.Single</c> 오용(여러 스레드가 게시)을 검사할지 여부.
    /// </summary>
    public bool ValidateSingleProducerThread { get; init; } = true;
}
