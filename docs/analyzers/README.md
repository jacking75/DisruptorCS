# DisruptorCS Analyzers

The `DisruptorCS` package ships Roslyn analyzers. They exist because **the dangerous ways to misuse
this library all compile cleanly and then corrupt data at runtime** — a class of bug that
documentation alone does not prevent.

No configuration is needed: installing the package activates them.

| ID | Rule | Default severity | Category |
|---|---|---|---|
| [DISR001](DISR001.md) | Synchronous Disruptor handlers must not be `async` | **Error** | Correctness |
| [DISR002](DISR002.md) | `Publish()` must run in a `finally` after `Next()` | Warning | Correctness |
| [DISR003](DISR003.md) | Don't pass capturing lambdas to `PublishEvent` | Info | Performance |
| [DISR004](DISR004.md) | Ring buffer size must be a power of two | **Error** | Correctness |
| [DISR005](DISR005.md) | Don't let batch span slot references escape | Warning | Correctness |

## Suppressing a rule

Per-line:

```csharp
#pragma warning disable DISR003 // this publish is not on a hot path
ring.PublishEvent((evt, seq) => evt.Id = capturedId);
#pragma warning restore DISR003
```

Project-wide, via `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.DISR003.severity = none
```

> DISR001 and DISR004 are errors on purpose. If you find yourself suppressing either one,
> please open an issue — it likely means we've mis-specified the rule.
