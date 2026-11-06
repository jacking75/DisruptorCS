# Benchmarks

Unless a dated section specifies its own environment, numbers here were produced on:

| | |
|---|---|
| CPU | Intel Core i7-12700KF (12 physical / 20 logical) |
| RAM | 64 GB |
| OS | Windows 11 (10.0.26200) |
| SDK / runtime | SDK 10.0.400 / .NET 10.0.11, Release, x64, Server GC |
| Harness | BenchmarkDotNet 0.14.0, `RunStrategy.Monitoring` |
| Compared against | [disruptor-net](https://www.nuget.org/packages/Disruptor) 6.0.1, `System.Threading.Channels` |

Reproduce:

```bash
dotnet run --project Disruptor.Benchmarks -c Release -- --filter '*Comparison*' \
  --exporters GitHub --artifacts docs/benchmarks/raw
dotnet run --project Disruptor.Benchmarks -c Release -- latency
```

Raw exports live in [`raw/`](raw/); the latency run is in [`latency.txt`](latency.txt).
The hardware above is the previously reported environment; the stored BenchmarkDotNet exports
identify the CPU as `Unknown processor`. SDK and runtime versions are distinct.

---

## Summary, including where we lose

**We publish the comparisons we lose.** Showing only favourable conditions is the fastest way to
lose a reader's trust, and the shape of the loss is itself useful information.

| Condition | Winner | Margin |
|---|---|---|
| Large ring — backpressure is rare | **DisruptorCS** | 1.5× faster than disruptor-net |
| Small ring — backpressure on nearly every publish | **disruptor-net** | 2.9× faster than DisruptorCS |
| Multi-producer, small ring | **disruptor-net** | 1.3× (2 producers) – 1.8× (8 producers) |
| vs `Channel<T>` (measured configurations) | **DisruptorCS** | 8–38× faster, 25–1,360× less allocation |

The pattern is consistent and reproducible: **our per-event cost is lower, our backpressure
handoff is more expensive.** See [the open item](#open-item-backpressure-bound-throughput) below.

---

## Unicast: 1 producer → 1 consumer, 2,000,000 events

| Library | Ring | Mean | Allocated |
|---|---:|---:|---:|
| **DisruptorCS** | 65,536 | **6.29 ms** | 2,566 KB |
| disruptor-net | 65,536 | 9.61 ms | 2,569 KB |
| `Channel<T>` (bounded SPSC) | 65,536 | 234.81 ms | 63,526 KB |
| | | | |
| DisruptorCS | 1,024 | 36.18 ms | 46 KB |
| **disruptor-net** | 1,024 | **12.32 ms** | 48 KB |
| `Channel<T>` (bounded SPSC) | 1,024 | 339.75 ms | 62,519 KB |

At ring 65,536 the consumer keeps up, so this measures raw per-event publish + dispatch cost:
**3.1 ns/event for us, 4.8 ns for disruptor-net**.

At ring 1,024 the ring is saturated and nearly every publish must wait for the consumer to free a
slot. disruptor-net's cost barely changes between the two ring sizes; ours rises ~6×.

This benchmark allocates a new reference-type event for every channel write while the Disruptor
reuses preallocated slots. The allocation ratio includes that payload-ownership choice; it is not
an inherent per-event allocation requirement of `Channel<T>`. Value-type payloads or pooling would
need a separate comparison.

## Multi-producer: N producers → 1 consumer, 200,000 events each, ring 1,024

| Library | Producers | Mean | Allocated |
|---|---:|---:|---:|
| DisruptorCS | 2 | 28.99 ms | 50 KB |
| **disruptor-net** | 2 | **21.78 ms** | 53 KB |
| DisruptorCS | 8 | 90.52 ms | 51 KB |
| **disruptor-net** | 8 | **49.13 ms** | 54 KB |

Same story: ring 1,024 means constant backpressure. Correctness is unaffected — the integrity
suite runs the identical configuration and confirms **0 lost events, 0 slot overwrites**.

## Historical single-producer throughput (retired combined demo)

10,000,000 events, ring 65,536, `YieldingWaitStrategy`, trivial handler:

| Metric | Value |
|---|---|
| Throughput | **249.8 M ops/s** |
| Latency | **4.0 ns/op** |
| Allocated | **0 bytes** (0.0000 B/event) |

These measurements came from scenario 12 of the retired combined demo. They are retained as
historical results, not measurements of the learning recipes. For current throughput measurements:

```bash
dotnet run --project Disruptor.Benchmarks -c Release -- --filter '*UnicastBenchmarks*'
```

`UnicastBenchmarks` uses 1,000,000 events per invocation and includes setup/teardown; its numbers
are not directly comparable to the old 10,000,000-event loop above.

## Historical game-server scenarios (retired combined demo)

| Scenario | Result |
|---|---|
| Multi-producer integrity (8P × 100K, ring 1,024) | 0 lost, 0 overwrites, 16.7 M ops/s |
| Room sharding (5,000 rooms × 40 events) | 200,000 events / 31 ms / **8 threads**, per-room order preserved |
| Journal → replay (20,000 events) | State checksum identical |
| Batch handler (200,000 events, ring 4,096) | 977 `OnBatch` calls instead of 200,000 per-event calls |
| EventPoller (100,000 events) | 391 frames, **0 extra threads**, checksum matches |
| Blocking strategy + slow upstream handler | 0.07 cores (no hot-spin) |

For current runnable code, follow the [scenario migration table](../../samples/README.md#where-the-former-combined-demo-went).
Correctness checks now belong to the recipes and test suite, rather than inline benchmark probes:

```bash
dotnet run --project samples/UsageRecipes -c Release -- all
dotnet test Disruptor.Tests -c Release --filter 'FullyQualifiedName~LosslessStressTests'
```

## 2026-11-07 reliability changes: throughput check

The batch recovery and drop-policy fixes were checked against the unchanged
`UnicastBenchmarks.DisruptorSingleProducer` benchmark. The baseline library source was commit
`b119515`; the after run used the working-tree recovery changes. Each invocation publishes
1,000,000 events and includes setup/teardown. Both runs used .NET SDK 10.0.301, runtime 10.0.9,
Release/x64, non-concurrent Server GC, and Windows build 26300.9818. BenchmarkDotNet could not
identify the processor or change the power plan in this environment.

| Ring size | Before mean | After mean | Before median | After median |
|---|---:|---:|---:|---:|
| 1,024 | 19.995 ms | 19.049 ms | 17.255 ms | 18.933 ms |
| 65,536 | 4.111 ms | 3.661 ms | 4.141 ms | 3.853 ms |

These are **regression-check observations, not a speedup claim**. There were only five measured
iterations after two warmups, iterations were shorter than BenchmarkDotNet recommends, and the
99.9% confidence intervals overlap substantially. The small-ring median increased while its mean
decreased. This does not establish performance equivalence, or say anything about all other
producer/consumer topologies. The fixes prioritize correct recovery, bounded retry shutdown,
payload release, and avoiding capacity waits in the drop policy; no per-event cancellation check
was added to the normal synchronous event loop.

Raw evidence: [before CSV](reliability-2026-11-07/before.csv),
[after CSV](reliability-2026-11-07/after.csv),
[before full output](reliability-2026-11-07/before.txt),
[after full output](reliability-2026-11-07/after.txt).

```bash
dotnet run --project Disruptor.Benchmarks -c Release -- --filter '*UnicastBenchmarks.DisruptorSingleProducer*' --artifacts artifacts/reliability-check
```

The deterministic tests in `BatchRecoveryTests` and `PublishGateSaturationTests` cover the failure
paths that this throughput benchmark does not exercise. In particular, a four-slot ring forces
one batch to split at the wrap boundary, and an injected second-segment failure distinguishes
retrying only that segment from replaying the already completed first segment.

The separately added async cancellation fix is covered by `AsyncCancellationTests` (idle wait,
external-token shutdown, and cancellation racing publication). This synchronous throughput
benchmark does not measure async waiting or cancellation-registration overhead.

Validation of the reliability changes:

| Check | Result |
|---|---|
| Full solution, Release | Build succeeded, zero warnings/errors |
| Core tests, Release and Debug | 141 passed in each configuration, including 28 new regression cases |
| Analyzer tests, Release | 20 passed |
| Stress suite | 20 consecutive passes, 180 test cases in total |
| Async cancellation suite | 10 consecutive passes, including 2,000 cancellation/publication races |
| Usage recipes | All 13 passed in Release and Debug |
| 10-minute soak, seed `12345` | 13,838 rounds, zero invariant violations |

The soak processed 276,760,000 events through the multi-producer backpressure scenario with zero
losses, plus 138,380,000 events each through multicast, exception injection, and sharding.
All three multicast handlers agreed; sharding reported zero ordering violations. The exception
scenario deliberately injected 138,051 failures and reported zero halted processors. Final
managed heap usage was 2.4 MB (1.5 MB above baseline), with 44 threads (six below baseline).
The [full soak output](reliability-2026-11-07/soak.txt) includes periodic resource observations.

These four soak scenarios exercise synchronous paths. The soak ran with the batch and drop-policy
fixes before the async cancellation fix was added; async behavior was checked separately by the
tests above. Passing these checks does not establish correctness for every workload.

```bash
dotnet run --project Disruptor.Soak -c Release -- --minutes 10 --seed 12345 --report-seconds 30
```

## Soak

See also the [2026-11-07 reliability check](#2026-11-07-reliability-changes-throughput-check)
for the batch-recovery and drop-policy changes.

`dotnet run --project Disruptor.Soak -c Release -- --minutes 30`

A 2-minute run: **2,480 rounds**, **49.6 M events** through the multi-producer backpressure
scenario alone (plus 24.8 M each through multicast, chaos-exception, and sharding).
Zero invariant violations. Heap flat at 3.2 MB, thread count flat.

## Latency distribution

Latency, not mean throughput, is what a game server dies from. Full run: [`latency.txt`](latency.txt).

### Producer wait under backpressure (ring 1,024, 300,000 publishes, slow consumer)

| Backoff policy | p99.9 | p99.99 | max | **&gt;15 ms** | Throughput |
|---|---:|---:|---:|---:|---:|
| SP / `SpinOnlyBackoff` (default) | 4,784 µs | 8,394 µs | 11.04 ms | **0** | 102,398 ops/s |
| MP / `SpinOnlyBackoff` | 3,353 µs | 10,720 µs | 11.91 ms | **0** | 93,892 ops/s |
| SP / `SleepingBackoff` (low-CPU) | 67 µs | 15,864 µs | 19.94 ms | **290** | 65,861 ops/s |

The `>15 ms` threshold exists because Windows' default timer resolution is 15.6 ms — any wait path
that reaches `Thread.Sleep(1)` costs a full 60 Hz tick. The library had **284 such stalls per
300,000 publishes** before this was fixed; the default path is now **0**.

`SleepingBackoff` reintroduces the cliff **on purpose**: it is the low-CPU option, and it is the
only policy that calls `Thread.Sleep(1)`. Its XML docs carry these exact numbers so the trade-off is
visible at the call site. Never select it for a tick thread.

### Consumer wake-up after idle (20,000 single publishes)

| Wait strategy | p50 | p99 | p99.9 | max | &gt;15 ms |
|---|---:|---:|---:|---:|---:|
| `YieldingWaitStrategy` | **2.4 µs** | 131 µs | 656 µs | 2.03 ms | 0 |
| `BusySpinWaitStrategy` | **2.6 µs** | 27.9 µs | 366 µs | 8.03 ms | 0 |
| `BlockingWaitStrategy` | 19.1 µs | 67.7 µs | 252 µs | 12.67 ms | 0 |
| `TimeoutBlockingWaitStrategy(1 ms)` | 22.8 µs | 123 µs | 381 µs | 1.36 ms | 0 |
| `PhasedBackoffWaitStrategy` | 25.5 µs | 110 µs | 944 µs | 29.88 ms | **5** |

Spin strategies wake ~8× faster than blocking ones (2.4 µs vs 19 µs), which is the quantitative
basis for the guidance in the README: spin on a tick thread, block when oversubscribed or
containerised.

`PhasedBackoffWaitStrategy` shows tail outliers in this specific shape — a completely idle period
followed by a single publish — because it burns its spin budget and then falls back anyway. It is
designed for *intermittent bursts*, not for a cold idle→single-event pattern. Prefer
`TimeoutBlockingWaitStrategy` if your traffic looks like this test.

### Release gate

- `>15 ms` count is **0** in the default (`SpinOnlyBackoff`) configuration
- p99.9 has not regressed against the previous recorded baseline

### Windows idle-tick resolution

`TimeoutBlockingWaitStrategy(1 ms)` fires `OnTimeout` at an actual interval of **15.40 ms** while
completely idle, because `Monitor.Wait` is quantised to the system timer. With
`HighResolutionTimerScope` active it drops to **1.52 ms** — a 10× improvement, measured over 2 s:

| | `OnTimeout` calls / 2 s | Actual interval |
|---|---:|---:|
| Default timer resolution | 130 | 15.40 ms |
| `HighResolutionTimerScope` | 1,314 | **1.52 ms** |

---

## Open item: backpressure-bound throughput

**Status: known gap, not yet explained.**

When the ring is saturated, disruptor-net is 1.3–2.9× faster than us. Because our per-event cost is
*lower* (3.1 ns vs 4.8 ns), the difference has to be in the producer↔consumer handoff, not the
steady-state path.

Ruled out by measurement, in order:

| Hypothesis | Change made | Result |
|---|---|---|
| Interface dispatch on `Next`/`Publish` | Added `SequencerDispatcher` (struct, branches on sealed concrete type) | **Helped at large ring** (2.2× slower → 1.5× faster). No effect at ring 1,024. |
| Producer yields to the kernel too early | Pure-spin phase before `SpinWait` escalation | No measurable change |
| `Thread.SpinWait(N)` with large N | Reduced to a single `PAUSE` per re-check (a `PAUSE` is ~140 cycles on Skylake+, so `SpinWait(256)` is ~10 µs of *not checking*) | No measurable change |
| Default-interface-method dispatch per batch (`OnBatchStart`, `OnEventProcessed`) | Detect non-implemented hooks once at construction and skip the call; only reset the failure streak when a failure actually occurred | No measurable change at ring 1,024 |
| `Thread.Sleep(1)` in the shutdown drain (measured inside the benchmark) | Replaced with `SpinWait.SpinOnce(sleep1Threshold: -1)` | Correct on principle; no measurable change here |
| Clock sampling in the wait loop | `Stopwatch.GetTimestamp()` now sampled 1-in-128, and only when a time-based backoff or stall callback needs it | No measurable change |

A standalone probe (raw `BatchEventProcessor`, no DSL) shows only a ~15% penalty from ring 1,024 vs
65,536 — so the gap is likely somewhere in the DSL/shutdown path or in the specific handoff pattern
the benchmark creates, not in the sequencer core.

Reproduce the comparison with [ComparisonBenchmarks.cs](../../Disruptor.Benchmarks/ComparisonBenchmarks.cs)
and the commands at the top of this guide. The standalone probe source is not included in this
repository; its observation above is historical context, not a reproducible benchmark artifact.
