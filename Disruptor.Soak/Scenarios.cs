using System.Diagnostics;
using Disruptor;
using Disruptor.Dsl;
using Disruptor.Game;
using Disruptor.WaitStrategy;
using Disruptor.Work;

namespace Disruptor.Soak;

public sealed class SoakEvent : IClearable
{
    public long Sequence;
    public long Value;
    public byte[]? Payload;

    public void Clear()
    {
        Sequence = 0;
        Value = 0;
        Payload = null;
    }
}

/// <summary>불변식 위반. 시나리오는 이것을 던져 하니스를 중단시킵니다.</summary>
public sealed class InvariantViolationException : Exception
{
    public InvariantViolationException(string message) : base(message) { }
}

/// <summary>
/// 장기 실행 시나리오. 주기적으로 불변식을 검사할 수 있어야 합니다.
/// </summary>
public abstract class SoakScenario : IDisposable
{
    protected SoakScenario(int seed) => Random = new Random(seed);

    protected Random Random { get; }

    public abstract string Name { get; }

    /// <summary>한 라운드를 실행합니다. 하니스가 반복 호출합니다.</summary>
    public abstract void RunRound();

    /// <summary>불변식을 검사합니다. 위반 시 <see cref="InvariantViolationException"/>을 던지세요.</summary>
    public abstract void CheckInvariants();

    /// <summary>진행 상황 한 줄 요약.</summary>
    public abstract string Describe();

    public virtual void Dispose() => GC.SuppressFinalize(this);
}

/// <summary>
/// 다중 프로듀서 + 작은 링 = 백프레셔 상시. 이식 버그가 있던 바로 그 조건입니다.
/// </summary>
public sealed class MultiProducerBackpressureScenario : SoakScenario
{
    private const int Producers = 8;
    private const int RingSize = 256;
    private const int PerRound = 20_000;

    private readonly Dsl.Disruptor<SoakEvent> _disruptor;
    private readonly RingBuffer<SoakEvent> _ring;
    private readonly CountingHandler _handler = new();

    private long _published;

    public MultiProducerBackpressureScenario(int seed) : base(seed)
    {
        _disruptor = new Dsl.Disruptor<SoakEvent>(
            () => new SoakEvent(), RingSize, ProducerType.Multi, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "soak-mp", AutoClearSlots = false });

        _disruptor.HandleEventsWith(_handler);
        _ring = _disruptor.Start();
    }

    public override string Name => "MP + 작은 링 (백프레셔 상시)";

    public override void RunRound()
    {
        var threads = new Thread[Producers];
        int perProducer = PerRound / Producers;

        for (int p = 0; p < Producers; p++)
        {
            threads[p] = new Thread(() =>
            {
                for (int i = 0; i < perProducer; i++)
                {
                    long seq = _ring.Next();
                    _ring[seq].Sequence = seq;
                    _ring.Publish(seq);
                }
            })
            { IsBackground = true };
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Interlocked.Add(ref _published, perProducer * Producers);
    }

    public override void CheckInvariants()
    {
        long published = Volatile.Read(ref _published);
        long cursor = _ring.Cursor;

        if (cursor + 1 != published)
            throw new InvariantViolationException(
                $"커서 불일치: 게시 {published:N0}, 커서 {cursor:N0}");

        // 소비가 따라오는지 확인 (백프레셔가 정상이면 결국 따라잡는다)
        var sw = Stopwatch.StartNew();
        while (_handler.Count < published && sw.Elapsed < TimeSpan.FromSeconds(30))
            Thread.Sleep(10);

        if (_handler.Count != published)
            throw new InvariantViolationException(
                $"소비 정지: 기대 {published:N0}, 실제 {_handler.Count:N0} " +
                $"(유실 {published - _handler.Count:N0}) — 컨슈머가 멈췄습니다.");

        if (_handler.Mismatches != 0)
            throw new InvariantViolationException(
                $"슬롯 덮어쓰기 {_handler.Mismatches:N0}회 — 백프레셔가 깨졌습니다.");

        if (_handler.OutOfOrder != 0)
            throw new InvariantViolationException($"순서 위반 {_handler.OutOfOrder:N0}회");
    }

    public override string Describe()
        => $"게시 {Volatile.Read(ref _published):N0} / 소비 {_handler.Count:N0} / 유실 0";

    public override void Dispose()
    {
        _disruptor.Dispose();
        base.Dispose();
    }

    private sealed class CountingHandler : IEventHandler<SoakEvent>
    {
        private long _count;
        private long _mismatches;
        private long _outOfOrder;
        private long _last = -1;

        public long Count => Volatile.Read(ref _count);
        public long Mismatches => Volatile.Read(ref _mismatches);
        public long OutOfOrder => Volatile.Read(ref _outOfOrder);

        public void OnEvent(SoakEvent data, long sequence, bool endOfBatch)
        {
            if (data.Sequence != sequence)
                Interlocked.Increment(ref _mismatches);

            if (sequence != _last + 1)
                Interlocked.Increment(ref _outOfOrder);

            _last = sequence;
            Interlocked.Increment(ref _count);
        }
    }
}

