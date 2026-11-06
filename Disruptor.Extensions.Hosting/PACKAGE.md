# DisruptorCS.Extensions.Hosting

`Microsoft.Extensions` integration for [DisruptorCS](https://www.nuget.org/packages/DisruptorCS/):
DI registration, Generic Host lifecycle binding, and health checks.

```bash
dotnet add package DisruptorCS.Extensions.Hosting
```

Targets **net8.0** and **net10.0**. AOT-compatible. MIT licensed.

## Registration

```csharp
builder.Services.AddDisruptor<GameCommand>(d => d
    .WithEventFactory(() => new GameCommand())
    .WithRingSize(65536)
    .WithProducerType(ProducerType.Multi)
    .HandleEventsWith<JournalHandler>()      // handlers are resolved from DI
    .Then<GameLogicHandler>()
    .ThenWorkerPool<DbWriteWorker>(workers: 4));
```

Handlers are constructed by the container, so they can take loggers, options, and repositories the
same way any other service does. Pipeline handlers are long-lived; use dependencies with compatible
lifetimes. Each worker is constructed separately. `WithExceptionHandler` shares its resolved or
supplied instance across processors, so use a thread-safe, stateless policy there; keep the default
per-processor exception handlers when independent failure streaks are needed.

`AddDisruptor` and `AddDisruptorCheck` live in `Microsoft.Extensions.DependencyInjection`, so a
typical `Program.cs` needs no extra `using` — they show up on `builder.Services` directly.

## Lifecycle

`AddDisruptor<T>` registers an `IHostedService`, so the pipeline starts with the host and shuts down
with it by calling `Shutdown()`. Stop and join publishers before the pipeline's hosted service
stops. Draining uses `DisruptorOptions.ShutdownTimeout` (default five seconds); a timeout logs a
warning and proceeds with stop attempts. A graceful host stop does not guarantee all events were
processed or every handler has exited. Verify outstanding work before disposing shared resources.
See [shutdown semantics](https://github.com/jacking75/DisruptorCS/blob/main/docs/processing-semantics.md#shutdown-and-durable-recovery).

## Health checks

```csharp
builder.Services.AddHealthChecks().AddDisruptorCheck<GameCommand>();
```

The check reports **Degraded** at 70% ring utilization and **Unhealthy** at 90% — a consumer that
cannot keep up shows up before the producers actually block. A halted processor is reported as
Unhealthy regardless of utilization, with the halted processor names in the result data.

Separately, `DisruptorHostedService<T>` logs a halt at `Critical` and, by default
(`ProcessorHaltPolicy.StopApplication`), triggers a graceful application stop: a halted processor
keeps its sequence in the gating set, so the producers are about to block forever. A pod that
restarts beats an unresponsive zombie. Set `ProcessorHaltPolicy.LogOnly` if you handle recovery
yourself.

## Types

| Type | Purpose |
|---|---|
| `DisruptorServiceCollectionExtensions.AddDisruptor<T>` | Registers the ring buffer, the pipeline, and the hosted service |
| `DisruptorBuilder<T>` | Fluent pipeline configuration with DI-resolved handlers |
| `DisruptorHostedService<T>` | Binds start/drain/shutdown to the host lifecycle |
| `DisruptorHealthCheck<T>` | `IHealthCheck` over utilization and processor state |
| `ProcessorHaltPolicy` | What the host does when a processor halts (`StopApplication` by default) |

## Links

- [README](https://github.com/jacking75/DisruptorCS/blob/main/README.md) · [한국어 README](https://github.com/jacking75/DisruptorCS/blob/main/README.ko.md)
- [AspNetCoreTelemetry sample](https://github.com/jacking75/DisruptorCS/tree/main/samples/AspNetCoreTelemetry) — DI, health checks, and live metrics end to end
- [CHANGELOG](https://github.com/jacking75/DisruptorCS/blob/main/CHANGELOG.md)
- [Issues](https://github.com/jacking75/DisruptorCS/issues)
