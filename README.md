# DisruptorCS
Disruptor의 C# 버전

## 아키텍처 다이어그램

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
│  │ 0 │ 1 │ 2 │ 3 │ 4 │ 5 │ 6 │ 7 │  (size=2^n)    │
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
        (WaitFor → WaitStrategy)
```

---

## 핵심 설계 원칙 요약

이 구현에서 LMAX Disruptor의 핵심 아이디어를 모두 반영했습니다.

**False Sharing 방지**: `Sequence` 클래스는 `StructLayout(Size=192)`로 캐시 라인 간 간섭을 방지합니다. CPU 캐시 라인은 보통 64바이트인데, 시퀀스 값을 양쪽으로 패딩하여 다른 변수와 같은 캐시 라인에 놓이지 않도록 합니다.

**메모리 사전 할당**: `RingBuffer<T>`는 생성 시 모든 이벤트 객체를 미리 만들어둡니다. 이후 이벤트를 "게시"할 때는 새 객체를 만드는 것이 아니라 기존 객체의 필드를 덮어쓰므로 GC 압력이 제로에 가깝습니다.

**Lock-free 알고리즘**: `SingleProducerSequencer`는 CAS 없이 단순 변수 조작만으로 동작하여 최고 성능을 냅니다. `MultiProducerSequencer`는 `Interlocked.CompareExchange`(CAS)를 사용하지만 여전히 lock을 사용하지 않습니다.

**멀티캐스트**: 큐와 달리 모든 이벤트가 등록된 모든 컨슈머에게 전달됩니다.

**컨슈머 의존성 그래프**: `HandleEventsWith(...).Then(...)` 패턴으로 컨슈머 간 실행 순서를 지정할 수 있습니다. 예를 들어 저널링과 복제가 완료된 후에만 비즈니스 로직이 실행되도록 구성할 수 있습니다.

**교체 가능한 대기 전략**: `BlockingWaitStrategy`(보수적, 낮은 CPU), `YieldingWaitStrategy`(저지연), `BusySpinWaitStrategy`(초저지연, 높은 CPU) 등 배포 환경에 맞게 선택할 수 있습니다.



## .NET 기본 제공 도구 vs LMAX Disruptor: 언제, 왜 Disruptor를 선택해야 하는가

### .NET이 이미 제공하는 것들

.NET에는 스레드 간 데이터 전달을 위한 도구가 이미 풍부합니다.

**`System.Threading.Channels`** (.NET Core 3.0+)는 가장 현대적인 선택지입니다. `async/await`를 기본 지원하고, 단일/다중 생산자·소비자 조합을 옵션으로 설정할 수 있으며, Bounded/Unbounded를 선택할 수 있습니다. 내부적으로 `UnboundedChannel`은 `ConcurrentQueue`를 감싸고 있고, `BoundedChannel`은 독자적인 링 버퍼를 구현하고 있어 상당히 빠릅니다.

**`ConcurrentQueue<T>`**는 lock-free MPMC 큐로, .NET Core에서 여러 차례 성능 개선을 거쳤습니다.

**`BlockingCollection<T>`**은 `ConcurrentQueue` 위에 blocking 의미를 추가한 래퍼입니다.

**`TPL Dataflow`**는 파이프라인과 그래프 구조를 선언적으로 구성할 수 있는 고수준 라이브러리입니다.

이 도구들은 대부분의 애플리케이션에 충분하고, 일반적인 웹 서버, CRUD 앱, 마이크로서비스에서는 오히려 더 나은 선택입니다. 그렇다면 Disruptor가 필요한 이유는 무엇일까요?

---

### 근본적인 설계 차이

핵심 차이는 Disruptor가 큐(Queue)가 아니라는 점입니다. 큐는 "저장", "생산자의 순서 주장", "소비자에 대한 알림"이라는 세 가지 관심사를 하나의 데이터 구조에 혼합합니다. Disruptor는 이 세 가지를 완전히 분리합니다.

#### 1. 메모리 할당이 제로에 가깝다

`Channel<T>`나 `ConcurrentQueue<T>`는 이벤트를 큐에 넣을 때마다 참조를 복사하거나, Bounded 채널의 경우 내부 배열 슬롯에 값을 저장합니다. 참조 타입이면 객체를 외부에서 생성해야 하고, 이는 곧 GC 압력으로 이어집니다.

Disruptor는 시작 시 모든 이벤트 객체를 미리 할당합니다. 생산자는 새 객체를 만드는 것이 아니라, 기존 슬롯의 필드를 덮어씁니다. 전체 생명 주기 동안 할당이 사실상 제로이므로, .NET의 GC 정지가 레이턴시에 영향을 주는 시스템에서 결정적인 차이를 만듭니다.

```
// Channel 방식 — 매번 새 객체 생성 → GC 부담
await channel.Writer.WriteAsync(new OrderEvent { Id = 1, Price = 100.5 });