/// <summary>
/// 멀티캐스트 + 느린 소비자 하나. 배치 효과와 게이팅이 장기적으로 안정한지 봅니다.
/// </summary>
public sealed class MulticastSlowConsumerScenario : SoakScenario
{
    private const int RingSize = 1024;
    private const int PerRound = 10_000;

    private readonly Dsl.Disruptor<SoakEvent> _disruptor;
    private readonly RingBuffer<SoakEvent> _ring;
    private readonly SimpleCounter _fast = new();
    private readonly SlowCounter _slow = new();
    private readonly SimpleCounter _terminal = new();

    private long _published;

    public MulticastSlowConsumerScenario(int seed) : base(seed)
    {
        _disruptor = new Dsl.Disruptor<SoakEvent>(
            () => new SoakEvent(), RingSize, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "soak-multicast", AutoClearSlots = false });

        _disruptor.HandleEventsWith(_fast, _slow).Then(_terminal);
        _ring = _disruptor.Start();
    }

    public override string Name => "멀티캐스트 + 느린 소비자";

    public override void RunRound()
    {
        for (int i = 0; i < PerRound; i++)
        {
            long seq = _ring.Next();
            _ring[seq].Sequence = seq;
            _ring.Publish(seq);
        }

        _published += PerRound;
    }

    public override void CheckInvariants()
    {
        var sw = Stopwatch.StartNew();
        while (_terminal.Count < _published && sw.Elapsed < TimeSpan.FromSeconds(60))
            Thread.Sleep(10);

        if (_fast.Count != _published || _slow.Count != _published || _terminal.Count != _published)
        {
            throw new InvariantViolationException(
                $"멀티캐스트 불일치: 게시 {_published:N0}, " +
                $"fast {_fast.Count:N0}, slow {_slow.Count:N0}, terminal {_terminal.Count:N0}");
        }
    }

    public override string Describe() => $"게시 {_published:N0} / 3개 핸들러 모두 일치";

    public override void Dispose()
    {
        _disruptor.Dispose();
        base.Dispose();
    }

    private sealed class SimpleCounter : IEventHandler<SoakEvent>
    {
        private long _count;
        public long Count => Volatile.Read(ref _count);
        public void OnEvent(SoakEvent data, long sequence, bool endOfBatch)
            => Interlocked.Increment(ref _count);
    }

    private sealed class SlowCounter : IEventHandler<SoakEvent>
    {
        private long _count;
        private int _n;
        public long Count => Volatile.Read(ref _count);

        public void OnEvent(SoakEvent data, long sequence, bool endOfBatch)
        {
            // 주기적으로 느려져서 배치가 커지게 만든다.
            if ((++_n & 4095) == 0)
                Thread.SpinWait(20_000);

            Interlocked.Increment(ref _count);
        }
    }
}

/// <summary>
/// 랜덤 예외 주입 + 워커 풀. 예외 정책이 장기적으로 프로세스를 죽이지 않는지 봅니다.
/// </summary>
public sealed class ChaosExceptionScenario : SoakScenario
{
    private const int RingSize = 512;
    private const int PerRound = 10_000;

    private readonly Dsl.Disruptor<SoakEvent> _disruptor;
    private readonly RingBuffer<SoakEvent> _ring;
    private readonly FlakyHandler _handler;
    private long _published;
    private long _errors;
    private long _halted;

    public ChaosExceptionScenario(int seed) : base(seed)
    {
        _handler = new FlakyHandler(new Random(seed ^ 0x5EED));

        _disruptor = new Dsl.Disruptor<SoakEvent>(
            () => new SoakEvent(), RingSize, ProducerType.Multi, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "soak-chaos", AutoClearSlots = false });

        _disruptor.SetDefaultExceptionHandler(new ResilientExceptionHandler<SoakEvent>(
            (ex, seq, evt) => Interlocked.Increment(ref _errors),
            maxConsecutiveFailures: 1_000_000)); // 소크 동안 정지하지 않게

