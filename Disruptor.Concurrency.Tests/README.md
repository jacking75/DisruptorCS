# Disruptor.Concurrency.Tests

Systematic concurrency exploration with [Microsoft Coyote](https://github.com/microsoft/coyote).

```bash
dotnet tool restore
dotnet build  Disruptor.Concurrency.Tests -c Release
dotnet coyote rewrite Disruptor.Concurrency.Tests/rewrite.coyote.json
dotnet Disruptor.Concurrency.Tests/bin/Release/net8.0/rewritten/Disruptor.Concurrency.Tests.dll -i 1000
```

See [docs/concurrency-testing.md](../docs/concurrency-testing.md) for what is modelled, the recorded historical
run, and the two limits that matter when quoting the results.

Targets `net8.0` because that is the highest framework Coyote 1.7 ships assets for. The models are
standalone by design — they do not reference `Disruptor`, so the state space is small enough for bounded schedule exploration. A passing run is not an
exhaustive proof; the runner also limits scheduling steps.
