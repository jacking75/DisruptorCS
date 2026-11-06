# Samples

> **English** · [한국어](README.ko.md)

This is the single entry point for runnable examples. Follow **basic recipes → application samples**.
Performance measurements live in `Disruptor.Benchmarks`; correctness checks live in `Disruptor.Tests`.
Run commands from the repository root with the .NET 10 SDK. Review
[processing semantics](../docs/processing-semantics.md) before adapting ownership and shutdown.

## Basic recipes

Start with [UsageRecipes](UsageRecipes/README.md) ([한국어](UsageRecipes/README.ko.md)):
**13 small, independently selectable examples** covering publication, dependency graphs, multiple
producers, batching, struct events, backpressure, polling, workers, async sinks, pooled buffers,
error handling, snapshot streams, and journal/replay. Each checks its result and returns a nonzero
exit code on failure. Start with `publish → pipeline → multi-producer → backpressure`, then choose
a recipe for your use case.

```bash
dotnet run --project samples/UsageRecipes -c Release -- --list
dotnet run --project samples/UsageRecipes -c Release -- all
```

## Application samples

Each sample answers **one question** a game-server engineer actually asks before adopting a
library. They are runnable programs, not snippets — every number they print is measured on your
machine at the moment you run them.

| Sample | The question it answers |
|---|---|
| [GameLoopPoller](GameLoopPoller/) | *I already have a main loop. Can I use this without changing my thread model?* |
| [ChatBroadcast](ChatBroadcast/) | *How does batching reduce broadcast flush calls?* |
| [MatchSimulation](MatchSimulation/) | *If the server dies, can I really restore the state it had?* |
| [ReplayInspector](ReplayInspector/) | *You say there's a journal — what can I actually do with it?* |
| [AspNetCoreTelemetry](AspNetCoreTelemetry/) | *If I put this in our ASP.NET Core service, can I see what it's doing in production?* |

Run any of them:

```bash
dotnet run --project samples/GameLoopPoller -c Release
```

---

## GameLoopPoller

`EventPoller` lets the **existing** loop consume events — Unity's `Update()`, your own frame loop,
an SDL tick. Zero dedicated consumer threads are added.

Prints frame-time percentiles, tick count vs. the 60 Hz expectation, and dropped commands, so you
can see the cost of integration rather than take it on faith.

## ChatBroadcast

Runs the same pipeline twice — flushing per event, then flushing only at `endOfBatch` — and prints
both simulated flush counts. It does not open sockets or measure syscalls. In a real sender,
buffering, partial writes, transport behavior, and scheduling determine the actual send count.

Measured on an i7-12700KF: **40,000,000 → 10,400 flushes** (≈3,800×) for 200,000 messages to
200 sessions.

## MatchSimulation

10,000 rooms across 8 shards at 60 Hz, journaling every command, then replaying the journals into
fresh state and **comparing checksums room by room**. The sample fails (exit code 1) if live and
replayed state diverge — it verifies determinism rather than claiming it.

Also demonstrates: `ShardedDisruptor` key→shard mapping, `FixedTickDriver`, write-ahead ordering
(journal first, mutate second), and `JournalReadMode.TruncateAtCorruption` for crash recovery.

## ReplayInspector

A CLI (`djrn`) that treats the journal as an operational artifact:

```
djrn verify <file>                    checksum + monotonic-sequence validation
djrn dump   <file> [--from --limit]   decoded record dump
djrn replay <file> --until-seq N      restore the state as of sequence N
djrn diff   <fileA> <fileB>           find the first sequence where two runs diverged
```

`djrn demo` (or running with no arguments) builds a journal, corrupts one byte in the middle,
truncates another copy's tail, and shows how each case reads back — including the distinction
between **content corruption** (checksum catches it) and a **torn tail** (a normal stopping point).

## AspNetCoreTelemetry

The sample registers its pipeline and health check as follows:

```csharp
builder.Services.AddDisruptor<GameCommand>(d => d
    .WithEventFactory(() => new GameCommand())
    .WithRingSize(4096)
    .WithProducerType(ProducerType.Multi)
    .WithOptions(o => o with { EnableMetrics = true })
    .HandleEventsWith<AuditHandler>()
    .Then<GameLogicHandler>());
builder.Services.AddHealthChecks().AddDisruptorCheck<GameCommand>(
    degradedUtilization: 0.50, unhealthyUtilization: 0.85);
```

| Endpoint | Purpose |
|---|---|
| `POST /commands` | `FailAfterTimeout`: synchronously tries for 50 ms; returns **202** when published, **503** on timeout |
| `POST /throttle?spin=N` | Slow the consumer down to simulate a slow downstream |
| `POST /load?rps=&seconds=` | Generate load and watch health go Degraded → Unhealthy |
| `GET /health` | Aggregate utilization and halted names; sample thresholds 50% Degraded / 85% Unhealthy |
| `GET /metrics` | Live `System.Diagnostics.Metrics` snapshot |
| `GET /pipeline` | Cursor, per-processor sequences, applied count |

The sample's handler/world types and other DI registrations are defined in `Program.cs`.
The command timeout occupies the request thread; use a drop policy or zero-timeout claim if the
application requires immediate capacity rejection. A 202 response acknowledges publication, not
completed processing. `/health` returns 200 for Healthy/Degraded and 503 for Unhealthy.

For OpenTelemetry, add the appropriate hosting/exporter packages and register the meter:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(DisruptorMetrics.MeterName));
```

The sample collects the same instruments by hand only so it runs offline with no extra packages.

## Where the former combined demo went

The top-level `Example` project has been retired. Its lessons are consolidated below; the original
13-scenario sequence is listed to help existing readers find the corresponding code.

| Former scenario | Current location |
|---|---|
| 1. Basic publication | [publish](UsageRecipes/Publication.cs) |
| 2. Dependency graph and `And` | [pipeline](UsageRecipes/OrderPipeline.cs) |
| 3. Batch handler | [batch](UsageRecipes/BatchAggregation.cs) |
| 4. Event poller | [poller](UsageRecipes/FramePolling.cs) |
| 5. Backpressure policies | [backpressure](UsageRecipes/Backpressure.cs) |
| 6. Fixed ticks and idle timeouts | [GameLoopPoller](GameLoopPoller/Program.cs), [MatchSimulation](MatchSimulation/Program.cs) |
| 7. Journal and replay | [journal](UsageRecipes/JournalReplay.cs) |
| 8. Journal integrity | [ReplayInspector](ReplayInspector/Demo.cs) |
| 9. Room sharding | [MatchSimulation](MatchSimulation/Program.cs) |
| 10. Logic followed by a worker pool | [workers](UsageRecipes/WorkerJobs.cs) |
| 11. Stall diagnostics | [AspNetCoreTelemetry](AspNetCoreTelemetry/Program.cs) (`OnStall`, `/throttle`, `/load`) |
| 12. Throughput measurement | [UnicastBenchmarks](../Disruptor.Benchmarks/ThroughputBenchmarks.cs) |
| 13. Multi-producer integrity stress | [LosslessStressTests](../Disruptor.Tests/LosslessStressTests.cs); introductory [multi-producer](UsageRecipes/MultiProducer.cs) |

The performance and stress suites already cover those responsibilities, so the old inline probes
are not duplicated. Benchmark parameters differ from the retired demo; see the
[benchmark guide](../docs/benchmarks/README.md) before comparing measurements.

```bash
dotnet run --project Disruptor.Benchmarks -c Release -- --filter '*UnicastBenchmarks*'
dotnet test Disruptor.Tests -c Release --filter 'FullyQualifiedName~LosslessStressTests'
```
