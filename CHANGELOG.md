# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

**Consumption models**
- `EventPoller<T>` / `ValueEventPoller<T>` — consume from an existing game loop with **no extra
  thread**. `RingBuffer.NewPoller()` / `ValueRingBuffer.NewPoller()`.
- `IBatchEventHandler<T>` / `IValueBatchEventHandler<T>` — receive a whole batch as a span; the
  handler call boundary drops from O(events) to O(segments ≤ 2). Value-type spans are writable,
  so SIMD over a batch is possible.
- `AsyncWaitStrategy`, `AsyncBatchEventProcessor<T>`, `IAsyncBatchEventHandler<T>` — `ValueTask`-based
  consumption for slow fan-out stages. Batch-level ordering is preserved.
- `AsyncBridge.ReadAllAsync` — expose a ring buffer as `IAsyncEnumerable<TSnapshot>` for dashboards
  and admin streams.

**Game layer**
- `HighResolutionTimerScope` — raises Windows timer resolution to 1 ms. Measured idle `OnTimeout`
  interval: **15.40 ms → 1.52 ms**. Reference-counted and safe to nest.
- `ShardedDisruptor.HealthSnapshot()` and `IsShardHalted()` — per-shard utilization, drop counts,
  and halt state.
- Journal record format **v2**: XxHash3 checksum over header + payload, a length ceiling, and
  sequence-monotonicity validation. `JournalReader.Verify()` checks a journal without replaying it;
  `JournalReadMode.TruncateAtCorruption` recovers up to the last valid record. v1 files still read.

**Operations**
- Roslyn analyzers shipped in the package: **DISR001** (async handler), **DISR002** (`Publish`
  outside `finally`), **DISR003** (capturing lambda in `PublishEvent`), **DISR004** (ring size not a
  power of two), **DISR005** (a batch handler storing a span element — the slot is reused the moment
  `OnBatch` returns, so the stored reference silently starts pointing at a different event).
  See [`docs/analyzers/`](docs/analyzers/).
- `DisruptorCS.Extensions.Hosting` package — `services.AddDisruptor<T>(...)` with DI-resolved
  handlers, Generic Host lifecycle binding, and `AddDisruptorCheck<T>()` health checks.
- `Disruptor.Soak` — long-running invariant harness with four scenarios, resource-leak detection,
  and a fixed seed for reproducibility.
- `Disruptor.Concurrency.Tests` — Microsoft Coyote systematic concurrency exploration over reduced
  models of the blocking-signal path, the worker claim loop, and the processor lifecycle. Includes a
  **negative control** that must fail, so a green run means something. See
  [`docs/concurrency-testing.md`](docs/concurrency-testing.md).
- `IValueWorkHandler<T>` / `ValueWorkProcessor<T>` / `ValueWorkerPool<T>` — order-independent work
  distribution for value-type events, with `ref` access to the slot (no copy).
  `ValueDisruptor.HandleEventsWithWorkerPool()` and `ThenWorkerPool()` wire it into the DSL.
- `SequencerOptions.UsePinnedObjectHeap` / `DisruptorOptions.UsePinnedObjectHeap` — allocate the
  slot array on the pinned object heap via `GC.AllocateArray<T>(n, pinned: true)`, avoiding LOH
  fragmentation for large rings.
- Four more samples under [`samples/`](samples/README.md): `ChatBroadcast` (measures the syscall
  reduction from batch flushing), `MatchSimulation` (sharded rooms + journal + replay, verified by
  comparing state checksums), `ReplayInspector` (a journal CLI: verify / dump / replay `--until-seq`
  / diff), and `AspNetCoreTelemetry` (DI, health checks, and live metrics).
- `SequencerOptions.StallReportInterval` — rate-limits stall reports so a sustained jam does not
  produce a log flood that makes the jam worse.
- `DisruptorOptions.MaxBatchSize` — caps batch size, which turns a batch bound into a **latency
  bound** for handlers that also drive ticks.
- `DisruptorOptions.MetricsBatchSampling` — record metrics every N batches on very hot pipelines.
- `EventHandlerGroup.And()` — join two independently registered groups so a later stage waits on both.
- `IEventTranslator<T, TArg>` — allocation-free publishing via a struct translator.
- `StripedCounter` — contention-free counter used by `PublishGate`.

### Changed

- **Consumer loop is now batch-shaped.** `try`/`catch`, failure-streak reset, and `IClearable`
  checks happen at batch boundaries instead of per event. Measured **339M → 649M ops/s (+87%)**
  in a consumer-bound configuration. Skip/Retry/Halt semantics are unchanged; a mid-batch exception
  now resumes with the remainder of the batch.
- **Publish path is devirtualized.** `RingBuffer.Next()`/`Publish()` dispatch through the
  `SequencerDispatcher` struct, which branches on the concrete sealed sequencer type so the JIT
  emits a direct, inlinable call. Added only after a benchmark against disruptor-net showed the gap.
- **Blocking strategies no longer take a lock on every publish.** `SignalAllWhenBlocking` uses a
  full fence plus a waiter check (4.1 ns) instead of unconditional `lock` + `PulseAll` (39.1 ns).
  The `Monitor.Wait` timeout is retained as a second line of defence against lost wakeups.
- **Backpressure loops sample the clock 1-in-128**, and only when the configured backoff or a stall
  callback actually needs elapsed time. `SpinOnlyBackoff` never reads the clock.
