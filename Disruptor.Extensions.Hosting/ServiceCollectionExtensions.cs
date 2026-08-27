using Disruptor;
using Disruptor.Dsl;
using Disruptor.Extensions.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

// IServiceCollection / IHealthChecksBuilder 확장은 Microsoft 관례대로
// Microsoft.Extensions.DependencyInjection 에 둔다. builder.Services 를 쓰는 파일은 이미 이
// 네임스페이스를 열어 두므로, 사용자가 AddDisruptor 를 찾으려고 별도 using 을 추가할 필요가 없다.
// (구현 타입은 그대로 Disruptor.Extensions.Hosting 에 있다.)
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// <c>IServiceCollection</c> 에 Disruptor 파이프라인을 등록합니다.
/// </summary>
/// <remarks>
/// .NET 서버 개발자의 절대다수는 Generic Host 위에서 작업합니다. 그들에게
/// "Thread 를 직접 만들고 Shutdown 을 직접 불러라"는 이질적입니다 —
/// <c>services.AddDisruptor(...)</c> 한 줄이 있고 없고가 평가 단계 통과를 가릅니다.
/// </remarks>
public static class DisruptorServiceCollectionExtensions
{
    /// <summary>
    /// Disruptor 파이프라인을 등록하고 호스트 수명주기에 결속합니다.
    /// </summary>
    /// <remarks>
    /// 등록되는 것:
    /// <list type="bullet">
    ///   <item><see cref="Disruptor{T}"/> — 싱글턴</item>
    ///   <item><see cref="RingBuffer{T}"/> — 싱글턴 (게시 측에서 바로 주입받아 쓸 수 있게)</item>
    ///   <item><c>IHostedService</c> — <c>StartAsync</c> 에서 Start, <c>StopAsync</c> 에서 드레인 후 Shutdown</item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddDisruptor&lt;GameCommand&gt;(d => d
    ///     .WithEventFactory(() => new GameCommand())
    ///     .WithRingSize(65536)
    ///     .WithProducerType(ProducerType.Multi)
    ///     .HandleEventsWith&lt;JournalHandler&gt;()
    ///     .Then&lt;GameLogicHandler&gt;()
    ///     .ThenWorkerPool&lt;DbWriteWorker&gt;(workers: 4));
    /// </code>
    /// </example>
    public static IServiceCollection AddDisruptor<T>(
        this IServiceCollection services,
        Action<DisruptorBuilder<T>> configure,
        string? pipelineName = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        string name = pipelineName ?? typeof(T).Name;

        services.AddSingleton(sp =>
        {
            var builder = new DisruptorBuilder<T>(sp, name);
            configure(builder);
            return builder.Build();
        });

        // 게시 측이 RingBuffer 를 바로 주입받을 수 있게 한다.
        services.AddSingleton(sp => sp.GetRequiredService<Disruptor<T>>().RingBuffer);

        // 백프레셔 정책 게이트도 함께 제공한다 — 게임 서버에서는 이게 사실상 기본 게시 경로다.
        services.AddSingleton(sp =>
        {
            var disruptor = sp.GetRequiredService<Disruptor<T>>();
            return new PublishGate<T>(disruptor.RingBuffer, disruptor.Metrics);
        });

        services.AddSingleton<DisruptorHostedService<T>>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DisruptorHostedService<T>>());

        return services;
    }

    /// <summary>
    /// Disruptor 파이프라인의 헬스체크를 등록합니다.
    /// </summary>
    /// <param name="builder">헬스체크 빌더</param>
    /// <param name="name">헬스체크 이름. 생략하면 <c>disruptor-{타입명}</c>.</param>
    /// <param name="degradedUtilization">이 사용률 이상이면 Degraded (기본 0.7)</param>
    /// <param name="unhealthyUtilization">이 사용률 이상이면 Unhealthy (기본 0.9)</param>
    /// <param name="failureStatus">실패 시 보고할 상태</param>
    /// <param name="tags">헬스체크 태그</param>
    public static IHealthChecksBuilder AddDisruptorCheck<T>(
        this IHealthChecksBuilder builder,
        string? name = null,
        double degradedUtilization = 0.7,
        double unhealthyUtilization = 0.9,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Add(new HealthCheckRegistration(
            name ?? $"disruptor-{typeof(T).Name}",
            sp => new DisruptorHealthCheck<T>(
                sp.GetRequiredService<Disruptor<T>>(),
                degradedUtilization,
                unhealthyUtilization),
            failureStatus,
            tags));
    }
}
