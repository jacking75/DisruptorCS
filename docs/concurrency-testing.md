# Systematic concurrency testing

Stress tests hit an interleaving *if you get lucky*. [Microsoft Coyote](https://github.com/microsoft/coyote)
takes control of the scheduler and walks interleavings **systematically**, so a low-probability race
shows up as a reproducible trace instead of a flake that happens once a month in production.

`Disruptor.Concurrency.Tests` contains reduced models of the three concurrency-critical parts of
this library, plus a **negative control** that proves the exploration actually has teeth.

## Running it

```bash
dotnet tool restore                                                  # once — installs the coyote CLI
dotnet build  Disruptor.Concurrency.Tests -c Release
dotnet coyote rewrite Disruptor.Concurrency.Tests/rewrite.coyote.json
dotnet Disruptor.Concurrency.Tests/bin/Release/net8.0/rewritten/Disruptor.Concurrency.Tests.dll -i 1000
```

The rewrite step is not optional. Coyote intercepts concurrency by rewriting IL; running the
un-rewritten assembly reports uncontrolled concurrency instead of exploring it.

Options: `-i <n>` iterations per model (default 1,000), `-m <substring>` to run one model.

Exit code is `0` only if every model reaches its expected verdict — including the negative control,
which must **fail**.

## What is modelled

| Model | Mirrors | Property under test |
|---|---|---|
| `SignalNeverLosesWakeup` | `WaitStrategy/BlockingWaitStrategy.cs` | A consumer waiting on a sequence is always woken once it is published |
| `SignalWakesEveryWaiter` | same | `PulseAll` reaches every waiter, not just one |
| `BuggyOrderLosesWakeup_ExpectedToFail` | deliberately inverted | **Negative control** — the exploration must find this |
| `TwoWorkersClaimEachSequenceExactlyOnce` | `Work/WorkProcessor.cs` | Every sequence is claimed exactly once — no duplicate, no loss |
| `ThreeWorkersClaimEachSequenceExactlyOnce` | same | Holds with more contenders |
| `HaltIsNeverLost` | `BatchEventProcessor.Run` | `Halt` racing `Run` always terminates the loop; the start gate is signalled exactly once |
| `DoubleRunStillReleasesTheGate` | same | A rejected second `Run` still releases the gate, so `Start()` cannot hang |
| `HaltRacingDisposeDoesNotThrow` | same | Shutdown paths don't leak `ObjectDisposedException` |

### Latest run

i7-12700KF / Windows 11 / .NET 10, 1,000 iterations per model:

```
OK                                             SignalNeverLosesWakeup            반복 1,000 · 버그 0
OK                                             SignalWakesEveryWaiter            반복 1,000 · 버그 0
OK   (예상대로 버그 발견 — 탐색이 살아 있음)    BuggyOrderLosesWakeup_ExpectedToFail  반복 39 · 버그 1
OK                                             TwoWorkersClaim...ExactlyOnce     반복 1,000 · 버그 0
OK                                             ThreeWorkersClaim...ExactlyOnce   반복 1,000 · 버그 0
OK                                             HaltIsNeverLost                   반복 1,000 · 버그 0
OK                                             DoubleRunStillReleasesTheGate     반복 1,000 · 버그 0
OK                                             HaltRacingDisposeDoesNotThrow     반복 1,000 · 버그 0
```

The control found its deadlock on iteration 39. That number is the reason to trust the other seven
lines: the same exploration that found a real lost wakeup in under 40 schedules found nothing in
1,000 schedules of the shipped design.

## Two limits you should know before quoting these results

**1. These are models, not the production code.** They were transcribed by hand from the files in
the table above and deliberately reduced — the wait strategy model keeps only the cursor, the waiter
count, and the gate. If you change the original, you must change the model. A green run says the
*design* survives the interleavings; it does not say the shipped file compiles to that design.

The models also **remove the safety nets on purpose**. `BlockingWaitStrategy` re-checks its
condition every 100 ms, so a genuine lost wakeup would show up in production as a 100 ms stall
rather than a hang. `Monitor.Wait` in the model has no timeout, precisely so that the bug surfaces
as a deadlock the explorer can report.

**2. Coyote explores schedules, not memory ordering.** It does not model store-load reordering, so
it cannot validate the `Interlocked.MemoryBarrier()` argument in
[IMPROVEMENTS.md §2.3](../IMPROVEMENTS.md). Those are complementary techniques: Coyote covers
*"which thread runs next"*, the fence argument covers *"which write is visible"*. Neither replaces
the other, and neither replaces the soak harness in `Disruptor.Soak`.

## Adding a model

1. Write a plain class that mirrors the concurrent structure — no project reference to `Disruptor`;
   the point is a small state space, not real code.
2. Assert invariants with `Microsoft.Coyote.Specifications.Specification.Assert`.
3. Add a `[Microsoft.Coyote.SystematicTesting.Test]` entry point that spawns the tasks.
4. Register it in the table at the top of `Program.cs`, with `expectBug: false`.

If your model passes on the first try, invert one ordering and confirm it fails. A model that cannot
fail is testing nothing.
