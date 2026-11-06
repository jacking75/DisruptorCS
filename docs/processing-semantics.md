# Processing, recovery, and shutdown

> **English** · [한국어](processing-semantics.ko.md) · [Samples](../samples/README.md)

This guide describes the current repository implementation. Changes under **Unreleased** in the
[changelog](../CHANGELOG.md) may not yet be in the NuGet version you installed.

## Publication and backpressure

Claiming a slot is not a transaction. After `Next()`, always call `Publish()` in `finally`, or use
a narrow `PublishEventScope()` block. Scopes, translators, and `PublishGate` publish even if filling
throws. Validate inputs before claiming and explicitly represent invalid/incomplete events when
filling can fail. Do not retain a slot reference for later work.

| `PublishGate` policy | Capacity behavior | Result |
|---|---|---|
| `Block` | Calls cancellable `Next`; configured producer backoff also applies | `true` after publication; cancellation/backoff can throw |
| `DropWhenCongested` | Checks utilization, then tries to claim; never waits for consumers to free capacity | `false` and `DroppedCount` increment on congestion or unavailable capacity |
| `FailAfterTimeout` | Synchronously retries until capacity is available or the timeout expires (default 50 ms) | `false` and `RejectedCount` increment on timeout |

`cancellationToken` on `TryPublish` applies to `Block` only. `FailAfterTimeout` occupies the calling
thread while trying; its timeout is not a hard real-time scheduling deadline. `DropWhenCongested`
does not coalesce updates or replace an older queued value. `true` means published, not processed
or durably stored. A filling exception propagates even though the slot is published.

## Synchronous exception policies

| Handler | Recovery unit |
|---|---|
| `IEventHandler<T>` / `IValueEventHandler<T>` | The failing event |
| `IBatchEventHandler<T>` / `IValueBatchEventHandler<T>` | The failing contiguous span passed to `OnBatch` |

A batch that wraps the ring can have two spans. If the second span fails, `Retry` repeats only
that span, and `Halt` preserves the completed first span. If the first span is skipped, the second
is still attempted. The processor cannot know which elements of a failing span already caused
side effects, so retries need idempotency for the whole span.

`Skip` advances processing; it does not roll back changes or automatically reject downstream
handlers. Encode business rejection as data if dependent stages must see it. `Halt` stops the
processor, leaves its sequence in gating, and can eventually block producers. Observe
`ProcessorHalted`, stop new input, and coordinate shutdown from a separate control flow.

The default `ResilientExceptionHandler` skips errors and halts at 100 consecutive failures.
Successful processing resets the streak; a skipped failure does not, including across batches.
A successful `OnBatch` resets it once for the segment, since individual element success is not
visible. Each processor needs its own stateful handler instance. `SetDefaultExceptionHandler`
and the hosting builder's `WithExceptionHandler` share the supplied instance across the pipeline;
do not pass one `ResilientExceptionHandler` to a multi-processor pipeline. Unconfigured processors
create separate defaults. For custom thresholds on ordinary synchronous processors, configure
each `BatchEventProcessor<T>` via `SetExceptionHandler` after registration and before `Start`.

Cancellation is checked between synchronous retry attempts and batch segments. It cannot interrupt
a handler that never returns. Retry has no automatic delay or attempt limit: supply a bounded
policy when repeated failure is possible.

## Slot ownership and clearing

`AutoClearSlots = true` is the DSL default. It enables cleanup for one terminal synchronous
processor when there is no terminal worker pool. Reference events must implement `IClearable`;
value events are reset to `default`. Multiple terminal readers need a final join stage before
cleanup, as shown in the [pooled-payload recipe](../samples/UsageRecipes/PooledPayload.cs).
Low-level processors, pollers, and async consumers need explicit ownership/cleanup arrangements.

For synchronous batch handlers, successful and skipped spans are cleared when enabled; a failing
span is retained on Retry or Halt. If reference-slot `Clear()` throws, the processor reports the
exception and halts regardless of the returned recovery action. This avoids replaying completed
business work or returning pooled buffers twice. Remaining payloads need coordinated cleanup
after all readers have stopped. Implement cleanup so it does not throw.

Copy values needed after a callback, including mutable nested data. Copying a reference-type event
reference is not a snapshot. Structs can also contain references; inline storage does not remove
GC scanning or nested-object lifetime concerns.

## Async consumption

Use `IAsyncBatchEventHandler<T>` with `AsyncBatchEventProcessor<T>` for awaited I/O. The processor
holds its sequence until the callback's `ValueTask` completes, so the callback may access slots
across `await`. Register its sequence with `AddGatingSequences` before publication. Synchronous
handlers, including `IWorkHandler`, must remain synchronous.

`AsyncWaitStrategy.WaitForAsync` completes an idle wait on cancellation without another publish.
The constructor's `timeout` controls synchronous monitor rechecks; it does **not** create async
timeouts or periodic `OnTimeoutAsync` callbacks. Add I/O deadlines in the handler as needed.
Cancellation is cooperative: awaited operations must observe their token.

Async recovery applies to the callback's sequence range, not synchronous wrap spans. `Skip` skips
that range; `Retry` resumes from the same starting sequence, but the available end can change on
the next attempt. Partial writes need idempotency. The synchronous clearing behavior above does
not automatically apply to an async processor.

## Shutdown and durable recovery

Stop and join producers first, then drain consumers, then release resources. `Shutdown(timeout)`
uses the configured timeout (default five seconds) to attempt draining. On drain timeout it logs
a warning and attempts to stop processors; it does not throw a drain-failure exception or guarantee
all handlers have exited. Thread joins can extend the total call beyond that timeout. Check final
sequences/outstanding work and processor state before disposing resources used by handlers.
The Generic Host integration calls this same shutdown path; a host stop is not a zero-loss guarantee.

`Halt` is cancellation, not draining. For a manually managed async consumer, finish producers,
wait for the final processed sequence, halt, await its task, and only then remove gating.

A journal-before-logic dependency gives write ordering, not automatic crash durability. A journal
failure must halt the stage rather than be skipped. `flushToDiskOnBatchEnd` defaults to `false`;
decide when file data must be flushed to disk before acknowledging work. Deterministic replay also
requires the same initial state, recorded time/tick/random inputs, and compatible logic. Shards
have independent sequences. Replication transport, snapshots, and external side-effect deduplication
are application responsibilities; see the [journal recipe](../samples/UsageRecipes/JournalReplay.cs).
