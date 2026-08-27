using System.Diagnostics;

namespace Disruptor.Dsl;

/// <summary>
/// 참조 타입 / 값 타입 Disruptor가 공유하는 수명 주기 관리
/// (스레드 생성, 시작 동기화, 드레인, 종료, 메트릭 연결).
/// </summary>
public abstract class DisruptorBase : IDisposable
{
    private readonly List<Thread> _threads = new();
    private int _started;

    protected DisruptorBase(DisruptorOptions options)
    {
        Options = options;
        ShutdownSource = new CancellationTokenSource();

        if (options.EnableMetrics)
            Metrics = new DisruptorMetrics(options.Name, options.MetricsBatchSampling);
    }

    /// <summary>이 파이프라인의 옵션.</summary>
    public DisruptorOptions Options { get; }

    protected CancellationTokenSource ShutdownSource { get; }

    protected List<IEventProcessor> RegisteredProcessors { get; } = new();

    /// <summary>이벤트 타입에 무관한 링 버퍼 조정 인터페이스.</summary>
    protected abstract IRingBufferCore Core { get; }

    /// <summary>파이프라인 이름.</summary>
    public string Name => Options.Name;

    /// <summary>메트릭 수집기. <see cref="DisruptorOptions.EnableMetrics"/>가 true일 때만 생성됩니다.</summary>
    public DisruptorMetrics? Metrics { get; }

    /// <summary>등록된 모든 프로세서.</summary>
    public IReadOnlyList<IEventProcessor> Processors => RegisteredProcessors;

    /// <summary>시작되었는가.</summary>
    public bool IsStarted => Volatile.Read(ref _started) == 1;

    /// <summary>
    /// 실행할 작업이 등록되었는가. 워커 풀처럼 별도 스레드를 관리하는 구성 요소가 있으면 재정의합니다.
    /// </summary>
    protected virtual bool HasRegisteredWork => RegisteredProcessors.Count > 0;

    /// <summary>
    /// 프로세서가 예외로 정지했을 때 발생합니다.
    /// <b>반드시 구독하세요</b> — 정지한 프로세서의 시퀀스가 게이팅에 남아 있으면
    /// 프로듀서가 곧 영구 블록됩니다.
    /// </summary>
    public event Action<IEventProcessor, Exception?>? ProcessorHalted;

    protected void RaiseProcessorHalted(IEventProcessor processor, Exception? ex)
        => ProcessorHalted?.Invoke(processor, ex);

    protected void EnsureNotStarted()
    {
        if (Volatile.Read(ref _started) != 0)
            throw new InvalidOperationException("Disruptor가 시작된 뒤에는 핸들러를 추가할 수 없습니다.");
    }

    /// <summary>
    /// 리프 노드만 게이팅 시퀀스로 유지합니다.
    /// </summary>
    /// <remarks>
    /// 반드시 <b>추가 후 제거</b> 순서여야 합니다. 반대로 하면 게이팅 집합이 순간적으로 비어
    /// 프로듀서가 미소비 슬롯을 덮어쓸 수 있습니다.
    /// </remarks>
    protected void UpdateGatingSequences(Sequence[] barrierSequences, Sequence[] newSequences)
    {
        Core.AddGatingSequences(newSequences);

        foreach (var seq in barrierSequences)
            Core.RemoveGatingSequence(seq);
    }

    protected void RegisterProcessorDiagnostics(IEventProcessor processor)
    {
        Core.SetGatingSequenceLabel(processor.Sequence, processor.Name);
        Metrics?.RegisterLagGauge(processor.Name, () => Core.Cursor - processor.Sequence.Value);
    }

    /// <summary>
    /// 모든 프로세서 스레드를 시작하고, 전부 이벤트 루프에 진입한 뒤 반환합니다.
    /// </summary>
    /// <remarks>
    /// 시작 동기화가 없으면 <c>Start()</c> 직후 <c>Shutdown()</c> 호출 시 정지 신호를 잃고
    /// 종료가 타임아웃까지 지연됩니다.
    /// </remarks>
    protected void StartProcessorThreads()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Disruptor가 이미 시작되었습니다.");

        OnBeforeStart();

        if (!HasRegisteredWork)
            throw new InvalidOperationException("등록된 핸들러가 없습니다. HandleEventsWith 를 먼저 호출하세요.");

        Metrics?.RegisterUtilizationGauge(() => Core.Utilization);

        if (RegisteredProcessors.Count > 0)
        {
            var gate = new CountdownEvent(RegisteredProcessors.Count);

            for (int i = 0; i < RegisteredProcessors.Count; i++)
            {
                var thread = ProcessorThreadFactory.Create(
                    RegisteredProcessors[i], ShutdownSource.Token, gate, ThreadOptionsForStart(), i);

                _threads.Add(thread);
                thread.Start();
            }

            gate.Wait();
            gate.Dispose();
        }