// Disruptor 방식 — 미리 할당된 객체의 필드만 변경 → GC 제로
long seq = ringBuffer.Next();
var evt = ringBuffer[seq]; // 이미 존재하는 객체
evt.Id = 1;
evt.Price = 100.5;
ringBuffer.Publish(seq);
```

#### 2. False Sharing 방지

`ConcurrentQueue`의 head와 tail 포인터는 서로 다른 스레드가 접근하지만, 같은 캐시 라인(64바이트)에 놓일 수 있습니다. 한 스레드가 head를 변경하면 다른 코어의 캐시에 있던 tail까지 무효화됩니다. 이것이 **False Sharing**이고, 현대 CPU에서 성능을 수십 배 떨어뜨리는 주범입니다.

Disruptor의 `Sequence` 클래스는 의도적으로 192바이트로 패딩하여, 각 시퀀스가 반드시 독립된 캐시 라인을 차지하도록 보장합니다. `Channel<T>`에는 이런 수준의 최적화가 없습니다.

#### 3. 멀티캐스트 vs 경쟁적 소비

`Channel<T>`와 `ConcurrentQueue<T>`는 하나의 메시지를 하나의 소비자만 읽습니다. 여러 소비자가 같은 데이터를 봐야 한다면, 별도의 채널을 만들고 데이터를 복제해야 합니다.

Disruptor는 기본적으로 멀티캐스트입니다. 모든 이벤트가 등록된 모든 소비자에게 전달됩니다. 저널링, 복제, 비즈니스 로직이 동일한 이벤트를 각자 독립적으로 처리할 수 있으며, 데이터 복사가 없습니다.

```
// Channel로 같은 이벤트를 3개의 소비자에게 전달하려면:
// → 3개의 채널에 동일한 데이터를 3번 써야 함

// Disruptor로는:
disruptor.HandleEventsWith(journalHandler, replicationHandler)
         .Then(businessLogicHandler);
