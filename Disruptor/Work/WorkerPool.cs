using System.Diagnostics;

namespace Disruptor.Work;

/// <summary>
/// 이벤트를 여러 워커에게 <b>분배</b>해서 처리하는 풀.
/// 하나의 이벤트는 정확히 한 워커가 처리합니다.
/// </summary>
public sealed class WorkerPool<T> : IDisposable where T : class
{
    private readonly RingBuffer<T> _ringBuffer;
    private readonly Sequence _workSequence = new();
    private readonly WorkProcessor<T>[] _processors;
    private readonly List<Thread> _threads = new();
    private int _started;

    public WorkerPool(
        RingBuffer<T> ringBuffer,
        ISequenceBarrier barrier,
        Func<int, IWorkHandler<T>> workHandlerFactory,
        int workerCount,
        string? namePrefix = null)
    {
        ArgumentNullException.ThrowIfNull(ringBuffer);
        ArgumentNullException.ThrowIfNull(barrier);
        ArgumentNullException.ThrowIfNull(workHandlerFactory);
        if (workerCount < 1)
            throw new ArgumentOutOfRangeException(nameof(workerCount));

        _ringBuffer = ringBuffer;
        _processors = new WorkProcessor<T>[workerCount];

        for (int i = 0; i < workerCount; i++)
        {
            var handler = workHandlerFactory(i);
            _processors[i] = new WorkProcessor<T>(
                ringBuffer, barrier, handler, _workSequence,
                name: $"{namePrefix ?? handler.GetType().Name}-{i}");
        }
    }

    /// <summary>게이팅에 등록해야 할 시퀀스들.</summary>
    public Sequence[] Sequences => Array.ConvertAll(_processors, p => p.Sequence);

    public IReadOnlyList<WorkProcessor<T>> Processors => _processors;

    /// <summary>워커들이 공유하는 클레임 카운터.</summary>
    public Sequence WorkSequence => _workSequence;

    public void SetExceptionHandler(IExceptionHandler<T> exceptionHandler)
    {
        foreach (var p in _processors)
            p.SetExceptionHandler(exceptionHandler);
    }

    public void SetMetrics(DisruptorMetrics? metrics)
    {
        foreach (var p in _processors)
            p.Metrics = metrics;
    }

    /// <summary>
    /// 워커 스레드를 시작합니다. 모든 워커가 이벤트 루프에 진입한 뒤 반환합니다.
    /// </summary>
    public void Start(CancellationToken cancellationToken, ProcessorThreadOptions? threadOptions = null)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("WorkerPool이 이미 시작되었습니다.");

        // 워커들은 현재 커서부터 처리한다.
        long cursor = _ringBuffer.Cursor;
        _workSequence.SetValue(cursor);
        foreach (var p in _processors)
            p.Sequence.SetValue(cursor);

        var gate = new CountdownEvent(_processors.Length);

        for (int i = 0; i < _processors.Length; i++)
        {
            var processor = _processors[i];
            int index = i;

            var thread = ProcessorThreadFactory.Create(
                processor, cancellationToken, gate, threadOptions, index);

            _threads.Add(thread);
            thread.Start();
        }

        gate.Wait();
        gate.Dispose();
    }

    /// <summary>
    /// 게시된 이벤트를 모두 소비할 때까지 기다린 뒤 정지합니다.
    /// </summary>
    public void DrainAndHalt(TimeSpan timeout)
    {
        long cursor = _ringBuffer.Cursor;
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (_workSequence.Value >= cursor && AllWorkersIdle(cursor))
                break;

            Thread.Sleep(1);
        }

        Halt();
    }

    private bool AllWorkersIdle(long cursor)
    {
        foreach (var p in _processors)
        {
            if (p.Sequence.Value < cursor)
                return false;
        }
        return true;
    }

    public void Halt()
    {
        foreach (var p in _processors)
            p.Halt();

        foreach (var t in _threads)
            t.Join(TimeSpan.FromSeconds(2));

        _threads.Clear();
    }

    public void Dispose()
    {
        Halt();
        foreach (var p in _processors)
            p.Dispose();
    }
}
