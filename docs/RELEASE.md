# Release checklist

This repository does **not** use CI. Every gate below is run manually, which makes writing them
down non-optional: an unwritten manual step is a step that eventually gets skipped.

Versioning is driven by [MinVer](https://github.com/adamralph/minver) from git tags —
tag `v1.2.3` produces package version `1.2.3`. Untagged builds fall back to the
`MinVerMinimumMajorMinor` floor in `Directory.Build.props` and get a `-preview.0.N` suffix, so
they can never be mistaken for a release. Bump that floor in the same commit that opens a new
minor version.

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

Packaging is scripted so the easy-to-forget checks cannot be forgotten. The script builds,
packs both projects, and then opens each `.nupkg` to confirm the analyzer, the package README,
the icon, both target frameworks, the XML docs, and the symbol package are actually inside.

```powershell
# 태그를 찍기 전 예행 연습 (버전을 강제)
./build/pack.ps1 -Version 0.10.0

# 태그를 찍은 뒤 실제 산출물 (MinVer 가 git 태그에서 버전을 읽는다)
./build/pack.ps1
```

- [ ] `build/pack.ps1` exits 0 — every `[OK]`, no `[누락]`
- [ ] The version on the produced files is the version you intend to ship

Version comes from MinVer: the tag `v0.10.0` produces `0.10.0`. Without a tag, the
`MinVerMinimumMajorMinor` floor in `Directory.Build.props` produces `0.10.0-preview.0.N`, so an
accidental untagged pack is obviously a pre-release rather than silently a wrong version.

## 7. Consumer gate

A package that builds is not the same as a package that works once installed. Install it from a
local feed into a throwaway project — this is what catches a missing dependency, an analyzer that
does not load, or a documented snippet that does not compile.

```powershell
$feed = (Resolve-Path ./artifacts).Path
$scratch = Join-Path $env:TEMP 'disruptorcs-consumer'
Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
dotnet new console -o $scratch
Push-Location $scratch
dotnet nuget add source $feed --name local
dotnet add package DisruptorCS --version 0.10.0
dotnet add package DisruptorCS.Extensions.Hosting --version 0.10.0
```

- [ ] A quickstart snippet copied from `Disruptor/PACKAGE.md` compiles and runs
- [ ] A deliberate ring size of `1000` produces **DISR004** — the analyzer shipped and loads
- [ ] `services.AddDisruptor<T>(...)` resolves **without** an extra `using` — the extension lives
      in `Microsoft.Extensions.DependencyInjection`

## 8. Documentation gate

- [ ] `CHANGELOG.md` updated (Keep a Changelog format) and the `[Unreleased]` section cut to the
      new version, with the compare/tag links at the bottom updated
- [ ] `README.md` benchmark numbers match the run from step 5
- [ ] `README.ko.md` kept in sync with `README.md`
- [ ] `Disruptor/PACKAGE.md` and `Disruptor.Extensions.Hosting/PACKAGE.md` reflect the new
      version's API — these are what nuget.org renders, and **relative links do not work there**,
      so every link in them must be absolute
- [ ] New public API has XML docs

## 9. Ship

```powershell
git tag v0.10.0
git push origin main --tags

./build/pack.ps1                 # 태그가 찍힌 상태에서 다시 만든다

dotnet nuget push artifacts/DisruptorCS.0.10.0.nupkg `
  --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
dotnet nuget push artifacts/DisruptorCS.Extensions.Hosting.0.10.0.nupkg `
  --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
```

Push the core package first and let it finish indexing. `DisruptorCS.Extensions.Hosting` depends on
the exact same version, so pushing it first leaves a package on nuget.org that cannot be restored.

`.snupkg` symbol packages are picked up automatically when they sit next to the `.nupkg`, so there
is no separate push for them.

- [ ] Tag pushed
- [ ] `DisruptorCS` pushed and indexed (visible at <https://www.nuget.org/packages/DisruptorCS/>)
- [ ] `DisruptorCS.Extensions.Hosting` pushed
- [ ] Both listing pages render the README and the icon
- [ ] GitHub Release created from the tag, body excerpted from `CHANGELOG.md`

---

## Rollback

NuGet packages cannot be deleted, only unlisted:

```powershell
dotnet nuget delete DisruptorCS 0.10.0 --source https://api.nuget.org/v3/index.json `
  --api-key $env:NUGET_API_KEY --non-interactive
```

Then ship a fixed patch version immediately. Never reuse a version number — nuget.org rejects a
re-push of an existing version, and any client that already restored the bad one has it cached.
