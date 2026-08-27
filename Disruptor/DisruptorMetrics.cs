using System.Diagnostics.Metrics;

namespace Disruptor;

/// <summary>
/// Disruptor 파이프라인의 운영 지표. <c>System.Diagnostics.Metrics</c> 기반이므로
/// OpenTelemetry / Prometheus 익스포터에 그대로 연결됩니다.
/// </summary>
/// <remarks>
/// 운영에서 가장 중요한 지표는 <b>handler_lag</b>(각 핸들러가 커서로부터 뒤처진 정도)입니다.
/// 이 값이 계속 증가하면 해당 핸들러가 병목이며, 곧 프로듀서가 블록됩니다.
/// </remarks>
public sealed class DisruptorMetrics : IDisposable
{
    /// <summary>계측 이름. OpenTelemetry에서 이 이름으로 구독하세요.</summary>
    public const string MeterName = "DisruptorCS";

    private readonly Meter _meter;
    private readonly KeyValuePair<string, object?> _pipelineTag;

    private readonly Counter<long> _published;
    private readonly Counter<long> _processed;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _handlerErrors;
    private readonly Counter<long> _stalls;
    private readonly Histogram<long> _batchSize;
    private readonly Histogram<double> _publishLatencyUs;

    public DisruptorMetrics(string pipelineName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

        PipelineName = pipelineName;
        _meter = new Meter(MeterName, "1.0.0");
        _pipelineTag = new KeyValuePair<string, object?>("pipeline", pipelineName);

        _published = _meter.CreateCounter<long>("disruptor.published", "events", "게시된 이벤트 수");
        _processed = _meter.CreateCounter<long>("disruptor.processed", "events", "처리된 이벤트 수");
        _dropped = _meter.CreateCounter<long>("disruptor.dropped", "events", "혼잡으로 드롭된 이벤트 수");
        _rejected = _meter.CreateCounter<long>("disruptor.rejected", "events", "타임아웃으로 거절된 게시 수");
        _handlerErrors = _meter.CreateCounter<long>("disruptor.handler_errors", "errors", "핸들러 예외 수");
        _stalls = _meter.CreateCounter<long>("disruptor.stalls", "events", "프로듀서 스톨 감지 수");
        _batchSize = _meter.CreateHistogram<long>("disruptor.batch_size", "events", "배치 크기 분포");
        _publishLatencyUs = _meter.CreateHistogram<double>("disruptor.publish_latency_us", "us", "게시 지연 분포");
    }

    public string PipelineName { get; }

    public void RecordPublished(long count = 1)
        => _published.Add(count, _pipelineTag);

    public void RecordProcessed(string handler, long count)
        => _processed.Add(count, _pipelineTag, new KeyValuePair<string, object?>("handler", handler));

    public void RecordBatch(string handler, long batchSize)
        => _batchSize.Record(batchSize, _pipelineTag, new KeyValuePair<string, object?>("handler", handler));

    public void RecordDropped(int opCode = 0)
        => _dropped.Add(1, _pipelineTag, new KeyValuePair<string, object?>("op", opCode));

    public void RecordRejected(int opCode = 0)
        => _rejected.Add(1, _pipelineTag, new KeyValuePair<string, object?>("op", opCode));

    public void RecordHandlerError(string handler)
        => _handlerErrors.Add(1, _pipelineTag, new KeyValuePair<string, object?>("handler", handler));

    public void RecordStall(StallInfo info)
        => _stalls.Add(1, _pipelineTag, new KeyValuePair<string, object?>("handler", info.SlowestHandler));

    public void RecordPublishLatency(double microseconds)
        => _publishLatencyUs.Record(microseconds, _pipelineTag);

    /// <summary>
    /// 핸들러가 커서로부터 뒤처진 정도를 관측 게이지로 등록합니다. 가장 중요한 지표입니다.
    /// </summary>
    public void RegisterLagGauge(string handlerName, Func<long> lagProvider)
    {
        ArgumentNullException.ThrowIfNull(lagProvider);

        var tags = new[] { _pipelineTag, new KeyValuePair<string, object?>("handler", handlerName) };
        _meter.CreateObservableGauge("disruptor.handler_lag",
            () => new Measurement<long>(lagProvider(), tags),
            "events", "핸들러가 커서로부터 뒤처진 정도");
    }

    /// <summary>
    /// 링 버퍼 사용률(0~1)을 등록합니다. 0.8 이상이 지속되면 컨슈머 증설/최적화 신호입니다.
    /// </summary>
    public void RegisterUtilizationGauge(Func<double> utilizationProvider)
    {
        ArgumentNullException.ThrowIfNull(utilizationProvider);

        var tags = new[] { _pipelineTag };
        _meter.CreateObservableGauge("disruptor.utilization",
            () => new Measurement<double>(utilizationProvider(), tags),
            "ratio", "링 버퍼 사용률 (0~1)");
    }

    public void Dispose() => _meter.Dispose();
}
