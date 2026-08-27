using System.Runtime.CompilerServices;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;

namespace Disruptor.Game;

/// <summary>
/// 키(룸 ID, 세션 ID 등)를 샤드에 매핑해, 같은 키의 이벤트는 항상 같은 샤드에서
/// 같은 순서로 처리되도록 보장합니다.
/// </summary>
/// <remarks>
/// <para>
/// Disruptor는 컨슈머 1개당 전용 스레드 1개를 요구하므로, 룸마다 Disruptor를 만들면
/// 10,000룸 = 스레드 10,000개 + 링 메모리 1.3GB로 커널 스케줄러가 붕괴합니다.
/// </para>
/// <para>
/// 샤딩하면 <b>스레드 수가 룸 수와 무관</b>해집니다.
/// 10,000룸 기준: 샤드 16 / 링 4096 → 스레드 16개, 링 메모리 약 8MB.
/// </para>
/// <para>
/// <b>대가</b>: 한 룸이 무거우면 같은 샤드의 다른 룸이 함께 밀립니다(head-of-line blocking).
/// 룸별 처리 시간 상한을 두고, 초과하는 작업은 워커 풀로 밀어내는 설계를 함께 두세요.
/// </para>
/// </remarks>
public sealed class ShardedDisruptor<T> : IDisposable where T : class
{
    private readonly Dsl.Disruptor<T>[] _shards;
    private readonly RingBuffer<T>[] _rings;
    private readonly PublishGate<T>[] _gates;
    private readonly int _shardMask;
    private int _started;

    /// <param name="shardCount">샤드 수. 2의 거듭제곱이어야 하며, 보통 (물리 코어 수 - 2)에 가장 가까운 2의 거듭제곱.</param>
    /// <param name="ringSizePerShard">샤드당 링 버퍼 크기</param>
    /// <param name="eventFactory">이벤트 생성기</param>
    /// <param name="handlerFactory">샤드 인덱스를 받아 그 샤드의 핸들러를 만드는 팩토리</param>
    /// <param name="producerType">프로듀서 유형</param>
    /// <param name="waitStrategyFactory">샤드별 대기 전략 팩토리. 인스턴스를 공유하면 안 되므로 팩토리를 받습니다.</param>
    /// <param name="options">DSL 옵션</param>
    public ShardedDisruptor(
        int shardCount,
        int ringSizePerShard,
        Func<T> eventFactory,
        Func<int, IEventHandler<T>> handlerFactory,
        ProducerType producerType = ProducerType.Multi,
        Func<int, IWaitStrategy>? waitStrategyFactory = null,
        DisruptorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(eventFactory);
        ArgumentNullException.ThrowIfNull(handlerFactory);

        if (shardCount < 1 || (shardCount & (shardCount - 1)) != 0)
            throw new ArgumentException("shardCount must be a power of 2", nameof(shardCount));

        options ??= DisruptorOptions.Default;
        _shardMask = shardCount - 1;
        _shards = new Dsl.Disruptor<T>[shardCount];
        _rings = new RingBuffer<T>[shardCount];
        _gates = new PublishGate<T>[shardCount];

        for (int i = 0; i < shardCount; i++)
        {
            var waitStrategy = waitStrategyFactory?.Invoke(i)
                               ?? new TimeoutBlockingWaitStrategy(TimeSpan.FromMilliseconds(1));

            var shardOptions = options with { Name = $"{options.Name}-shard{i}" };

            var disruptor = new Dsl.Disruptor<T>(
                eventFactory, ringSizePerShard, producerType, waitStrategy, shardOptions);

            disruptor.HandleEventsWith(handlerFactory(i));
            disruptor.ProcessorHalted += (p, ex) => ProcessorHalted?.Invoke(p, ex);

            _shards[i] = disruptor;
        }
    }

    public int ShardCount => _shards.Length;

    public IReadOnlyList<Dsl.Disruptor<T>> Shards => _shards;

    /// <summary>샤드의 프로세서가 예외로 정지했을 때 발생합니다.</summary>
    public event Action<IEventProcessor, Exception?>? ProcessorHalted;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("ShardedDisruptor가 이미 시작되었습니다.");

        for (int i = 0; i < _shards.Length; i++)
        {
            _rings[i] = _shards[i].Start();
            _gates[i] = new PublishGate<T>(_rings[i], _shards[i].Metrics);
        }
    }

    /// <summary>키를 샤드에 고정 매핑합니다. 같은 키는 항상 같은 샤드 = 순서 보장.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ShardOf(long key) => (int)(Mix((ulong)key) & (uint)_shardMask);

    /// <summary>
    /// 룸 ID 가 연속 정수여도 균등 분산되도록 섞습니다 (splitmix64 finalizer).
    /// </summary>
    /// <remarks>
    /// 단순히 <c>key &amp; mask</c>를 쓰면 룸 ID가 연속일 때는 균등하지만,
    /// 특정 배수 패턴(예: 채널당 16씩 증가)에서는 한 샤드에 몰립니다.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong key)
    {
        ulong z = key + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>지정 샤드의 링 버퍼.</summary>
    public RingBuffer<T> RingBufferForShard(int shard) => _rings[shard];

    /// <summary>키에 해당하는 샤드의 링 버퍼.</summary>
    public RingBuffer<T> RingBufferForKey(long key) => _rings[ShardOf(key)];

    /// <summary>키에 해당하는 샤드에 이벤트를 게시합니다. 용량이 없으면 대기합니다.</summary>
    public void Publish(long key, Action<T, long> fill)
    {
        EnsureStarted();
        var ring = _rings[ShardOf(key)];
        long seq = ring.Next();
        try
        {
            fill(ring[seq], seq);
        }
        finally
        {
            ring.Publish(seq);
        }
    }

    /// <summary>정책에 따라 게시합니다.</summary>
    public bool TryPublish(long key, Action<T, long> fill,
                           BackpressurePolicy policy = BackpressurePolicy.FailAfterTimeout,
                           TimeSpan? timeout = null,
                           int opCode = 0,
                           CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        return _gates[ShardOf(key)].TryPublish(policy, fill, opCode, timeout, cancellationToken);
    }

    /// <summary>샤드별 사용률. 특정 샤드에 부하가 몰렸는지 확인할 때 사용합니다.</summary>
    public double[] ShardUtilizations()
    {
        EnsureStarted();
        return Array.ConvertAll(_rings, r => r.Utilization);
    }

    /// <summary>샤드별 드롭 건수.</summary>
    public long[] ShardDropCounts()
    {
        EnsureStarted();
        return Array.ConvertAll(_gates, g => g.DroppedCount);
    }

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _started) == 0)
            throw new InvalidOperationException("ShardedDisruptor를 먼저 Start() 해야 합니다.");
    }

    public void Shutdown(TimeSpan? timeout = null)
    {
        foreach (var shard in _shards)
            shard.Shutdown(timeout);
    }

    public void Dispose()
    {
        foreach (var shard in _shards)
            shard.Dispose();
    }
}
