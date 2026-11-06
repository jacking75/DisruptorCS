# DisruptorCS (한국어)

> [English](README.md) · **한국어**

**LMAX Disruptor의 C# 구현체 + 게임 서버 레이어**: 룸 샤딩 · 고정 틱 통합 · 결정론적 저널/리플레이,
그리고 실제 서버에 필요한 운영 도구(백프레셔 정책 · 스톨 진단 · 컴파일 타임 오용 검출)까지.

[![NuGet](https://img.shields.io/nuget/v/DisruptorCS.svg)](https://www.nuget.org/packages/DisruptorCS/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)

기존 측정: 종료된 통합 데모의 1,000만 이벤트 루프에서 **250M ops/s · 4 ns/op · 할당 0 bytes**
(단일 생산자, Release, i7-12700KF), 8 생산자·1,024 슬롯 스트레스 실행에서 유실 0을 기록했다.
[측정 조건과 최신 안정성 검증](docs/benchmarks/README.md)을 함께 확인한다. 모든 부하에서의 성능·전달 보장값은 아니다.

```bash
dotnet add package DisruptorCS
```

> 이 문서는 [영어판 README](README.md) 의 한국어 번역본입니다. 최신 내용과 전체 구성은 영어판을 참조하세요.
> 아래는 이 라이브러리의 설계 근거와 게임 서버 적용 지침을 한국어로 정리한 것입니다.

## 주요 기능

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

`net8.0` · `net10.0` 멀티 타깃 · AOT 호환 · 사전 할당된 슬롯 재사용

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

using var disruptor = new Disruptor<OrderEvent>(
    eventFactory: () => new OrderEvent(),
    ringBufferSize: 1024,
    producerType: ProducerType.Single);

var handler = new LogicHandler();
disruptor.HandleEventsWith(handler);
var ring = disruptor.Start();

using (var scope = ring.PublishEventScope())
{
    scope.Event.OrderId = 1;
    scope.Event.Quantity = 100;
} // Publishes on Dispose, including when filling throws.

disruptor.Shutdown(); // Stop producers first; this example has finished publishing.
if (handler.Count != 1 || handler.TotalQuantity != 100)
    throw new InvalidOperationException("The order was not fully processed.");
Console.WriteLine($"Orders={handler.Count}, quantity={handler.TotalQuantity}");

public sealed class OrderEvent
{
    public long OrderId;
    public int Quantity;
}

public sealed class LogicHandler : IEventHandler<OrderEvent>
{
    public int Count;
    public int TotalQuantity;

    public void OnEvent(OrderEvent e, long sequence, bool endOfBatch)
    {
        Count++;
        TotalQuantity += e.Quantity;
        // Use endOfBatch for flushing. OnBatchStart runs BEFORE the batch.
    }
}
```

패키지를 추가한 콘솔 프로젝트의 `Program.cs`에 위 코드를 넣으면
`Orders=1, quantity=100`을 출력한다. 저장소 예제에는 .NET 10 SDK가 필요하며, 라이브러리는
.NET 8과 .NET 10을 지원한다. 기본 대기 전략은 `TimeoutBlockingWaitStrategy(1 ms)`다.

직접 게시할 때는 `Next()` 이후 반드시 `finally`에서 `Publish()`한다. 좁은 범위의 scope로
감싸면 이를 자동으로 처리한다. 입력 검증은 슬롯 확보 전에 끝내야 한다. 작성 도중 예외가 나도
scope는 게시하며, 부분 작성된 이벤트를 롤백하지 않는다.

```csharp
using (var scope = ring.PublishEventScope())
{
    scope.Event.OrderId = 1;
    scope.Event.Quantity = 100;
}   // Dispose 에서 자동 Publish
```

모든 사용 예제는 **[샘플 안내](samples/README.ko.md)**에서 찾을 수 있다.
처음 사용하는 경우 **[활용 예제 13선](samples/UsageRecipes/README.ko.md)**부터 시작하면 된다.
예제마다 적용 상황, 실행 이름, 별도 소스 파일, 결과 검증이 있다. 다중 생산자, 병렬 분기와 합류,
배치, 구조체, 워커, 비동기 저장, 풀 버퍼 소유권, 오류 처리 등을 다룬다.

```bash
dotnet run --project samples/UsageRecipes -c Release -- --list
dotnet run --project samples/UsageRecipes -c Release -- pipeline
dotnet run --project samples/UsageRecipes -c Release -- all
```

기초 예제를 익힌 뒤 [응용 샘플 5종](samples/README.ko.md#응용-예제)으로 게임 루프·채팅·샤딩·저널·웹 서비스 구성을 살펴본다.

---

## 무엇이 들어 있나

### 코어

| 구성 요소 | 설명 |
|---|---|
| `RingBuffer<T>` | 참조 타입 이벤트를 사전 할당하는 링 버퍼 |
| `ValueRingBuffer<T>` | **값 타입** 이벤트를 배열에 인라인 저장 (`ref T` 반환; 참조 필드가 있는 구조체는 GC 스캔 대상) |
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
| `BlockingWaitStrategy` | p50 19.1 µs | 최저 | 초과 구독 상태 |
| **`TimeoutBlockingWaitStrategy`** | p50 22.8 µs | 최저 | **기본값** — `OnTimeout` 훅 제공 |
| `PhasedBackoffWaitStrategy` | p50 25.5 µs | 적응형 | 간헐적 버스트 |
| `AsyncWaitStrategy` | 해당 측정 없음 | 최저 | `await` 기반 소비자 |

수치는 [기존 유휴 후 깨우기 측정](docs/benchmarks/README.md#consumer-wake-up-after-idle-20000-single-publishes)이며
타이머 주기나 지연 보장값이 아니다.

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

이하 게임 기능 코드는 구성 예시이며 `GameCommand`, `world`, `codec` 등은 애플리케이션이 정의한다.
고정 시간 간격만으로 결정론적 재생이 보장되지는 않는다. 틱 경계와 시간·난수 등 입력도 기록해야 한다.
완전한 실행 코드는 샘플에서 확인한다.

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

`DropWhenCongested`는 소비자가 공간을 비울 때까지 기다리지 않고 `false`를 반환한다.
`FailAfterTimeout`은 성공·시간 초과까지 호출 스레드를 점유하는 동기 재시도다. 전달한 취소 토큰은
`Block`에만 적용된다. [게시 동작 설명](docs/processing-semantics.ko.md#게시와-백프레셔)을 참고한다.

### 3. 룸 샤딩 — 10,000룸을 8스레드로

```csharp
using var sharded = new ShardedDisruptor<GameCommand>(
    shardCount: 8,                 // 2의 거듭제곱
    ringSizePerShard: 4096,
    eventFactory: () => new GameCommand(),
    handlerFactory: i => new RoomShardHandler(i));

sharded.Start();

// 같은 룸은 같은 샤드로 간다. 소비 순서는 슬롯 점유 시퀀스 순서다.
sharded.Publish(roomId, (evt, seq) => Fill(evt, cmd));
```

| 방식 | 스레드 | 링 메모리 (10,000룸) | 판정 |
|---|---:|---:|---|
| 룸당 Disruptor (링 1024) | 10,000 | ~1.3 GB | 불가능 |
| 룸당 `Channel<T>` | 전용 스레드 불필요 | 용량·데이터에 따라 다름 | 큐 순서 제공, 멀티캐스트는 별도 구성 |
| **ShardedDisruptor (샤드 16, 링 4096)** | **16** | **~8 MB** | **권장** |

위 메모리 수치는 설명용 추정값이며 이벤트 크기와 핸들러 수에 따라 달라진다. 샤드마다 독립적인
시퀀스를 사용하므로 샤드 전체의 전역 순서는 없다. 같은 룸에 여러 생산자가 동시에 게시한다면
업무상 순서는 명령 번호 등으로 별도 정의해야 한다.

> 대가: 한 룸이 무거우면 같은 샤드의 다른 룸이 함께 밀립니다(head-of-line blocking).
> 룸별 처리 시간 상한을 두고, 초과 작업은 `WorkerPool`로 분리하세요.

### 4. 결정론적 저널 / 리플레이

```csharp
// 기록 실패를 건너뛰면 저널 없이 로직이 진행된다. 실패 시 정지한다.
disruptor.SetDefaultExceptionHandler(new HaltOnExceptionHandler<GameCommand>());
disruptor.HandleEventsWith(new JournalHandler<GameCommand>(writer, codec))
         .Then(new WorldUpdateHandler(world));

// 재생: 같은 순서로 흘리면 상태가 정확히 재현된다
JournalReader.Replay(stream, codec, () => new GameCommand(), new WorldUpdateHandler(replayWorld));
```

이 구성은 재생·감사를 위한 기록 순서를 제공한다. 크래시 복구에는 디스크 flush 정책
(`flushToDiskOnBatchEnd` 기본값 `false`), 재현 가능한 입력·초기 상태, `ProcessorHalted`를 통한
종료 조정도 필요하다. 복제 전송과 스냅샷은 애플리케이션에서 구성한다.
[종료와 내구성](docs/processing-semantics.ko.md#종료와-내구성)을 참고한다.

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
// 동기 단계 등록 후 Start 전에 처리기마다 별도 인스턴스를 설정한다.
foreach (var processor in disruptor.Processors.OfType<BatchEventProcessor<GameCommand>>())
    processor.SetExceptionHandler(new ResilientExceptionHandler<GameCommand>(
        onError: (ex, seq, evt) => Console.Error.WriteLine($"seq={seq}: {ex}"),
        maxConsecutiveFailures: 100)); // 임계값에 도달하면 정지한다.

disruptor.ProcessorHalted += (p, ex) => alerting.Fatal($"핸들러 '{p.Name}' 정지 — 드레인 필요");
```

| 정책 | 동작 |
|---|---|
| `Skip` | 이벤트를 버리고 진행 (게임 서버 기본값) |
| `Retry` | 같은 이벤트 또는 동기 배치의 실패 span 재시도; 멱등성 필요 |
| `Halt` | **이 프로세서만** 정지. 프로세스는 살아 있음 |

Skip은 롤백이 아니며 후속 단계가 진행할 수 있다. 상태가 있는 `ResilientExceptionHandler`를
`SetDefaultExceptionHandler`로 여러 처리기에 공유하면 실패 횟수가 섞인다. 기본 설정은 처리기마다
별도 인스턴스를 만든다. [복구·정리 규칙](docs/processing-semantics.ko.md#동기-예외-정책)을 참고한다.

---

## 핵심 설계 원칙

**False Sharing 방지** — `Sequence`는 값 앞뒤로 **128바이트씩** 패딩합니다. Intel의 스트리밍 프리페처가 인접 캐시 라인 쌍(128B)을 함께 로드하므로 64바이트로는 부족합니다.

**메모리 사전 할당** — `RingBuffer<T>`는 슬롯 객체를 생성 시 미리 만든다. 필드만 덮어쓰는 게시 경로는
이벤트 객체를 매번 만들지 않는다. payload 생성, 캡처 람다, 비동기 대기 등 다른 경로의 할당은 별도다.
기존 10M 이벤트 루프의 할당 0 측정은 [측정 조건](docs/benchmarks/README.md)과 함께 해석한다.

**슬롯 정리** — DSL은 `AutoClearSlots = true`(기본값)이고 최종 동기 프로세서가 하나이며 최종 워커 풀이
없을 때만 자동 정리를 켠다. 참조 이벤트는 `IClearable`을 구현해야 하고, 값 슬롯은 `default`로
초기화한다. 최종 독자가 여러 개면 합류 후 정리하는 단계를 둔다. 저수준 프로세서와 폴러는
소유권·정리를 직접 구성한다. [풀 버퍼 예제](samples/UsageRecipes/README.ko.md#10-풀-버퍼-소유권-pooled-payload)를 참고한다.

**시퀀스 점유** — 단일 생산자는 CAS 없이, 다중 생산자는 CAS로 슬롯을 점유한다. 전체 파이프라인이
lock-free인 것은 아니다. 블로킹 대기 전략은 모니터 락을 사용하며, 링이 차면 생산자가 소비자를 기다릴 수 있다.

**기본 생산자 백오프는 `Thread.Sleep(1)`을 피한다.** `SpinOnlyBackoff`는 스핀·양보로 대기한다.
`SleepingBackoff`와 `SleepingWaitStrategy`는 CPU 절약을 위해 의도적으로 sleep을 사용하고,
블로킹 전략은 모니터 대기를 사용한다. 모든 대기 경로가 스핀하는 것은 아니다.

**멀티캐스트** — 큐와 달리 모든 이벤트가 등록된 모든 컨슈머에게 전달됩니다. 복사가 없습니다.

**컨슈머 의존성 그래프** — `HandleEventsWith(...).Then(...)`으로 실행 순서를 선언합니다. 하나의 링 버퍼만으로 표현하므로 단계마다 큐 비용이 곱해지지 않습니다.

**배치 효과** — 컨슈머가 뒤처졌다가 깨어나면 쌓인 이벤트를 한 배치로 처리합니다. `OnBatchStart` / `endOfBatch`를 쓰면 I/O 시스템 콜과 DB 왕복을 배치 크기만큼 줄일 수 있습니다.

---

## .NET 기본 도구 vs Disruptor: 언제 무엇을 쓸 것인가

### .NET이 이미 제공하는 것들

`System.Threading.Channels`는 `async/await`를 기본 지원하고 Bounded/Unbounded를 선택할 수 있는 현대적 선택지입니다. `ConcurrentQueue<T>`는 lock-free MPMC 큐이고, `TPL Dataflow`는 파이프라인을 선언적으로 구성합니다. **대부분의 애플리케이션에는 이것들이 더 나은 선택입니다.**

### 근본적인 설계 차이

핵심은 Disruptor가 큐가 아니라는 점입니다. 큐는 "저장", "생산자의 순서 주장", "소비자에 대한 알림"을 하나의 자료구조에 섞습니다. Disruptor는 이 셋을 분리합니다.

**1. 슬롯을 재사용한다.** Disruptor는 사전 할당된 이벤트를 덮어쓴다. `Channel<T>`도 값 타입이나 재사용 객체를 전달할 수 있으므로 이벤트당 객체 할당이 필수는 아니다. 비교 시 payload 생성과 큐 내부 비용을 구분한다.

**2. 시퀀스 간 간섭을 줄인다.** `Sequence`는 패딩으로 인접한 상태와의 캐시 라인 공유를 줄인다. 다른 큐 구현보다 항상 빠르다는 의미는 아니다.

**3. 멀티캐스트가 기본이다.** `Channel<T>`는 한 메시지를 한 소비자만 읽습니다. 3개 소비자에게 보내려면 3개 채널에 3번 써야 합니다.

**4. 의존성 그래프에 큐 비용이 없다.** `TPL Dataflow`는 단계마다 내부 큐가 있습니다. Disruptor는 시퀀스 번호 비교만으로 조율합니다.

**5. 배치 효과.** 모인 이벤트를 함께 처리하면 호출·I/O 비용을 분산할 수 있다. 소비 용량을 넘는 부하에서는 Disruptor도 대기와 지연이 늘어나므로 실제 처리량과 꼬리 지연을 측정해야 한다.

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
├── Directory.Build.props       공통 패키지 메타데이터 + MinVer 버전 하한
├── build/                      pack.ps1 (릴리스 패키징·nupkg 검증), icon.png
├── Disruptor/                  라이브러리 (net8.0 / net10.0)
│   ├── Async/                  AsyncWaitStrategy, AsyncBatchEventProcessor, AsyncBridge
│   ├── Dsl/                    Disruptor<T>, ValueDisruptor<T>, DisruptorOptions
│   ├── WaitStrategy/           7종 대기 전략
│   ├── Work/                   WorkerPool, WorkProcessor, IWorkHandler
│   └── Game/                   ShardedDisruptor, FixedTickDriver, Journal
├── Disruptor.Analyzers/        Roslyn 분석기 (DISR001~005), 패키지에 동봉
├── Disruptor.Extensions.Hosting/  DI · Generic Host · 헬스체크 (별도 패키지)
├── Disruptor.Tests/            xUnit — 141개 테스트
├── Disruptor.Analyzers.Tests/  분석기 테스트 — 20개
├── Disruptor.Concurrency.Tests/  Coyote 체계적 동시성 모델 — 8종
├── Disruptor.Benchmarks/       BenchmarkDotNet + 지연 분포 측정
├── Disruptor.Soak/             장시간 불변식 하니스
└── samples/                    기능별 예제 13개 + 응용 샘플 5종
```

### 기능별 예제 13개와 응용 샘플 5종

[활용 예제 13선](samples/UsageRecipes/README.ko.md)에서 작은 구성으로 API를 익힌 뒤,
아래 응용 샘플로 게임 서버와 웹 서비스에 적용하는 흐름을 살펴볼 수 있다.

각 샘플은 **질문 하나에 답합니다.** 주장이 아니라 실행 결과로 답합니다.

| 샘플 | 답하는 질문 |
|---|---|
| [GameLoopPoller](samples/GameLoopPoller/) | *스레드 모델을 바꾸지 않고 쓸 수 있는가?* |
| [ChatBroadcast](samples/ChatBroadcast/) | *브로드캐스트 flush 호출을 어떻게 묶는가?* |
| [MatchSimulation](samples/MatchSimulation/) | *서버가 죽으면 정말 상태를 복원할 수 있는가?* |
| [ReplayInspector](samples/ReplayInspector/) | *저널로 실제로 무엇을 할 수 있는가?* |
| [AspNetCoreTelemetry](samples/AspNetCoreTelemetry/) | *운영에서 무슨 일이 벌어지는지 보이는가?* |

```bash
dotnet build DisruptorCS.slnx -c Release
dotnet test  Disruptor.Tests  -c Release          # JIT 최적화 경로의 동시성도 검증한다
dotnet run   --project samples/UsageRecipes -c Release -- all
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

아래는 기존 측정 결과다. 최신 변경의 검증은 [2026-11-07 기록](docs/benchmarks/README.md#2026-11-07-reliability-changes-throughput-check)을 참조한다.

기존 측정 환경: Intel i7-12700KF (12P/20L), 64GB RAM, Windows 11, .NET 10, Release/x64, Server GC

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
| 브로드캐스트 배칭 (20만건 × 200세션) | flush 40,000,000회 → **10,400회** (모사한 flush 약 3,800배 감소) |
| 샤딩 저널 → 재생 (룸 1만개, 커맨드 200만건) | 라이브·재생 체크섬 완전 일치, 불일치 룸 0 |
| Coyote 탐색 (정상 모델 7종, 음성 대조군 1종) | 정상 모델별 1,000회에서 버그 미발견. 음성 대조군은 **39회째** 교착 발견 |

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

배치 복구·취소·정리 소유권·드레인 시간 초과는 [처리·복구·종료 동작](docs/processing-semantics.ko.md)에서 확인한다.

**동기 핸들러 안에서 `await` 금지.** Disruptor 컨슈머는 동기 블로킹 스레드입니다. `async void OnEvent`는 첫 `await`에서 반환되므로 프레임워크가 이벤트를 처리 완료로 보고 시퀀스를 전진시킵니다. 그 결과 순서 보장이 깨지고 슬롯이 처리 도중 재사용됩니다. DEBUG 빌드는 이를 생성자에서 자동 검출합니다. 동기 I/O는 `WorkerPool`로 분리하고, 비동기 I/O는 `IAsyncBatchEventHandler`와 `AsyncBatchEventProcessor`를 사용한다. 워커의 `OnEvent`도 동기 콜백이다.

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

- [처리·복구·종료 동작](docs/processing-semantics.ko.md)
- [활용 예제 13선](samples/UsageRecipes/README.ko.md) — API 구성과 결과 검증
- [분석기 규칙](docs/analyzers/README.md)
- [성능 측정](docs/benchmarks/README.md)
- [동시성 모델 검증](docs/concurrency-testing.md)
- [릴리스 절차](docs/RELEASE.md)
- [변경 내역](CHANGELOG.md)
- [기여 안내](CONTRIBUTING.md)

## 라이선스

MIT
