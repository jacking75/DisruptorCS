using Disruptor;

namespace Disruptor.Tests;

/// <summary>테스트용 참조 타입 이벤트.</summary>
public sealed class TestEvent : IClearable
{
    public long Value;
    public long Sequence;
    public byte[]? Payload;

    public void Clear()
    {
        Value = 0;
        Sequence = 0;
        Payload = null;
    }

    public override string ToString() => $"TestEvent(Value={Value}, Sequence={Sequence})";
}

/// <summary>테스트용 값 타입 이벤트. 32바이트로 캐시 라인에 2개가 들어간다.</summary>
public struct TestValueEvent
{
    public long Value;
    public long Sequence;
    public long TickId;
    public int Flags;
    public int Padding;
}

/// <summary>아무것도 하지 않는 핸들러.</summary>
public sealed class NoopHandler : IEventHandler<TestEvent>
{
    private long _count;

    public long Count => Volatile.Read(ref _count);

    public void OnEvent(TestEvent data, long sequence, bool endOfBatch)
        => Interlocked.Increment(ref _count);
}

/// <summary>
/// 각 이벤트가 정확히 한 번, 시퀀스 순서대로 도착했는지 검증하는 핸들러.
/// </summary>
public sealed class VerifyingHandler : IEventHandler<TestEvent>
{
    private long _count;
    private long _mismatches;
    private long _outOfOrder;
    private long _lastSequence = -1;
    private readonly ManualResetEventSlim? _completion;
    private readonly long _expected;

    public VerifyingHandler(long expected = -1, ManualResetEventSlim? completion = null)
    {
        _expected = expected;
        _completion = completion;
    }

    public long Count => Volatile.Read(ref _count);
    public long Mismatches => Volatile.Read(ref _mismatches);
    public long OutOfOrder => Volatile.Read(ref _outOfOrder);
    public long LastSequence => Volatile.Read(ref _lastSequence);
    public long BatchStarts { get; private set; }
    public long MaxBatchSize { get; private set; }
    public long Timeouts { get; private set; }

    public void OnBatchStart(long batchSize)
    {
        BatchStarts++;
        if (batchSize > MaxBatchSize) MaxBatchSize = batchSize;
    }

    public void OnTimeout(long lastProcessedSequence) => Timeouts++;

    public void OnEvent(TestEvent data, long sequence, bool endOfBatch)
    {
        // 슬롯이 덮어써졌다면 값이 시퀀스와 어긋난다.
        if (data.Sequence != sequence)
            Interlocked.Increment(ref _mismatches);

        long previous = Volatile.Read(ref _lastSequence);
        if (sequence != previous + 1)
            Interlocked.Increment(ref _outOfOrder);

        Volatile.Write(ref _lastSequence, sequence);

        long n = Interlocked.Increment(ref _count);
        if (_expected > 0 && n == _expected)
            _completion?.Set();
    }
}

/// <summary>지정한 시퀀스에서 예외를 던지는 핸들러.</summary>
public sealed class ThrowingHandler : IEventHandler<TestEvent>
{
    private readonly Func<long, bool> _shouldThrow;
    private long _processed;

    public ThrowingHandler(Func<long, bool> shouldThrow) => _shouldThrow = shouldThrow;

    public long Processed => Volatile.Read(ref _processed);

    public void OnEvent(TestEvent data, long sequence, bool endOfBatch)
    {
        if (_shouldThrow(sequence))
            throw new InvalidOperationException($"의도적 예외 at {sequence}");

        Interlocked.Increment(ref _processed);
    }
}

/// <summary>이벤트당 일정 시간을 소비하는 느린 핸들러.</summary>
public sealed class SlowHandler : IEventHandler<TestEvent>
{
    private readonly int _sleepMs;
    private long _count;

    public SlowHandler(int sleepMs) => _sleepMs = sleepMs;

    public long Count => Volatile.Read(ref _count);

    public void OnEvent(TestEvent data, long sequence, bool endOfBatch)
    {
        Thread.Sleep(_sleepMs);
        Interlocked.Increment(ref _count);
    }
}

/// <summary>상위 핸들러의 진행 상황을 기록해 순서 위반을 검출하는 하위 핸들러.</summary>
public sealed class DependencyCheckHandler : IEventHandler<TestEvent>
{
    private readonly Sequence _upstream;
    private long _violations;
    private long _count;

    public DependencyCheckHandler(Sequence upstream) => _upstream = upstream;

    public long Violations => Volatile.Read(ref _violations);
    public long Count => Volatile.Read(ref _count);

    public void OnEvent(TestEvent data, long sequence, bool endOfBatch)
    {
        // 하위 핸들러는 상위 핸들러를 절대 앞지를 수 없다.
        if (_upstream.Value < sequence)
            Interlocked.Increment(ref _violations);

        Interlocked.Increment(ref _count);
    }
}
