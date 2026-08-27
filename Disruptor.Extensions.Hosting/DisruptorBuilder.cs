using System.Diagnostics.CodeAnalysis;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;
using Disruptor.Work;
using Microsoft.Extensions.DependencyInjection;

namespace Disruptor.Extensions.Hosting;

/// <summary>
/// DI 컨테이너에서 핸들러를 해석해 <see cref="Dsl.Disruptor{T}"/>를 구성하는 빌더.
/// </summary>
/// <remarks>
/// <para>
/// 핸들러를 DI 로 해석하므로 핸들러가 로거·DB 컨텍스트·설정을 생성자 주입으로 받을 수 있습니다.
/// 이것이 없으면 핸들러를 직접 <c>new</c> 해야 해서 ASP.NET Core 사용자에게 이질적입니다.
/// </para>
/// <para><b>해석 규칙</b></para>
/// <list type="bullet">
///   <item>
///     단계 핸들러: 컨테이너에 등록되어 있으면 그것을 쓰고, 없으면 생성자 인자만 DI 에서 채워
///     새로 만듭니다(<see cref="ActivatorUtilities.GetServiceOrCreateInstance{T}(IServiceProvider)"/>).
///     핸들러를 따로 <c>AddSingleton</c> 하지 않아도 되도록 하기 위한 것입니다.
///   </item>
///   <item>
///     워커 풀: <b>항상 워커마다 새 인스턴스</b>를 만듭니다
///     (<see cref="ActivatorUtilities.CreateInstance{T}(IServiceProvider, object[])"/>).
///     워커가 인스턴스를 공유하면 DB 연결·버퍼 같은 워커 로컬 상태가 스레드 간에 뒤섞입니다.
///   </item>
/// </list>
/// </remarks>
public sealed class DisruptorBuilder<T> where T : class
{
    private readonly IServiceProvider _services;
    private readonly List<Stage> _stages = new();

    private Func<T>? _eventFactory;
    private int _ringSize = 1024;
    private ProducerType _producerType = ProducerType.Multi;
    private Func<IWaitStrategy>? _waitStrategyFactory;
    private DisruptorOptions _options;
    private Func<IServiceProvider, IExceptionHandler<T>>? _exceptionHandlerFactory;

    internal DisruptorBuilder(IServiceProvider services, string pipelineName)
    {
        _services = services;
        _options = DisruptorOptions.Default with { Name = pipelineName };
    }

    private abstract record Stage;

    private sealed record HandlerStage(Func<IServiceProvider, IEventHandler<T>>[] Factories) : Stage;

    private sealed record BatchHandlerStage(Func<IServiceProvider, IBatchEventHandler<T>>[] Factories) : Stage;

    private sealed record WorkerPoolStage(int Workers, Func<IServiceProvider, int, IWorkHandler<T>> Factory) : Stage;

    /// <summary>이벤트 인스턴스를 만드는 팩토리. 링 버퍼가 시작 시 이것으로 모든 슬롯을 채웁니다.</summary>
    public DisruptorBuilder<T> WithEventFactory(Func<T> eventFactory)
    {
        _eventFactory = eventFactory ?? throw new ArgumentNullException(nameof(eventFactory));
        return this;
    }

    /// <summary>링 버퍼 크기. 2의 거듭제곱이어야 합니다.</summary>
    public DisruptorBuilder<T> WithRingSize(int size)
    {
        _ringSize = size;
        return this;
    }

    public DisruptorBuilder<T> WithProducerType(ProducerType type)
    {
        _producerType = type;
        return this;
    }

