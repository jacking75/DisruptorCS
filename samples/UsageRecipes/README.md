# Thirteen DisruptorCS usage recipes

> **English** · [한국어](README.ko.md) · [All samples](../README.md)

These small programs show **when to use an API, how to wire it, and how to check the result**.
Each recipe lives in its own source file. One console runner selects a recipe or runs all thirteen.
No database, network server, or configuration is needed; asynchronous storage uses a simulated
in-memory sink. These are learning examples, not performance benchmarks.

## Run

Use the .NET 10 SDK from the repository root. The project references the local library;
in your own application, install the `DisruptorCS` NuGet package instead.

```bash
dotnet run --project samples/UsageRecipes -c Release -- --list
dotnet run --project samples/UsageRecipes -c Release -- pipeline
dotnet run --project samples/UsageRecipes -c Release -- all
```

Replace `pipeline` with any name below. No arguments prints the list. Each successful recipe
prints `PASS`. Application exit codes: `0` success, `1` failed verification or timeout, `2` invalid
arguments. `dotnet run` may normalize failures to `1`; run the built DLL directly with `dotnet`
if you need to distinguish failure codes.
Batch sizes and worker distribution vary with scheduling; counts, sums, ordering, and ownership
are checked by the code rather than inferred from timing.

## Choose a recipe

| Name / source | Use it for | Main API | Verified result |
|---|---|---|---|
| [`publish`](Publication.cs) | First commands, scores, or log events | Scopes, static translators, `PublishEvents` | 6 scores, sum 210 |
| [`pipeline`](OrderPipeline.cs) | Independent checks followed by a decision | `And(...).Then(...)` | Each branch sees 3 orders; 2 accepted, 1 rejected, revenue 500 |
| [`multi-producer`](MultiProducer.cs) | Merging concurrent connections | `ProducerType.Multi`, cancellable `Next` | 4,000 commands, no per-producer order errors |
| [`batch`](BatchAggregation.cs) | Telemetry aggregation | `IBatchEventHandler`, `MaxBatchSize` | 1,000 values, sum 500,500, spans ≤ 16 |
| [`value`](ValueEvents.cs) | Small numeric events transformed in place | `ValueDisruptor`, `ref T` | 100 movements, position sum 5,150 |
| [`backpressure`](Backpressure.cs) | Choosing between drop, rejection, and waiting | `PublishGate`, cancellation, timeout | 5 published, 1 dropped, 1 rejected, cancellation observed |
| [`poller`](FramePolling.cs) | Preserving an existing frame loop | `NewPoller`, `TryNext`, `maxEvents` | 100 commands in 34 frames, at most 3 per frame |
| [`workers`](WorkerJobs.cs) | Preprocessing, distributing jobs, then collecting results | `ThenWorkerPool(...).Then(...)` | 1,000 complete results, no missing or duplicate jobs |
| [`async-batch`](AsyncSink.cs) | Awaiting an ordered asynchronous sink | `AsyncBatchEventProcessor`, `AsyncWaitStrategy` | 80 ordered records, slots protected across await |
| [`pooled-payload`](PooledPayload.cs) | Returning rented buffers after all readers | `IClearable`, `AutoClearSlots`, `ArrayPool` | Both readers see 100 packets; 100 returns |
| [`errors`](ErrorHandling.cs) | Recording invalid inputs and continuing | `ResilientExceptionHandler` | 3 accepted, sum 60, 2 rejection snapshots |
| [`snapshots`](SnapshotStream.cs) | Streaming to administrative tools | `ReadAllAsync`, immutable value snapshots | 100 snapshots, sum 49,500 after slot reuse |
| [`journal`](JournalReplay.cs) | Recording commands before applying state, then replaying | `JournalHandler`, `JournalReader.Verify/Replay` | 40 records, live/replayed player scores both [190, 200, 210, 220] |

Start with `publish → pipeline → multi-producer → backpressure`. Then try `poller` for a game
loop, `workers` for independent jobs, or `async-batch` for asynchronous storage.

## 1. Safe publication: publish