        _disruptor.ProcessorHalted += (_, _) => Interlocked.Increment(ref _halted);
        _disruptor.HandleEventsWith(_handler);
        _ring = _disruptor.Start();
    }

    public override string Name => "랜덤 예외 주입 (프로세스 생존 확인)";

    public override void RunRound()
    {
        for (int i = 0; i < PerRound; i++)
        {
            long seq = _ring.Next();
            _ring[seq].Sequence = seq;
            _ring.Publish(seq);
        }

        _published += PerRound;
    }

    public override void CheckInvariants()
    {
        if (Volatile.Read(ref _halted) != 0)
            throw new InvariantViolationException("프로세서가 정지했습니다 (Skip 정책인데 Halt 발생).");

        var sw = Stopwatch.StartNew();
        while (_handler.Seen + Volatile.Read(ref _errors) < _published
               && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(10);
        }

        long accounted = _handler.Seen + Volatile.Read(ref _errors);
        if (accounted != _published)
        {
            throw new InvariantViolationException(
                $"이벤트 정산 불일치: 게시 {_published:N0}, 처리 {_handler.Seen:N0} + 오류 {_errors:N0} = {accounted:N0}");
        }
    }

    public override string Describe()
        => $"게시 {_published:N0} / 처리 {_handler.Seen:N0} / 주입 오류 {Volatile.Read(ref _errors):N0} / 정지 0";

    public override void Dispose()
    {
        _disruptor.Dispose();
        base.Dispose();
    }

    private sealed class FlakyHandler : IEventHandler<SoakEvent>
    {
        private readonly Random _random;
        private long _seen;

        public FlakyHandler(Random random) => _random = random;

        public long Seen => Volatile.Read(ref _seen);

        public void OnEvent(SoakEvent data, long sequence, bool endOfBatch)
        {
            // 약 0.1% 확률로 실패시킨다.
            if (_random.Next(1000) == 0)
                throw new InvalidOperationException($"카오스 예외 at {sequence}");

            Interlocked.Increment(ref _seen);
        }
    }
}

/// <summary>
/// 수천 룸 샤딩 + 룸별 순서 검증.
/// </summary>
public sealed class ShardedRoomsScenario : SoakScenario
{
    private const int Shards = 4;
    private const int Rooms = 500;
    private const int PerRoomPerRound = 20;

    private readonly ShardedDisruptor<SoakEvent> _sharded;
    private readonly OrderCheckingHandler[] _handlers = new OrderCheckingHandler[Shards];
    private long _published;

    public ShardedRoomsScenario(int seed) : base(seed)
    {
        _sharded = new ShardedDisruptor<SoakEvent>(
            Shards, 2048,
            () => new SoakEvent(),
            i => _handlers[i] = new OrderCheckingHandler(),
            ProducerType.Multi,
            _ => new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "soak-shard", AutoClearSlots = false });

        _sharded.Start();
    }

    public override string Name => $"샤딩 ({Rooms}룸 / {Shards}샤드)";

    public override void RunRound()
    {
        long baseOrder = _published / Rooms;

        Parallel.For(0, Rooms, room =>
        {
            for (int i = 0; i < PerRoomPerRound; i++)
            {
                long order = baseOrder + i;
                _sharded.Publish(room, (evt, seq) =>
                {
                    evt.Sequence = room;
                    evt.Value = order;
                });
            }
        });

        _published += (long)Rooms * PerRoomPerRound;
    }

    public override void CheckInvariants()
    {
        var sw = Stopwatch.StartNew();
        long total() => _handlers.Sum(h => h.Count);

        while (total() < _published && sw.Elapsed < TimeSpan.FromSeconds(60))
            Thread.Sleep(10);

        if (total() != _published)
            throw new InvariantViolationException(
                $"샤딩 유실: 게시 {_published:N0}, 처리 {total():N0}");

        long violations = _handlers.Sum(h => h.OrderViolations);
        if (violations != 0)
            throw new InvariantViolationException($"룸별 순서 위반 {violations:N0}회");

        foreach (ShardHealth health in _sharded.HealthSnapshot())
        {
            if (health.Halted)
                throw new InvariantViolationException($"샤드 {health.Shard} 가 정지했습니다.");
        }
    }

    public override string Describe()
        => $"게시 {_published:N0} / 순서 위반 0 / 샤드 {Shards}개 정상";

    public override void Dispose()
    {
        _sharded.Dispose();
        base.Dispose();
    }

    private sealed class OrderCheckingHandler : IEventHandler<SoakEvent>
    {
        private readonly Dictionary<long, long> _lastPerRoom = new();
        private long _count;
        private long _violations;

        public long Count => Volatile.Read(ref _count);
        public long OrderViolations => Volatile.Read(ref _violations);

        public void OnEvent(SoakEvent data, long sequence, bool endOfBatch)
        {
            // 샤드당 핸들러 하나이므로 단일 스레드 접근이다.
            if (_lastPerRoom.TryGetValue(data.Sequence, out long last) && data.Value <= last)
                _violations++;

            _lastPerRoom[data.Sequence] = data.Value;
            Interlocked.Increment(ref _count);
        }
    }
}
