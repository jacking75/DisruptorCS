namespace Disruptor;

/// <summary>
/// 대기 연산의 결과. 예외(<c>AlertException</c>) 기반 종료 신호를 대체합니다.
/// </summary>
/// <remarks>
/// 예외를 정상 종료 경로에 사용하면 (1) 스택 언와인딩 비용이 크고,
/// (2) 싱글턴 예외 인스턴스를 여러 스레드가 공유할 경우 stack trace가 서로를 덮어써
/// 사고 분석이 불가능해집니다. <see cref="SequenceWaitResult"/>는 8바이트 readonly struct이므로
/// 할당이 없고 스레드 간 공유 상태도 없습니다.
/// </remarks>
public readonly struct SequenceWaitResult : IEquatable<SequenceWaitResult>
{
    private const long TimeoutValue = -1L;
    private const long CancelledValue = -2L;

    private readonly long _value;

    private SequenceWaitResult(long value) => _value = value;

    /// <summary>대기 시간이 초과되었습니다. 핸들러의 OnTimeout 훅을 호출해야 합니다.</summary>
    public static SequenceWaitResult Timeout => new(TimeoutValue);

    /// <summary>취소가 요청되었습니다. 이벤트 루프를 종료해야 합니다.</summary>
    public static SequenceWaitResult Cancelled => new(CancelledValue);

    /// <summary>사용 가능한 시퀀스를 결과로 변환합니다.</summary>
    public static implicit operator SequenceWaitResult(long availableSequence)
        => new(availableSequence);

    public bool IsTimeout => _value == TimeoutValue;

    public bool IsCancelled => _value == CancelledValue;

    /// <summary>
    /// 사용 가능한 최대 시퀀스.
    /// <see cref="IsTimeout"/> 또는 <see cref="IsCancelled"/>가 false일 때만 유효합니다.
    /// </summary>
    public long AvailableSequence => _value;

    public bool Equals(SequenceWaitResult other) => _value == other._value;

    public override bool Equals(object? obj) => obj is SequenceWaitResult other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    public static bool operator ==(SequenceWaitResult left, SequenceWaitResult right) => left.Equals(right);

    public static bool operator !=(SequenceWaitResult left, SequenceWaitResult right) => !left.Equals(right);

    public override string ToString() => _value switch
    {
        TimeoutValue => "Timeout",
        CancelledValue => "Cancelled",
        _ => _value.ToString(),
    };
}