    public DisruptorBuilder<T> WithWaitStrategy(Func<IWaitStrategy> factory)
    {
        _waitStrategyFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>DSL 옵션을 변형합니다.</summary>
    public DisruptorBuilder<T> WithOptions(Func<DisruptorOptions, DisruptorOptions> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        _options = mutate(_options);
        return this;
    }

    /// <summary>예외 핸들러를 DI 에서 해석합니다.</summary>
    public DisruptorBuilder<T> WithExceptionHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where THandler : class, IExceptionHandler<T>
    {
        _exceptionHandlerFactory = static sp => ActivatorUtilities.GetServiceOrCreateInstance<THandler>(sp);
        return this;
    }

    /// <summary>예외 핸들러를 직접 지정합니다.</summary>
    public DisruptorBuilder<T> WithExceptionHandler(IExceptionHandler<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _exceptionHandlerFactory = _ => handler;
        return this;
    }

    /// <summary>첫 단계 핸들러를 DI 에서 해석해 등록합니다.</summary>
    public DisruptorBuilder<T> HandleEventsWith<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>() where THandler : class, IEventHandler<T>
        => AddHandlerStage(static sp => ActivatorUtilities.GetServiceOrCreateInstance<THandler>(sp));

    /// <summary>첫 단계에 병렬 핸들러 두 개를 등록합니다.</summary>
    public DisruptorBuilder<T> HandleEventsWith<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T1, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T2>()
        where T1 : class, IEventHandler<T>
        where T2 : class, IEventHandler<T>
        => AddHandlerStage(
            static sp => ActivatorUtilities.GetServiceOrCreateInstance<T1>(sp),
            static sp => ActivatorUtilities.GetServiceOrCreateInstance<T2>(sp));

    /// <summary>첫 단계에 병렬 핸들러 세 개를 등록합니다.</summary>
    public DisruptorBuilder<T> HandleEventsWith<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T1, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T2, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T3>()
        where T1 : class, IEventHandler<T>
        where T2 : class, IEventHandler<T>
        where T3 : class, IEventHandler<T>
        => AddHandlerStage(
            static sp => ActivatorUtilities.GetServiceOrCreateInstance<T1>(sp),
            static sp => ActivatorUtilities.GetServiceOrCreateInstance<T2>(sp),
            static sp => ActivatorUtilities.GetServiceOrCreateInstance<T3>(sp));

    /// <summary>배치 핸들러를 등록합니다.</summary>
    public DisruptorBuilder<T> HandleEventsWithBatch<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>() where THandler : class, IBatchEventHandler<T>
    {
        _stages.Add(new BatchHandlerStage([static sp => ActivatorUtilities.GetServiceOrCreateInstance<THandler>(sp)]));
        return this;
    }

    /// <summary>이전 단계가 끝난 뒤 실행될 핸들러를 등록합니다.</summary>
    public DisruptorBuilder<T> Then<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>() where THandler : class, IEventHandler<T>
        => AddHandlerStage(static sp => ActivatorUtilities.GetServiceOrCreateInstance<THandler>(sp));

    /// <summary>이전 단계가 끝난 뒤 실행될 병렬 핸들러 두 개를 등록합니다.</summary>
    public DisruptorBuilder<T> Then<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T1, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T2>()
        where T1 : class, IEventHandler<T>
        where T2 : class, IEventHandler<T>
        => AddHandlerStage(
            static sp => ActivatorUtilities.GetServiceOrCreateInstance<T1>(sp),
            static sp => ActivatorUtilities.GetServiceOrCreateInstance<T2>(sp));

    /// <summary>이전 단계가 끝난 뒤 이벤트를 분배할 워커 풀을 등록합니다.</summary>
    public DisruptorBuilder<T> ThenWorkerPool<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TWorker>(int workers)
        where TWorker : class, IWorkHandler<T>
    {
        if (workers < 1)
            throw new ArgumentOutOfRangeException(nameof(workers));

        // 워커마다 별도 인스턴스 — 공유하면 워커 로컬 상태가 스레드 간에 섞인다.
        _stages.Add(new WorkerPoolStage(workers, static (sp, _) => ActivatorUtilities.CreateInstance<TWorker>(sp)));
        return this;
    }

    private DisruptorBuilder<T> AddHandlerStage(params Func<IServiceProvider, IEventHandler<T>>[] factories)
    {
        _stages.Add(new HandlerStage(factories));
        return this;
    }

    internal Dsl.Disruptor<T> Build()
    {
        if (_eventFactory is null)
            throw new InvalidOperationException(
                $"WithEventFactory() 를 호출해 {typeof(T).Name} 생성 방법을 지정해야 합니다.");

        if (_stages.Count == 0)
            throw new InvalidOperationException("HandleEventsWith<THandler>() 로 최소 1개의 핸들러를 등록하세요.");

        var waitStrategy = _waitStrategyFactory?.Invoke();

        var disruptor = new Dsl.Disruptor<T>(
            _eventFactory, _ringSize, _producerType, waitStrategy, _options);

        if (_exceptionHandlerFactory is not null)
            disruptor.SetDefaultExceptionHandler(_exceptionHandlerFactory(_services));

        Dsl.Disruptor<T>.EventHandlerGroup? group = null;

        foreach (Stage stage in _stages)
        {
            group = stage switch
            {
                HandlerStage h => group is null
                    ? disruptor.HandleEventsWith(Resolve(h.Factories))
                    : group.Then(Resolve(h.Factories)),

                BatchHandlerStage b => group is null
                    ? disruptor.HandleEventsWith(Resolve(b.Factories))
                    : group.Then(Resolve(b.Factories)),

                WorkerPoolStage w => group is null
                    ? disruptor.HandleEventsWithWorkerPool(w.Workers, i => w.Factory(_services, i))
                    : group.ThenWorkerPool(w.Workers, i => w.Factory(_services, i)),

                _ => throw new InvalidOperationException($"알 수 없는 단계: {stage}"),
            };
        }

        return disruptor;
    }

    private TResult[] Resolve<TResult>(Func<IServiceProvider, TResult>[] factories)
        => Array.ConvertAll(factories, f => f(_services));
}
