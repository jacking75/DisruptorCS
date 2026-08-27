using Disruptor.WaitStrategy;

namespace Disruptor.Dsl;

/// <summary>
/// Disruptor DSL 동작 옵션.
/// </summary>
public sealed record DisruptorOptions
{
    public static DisruptorOptions Default { get; } = new();

    /// <summary>파이프라인 이름. 스레드 이름 / 메트릭 태그 / 진단 메시지에 사용됩니다.</summary>
    public string Name { get; init; } = "default";

    /// <summary>대기 전략을 지정하지 않았을 때 사용할 기본 전략.</summary>
    /// <remarks>
    /// 기본값은 1ms <see cref="TimeoutBlockingWaitStrategy"/>입니다.
    /// 스핀 전략을 기본으로 두면 컨테이너 환경에서 CPU 쿼터를 소진해
    /// CFS 스로틀링을 유발하므로, 안전한 쪽을 기본값으로 삼습니다.
    /// </remarks>
    public Func<IWaitStrategy> DefaultWaitStrategyFactory { get; init; }
        = static () => new TimeoutBlockingWaitStrategy(TimeSpan.FromMilliseconds(1));

    /// <summary>링 버퍼가 가득 찼을 때 프로듀서의 대기 정책.</summary>
    public IProducerBackoff ProducerBackoff { get; init; } = SpinOnlyBackoff.Instance;

    /// <summary>프로듀서가 이 시간을 넘게 블록되면 <see cref="OnStall"/>이 호출됩니다.</summary>
    public TimeSpan StallThreshold { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>스톨 감지 콜백. 어느 핸들러가 병목인지 알려줍니다.</summary>
    public Action<StallInfo>? OnStall { get; init; }

    /// <inheritdoc cref="SequencerOptions.StallReportInterval"/>
    public TimeSpan StallReportInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>종료 시 미처리 이벤트를 드레인할 최대 시간.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>프로세서 스레드 생성 옵션.</summary>
    public ProcessorThreadOptions? ThreadOptions { get; init; }

    /// <summary>
    /// 리프 프로세서가 처리한 뒤 <see cref="IClearable"/> 이벤트의 참조를 자동으로 해제할지 여부.
    /// </summary>
    public bool AutoClearSlots { get; init; } = true;

    /// <summary><c>System.Diagnostics.Metrics</c> 기반 지표를 수집할지 여부.</summary>
    public bool EnableMetrics { get; init; }

    /// <summary>DEBUG 빌드에서 ProducerType.Single 오용을 검사할지 여부.</summary>
    public bool ValidateSingleProducerThread { get; init; } = true;

    /// <inheritdoc cref="SequencerOptions.UsePinnedObjectHeap"/>
    public bool UsePinnedObjectHeap { get; init; }

    /// <summary>
    /// 한 번에 처리할 최대 이벤트 수. 모든 프로세서에 적용됩니다. 기본값은 무제한입니다.
    /// </summary>
    /// <inheritdoc cref="BatchEventProcessor{T}.MaxBatchSize" path="/remarks"/>
    public int MaxBatchSize { get; init; } = int.MaxValue;

    /// <summary>
    /// 배치 메트릭 샘플링 주기. N 이면 N 배치당 1회만 기록하고 값을 N 배로 보정합니다.
    /// 초고빈도 파이프라인에서 메트릭의 상시 비용을 낮춥니다.
    /// </summary>
    public int MetricsBatchSampling { get; init; } = 1;

    internal IWaitStrategy CreateDefaultWaitStrategy() => DefaultWaitStrategyFactory();

    /// <summary>스레드 이름 접두사에 파이프라인 이름을 반영한 스레드 옵션을 만듭니다.</summary>
    public DisruptorOptions WithName(string name)
        => this with
        {
            Name = name,
            ThreadOptions = (ThreadOptions ?? ProcessorThreadOptions.Default) with { NamePrefix = name },
        };
}
