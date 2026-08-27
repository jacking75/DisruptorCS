# Benchmarks

All numbers here were produced on:

| | |
|---|---|
| CPU | Intel Core i7-12700KF (12 physical / 20 logical) |
| RAM | 64 GB |
| OS | Windows 11 (10.0.26200) |
| Runtime | .NET 10.0.400, Release, x64, Server GC |
| Harness | BenchmarkDotNet 0.14.0, `RunStrategy.Monitoring` |
| Compared against | [disruptor-net](https://www.nuget.org/packages/Disruptor) 6.0.1, `System.Threading.Channels` |

Reproduce:

```bash
dotnet run --project Disruptor.Benchmarks -c Release -- --filter '*Comparison*' \
  --exporters GitHub --artifacts docs/benchmarks/raw
dotnet run --project Disruptor.Benchmarks -c Release -- latency
```

Raw exports live in [`raw/`](raw/); the latency run is in [`latency.txt`](latency.txt).

---

## Summary, including where we lose

**We publish the comparisons we lose.** Showing only favourable conditions is the fastest way to
lose a reader's trust, and the shape of the loss is itself useful information.

| Condition | Winner | Margin |
|---|---|---|
| Large ring — backpressure is rare | **DisruptorCS** | 1.5× faster than disruptor-net |
| Small ring — backpressure on nearly every publish | **disruptor-net** | 2.9× faster than DisruptorCS |
| Multi-producer, small ring | **disruptor-net** | 1.3× (2 producers) – 1.8× (8 producers) |
| vs `Channel<T>` (any condition) | **DisruptorCS** | 8–38× faster, 25–1,360× less allocation |

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

`Channel<T>` allocates an object per event, which is where the 1,360× allocation ratio comes from.
It is not a defect of `Channel<T>` — it is the design difference that makes a ring buffer a ring
buffer.

## Multi-producer: N producers → 1 consumer, 200,000 events each, ring 1,024

| Library | Producers | Mean | Allocated |
|---|---:|---:|---:|
| DisruptorCS | 2 | 28.99 ms | 50 KB |
| **disruptor-net** | 2 | **21.78 ms** | 53 KB |
| DisruptorCS | 8 | 90.52 ms | 51 KB |
| **disruptor-net** | 8 | **49.13 ms** | 54 KB |

Same story: ring 1,024 means constant backpressure. Correctness is unaffected — the integrity
suite runs the identical configuration and confirms **0 lost events, 0 slot overwrites**.

## Single-producer throughput (Example scenario 12)

10,000,000 events, ring 65,536, `YieldingWaitStrategy`, trivial handler:

| Metric | Value |
|---|---|
| Throughput | **249.8 M ops/s** |
| Latency | **4.0 ns/op** |
| Allocated | **0 bytes** (0.0000 B/event) |

Reproduce with `dotnet run --project Example -c Release`.

## Game-server scenarios (Example)

| Scenario | Result |
|---|---|
| Multi-producer integrity (8P × 100K, ring 1,024) | 0 lost, 0 overwrites, 16.7 M ops/s |
| Room sharding (5,000 rooms × 40 events) | 200,000 events / 31 ms / **8 threads**, per-room order preserved |
| Journal → replay (20,000 events) | State checksum identical |
| Batch handler (200,000 events, ring 4,096) | 977 `OnBatch` calls instead of 200,000 per-event calls |
| EventPoller (100,000 events) | 391 frames, **0 extra threads**, checksum matches |
| Blocking strategy + slow upstream handler | 0.07 cores (no hot-spin) |

## Soak

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

If you want to pick this up, `IMPROVEMENTS.md` §2 has the probe methodology, and
`Disruptor.Benchmarks/ComparisonBenchmarks.cs` is the reproduction. Please bring a measurement.
