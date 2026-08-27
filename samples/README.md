# Samples

Each sample answers **one question** a game-server engineer actually asks before adopting a
library. They are runnable programs, not snippets — every number they print is measured on your
machine at the moment you run them.

| Sample | The question it answers |
|---|---|
| [GameLoopPoller](GameLoopPoller/) | *I already have a main loop. Can I use this without changing my thread model?* |
| [ChatBroadcast](ChatBroadcast/) | *How does batching cut syscalls to 1/N when broadcasting packets?* |
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
both flush counts. In a real server each flush is one `Socket.SendAsync`, so the ratio is the
syscall reduction.

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

Three lines of integration, three things immediately visible:

```csharp
services.AddDisruptor<GameCommand>(d => d./* … */.WithOptions(o => o with { EnableMetrics = true }));
services.AddHealthChecks().AddDisruptorCheck<GameCommand>();
```

| Endpoint | Purpose |
|---|---|
| `POST /commands` | Publish through `PublishGate` — returns **503**, never blocks the request thread |
| `POST /throttle?spin=N` | Slow the consumer down to simulate a slow downstream |
| `POST /load?rps=&seconds=` | Generate load and watch health go Degraded → Unhealthy |
| `GET /health` | Ring utilization, halted processors, per-processor detail |
| `GET /metrics` | Live `System.Diagnostics.Metrics` snapshot |
| `GET /pipeline` | Cursor, per-processor sequences, applied count |

For OpenTelemetry, replace the sample's `MeterListener` with one line:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(DisruptorMetrics.MeterName));
```

The sample collects the same instruments by hand only so it runs offline with no extra packages.
