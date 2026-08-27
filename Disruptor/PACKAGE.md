# DisruptorCS

**LMAX Disruptor for C#, with a game-server layer**: room sharding, fixed-tick integration, and
deterministic journal/replay — plus the operational tooling a real server needs (backpressure
policies, stall diagnostics, and compile-time misuse detection).

**250M ops/s · 4 ns/op · 0 bytes allocated** over 10,000,000 events (single producer, Release, i7-12700KF).
**Zero loss** under sustained backpressure (8 producers into a 1024-slot ring).

```bash
dotnet add package DisruptorCS
```

Targets **net8.0** and **net10.0**. AOT-compatible. MIT licensed.

## Quick start

```csharp
using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;

public sealed class OrderEvent
{
    public long OrderId;
    public int  Quantity;
}

public sealed class LogicHandler : IEventHandler<OrderEvent>
{
    public void OnEvent(OrderEvent e, long sequence, bool endOfBatch) { /* ... */ }

    // Flush only at the end of a batch: 1,000 events => 1 syscall instead of 1,000.
    public void OnBatchStart(long batchSize) { }

    // Called even when no events arrive — advance ticks, flush sends, send heartbeats.
    public void OnTimeout(long lastProcessedSequence) { }
}

using var disruptor = new Disruptor<OrderEvent>(
    eventFactory: () => new OrderEvent(),
    ringBufferSize: 1024,
    producerType: ProducerType.Multi,
    waitStrategy: new TimeoutBlockingWaitStrategy(TimeSpan.FromMilliseconds(1)),
    options: new DisruptorOptions
    {
        Name = "packet-pipeline",
        EnableMetrics = true,
        OnStall = info => logger.Warn(info.ToString()),   // names the bottleneck handler
    });

disruptor.HandleEventsWith(journal, antiCheat)   // run in parallel
         .Then(logic);                            // runs only after both finish

var ring = disruptor.Start();

using (var scope = ring.PublishEventScope())      // publishes on Dispose
{
    scope.Event.OrderId = 1;
    scope.Event.Quantity = 100;
}

disruptor.Shutdown();        // drains pending events, then stops
```

Already have a game loop and don't want another thread? Poll from it:

```csharp
using var poller = ring.NewPoller();

while (running)                                   // your existing loop — thread model unchanged
{
    poller.Poll(static (cmd, seq, eob) => { world.Apply(cmd); return true; }, maxEvents: 512);
    world.Simulate(dt);
    world.Render();
}
```

## What's in the box

| Area | Types |
|---|---|
| Core | `RingBuffer<T>`, `ValueRingBuffer<T>` (inline value-type slots, `ref` access), `Disruptor<T>`, `SequenceBarrier` |
| Consumption | `IEventHandler<T>`, `IBatchEventHandler<T>` (whole batch as a span), `EventPoller<T>` (no extra thread), `AsyncBatchEventProcessor<T>`, `WorkerPool<T>` / `ValueWorkerPool<T>` |
| Wait strategies | Busy-spin, yielding, spin-wait, sleeping, blocking, **timeout-blocking** (the game-server default — provides `OnTimeout`), phased backoff, async |
| Backpressure | `PublishGate<T>` with `Block` / `DropWhenCongested` / `FailAfterTimeout` |
| Observability | `DisruptorMetrics` (`System.Diagnostics.Metrics`, OpenTelemetry-compatible) and stall reports that name the bottleneck handler |
| Game layer | `ShardedDisruptor<T>` (10,000 rooms on 8 threads), `FixedTickDriver`, `JournalWriter` / `JournalReader` with checksummed records, `HighResolutionTimerScope` (idle tick 15.40 ms → 1.52 ms on Windows) |

## Analyzers included

Installing the package also installs Roslyn analyzers that catch Disruptor-specific misuse at
compile time — the failure modes that otherwise show up as a silently jammed ring in production:

| ID | What it catches |
|---|---|
| DISR001 | `async void` / `async Task` event handler |
| DISR002 | `Publish` that is not in a `finally` |
| DISR003 | Capturing lambda in `PublishEvent` (allocates per publish) |
| DISR004 | Ring buffer size that is not a power of two |
| DISR005 | A batch handler storing a span element past `OnBatch` |

See [`docs/analyzers/`](https://github.com/jacking75/DisruptorCS/tree/main/docs/analyzers).

## Companion package

```bash
dotnet add package DisruptorCS.Extensions.Hosting
```

DI registration, Generic Host lifecycle binding, and health checks.

## Links

- [README](https://github.com/jacking75/DisruptorCS/blob/main/README.md) · [한국어 README](https://github.com/jacking75/DisruptorCS/blob/main/README.ko.md)
- [Samples](https://github.com/jacking75/DisruptorCS/blob/main/samples/README.md) — game-loop polling, chat broadcast, match simulation with replay, journal CLI, ASP.NET Core telemetry
- [Benchmarks](https://github.com/jacking75/DisruptorCS/blob/main/docs/benchmarks/README.md) — including the results where we lose to `disruptor-net`
- [CHANGELOG](https://github.com/jacking75/DisruptorCS/blob/main/CHANGELOG.md)
- [Issues](https://github.com/jacking75/DisruptorCS/issues)
