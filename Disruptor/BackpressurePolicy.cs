namespace Disruptor;

/// <summary>
/// 링 버퍼가 가득 찼을 때의 처리 정책.
/// </summary>
/// <remarks>
/// 게임 서버는 이벤트 종류마다 다른 정책이 필요합니다.
/// <list type="table">
///   <item><term>이동 패킷</term><description>드롭 — 다음 패킷이 곧 온다</description></item>
///   <item><term>아이템 구매 / 재화 변동</term><description>블록 또는 오류 응답 — 절대 버릴 수 없다</description></item>
///   <item><term>채팅</term><description>드롭 + 클라이언트에 "혼잡" 알림</description></item>
///   <item><term>로그 / 메트릭</term><description>드롭 + 드롭 카운터 증가</description></item>
/// </list>
/// 네트워크 I/O 스레드를 무조건 블록시키면 서버 전체가 굳으므로, 기본값을 신중히 고르세요.
/// </remarks>
public enum BackpressurePolicy
{
    /// <summary>슬롯이 날 때까지 대기합니다. 유실이 절대 허용되지 않는 이벤트에만 사용하세요.</summary>
    Block,

    /// <summary>혼잡 임계치를 넘으면 즉시 드롭합니다. 최신값만 의미 있는 이벤트에 적합합니다.</summary>
    DropWhenCongested,

    /// <summary>타임아웃까지 시도하고 실패하면 거절합니다. 호출자가 오류 응답을 보낼 수 있습니다.</summary>
    FailAfterTimeout,
}

/// <summary>
/// 정책 기반 게시 게이트. 드롭/거절 횟수를 집계하고 메트릭에 반영합니다.
/// </summary>
public sealed class PublishGate<T> where T : class
{
    private readonly RingBuffer<T> _ringBuffer;
    private readonly DisruptorMetrics? _metrics;
    private long _dropped;
    private long _rejected;
    private long _published;

    public PublishGate(RingBuffer<T> ringBuffer, DisruptorMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(ringBuffer);
        _ringBuffer = ringBuffer;
        _metrics = metrics;
    }

    /// <summary>혼잡 판정 임계치 (0~1). 기본 0.9.</summary>
    public double CongestionThreshold { get; init; } = 0.9;

    /// <summary><see cref="BackpressurePolicy.FailAfterTimeout"/>의 기본 타임아웃.</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromMilliseconds(50);

    public long DroppedCount => Volatile.Read(ref _dropped);

    public long RejectedCount => Volatile.Read(ref _rejected);

    public long PublishedCount => Volatile.Read(ref _published);

    /// <summary>
    /// 정책에 따라 이벤트를 게시합니다.
    /// </summary>
    /// <param name="policy">백프레셔 정책</param>
    /// <param name="fill">슬롯을 채우는 동작</param>
    /// <param name="opCode">메트릭 태그용 식별자 (선택)</param>
    /// <param name="timeout"><see cref="BackpressurePolicy.FailAfterTimeout"/>일 때 사용할 타임아웃</param>
    /// <param name="cancellationToken">블록 대기 취소</param>
    /// <returns>게시했으면 true, 드롭/거절되었으면 false</returns>
    public bool TryPublish(
        BackpressurePolicy policy,
        Action<T, long> fill,
        int opCode = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fill);

        switch (policy)
        {
            case BackpressurePolicy.DropWhenCongested:
                if (_ringBuffer.IsCongested(CongestionThreshold))
                {
                    Interlocked.Increment(ref _dropped);
                    _metrics?.RecordDropped(opCode);
                    return false;
                }
                goto case BackpressurePolicy.Block;

            case BackpressurePolicy.Block:
            {
                long sequence = _ringBuffer.Next(cancellationToken);
                Fill(fill, sequence);
                return true;
            }

            case BackpressurePolicy.FailAfterTimeout:
            {
                if (!_ringBuffer.TryNext(out long sequence, timeout ?? DefaultTimeout))
                {
                    Interlocked.Increment(ref _rejected);
                    _metrics?.RecordRejected(opCode);
                    return false;
                }

                Fill(fill, sequence);
                return true;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(policy));
        }
    }

    private void Fill(Action<T, long> fill, long sequence)
    {
        try
        {
            fill(_ringBuffer[sequence], sequence);
        }
        finally
        {
            // 채우는 도중 예외가 나도 반드시 게시해야 한다.
            // 게시하지 않으면 그 슬롯에서 링 버퍼가 영구히 막힌다.
            _ringBuffer.Publish(sequence);
            Interlocked.Increment(ref _published);
            _metrics?.RecordPublished();
        }
    }

    public void ResetCounters()
    {
        Volatile.Write(ref _dropped, 0);
        Volatile.Write(ref _rejected, 0);
        Volatile.Write(ref _published, 0);
    }
}
