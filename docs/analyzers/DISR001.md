# DISR001: Disruptor handlers must not be `async`

**Severity:** Error · **Category:** Disruptor.Correctness

## Cause

A method that implements one of these interfaces is declared `async`:

- `IEventHandler<T>`
- `IValueEventHandler<T>`
- `IBatchEventHandler<T>`
- `IValueBatchEventHandler<T>`
- `IWorkHandler<T>`

## Why this is an error

Disruptor consumers are **synchronous, blocking threads**. The framework calls your handler and,
when the call returns, treats the event as processed and advances the consumer's sequence.

An `async void` handler returns at the **first `await`**, not when the work finishes. So:

1. The framework advances the sequence while your continuation is still running.
2. The producer sees that slot as free and **overwrites it** — your continuation is now reading
   a different event's data.
3. Multiple continuations run concurrently, so **ordering is gone**.

None of this throws. It silently produces wrong results, which is the worst failure mode a
deterministic pipeline can have.

## Example

```csharp
// Violation
public sealed class BadHandler : IEventHandler<OrderEvent>
{
    public async void OnEvent(OrderEvent e, long sequence, bool endOfBatch)
    {
        await _database.SaveAsync(e);   // returns here; the slot is recycled behind you
    }
}
```

## How to fix

**Option 1 — move the slow work to a `WorkerPool`** (preferred when ordering doesn't matter
for that stage):

```csharp
public sealed class DbWriteWorker : IWorkHandler<OrderEvent>
{
    public void OnEvent(OrderEvent e) => _database.Save(e);   // synchronous
}

disruptor.HandleEventsWith(logic)
         .ThenWorkerPool(workers: 4, i => new DbWriteWorker(_database));
```

**Option 2 — copy what you need and hand it off:**

```csharp
public sealed class GoodHandler : IEventHandler<OrderEvent>
{
    private readonly Channel<OrderSnapshot> _slow;

    public void OnEvent(OrderEvent e, long sequence, bool endOfBatch)
    {
        // Snapshot out of the slot, then let another stage do the async work.
        _slow.Writer.TryWrite(OrderSnapshot.From(e));
    }
}
```

**Option 3 — use the async processor**, where a *batch* is the ordering unit:

```csharp
public sealed class AsyncFanout : IAsyncBatchEventHandler<OrderEvent>
{
    public async ValueTask OnBatchAsync(RingBuffer<OrderEvent> ring, long from, long to,
                                        CancellationToken ct)
    {
        // The processor does not advance its sequence until this completes,
        // so batch-level ordering is preserved.
        for (long s = from; s <= to; s++)
            await _sink.WriteAsync(ring[s], ct);
    }
}
```

## Notes

`IValueEventHandler<T>.OnEvent` and the batch handlers take `ref` / `Span<T>` parameters, so the
C# compiler already rejects `async` on them (CS1988). This analyzer covers the remaining surface:
`IEventHandler`, `IWorkHandler`, and the lifecycle hooks (`OnStart`, `OnShutdown`, `OnTimeout`,
`OnBatchStart`) on every handler type.

An `async` `OnTimeout` is especially easy to miss and especially damaging: a tick driver that
awaits will report the tick as advanced before it actually is.
