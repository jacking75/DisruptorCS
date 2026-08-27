# DisruptorCS

> **English** · [한국어](README.ko.md)

**LMAX Disruptor for C#, with a game-server layer**: room sharding, fixed-tick integration,
and deterministic journal/replay — plus the operational tooling a real server needs
(backpressure policies, stall diagnostics, and compile-time misuse detection).

[![NuGet](https://img.shields.io/nuget/v/DisruptorCS.svg)](https://www.nuget.org/packages/DisruptorCS/)
[![Downloads](https://img.shields.io/nuget/dt/DisruptorCS.svg)](https://www.nuget.org/packages/DisruptorCS/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)

**250M ops/s · 4 ns/op · 0 bytes allocated** over 10,000,000 events (single producer, Release, i7-12700KF).
**Zero loss** under sustained backpressure (8 producers into a 1024-slot ring).

```bash
dotnet add package DisruptorCS
```

---

## Table of contents

- [Quick start](#quick-start)
- [Why this and not disruptor-net?](#why-this-and-not-disruptor-net)
- [What's in the box](#whats-in-the-box)
- [Game-server features](#game-server-features)
- [Architecture](#architecture)
- [Core design principles](#core-design-principles)
- [When to use Disruptor vs `Channel<T>`](#when-to-use-disruptor-vs-channelt)
- [Benchmarks](#benchmarks)
- [Pitfalls the analyzer catches for you](#pitfalls-the-analyzer-catches-for-you)
- [Project layout](#project-layout)
- [Gotchas](#gotchas)

---

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

long seq = ring.Next();
try
{
    var e = ring[seq];       // pre-allocated slot — no object is created
    e.OrderId = 1;
    e.Quantity = 100;
}
finally
{
    ring.Publish(seq);       // the finally is mandatory — skipping it jams the ring forever
}

disruptor.Shutdown();        // drains pending events, then stops
```

Worried about forgetting the `try/finally`? Use the scope pattern — the bundled analyzer
(DISR002) also flags the unguarded form:

```csharp
using (var scope = ring.PublishEventScope())
{
    scope.Event.OrderId = 1;
}   // publishes on Dispose
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

Full runnable examples: [`Example/Program.cs`](Example/Program.cs) (13 scenarios) and
[`samples/`](samples/README.md) — five programs, each answering one question:

| Sample | The question it answers |
|---|---|
| [GameLoopPoller](samples/GameLoopPoller/) | *Can I use this without changing my thread model?* |
| [ChatBroadcast](samples/ChatBroadcast/) | *How does batching cut broadcast syscalls to 1/N?* |
| [MatchSimulation](samples/MatchSimulation/) | *If the server dies, can I really restore its state?* |
| [ReplayInspector](samples/ReplayInspector/) | *What can I actually do with the journal?* |
| [AspNetCoreTelemetry](samples/AspNetCoreTelemetry/) | *Can I see what it's doing in production?* |

```bash
dotnet run --project Example -c Release
dotnet run --project samples/ChatBroadcast -c Release
```

---

## Why this and not disruptor-net?

[disruptor-net](https://github.com/disruptor-net/Disruptor-net) is a mature, excellent,
general-purpose Disruptor. **If you need a general-purpose ring buffer, evaluate it first.**

DisruptorCS answers a different question: *"what does a Disruptor look like when the things a
game server rebuilds every time are already in the box?"*

| | disruptor-net | DisruptorCS |
|---|---|---|
| Maturity / production history | ✅ Years of use | New (133 tests, soak- and Coyote-verified) |
| Raw unicast throughput | Faster under backpressure | **Faster** when the ring isn't saturated — [both results published](#benchmarks) |
| Room / session sharding | — | ✅ `ShardedDisruptor` (5,000 rooms → 8 threads) |
| Fixed-tick integration | — | ✅ `FixedTickDriver` + `OnTimeout` |
| Deterministic journal / replay | — | ✅ With integrity verification |
| Backpressure policies (drop / reject) | — | ✅ `PublishGate`, 3 policies |
| Stall diagnostics (names the slow handler) | — | ✅ Built in, OpenTelemetry-compatible |
| Compile-time misuse detection | — | ✅ Roslyn analyzers (DISR001–005) |
| DI / Generic Host / health checks | — | ✅ `DisruptorCS.Extensions.Hosting` |
| Thread-free polling consumer | ✅ `EventPoller` | ✅ `EventPoller` |
| Korean documentation | — | ✅ Complete |

**In one line: DisruptorCS is a game-server distribution of the Disruptor pattern.**
It doesn't reinvent the core — it standardizes the layer above it.

---

## What's in the box

### Core

| Type | Purpose |
|---|---|
| `RingBuffer<T>` | Pre-allocated ring buffer for reference-type events |
| `ValueRingBuffer<T>` | **Value-type** events stored inline (`ref T` access, excluded from GC scanning) |
| `EventPoller<T>` | Thread-free consumption — integrate with an existing game loop |
| `IBatchEventHandler<T>` | Receive a whole batch as a `ReadOnlySpan<T>` (SIMD-friendly) |
| `SingleProducerSequencer` | CAS-free single-producer sequencer |
| `MultiProducerSequencer` | CAS-based multi-producer sequencer |
| `DependentSequenceGroup` | First-class "what am I waiting on" — lets blocking strategies actually block |
| `SequenceWaitResult` | Exception-free wait result (Timeout / Cancelled / sequence) |
| `WorkerPool<T>` | **Distributes** events across workers (order-independent, slow I/O) |
| `PublishGate<T>` | Backpressure policy (Block / DropWhenCongested / FailAfterTimeout) |
| `DisruptorMetrics` | `System.Diagnostics.Metrics` — OpenTelemetry-compatible |
| `AsyncWaitStrategy` + `AsyncBatchEventProcessor<T>` | `ValueTask`-based consumption for slow fan-out stages |

### Wait strategies

| Strategy | Wake-up p50 (measured) | CPU | Use when |
|---|---|---|---|
| `BusySpinWaitStrategy` | **2.6 µs** | 100% | Bare metal, dedicated cores |
| `YieldingWaitStrategy` | **2.4 µs** | high | Handler threads < logical cores |
| `SpinWaitWaitStrategy` | low | medium | General-purpose spin |
| `SleepingWaitStrategy` | medium | low | Background pipelines |
| `BlockingWaitStrategy` | 19.1 µs | lowest | Oversubscribed |
| **`TimeoutBlockingWaitStrategy`** | 22.8 µs | lowest | **Game-server default** — provides the `OnTimeout` hook |
| `PhasedBackoffWaitStrategy` | 25.5 µs | adaptive | Intermittent bursts (not cold-idle traffic) |
| `AsyncWaitStrategy` | ~20 µs | lowest | `await`-based consumers |

### Game layer (`Disruptor.Game`)

| Type | Problem it solves |
|---|---|
| `ShardedDisruptor<T>` | One Disruptor per room means 10,000 rooms = 10,000 threads. Sharding makes **thread count independent of room count** |
| `FixedTickDriver` | Event consumption and fixed-step simulation on one thread (with death-spiral protection) |
| `JournalWriter` / `JournalHandler<T>` / `JournalReader` | Sequence number = global order → bug reproduction, hot-standby replication, cheat verification, crash recovery |
| `HighResolutionTimerScope` | Idle tick precision on Windows: **15.40 ms → 1.52 ms** (measured) |

---

## Game-server features

### 1. Tick loop integration — no separate timer thread

```csharp
public sealed class RoomHandler : IEventHandler<GameCommand>
{
    private readonly FixedTickDriver _driver;
    private readonly List<GameCommand> _pending = new(256);

    public RoomHandler() => _driver = FixedTickDriver.At60Hz(tick => Simulate(tick));

    public void OnEvent(GameCommand cmd, long seq, bool endOfBatch)
    {
        _pending.Add(cmd.Snapshot());        // bucket into the current tick → determinism
        if (endOfBatch) _driver.TryAdvance();
    }

    // Called even with no events (TimeoutBlockingWaitStrategy)
    public void OnTimeout(long lastSeq) => _driver.TryAdvance();
}
```

For precise ticks while idle, turn on the high-resolution timer at startup:

```csharp
using var timerScope = new HighResolutionTimerScope();   // 15.40 ms → 1.52 ms (measured)
```

### 2. Backpressure policy — dropping is a policy too

```csharp
var gate = new PublishGate<GameCommand>(ring, disruptor.Metrics);

// Movement packets — only the latest matters, so drop when congested
gate.TryPublish(BackpressurePolicy.DropWhenCongested, (e, s) => Fill(e, move));

// Currency changes — never droppable, so reject after a timeout and answer with an error
if (!gate.TryPublish(BackpressurePolicy.FailAfterTimeout, (e, s) => Fill(e, purchase),
                     timeout: TimeSpan.FromMilliseconds(50)))
    return Error.ServerBusy;
```

### 3. Room sharding — 10,000 rooms on 8 threads

```csharp
using var sharded = new ShardedDisruptor<GameCommand>(
    shardCount: 8,                 // power of two
    ringSizePerShard: 4096,
    eventFactory: () => new GameCommand(),
    handlerFactory: i => new RoomShardHandler(i));

sharded.Start();

// The same room always maps to the same shard → per-room ordering is guaranteed
sharded.Publish(roomId, (evt, seq) => Fill(evt, cmd));
```

| Approach | Threads | Ring memory (10,000 rooms) | Verdict |
|---|---:|---:|---|
| One Disruptor per room (ring 1024) | 10,000 | ~1.3 GB | Impossible |
| One `Channel<T>` per room | 0 (thread pool) | tens of MB | Works, but no ordering/batching benefit |
| **ShardedDisruptor (16 shards, ring 4096)** | **16** | **~8 MB** | **Recommended** |

> The trade-off: one heavy room slows the other rooms on its shard (head-of-line blocking).
> Cap per-room processing time and push the overflow to a `WorkerPool`.

### 4. Deterministic journal / replay

```csharp
// The journal is written before the logic commits
disruptor.HandleEventsWith(new JournalHandler<GameCommand>(writer, codec))
         .Then(new WorldUpdateHandler(world));

// Replay: feed the same order back and the state is reproduced exactly
JournalReader.Replay(stream, codec, () => new GameCommand(), new WorldUpdateHandler(replayWorld));
```

Records are checksummed (XxHash3 over header + payload), length-bounded, and sequence-monotonic —
so a corrupted journal fails loudly instead of silently replaying wrong state:

```csharp
JournalVerificationResult result = JournalReader.Verify(stream);
// "Corrupt: valid through 1,204,331 records. seq 1204331: checksum mismatch"
```

With this in place you get: exact bug reproduction, hot-standby replication, cheat verification
(client claim vs. server journal), and crash recovery from the last valid sequence.

### 5. Observability — you have to be able to debug an incident

```csharp
new DisruptorOptions
{
    Name = "packet-pipeline",
    EnableMetrics = true,                             // handler_lag / utilization / batch_size ...
    StallThreshold = TimeSpan.FromMilliseconds(250),
    OnStall = info => logger.Warn(info.ToString()),
}
```

```
[WARN] Disruptor 'packet-pipeline' producer blocked for 250ms.
       Slowest handler = 'DbJournalHandler' (seq 1,204,331 / cursor 1,270,867, lag 66,536)
```

The single most important metric is **`disruptor.handler_lag`**. If it keeps growing, that handler
is the bottleneck and producers are about to block.

### 6. Exceptions don't kill the process

```csharp
disruptor.SetDefaultExceptionHandler(new ResilientExceptionHandler<GameCommand>(
    onError: (ex, seq, evt) => logger.Error(ex, "seq={Seq}", seq),
    maxConsecutiveFailures: 100));   // halt only this processor past the threshold

disruptor.ProcessorHalted += (p, ex) => alerting.Fatal($"handler '{p.Name}' halted — drain needed");
```

| Policy | Behaviour |
|---|---|
| `Skip` | Drop the event and continue (game-server default) |
| `Retry` | Re-process the same event (idempotent handlers only) |
| `Halt` | Stop **only this processor**. The process stays alive |

### 7. ASP.NET Core / Generic Host integration

```bash
dotnet add package DisruptorCS.Extensions.Hosting
```

```csharp
builder.Services.AddDisruptor<GameCommand>(d => d
    .WithEventFactory(() => new GameCommand())
    .WithRingSize(65536)
    .WithProducerType(ProducerType.Multi)
    .HandleEventsWith<JournalHandler>()     // handlers resolved from DI
    .Then<GameLogicHandler>()
    .ThenWorkerPool<DbWriteWorker>(workers: 4));

builder.Services.AddHealthChecks().AddDisruptorCheck<GameCommand>();
```

Start/Shutdown are bound to the host lifecycle (with draining), and a halted processor is reported
as `Critical` and — by default — triggers a graceful application stop.

---

## Architecture

```
┌──────────────────────────────────────────────────────┐
│                    PRODUCER(s)                        │
│     Next() → Write Event → Publish()                 │
└──────────────┬───────────────────────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────────────┐
│              RING BUFFER (pre-allocated T[])          │
│  ┌───┬───┬───┬───┬───┬───┬───┬───┐                  │
│  │ 0 │ 1 │ 2 │ 3 │ 4 │ 5 │ 6 │ 7 │  (size = 2^n)   │
│  └───┴───┴───┴───┴───┴───┴───┴───┘                  │
│         ↑ Cursor (Sequencer)                         │
└──────┬──────────┬──────────┬─────────────────────────┘
       │          │          │
       ▼          ▼          ▼
┌──────────┐ ┌──────────┐ ┌──────────────────┐
│ Consumer │ │ Consumer │ │   Consumer C      │
│    A     │ │    B     │ │ (depends on A, B) │
│(Journal) │ │(Replica) │ │ (BusinessLogic)   │
└────┬─────┘ └────┬─────┘ └────────┬─────────┘
     │            │                 │
     └────────────┴────┬────────────┘
                       │
              SequenceBarrier
      (WaitFor → DependentSequenceGroup → WaitStrategy)
```

---

## Core design principles

**False-sharing padding that accounts for prefetch.** `Sequence` pads **128 bytes on each side** of
its value. Intel's streaming prefetcher pulls adjacent cache-line pairs (128 B), so 64 isn't enough.

**Pre-allocation.** Every event object is created when the ring buffer is built. Publishing
overwrites fields of an existing slot instead of allocating — measured **0 bytes across 10M events**.

**Slot clearing.** A slot that holds a `byte[]` or a session handle keeps it alive for up to one full
lap of the ring. Implement `IClearable` and the framework calls `Clear()` right after the last
handler processes the event.

**Lock-free.** `SingleProducerSequencer` uses no CAS at all; `MultiProducerSequencer` uses only CAS.
No locks anywhere on the hot path.

**No `Thread.Sleep(1)`.** Every wait path uses `SpinWait.SpinOnce(sleep1Threshold: -1)`. The default
`SpinOnce()` eventually sleeps, and Windows' default timer resolution (15.6 ms) turns that into a
full lost tick for a 60 Hz server. Measured: `>15 ms` stalls went **284 → 0** and throughput **+50%**.

**Batch-shaped consumer loop.** `try`/`catch`, failure-streak reset, and `IClearable` checks all
happen at batch boundaries, not per event — a `try` region per event constrains the JIT's code motion.
Measured: **339M → 649M ops/s (+87%)** in a consumer-bound configuration.

**Devirtualized publish path.** `RingBuffer.Next()`/`Publish()` dispatch through a struct
(`SequencerDispatcher`) that branches on the concrete sealed sequencer type, so the JIT emits a
direct, inlinable call instead of an interface dispatch. Added only after a benchmark showed the gap.

**Multicast.** Unlike a queue, every event reaches every registered consumer. No copying.

**Dependency graph.** `HandleEventsWith(...).Then(...)` (and `.And(...)` to join groups) expresses
execution order with a single ring buffer — no per-stage queue cost.

---

## When to use Disruptor vs `Channel<T>`

`System.Threading.Channels` is excellent and is the right default for most applications.
The fundamental difference is that a Disruptor **isn't a queue**: a queue fuses storage, sequence
claiming, and consumer notification into one structure; a Disruptor separates all three.

```
1. Do two or more consumers each need to see every event?
   ├─ Yes → Disruptor candidate  (Channel requires duplicating per consumer)
   └─ No  → 2

2. Must processing order be globally fixed? (replay / rollback / journaling)
   ├─ Yes → Disruptor candidate  (the sequence number IS the global order)
   └─ No  → 3

3. Is p99.9 latency an SLA, and is it under 1 ms?
   ├─ Yes → Disruptor candidate
   └─ No  → use Channel<T>. Stopping here is a perfectly good answer.

4. If it's a candidate, do all of these hold?
   ├─ Total handler threads ≤ logical cores − 4   → otherwise shard
   ├─ No await / blocking I/O inside handlers      → otherwise use a WorkerPool
   └─ Fewer than a few dozen ring buffers          → otherwise sharding is mandatory
```

### Recommendation per game-server component

| Component | Recommendation |
|---|---|
| Packet receive → decode → logic pipeline | **Disruptor** (multicast + ordering + batching) |
| Journaling / replay / standby replication | **Disruptor (top pick)** |
| Room/zone command queues (thousands) | **ShardedDisruptor** — never one per room |
| Async logging / metrics | **Disruptor** (same reason Log4j2 uses it) |
| Ranking / currency settlement | **Disruptor** (batch commit) |
| DB writes / external API calls | **WorkerPool** or `Channel<T>` |
| Request-response RPC | `Channel<T>` / `TaskCompletionSource` |
| HTTP admin API | Plain ASP.NET Core |

---

## Benchmarks

Measured on Intel i7-12700KF (12P/20L), Windows 11, .NET 10.0.400, Release/x64, Server GC.
Full results and how to reproduce: [`docs/benchmarks/`](docs/benchmarks/).

| Scenario | Result |
|---|---|
| Single-producer throughput (10M events, ring 64K) | **249.8M ops/s**, 4.0 ns/op, **0 bytes allocated** |
| Multi-producer integrity (8P × 100K, ring 1024) | **0 lost**, 0 slot overwrites, 16.7M ops/s |
| Room sharding (5,000 rooms × 40 events) | 200,000 events / 31 ms / **8 threads**, per-room order preserved |
| Journal → replay (20,000 events) | State checksum matches exactly |
| Batch handler (200,000 events) | 977 `OnBatch` calls instead of 200,000 per-event calls |
| Blocking strategy + slow upstream handler | **0.07 cores** (no hot-spin) |
| Soak (2,480 rounds, 49.6M events under constant backpressure) | 0 loss, heap flat at 3.2 MB, thread count flat |
| Producer wait under backpressure | `>15 ms` stalls: **0** (was 284 per 300K publishes) |
| Broadcast batching (200K messages × 200 sessions) | 40,000,000 → **10,400 flushes** (≈3,800× fewer syscalls) |
| Sharded journal → replay (10,000 rooms, 2M commands) | Live and replayed state checksums identical, 0 room mismatches |
| Coyote systematic exploration (8 models × 1,000 schedules) | 0 bugs; the negative control found its deadlock in 39 |

**Head-to-head with disruptor-net — including where we lose:**

| Condition | Winner | Margin |
|---|---|---|
| Large ring (backpressure rare) | **DisruptorCS** | 1.5× faster |
| Small ring (backpressure constant) | **disruptor-net** | 2.9× faster |
| Multi-producer, small ring | **disruptor-net** | 1.3–1.8× faster |
| vs `Channel<T>` | **DisruptorCS** | 8–38× faster, 25–1,360× less allocation |

Our per-event cost is lower (3.1 ns vs 4.8 ns); our backpressure handoff is more expensive.
That gap is documented as an [open item](docs/benchmarks/README.md#open-item-backpressure-bound-throughput)
with the full list of hypotheses we already ruled out by measurement.

Latency distribution matters more than the mean for a game server —
run `dotnet run --project Disruptor.Benchmarks -c Release -- latency` for
p50/p99/p99.9/p99.99/max plus `>1 ms` / `>5 ms` / `>15 ms` counts.

Full results, methodology, and the open performance item:
[`docs/benchmarks/`](docs/benchmarks/).

---

## Pitfalls the analyzer catches for you

The package ships Roslyn analyzers. These mistakes compile fine but corrupt data at runtime,
so they're caught at build time instead:

| ID | Rule | Severity |
|---|---|---|
| **DISR001** | A handler method is declared `async` — it returns at the first `await`, so ordering breaks and slots get reused mid-processing | Error |
| **DISR002** | `Next()` without a matching `Publish()` in a `finally` — an exception jams the ring forever | Warning |
| **DISR003** | A capturing lambda passed to `PublishEvent` — allocates a closure per publish | Info |
| **DISR004** | Ring size isn't a power of two | Error |
| **DISR005** | A batch handler stores a span element — the slot is reused the moment `OnBatch` returns, so the reference silently starts pointing at a different event | Warning |

Details: [`docs/analyzers/`](docs/analyzers/).

---

## Project layout

```
DisruptorCS.slnx
├── Directory.Build.props            Shared package metadata + MinVer version floor
├── build/                           pack.ps1 (release packaging + nupkg verification), icon.png
├── Disruptor/                       Core library (net8.0 / net10.0)
│   ├── Async/                       AsyncWaitStrategy, AsyncBatchEventProcessor, AsyncBridge
│   ├── Dsl/                         Disruptor<T>, ValueDisruptor<T>, DisruptorOptions
│   ├── WaitStrategy/                8 wait strategies
│   ├── Work/                        WorkerPool, WorkProcessor, IWorkHandler
│   └── Game/                        ShardedDisruptor, FixedTickDriver, Journal, HighResolutionTimerScope
├── Disruptor.Analyzers/             Roslyn analyzers (DISR001–005), shipped in the package
├── Disruptor.Extensions.Hosting/    DI / Generic Host / health checks (a second package)
├── Disruptor.Tests/                 xUnit — 113 tests
├── Disruptor.Analyzers.Tests/       Analyzer tests — 20 tests
├── Disruptor.Concurrency.Tests/     Coyote systematic concurrency models — 8 models
├── Disruptor.Benchmarks/            BenchmarkDotNet + latency distribution runner
├── Disruptor.Soak/                  Long-running invariant harness
├── Example/                         13 feature scenarios
└── samples/                         5 end-to-end samples
```

```bash
dotnet build DisruptorCS.slnx -c Release
dotnet test  Disruptor.Tests  -c Release          # memory-ordering bugs only show with optimizations on
dotnet test  Disruptor.Tests  -c Debug            # Debug-only diagnostics (single-producer misuse, async handlers)
dotnet run   --project Example -c Release
dotnet run   --project Disruptor.Soak -c Release -- --minutes 30
dotnet run   --project Disruptor.Benchmarks -c Release -- latency
```

Systematic concurrency exploration needs one extra step, because Coyote controls scheduling by
rewriting IL:

```bash
dotnet tool restore
dotnet build  Disruptor.Concurrency.Tests -c Release
dotnet coyote rewrite Disruptor.Concurrency.Tests/rewrite.coyote.json
dotnet Disruptor.Concurrency.Tests/bin/Release/net8.0/rewritten/Disruptor.Concurrency.Tests.dll -i 1000
```

See [`docs/concurrency-testing.md`](docs/concurrency-testing.md) — including the two limits that
matter when quoting the results.

Release procedure: [`docs/RELEASE.md`](docs/RELEASE.md).

---

## Gotchas

**Never `await` inside a handler.** Disruptor consumers are synchronous, blocking threads.
`async void OnEvent` returns at the first `await`, so the framework advances the sequence and the
slot gets reused while you're still using it. DISR001 catches this at compile time. Push slow I/O
into a `WorkerPool`, or use `AsyncBatchEventProcessor` where a batch boundary is an acceptable
ordering unit.

**`ProducerType.Single` means exactly one thread.** In Release it corrupts data silently; Debug
builds throw immediately when a second thread publishes.

**Don't spin inside a container.** With a Kubernetes `cpu: "2"` limit, one `BusySpin` handler burns
the quota and triggers CFS throttling, stalling the whole process in 100 ms chunks. Use the default
`TimeoutBlockingWaitStrategy`.

**Always `Publish()` what you `Next()`.** Even on the exception path — a single unpublished slot
stops gating forever. `PublishEventScope()` makes this structural; DISR002 catches the rest.

**Thread budget.** Handler threads assume dedicated cores:

```
Available logical cores = C
  Network I/O                              : ~2
  Disruptor handlers (spin strategies)      : ≤ C - 4   ← never exceed
  .NET ThreadPool (timers / DB / external)  : the rest
```

---

## Documentation

- [`IMPROVEMENTS.md`](IMPROVEMENTS.md) — performance/stability investigation and adoption strategy (with probe measurements)
- [`EVALUATION.md`](EVALUATION.md) — game-server suitability assessment and the design that produced this library
- [`docs/analyzers/`](docs/analyzers/) — analyzer rule reference
- [`docs/benchmarks/`](docs/benchmarks/) — full benchmark results
- [`docs/RELEASE.md`](docs/RELEASE.md) — manual release checklist
- [`CHANGELOG.md`](CHANGELOG.md)
- [`CONTRIBUTING.md`](CONTRIBUTING.md)

## License

MIT