        OnAfterStart();
    }

    private ProcessorThreadOptions ThreadOptionsForStart()
        => (Options.ThreadOptions ?? ProcessorThreadOptions.Default) with { NamePrefix = Options.Name };

    /// <summary>시작 직전 훅 (슬롯 자동 정리 설정 등).</summary>
    protected virtual void OnBeforeStart() { }

    /// <summary>프로세서 스레드가 모두 준비된 직후 훅 (워커 풀 시작 등).</summary>
    protected virtual void OnAfterStart() { }

    /// <summary>종료 시 추가로 정지시켜야 할 대상 훅.</summary>
    protected virtual void OnHalt() { }

    /// <summary>
    /// Disruptor를 정지합니다. 타임아웃 내에서 미처리 이벤트를 모두 소비한 후 종료합니다.
    /// </summary>
    public void Shutdown(TimeSpan? timeout = null)
    {
        if (Interlocked.CompareExchange(ref _started, 2, 1) != 1)
            return; // 시작하지 않았거나 이미 종료됨

        timeout ??= Options.ShutdownTimeout;

        // 단조 시계를 사용한다. DateTime.UtcNow 는 NTP 보정이나 수동 시간 변경에 취약하다.
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.Value.TotalSeconds * Stopwatch.Frequency);

        // Phase 1: 게시된 이벤트를 모두 소비할 때까지 대기
        DrainProcessors(deadline);

        // Phase 2: 프로세서 중지
        foreach (var processor in RegisteredProcessors)
            processor.Halt();

        ShutdownSource.Cancel();
        OnHalt();

        // Phase 3: 스레드 종료 대기
        foreach (var thread in _threads)
        {
            long remainingTicks = deadline - Stopwatch.GetTimestamp();
            int remainingMs = remainingTicks > 0 ? (int)(remainingTicks * 1000 / Stopwatch.Frequency) : 0;

            if (!thread.Join(Math.Max(remainingMs, 100)))
            {
                Console.Error.WriteLine(
                    $"[Disruptor][WARN] '{Name}': 스레드 '{thread.Name}'가 시간 내에 종료되지 않았습니다.");
            }
        }

        _threads.Clear();
    }

    private void DrainProcessors(long deadline)
    {
        long start = MinimumProcessorSequence();
        if (start == long.MaxValue)
            return; // 추적할 컨슈머가 없음

        long cursor = Core.Cursor;
        if (cursor < 0)
            return; // 게시된 이벤트 없음

        // 다중 프로듀서에서 커서는 "점유" 최대치이므로, 실제로 연속 게시된 상한을 목표로 삼는다.
        long target = Core.GetHighestPublishedSequence(start + 1, cursor);

        // Thread.Sleep(1) 을 쓰지 않는다. Windows 기본 타이머 분해능(15.6ms) 때문에
        // "곧 끝날 드레인"에도 최소 15ms 가 붙는다 — 라이브러리의 다른 모든 대기 경로와 같은 규칙이다.
        // 대부분의 드레인은 마이크로초 단위로 끝나므로 스핀이 압도적으로 유리하다.
        var spin = new SpinWait();

        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (MinimumProcessorSequence() >= target)
                return;

            spin.SpinOnce(sleep1Threshold: -1);
        }

        Console.Error.WriteLine(
            $"[Disruptor][WARN] '{Name}': 종료 드레인 타임아웃. " +
            $"목표 시퀀스 {target}, 도달 {MinimumProcessorSequence()} (미처리 이벤트가 유실될 수 있습니다).");
    }

    /// <summary>
    /// 모든 컨슈머(워커 포함)의 최소 시퀀스. 추적 대상이 없으면 <see cref="long.MaxValue"/>.
    /// </summary>
    /// <remarks>
    /// 재정의할 때는 반드시 <c>base</c> 값에서 시작해 <c>Math.Min</c>으로 좁혀야 합니다.
    /// 여기서 "없음"을 -1로 표현하면, 배치 프로세서 없이 워커 풀만 있는 구성에서
    /// 최솟값이 -1에 고정되어 드레인이 영원히 완료되지 않습니다.
    /// </remarks>
    protected virtual long MinimumProcessorSequence()
    {
        long min = long.MaxValue;

        foreach (var processor in RegisteredProcessors)
            min = Math.Min(min, processor.Sequence.Value);

        return min;
    }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);

        Shutdown();
        ShutdownSource.Dispose();

        foreach (var processor in RegisteredProcessors)
        {
            if (processor is IDisposable disposable)
                disposable.Dispose();
        }

        Metrics?.Dispose();
    }
}
