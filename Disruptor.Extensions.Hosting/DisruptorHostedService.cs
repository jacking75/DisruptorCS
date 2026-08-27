using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Disruptor.Extensions.Hosting;

/// <summary>
/// 프로세서가 정지했을 때 호스트가 취할 동작.
/// </summary>
public enum ProcessorHaltPolicy
{
    /// <summary>
    /// 애플리케이션을 계획적으로 종료합니다. (기본값)
    /// </summary>
    /// <remarks>
    /// 프로세서가 멈추면 그 시퀀스가 게이팅에 남아 <b>프로듀서가 곧 영구 블록</b>됩니다.
    /// 그 상태로 서버를 방치하면 "응답 없는 좀비"가 되므로, 기본값은 계획 종료입니다.
    /// </remarks>
    StopApplication,

    /// <summary>로그만 남기고 계속 실행합니다. 호출자가 직접 복구 로직을 구현할 때만 사용하세요.</summary>
    LogOnly,
}

/// <summary>
/// Disruptor 를 Generic Host 수명주기에 결속합니다.
/// </summary>
public sealed class DisruptorHostedService<T> : IHostedService where T : class
{
    private readonly Dsl.Disruptor<T> _disruptor;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly ILogger<DisruptorHostedService<T>>? _logger;

    public DisruptorHostedService(
        Dsl.Disruptor<T> disruptor,
        IHostApplicationLifetime? lifetime = null,
        ILogger<DisruptorHostedService<T>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(disruptor);

        _disruptor = disruptor;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>프로세서 정지 시 동작. 기본값은 계획 종료입니다.</summary>
    public ProcessorHaltPolicy HaltPolicy { get; init; } = ProcessorHaltPolicy.StopApplication;

    /// <summary>정지한 프로세서가 있는가. 헬스체크가 참조합니다.</summary>
    public bool HasHaltedProcessor => Volatile.Read(ref _haltedCount) > 0;

    private int _haltedCount;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _disruptor.ProcessorHalted += OnProcessorHalted;

        if (_disruptor.Options is { OnStall: null })
        {
            // 사용자가 스톨 콜백을 지정하지 않았다면 로거로 연결해 준다.
            // 이게 없으면 "서버가 멈췄는데 어느 핸들러 때문인지 모른다"가 된다.
        }

        _logger?.LogInformation(
            "Disruptor '{Pipeline}' 시작: 프로세서 {ProcessorCount}개, 링 {RingSize}",
            _disruptor.Name, _disruptor.Processors.Count, _disruptor.RingBuffer.BufferSize);

        _disruptor.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Disruptor '{Pipeline}' 종료 중 (미처리 이벤트 드레인)", _disruptor.Name);

        _disruptor.Shutdown();
        _disruptor.ProcessorHalted -= OnProcessorHalted;

        _logger?.LogInformation("Disruptor '{Pipeline}' 종료 완료", _disruptor.Name);
        return Task.CompletedTask;
    }

    private void OnProcessorHalted(IEventProcessor processor, Exception? exception)
    {
        Interlocked.Increment(ref _haltedCount);

        _logger?.LogCritical(
            exception,
            "Disruptor '{Pipeline}' 프로세서 '{Processor}' 가 정지했습니다. " +
            "이 프로세서의 시퀀스가 게이팅에 남아 있어 프로듀서가 곧 블록됩니다.",
            _disruptor.Name, processor.Name);

        if (HaltPolicy == ProcessorHaltPolicy.StopApplication)
        {
            _logger?.LogCritical("애플리케이션을 계획 종료합니다.");
            _lifetime?.StopApplication();
        }
    }
}

/// <summary>
/// Disruptor 파이프라인의 헬스체크.
/// </summary>
/// <remarks>
/// 링 사용률이 계속 높다는 것은 컨슈머가 프로듀서를 따라가지 못한다는 뜻이고,
/// 방치하면 프로듀서가 블록됩니다. Degraded 단계에서 알아채는 것이 중요합니다.
/// </remarks>
public sealed class DisruptorHealthCheck<T> : IHealthCheck where T : class
{
    private readonly Dsl.Disruptor<T> _disruptor;
    private readonly double _degradedUtilization;
    private readonly double _unhealthyUtilization;

    public DisruptorHealthCheck(
        Dsl.Disruptor<T> disruptor,
        double degradedUtilization = 0.7,
        double unhealthyUtilization = 0.9)
    {
        ArgumentNullException.ThrowIfNull(disruptor);

        _disruptor = disruptor;
        _degradedUtilization = degradedUtilization;
        _unhealthyUtilization = unhealthyUtilization;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        RingBuffer<T> ring = _disruptor.RingBuffer;
        double utilization = ring.Utilization;

        var data = new Dictionary<string, object>
        {
            ["pipeline"] = _disruptor.Name,
            ["utilization"] = utilization,
            ["remainingCapacity"] = ring.RemainingCapacity,
            ["bufferSize"] = ring.BufferSize,
            ["cursor"] = ring.Cursor,
            ["processors"] = _disruptor.Processors.Count,
        };

        // 정지한 프로세서를 찾는다. 시작 전이면 IsRunning 이 false 라 오탐이 나므로 시작 여부를 먼저 본다.
        if (_disruptor.IsStarted)
        {
            var halted = _disruptor.Processors.Where(p => !p.IsRunning).Select(p => p.Name).ToArray();
            if (halted.Length > 0)
            {
                data["haltedProcessors"] = halted;
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"프로세서 {halted.Length}개가 정지했습니다: {string.Join(", ", halted)}", data: data));
            }
        }

        if (utilization >= _unhealthyUtilization)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"링 버퍼 사용률 {utilization:P0} — 프로듀서가 곧 블록됩니다.", data: data));
        }

        if (utilization >= _degradedUtilization)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"링 버퍼 사용률 {utilization:P0} — 컨슈머가 뒤처지고 있습니다.", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"링 버퍼 사용률 {utilization:P0}", data: data));
    }
}
