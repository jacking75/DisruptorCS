# Release checklist

This repository does **not** use CI. Every gate below is run manually, which makes writing them
down non-optional: an unwritten manual step is a step that eventually gets skipped.

Versioning is driven by [MinVer](https://github.com/adamralph/minver) from git tags —
tag `v1.2.3` produces package version `1.2.3`. Untagged builds get a `-preview.0` suffix.

---

## 1. Correctness gates

```bash
# Warnings are errors, so a clean build is a real signal.
dotnet build DisruptorCS.slnx -c Release

# Memory-ordering defects only surface with optimizations on.
dotnet test Disruptor.Tests -c Release

# Debug-only diagnostics (single-producer misuse, async handler detection) run only here.
dotnet test Disruptor.Tests -c Debug

# Analyzer behaviour.
dotnet test Disruptor.Analyzers.Tests -c Release
```

- [ ] Release build: **0 warnings, 0 errors**
- [ ] `Disruptor.Tests` Release: all pass
- [ ] `Disruptor.Tests` Debug: all pass
- [ ] `Disruptor.Analyzers.Tests`: all pass

## 2. Race-condition gate

A concurrency bug does not show up in one run. Repeat the stress suite:

```bash
for i in $(seq 1 20); do
  dotnet test Disruptor.Tests -c Release --no-build --filter "Category=Stress" || break
done
```

- [ ] 20/20 iterations pass

## 3. Soak gate

Catches what short tests cannot: slow leaks, rare interleavings, behaviour once state has grown.

```bash
dotnet run --project Disruptor.Soak -c Release -- --minutes 30
```

- [ ] Exit code 0 (no invariant violations, no resource-leak trip)
- [ ] Heap and thread count flat across reports

For a release after significant concurrency changes, run `--hours 8` overnight instead.

## 4. End-to-end gate

```bash
dotnet run --project Example -c Release | tee /tmp/example-output.txt
grep -E "유실 발생|결함 회귀|불일치|순서 위반" /tmp/example-output.txt && echo "FAILED" || echo "OK"
```

- [ ] No loss/regression strings in the output
- [ ] Sample projects under `samples/` still run

## 5. Performance gate

```bash
# Latency distribution — the number that matters for a game server.
dotnet run --project Disruptor.Benchmarks -c Release -- latency \
  | tee docs/benchmarks/latency.txt

# Throughput comparison against disruptor-net and Channel<T>.
dotnet run --project Disruptor.Benchmarks -c Release -- --filter '*Comparison*' \
  --exporters GitHub --artifacts docs/benchmarks/raw
```

- [ ] `>15 ms` count is **0** in the default (`SpinOnlyBackoff`) configuration
- [ ] p99.9 has not regressed against the previous baseline in `docs/benchmarks/`
- [ ] Comparison table refreshed in `docs/benchmarks/README.md` — **including results we lose**

> The release gate is p99.9 and the `>15 ms` count, not mean throughput. A game server dies from
> tail latency, not from averages.

## 6. Package gate

```bash
dotnet pack Disruptor/Disruptor.csproj -c Release -o ./artifacts
dotnet pack Disruptor.Extensions.Hosting/Disruptor.Extensions.Hosting.csproj -c Release -o ./artifacts

# Verify the analyzer actually shipped inside the package.
unzip -l artifacts/DisruptorCS.*.nupkg | grep analyzers
```

- [ ] `analyzers/dotnet/cs/Disruptor.Analyzers.dll` present in the package
- [ ] `README.md` present in the package
- [ ] `.snupkg` symbol packages produced

## 7. Documentation gate

- [ ] `CHANGELOG.md` updated (Keep a Changelog format)
- [ ] `README.md` benchmark numbers match the run from step 5
- [ ] `README.ko.md` kept in sync with `README.md`
- [ ] New public API has XML docs

## 8. Ship

```bash
git tag v0.3.0
git push origin main --tags

dotnet nuget push artifacts/DisruptorCS.0.3.0.nupkg \
  --source https://api.nuget.org/v3/index.json --api-key $NUGET_API_KEY
dotnet nuget push artifacts/DisruptorCS.Extensions.Hosting.0.3.0.nupkg \
  --source https://api.nuget.org/v3/index.json --api-key $NUGET_API_KEY
```

- [ ] Tag pushed
- [ ] Packages pushed
- [ ] GitHub Release created from the tag, body excerpted from `CHANGELOG.md`

---

## Rollback

NuGet packages cannot be deleted, only unlisted:

```bash
dotnet nuget delete DisruptorCS 0.3.0 --source https://api.nuget.org/v3/index.json --api-key $KEY --non-interactive
```

Then ship a fixed patch version immediately. Never reuse a version number.
