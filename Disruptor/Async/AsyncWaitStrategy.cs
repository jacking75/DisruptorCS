using System.Collections.Concurrent;
using System.Threading.Tasks.Sources;
using Disruptor.WaitStrategy;

namespace Disruptor.Async;

/// <summary>
/// 스레드를 점유하지 않고 <see cref="ValueTask"/> 로 대기하는 전략.
/// </summary>
/// <remarks>
/// <para>
/// 동기 <see cref="WaitFor"/> 경로도 함께 제공하므로 동기 프로세서와 <b>같은 파이프라인에서 혼용</b>할 수 있습니다.
/// </para>
/// <para>
/// <b>포지셔닝</b>: async 소비자는 "저널 뒤의 느린 팬아웃"용입니다.
/// 코어 파이프라인(패킷 디코드 → 로직)은 동기로 유지하세요 —
/// await 는 스케줄링 지연과 continuation 비용을 더하므로 저지연 경로에는 맞지 않습니다.
/// </para>
/// <para>
/// <b>게시 지연 보호</b>: 대기자를 깨울 때 continuation 이 게시 스레드에서 인라인 실행되면
/// 게시 경로가 느려집니다. 이 구현은 완료 신호를 <see cref="ThreadPool"/> 로 넘겨 그것을 막습니다.
/// </para>
/// </remarks>
public sealed class AsyncWaitStrategy : IWaitStrategy, IAsyncWaitStrategy
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<Waiter> _asyncWaiters = new();
    private readonly int _timeoutMs;

    private int _numSyncWaiters;

    public AsyncWaitStrategy(TimeSpan? timeout = null)
    {
        double ms = (timeout ?? TimeSpan.FromMilliseconds(100)).TotalMilliseconds;
        _timeoutMs = ms < 1 ? 1 : (int)Math.Min(ms, int.MaxValue);
    }

    public bool IsBlockingStrategy => true;

    /// <inheritdoc/>
    public SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                                      CancellationToken cancellationToken)
    {
        long available = dependents.Value;
        if (available >= sequence)
            return available;

        lock (_gate)
        {
            _numSyncWaiters++;
            try
            {
                while ((available = dependents.Value) < sequence)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return SequenceWaitResult.Cancelled;

                    Monitor.Wait(_gate, _timeoutMs);
                }
            }
            finally
            {
                _numSyncWaiters--;
            }
        }

        return available;
    }

    /// <inheritdoc/>
    /// <remarks>Cancellation completes a pending async wait without requiring a publication signal.</remarks>
    public ValueTask<SequenceWaitResult> WaitForAsync(
        long sequence, DependentSequenceGroup dependents, CancellationToken cancellationToken)
    {
        long available = dependents.Value;
        if (available >= sequence)
            return new ValueTask<SequenceWaitResult>(available);

        if (cancellationToken.IsCancellationRequested)
            return new ValueTask<SequenceWaitResult>(SequenceWaitResult.Cancelled);

        var waiter = new Waiter(this, sequence, dependents, cancellationToken);
        _asyncWaiters.Enqueue(waiter);
        // Register after enqueue: an already-cancelled token must be able to remove its waiter.
        waiter.RegisterCancellation();

        // 큐에 넣은 뒤 재확인 — 그 사이에 게시가 일어났으면 스스로 완료시킨다.
        // (게시자가 큐를 비우는 시점과 우리가 큐에 넣는 시점의 경쟁을 닫는다)
        Interlocked.MemoryBarrier();
        waiter.TryComplete();

        return waiter.AsValueTask();
    }

    /// <inheritdoc cref="BlockingWaitStrategy.SignalAllWhenBlocking"/>
    public void SignalAllWhenBlocking()
    {
        Interlocked.MemoryBarrier();

        if (Volatile.Read(ref _numSyncWaiters) > 0)
        {
            lock (_gate)
            {
                Monitor.PulseAll(_gate);
            }
        }

        CompleteAsyncWaiters();
    }

    private void CompleteAsyncWaiters()
    {
        if (_asyncWaiters.IsEmpty)
            return;

        // 완료 가능한 대기자만 꺼내고, 아직인 것은 되돌려 놓는다.
        int pending = _asyncWaiters.Count;
        for (int i = 0; i < pending && _asyncWaiters.TryDequeue(out Waiter? waiter); i++)
        {
            if (!waiter.TryComplete())
                _asyncWaiters.Enqueue(waiter);
        }
    }

    /// <summary>
    /// 하나의 비동기 대기자. <see cref="ManualResetValueTaskSourceCore{T}"/> 로 할당을 최소화합니다.
    /// </summary>
    private sealed class Waiter : IValueTaskSource<SequenceWaitResult>
    {
        private readonly AsyncWaitStrategy _owner;
        private readonly long _sequence;
        private readonly DependentSequenceGroup _dependents;
        private readonly CancellationToken _cancellationToken;

        private ManualResetValueTaskSourceCore<SequenceWaitResult> _core;
        private int _completed;
        private CancellationTokenRegistration _registration;

        public Waiter(AsyncWaitStrategy owner, long sequence, DependentSequenceGroup dependents, CancellationToken cancellationToken)
        {
            _owner = owner;
            _sequence = sequence;
            _dependents = dependents;
            _cancellationToken = cancellationToken;

            // continuation 이 게시 스레드에서 인라인 실행되면 게시 경로가 느려진다.
            _core.RunContinuationsAsynchronously = true;
        }

        public ValueTask<SequenceWaitResult> AsValueTask() => new(this, _core.Version);

        public void RegisterCancellation()
        {
            if (_cancellationToken.CanBeCanceled)
                _registration = _cancellationToken.Register(static state => ((Waiter)state!).Cancel(), this);
        }

        private void Cancel()
        {
            // Complete directly: a publisher may temporarily have dequeued this waiter, so
            // scanning the queue alone can miss it and leave an idle consumer asleep forever.
            Complete(SequenceWaitResult.Cancelled);
            _owner.CompleteAsyncWaiters();
        }

        /// <summary>조건이 충족되었으면 완료시키고 true 를 반환합니다.</summary>
        public bool TryComplete()
        {
            if (Volatile.Read(ref _completed) != 0)
                return true;

            if (_cancellationToken.IsCancellationRequested)
                return Complete(SequenceWaitResult.Cancelled);

            long available = _dependents.Value;
            if (available < _sequence)
                return false;

            return Complete(available);
        }

        private bool Complete(SequenceWaitResult result)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return true;

            _core.SetResult(result);
            return true;
        }

        public SequenceWaitResult GetResult(short token)
        {
            try { return _core.GetResult(token); }
            finally { _registration.Dispose(); }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token,
                                ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }
}

/// <summary>
/// 비동기 대기를 지원하는 전략.
/// </summary>
public interface IAsyncWaitStrategy : IWaitStrategy
{
    /// <summary>주어진 시퀀스가 소비 가능해질 때까지 비동기로 대기합니다.</summary>
    ValueTask<SequenceWaitResult> WaitForAsync(
        long sequence, DependentSequenceGroup dependents, CancellationToken cancellationToken);
}