A single consumer totals scores published using a scope, a noncapturing static lambda, a struct
translator, and a batch. Six events fit in the 16-slot ring without saturating publication.
Use a narrow `using (...)` block: a scope publishes on disposal, so declaring it at the outer
method scope delays publication. A `PublishEvents` input batch must fit in the ring.

Parse and validate before claiming. A scope or translator still publishes if filling throws;
it does not roll back the event. Design explicitly for incomplete events if filling can fail.

## 2. Parallel branches and a join: pipeline

```text
               +-- validate quantity --+
publish order -+                       +-- accept/reject
               +-- calculate price ----+
```

Both branches receive every order. The final handler waits for both. Parallel branches write
different fields (`Valid` and `Total`); the topology does not make concurrent writes to the same
field or shared collection safe. A business rejection is represented as data (`Valid = false`),
separately from an exception that skips processing. Adapt this to independent checks or encoding
stages that must finish before a final decision.

The code registers the branches separately and joins their groups with `And`.
For branches registered together, `HandleEventsWith(validate, price).Then(accept)` is equivalent.

## 3. Concurrent producers: multi-producer

Four tasks model connections, each sending 1,000 numbered commands through a small ring.
One consumer verifies the next number for each connection. Each connection has one producer
that publishes its commands sequentially; interleaving between different connections can vary.
Multiple producers targeting the same player still need a domain ordering rule or command number.

`Task.WhenAll` finishes all publishers before `Shutdown` captures the drain target. Capacity
waits use a cancellation token so a stalled consumer does not leave the sample publishing forever.

## 4. Bounded aggregation: batch

