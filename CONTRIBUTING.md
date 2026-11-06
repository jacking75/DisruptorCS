# Contributing to DisruptorCS

Thanks for considering a contribution. This is a concurrency library, so the bar for changes is
higher than usual in one specific way: **a bug here corrupts data silently rather than throwing.**
Everything below exists because of that.

## The one rule that matters

> Measure before you optimize, and publish the measurement.

Record new performance claims with reproducible commands and raw results in `docs/benchmarks/`.
Historical probe observations without source are context, not a substitute for a new measurement. Two of the biggest optimizations we *didn't* ship were killed
by measurement:

- Struct-generic devirtualization of the handler call — .NET's Dynamic PGO already handles the
  monomorphic call site, so the "optimization" was 3% *slower*.
- Aggressive producer spinning — `Thread.SpinWait(256)` is ~10 µs on Skylake+, which is longer than
  the wait it was supposed to avoid.

If your PR says "this should be faster", please attach the benchmark that says it is.

## Getting started

```bash
git clone https://github.com/jacking75/DisruptorCS
cd DisruptorCS
dotnet build DisruptorCS.slnx -c Release
dotnet test  Disruptor.Tests  -c Release
```

You need the .NET 10 SDK. The library multi-targets `net8.0` and `net10.0`.

## Before you open a PR

```bash
# Warnings are errors — a clean build is a real signal.
dotnet build DisruptorCS.slnx -c Release

# Include optimized builds: JIT optimizations can expose additional memory-ordering defects.
dotnet test Disruptor.Tests -c Release
dotnet test Disruptor.Analyzers.Tests -c Release

# Debug-only diagnostics (single-producer misuse, async handler detection) live here.
dotnet test Disruptor.Tests -c Debug

# Concurrency changes: run the stress suite repeatedly. Once proves nothing.
for i in $(seq 1 20); do
  dotnet test Disruptor.Tests -c Release --no-build --filter "Category=Stress" || break
done

# Touched the wait/publish/gating path? Soak it.
dotnet run --project Disruptor.Soak -c Release -- --minutes 10
```

## What a good PR looks like

**For a bug fix**

1. A failing test *first*, in the suite that should have caught it.
2. The fix.
3. A comment at the fix site explaining the failure mode, not the mechanics. Compare:
   - Poor: `// use while(true) instead of do-while`
   - Good: `// C#'s continue inside do…while jumps to the condition, so the CAS ran even after the
     capacity check failed — backpressure was completely disabled.`

**For a performance change**

1. Benchmark output before and after, in the PR description.
2. The scenario where the gain appears *and* where it doesn't. A change that helps large batches and
   hurts small ones needs to say so.
3. Allocation numbers if the publish path is touched — "zero allocation on the normal path" is a
   guarantee, not an aspiration.

**For a new feature**

1. Why the existing API can't express it.
2. Tests for the failure paths, not just the happy path: exceptions inside the handler, shutdown
   while in flight, backpressure while the feature is active.
3. XML docs on the public surface, written for someone who will misuse it.

## Testing conventions

- `[Trait("Category", "Stress")]` for long/repeated tests, `"Cpu"` for CPU-budget tests.
- **Never** assert on a timeout's side effects without asserting the timeout itself. The original
  benchmark in this repository ignored `allDone.Wait(10s)`'s return value and printed throughput —
  it reported success while losing 99% of events.
- Timing-sensitive tests run with parallelization disabled
  (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`) because
  `Process.TotalProcessorTime` is process-wide.

## Code style

- Comments explain **why**, and specifically what breaks without the code. Skip comments that
  restate the line below them.
- Public API gets XML docs. Prefer documenting the trap over documenting the parameter.
- Korean is fine in internal implementation comments; **public XML docs should be English** so
  IntelliSense works for everyone.
- Keep `Thread.Sleep(1)` out of default spin/backoff paths. Use
  `SpinWait.SpinOnce(sleep1Threshold: -1)` there; explicitly selected sleeping strategies are
  deliberate CPU/latency trade-offs and must document that behavior.

## Adding an analyzer rule

Good first contribution. Pattern:

1. Add the ID to `Disruptor.Analyzers/DiagnosticIds.cs`.
2. Implement the analyzer (see `AsyncHandlerAnalyzer` for the simplest complete example).
3. Add tests in `Disruptor.Analyzers.Tests` covering both the violation and the correct form.
4. Write `docs/analyzers/DISRxxx.md` explaining **why** the pattern is dangerous, with the fix.
5. Add a row to `docs/analyzers/README.md` and the README table.

Candidate rules we'd welcome:

- Detecting `ProducerType.Single` where the publish site is reachable from multiple threads.
- Flagging a `BusySpinWaitStrategy` in a project that also targets a container runtime.

## Reporting bugs

For a concurrency bug, please include:

- Producer type, ring size, wait strategy, handler topology
- Whether it reproduces in Debug, Release, or both (this matters a lot)
- How many runs out of how many
- CPU model and core count

A minimal repro is ideal, but a precise description of the topology is often enough — most defects
of this kind are structural.

## License

Contributions are licensed under MIT, the same as the project.
