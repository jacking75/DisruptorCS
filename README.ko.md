# DisruptorCS (한국어)

> [English](README.md) · **한국어**

**LMAX Disruptor의 C# 구현체 + 게임 서버 레이어**: 룸 샤딩 · 고정 틱 통합 · 결정론적 저널/리플레이,
그리고 실제 서버에 필요한 운영 도구(백프레셔 정책 · 스톨 진단 · 컴파일 타임 오용 검출)까지.

[![NuGet](https://img.shields.io/nuget/v/DisruptorCS.svg)](https://www.nuget.org/packages/DisruptorCS/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)

**250M ops/s · 4 ns/op · 할당 0 bytes** (1,000만 이벤트, 단일 프로듀서, Release, i7-12700KF).
백프레셔 포화 상태(8 프로듀서 → 1024 슬롯 링)에서 **유실 0**.

```bash
dotnet add package DisruptorCS
```

> 이 문서는 [영어판 README](README.md) 의 한국어 번역본입니다. 최신 내용과 전체 구성은 영어판을 참조하세요.
> 아래는 이 라이브러리의 설계 근거와 게임 서버 적용 지침을 한국어로 정리한 것입니다.

## 이번 릴리스의 주요 추가

| 기능 | 설명 |
|---|---|
| `EventPoller<T>` | **전용 스레드 없이** 기존 게임 루프에서 소비. Unity `Update()` · 자체 프레임 루프에 그대로 통합 |
| `IBatchEventHandler<T>` | 이벤트 구간을 `ReadOnlySpan<T>` 로 통째로 수신 (SIMD·일괄 직렬화 가능) |
| Roslyn Analyzer | DISR001~005 — async 핸들러 · finally 없는 Publish · 캡처 람다 · 2의 거듭제곱 아닌 링 크기 · **배치 span 슬롯 참조 유출**을 컴파일 타임에 차단 |
| `DisruptorCS.Extensions.Hosting` | `services.AddDisruptor<T>()` · DI 핸들러 해석 · Generic Host 수명주기 · 헬스체크 |
| `HighResolutionTimerScope` | 유휴 틱 정밀도 **15.40ms → 1.52ms** (실측) |
| 저널 무결성 v2 | XxHash3 체크섬 · 길이 상한 · 시퀀스 단조 검증 · 손상 복구 모드 |
| `AsyncWaitStrategy` | `ValueTask` 기반 소비 (느린 팬아웃 단계용) |
| `PhasedBackoff` · `SleepingWaitStrategy` | 대기 전략 8종 |

문서: [`docs/analyzers/`](docs/analyzers/) · [`docs/benchmarks/`](docs/benchmarks/) ·
[`docs/RELEASE.md`](docs/RELEASE.md) · [`CHANGELOG.md`](CHANGELOG.md) · [`CONTRIBUTING.md`](CONTRIBUTING.md)

---

`net8.0` · `net10.0` 멀티 타깃 · AOT 호환 · 정상 경로 할당 0

---

## 목차

- [빠른 시작](#빠른-시작)
- [무엇이 들어 있나](#무엇이-들어-있나)
- [아키텍처](#아키텍처)
- [게임 서버 기능](#게임-서버-기능)
- [핵심 설계 원칙](#핵심-설계-원칙)
- [.NET 기본 도구 vs Disruptor: 언제 무엇을 쓸 것인가](#net-기본-도구-vs-disruptor-언제-무엇을-쓸-것인가)
- [프로젝트 구조](#프로젝트-구조)
- [측정 결과](#측정-결과)
- [주의사항](#주의사항)

---

## 빠른 시작

```csharp
using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;

public sealed class OrderEvent
{
    public long OrderId;
    public int  Quantity;
}

public sealed class LogicHandler : IEventHandler<OrderEvent>
{
    public void OnEvent(OrderEvent e, long sequence, bool endOfBatch) { /* ... */ }

    // 배치 끝에서만 flush → 1,000건 처리 시 syscall 1,000회 → 1회
    public void OnBatchStart(long batchSize) { }

    // 이벤트가 없어도 주기적으로 호출 (틱 진행 / 송신 플러시 / 하트비트)
    public void OnTimeout(long lastProcessedSequence) { }
}

using var disruptor = new Disruptor<OrderEvent>(
    eventFactory: () => new OrderEvent(),
    ringBufferSize: 1024,
    producerType: ProducerType.Multi,
    waitStrategy: new TimeoutBlockingWaitStrategy(TimeSpan.FromMilliseconds(1)),
    options: new DisruptorOptions
    {
        Name = "packet-pipeline",
        EnableMetrics = true,
        OnStall = info => logger.Warn(info.ToString()),   // 병목 핸들러를 즉시 지목
    });

disruptor.HandleEventsWith(journal, antiCheat)   // 병렬
         .Then(logic);                            // 위 둘이 끝난 뒤에만 실행

var ring = disruptor.Start();

long seq = ring.Next();
try
{
    var e = ring[seq];       // 미리 할당된 슬롯 — 새 객체를 만들지 않는다
    e.OrderId = 1;
    e.Quantity = 100;
}
finally
{
    ring.Publish(seq);       // finally 필수 — 빠뜨리면 링 버퍼가 영구히 막힌다
}

disruptor.Shutdown();        // 미처리 이벤트를 드레인한 뒤 종료
```

`try/finally`를 잊는 사고가 걱정되면 스코프 패턴을 쓰세요.

```csharp
using (var scope = ring.PublishEventScope())
{
    scope.Event.OrderId = 1;
}   // Dispose 에서 자동 Publish
```

전체 예제는 [`Example/Program.cs`](Example/Program.cs)에 10개 시나리오로 있습니다.

```bash
dotnet run --project Example -c Release
```

---

## 무엇이 들어 있나

### 코어

| 구성 요소 | 설명 |
|---|---|
| `RingBuffer<T>` | 참조 타입 이벤트를 사전 할당하는 링 버퍼 |
| `ValueRingBuffer<T>` | **값 타입** 이벤트를 배열에 인라인 저장 (`ref T` 반환, GC 스캔 대상 제외) |
| `SingleProducerSequencer` | CAS 없는 단일 프로듀서 시퀀서 |
| `MultiProducerSequencer` | CAS 기반 다중 프로듀서 시퀀서 |
| `DependentSequenceGroup` | 대기 전략이 "커서" 대신 "의존 대상"을 보게 하는 1급 개념 |
| `SequenceWaitResult` | 예외 대신 값으로 표현하는 대기 결과 (Timeout / Cancelled / 시퀀스) |
| `BatchEventProcessor<T>` | 배치 프로세서 (`OnBatchStart` / `OnTimeout` / 예외 정책 / 슬롯 정리) |
| `WorkerPool<T>` | 이벤트를 **분배**하는 워커 풀 (순서 무관 + 느린 I/O 용) |
| `PublishGate<T>` | 백프레셔 정책(Block / DropWhenCongested / FailAfterTimeout) |
| `DisruptorMetrics` | `System.Diagnostics.Metrics` 기반 지표 (OpenTelemetry 호환) |

### 대기 전략

| 전략 | 지연 | CPU | 용도 |
|---|---|---|---|
| `BusySpinWaitStrategy` | 최저 | 100% | 전용 코어를 줄 수 있는 베어메탈 |
| `YieldingWaitStrategy` | 매우 낮음 | 높음 | 핸들러 스레드 < 논리 코어 |
| `SpinWaitWaitStrategy` | 낮음 | 중간 | 범용 스핀 |
| `SleepingWaitStrategy` | 중간 | 낮음 | 배경 파이프라인 |
| `BlockingWaitStrategy` | ~12μs | 최저 | 초과 구독 상태 |
| **`TimeoutBlockingWaitStrategy`** | ~12μs | 최저 | **게임 서버 기본값** — `OnTimeout` 훅 제공 |
| `PhasedBackoffWaitStrategy` | 낮음→최저 | 적응형 | 짧은 대기는 스핀, 길어지면 블로킹 |

### 게임 서버 레이어 (`Disruptor.Game`)

| 구성 요소 | 해결하는 문제 |
|---|---|
| `ShardedDisruptor<T>` | 룸당 Disruptor를 만들면 10,000룸 = 스레드 10,000개. 샤딩하면 **스레드 수가 룸 수와 무관** |
| `FixedTickDriver` | 이벤트 소비와 고정 스텝 시뮬레이션을 한 스레드에서 (death spiral 방지 포함) |
| `JournalWriter` / `JournalHandler<T>` / `JournalReader` | 시퀀스 번호 = 전역 순서 → 버그 재현 / 핫 스탠바이 복제 / 치팅 검증 / 크래시 복구 |

---

## 아키텍처

```
┌──────────────────────────────────────────────────────┐
│                    PRODUCER(s)                        │
│     Next() → Write Event → Publish()                 │
└──────────────┬───────────────────────────────────────┘
               │
               ▼
┌──────────────────────────────────────────────────────┐
│              RING BUFFER (pre-allocated T[])          │
│  ┌───┬───┬───┬───┬───┬───┬───┬───┐                  │
│  │ 0 │ 1 │ 2 │ 3 │ 4 │ 5 │ 6 │ 7 │  (size=2^n)     │
│  └───┴───┴───┴───┴───┴───┴───┴───┘                  │
│         ↑ Cursor (Sequencer)                         │
└──────┬──────────┬──────────┬─────────────────────────┘
       │          │          │
       ▼          ▼          ▼
┌──────────┐ ┌──────────┐ ┌──────────────────┐
│ Consumer │ │ Consumer │ │   Consumer C      │
│    A     │ │    B     │ │ (depends on A, B) │
│(Journal) │ │(Replica) │ │ (BusinessLogic)   │
└────┬─────┘ └────┬─────┘ └────────┬─────────┘
     │            │                 │
     └────────────┴────┬────────────┘
                       │
              SequenceBarrier
      (WaitFor → DependentSequenceGroup → WaitStrategy)
```

---

## 게임 서버 기능

### 1. 틱 루프 통합 — 별도 타이머 스레드가 필요 없다

```csharp
public sealed class RoomHandler : IEventHandler<GameCommand>
{
    private readonly FixedTickDriver _driver;
    private readonly List<GameCommand> _pending = new(256);

    public RoomHandler() => _driver = FixedTickDriver.At60Hz(tick => Simulate(tick));

    public void OnEvent(GameCommand cmd, long seq, bool endOfBatch)
    {
        _pending.Add(cmd.Snapshot());        // 틱 버킷에 모은다 → 틱 내 결정론
        if (endOfBatch) _driver.TryAdvance();
    }

    // 이벤트가 없어도 호출된다 (TimeoutBlockingWaitStrategy)
    public void OnTimeout(long lastSeq) => _driver.TryAdvance();
}
```

### 2. 백프레셔 정책 — 게임 서버는 "버리는 것"도 정책이다

```csharp
var gate = new PublishGate<GameCommand>(ring, disruptor.Metrics);

// 이동 패킷 — 최신값만 의미 있으므로 혼잡하면 버린다
gate.TryPublish(BackpressurePolicy.DropWhenCongested, (e, s) => Fill(e, move));

// 재화 변동 — 절대 버릴 수 없으므로 타임아웃 후 거절해 오류 응답
if (!gate.TryPublish(BackpressurePolicy.FailAfterTimeout, (e, s) => Fill(e, purchase),
                     timeout: TimeSpan.FromMilliseconds(50)))
    return Error.ServerBusy;
```

### 3. 룸 샤딩 — 10,000룸을 8스레드로

```csharp
using var sharded = new ShardedDisruptor<GameCommand>(
    shardCount: 8,                 // 2의 거듭제곱
    ringSizePerShard: 4096,
    eventFactory: () => new GameCommand(),
    handlerFactory: i => new RoomShardHandler(i));

sharded.Start();

// 같은 룸은 항상 같은 샤드 → 룸별 순서 보장
sharded.Publish(roomId, (evt, seq) => Fill(evt, cmd));
```

| 방식 | 스레드 | 링 메모리 (10,000룸) | 판정 |
|---|---:|---:|---|
| 룸당 Disruptor (링 1024) | 10,000 | ~1.3 GB | 불가능 |
| 룸당 `Channel<T>` | 0 (ThreadPool) | 수십 MB | 가능하나 순서/배치 이점 없음 |
| **ShardedDisruptor (샤드 16, 링 4096)** | **16** | **~8 MB** | **권장** |

> 대가: 한 룸이 무거우면 같은 샤드의 다른 룸이 함께 밀립니다(head-of-line blocking).
> 룸별 처리 시간 상한을 두고, 초과 작업은 `WorkerPool`로 분리하세요.

### 4. 결정론적 저널 / 리플레이

```csharp
// 저널이 먼저 기록된 뒤에만 로직이 커밋된다
disruptor.HandleEventsWith(new JournalHandler<GameCommand>(writer, codec))
         .Then(new WorldUpdateHandler(world));

// 재생: 같은 순서로 흘리면 상태가 정확히 재현된다
JournalReader.Replay(stream, codec, () => new GameCommand(), new WorldUpdateHandler(replayWorld));
```

이 구조가 확보되면 다음이 전부 가능해집니다.
- 버그 리포트의 정확한 재현 (시퀀스 구간만 재생)
- 핫 스탠바이 노드로의 상태 복제
- 치팅 검증 (클라이언트 주장 vs 서버 저널 대조)
- 서버 크래시 후 마지막 저널 시퀀스부터 복구

### 5. 관측성 — 사고를 풀 수 있어야 한다

```csharp
new DisruptorOptions
{
    Name = "packet-pipeline",
    EnableMetrics = true,                             // handler_lag / utilization / batch_size ...
    StallThreshold = TimeSpan.FromMilliseconds(250),
    OnStall = info => logger.Warn(info.ToString()),
}
```

```
[WARN] Disruptor 'packet-pipeline' 프로듀서가 250ms 블록됨.
       가장 느린 핸들러 = 'DbJournalHandler' (seq 1,204,331 / cursor 1,270,867, lag 66,536)
```

가장 중요한 지표는 **`disruptor.handler_lag`** 입니다. 계속 증가하면 그 핸들러가 병목이고, 곧 프로듀서가 블록됩니다.

### 6. 예외가 프로세스를 죽이지 않는다

```csharp
disruptor.SetDefaultExceptionHandler(new ResilientExceptionHandler<GameCommand>(
    onError: (ex, seq, evt) => logger.Error(ex, "seq={Seq}", seq),
    maxConsecutiveFailures: 100));   // 연속 실패가 임계치를 넘으면 이 프로세서만 정지

disruptor.ProcessorHalted += (p, ex) => alerting.Fatal($"핸들러 '{p.Name}' 정지 — 드레인 필요");
```

| 정책 | 동작 |
|---|---|
| `Skip` | 이벤트를 버리고 진행 (게임 서버 기본값) |
| `Retry` | 같은 이벤트 재시도 (핸들러가 멱등할 때) |
| `Halt` | **이 프로세서만** 정지. 프로세스는 살아 있음 |

---

## 핵심 설계 원칙

**False Sharing 방지** — `Sequence`는 값 앞뒤로 **128바이트씩** 패딩합니다. Intel의 스트리밍 프리페처가 인접 캐시 라인 쌍(128B)을 함께 로드하므로 64바이트로는 부족합니다.

**메모리 사전 할당** — `RingBuffer<T>`는 생성 시 모든 이벤트 객체를 미리 만듭니다. 게시할 때는 새 객체를 만드는 것이 아니라 기존 슬롯의 필드를 덮어쓰므로 GC 압력이 0입니다 (예제 9 실측: **0 bytes / 10M events**).

**슬롯 정리** — 슬롯이 외부 참조(`byte[]`, 세션 핸들)를 붙들면 객체 수명이 링 버퍼 한 바퀴만큼 연장됩니다. 이벤트가 `IClearable`을 구현하면 마지막 핸들러 처리 직후 프레임워크가 `Clear()`를 호출합니다.

**Lock-free** — `SingleProducerSequencer`는 CAS 없이, `MultiProducerSequencer`는 CAS만으로 동작합니다. lock이 없습니다.

**`Thread.Sleep(1)` 금지** — 모든 대기 경로가 `SpinWait.SpinOnce(sleep1Threshold: -1)`을 사용합니다. 기본 `SpinOnce()`는 Windows 기본 타이머 분해능(15.6ms) 때문에 대기 한 번이 15ms 이상 걸리는데, 이는 60Hz 틱 예산(16.6ms)을 통째로 소모합니다.

**멀티캐스트** — 큐와 달리 모든 이벤트가 등록된 모든 컨슈머에게 전달됩니다. 복사가 없습니다.

**컨슈머 의존성 그래프** — `HandleEventsWith(...).Then(...)`으로 실행 순서를 선언합니다. 하나의 링 버퍼만으로 표현하므로 단계마다 큐 비용이 곱해지지 않습니다.

**배치 효과** — 컨슈머가 뒤처졌다가 깨어나면 쌓인 이벤트를 한 배치로 처리합니다. `OnBatchStart` / `endOfBatch`를 쓰면 I/O 시스템 콜과 DB 왕복을 배치 크기만큼 줄일 수 있습니다.

---

## .NET 기본 도구 vs Disruptor: 언제 무엇을 쓸 것인가

### .NET이 이미 제공하는 것들

`System.Threading.Channels`는 `async/await`를 기본 지원하고 Bounded/Unbounded를 선택할 수 있는 현대적 선택지입니다. `ConcurrentQueue<T>`는 lock-free MPMC 큐이고, `TPL Dataflow`는 파이프라인을 선언적으로 구성합니다. **대부분의 애플리케이션에는 이것들이 더 나은 선택입니다.**

### 근본적인 설계 차이

핵심은 Disruptor가 큐가 아니라는 점입니다. 큐는 "저장", "생산자의 순서 주장", "소비자에 대한 알림"을 하나의 자료구조에 섞습니다. Disruptor는 이 셋을 분리합니다.

**1. 할당이 0에 가깝다.** `Channel<T>`는 이벤트마다 객체를 만들어야 하고 이는 GC 압력이 됩니다. Disruptor는 슬롯의 필드를 덮어씁니다.

**2. False sharing을 막는다.** `ConcurrentQueue`의 head/tail은 같은 캐시 라인에 놓일 수 있어 서로를 무효화합니다. `Sequence`는 의도적으로 독립 캐시 라인을 차지합니다.

**3. 멀티캐스트가 기본이다.** `Channel<T>`는 한 메시지를 한 소비자만 읽습니다. 3개 소비자에게 보내려면 3개 채널에 3번 써야 합니다.

**4. 의존성 그래프에 큐 비용이 없다.** `TPL Dataflow`는 단계마다 내부 큐가 있습니다. Disruptor는 시퀀스 번호 비교만으로 조율합니다.

**5. 배치 효과.** 부하가 올라가도 레이턴시가 거의 평탄하게 유지됩니다. 큐 기반 시스템은 부하 증가 시 레이턴시가 지수적으로 오르는 "J-curve"를 보입니다.

### 결정 순서도

```
1. 하나의 이벤트를 2개 이상의 소비자가 "각자" 봐야 하는가?
   ├─ Yes → Disruptor 후보
   └─ No  → 2번

2. 처리 순서가 전역적으로 고정되어야 하는가? (리플레이/롤백/저널)
   ├─ Yes → Disruptor 후보
   └─ No  → 3번

3. p99.9 지연이 SLA 이고, 그 값이 1ms 미만인가?
   ├─ Yes → Disruptor 후보
   └─ No  → Channel<T> 를 쓴다. (여기서 멈춰도 아무 문제 없다)

4. Disruptor 후보라면 다음을 모두 만족하는가?
   ├─ 핸들러 스레드 총합 ≤ 논리코어 − 4     → 아니면 ShardedDisruptor
   ├─ 핸들러 안에서 await / 블로킹 I/O 없음  → 아니면 WorkerPool 로 분리
   └─ 링 버퍼 개수가 수십 개 이내            → 아니면 샤딩 필수
```

### 게임 서버 컴포넌트별 권고

| 컴포넌트 | 권고 |
|---|---|
| 패킷 수신 → 디코드 → 로직 파이프라인 | **Disruptor** (멀티캐스트 + 순서 + 배치) |
| 저널링 / 리플레이 / 스탠바이 복제 | **Disruptor (최우선)** |
| 룸/존 커맨드 큐 (수천 개) | **ShardedDisruptor** — 룸당 Disruptor는 금지 |
| 비동기 로깅 / 메트릭 | **Disruptor** (Log4j2와 동일한 근거) |
| 랭킹 / 재화 정산 집계 | **Disruptor** (배치 커밋 이점) |
| DB 쓰기 / 외부 API 호출 | **WorkerPool** 또는 `Channel<T>` |
| 요청-응답 RPC | `Channel<T>` / `TaskCompletionSource` |
| HTTP 관리 API | `ASP.NET Core` 기본 |

### 한 줄 요약

`Channel<T>`는 **"올바르게 동작하는 것"이 최우선인 99%의 애플리케이션**에 적합하고, Disruptor는 **"나노초 단위의 예측 가능성이 곧 비즈니스 가치"인 1%의 시스템**을 위한 도구입니다.

---

## 프로젝트 구조

```
DisruptorCS.slnx
├── Disruptor/                  라이브러리 (net8.0 / net10.0)
│   ├── Dsl/                    Disruptor<T>, ValueDisruptor<T>, DisruptorOptions
│   ├── WaitStrategy/           7종 대기 전략
│   ├── Work/                   WorkerPool, WorkProcessor, IWorkHandler
│   └── Game/                   ShardedDisruptor, FixedTickDriver, Journal
├── Disruptor.Analyzers/        Roslyn 분석기 (DISR001~005), 패키지에 동봉
├── Disruptor.Extensions.Hosting/  DI · Generic Host · 헬스체크
├── Disruptor.Tests/            xUnit — 113개 테스트
├── Disruptor.Analyzers.Tests/  분석기 테스트 — 20개
├── Disruptor.Concurrency.Tests/  Coyote 체계적 동시성 모델 — 8종
├── Disruptor.Benchmarks/       BenchmarkDotNet + 지연 분포 측정
├── Disruptor.Soak/             장시간 불변식 하니스
├── Example/                    13개 게임 서버 시나리오
└── samples/                    실행 가능한 샘플 5종
```

### 샘플 5종

각 샘플은 **질문 하나에 답합니다.** 주장이 아니라 실행 결과로 답합니다.

| 샘플 | 답하는 질문 |
|---|---|
| [GameLoopPoller](samples/GameLoopPoller/) | *스레드 모델을 바꾸지 않고 쓸 수 있는가?* |
| [ChatBroadcast](samples/ChatBroadcast/) | *브로드캐스트 syscall 을 어떻게 1/N 로 줄이는가?* |
| [MatchSimulation](samples/MatchSimulation/) | *서버가 죽으면 정말 상태를 복원할 수 있는가?* |
| [ReplayInspector](samples/ReplayInspector/) | *저널로 실제로 무엇을 할 수 있는가?* |
| [AspNetCoreTelemetry](samples/AspNetCoreTelemetry/) | *운영에서 무슨 일이 벌어지는지 보이는가?* |

```bash
dotnet build DisruptorCS.slnx -c Release
dotnet test  Disruptor.Tests  -c Release          # 메모리 순서 결함은 Release 에서만 드러난다
dotnet run   --project Example -c Release
dotnet run   --project Disruptor.Benchmarks -c Release -- latency   # 지연 분포
dotnet run   --project Disruptor.Benchmarks -c Release -- --filter '*'  # BenchmarkDotNet
dotnet run   --project Disruptor.Soak -c Release -- --minutes 30
dotnet run   --project samples/ChatBroadcast -c Release
```

Coyote 체계적 동시성 탐색은 IL 재작성이 필요하므로 한 단계가 더 붙습니다:

```bash
dotnet tool restore
dotnet build  Disruptor.Concurrency.Tests -c Release
dotnet coyote rewrite Disruptor.Concurrency.Tests/rewrite.coyote.json
dotnet Disruptor.Concurrency.Tests/bin/Release/net8.0/rewritten/Disruptor.Concurrency.Tests.dll -i 1000
```

결과를 인용하기 전에 반드시 알아야 할 두 가지 한계를
[`docs/concurrency-testing.md`](docs/concurrency-testing.md) 에 적어 두었습니다.

---

## 측정 결과

측정 환경: Intel i7-12700KF (12P/20L), 64GB RAM, Windows 11, .NET 10, Release/x64, Server GC

| 시나리오 | 결과 |
|---|---|
| 단일 프로듀서 처리량 (10M events, ring 64K) | **249.8M ops/s**, 4.0 ns/op, **할당 0 bytes** |
| 다중 프로듀서 무결성 (8P × 100K, ring 1024) | **유실 0**, 슬롯 덮어쓰기 0, 16.7M ops/s |
| 룸 샤딩 (5,000룸 × 40건) | 200,000건 / 31ms / **스레드 8개**, 룸별 순서 보장 |
| 저널 → 리플레이 (20,000건) | 상태 체크섬 완전 일치 |
| 배치 핸들러 (200,000건) | `OnBatch` 호출 977회 (이벤트당 호출 대신) |
| Blocking 전략 + 느린 상위 핸들러 | CPU **0.07 코어** (hot-spin 없음) |
| Soak (2분 2,480라운드 4,960만 이벤트) | 유실 0, 힙 3.2MB 평탄 |
| 백프레셔 시 프로듀서 대기 | `>15ms` 스톨 **0건** (이전 300K당 284건) |
| 브로드캐스트 배칭 (20만건 × 200세션) | flush 40,000,000회 → **10,400회** (syscall 약 3,800배 감소) |
| 샤딩 저널 → 재생 (룸 1만개, 커맨드 200만건) | 라이브·재생 체크섬 완전 일치, 불일치 룸 0 |
| Coyote 체계적 탐색 (모델 8종 × 1,000 스케줄) | 버그 0. 음성 대조군은 **39회 만에** 교착 발견 |

**disruptor-net 대비 — 지는 결과 포함**

| 조건 | 우세 | 배율 |
|---|---|---|
| 큰 링 (백프레셔 드묾) | **DisruptorCS** | 1.5배 |
| 작은 링 (백프레셔 포화) | **disruptor-net** | 2.9배 |
| 다중 프로듀서, 작은 링 | **disruptor-net** | 1.3~1.8배 |
| vs `Channel<T>` | **DisruptorCS** | 8~38배 빠름, 할당 25~1,360배 적음 |

per-event 비용은 우리가 낮고(3.1ns vs 4.8ns) 백프레셔 핸드오프가 비쌉니다.
배제한 가설 6가지와 함께 [미해결 항목](docs/benchmarks/README.md#open-item-backpressure-bound-throughput)으로 공개했습니다.

---

## 주의사항

**핸들러 안에서 `await` 금지.** Disruptor 컨슈머는 동기 블로킹 스레드입니다. `async void OnEvent`는 첫 `await`에서 반환되므로 프레임워크가 이벤트를 처리 완료로 보고 시퀀스를 전진시킵니다. 그 결과 순서 보장이 깨지고 슬롯이 처리 도중 재사용됩니다. DEBUG 빌드는 이를 생성자에서 자동 검출합니다. 느린 I/O는 `WorkerPool`로 분리하세요.

**`ProducerType.Single`은 반드시 한 스레드에서만.** Release에서는 조용히 데이터가 깨집니다. DEBUG 빌드는 여러 스레드의 게시를 즉시 예외로 잡아냅니다.

**컨테이너에서 스핀 전략 금지.** Kubernetes에서 `cpu: "2"` 같은 제한을 걸면 `BusySpin` 핸들러 하나가 쿼터를 다 태우고 CFS 스로틀링을 유발해 프로세스 전체가 100ms 단위로 정지합니다. 기본값 `TimeoutBlockingWaitStrategy`를 쓰세요.

**`Next()` 후에는 반드시 `Publish()`.** 예외가 나도 `finally`에서 게시해야 합니다. 게시하지 않으면 그 슬롯에서 링 버퍼가 영구히 막힙니다. `PublishEventScope()`가 이를 구조적으로 방지합니다.

**스레드 예산.** 핸들러 스레드는 전용 코어를 전제로 합니다.

```
사용 가능 논리 코어 = C
  네트워크 I/O                        : ~2
  Disruptor 핸들러 (스핀 전략)        : ≤ C - 4   ← 절대 초과 금지
  .NET ThreadPool (타이머/DB/외부 API): 나머지
```

---

## 문서

- [`EVALUATION.md`](EVALUATION.md) — 게임 서버 적용성 평가 및 개선 설계서 (실측 검증 포함)
- [`IMPROVEMENTS.md`](IMPROVEMENTS.md) — 고성능·안정성 개선 및 채택 전략 설계서 (프로브 실측 기반 차기 로드맵)
- [`working_log.md`](working_log.md) — 작업 로그

## 라이선스

MIT