`IBatchEventHandler<T>` receives contiguous ring segments as `ReadOnlySpan<T>`. The handler
accumulates values and commits its aggregate at `endOfBatch`. A ring wrap can split one batch
across multiple spans. `MaxBatchSize = 16` is a ceiling, not a request to wait for sixteen events;
single-event callbacks are possible. Do not retain spans or slot references after returning.
Batch exception policies apply to a whole failed segment, not an individually identified event.
Retry preserves completed earlier segments; Skip advances only the failed segment. See the
[recovery and clearing rules](../../docs/processing-semantics.md#synchronous-exception-policies).

## 5. Struct events: value

The first handler applies `Position += Velocity`; the next sums the transformed positions.
The producer uses `ref Movement movement = ref ring[sequence]` to initialize the actual slot.
Using `var movement = ring[sequence]` would modify a copy instead.

Try this for small numeric records. Large structs or structs containing references need their
own measurements; the recipe makes no throughput or allocation claim.

## 6. Saturation policies: backpressure

A deliberately unpolled four-slot ring makes saturation reproducible. At 75% utilization it
exceeds the 50% threshold and drops an event; after filling the last slot it demonstrates a
timed rejection and cancellation. Polling frees capacity and the next publication succeeds.

`DropWhenCongested` checks utilization and then uses `TryNext`. If another producer fills the
ring between those operations, it drops without waiting for consumer capacity. Use `TryNext`
for a raw capacity check, or `FailAfterTimeout` with `TimeSpan.Zero` to count a rejection instead.
Dropping also does not coalesce to the latest value: decide whether future data can replace the
discarded update in your application.

## 7. A frame budget: poller

One thread publishes and consumes 100 commands, with at most three commands per frame.
Blocking `Next()` on a full ring would wait for that very same thread to consume. The sample
uses `TryNext` and switches to polling when capacity runs out. Run simulation/rendering after
polling in a real game loop.

Create the poller before publishing: it starts at the current cursor. Dispose it when finished
so its gating sequence no longer prevents producers from reusing slots.

## 8. Independent jobs: workers

An upstream handler prepares each operand before `ThenWorkerPool` distributes it. Three workers
compute the squares of 1,000 integers. Each job goes to one worker, and a dependent
regular handler collects completed results in ring order. Unlike the parallel branches in
`pipeline`, workers do not each receive every event. Completion order and even distribution are
not guaranteed; one slow job can delay the dependent sequential consumer.

The duplicate check covers this successful run. It does not imply exactly-once external side
effects across failures and retries. Create a separate handler instance for each worker.

## 9. Awaiting a batch: async-batch

`AsyncBatchEventProcessor` advances its sequence only after the callback's `ValueTask` completes.
The callback can therefore read its slots after await, but must not retain them after returning.
This is a separate API from synchronous `IEventHandler`/`IWorkHandler`; making those callbacks
`async void` would release slots before the asynchronous operation finishes.

The low-level setup explicitly calls `AddGatingSequences(processor.Sequence)`. Shutdown follows:
finish publication → wait for the last processed sequence → `Halt` → await the consumer task →
remove gating. `Halt` cancels; it does not drain. When replacing the simulated sink with real I/O,
add failure policy, timeouts, and idempotency for retries after partial writes.

## 10. Pooled buffer ownership: pooled-payload

The producer prepares an `ArrayPool<byte>` buffer and transfers ownership to the slot. Two
readers inspect it; one terminal consumer joins them before automatic `IClearable.Clear()`
returns the buffer. If claiming times out, ownership stays with the producer, which returns it.

Automatic clearing requires a single terminal consumer. Multiple terminal consumers or a
terminal worker pool disable it. Copy anything needed after processing, and never keep using a
returned array for an asynchronous send. Consider `clearArray: true` on return for sensitive data.

## 11. Isolating invalid input: errors

Negative scores throw before any business state changes. The exception callback copies sequence,
score, and reason into a rejection record; it never saves the reusable event reference.
`ResilientExceptionHandler` skips errors and halts on a configured consecutive-failure threshold.
Successful inputs reset the streak, so this recipe never reaches its threshold of three.

Skip is neither rollback nor retry, and dependent handlers may continue. A production dead-letter
store needs durability and capacity limits. Do not share this stateful exception handler across
processors. If using a halt policy, observe `ProcessorHalted`, stop new input, and coordinate
shutdown from a separate control flow.

## 12. Administrative streams: snapshots

`ReadAllAsync` copies events to immutable value snapshots for `await foreach`. After enumeration
ends, the recipe overwrites every ring slot and verifies that the saved snapshots are unchanged.
Asynchronous enumeration is lazy: creating the enumerable alone does not register a subscriber.
Calling `ConsumeAsync()` directly reaches the first enumeration before starting the publisher.

Copy mutable nested objects/arrays deeply enough for your use case. The bridge has copying and
polling costs, and a slow subscriber can backpressure producers. Use it for tools and validation
streams with those costs understood.

## 13. Journal and replay: journal

`JournalHandler` records each command before the dependent state handler applies it. A fixed
eight-byte codec stores player ID and score delta. After draining, the sample verifies the journal,
rewinds the stream, and replays it into fresh state. It checks record counts, sequence order, and
all four player scores against both the live state and known expected values.

`MemoryStream` keeps this lesson small; it demonstrates ordering and replay, not crash durability.
For disk-backed state, decide when to use `flushToDiskOnBatchEnd`, and make simulation inputs
(including time and randomness) reproducible. A journal failure must not be silently skipped:
this recipe uses a halt policy and bounded publication waits. A service must also coordinate
shutdown through `ProcessorHalted`. See MatchSimulation for files and sharding, and ReplayInspector
for corruption handling and operational commands.

## Expand into a larger system

| Next step | Existing runnable sample |
|---|---|
| Real frame timing and tick integration | [GameLoopPoller](../GameLoopPoller/Program.cs) |
| Batched broadcast flushing | [ChatBroadcast](../ChatBroadcast/Program.cs) |
| Room sharding, fixed ticks, journal and replay comparison | [MatchSimulation](../MatchSimulation/Program.cs) |
| Journal verification, dumps, replay to a sequence, and diffs | [ReplayInspector](../ReplayInspector/Program.cs) |
| ASP.NET Core DI, lifecycle, health checks, and metrics | [AspNetCoreTelemetry](../AspNetCoreTelemetry/Program.cs) |

The common lifecycle is **stop and join producers → drain consumers → release resources**.
`Shutdown(timeout)` logs a warning and attempts to stop on a drain timeout; it does not throw to
guarantee that all work completed. That is why these recipes check results after shutdown.
Observe final sequences, outstanding work, and halted processors in a real service too.