- `PublishGate` counters are striped, removing cache-line ping-pong between producers.
- The assembly is now named `DisruptorCS` (the namespace remains `Disruptor`), so it can sit
  alongside the official `disruptor-net` package in the same project.
- Versioning moved to MinVer (git-tag driven).
- `PublishEventScope().Event` is now a property; `GetEvent()` is kept but `[Obsolete]`.
- `AddDisruptor<T>()` resolves stage handlers with `ActivatorUtilities.GetServiceOrCreateInstance`
  instead of `GetRequiredService`, so a handler no longer has to be registered separately — which is
  what the documented example already implied. Worker-pool handlers are now built with
  `ActivatorUtilities.CreateInstance`, giving **each worker its own instance**; previously all N
  workers shared one, so any worker-local state was silently shared across threads.
- `RingBuffer.PublishedCursor` now documents that it is an O(n) scan and must not be called on a
  hot path.

### Fixed

- **`ResilientExceptionHandler<T>.Default` was a shared singleton**, so one processor's success reset
  another's consecutive-failure streak — infinite error loops could go undetected forever, and
  unrelated pipelines' failures could combine to halt the wrong processor. Replaced with
  `CreateDefault()`, which returns a per-processor instance.
- `Halt()` after `Dispose()` threw `ObjectDisposedException`; it is now a no-op. `Dispose()` is
  idempotent, and `Run()` after `Dispose()` throws a clear `ObjectDisposedException`.
- Publishing to a halted shard could block forever under `BackpressurePolicy.Block`.
  `ShardedDisruptor.TryPublish` now fails fast and `Publish` throws `ShardHaltedException`.
- The unpublished-sequence wait in `BatchEventProcessor` escalated with a fixed `Thread.SpinWait(1)`;
  it now uses a `SpinWait` that resets on progress (and still never calls `Thread.Sleep(1)`).
- `JournalWriter.Dispose()` was not idempotent. `JournalHandler.OnShutdown()` disposes the writer,
  so an owner disposing it again flushed a closed stream — turning a clean shutdown into an
  exception. It is now guarded.

### Removed

- `ResilientExceptionHandler<T>.Default` (use `CreateDefault()`).

---

## [0.2.0]

### Fixed

- **`MultiProducerSequencer.Next()` disabled backpressure entirely.** In C#, `continue` inside a
  `do…while` jumps to the *condition*, not the top of the body — so the CAS ran even after the
  "no capacity" check failed. Producers overwrote unconsumed slots, the availability-buffer round
  flag was overwritten, and consumers stalled permanently.
  Measured: 8 producers × 100,000 events into a 1024-slot ring lost **794,332 of 800,000 events
  (99.3%)**. After the fix: **0 lost**.
- `SpinWait.SpinOnce()` reaches `Thread.Sleep(1)`, which Windows' 15.6 ms timer resolution turns
  into a full lost tick for a 60 Hz server. All wait paths now pass `sleep1Threshold: -1`.
  Measured: `>15 ms` stalls **284 → 0**, throughput **+50%**.
- `BlockingWaitStrategy` hot-spun in dependency chains: a downstream handler burned a full core
  waiting on a slow upstream one. Introduced `DependentSequenceGroup` and signal propagation on
  sequence updates. Measured: **0.98 cores → 0.07 cores**.
- The default exception handler rethrew, producing an unhandled exception on a background thread —
  **one malformed packet killed the whole process**. `IExceptionHandler` now returns
  `EventExceptionAction` (Skip / Retry / Halt).
- Lost-wakeup race in `BlockingWaitStrategy` (`_numWaiters` read could be reordered past the cursor
  write).
- `Start()` / `Shutdown()` race: shutdown could lose the stop signal and hang until the 5 s timeout.
  `Start()` now waits for every processor to enter its event loop; stop signalling is unified on
  `CancellationToken`.
- Shutdown draining used the multi-producer *claim* cursor rather than the highest published
  sequence.
- Worker-pool-only pipelines skipped draining entirely on shutdown, losing in-flight events.
- Deadlines used `DateTime.UtcNow` (vulnerable to NTP adjustment); now `Stopwatch`.

### Added

- `TimeoutBlockingWaitStrategy`, `SleepingWaitStrategy`, `PhasedBackoffWaitStrategy`.
- `IEventHandler.OnBatchStart` and `OnTimeout` hooks.
- `ValueRingBuffer<T>` / `ValueDisruptor<T>` for value-type events.
- `WorkerPool<T>` for order-independent work distribution.
- `PublishGate<T>` backpressure policies.
- `DisruptorMetrics` and stall detection that names the bottleneck handler.
- `IClearable` with automatic slot clearing on the leaf processor.
- `ShardedDisruptor<T>`, `FixedTickDriver`, and the journal/replay framework.
- `SequenceWaitResult` replacing `AlertException`.
- `ProcessorThreadOptions` (priority, stack size, core affinity).
- Multi-targeting `net8.0` / `net10.0`, AOT compatibility, NuGet packaging.

### Removed

- `AlertException` (replaced by `SequenceWaitResult`).
- `FatalExceptionHandler<T>` (replaced by `ResilientExceptionHandler<T>` /
  `HaltOnExceptionHandler<T>`).

---

## [0.1.0]

Initial implementation: ring buffer, single/multi-producer sequencers, sequence barrier,
four wait strategies, and the DSL.
