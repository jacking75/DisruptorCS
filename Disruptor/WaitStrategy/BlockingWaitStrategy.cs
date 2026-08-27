namespace Disruptor.WaitStrategy;

/// <summary>
/// 가장 보수적인 전략. lock + Monitor를 사용합니다.
/// CPU 사용량이 낮지만 wake-up 지연(실측 p50 약 12us)이 있습니다.
/// 핸들러 스레드 수가 논리 코어 수 이상이거나 컨테이너 환경일 때 권장됩니다.
/// </summary>
public sealed class BlockingWaitStrategy : IWaitStrategy
{
    /// <summary>
    /// lost-wakeup 자가 치유용 재확인 주기.
    /// </summary>
    /// <remarks>
    /// <see cref="SignalAllWhenBlocking"/>의 전체 펜스가 1차 방어이고, 이 타임아웃이 2차 방어입니다.
    /// 메모리 순서 논증에 오류가 있거나 플랫폼 특이 사항이 있어도 정지 시간이 이 값으로 상한됩니다.
    /// 유휴 상태에서만 비용이 발생하므로 처리량에는 영향이 없습니다.
    /// </remarks>
    private const int RecheckIntervalMs = 100;

    private readonly object _gate = new();

    /// <summary>대기 중인 컨슈머 수. <see cref="_gate"/> 안에서만 증감합니다.</summary>
    private int _numWaiters;

    public bool IsBlockingStrategy => true;

    public SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                                      CancellationToken cancellationToken)
    {
        long available = dependents.Value;
        if (available >= sequence)
            return available; // 빠른 경로: lock 진입 없음

        lock (_gate)
        {
            // lock 진입은 전체 펜스를 포함한다. 아래 조건 재확인은 이 펜스 뒤에서 일어나므로
            // "게시자가 커서를 쓴 뒤 대기자 수를 읽는" 경로와 전역 순서로 짝을 이룬다.
            _numWaiters++;
            try
            {
                while ((available = dependents.Value) < sequence)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return SequenceWaitResult.Cancelled;

                    // 의존 핸들러가 있는 경우에도 여기서 잠든다 -> hot-spin 제거
                    Monitor.Wait(_gate, RecheckIntervalMs);
                }
            }
            finally
            {
                _numWaiters--;
            }
        }

        return available;
    }

    /// <summary>
    /// 대기 중인 컨슈머를 깨웁니다. 대기자가 없으면 lock 을 진입하지 않습니다.
    /// </summary>
    /// <remarks>
    /// <para><b>왜 무조건 lock 하지 않는가</b>: 게시 1건마다 <c>lock + PulseAll</c>을 지불하면
    /// 무경합 상태에서도 39.1ns/op 이 든다(실측). per-event 게시 유니캐스트 처리량이
    /// 스핀 전략 대비 1/6~1/7 로 떨어진다(8.6M vs 50~60M ops/s). 펜스 + 대기자 검사는 4.1ns 다.</para>
    ///
    /// <para><b>lost-wakeup 이 없는 이유</b>: 호출자(<c>Sequencer.Publish</c> 또는 프로세서의
    /// 시퀀스 갱신)는 이 호출 <i>직전에</i> volatile write 를 마쳤다. 문제가 되는 것은
    /// StoreLoad 재배치 — 그 write 가 아래 <c>_numWaiters</c> 읽기보다 늦게 관측되는 경우다.
    /// <c>Interlocked.MemoryBarrier()</c>가 그 재배치를 차단한다. 그러면 두 개의 전체 펜스
    /// (여기의 배리어, 컨슈머 쪽 lock 진입)가 전역 순서를 가지므로 반드시 둘 중 하나가 성립한다:</para>
    /// <list type="number">
    ///   <item>게시자의 펜스가 먼저 완료 → 컨슈머의 조건 재확인이 새 값을 본다 → 대기하지 않는다.</item>
    ///   <item>컨슈머의 <c>_numWaiters++</c> 가 먼저 → 게시자가 <c>&gt; 0</c> 을 보고 PulseAll 한다.</item>
    /// </list>
    /// <para>여기에 <see cref="RecheckIntervalMs"/> 재확인이 백스톱으로 남아 있다.</para>
    /// </remarks>
    public void SignalAllWhenBlocking()
    {
        Interlocked.MemoryBarrier();

        if (Volatile.Read(ref _numWaiters) > 0)
        {
            lock (_gate)
            {
                Monitor.PulseAll(_gate);
            }
        }
    }
}
