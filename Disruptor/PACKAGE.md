# DisruptorCS

**LMAX Disruptor for C#, with a game-server layer**: room sharding, fixed-tick integration, and
deterministic journal/replay — plus the operational tooling a real server needs (backpressure
policies, stall diagnostics, and compile-time misuse detection).

Historical results include 250M ops/s in a single-producer 10M-event loop and zero loss in an
8-producer/1,024-slot stress run. See the
[benchmark conditions](https://github.com/jacking75/DisruptorCS/blob/main/docs/benchmarks/README.md);
these are specific observations, not performance or delivery guarantees.

```bash
dotnet add package DisruptorCS
```

Targets **net8.0** and **net10.0**. AOT-compatible. MIT licensed.

## Quick start

```csharp
using Disruptor;
using Disruptor.Dsl;

using var disruptor = new Disruptor<OrderEvent>(
    eventFactory: () => new OrderEvent(),
    ringBufferSize: 1024,
    producerType: ProducerType.Single);

var handler = new LogicHandler();
disruptor.HandleEventsWith(handler);
var ring = disruptor.Start();

using (var scope = ring.PublishEventScope())
{
    scope.Event.OrderId = 1;
    scope.Event.Quantity = 100;
} // Publishes on Dispose, including when filling throws.

disruptor.Shutdown(); // Stop producers first; this example has finished publishing.
if (handler.Count != 1 || handler.TotalQuantity != 100)
    throw new InvalidOperationException("The order was not fully processed.");
Console.WriteLine($"Orders={handler.Count}, quantity={handler.TotalQuantity}");

public sealed class OrderEvent
{
    public long OrderId;
    public int Quantity;
}

public sealed class LogicHandler : IEventHandler<OrderEvent>
{
    public int Count;
    public int TotalQuantity;

    public void OnEvent(OrderEvent e, long sequence, bool endOfBatch)
    {
        Count++;
        TotalQuantity += e.Quantity;
        // Use endOfBatch for flushing. OnBatchStart runs BEFORE the batch.
    }
}
```

Paste the complete example into `Program.cs` of a console project after installing the package.
Expected output: `Orders=1, quantity=100`. Input validation should happen before claiming a slot:
a scope still publishes if filling throws; it does not roll back a partial event.

Already have a game loop and don't want another thread? Poll from it:

```csharp
var polledRing = RingBuffer<OrderEvent>.CreateSingleProducer(
    () => new OrderEvent(), 1024, new Disruptor.WaitStrategy.BlockingWaitStrategy());
using var poller = polledRing.NewPoller(); // Register BEFORE publishing; starts at the current cursor.

// In the existing frame loop, consume at most 512 events, then run simulation/rendering.
poller.Poll(static (order, seq, endOfBatch) =>
{
    Console.WriteLine(order.OrderId);
    return true;
}, maxEvents: 512);
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

## Processing guarantees and limits

`DropWhenCongested` does not wait for consumer capacity; `FailAfterTimeout` retries synchronously.
Batch recovery acts on the failing contiguous span. Automatic slot clearing requires one terminal
synchronous processor and no terminal worker pool. `Shutdown` attempts a bounded drain and logs
timeouts; it is not a guarantee that all work completed.

Read [processing, recovery, and shutdown](https://github.com/jacking75/DisruptorCS/blob/main/docs/processing-semantics.md)
for ownership, exception-handler sharing, async cancellation, and journal durability. That guide
tracks the repository; consult the changelog for changes not yet released in your installed package.

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
- [Samples](https://github.com/jacking75/DisruptorCS/blob/main/samples/README.md) — 13 focused usage recipes plus five application samples
- [Benchmarks](https://github.com/jacking75/DisruptorCS/blob/main/docs/benchmarks/README.md) — including the results where we lose to `disruptor-net`
- [CHANGELOG](https://github.com/jacking75/DisruptorCS/blob/main/CHANGELOG.md)
- [Issues](https://github.com/jacking75/DisruptorCS/issues)
