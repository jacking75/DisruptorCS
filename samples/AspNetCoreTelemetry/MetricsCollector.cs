using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Disruptor;
using Microsoft.Extensions.Hosting;

namespace AspNetCoreTelemetry;

/// <summary>
/// <c>DisruptorCS</c> 미터를 구독해 최신값을 보관합니다.
/// </summary>
/// <remarks>
/// <para>
/// 이 클래스는 <b>OpenTelemetry 를 흉내 내려는 것이 아닙니다.</b> 실제 서비스라면
/// <c>AddOpenTelemetry().WithMetrics(m => m.AddMeter(DisruptorMetrics.MeterName))</c> 한 줄이면 됩니다.
/// 여기서는 외부 패키지 없이도 샘플이 돌아가도록 <see cref="MeterListener"/> 로 같은 계측을 직접 읽습니다.
/// </para>
/// <para>
/// 즉 이 파일이 증명하는 것은 "계측이 진짜로 방출되고 있다"이지, 수집기의 구현 방식이 아닙니다.
/// </para>
/// </remarks>
public sealed class MetricsCollector : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<string, double> _values = new();
    private readonly ConcurrentDictionary<string, long> _counts = new();
    private MeterListener? _listener;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new MeterListener();

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            // DisruptorCS 미터만 구독한다.
            if (instrument.Meter.Name == DisruptorMetrics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.Start();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Dispose();
        _listener = null;
        return Task.CompletedTask;
    }

    private void OnMeasurement<T>(Instrument instrument, T measurement,
                                  ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
        where T : struct
    {
        string key = Key(instrument, tags);
        double value = Convert.ToDouble(measurement);

        switch (instrument)
        {
            case Counter<long>:
                _values.AddOrUpdate(key, value, (_, previous) => previous + value);
                break;

            case Histogram<long> or Histogram<double>:
                // 실제 수집기라면 분포를 유지한다. 여기서는 합·건수만 보관해 평균을 낸다.
                _values.AddOrUpdate(key, value, (_, previous) => previous + value);
                _counts.AddOrUpdate(key, 1, (_, previous) => previous + 1);
                break;

            default:   // ObservableGauge — 최신값으로 덮어쓴다
                _values[key] = value;
                break;
        }
    }

    private static string Key(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
            return instrument.Name;

        var parts = new List<string>(tags.Length);
        foreach (var tag in tags)
            parts.Add($"{tag.Key}={tag.Value}");

        parts.Sort(StringComparer.Ordinal);   // 태그 순서에 따라 키가 갈라지지 않게
        return $"{instrument.Name}{{{string.Join(',', parts)}}}";
    }

    /// <summary>현재까지 관측된 값들의 스냅샷.</summary>
    public IReadOnlyDictionary<string, object> Snapshot()
    {
        // ObservableGauge 는 관측을 요청해야 값이 들어온다.
        _listener?.RecordObservableInstruments();

        var result = new SortedDictionary<string, object>(StringComparer.Ordinal);

        foreach (var (key, value) in _values)
        {
            result[key] = _counts.TryGetValue(key, out long count) && count > 0
                ? new { sum = Math.Round(value, 3), count, avg = Math.Round(value / count, 3) }
                : Math.Round(value, 4);
        }

        if (result.Count == 0)
        {
            result["note"] = "아직 관측된 계측이 없습니다. POST /load 로 트래픽을 발생시키세요. " +
                             $"(미터 이름: {DisruptorMetrics.MeterName})";
        }

        return result;
    }

    public void Dispose() => _listener?.Dispose();
}
