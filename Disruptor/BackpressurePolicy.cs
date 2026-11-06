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

    /// <summary>Drops when congested or when no slot can be claimed, without waiting for consumer capacity.</summary>
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

    // 모든 프로듀서가 하나의 Interlocked 대상을 공유하면 그 카운터가 게시 경로의 스케일링을 깎는다
    // (캐시라인 핑퐁). 스트라이핑하면 스레드별로 다른 라인에 기록한다.
    private readonly StripedCounter _dropped = new();
    private readonly StripedCounter _rejected = new();
    private readonly StripedCounter _published = new();

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

    /// <summary>혼잡으로 드롭된 건수. 스트라이프 합산이라 시점 일치를 보장하지 않습니다.</summary>
    public long DroppedCount => _dropped.Value;

    /// <summary>타임아웃으로 거절된 건수.</summary>
    public long RejectedCount => _rejected.Value;

    /// <summary>게시에 성공한 건수.</summary>
    public long PublishedCount => _published.Value;

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
            {
                // Another producer can fill the ring after the utilization check. Never fall
                // through to blocking Next here: request/network threads must be able to shed load.
                if (_ringBuffer.IsCongested(CongestionThreshold) || !_ringBuffer.TryNext(out long sequence))
                {
                    _dropped.Increment();
                    _metrics?.RecordDropped(opCode);
                    return false;
                }
                Fill(fill, sequence);
                return true;
            }

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
                    _rejected.Increment();
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
            _published.Increment();
            _metrics?.RecordPublished();
        }
    }

    public void ResetCounters()
    {
        _dropped.Reset();
        _rejected.Reset();
        _published.Reset();
    }
}