// → 하나의 링 버퍼, 복사 없음, 의존성 그래프까지 표현 가능
```

#### 4. 소비자 의존성 그래프

`TPL Dataflow`도 파이프라인을 구성할 수 있지만, 각 단계(Block) 사이에 내부 큐가 존재하며, 각 큐마다 enqueue/dequeue 비용과 동기화 오버헤드가 발생합니다.

Disruptor에서는 하나의 링 버퍼만으로 복잡한 의존성 그래프를 표현합니다. 단계 간의 조율은 시퀀스 번호 비교만으로 이루어지므로, 큐 비용이 곱해지는 문제가 없습니다.

#### 5. 배치 효과 (Batching Effect)

소비자가 잠시 느려져서 뒤처졌을 때, 깨어나면 그 사이에 쌓인 이벤트를 한꺼번에 배치로 처리할 수 있습니다. 이 과정에서 동시성 메커니즘(CAS, 배리어)에 관여하지 않으므로, 부하가 높아져도 레이턴시가 거의 일정하게 유지됩니다.

큐 기반 시스템은 부하가 증가하면 레이턴시가 지수적으로 증가하는 **"J-curve"** 현상을 보이지만, Disruptor는 메모리 서브시스템이 포화될 때까지 거의 **평탄한 레이턴시**를 유지합니다.

---

### 성능 차이: 숫자로 보기

LMAX의 공식 벤치마크(AMD EPYC 9374F, Java 기준이지만 .NET 포팅도 동일한 패턴)에서 Disruptor 4와 `ArrayBlockingQueue`를 비교한 결과입니다.

| 시나리오 | ArrayBlockingQueue | Disruptor 4 | 배율 |
|---|---|---|---|
| Unicast 1P→1C | 20.9M ops/s | **160.4M ops/s** | **7.7x** |
| Pipeline 1P→3C | 5.2M ops/s | **101.3M ops/s** | **19.4x** |
| Multicast 1P→3C | 2.4M ops/s | **70.0M ops/s** | **29.7x** |
| Diamond 1P→3C | 3.4M ops/s | **63.1M ops/s** | **18.4x** |

레이턴시 차이는 더 극적입니다.

| 지표 | ArrayBlockingQueue | Disruptor |
|---|---|---|
| 평균 레이턴시 | 32,757 ns | **52 ns** |
| P99 레이턴시 | 2,097,152 ns | **128 ns** |

평균 레이턴시가 **630배**, P99가 **16,000배** 차이납니다.

---

### 그렇다면 언제 Disruptor를 써야 하는가?

#### Disruptor가 적합한 경우

**금융 거래 시스템(HFT, 거래소)**이 가장 대표적입니다. LMAX 자체가 금융 거래소이고, Disruptor는 초당 수백만 건의 거래를 마이크로초 단위 레이턴시로 처리하기 위해 탄생했습니다. 주문 매칭 엔진, 리스크 계산, 시장 데이터 피드 같은 곳에서 마이크로초가 곧 돈입니다.

**실시간 이벤트 처리 파이프라인**에서도 빛을 발합니다. 하나의 이벤트 스트림을 저널링, 복제, 비즈니스 로직 등 여러 단계가 동시에/순차적으로 처리해야 하는 구조는 Disruptor의 멀티캐스트 + 의존성 그래프가 정확히 맞습니다.

**GC 정지가 치명적인 시스템**에 해당합니다. .NET의 GC가 아무리 개선되었더라도, Gen2 컬렉션이 발생하면 수 밀리초의 정지가 생길 수 있습니다. Disruptor의 사전 할당 모델은 이런 정지를 원천적으로 제거합니다.

**예측 가능한(결정적) 레이턴시가 필요한 시스템**도 해당합니다. 평균이 아니라 P99, P99.99 레이턴시가 중요한 시스템에서 Disruptor의 배치 효과와 lock-free 설계는 레이턴시 꼬리(tail latency)를 극적으로 줄여줍니다.

**고처리량 로깅/감사 시스템**에서도 유용합니다. Log4j2가 내부적으로 Disruptor를 사용하는 이유와 같습니다. 비동기 로깅에서 애플리케이션 스레드의 지연을 최소화합니다.

#### .NET 기본 도구가 더 나은 경우

**`async/await`가 필요한 경우**, Disruptor는 동기적(blocking/spinning) 모델입니다. 비동기 I/O와 자연스럽게 통합하려면 `Channel<T>`가 훨씬 편리합니다.

**일반적인 웹 서버, API, CRUD 애플리케이션**에서는 레이턴시 차이가 무의미합니다. `Channel<T>`의 수 마이크로초 오버헤드는 네트워크 왕복(밀리초)이나 DB 쿼리(밀리초) 앞에서 측정조차 되지 않습니다.

**복잡도가 부담인 경우**, Disruptor는 2단계 게시(claim → publish), 시퀀스 관리, 대기 전략 선택 등 이해해야 할 개념이 많습니다. `Channel<T>`는 `WriteAsync/ReadAsync` 두 줄이면 됩니다.

**소비자가 독립적으로 다른 메시지를 처리해야 하는 경우(Work Distribution)**, Disruptor의 기본 모델은 멀티캐스트(모든 소비자가 모든 메시지를 봄)입니다. 메시지를 소비자끼리 나눠 처리하는 패턴은 `Channel<T>`의 다중 소비자 모델이 더 자연스럽습니다.

---

### 결정 프레임워크

```
레이턴시 요구사항이 마이크로초 이하인가?
├── Yes → Disruptor
└── No
    └── 하나의 이벤트를 여러 소비자가 동시에 봐야 하는가?
        ├── Yes → Disruptor (멀티캐스트 네이티브)
        └── No
            └── GC 정지가 SLA에 영향을 주는가?
                ├── Yes → Disruptor (할당 제로)
                └── No
                    └── async/await 통합이 필요한가?
                        ├── Yes → Channel<T>
                        └── No → Channel<T> 또는 ConcurrentQueue<T>
```

---

### 한 줄 요약
`Channel<T>`는 **"올바르게 동작하는 것"** 이 최우선인 99%의 애플리케이션에 적합하고, Disruptor는 **"나노초 단위의 레이턴시와 예측 가능성이 곧 비즈니스 가치"** 인 1%의 시스템을 위한 도구입니다. 성능이 충분한 도구를 더 복잡한 도구로 교체하는 것은 낭비이고, 반대로 나노초가 돈인 환경에서 범용 도구를 고집하는 것도 낭비입니다.