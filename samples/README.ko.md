# 샘플 안내

> [English](README.md) · **한국어**

실행 가능한 사용 예제는 모두 `samples`에서 찾을 수 있다. **기초 예제 → 응용 예제** 순서로
학습한다. 성능 측정은 `Disruptor.Benchmarks`, 정확성 검증은 `Disruptor.Tests`가 담당한다.
아래 명령은 .NET 10 SDK를 사용해 저장소 루트에서 실행한다.
소유권·예외 처리·종료를 적용할 때는 [처리 동작](../docs/processing-semantics.ko.md)도 확인한다.

## 기초 예제

[UsageRecipes 활용 예제 13선](UsageRecipes/README.ko.md)은 예제마다 하나의 소스 파일을 제공하고,
이름으로 골라 실행할 수 있다. 외부 서비스 없이 실행되며 각 결과를 자동 검증한다.

```bash
dotnet run --project samples/UsageRecipes -c Release -- --list
dotnet run --project samples/UsageRecipes -c Release -- publish
dotnet run --project samples/UsageRecipes -c Release -- all
```

먼저 `publish → pipeline → multi-producer → backpressure` 순서로 게시·의존 관계·동시성·포화를
익힌다. 이후 목적에 맞게 선택한다.

| 목적 | 예제 이름 |
|---|---|
| 배치 집계와 구조체 이벤트 | `batch`, `value` |
| 기존 게임 루프의 처리 예산 유지 | `poller` |
| 전처리 후 독립 작업 분배 | `workers` |
| 비동기 저장과 관리자 스트림 | `async-batch`, `snapshots` |
| 버퍼 소유권과 실패 입력 처리 | `pooled-payload`, `errors` |
| 명령 기록 후 상태 변경 및 재생 | `journal` |

## 응용 예제

기초 API를 익힌 뒤 여러 기능을 함께 사용하는 구조를 살펴본다.

| 프로젝트 / 소스 | 학습할 내용 | 실행 방식 |
|---|---|---|
| [GameLoopPoller](GameLoopPoller/Program.cs) | 기존 루프에서 폴링, 틱 진행, 프레임 시간 분포 | 콘솔 실행 후 종료 |
| [ChatBroadcast](ChatBroadcast/Program.cs) | 이벤트마다 flush하는 경우와 배치 끝에서 flush하는 경우 비교 | 콘솔 실행 후 종료 |
| [MatchSimulation](MatchSimulation/Program.cs) | 방 10,000개 샤딩, 고정 틱, 파일 저널, 방별 재생 상태 비교 | 임시 저널 파일을 사용하는 콘솔 |
| [ReplayInspector](ReplayInspector/Program.cs) | 저널 검증·덤프·시퀀스별 재생·비교와 손상 처리 | 인자 없이 실행하면 데모 |
| [AspNetCoreTelemetry](AspNetCoreTelemetry/Program.cs) | DI·수명주기·헬스체크·메트릭·스톨 진단 | HTTP 서버, 종료는 Ctrl+C |

```bash
dotnet run --project samples/GameLoopPoller -c Release
dotnet run --project samples/ChatBroadcast -c Release
dotnet run --project samples/MatchSimulation -c Release
dotnet run --project samples/ReplayInspector -c Release
dotnet run --project samples/AspNetCoreTelemetry -c Release
```

웹 샘플에서는 `/commands`로 게시하고 `/health`, `/metrics`, `/pipeline`으로 상태를 본다.
`/commands`는 최대 50ms의 동기 용량 재시도를 사용하므로 요청 스레드를 점유한다. 게시하면 202,
시간 초과 시 503을 반환하며 202는 처리 완료를 뜻하지 않는다. 샘플의 헬스 임계값은 사용률
50% Degraded, 85% Unhealthy다. Healthy·Degraded는 HTTP 200, Unhealthy는 503을 반환한다.
ChatBroadcast는 flush 횟수를 모사하며 실제 소켓 syscall을 측정하는 샘플은 아니다.
`/throttle`과 `/load`로 느린 소비자와 부하를 재현할 수 있다. 세부 명령과 엔드포인트는
[영문 샘플 안내](README.md#aspnetcoretelemetry)에 있다.

## 기존 통합 예제의 새 위치

최상위 `Example` 프로젝트를 제거하고 학습 내용을 다음 위치로 통합했다.
기존 시나리오 번호로 읽던 사람도 대응되는 코드를 찾을 수 있다.

| 기존 시나리오 | 현재 위치 |
|---|---|
| 1. 기본 게시 | [publish](UsageRecipes/Publication.cs) |
| 2. 의존성 그래프와 `And` | [pipeline](UsageRecipes/OrderPipeline.cs) |
| 3. 배치 핸들러 | [batch](UsageRecipes/BatchAggregation.cs) |
| 4. EventPoller | [poller](UsageRecipes/FramePolling.cs) |
| 5. 백프레셔 정책 | [backpressure](UsageRecipes/Backpressure.cs) |
| 6. 고정 틱과 유휴 타임아웃 | [GameLoopPoller](GameLoopPoller/Program.cs), [MatchSimulation](MatchSimulation/Program.cs) |
| 7. 저널 기록과 재생 | [journal](UsageRecipes/JournalReplay.cs) |
| 8. 저널 무결성 | [ReplayInspector](ReplayInspector/Demo.cs) |
| 9. 방 샤딩 | [MatchSimulation](MatchSimulation/Program.cs) |
| 10. 로직 이후 워커 분배 | [workers](UsageRecipes/WorkerJobs.cs) |
| 11. 스톨 진단 | [AspNetCoreTelemetry](AspNetCoreTelemetry/Program.cs)의 `OnStall`, `/throttle`, `/load` |
| 12. 처리량 측정 | [UnicastBenchmarks](../Disruptor.Benchmarks/ThroughputBenchmarks.cs) |
| 13. 다중 생산자 무결성 스트레스 | [LosslessStressTests](../Disruptor.Tests/LosslessStressTests.cs), 입문용 [multi-producer](UsageRecipes/MultiProducer.cs) |

성능 측정과 스트레스 검증은 기존 전용 프로젝트에 대응 구현이 있어 중복 코드를 옮기지 않았다.
기존 통합 데모와 벤치마크의 측정 조건은 다르므로 수치를 비교할 때는
[벤치마크 문서](../docs/benchmarks/README.md)를 함께 확인한다.

```bash
dotnet run --project Disruptor.Benchmarks -c Release -- --filter '*UnicastBenchmarks*'
dotnet test Disruptor.Tests -c Release --filter 'FullyQualifiedName~LosslessStressTests'
```
