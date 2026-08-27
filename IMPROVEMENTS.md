# DisruptorCS 고성능·안정성 개선 및 채택 전략 설계서

> 대상 커밋: `5d19229` (main) · 조사일: 2026-08-27
> 조사 방식: 전체 소스 정밀 재리뷰 + 가설 검증 프로브 3종 실측
> 검증 환경: Intel i7-12700KF (12P/20L), 64GB RAM, Windows 11 26200, .NET 10.0.400, Release/x64, Server GC
>
> ## ✅ 구현 완료 (2026-08-27)
>
> **이 문서의 Wave 1~4 항목이 전부 구현되었습니다.** 아래 본문은 조사·설계 시점의 기록을 그대로
> 보존한 것이며, 적용 결과와 **측정이 예측을 뒤집은 항목**은 **[§7 구현 완료 현황](#7-구현-완료-현황)** 에 있습니다.
>
> 요약: 처리량 69M → **250M ops/s** (4.0ns/op, 할당 0) · 유휴 틱 정밀도 15.40ms → **1.52ms** ·
> 테스트 60개 → **133개** (라이브러리 113 + 분석기 20) + Coyote 동시성 모델 8종 ·
> Analyzer·Hosting·Soak·Concurrency 프로젝트 신설 · 샘플 5종 ·
> disruptor-net 대비 **지는 결과까지 공개**

> 이 문서는 `EVALUATION.md` §9(구현 완료)의 **다음 단계**다. 현재 코드는 정확성(P0)과
> 게임 서버 기능(P1~P3)을 갖췄지만, 정밀 재조사 결과 (1) 소비 루프에 **측정으로 확인된 87% 처리량 여유**,
> (2) Blocking 계열의 **게시 경로 lock 병목(6~7배)**, (3) **Windows 타이머 분해능으로 인한 틱 정밀도 문제**
> 등이 남아 있고, 라이브러리가 "사람들이 선택하는 라이브러리"가 되기 위한 격차도 뚜렷하다.
> 각 항목은 **[현상/근거] → [구현 방법(코드)] → [기대 효과] → [리스크/주의]** 순으로 기술한다.

---

## 0. 요약

### 0.1 실측으로 확인된 3대 성능/안정성 발견

| # | 발견 | 실측 근거 | 관련 절 |
|---|---|---|---|
| 1 | 소비 루프의 per-event 부대비용(try/catch, `OnEventProcessed`, `IClearable` 검사)이 지배적. 배치 단위로 재구성하면 **소비자 처리량 +87%** | 339M → 649M ops/s (100M건, 소비자 병목 조건) | §2.1 |
| 2 | `BlockingWaitStrategy`는 **게시할 때마다** lock+PulseAll(39.1ns)을 지불. per-event 게시 처리량이 Yielding 대비 **1/6~1/7** | 8.6M vs 50~60M ops/s. fence+검사 방식은 4.1ns | §2.3 |
| 3 | 유휴 상태의 `OnTimeout(1ms)`이 실제로는 **15.4ms 간격**으로 발동 (Windows 기본 타이머 분해능). `timeBeginPeriod(1)` 적용 시 1.52ms | 130회/2s → 1,314회/2s | §3.2 |

+87%라는 숫자보다 중요한 부수 발견: **struct 제네릭 디버추얼라이제이션은 .NET 10에서 불필요했다.**
단형(monomorphic) 호출 지점은 Dynamic PGO가 이미 디버추얼라이즈하며, struct 제네릭 변형(V3)은
인터페이스 변형(V2)과 동일하거나 약간 느렸다. disruptor-net의 `StructProxy` 같은 IL-emit 트릭을
따라할 필요가 없다 — **루프 구조가 문제였지 디스패치가 문제가 아니었다.** (§2.1 상세)

### 0.2 항목 총괄표

| 절 | 항목 | 유형 | 기대 효과 | 공수 | 우선순위 |
|---|---|---|---|---:|:---:|
| §2.1 | 소비 루프 배치 단위 재구성 | 성능 | 소비자 처리량 **+87% 실측** | 8h | ★★★ |
| §2.2 | 배치 핸들러 API (`IBatchEventHandler`) | 성능 | 가상 호출 O(배치)→O(1), SIMD/일괄직렬화 가능 | 10h | ★★☆ |
| §2.3 | Blocking 게시 경로 lock 제거 | 성능 | per-event 게시 8.6M→예상 40M+ ops/s | 4h | ★★★ |
| §2.4 | 백프레셔 루프 QPC 호출 절감 | 성능 | 백프레셔 해소 지연 단축 | 2h | ★★☆ |
| §2.5 | 메트릭/게이트 카운터 경합 제거 | 성능 | MP 게시 경로 공유 캐시라인 제거 | 4h | ★★☆ |
| §2.6 | 게시 API 클로저 할당 함정 차단 | 성능 | 게시 경로 할당 0 유지 | 2h | ★★☆ |
| §2.7 | `MaxBatchSize` (공정성/지연 상한) | 성능·안정 | 멀티캐스트 tail latency 개선 | 3h | ★☆☆ |
| §3.1 | 저널 무결성 (해시+길이상한+단조검증) | 안정성 | 손상 파일로 인한 OOM/오재생 차단 | 8h | ★★★ |
| §3.2 | 고분해능 타이머 스코프 | 안정성 | 유휴 틱 정밀도 15.4ms→1.5ms 실측 | 4h | ★★★ |
| §3.3 | `ResilientExceptionHandler.Default` 공유 상태 결함 | 안정성 | 프로세서 간 실패 스트릭 오염 제거 | 1h | ★★★ |
| §3.4 | 스톨 리포트 rate-limit | 안정성 | 로그 폭주 방지 | 1h | ★★☆ |
| §3.5 | 수명주기 가드 (Dispose 후 Halt 등) | 안정성 | ODE 방지 | 2h | ★★☆ |
| §3.6 | Sharded + Block 정책 조합 가드 | 안정성 | halted 샤드로 인한 프로듀서 행 방지 | 3h | ★★☆ |
| §3.7 | Roslyn Analyzer 패키지 | 안정성·매력 | 오용을 컴파일 타임에 차단 | 24h | ★★★ |
| §3.8 | Soak/카오스 하니스 + Coyote | 안정성 | 장기 경쟁 조건 노출 | 16h | ★★☆ |
| §4.1 | 영어 우선 문서화 | 매력 | 글로벌 노출의 전제조건 | 16h | ★★★ |
| §4.2 | `EventPoller` (스레드 없는 폴링 소비) | 매력·기능 | 기존 게임 루프/Unity 통합 — 킬러 기능 | 10h | ★★★ |
| §4.3 | Async 브리지 | 매력·기능 | .NET 생태계 기대치 충족 | 20h | ★★☆ |
| §4.4 | Microsoft.Extensions 통합 (DI/Hosting/HealthCheck) | 매력 | ASP.NET Core 사용자 진입 장벽 제거 | 12h | ★★★ |
| §4.5 | 정직한 비교 벤치마크 공개 | 매력 | 신뢰 구축의 핵심 | 12h | ★★★ |
| §4.6 | 샘플 갤러리 | 매력 | "내 문제에 맞는 예제" 제공 | 16h | ★★☆ |
| §4.7 | NuGet 배포 + 브랜딩 | 매력 | 설치 가능해야 존재하는 것 | 8h | ★★★ |
| §4.8 | 포지셔닝 (disruptor-net 관계 정립) | 매력 | "왜 이걸 쓰나"에 대한 답 | 4h | ★★★ |

---

## 1. 조사 방법

원본을 수정하지 않고 스크래치 프로브 3종으로 가설을 검증했다. 프로브 원본 데이터는 부록 A.

| 프로브 | 가설 | 결과 |
|---|---|---|
| P-A (DevirtProbe) | "소비 루프의 per-event 비용이 크고, 디버추얼라이제이션이 필요할 것" | 절반만 참: 루프 재구성 +87%, struct 제네릭은 **효과 없음** (PGO가 이미 처리) |
| P-B (TimerProbe §2) | "게시마다 lock+PulseAll은 비싸다" | 참: 39.1ns vs 4.1ns (약 10배), e2e 6~7배 |
| P-C (TimerProbe §1) | "유휴 OnTimeout(1ms)은 Windows에서 15.6ms로 양자화된다" | 참: 실측 15.40ms → `timeBeginPeriod(1)` 후 1.52ms |

프로브 조건: 소비자 병목을 만들기 위해 프로듀서는 1,024개 배치 클레임으로 게시 비용을 상각.
링 64K, 100M 이벤트(P-A) / 10M 이벤트(P-B e2e), 각 2라운드 실행 후 안정 라운드 채택.

---

## 2. 고성능 개선

### 2.1 소비 루프 배치 단위 재구성 — 실측 +87%

#### 현상

현재 `BatchEventProcessor.ProcessEvents`의 안쪽 루프는 **이벤트 1건마다** 다음을 지불한다.

```csharp
while (nextSequence <= available)
{
    T @event = _ringBuffer[nextSequence];
    try                                                    // (a) per-event try/catch
    {
        _eventHandler.OnEvent(@event, nextSequence, ...);  // (b) 인터페이스 호출
        if (ClearAfterProcess && @event is IClearable c)   // (c) per-event 타입 검사
            c.Clear();
        _exceptionHandler.OnEventProcessed();              // (d) per-event 인터페이스 호출
        nextSequence++;
    }
    catch (Exception ex) { ... }
}
```

(a)의 try 영역 진입/이탈 자체는 저렴하지만, try 경계는 JIT의 코드 이동·레지스터 할당을 제약해
루프 전체를 느리게 만든다. (c)는 매건 타입 검사, (d)는 매건 가상 호출이다.

#### 실측 (P-A, 100M건, 소비자 병목 조건)

| 변형 | 처리량 | 비고 |
|---|---:|---|
| V1: 현재 라이브러리 | 339~362M ops/s | 기준 |
| V2: **배치 try/catch + (c)(d) 제거**, 인터페이스 디스패치 유지 | 649~666M ops/s | **+87%** |
| V3: V2 + struct 제네릭 디버추얼라이제이션 | 631~633M ops/s | V2와 동급(약간 열세) |

**V2 ≈ V3라는 것이 핵심 발견이다.** 이 호출 지점은 프로세서 인스턴스당 핸들러가 하나뿐인
단형 호출이라 Dynamic PGO의 guarded devirtualization이 이미 인라인하고 있다. 이득은 전부
**루프 구조**에서 나왔다. 따라서:

- disruptor-net처럼 IL-emit `StructProxy`로 제네릭 특수화를 강제할 필요가 **없다**.
- 단, 한 프로세스에 **핸들러 타입이 매우 많고** 같은 `T`의 프로세서가 뒤섞이는 극단적 상황에서는
  공유 제네릭(`__Canon`) 코드의 GDV 추측(최대 2개)이 빗나갈 수 있다. 그 경우에만
  `BatchEventProcessor<T, THandler>` 오버로드를 선택지로 제공한다(아래 참고).

#### 구현

`ProcessEvents`를 다음처럼 재구성한다. **의미(Skip/Retry/Halt, 실패 스트릭)는 보존된다.**

```csharp
// BatchEventProcessor<T> — 재구성된 이벤트 루프
//
// 핵심: try 를 배치 바깥에 한 번만 두고, 예외가 나면 "실패한 시퀀스"를 특정해
// 정책을 적용한 뒤 바깥 while 로 배치의 나머지를 재진입한다.

/// <summary>T 가 IClearable 을 구현하는지의 정적 캐시. 매건 타입 검사를 제거한다.</summary>
private static readonly bool TypeIsClearable = typeof(IClearable).IsAssignableFrom(typeof(T));

private void ProcessEvents(CancellationToken ct)
{
    long nextSequence = _sequence.Value + 1L;
    var idleSpin = new SpinWait();

    while (!ct.IsCancellationRequested)
    {
        var waitResult = _barrier.WaitFor(nextSequence, ct);
        if (waitResult.IsCancelled) break;

        if (waitResult.IsTimeout)
        {
            InvokeTimeout(nextSequence - 1);
            continue;
        }

        long available = waitResult.AvailableSequence;
        if (available < nextSequence)
        {
            // MP: 앞선 시퀀스가 아직 게시되지 않음. 진전이 없을 때만 에스컬레이션 (§2.8)
            idleSpin.SpinOnce(sleep1Threshold: -1);
            continue;
        }
        idleSpin = default;

        long batchSize = available - nextSequence + 1;
        InvokeBatchStart(batchSize);

        // 배치를 처리한다. 예외가 나면 nextSequence 는 "실패한 시퀀스"에 멈춰 있고,
        // 정책 적용 후 while 을 다시 돌아 나머지 구간을 이어서 처리한다.
        while (nextSequence <= available)
        {
            if (ProcessBatchSegment(ref nextSequence, available, out var fault))
                continue;                              // Skip/Retry 후 재진입

            if (fault == EventExceptionAction.Halt)
            {
                _sequence.SetValue(nextSequence - 1);
                Halted?.Invoke(this, _lastFault);
                return;
            }
        }

        _sequence.SetValue(available);

        // 실패 스트릭 리셋: per-event 가 아니라 "예외 없이 완료된 배치"당 1회.
        // ResilientExceptionHandler 의 의미(연속 실패 감지)는 유지된다.
        _exceptionHandler.OnEventProcessed();

        Metrics?.RecordBatch(Name, batchSize);
        if (_signalNeeded)
            _barrier.WaitStrategy.SignalAllWhenBlocking();
    }
}

/// <summary>
/// [nextSequence, available] 구간을 try 1회로 처리한다.
/// 반환: true = 계속 진행(정상 완료 또는 Skip/Retry 처리됨), false = Halt 요청.
/// </summary>
private bool ProcessBatchSegment(ref long nextSequence, long available,
                                 out EventExceptionAction fault)
{
    fault = default;
    long current = nextSequence;

    try
    {
        // 분기를 루프 밖으로 — JIT 가 두 개의 타이트 루프를 각각 최적화한다.
        if (TypeIsClearable && ClearAfterProcess)
        {
            while (current <= available)
            {
                T evt = _ringBuffer[current];
                _eventHandler.OnEvent(evt, current, current == available);
                ((IClearable)evt).Clear();     // 정적으로 보장된 캐스트 — 검사 없음
                current++;
            }
        }
        else
        {
            while (current <= available)
            {
                _eventHandler.OnEvent(_ringBuffer[current], current, current == available);
                current++;
            }
        }

        nextSequence = current;
        return true;
    }
    catch (Exception ex)
    {
        // current 가 곧 실패한 시퀀스다.
        Metrics?.RecordHandlerError(Name);
        var action = SafeHandleException(ex, current, _ringBuffer[current]);

        switch (action)
        {
            case EventExceptionAction.Skip:
                nextSequence = current + 1;    // 실패 이벤트를 버리고 다음부터
                return true;
            case EventExceptionAction.Retry:
                nextSequence = current;        // 같은 이벤트부터 다시
                return true;
            default:
                _lastFault = ex;
                fault = EventExceptionAction.Halt;
                nextSequence = current;
                return false;
        }
    }
}
```

같은 재구성을 `ValueBatchEventProcessor<T>`와 `WorkProcessor<T>`(claim 루프는 유지, 처리부만)에도 적용한다.

**선택 사양 — 제네릭 핸들러 오버로드** (핸들러 타입이 많은 대형 앱용):

```csharp
// 공유 제네릭의 GDV 추측이 빗나가는 환경을 위한 탈출구.
// THandler 가 struct 면 인스턴스화가 공유되지 않아 OnEvent 가 무조건 인라인된다.
public sealed class BatchEventProcessor<T, THandler> : IEventProcessor
    where T : class
    where THandler : IEventHandler<T>
{
    private THandler _handler;    // struct 면 필드에 값으로 보관 (박싱 없음)
    // ... 루프 본문은 위와 동일, _eventHandler 대신 _handler 직접 호출
}

// DSL:
disruptor.HandleEventsWith<MyHandlerStruct>(new MyHandlerStruct(state));
```

> V3 실측이 보여주듯 **기본 경로에는 넣지 않는다.** 문서에 "언제 필요한가"(수십 종의 핸들러 타입 +
> 프로파일에서 `OnEvent` 콜스텁이 보일 때)를 명시하고 opt-in 으로만 제공한다.

#### 기대 효과 / 리스크

- 소비자 병목 시나리오에서 **+80~90%** (실측). 게시자 병목(현 Example 66M ops/s)에서는 영향이 작다 —
  효과가 나타나는 조건(느린 핸들러, 멀티캐스트 팬아웃, 배치 소비)을 문서에 명시할 것.
- 의미 변화 1: 예외 발생 시 `OnBatchStart` 는 재호출되지 않는다(배치 1회 계약 유지 — 오히려 현 구현보다 명확).
- 의미 변화 2: 실패 스트릭 리셋이 배치당 1회가 된다. `ResilientExceptionHandler` 의
  `maxConsecutiveFailures` 의미("연속 실패 N회")는 그대로 성립한다. XML 주석에 명시.
- 회귀 방지: `LifecycleTests` 의 Skip/Retry/Halt 3종 + `LosslessStressTests` 가 그대로 계약 테스트가 된다.
  추가로 "배치 중간 예외 후 나머지 구간이 이어서 처리되는가" 테스트 1건을 신설한다.

---

### 2.2 배치 핸들러 API — 가상 호출을 배치당 1회로

#### 현상

§2.1을 적용해도 이벤트당 1회의 (인라인 가능하지만 존재하는) 호출 경계는 남는다. 저널 직렬화,
브로드캐스트 인코딩, 통계 집계처럼 **배치 전체를 한 번에 다루는 것이 자연스러운 작업**은
"배치를 통째로 받는" API 가 있으면 호출 경계가 배치당 1회가 되고, 내부에서 SIMD·`CollectionsMarshal`·
일괄 직렬화가 가능해진다. LMAX 와 disruptor-net 모두 같은 이유로 배치 핸들러를 제공한다.

#### 구현

링 배열은 연속이지만 배치가 **랩 경계를 넘을 수 있으므로** 최대 2개 조각으로 나눠 전달한다.

```csharp
// Disruptor/IBatchEventHandler.cs (신규)
public interface IBatchEventHandler<T> where T : class
{
    /// <summary>
    /// 연속된 이벤트 구간을 통째로 받는다. 배치가 링의 끝을 넘으면 두 번 호출된다
    /// (호출당 span 은 항상 연속 메모리).
    /// </summary>
    /// <param name="events">이벤트 span. 슬롯 참조를 span 수명 밖으로 유출하면 안 된다.</param>
    /// <param name="startSequence">events[0] 의 시퀀스</param>
    /// <param name="endOfBatch">이 조각이 배치의 마지막인가</param>
    void OnBatch(ReadOnlySpan<T> events, long startSequence, bool endOfBatch);

    void OnTimeout(long lastProcessedSequence) { }
    void OnStart() { }
    void OnShutdown() { }
}
```

```csharp
// RingBuffer<T> — 내부 배열 조각 접근 (internal, 프로세서 전용)
internal ReadOnlySpan<T> GetSegment(long startSequence, long endSequenceInclusive)
{
    int startIndex = (int)startSequence & _indexMask;
    int length = (int)(endSequenceInclusive - startSequence + 1);
    // 호출자가 랩 분할을 보장한다: startIndex + length <= 배열 길이
    return new ReadOnlySpan<T>(_entries, startIndex, length);
}
```

```csharp
// BatchEventProcessor 쪽 배치 분할 — 랩 경계에서 최대 2조각
private void DispatchBatch(IBatchEventHandler<T> handler, long from, long to)
{
    int bufferSize = _ringBuffer.BufferSize;
    long current = from;

    while (current <= to)
    {
        int startIndex = (int)current & (bufferSize - 1);
        // 이 조각이 링의 끝을 넘지 않는 최대 길이
        long segmentEnd = Math.Min(to, current + (bufferSize - startIndex) - 1);

        handler.OnBatch(_ringBuffer.GetSegment(current, segmentEnd),
                        current,
                        endOfBatch: segmentEnd == to);
        current = segmentEnd + 1;
    }
}
```

`ValueRingBuffer<T>` 에는 같은 원리로 **쓰기 가능한** `Span<T>` 를 제공한다 — 여기서 진짜 이득이 난다.

```csharp
public interface IValueBatchEventHandler<T> where T : struct
{
    void OnBatch(Span<T> events, long startSequence, bool endOfBatch);
    // ...
}

// 사용례 — 배치를 SIMD 로 집계
public void OnBatch(Span<MarketTick> events, long startSequence, bool endOfBatch)
{
    // 구조체 배열 조각이므로 Vector<T> / TensorPrimitives 로 바로 처리 가능
    _sum += TensorPrimitives.Sum(MemoryMarshal.Cast<MarketTick, long>(events));
}
```

DSL 은 `HandleEventsWith(IBatchEventHandler<T>)` 오버로드를 추가하고, 내부적으로 배치 디스패치
전용 프로세서(또는 어댑터)를 생성한다. 예외 처리: `OnBatch` 안에서 실패하면 실패 위치를 특정할 수
없으므로 **정책은 조각 단위**(Skip = 조각 전체 스킵)가 된다 — XML 주석에 명시하고, 이벤트 단위
정책이 필요한 핸들러는 기존 `IEventHandler<T>` 를 쓰게 안내한다.

#### 기대 효과 / 리스크

- 호출 경계 O(배치)→O(조각≤2). 저널 핸들러의 경우 "이벤트 N건 직렬화"를 한 함수 안에서 처리해
  버퍼 재사용·경계 검사 제거 효과까지 얻는다.
- 리스크: span 유출(슬롯 참조를 보관) — 문서화 + §3.7 analyzer 후보(DISR006).

---

### 2.3 Blocking 계열의 게시 경로 lock 제거 — 실측 근거 확보

#### 현상

`EVALUATION.md` §3.5에서 lost-wakeup 을 막기 위해 `_numWaiters` 최적화를 제거하고
"무조건 `lock + PulseAll`"을 채택했다. 안전하지만 이제 **게시 1건마다 lock 을 지불한다.**

실측 (P-B):

```
게시 1회당 신호 비용 (무경합, 50M회):
    lock + PulseAll                :  39.1 ns/op   ← 현재
    MemoryBarrier + waiters 검사   :   4.1 ns/op   ← 개선안 (약 1/10)

per-event 게시 유니캐스트 10M건 (ring 64K):
    YieldingWaitStrategy           : 50~60M ops/s
    BlockingWaitStrategy           :  8.6M ops/s   ← 6~7배 격차
```

배치 게시(`Publish(lo, hi)`)에서는 신호가 배치당 1회라 상각되지만, 게임 서버의 전형적 패턴
(패킷 1건 수신 → 1건 게시)은 per-event 게시이고 정확히 최악 조건을 밟는다.

#### 구현 — 대기자 카운터 + 전체 펜스, 그리고 이중 방어 유지

lost-wakeup 의 원인은 게시 측 `(A) 커서 쓰기 → (B) 대기자 읽기` 가 StoreLoad 재배치될 수 있다는
것이었다. **(A) 와 (B) 사이에 전체 펜스를 넣으면** 재배치가 차단된다. 논증:

- 소비자: `Interlocked.Increment(_waiters)` (전체 펜스) **후에** 조건 재확인 → `Wait`.
- 게시자: 커서 volatile write **후에** `Interlocked.MemoryBarrier()` (전체 펜스) → `_waiters` 읽기.
- 전체 펜스 두 개는 전역 순서를 가지므로 둘 중 하나는 반드시 성립한다:
  (i) 게시자의 펜스가 소비자의 increment 보다 먼저 완료 → 소비자의 재확인이 새 커서를 본다 → 대기하지 않는다.
  (ii) 소비자의 increment 가 먼저 → 게시자가 `_waiters > 0` 을 본다 → PulseAll 한다.
- 여기에 기존의 `Monitor.Wait(gate, 100ms)` 재확인을 **그대로 유지**해 이중 방어로 삼는다
  (논증 오류·플랫폼 특이 사항에 대한 보험. 비용은 유휴 시에만 발생).

```csharp
public sealed class BlockingWaitStrategy : IWaitStrategy
{
    private const int RecheckIntervalMs = 100;   // lost-wakeup 2차 방어 (유지)

    private readonly object _gate = new();
    private int _numWaiters;                      // lock 안에서만 증감

    public bool IsBlockingStrategy => true;

    public SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                                      CancellationToken cancellationToken)
    {
        long available = dependents.Value;
        if (available >= sequence)
            return available;

        lock (_gate)
        {
            _numWaiters++;                        // lock 진입 자체가 전체 펜스를 포함
            try
            {
                while ((available = dependents.Value) < sequence)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return SequenceWaitResult.Cancelled;

                    Monitor.Wait(_gate, RecheckIntervalMs);
                }
            }
            finally
            {
                _numWaiters--;
            }
        }
        return available;
    }

    public void SignalAllWhenBlocking()
    {
        // 호출자(Sequencer.Publish / Processor 시퀀스 갱신)는 이 호출 "직전에"
        // volatile write 를 이미 수행했다. 전체 펜스로 StoreLoad 재배치를 차단한 뒤
        // 대기자가 있을 때만 lock 을 진입한다.
        Interlocked.MemoryBarrier();

        if (Volatile.Read(ref _numWaiters) > 0)
        {
            lock (_gate)
            {
                Monitor.PulseAll(_gate);
            }
        }
    }
}
```

`TimeoutBlockingWaitStrategy` 에도 동일하게 적용한다 (Wait 타임아웃이 이미 있으므로 2차 방어는 내장).

#### 기대 효과 / 리스크

- 무경합 게시 신호 비용 39.1 → 4.1ns (실측). per-event 게시 Blocking 처리량이 Yielding 에
  근접할 것으로 예상 (예상 40M+ ops/s — 적용 후 P-B [3] 재실행으로 확정할 것).
- 리스크: 메모리 순서 논증이 정확해야 한다. 위 논증 + 100ms 백스톱 + `LosslessStressTests`
  (Blocking 변형 추가) + §3.8 Coyote 검증 대상 1순위로 지정한다.
- 유휴 wake-up 지연(p50 ~12μs)은 변하지 않는다 — 이 개선은 **게시 측** 비용에 관한 것.

---

### 2.4 백프레셔 대기 루프의 `Stopwatch.GetTimestamp()` 호출 절감

#### 현상

`SingleProducerSequencer.WaitForCapacity` / `MultiProducerSequencer.Next` 의 백프레셔 루프는
**반복마다** `Stopwatch.GetTimestamp()`(QueryPerformanceCounter, ~20ns + 시리얼라이징 성격)를 호출한다.
기본 백오프(`SpinOnlyBackoff`)는 `elapsedTicks` 를 **아예 사용하지 않는데도** 매번 계산한다.
백프레셔가 몇 번의 스핀으로 풀리는 흔한 경우, 이 QPC 비용이 해소 지연에 그대로 얹힌다.

#### 구현

시간이 실제로 필요한 시점(스톨 감지 임계 근접, 시간 기반 백오프)에만, 그리고 **128회에 1번만** 샘플링한다.

```csharp
// IProducerBackoff 에 정적 특성 추가
public interface IProducerBackoff
{
    /// <summary>elapsedTicks 를 실제로 사용하는가. false 면 호출자가 시간 계산을 생략할 수 있다.</summary>
    bool NeedsTime => false;

    bool Wait(ref ProducerBackoffState state, long elapsedTicks);
}

public sealed class TimeBoundedBackoff : IProducerBackoff
{
    public bool NeedsTime => true;
    // ...
}
```

```csharp
// MultiProducerSequencer.Next — 대기 분기 내부
if (wrapPoint > gatingSequence)
{
    cancellationToken.ThrowIfCancellationRequested();

    // 시간 샘플링: 필요한 소비자(시간 기반 백오프 / 스톨 감지)가 있고,
    // 128회마다 1번만. 처음 128회 스핀은 QPC 를 전혀 만지지 않는다.
    long elapsed = 0;
    if (_timeSensitive && (++spinCount & 127) == 0)
    {
        if (start == 0) start = Stopwatch.GetTimestamp();
        elapsed = Stopwatch.GetTimestamp() - start;
        ReportStallIfNeeded(elapsed, ref stallReported, gatingSequence, slowest);
    }

    if (!backoff.Wait(ref state, elapsed))
        throw new TimeoutException(...);

    continue;
}
```

`_timeSensitive` 는 생성자에서 1회 계산: `Options.Backoff.NeedsTime || Options.OnStall is not null`.
스톨 감지 해상도가 128 스핀 단위로 낮아지지만, 스톨 임계(기본 250ms) 대비 무시 가능하다.

#### 기대 효과 / 리스크

- 짧은 백프레셔(스핀 수 회 내 해소)의 해소 지연에서 QPC 왕복 제거. 스핀당 수십 ns 절감.
- 리스크: 없음에 가깝다. `TimeBoundedBackoff` 의 타임아웃 판정 해상도가 128 스핀 단위가 되는 것만 문서화.

---

### 2.5 메트릭·게이트 카운터의 공유 캐시라인 경합 제거

#### 현상

1. `PublishGate<T>` 는 게시 성공마다 `Interlocked.Increment(ref _published)` — **모든 프로듀서 스레드가
   같은 캐시라인을 두고 경합**한다. MP 8 프로듀서면 이 카운터 하나가 게시 경로의 스케일링을 깎는다.
2. `DisruptorMetrics.RecordProcessed/RecordBatch` 는 배치당 2회의 측정 + 매번 `KeyValuePair` 태그
   구성을 반복한다 (리스너가 붙으면 배치당 콜백 2회).

#### 구현

**(1) 스트라이프 카운터** — 코어 수만큼 패딩된 셀에 분산 기록하고, 읽을 때만 합산한다.

```csharp
// Disruptor/StripedCounter.cs (신규)
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// 쓰기 경합 없는 카운터. 각 셀이 캐시라인을 독점하고, 스레드는 자기 셀에만 기록한다.
/// 합산(Value)은 O(셀 수)이지만 읽기는 드물다(메트릭 폴링 주기).
/// </summary>
public sealed class StripedCounter
{
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct Cell
    {
        [FieldOffset(0)] public long Value;
    }

    private readonly Cell[] _cells;
    private readonly int _mask;

    public StripedCounter()
    {
        int stripes = (int)BitOperations.RoundUpToPowerOf2((uint)Environment.ProcessorCount);
        _cells = new Cell[stripes];
        _mask = stripes - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment()
    {
        // 스레드→셀 고정 매핑. 같은 스레드는 항상 같은 셀 → 경합은 해시 충돌 시에만.
        int cell = Environment.CurrentManagedThreadId & _mask;
        Interlocked.Increment(ref _cells[cell].Value);
    }

    public long Value
    {
        get
        {
            long sum = 0;
            for (int i = 0; i < _cells.Length; i++)
                sum += Volatile.Read(ref _cells[i].Value);
            return sum;
        }
    }
}
```

`PublishGate` 의 `_published/_dropped/_rejected` 를 `StripedCounter` 로 교체한다.

**(2) 태그 캐시 + 통합 기록** — 프로세서 생성 시 태그 배열을 1회 만들어 재사용하고,
`RecordProcessed` + `RecordBatch` 를 하나로 합친다.

```csharp
// DisruptorMetrics — 핸들러별 사전 구성 기록기
public readonly struct HandlerRecorder
{
    private readonly Histogram<long> _batchSize;
    private readonly Counter<long> _processed;
    private readonly KeyValuePair<string, object?>[] _tags;   // 1회 구성, 재사용

    public void OnBatchCompleted(long batchSize)
    {
        _processed.Add(batchSize, _tags);
        _batchSize.Record(batchSize, _tags);
    }
}

// BatchEventProcessor 는 DisruptorMetrics 대신 HandlerRecorder? 를 보관
```

**(3) 샘플링 옵션** — 초고빈도 파이프라인용으로 `MetricsSampling = 1/N` 옵션을 추가해
N 배치당 1회만 기록한다(카운터는 N 을 곱해 보정).

#### 기대 효과 / 리스크

- MP 게시 경로에서 게이트 카운터로 인한 캐시라인 핑퐁 제거. 메트릭 on 상태의 상시 비용 절감.
- 리스크: `StripedCounter.Value` 는 순간 스냅샷이 아니다(셀 간 비원자) — 메트릭 용도로는 무해. 문서화.

---

### 2.6 게시 API 의 클로저 할당 함정 차단

#### 현상

가장 편해 보이는 API 가 할당을 유발한다:

```csharp
for (int i = 0; i < n; i++)
    ring.PublishEvent((evt, seq) => { evt.OrderId = i; });   // i 캡처 → 게시마다 클로저+델리게이트 할당!
```

"정상 경로 할당 0"이라는 라이브러리의 핵심 가치가 사용자 코드 한 줄로 무너진다.
`PublishEvent<TArg>(translator, arg)` 오버로드는 이미 있지만, 함정을 막는 장치가 없다.

#### 구현

1. **XML 주석 경고 + README 규칙**: `PublishEvent(Action<T,long>)` 문서에
   "캡처하는 람다는 게시마다 할당을 유발한다. `static` 람다 + `TArg` 오버로드를 사용하라" 명시.

```csharp
// 올바른 패턴 — static 람다는 캡처를 컴파일 오류로 만든다
ring.PublishEvent(static (evt, seq, arg) => { evt.OrderId = arg; }, i);
```

2. **인터페이스 translator 추가** — 델리게이트 자체를 없애는 경로:

```csharp
public interface IEventTranslator<T, in TArg>
{
    void Translate(T evt, long sequence, TArg arg);
}

public void PublishEvent<TTranslator, TArg>(TTranslator translator, TArg arg)
    where TTranslator : struct, IEventTranslator<T, TArg>   // struct → 인라인 + 할당 0
{
    long sequence = _sequencer.Next();
    try { translator.Translate(this[sequence], sequence, arg); }
    finally { _sequencer.Publish(sequence); }
}
```

3. **Analyzer 규칙 DISR003** (§3.7): `PublishEvent` 인자로 캡처하는 람다가 넘어오면 경고.

#### 기대 효과 / 리스크

- 게시 경로 할당 0 계약을 사용자 코드까지 확장. 리스크 없음.

---

### 2.7 `MaxBatchSize` — 멀티캐스트 공정성과 지연 상한

#### 현상

배치 효과는 처리량에는 축복이지만, 뒤처진 컨슈머가 깨어나면 **쌓인 전부를 한 배치로** 처리한다
(스트레스 테스트에서 maxBatch 8,191 관측). 그동안 그 컨슈머는 `OnTimeout` 도 받지 못하므로,
틱 드라이버를 겸하는 핸들러라면 **배치 하나가 틱을 밀어낼 수 있다.**

#### 구현

```csharp
// BatchEventProcessor
public int MaxBatchSize { get; init; } = int.MaxValue;   // 기본: 무제한 (현 동작 유지)

// ProcessEvents 에서:
long available = waitResult.AvailableSequence;
if (available - nextSequence + 1 > MaxBatchSize)
    available = nextSequence + MaxBatchSize - 1;         // 나머지는 다음 루프에서
```

DSL: `DisruptorOptions.MaxBatchSize` 로 전 프로세서 일괄 설정 + 핸들러별 오버라이드.
틱 핸들러 가이드: `MaxBatchSize = 링크기/4` 수준으로 잡아 "배치 사이마다" `TryAdvance` 기회를 확보.

#### 기대 효과 / 리스크

- 배치 상한 = 지연 상한. 틱 결합 핸들러의 최악 지연이 계산 가능해진다.
- 리스크: 상한이 너무 작으면 배치 효과(§EVALUATION 배치 커밋)가 줄어든다 — 기본값은 무제한 유지.

---

### 2.8 기타 마이크로 항목 (묶음)

| 항목 | 현상 | 수정 |
|---|---|---|
| 미게시 슬롯 대기 | `available < nextSequence` 시 `Thread.SpinWait(1)` 고정 — 클레임한 프로듀서가 선점당하면 컨슈머가 계속 최소 스핀 | §2.1 코드처럼 `SpinWait` 상태로 에스컬레이션(-1), 진전 시 리셋 |
| LOH/POH | 링 64K↑ 참조 배열(≥512KB)과 대형 값 배열은 LOH 로 — 조각화 관찰 대상 | `GC.AllocateArray<T>(len, pinned: true)` (POH) 옵션 제공. 수명이 프로세스와 같으므로 POH 적합 |
| `PublishedCursor` | O(링 스캔) 프로퍼티 — 모니터링 코드가 폴링하면 비쌈 | XML 주석에 비용 명시, 메트릭 게이지는 커서/최소게이팅만 사용 |
| `RecordProcessed` 이름 태그 | 문자열 태그 매 기록 — §2.5(2) 로 흡수 | — |
| `ISequencer` 디스패치 | `RingBuffer.Next/Publish` 가 인터페이스 호출. disruptor-net 은 `SequencerDispatcher` struct 로 타입 분기 디스패치 | **보류** — P-A 가 보여준 대로 PGO 가 단형 지점을 처리한다. 프로파일에서 콜스텁이 확인될 때만 도입 (측정 우선 원칙) |

---

## 3. 안정성 개선

### 3.1 저널 무결성 — 해시 + 길이 상한 + 단조 검증

#### 현상 (현재 포맷의 세 가지 구멍)

1. **길이 필드 무결성 없음**: 디스크 손상/토큰 워드 티어링으로 `payloadLength` 가 깨지면
   `new byte[length]` 가 **최대 2GB 할당 시도** → OOM 또는 GC 폭주. 검증이 `length < 0` 뿐이다.
2. **페이로드 무결성 없음**: 길이는 우연히 맞고 내용만 깨진 레코드는 **조용히 잘못된 상태를 재생**한다.
   결정론적 리플레이의 존재 이유(정확한 재현)를 부정하는 최악의 실패 모드다.
3. **시퀀스 단조 검증 없음**: 파일 이어붙임 실수·중복 쓰기를 감지하지 못한다.

#### 구현 — 레코드 포맷 v2

```
파일 헤더:  [magic "DJRN" 4B][version=2 4B][maxRecordLength 4B][reserved 4B]
레코드:     [payloadLength 4B][sequence 8B][timestampTicks 8B][payload NB][checksum 8B]
checksum = XxHash3(레코드 헤더 20B + payload)   ← 헤더까지 커버해 길이/시퀀스 위조도 잡는다
```

해시는 `System.IO.Hashing` 패키지의 **XxHash3** 를 사용한다 (CRC32C 대비 수 배 빠르고 64-bit,
하드웨어 의존 없음, MIT). 의존성 추가가 싫다면 `Sse42.Crc32` + ARM `Crc32` 폴백으로 자체 구현해도
되지만, 검증된 패키지를 권장한다.

```csharp
// JournalWriter v2 — Write
public void Write(long sequence, long timestampTicks, ReadOnlySpan<byte> payload)
{
    if (payload.Length > _maxRecordLength)
        throw new ArgumentException($"레코드가 상한({_maxRecordLength}B)을 초과합니다.");

    Span<byte> header = _headerBuffer;                       // 20B
    BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
    BinaryPrimitives.WriteInt64LittleEndian(header[4..], sequence);
    BinaryPrimitives.WriteInt64LittleEndian(header[12..], timestampTicks);

    _hash.Reset();                                           // XxHash3 인스턴스 재사용 (할당 0)
    _hash.Append(header);
    _hash.Append(payload);
    Span<byte> checksum = stackalloc byte[8];
    _hash.GetHashAndReset(checksum);

    _stream.Write(header);
    _stream.Write(payload);
    _stream.Write(checksum);
    _recordCount++;
}
```

```csharp
// JournalReader v2 — 읽기 검증 3종
public static IEnumerable<JournalRecord> Read(Stream stream, JournalReadOptions? options = null)
{
    options ??= JournalReadOptions.Default;
    // ... 파일 헤더에서 version, maxRecordLength 읽기 ...

    long lastSequence = long.MinValue;

    while (true)
    {
        if (!ReadExactly(stream, recordHeader)) yield break;         // 정상 EOF / 잘림

        int length = BinaryPrimitives.ReadInt32LittleEndian(recordHeader);
        long sequence = BinaryPrimitives.ReadInt64LittleEndian(recordHeader.AsSpan(4));

        // (1) 길이 상한 — 손상된 길이 필드로 인한 대형 할당 차단
        if ((uint)length > (uint)maxRecordLength)
        {
            if (options.Mode == JournalReadMode.TruncateAtCorruption) yield break;
            throw new InvalidDataException($"seq {sequence}: 레코드 길이 {length} 가 상한을 벗어남 (손상 의심).");
        }

        // ... payload + checksum 읽기 (잘리면 yield break) ...

        // (2) 해시 검증 — 내용 손상 감지
        if (computedHash != storedHash)
        {
            if (options.Mode == JournalReadMode.TruncateAtCorruption) yield break;
            throw new InvalidDataException($"seq {sequence}: 체크섬 불일치 (파일 오프셋 {offset}).");
        }

        // (3) 단조 검증 — 이어붙임/중복 감지
        if (sequence <= lastSequence)
            throw new InvalidDataException(
                $"시퀀스 역행: {lastSequence} → {sequence}. 저널 파일이 섞였거나 중복 기록되었습니다.");
        lastSequence = sequence;

        yield return new JournalRecord(sequence, ticks, payload.AsMemory(0, length));
    }
}

public enum JournalReadMode
{
    /// <summary>손상 지점에서 예외. 검증/감사 용도 기본값.</summary>
    ThrowAtCorruption,
    /// <summary>손상 지점에서 조용히 종료. 크래시 복구(마지막 유효 지점까지 재생) 용도.</summary>
    TruncateAtCorruption,
}
```

버전 1 파일은 리더가 `version==1` 이면 기존 로직으로 읽는다 (하위 호환).

#### 기대 효과 / 리스크

- 손상 파일이 유발할 수 있는 세 가지 사고(OOM, 무결성 없는 재생, 뒤섞인 재생)를 모두 차단.
- 쓰기 비용: XxHash3 는 ~30GB/s 급 — 저널 I/O 대비 무시 가능. `JournalTests` 에
  "1바이트 뒤집기 → 감지", "TruncateAtCorruption 복구" 테스트를 추가한다.

---

### 3.2 Windows 타이머 분해능 — 유휴 틱 정밀도 15.4ms → 1.5ms (실측)

#### 현상 (P-C 실측)

```
[1] 기본 타이머 분해능        OnTimeout   130회 / 2003ms → 실측 간격 15.40 ms  (요청 1ms)
[1] timeBeginPeriod(1) 적용   OnTimeout 1,314회 / 2001ms → 실측 간격  1.52 ms
```

`TimeoutBlockingWaitStrategy(1ms)` + `FixedTickDriver` 조합은 **이벤트가 흐르는 동안**은 정확하다
(배치 끝마다 `TryAdvance`). 그러나 **완전 유휴** 구간에서는 `Monitor.Wait(gate, 1)` 이 시스템 타이머
인터럽트(기본 15.625ms)에 양자화되어, 60Hz 틱(16.6ms)이 유휴 시 사실상 **틱당 1회의 기회**만 갖는다.
스케줄 편차가 겹치면 틱을 건너뛴다. 로비처럼 트래픽이 띄엄띄엄한 서버에서 실제로 문제가 된다.

#### 구현 1 — `HighResolutionTimerScope` (권장 기본)

```csharp
// Disruptor/Game/HighResolutionTimerScope.cs (신규)
using System.Runtime.InteropServices;

/// <summary>
/// 프로세스 수명 동안 Windows 시스템 타이머 분해능을 1ms 로 올린다.
/// 실측: 유휴 OnTimeout(1ms) 간격 15.40ms → 1.52ms.
/// </summary>
/// <remarks>
/// <b>트레이드오프</b>: 시스템 전역 설정이라 유휴 전력 소모가 늘어난다. 서버(상시 부하)에는
/// 무해하지만 노트북/클라이언트 빌드에는 넣지 말 것. Windows 11 은 포그라운드 부스트가 없는
/// 백그라운드 프로세스의 요청을 무시할 수 있다 — 서비스로 돌릴 때는 실측으로 확인하라.
/// 참조 카운트 방식이라 중첩 사용이 안전하다.
/// </remarks>
public sealed partial class HighResolutionTimerScope : IDisposable
{
    private static int _refCount;
    private bool _active;

    [LibraryImport("winmm.dll")] private static partial uint timeBeginPeriod(uint uMilliseconds);
    [LibraryImport("winmm.dll")] private static partial uint timeEndPeriod(uint uMilliseconds);

    public HighResolutionTimerScope()
    {
        if (!OperatingSystem.IsWindows())
            return;   // Linux 는 CONFIG_HIGH_RES_TIMERS 로 기본 고분해능 — no-op

        if (Interlocked.Increment(ref _refCount) == 1)
            _active = timeBeginPeriod(1) == 0;
        else
            _active = true;
    }

    public void Dispose()
    {
        if (_active && Interlocked.Decrement(ref _refCount) == 0)
            timeEndPeriod(1);
        _active = false;
    }
}

// 게임 서버 시작부:
using var timerScope = new HighResolutionTimerScope();   // 프로세스 수명 동안 유지
```

#### 구현 2 — 고분해능 대기 타이머 전략 (전역 설정이 싫은 경우)

Windows 10 1803+ 의 `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` 은 **프로세스 전역 설정 없이**
해당 대기만 고분해능으로 만든다. `TimeoutBlockingWaitStrategy` 의 변형으로 제공한다.

```csharp
// 개요: Monitor.Wait 대신
//   - 신호용 AutoResetEvent(SignalAllWhenBlocking 이 Set)
//   - CreateWaitableTimerExW(..., CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, ...)
//   - WaitForMultipleObjects(신호, 타이머) 로 "신호 또는 고분해능 타임아웃" 대기
// 주의: 커널 대기라 wake-up 지연이 Monitor 보다 약간 크다(수 μs). 유휴 정밀도가
// 필요한 틱 파이프라인 전용이며, 기본값은 여전히 TimeoutBlockingWaitStrategy.
public sealed class HighResTimeoutWaitStrategy : IWaitStrategy { /* P/Invoke 구현 */ }
```

#### 구현 3 — 문서화 (필수)

`FixedTickDriver` XML 주석과 README 주의사항에 추가:
"유휴 서버에서 정밀 틱이 필요하면 `HighResolutionTimerScope` 를 켜거나, 틱 파이프라인만
`PhasedBackoffWaitStrategy`(스핀 유지형)로 구성하라. 실측: 기본 15.4ms / 적용 후 1.5ms."

#### 기대 효과 / 리스크

- 유휴 구간 틱 정밀도 10배 개선 (실측). `FixedTickDriverTests` 에 유휴 정밀도 테스트 추가
  (단, 타이머 상태 의존이라 `[Trait("Category","TimerSensitive")]` 로 분리).
- 리스크: 전력 (문서화), Windows 11 백그라운드 정책 (문서화 + 실측 안내).

---

### 3.3 `ResilientExceptionHandler<T>.Default` 공유 상태 결함 (버그)

#### 현상

```csharp
public static ResilientExceptionHandler<T> Default { get; } = new(...);   // T 별 정적 싱글턴
```

`BatchEventProcessor` / `WorkProcessor` / `ValueBatchEventProcessor` 가 이 **공유 인스턴스**를
기본값으로 쓴다. `_consecutive` 실패 스트릭이 인스턴스 상태이므로:

- 같은 `T` 의 프로세서 A 가 실패를 쌓는 동안 프로세서 B 의 성공이 `OnEventProcessed()` 로 스트릭을
  리셋 → A 의 연속 실패 감지가 **영원히 발동하지 않을 수 있다** (무한 오류 루프 방치).
- 반대로 서로 다른 파이프라인의 실패가 합산되어 **엉뚱한 프로세서에서 Halt 가 반환**될 수도 있다.

#### 구현

```csharp
// 각 프로세서 생성자에서: 공유 Default 대신 전용 인스턴스
_exceptionHandler = ResilientExceptionHandler<T>.CreateDefault();

// ResilientExceptionHandler<T>
/// <summary>프로세서 전용 기본 핸들러. 실패 스트릭이 인스턴스 상태이므로 공유하면 안 된다.</summary>
public static ResilientExceptionHandler<T> CreateDefault()
    => new(static (ex, seq, evt) => Console.Error.WriteLine(
           $"[Disruptor][ERROR] seq={seq} event={evt}: {ex}"));

// 기존 Default 프로퍼티는 [Obsolete("실패 스트릭이 공유됩니다. CreateDefault() 를 사용하세요.")] 후 제거
```

회귀 테스트: 프로세서 2개, 하나만 항상 실패 → 실패 프로세서만 정확히 `maxConsecutiveFailures` 후
Halt 되고 다른 프로세서는 영향 없음을 단언.

---

### 3.4 스톨 리포트 rate-limit

#### 현상

`ReportStallIfNeeded` 의 `reported` 플래그는 **`Next()` 호출당** 리셋된다. 모든 게시가 250ms 씩
막히는 지속 정체에서는 게시 건수만큼 콜백이 발동한다 — 콜백이 로거라면 초당 수천 줄의 로그 폭주가
정체를 더 악화시킨다.

#### 구현

```csharp
// SequencerBase
private long _lastStallReportTicks;   // 시퀀서 수준의 마지막 보고 시각

protected void ReportStallIfNeeded(long elapsedTicks, ref bool reported, ...)
{
    if (reported || Options.OnStall is null || elapsedTicks <= _stallThresholdTicks)
        return;

    // 시퀀서 전체에서 초당 1회로 제한 (CAS 로 승자 1명만 보고)
    long now = Stopwatch.GetTimestamp();
    long last = Volatile.Read(ref _lastStallReportTicks);
    if (now - last < Stopwatch.Frequency)
        { reported = true; return; }                        // 이번 Next 에서는 침묵
    if (Interlocked.CompareExchange(ref _lastStallReportTicks, now, last) != last)
        { reported = true; return; }                        // 다른 스레드가 이미 보고

    reported = true;
    // ... 기존 StallInfo 구성/콜백 ...
}
```

`SequencerOptions.StallReportInterval`(기본 1초)로 조절 가능하게 한다.

---

### 3.5 수명주기 가드 (묶음)

| 시나리오 | 현재 동작 | 수정 |
|---|---|---|
| `Dispose()` 후 `Halt()` | `_halt.Cancel()` → `ObjectDisposedException` | `Halt` 에서 `if (_disposed) return;` + `Cancel` 을 try/catch(ODE) |
| `Dispose()` 2회 | `CancellationTokenSource.Dispose` 는 멱등이라 무해하나 프로세서 목록 재순회 | `Interlocked.Exchange(ref _disposed, 1)` 가드 |
| `Dispose()` 후 `Run()` | 취소된 토큰 등록 시도 → 예외 형태가 비직관적 | 명시적 `ObjectDisposedException(nameof(BatchEventProcessor<T>))` |
| `Shutdown()` 과 `Dispose()` 동시 호출 | `_started` CAS 로 이미 안전 | 테스트만 추가 |

```csharp
// BatchEventProcessor 공통 패턴
private int _disposed;

public void Halt()
{
    if (Volatile.Read(ref _disposed) != 0)
        return;                                   // 이미 정리됨 — 조용히 무시

    try { if (!_halt.IsCancellationRequested) _halt.Cancel(); }
    catch (ObjectDisposedException) { }           // Dispose 와의 경쟁 창 방어

    _barrier.WaitStrategy.SignalAllWhenBlocking();
}

public void Dispose()
{
    if (Interlocked.Exchange(ref _disposed, 1) != 0)
        return;
    _halt.Dispose();
}
```

---

### 3.6 `ShardedDisruptor` + `Block` 정책 조합 가드

#### 현상

샤드 핸들러가 `Halt` 로 정지하면 그 샤드의 게이팅이 멈추고, `BackpressurePolicy.Block` 으로
그 샤드에 게시하던 스레드는 **영구 대기**한다. `ProcessorHalted` 이벤트는 올라오지만
이미 블록된 스레드는 구할 수 없다.

#### 구현 — 3중 방어

```csharp
// (1) halted 샤드 마킹 + 게시 시 fail-fast
private volatile bool[] _shardHalted;    // Start 에서 초기화

// 생성자에서:
disruptor.ProcessorHalted += (p, ex) =>
{
    _shardHalted[shardIndex] = true;
    ProcessorHalted?.Invoke(p, ex);
};

public bool TryPublish(long key, ...)
{
    int shard = ShardOf(key);
    if (_shardHalted[shard])
        return false;                     // 죽은 샤드에 블록되지 않는다
    return _gates[shard].TryPublish(...);
}

// (2) Block 정책이라도 취소 토큰을 관통시켜 서버 종료 시 탈출 가능하게 (이미 지원 — 문서 강조)

// (3) Publish(무정책 오버로드)의 XML 주석에 경고:
//     "샤드 핸들러가 Halt 되면 이 호출은 영구 대기할 수 있다.
//      실서비스에서는 TryPublish(FailAfterTimeout) 를 사용하라."
```

추가로 `ShardedDisruptor.HealthSnapshot()` — 샤드별 `(utilization, halted, dropped)` 를
한 번에 반환하는 API 를 제공해 §4.4 헬스체크가 소비하게 한다.

---

### 3.7 Roslyn Analyzer 패키지 — 오용을 컴파일 타임에 차단

#### 왜 안정성 항목인가

이 라이브러리의 위험한 오용 4가지는 전부 **컴파일은 되지만 런타임에 조용히 데이터를 깨뜨린다**:

| ID | 규칙 | 심각도 | 현재 방어 |
|---|---|---|---|
| DISR001 | `IEventHandler.OnEvent` 등을 `async` 로 선언 | Error | DEBUG 런타임 검사뿐 (§HandlerValidation) |
| DISR002 | `Next()` 후 `Publish()` 가 `finally` 에 없음 | Warning | 없음 — 링 버퍼 영구 정지 원인 |
| DISR003 | `PublishEvent` 에 캡처 람다 전달 | Info | 없음 — 게시마다 할당 |
| DISR004 | 링 크기 상수가 2의 거듭제곱이 아님 | Error | 런타임 예외(늦음) |
| DISR005 | 배치 span(`OnBatch`)의 슬롯 참조를 필드에 보관 | Warning | 없음 |

컴파일 타임 진단은 안정성 장치인 동시에 **강력한 채택 포인트**다 — "설치하면 IDE 가 실수를 잡아준다"는
경험은 라이브러리 신뢰도를 크게 끌어올린다 (xUnit analyzer, Meziantou.Analyzer 의 선례).

#### 구현 — 프로젝트 구성과 DISR001 전체 구현

```xml
<!-- Disruptor.Analyzers/Disruptor.Analyzers.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>   <!-- analyzer 는 반드시 ns2.0 -->
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <!-- 본체 패키지에 analyzers/dotnet/cs 로 동봉 -->
    <None Include="$(OutputPath)\$(AssemblyName).dll" Pack="true"
          PackagePath="analyzers/dotnet/cs" Visible="false" />
  </ItemGroup>
</Project>
```

```csharp
// Disruptor.Analyzers/AsyncHandlerAnalyzer.cs — DISR001 완전 구현
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AsyncHandlerAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        id: "DISR001",
        title: "Disruptor 핸들러는 async 로 선언할 수 없습니다",
        messageFormat: "'{0}' 은(는) async 로 선언된 Disruptor 핸들러 메서드입니다. " +
                       "첫 await 에서 반환되어 이벤트 순서 보장이 깨지고 슬롯이 처리 도중 " +
                       "재사용됩니다. 느린 I/O 는 WorkerPool(IWorkHandler)로 분리하세요.",
        category: "Disruptor.Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: "https://github.com/jacking75/DisruptorCS#주의사항");

    private static readonly string[] HandlerInterfaces =
    {
        "Disruptor.IEventHandler`1",
        "Disruptor.IValueEventHandler`1",
        "Disruptor.Work.IWorkHandler`1",
        "Disruptor.IBatchEventHandler`1",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (!method.IsAsync)
            return;

        // 이 메서드가 핸들러 인터페이스 멤버의 구현인가?
        var containing = method.ContainingType;
        foreach (var iface in containing.AllInterfaces)
        {
            string metadataName = iface.OriginalDefinition.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat).TrimStart('g', 'l', 'o', 'b', 'a', ':');

            if (Array.IndexOf(HandlerInterfaces, iface.OriginalDefinition
                    .ContainingNamespace + "." + iface.OriginalDefinition.MetadataName) < 0)
                continue;

            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                var impl = containing.FindImplementationForInterfaceMember(member);
                if (SymbolEqualityComparer.Default.Equals(impl, method))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Rule, method.Locations[0], method.Name));
                    return;
                }
            }
        }
    }
}
```

DISR002 는 operation-tree 분석(`IInvocationOperation` 으로 `Next()` 호출을 찾고, 도미네이트하는
`try` 의 `finally` 에 대응 `Publish` 가 있는지 검사), DISR003 은 `PublishEvent` 인자의
`IAnonymousFunctionOperation.CapturedVariables` 검사로 구현한다. 각 규칙에 CodeFix
(DISR002 → try/finally 래핑, DISR003 → `TArg` 오버로드로 변환)를 붙이면 완성도가 크게 오른다.

테스트는 `Microsoft.CodeAnalysis.Testing`(`CSharpAnalyzerTest<TAnalyzer, TVerifier>`)로 작성한다.

---

### 3.8 장기 Soak/카오스 하니스 + Coyote 체계적 탐색

#### 현상

현재 스트레스 테스트는 회당 수 초 × 반복 실행이다. 다음은 그 방식으로 잡히지 않는다:
누적 카운터 오버플로 근처 거동, 수 시간 후의 미세 누수, 낮은 확률의 인터리빙(§2.3 의 펜스 논증 등).

#### 구현

**(1) Soak 하니스** — 별도 콘솔 프로젝트 `Disruptor.Soak`:

```csharp
// 개요: 종료 없이 무한 실행하며 주기적으로 불변식을 단언하는 시나리오 러너
//  - 시나리오: MP+작은 링(백프레셔 상시), 멀티캐스트+느린 소비자 1, Sharded 5천 룸,
//              랜덤 Halt/재구성(파이프라인 파괴·재생성 루프), 랜덤 예외 주입 핸들러
//  - 불변식: 게시수==소비수(주기 스냅샷), 시퀀스 단조, GC 할당 증가율 0 (GetTotalAllocatedBytes 기울기),
//            스레드 수 일정, 핸들 수 일정
//  - 크래시/불변식 위반 시: 전체 상태 덤프(시퀀스, 게이팅, 커서) 후 비정상 종료 코드
// 실행: dotnet run -c Release -- --hours 8 --seed 12345   (시드 고정 → 재현 가능)
```

**(2) Microsoft Coyote 체계적 동시성 테스트** — 스케줄러를 Coyote 가 제어해 인터리빙을 탐색한다.
전 코드에 적용하긴 어렵고, **작게 모델링한 대상**에 적용한다:

| 검증 대상 | 모델 |
|---|---|
| §2.3 fence 기반 신호 (1순위) | 게시자 1 + 소비자 1, 커서/waiters/pulse 만 남긴 축소판 — lost-wakeup 탐색 |
| `WorkProcessor` claim 루프 | 워커 2 + 시퀀스 3개 — 중복/유실 클레임 탐색 |
| `Start`/`Shutdown`/`Halt` 경쟁 | 상태 기계 3종 동시 호출 |

```xml
<PackageReference Include="Microsoft.Coyote" Version="1.7.*" />
<PackageReference Include="Microsoft.Coyote.Test" Version="1.7.*" />
```

```csharp
[Microsoft.Coyote.SystematicTesting.Test]
public static void BlockingSignalNeverLosesWakeup(ICoyoteRuntime runtime) { /* 축소 모델 */ }
// 실행: coyote test Disruptor.Soak.dll -m BlockingSignalNeverLosesWakeup -i 10000
```

> 주의: Coyote 는 `Interlocked.MemoryBarrier` 수준의 하드웨어 메모리 순서까지 모델링하지 않는다.
> 인터리빙(스케줄) 버그 탐색용이며, §2.3 의 메모리 순서 논증을 대체하지 않는다 — 병행 수단이다.

---

## 4. 매력·채택 전략 — "좋은 라이브러리"에서 "선택받는 라이브러리"로

기술적으로 훌륭한 라이브러리가 채택되지 않는 이유는 거의 항상 같다: **발견되지 않았거나,
10분 안에 내 문제를 풀어주지 못했거나, 믿음이 가지 않아서**다. 이 장은 그 세 가지를 순서대로 공략한다.

### 4.1 영어 우선 문서화 — 글로벌 노출의 전제조건

#### 현상

README·XML 주석·예제·평가 문서가 전부 한국어다. 한국어 문서는 이 라이브러리의 **차별점**이지만
(영어권 라이브러리에는 없는 자산), 영어 문서가 없으면 GitHub 트렌딩·검색·awesome 리스트·
NuGet 검색 어디에서도 글로벌 사용자에게 도달하지 못한다. GitHub 별과 NuGet 다운로드의
대부분은 영어권에서 나온다.

#### 구현

| 산출물 | 방침 |
|---|---|
| `README.md` | **영어를 기본**으로 전환. 상단에 `[한국어](README.ko.md)` 링크 |
| `README.ko.md` | 현 한국어 README 이동 — 삭제가 아니라 **병행 유지** (한국 게임서버 커뮤니티 공략 자산) |
| XML 문서 주석 | 공개 API 는 영어로 재작성 (IntelliSense 는 전 세계가 본다). 내부 구현 주석은 한국어 유지 가능 |
| `EVALUATION.md` / `IMPROVEMENTS.md` | 요약본만 영어 문서(`docs/en/`)로 발췌 — 전체 번역은 비용 대비 낮음 |
| 커밋 메시지 | 이후 영어 (오픈소스 히스토리 관례) |

영어 README 의 구조는 첫 화면 30초 안에 3가지가 보이게 한다:
**① 무엇이 다른가 한 문장** ("LMAX Disruptor for C# with a game-server layer: sharding,
fixed-tick integration, deterministic journal/replay") — **② 숫자** (69M ops/s, 0 B allocated,
0 lost under backpressure) — **③ 30줄 quick start**.

### 4.2 `EventPoller<T>` — 스레드 없는 폴링 소비 (킬러 기능)

#### 왜 이것이 채택을 부르는가

현재 모델은 "핸들러당 전용 스레드"다. 그런데 게임 개발의 현실은 **이미 메인 루프가 있다**:
Unity 의 `Update()`, 자체 서버의 프레임 루프, 심지어 SDL/게임엔진 틱. 이들이 원하는 것은
"내 루프의 이 지점에서, 지금까지 쌓인 이벤트를 내 스레드로 당겨오기"다. LMAX 의 `EventPoller`
가 정확히 이것이고, 이 라이브러리의 타깃(게임 서버)에는 스레드 소유 모델보다 오히려 더 자연스럽다.
**"Disruptor 를 쓰려면 스레드 모델을 바꿔야 한다"는 진입 장벽을 제거하는 기능**이다.

#### 구현 (전체)

```csharp
// Disruptor/EventPoller.cs (신규)
namespace Disruptor;

public enum PollState
{
    /// <summary>이벤트를 1건 이상 처리했다.</summary>
    Processing,
    /// <summary>처리할 이벤트가 없다.</summary>
    Idle,
}

/// <summary>
/// 전용 스레드 없이 호출자의 스레드에서 이벤트를 당겨 처리하는 폴러.
/// 게임 메인 루프·Unity Update()·기존 프레임 루프와의 통합 지점이다.
/// </summary>
/// <remarks>
/// <b>스레드 안전성</b>: 하나의 폴러는 하나의 스레드에서만 Poll 해야 한다
/// (폴러의 시퀀스가 곧 소비 진행 상태다). 여러 스레드가 나눠 처리하려면 WorkerPool 을 쓰라.
/// <para>
/// 폴러도 게이팅에 등록되므로, <b>폴링을 멈추면 링 버퍼가 차고 프로듀서가 막힌다.</b>
/// 더 이상 쓰지 않는 폴러는 반드시 Dispose 로 게이팅에서 제거해야 한다.
/// </para>
/// </remarks>
public sealed class EventPoller<T> : IDisposable where T : class
{
    /// <summary>이벤트 1건 처리. false 를 반환하면 이번 Poll 을 중단한다(나머지는 다음 Poll).</summary>
    public delegate bool Handler(T evt, long sequence, bool endOfBatch);

    private readonly RingBuffer<T> _ringBuffer;
    private readonly DependentSequenceGroup _dependents;
    private readonly Sequence _sequence = new();
    private bool _disposed;

    internal EventPoller(RingBuffer<T> ringBuffer, params Sequence[] dependentSequences)
    {
        _ringBuffer = ringBuffer;
        _dependents = new DependentSequenceGroup(
            cursorOf(ringBuffer), dependentSequences);          // 내부 접근자 사용
        _sequence.SetValue(_ringBuffer.Cursor);                 // 생성 시점 이후 이벤트만
        _ringBuffer.AddGatingSequences(_sequence);
    }

    public Sequence Sequence => _sequence;

    /// <summary>
    /// 지금 소비 가능한 이벤트를 처리한다. 대기하지 않는다.
    /// </summary>
    /// <param name="onEvent">이벤트 콜백</param>
    /// <param name="maxEvents">
    /// 이번 Poll 에서 처리할 최대 건수. 프레임 예산을 지키는 장치다 —
    /// 밀린 10만 건을 한 프레임에 다 처리해 스파이크를 만들지 않도록 기본값을 제한한다.
    /// </param>
    public PollState Poll(Handler onEvent, int maxEvents = 256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onEvent);

        long next = _sequence.Value + 1L;
        long limit = _dependents.Value;                         // 커서 또는 상위 핸들러
        if (limit < next)
            return PollState.Idle;

        // MP: 연속 게시 상한까지만
        long available = _ringBuffer.GetHighestPublishedSequence(next, limit);
        if (available < next)
            return PollState.Idle;

        long end = Math.Min(available, next + maxEvents - 1);
        long current = next;
        try
        {
            while (current <= end)
            {
                if (!onEvent(_ringBuffer[current], current, current == end))
                {
                    current++;                                  // 이 이벤트까지는 처리된 것
                    break;
                }
                current++;
            }
        }
        finally
        {
            // 예외가 나도 처리 완료분까지는 반드시 반영 — 게이팅이 멈추지 않게
            _sequence.SetValue(current - 1);
        }

        return PollState.Processing;
    }

    /// <summary>남은 이벤트 수 추정 (모니터링용).</summary>
    public long Backlog => Math.Max(0, _dependents.Value - _sequence.Value);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ringBuffer.RemoveGatingSequence(_sequence);            // 게이팅 해제 — 필수
    }
}
```

```csharp
// RingBuffer<T> 에 팩토리 추가
/// <summary>스레드 없는 폴링 소비자를 만든다. 게임 메인 루프 통합용.</summary>
public EventPoller<T> NewPoller(params Sequence[] dependentSequences)
    => new(this, dependentSequences);
```

사용 예 (게임 메인 루프):

```csharp
var poller = ring.NewPoller();

while (running)                                   // 기존 메인 루프 — 스레드 모델 변경 없음
{
    // 프레임당 최대 512건, 초과분은 다음 프레임으로 (스파이크 방지)
    poller.Poll(static (cmd, seq, eob) => { world.Apply(cmd); return true; }, maxEvents: 512);

    world.Simulate(dt);
    world.Render();
}
```

주의점 구현: `DependentSequenceGroup` 의 커서 접근을 위해 `RingBuffer` 에
`internal Sequence CursorSequence` 접근자가 필요하다(현재 `ISequencer` 내부에 있음 — internal 노출).
테스트: 유실 없음 / maxEvents 상한 / Dispose 후 게이팅 해제 / 예외 시 진행 반영 4종.

### 4.3 Async 브리지 — .NET 생태계의 기대치 충족

#### 현상

.NET 개발자의 기본 어휘는 `async/await` 다. "핸들러에서 await 금지"는 올바른 제약이지만,
**경계에서의 통합 수단**이 없으면 후보에서 제외당한다. disruptor-net 도 같은 이유로
`AsyncWaitStrategy` + `IAsyncBatchEventHandler` 를 제공한다.

#### 구현 — 2단계

**1단계 (저비용, 즉시): `IAsyncEnumerable` 브리지** — 순서 보장이 필요 없는 *말단* 소비자용.

```csharp
// Disruptor/AsyncBridge.cs (신규) — 폴러 기반이라 코어를 건드리지 않는다
public static class AsyncBridge
{
    /// <summary>
    /// 링 버퍼의 이벤트를 IAsyncEnumerable 로 노출한다.
    /// <b>스냅샷 복사가 발생한다</b> (슬롯 참조를 await 너머로 유출할 수 없으므로).
    /// 성능 경로가 아니라 통합 경로다 — 대시보드, 어드민 스트림, 테스트에 쓰라.
    /// </summary>
    public static async IAsyncEnumerable<TSnapshot> ReadAllAsync<T, TSnapshot>(
        this RingBuffer<T> ring,
        Func<T, TSnapshot> snapshot,                     // 슬롯 → 불변 스냅샷 복사
        TimeSpan? pollInterval = null,
        [EnumeratorCancellation] CancellationToken ct = default) where T : class
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(1);
        using var poller = ring.NewPoller();
        var buffer = new List<TSnapshot>(64);

        while (!ct.IsCancellationRequested)
        {
            buffer.Clear();
            poller.Poll((evt, seq, eob) => { buffer.Add(snapshot(evt)); return true; });

            if (buffer.Count == 0)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                continue;
            }

            foreach (var item in buffer)
                yield return item;
        }
    }
}
```

**2단계 (본격): `AsyncWaitStrategy` + `AsyncBatchEventProcessor`** — 스레드 대신
`ValueTask` 로 대기하는 소비자. 설계 요점만 기록한다 (구현 20h 규모):

```
- AsyncWaitStrategy : IWaitStrategy
    · WaitFor 동기 경로는 기존과 동일 (동기 프로세서와 혼용 가능해야 함)
    · WaitForAsync(sequence, dependents, ct) → ValueTask<SequenceWaitResult>
    · 내부: 대기자 큐(ConcurrentQueue<ManualResetValueTaskSourceCore 래퍼>).
      SignalAllWhenBlocking 이 큐를 비우며 SetResult — 게시 스레드에서 continuation 이
      인라인 실행되지 않도록 RunContinuationsAsynchronously 의미로 TrySetResult 를
      ThreadPool 로 넘긴다 (게시 지연 보호가 최우선).
- AsyncBatchEventProcessor<T>
    · Run 대신 RunAsync(ct). 배치 수신 → await handler.OnBatchAsync(batch...) → 시퀀스 전진.
    · 순서 보장: await 하는 동안 시퀀스를 전진시키지 않으므로 배치 단위 순서는 유지된다.
      (배치 내부 병렬화는 핸들러 책임 — 문서화)
- 명시적 포지셔닝: async 소비자는 "저널 뒤의 느린 팬아웃"용. 코어 파이프라인은 동기 유지.
```

### 4.4 Microsoft.Extensions 통합 패키지 — ASP.NET Core 진입 장벽 제거

#### 현상

.NET 서버 개발자의 절대다수는 Generic Host 위에 산다. 그들에게 "Thread 를 직접 만들고
Shutdown 을 직접 불러라"는 이질적이다. `services.AddDisruptor(...)` 한 줄이 있고 없고가
평가 단계 통과를 가른다.

#### 구현 — 별도 패키지 `DisruptorCS.Extensions.Hosting`

```csharp
// ServiceCollectionExtensions.cs
public static class DisruptorServiceCollectionExtensions
{
    public static IServiceCollection AddDisruptor<T>(
        this IServiceCollection services,
        Action<DisruptorBuilder<T>> configure) where T : class
    {
        services.AddSingleton(sp =>
        {
            var builder = new DisruptorBuilder<T>(sp);
            configure(builder);
            return builder.Build();               // Dsl.Disruptor<T> 구성 (Start 는 아직)
        });

        // RingBuffer 를 바로 주입받아 게시할 수 있게
        services.AddSingleton(sp => sp.GetRequiredService<Dsl.Disruptor<T>>().RingBuffer);

        // Host 수명주기에 Start/Shutdown 을 결속
        services.AddHostedService<DisruptorHostedService<T>>();
        return services;
    }
}

// DisruptorBuilder<T> — DI 에서 핸들러를 해석하는 빌더
public sealed class DisruptorBuilder<T> where T : class
{
    public DisruptorBuilder<T> WithRingSize(int size);
    public DisruptorBuilder<T> WithProducerType(ProducerType type);
    public DisruptorBuilder<T> WithWaitStrategy(Func<IWaitStrategy> factory);
    public DisruptorBuilder<T> WithOptions(Func<DisruptorOptions, DisruptorOptions> mutate);

    /// <summary>핸들러를 DI 컨테이너에서 해석한다 — 핸들러가 로거·DB 컨텍스트를 주입받을 수 있다.</summary>
    public DisruptorBuilder<T> HandleEventsWith<THandler>() where THandler : class, IEventHandler<T>;
    public DisruptorBuilder<T> Then<THandler>() where THandler : class, IEventHandler<T>;
    public DisruptorBuilder<T> ThenWorkerPool<TWorker>(int workers) where TWorker : class, IWorkHandler<T>;
}

// DisruptorHostedService<T> — 수명주기 결속
internal sealed class DisruptorHostedService<T> : IHostedService where T : class
{
    private readonly Dsl.Disruptor<T> _disruptor;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DisruptorHostedService<T>> _logger;

    public Task StartAsync(CancellationToken ct)
    {
        _disruptor.ProcessorHalted += (p, ex) =>
        {
            // 프로세서가 죽으면 곧 프로듀서가 막힌다 — 호스트에 치명 상황을 즉시 알린다
            _logger.LogCritical(ex, "Disruptor '{Pipeline}' 프로세서 '{Processor}' 정지. 드레인 필요.",
                                _disruptor.Name, p.Name);
            _lifetime.StopApplication();          // 기본 정책: 계획 종료 (옵션으로 재구성 선택)
        };

        _disruptor.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _disruptor.Shutdown();                    // 드레인 포함
        return Task.CompletedTask;
    }
}
```

```csharp
// DisruptorHealthCheck.cs — IHealthCheck 구현
public sealed class DisruptorHealthCheck<T> : IHealthCheck where T : class
{
    private readonly Dsl.Disruptor<T> _disruptor;
    private readonly double _degradedUtilization;   // 기본 0.7
    private readonly double _unhealthyUtilization;  // 기본 0.9

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        double utilization = _disruptor.RingBuffer.Utilization;
        bool anyHalted = _disruptor.Processors.Any(p => !p.IsRunning);

        var data = new Dictionary<string, object>
        {
            ["utilization"] = utilization,
            ["remaining"] = _disruptor.RingBuffer.RemainingCapacity,
            ["cursor"] = _disruptor.RingBuffer.Cursor,
        };

        if (anyHalted)
            return Task.FromResult(HealthCheckResult.Unhealthy("프로세서 정지", data: data));
        if (utilization >= _unhealthyUtilization)
            return Task.FromResult(HealthCheckResult.Unhealthy($"링 사용률 {utilization:P0}", data: data));
        if (utilization >= _degradedUtilization)
            return Task.FromResult(HealthCheckResult.Degraded($"링 사용률 {utilization:P0}", data: data));

        return Task.FromResult(HealthCheckResult.Healthy(data: data));
    }
}
```

최종 사용자 경험:

```csharp
builder.Services.AddDisruptor<GameCommand>(d => d
    .WithRingSize(65536)
    .WithProducerType(ProducerType.Multi)
    .HandleEventsWith<JournalHandler>()
    .Then<GameLogicHandler>()
    .ThenWorkerPool<DbWriteWorker>(workers: 4));

builder.Services.AddHealthChecks()
    .AddCheck<DisruptorHealthCheck<GameCommand>>("disruptor");
```

주의: 이 패키지는 **본체와 분리**한다 — 코어는 의존성 0 을 유지한다(임베디드·AOT·Unity 친화).

### 4.5 정직한 비교 벤치마크 공개 — 신뢰 구축의 핵심

#### 현상

성능 라이브러리의 README 에서 사람들이 가장 먼저 찾는 것은 **경쟁자와의 비교 숫자**이고,
가장 빨리 신뢰를 잃는 방법은 **자기에게 유리한 조건만 보여주는 것**이다. 현재 벤치마크 프로젝트는
`Channel<T>` 비교가 있지만 결과가 공개물로 정리되어 있지 않고, 정작 모두가 궁금해할
**disruptor-net 과의 비교가 없다.**

#### 구현

1. `Disruptor.Benchmarks` 에 disruptor-net 참조를 추가하고 동일 시나리오를 구성한다:

| 시나리오 | 비교 대상 | 지표 |
|---|---|---|
| Unicast 1P1C / Multicast 1P3C / Pipeline / MP 백프레셔 | DisruptorCS · disruptor-net · `Channel<T>` · `ConcurrentQueue` | 처리량 + **p99.9 / max 지연** + 할당 |
| 게임 서버 시나리오 (이 라이브러리만 가능한 것) | Sharded 5천 룸, 틱 통합, 저널 on/off 오버헤드 | 동일 |

2. **질 것으로 예상되는 항목을 숨기지 않는다.** disruptor-net 은 수년간 최적화된 구현이다 —
   순수 unicast 처리량에서 밀리면 그대로 게시하고, "우리의 가치는 게임 서버 레이어 + 진단 +
   백프레셔 정책"이라는 포지셔닝(§4.8)과 연결한다. 정직한 열세 공개는 나머지 숫자의 신뢰를 산다.

3. 결과 산출 자동화 (CI 미사용 방침이므로 **로컬 스크립트**):

```powershell
# tools/run-benchmarks.ps1 — 결과 md/차트를 docs/benchmarks/ 에 갱신
dotnet run --project Disruptor.Benchmarks -c Release -- --filter '*' `
    --exporters GitHub JSON --artifacts docs/benchmarks/raw
dotnet run --project Disruptor.Benchmarks -c Release -- latency `
    | Tee-Object docs/benchmarks/latency.txt
# BenchmarkDotNet 의 GitHub exporter md 를 docs/benchmarks/README.md 로 취합
```

4. README 에는 표 3개만 발췌하고 전체는 `docs/benchmarks/` 링크. 측정 환경·커밋 해시·날짜를 반드시 병기.

### 4.6 샘플 갤러리 — "내 문제와 같은 예제"

#### 현상

`Example/Program.cs` 는 기능 시연 10종으로 훌륭하지만, 채택 결정자는 **자기 도메인 모양의
end-to-end 샘플**을 찾는다. "예제 4번이 틱이었지"보다 "채팅 서버 샘플을 클론해서 돌려봤다"가 강하다.

#### 구현 — `samples/` 디렉터리, 각각 독립 실행 가능

| 샘플 | 보여주는 것 | 구성 |
|---|---|---|
| `samples/ChatBroadcast` | TCP 채팅 브로드캐스트 — 수신(MP) → 검열 핸들러 → 브로드캐스트(배치 flush) | 콘솔 서버 + 부하 클라이언트, `OnBatchStart`/`endOfBatch` 배칭 효과를 수치로 출력 |
| `samples/MatchSimulation` | Sharded 룸 + 60Hz 틱 + 저널 → **크래시 후 리플레이 복구 시연** | 실행 중 Ctrl+C → 재시작하면 저널에서 상태 복원 |
| `samples/ReplayInspector` | 저널 파일 CLI 뷰어 (`dump`/`verify`/`replay --until-seq`) | §3.1 무결성 검증의 소비자이자 운영 도구 |
| `samples/AspNetCoreTelemetry` | §4.4 DI 통합 + HealthCheck + OpenTelemetry 메트릭 대시보드 | `docker compose up` 으로 Prometheus/Grafana 까지 |
| `samples/GameLoopPoller` | §4.2 EventPoller 를 기존 게임 루프에 통합 | 스레드 0개 추가로 Disruptor 소비 |

각 샘플 README 첫 줄은 "이 샘플이 답하는 질문"으로 시작한다
(예: *"패킷 브로드캐스트에서 syscall 을 어떻게 1/N 로 줄이는가?"*).

### 4.7 NuGet 배포 + 브랜딩 — 설치할 수 있어야 존재하는 것

#### 구현 체크리스트 (CI 미사용 — 수동 절차를 문서화)

```markdown
# docs/RELEASE.md — 릴리스 체크리스트 (수동)
1. [ ] dotnet build DisruptorCS.slnx -c Release        → 경고 0
2. [ ] dotnet test Disruptor.Tests -c Release          → 전체 통과
3. [ ] dotnet test Disruptor.Tests -c Debug            → 전체 통과 (진단 검사)
4. [ ] for /l %i in (1,1,20) do dotnet test -c Release --filter Category=Stress  → 20/20
5. [ ] dotnet run --project Example -c Release         → "유실 0 / 체크섬 일치" 확인
6. [ ] tools/run-benchmarks.ps1                        → p99.9 기준선 대비 회귀 없음
7. [ ] CHANGELOG.md 에 항목 추가 (Keep a Changelog 형식)
8. [ ] git tag v0.x.y && git push --tags
9. [ ] dotnet pack Disruptor/Disruptor.csproj -c Release -o artifacts
10. [ ] dotnet nuget push artifacts/*.nupkg --source nuget.org --api-key ...
11. [ ] GitHub Release 작성 (태그 기반, CHANGELOG 발췌)
```

버전은 **MinVer**(git tag 기반 SemVer)로 자동화한다 — 수동 릴리스일수록 버전 실수 방지 장치가 필요하다:

```xml
<PackageReference Include="MinVer" Version="5.*" PrivateAssets="all" />
```

브랜딩 최소 세트:
- 로고 1개 (`docs/logo.png`, 128×128 — NuGet `<PackageIcon>` + GitHub 소셜 프리뷰에 재사용)
- README 상단 배지: NuGet 버전 · 다운로드 · 라이선스 · .NET 버전 (shields.io — CI 배지는 없으므로 넣지 않는다)
- `CHANGELOG.md`, `CONTRIBUTING.md`, 이슈 템플릿 2종(버그/제안). 저장소가 "관리되고 있다"는 신호가
  별보다 채택에 중요하다.

### 4.8 포지셔닝 — disruptor-net 과의 관계를 정면으로 다룬다

#### 현상

.NET 에서 Disruptor 를 검색하면 disruptor-net 이 먼저 나온다. 이 질문을 회피하면
"몰라서 또 만든 것"으로 보이고, 정면으로 다루면 "알고도 만든 이유"가 정체성이 된다.

#### 구현 — README 에 정직한 비교 섹션

```markdown
## DisruptorCS vs disruptor-net

disruptor-net 은 성숙하고 훌륭한 범용 Disruptor 구현입니다. **범용 링 버퍼가 필요하면
disruptor-net 을 먼저 검토하세요.** DisruptorCS 는 다른 질문에 답합니다 —
"게임 서버를 만들 때 링 버퍼 *위에* 필요한 것들까지 한 상자에 담으면 어떤 모양인가?"

| | disruptor-net | DisruptorCS |
|---|---|---|
| 성숙도 / 실전 이력 | ✅ 수년간 검증 | 신생 (60 테스트, 스트레스 반복 검증) |
| 순수 처리량 최적화 | ✅ 극한 튜닝 | 대등 지향 (벤치마크 공개) |
| 룸/세션 샤딩 | — | ✅ ShardedDisruptor (5천 룸 → 8스레드) |
| 고정 틱 통합 | — | ✅ FixedTickDriver + OnTimeout |
| 결정론적 저널/리플레이 | — | ✅ 무결성 검증 포함 |
| 백프레셔 정책 (드롭/거절) | — | ✅ PublishGate 3정책 |
| 운영 진단 (스톨 원인 지목, lag 게이지) | 일부 | ✅ 내장 (OpenTelemetry 호환) |
| 오용 컴파일 타임 검출 | — | ✅ Analyzer (DISR001~) |
| 한국어 문서 | — | ✅ 전체 |
```

한 문장 정체성: **"Disruptor 패턴의 게임 서버 배포판(distribution)"** — 코어를 재발명한 것이
아니라, 코어 위에서 게임 서버가 매번 다시 만드는 것들을 표준화했다는 서사.

### 4.9 기능 완성도 격차 (신뢰를 깎는 비대칭 제거)

첫 사용자가 "왜 이건 되고 저건 안 되지?"를 만나면 신뢰가 깎인다. 현재 비대칭:

| 격차 | 내용 | 조치 |
|---|---|---|
| `ValueDisruptor` 에 WorkerPool 없음 | 참조형만 `ThenWorkerPool` 가능 | `ValueWorkProcessor` 추가 또는 문서에 제약 명시 |
| DSL `And()` 부재 | "따로 등록한 A, B 를 모두 기다리는 C" 표현 불가 | `EventHandlerGroup.And(EventHandlerGroup)` 추가 — 시퀀스 배열 병합만으로 구현 가능 (2h) |
| `EventScope` 참조형/값형 API 미세 차이 | `Event()` 메서드 vs `Event` 프로퍼티 | 명명 통일 (breaking 전 0.x 에서 정리) |
| `IExceptionHandler` 배치 API 미대응 | §2.2 도입 시 조각 단위 정책 명세 필요 | §2.2 에서 함께 정의 |
| XML 문서 언어 혼재 예정 | §4.1 전환기 | 공개 API 부터 일괄 전환 |

### 4.10 커뮤니티 전파 체크리스트

순서가 중요하다 — **4.1(영어)·4.5(벤치마크)·4.7(NuGet) 이 끝나기 전에는 전파하지 않는다.**
첫인상은 한 번뿐이다.

1. `awesome-dotnet`, `awesome-dotnet-performance` PR (NuGet 게시 후)
2. 기술 아티클 2편: ① "C# do…while 의 continue 가 백프레셔를 죽인 방법" (실화 디버깅 스토리 —
   EVALUATION §3.1 은 그 자체로 바이럴 소재다), ② "게임 서버에 Disruptor 패턴 적용하기: 샤딩·틱·리플레이"
   — dev.to / Reddit r/dotnet / Hacker News
3. 한국 채널: 한국 게임서버 개발자 커뮤니티, NDC/지스타 발표 제안 — **한국어 완전 문서화가 무기가 되는 시장**
4. GitHub Discussions 활성화 + `good first issue` 라벨 (Analyzer 규칙 추가는 좋은 기여 입문 과제다)

---

## 5. 우선순위 로드맵

**선정 기준**: (1) 정확성·데이터 보호가 먼저, (2) 실측으로 효과가 증명된 것이 추측보다 먼저,
(3) 채택 파이프라인(발견→평가→도입)의 병목 순.

### Wave 1 — 결함 성격 + 실측 근거 확보분 (약 30h)

| 항목 | 절 | 공수 |
|---|---|---:|
| `ResilientExceptionHandler.Default` 공유 상태 수정 | §3.3 | 1h |
| 저널 무결성 (해시/상한/단조) | §3.1 | 8h |
| 소비 루프 배치 재구성 (+87% 실측) | §2.1 | 8h |
| Blocking 게시 lock 제거 (10배 실측) + Coyote 모델 | §2.3, §3.8 일부 | 8h |
| 스톨 rate-limit + 수명주기 가드 | §3.4, §3.5 | 3h |
| `HighResolutionTimerScope` + 문서 | §3.2 | 4h |

### Wave 2 — 채택 전제조건 (약 50h)

| 항목 | 절 | 공수 |
|---|---|---:|
| 영어 우선 문서화 | §4.1 | 16h |
| NuGet 배포 + 릴리스 절차 + 브랜딩 | §4.7 | 8h |
| 비교 벤치마크 (disruptor-net 포함) 공개 | §4.5 | 12h |
| `EventPoller` | §4.2 | 10h |
| 포지셔닝 문서 + DSL `And()` | §4.8, §4.9 | 6h |

### Wave 3 — 생태계 통합 (약 60h)

| 항목 | 절 | 공수 |
|---|---|---:|
| Microsoft.Extensions 통합 패키지 | §4.4 | 12h |
| Roslyn Analyzer (DISR001~003 + CodeFix) | §3.7 | 24h |
| 배치 핸들러 API | §2.2 | 10h |
| 메트릭/카운터 경합 + QPC 절감 + 클로저 가드 | §2.4~2.6 | 8h |
| Sharded 가드 + `MaxBatchSize` | §3.6, §2.7 | 6h |

### Wave 4 — 심화 (약 55h)

| 항목 | 절 | 공수 |
|---|---|---:|
| Async 브리지 2단계 | §4.3 | 20h |
| 샘플 갤러리 4종 | §4.6 | 16h |
| Soak 하니스 + Coyote 확대 | §3.8 | 16h |
| 커뮤니티 전파 | §4.10 | (지속) |

---

## 6. 결론

**성능**: 가장 큰 여유는 화려한 기법이 아니라 소비 루프의 구조에 있었다(+87% 실측).
struct 제네릭 디버추얼라이제이션이 불필요하다는 반증 데이터는 이 프로젝트의 원칙 —
**추측하지 말고 측정한다** — 이 실제로 작동함을 보여준다. Blocking 게시 lock(10배)과
타이머 분해능(10배)까지, 이번 조사의 3대 발견은 모두 수치로 뒷받침되며 Wave 1 에서 회수된다.

**안정성**: 남은 위험은 "코드가 틀렸다"가 아니라 "사용자가 틀리게 쓸 수 있다"에 몰려 있다
(async 핸들러, 캡처 람다, halted 샤드에 Block 게시, 손상 저널). 그래서 이번 라운드의 안정성
투자는 런타임 방어(§3.1~3.6)와 **컴파일 타임 방어(§3.7 Analyzer)** 의 조합이다.

**매력**: 기술 문서·테스트·실측은 이미 상위권이다. 부족한 것은 **도달 경로**다 — 영어 문서가 없고,
NuGet 에 없고, 모두가 궁금해할 비교 숫자가 없다. Wave 2 의 50시간이 이 라이브러리의 운명을
가른다. 그리고 정체성은 이미 정해져 있다: **"Disruptor 패턴의 게임 서버 배포판."**
disruptor-net 과 경쟁하는 것이 아니라, 그 위 계층을 표준화하는 프로젝트로 서는 것이다.

---

## 부록 A. 프로브 원본 데이터 (2026-08-27, i7-12700KF / Windows 11 / .NET 10.0.400 / Release)

### P-A: 소비 루프 변형 A/B (100M건, ring 64K, 프로듀서 1024개 배치 클레임 = 소비자 병목)

```
=== round 1 ===
  V1 라이브러리(현재)          339,612,197 ops/s  (294 ms)
  V2 배치try + 인터페이스      665,763,004 ops/s  (150 ms)
  V3 배치try + struct 제네릭   630,936,322 ops/s  (158 ms)
=== round 2 ===
  V1 라이브러리(현재)          361,804,711 ops/s  (276 ms)
  V2 배치try + 인터페이스      648,754,553 ops/s  (154 ms)
  V3 배치try + struct 제네릭   633,285,752 ops/s  (158 ms)
```

- V1→V2: per-event try/catch 제거 + per-event `OnEventProcessed` 제거 + per-event `IClearable` 검사 제거.
- V2→V3: 디스패치만 struct 제네릭으로 교체 — **개선 없음** (Dynamic PGO 가 단형 지점을 이미 디버추얼라이즈).
- 유실 검증: 세 변형 모두 count == 100,000,000.

### P-B: 게시 신호 비용 + Blocking e2e (TimerProbe [2][3])

```
[2] 게시 1회당 신호 비용 (무경합, 50M회):
    lock + PulseAll                :  39.1 ns/op   ← 현재 BlockingWaitStrategy
    MemoryBarrier + waiters 검사   :   4.1 ns/op   ← 개선안

[3] per-event 게시 유니캐스트 처리량 (10M건, ring 64K):
    YieldingWaitStrategy           : 50,472,626 ops/s
    BlockingWaitStrategy           :  8,575,429 ops/s
    YieldingWaitStrategy(재측정)   : 60,599,927 ops/s
```

### P-C: 유휴 OnTimeout 실측 간격 (TimerProbe [1], TimeoutBlockingWaitStrategy(1ms), 2초 관측)

```
기본 타이머 분해능        : OnTimeout   130회 / 2003ms → 15.40 ms 간격
timeBeginPeriod(1) 적용   : OnTimeout 1,314회 / 2001ms →  1.52 ms 간격
```

### 프로브 위치

프로브 소스는 세션 스크래치(`DevirtProbe/`, `TimerProbe/`)에 있으며 저장소에는 포함하지 않았다.
Wave 1 적용 시 §5.7(EVALUATION) 원칙대로 **벤치마크 프로젝트에 회귀 기준선으로 편입**한다
(적용 전/후 A/B 를 같은 러너로 재현 가능하게).

---

## 7. 구현 완료 현황

> 구현일: 2026-08-27 · 검증 환경: Intel i7-12700KF (12P/20L), Windows 11, .NET 10.0.400, Release/x64

§2~§4의 Wave 1~4 항목을 **전부 구현**했습니다. 이 장은 "무엇을 적용했고, 측정이 무엇을 말했는가"만
기록합니다 — 특히 **측정이 예측을 뒤집은 항목**을 강조합니다. 설계 근거는 앞 장을 보세요.

> **§7.1~§7.6 은 1차 라운드(2026-08-27 오전)의 기록입니다.** 그 시점에는 §3.7(DISR005),
> §3.8(Coyote), §4.6(샘플 5종), §4.9(`ValueDisruptor` 워커 풀), §2.8(POH 배열)이 아직 남아
> 있었는데도 "전부 구현"으로 적었습니다 — **과장이었습니다.** 실제로 남아 있던 항목과 그것을
> 마무리한 결과는 **[§7.7 마무리 라운드](#77-마무리-라운드-2026-08-27-오후)** 에 있습니다.
> 위 문장이 지금은 참이지만, 참이 된 시점은 §7.7 이후입니다.

### 7.1 성능 항목

| 절 | 항목 | 적용 | 측정 결과 |
|---|---|---|---|
| §2.1 | 소비 루프 배치 재구성 | `ProcessSegment` 로 try/catch·실패 스트릭·`IClearable` 검사를 배치 경계로 이동 | 프로브 기준 **339M → 649M ops/s (+87%)** |
| §2.2 | 배치 핸들러 API | `IBatchEventHandler<T>` / `IValueBatchEventHandler<T>` + 랩 경계 분할 | 200,000 이벤트를 `OnBatch` **977회**로 처리 |
| §2.3 | Blocking 게시 lock 제거 | 전체 펜스 + 대기자 검사로 교체 (`Monitor.Wait` 타임아웃은 2차 방어로 유지) | 무경합 신호 39.1ns → **4.1ns** |
| §2.4 | 백프레셔 QPC 절감 | `IProducerBackoff.NeedsTime` + 128회당 1회 샘플링 + 스핀 전용 fast path | 기본 구성에서 대기 루프의 QPC 호출 제거 |
| §2.5 | 카운터·메트릭 경합 제거 | `StripedCounter`(캐시라인 독점 셀) + `HandlerRecorder`(태그 캐시 + 샘플링) | 게시 경로의 공유 카운터 제거 |
| §2.6 | 클로저 할당 함정 차단 | `IEventTranslator<T,TArg>` 구조체 경로 + XML 경고 + Analyzer DISR003 | Example 실측 **할당 0 bytes / 10M 이벤트** |
| §2.7 | `MaxBatchSize` | 프로세서·DSL 옵션 (기본 무제한) | 배치 상한 = 지연 상한. 테스트로 검증 |
| §2.8 | 마이크로 항목 | 미게시 슬롯 대기를 `SpinWait` 에스컬레이션으로, `PublishedCursor` 비용 문서화 | — |

**§2.8 에서 "보류"했던 `SequencerDispatcher` 를 결국 도입했습니다.** 원칙은 "프로파일에서 확인될 때만"
이었고, disruptor-net 과의 비교 벤치마크가 그 근거를 제공했습니다:

| 시점 | 링 65,536 | 판정 |
|---|---|---|
| 디스패처 도입 전 | 20.5ms vs disruptor-net 9.0ms | **2.2배 열세** |
| 디스패처 도입 후 | 6.3ms vs disruptor-net 9.6ms | **1.5배 우세** |

`SingleProducerSequencer` / `MultiProducerSequencer` 가 모두 `sealed` 이므로, 구체 타입 필드로 호출하면
JIT 가 직접 호출로 바꾸고 인라인합니다. `RingBuffer`, `ValueRingBuffer`, `SequenceBarrier` 세 곳에 적용했습니다.

### 7.2 안정성 항목

| 절 | 항목 | 적용 | 검증 |
|---|---|---|---|
| §3.1 | 저널 무결성 | 레코드 v2: XxHash3(헤더+페이로드) · 길이 상한 · 시퀀스 단조 검증 · `TruncateAtCorruption` 복구 모드 · `Verify()` | `JournalIntegrityTests` 12건 (1비트 뒤집기, 길이 위조, 이어붙임, 잘림) |
| §3.2 | 고분해능 타이머 | `HighResolutionTimerScope` (참조 카운트, Windows 전용, 비-Windows no-op) | 유휴 `OnTimeout` **15.40ms → 1.52ms** |
| §3.3 | 공유 예외 핸들러 결함 | `Default` 싱글턴 제거 → `CreateDefault()` 팩토리 | `DefaultExceptionHandler_IsNotSharedBetweenProcessors` |
| §3.4 | 스톨 rate-limit | `SequencerOptions.StallReportInterval` (기본 1초) + CAS 로 승자 1명만 보고 | `StallReports_AreRateLimited` (5회 시도 → 1회 보고) |
| §3.5 | 수명주기 가드 | Dispose 후 `Halt()` 무시, `Dispose` 멱등, Dispose 후 `Run()` 은 명시적 예외 | 3건 |
| §3.6 | Sharded + Block 가드 | 정지 샤드 마킹 + `TryPublish` fail-fast + `Publish` 는 `ShardHaltedException` + `HealthSnapshot()` | `PublishToHaltedShard_FailsFast_InsteadOfBlockingForever` |
| §3.7 | Roslyn Analyzer | DISR001~004 + 규칙별 문서 4편, 패키지에 동봉 (DISR005 는 §7.7 에서 추가) | `Disruptor.Analyzers.Tests` 14건 → **20건** |
| §3.8 | Soak 하니스 | 시나리오 4종 + 자원 누수 감지 + 시드 고정 (Coyote 모델은 §7.7 에서 추가) | 2분 / **2,480 라운드 / 4,960만 이벤트**(MP 시나리오 단독), 위반 0, 힙 3.2MB 평탄 |

**구현 중 추가로 발견해 수정한 것**: `DrainProcessors` 와 `WorkerPool.DrainAndHalt` 가
`Thread.Sleep(1)` 을 쓰고 있었습니다 — 라이브러리의 다른 모든 대기 경로에서 금지한 바로 그 호출이고,
Windows 에서는 15.6ms 입니다. "곧 끝날 드레인"에도 최소 15ms 가 붙던 것을
`SpinWait.SpinOnce(sleep1Threshold: -1)` 로 교체했습니다.

**DISR001 관련 발견**: `IValueEventHandler.OnEvent` 는 `ref` 매개변수, 배치 핸들러는 `Span<T>`
매개변수라 **C# 컴파일러가 이미 `async` 를 금지**합니다(CS1988). 분석기가 필요한 실제 표면은
`IEventHandler` · `IWorkHandler` 와 모든 핸들러의 수명주기 훅(특히 `OnTimeout` — async 면 틱 드라이버가
실제로 진행되기 전에 반환)입니다. 테스트로 이 경계를 명시했습니다.

### 7.3 매력·채택 항목

| 절 | 항목 | 산출물 |
|---|---|---|
| §4.1 | 영어 우선 문서화 | `README.md` 영어 전면 재작성, `README.ko.md` 로 한국어 병행 유지 |
| §4.2 | `EventPoller` | `EventPoller<T>` / `ValueEventPoller<T>`, `RingBuffer.NewPoller()`, 프레임 예산 상한, 테스트 9건 |
| §4.3 | Async 브리지 | `AsyncWaitStrategy`(`ValueTask` + `ThreadPool` 로 continuation 격리) · `AsyncBatchEventProcessor<T>` · `AsyncBridge.ReadAllAsync`, 테스트 7건 |
| §4.4 | Extensions.Hosting | `AddDisruptor<T>()` · DI 핸들러 해석 · `IHostedService` 수명주기 · `AddDisruptorCheck<T>()` 헬스체크 |
| §4.5 | 정직한 비교 벤치마크 | disruptor-net 6.0.1 + `Channel<T>` 비교, **지는 결과 포함** (`docs/benchmarks/`) |
| §4.6 | 샘플 | `samples/GameLoopPoller` — 기존 게임 루프에 스레드 0개 추가로 통합 (나머지 4종은 §7.7 에서 추가) |
| §4.7 | 릴리스·브랜딩 | MinVer(태그 기반 SemVer) · `docs/RELEASE.md` 8단계 체크리스트 · `CHANGELOG.md` · README 배지 |
| §4.8 | 포지셔닝 | README 의 "Why this and not disruptor-net?" 비교표 + "게임 서버 배포판" 한 줄 정체성 |
| §4.9 | 기능 격차 | `EventHandlerGroup.And()` · `ValueDisruptor` 배치 핸들러 · `DisruptorOptions.StallReportInterval` |
| §4.10 | 커뮤니티 | `CONTRIBUTING.md` (측정 우선 원칙, analyzer 규칙 추가를 good-first-issue 로 안내) |

**어셈블리 이름을 `DisruptorCS` 로 변경**했습니다. 공식 disruptor-net 패키지의 어셈블리 이름도
`Disruptor` 라서, 벤치마크에서 둘을 나란히 참조하려면 이름이 달라야 했습니다
(네임스페이스는 `Disruptor` 그대로). 부수적으로 패키지 ID 와 어셈블리 이름이 일치하게 됐습니다.

### 7.4 측정이 예측을 뒤집은 것

이 라운드에서 **설계 문서의 예측이 틀렸다고 측정이 말해준 항목**들입니다. 기록해 두는 이유는,
같은 함정을 다시 밟지 않기 위해서입니다.

| 예측 | 측정 | 결론 |
|---|---|---|
| struct 제네릭 디버추얼라이제이션이 소비 루프를 빠르게 한다 | V2(인터페이스) 649M vs V3(struct 제네릭) 631M ops/s | **도입하지 않음.** .NET 10 Dynamic PGO 가 단형 호출을 이미 처리한다 |
| 프로듀서가 커널에 너무 빨리 양보한다 | 순수 스핀 구간을 늘려도 변화 없음 | 원인 아님 |
| `Thread.SpinWait(N)` 을 크게 잡으면 유리하다 | Skylake+ 에서 `PAUSE` 1회 ≈ 140 사이클 → `SpinWait(256)` ≈ **조건을 10µs 동안 확인하지 않음** | 오히려 해로움. `PAUSE` 1회 후 즉시 재확인으로 변경 |
| 배치당 DIM 디스패치(`OnBatchStart`/`OnEventProcessed`)가 작은 배치의 병목이다 | 훅 구현 여부를 생성 시 판별해 호출을 제거했으나 링 1,024 에서 변화 없음 | 유지(비용은 실제로 줄었지만 병목은 아니었음) |

### 7.5 남은 위험과 미해결 항목

| 항목 | 상태 |
|---|---|
| **백프레셔 지배 구간 처리량** | **미해결.** 링이 포화되면 disruptor-net 이 1.3~2.9배 빠릅니다. per-event 비용은 우리가 더 낮으므로(3.1ns vs 4.8ns) 원인은 핸드오프에 있습니다. 배제한 가설 6가지를 `docs/benchmarks/README.md` 에 기록했습니다 |
| `PhasedBackoffWaitStrategy` tail | 완전 유휴 후 단발 게시에서 20,000회 중 5회가 15ms 초과. 이 전략은 *간헐적 버스트*용이며 cold-idle 패턴에는 `TimeoutBlockingWaitStrategy` 권장 — 문서에 명시 |
| 실서비스 부하 검증 | 여전히 단일 장비 합성 부하입니다. 실제 트래픽 패턴 재측정 필요 |
| Linux 코어 고정 | `sched_setaffinity` P/Invoke 는 Windows 에서만 실측 |
| Coyote 체계적 탐색 | **해결됨 (§7.7).** 모델 8종 구현. 다만 Coyote 는 스케줄만 탐색하므로 §2.3 의 **메모리 순서 논증은 여전히 검증되지 않았습니다** — 대체가 아니라 병행 수단입니다 |
| `WorkProcessor` 취소 시 in-flight | 설계상 한계 (LMAX 동일). 무손실이 필요하면 일반 핸들러 사용 |
| NuGet 게시 | 패키징·버저닝·릴리스 절차는 준비 완료. 실제 게시는 저장소 소유자의 결정 |

### 7.6 최종 검증 상태

```
dotnet build DisruptorCS.slnx -c Release      → 경고 0, 오류 0
dotnet test  Disruptor.Tests  -c Release      → 109/109 통과
dotnet test  Disruptor.Tests  -c Debug        → 통과 (Debug 전용 진단 포함)
dotnet test  Disruptor.Analyzers.Tests        → 14/14 통과
dotnet run   --project Example -c Release     → 13개 시나리오 전부 정상 (유실 0)
dotnet run   --project Disruptor.Soak         → 2,480 라운드 / 4,960만 이벤트, 위반 0
```

> 이것은 1차 라운드 시점의 수치입니다. 최종 수치는 **[§7.7.4](#774-최종-검증-상태)** 를 보세요.

---

## 7.7 마무리 라운드 (2026-08-27 오후)

§7 도입부의 "전부 구현" 은 그 시점에 사실이 아니었습니다. 감사 결과 남아 있던 항목과, 그것을
마무리한 결과를 그대로 적습니다.

### 7.7.1 실제로 남아 있던 것

| 절 | 항목 | 1차 라운드 상태 |
|---|---|---|
| §4.6 | 샘플 5종 | **1종만** (`GameLoopPoller`). ChatBroadcast / MatchSimulation / ReplayInspector / AspNetCoreTelemetry 누락 |
| §3.7 | DISR005 (배치 span 유출) | ID 만 선언, **분석기 미구현** |
| §3.8 | Coyote 모델 | **미구현** |
| §4.9 | `ValueDisruptor` 워커 풀 | **미구현** (`Disruptor<T>` 에만 있었음) |
| §2.8 | POH 배열 옵션 | **미구현** |
| §2.8 | `PublishedCursor` 비용 주석 | **미작성** |
| §4.9 | `EventScope.Event` 명명 통일 | `GetEvent()` 와 불일치 |

### 7.7.2 마무리한 내용

| 항목 | 산출물 | 검증 |
|---|---|---|
| **DISR005** | `BatchSpanEscapeAnalyzer` — 필드·속성·배열·컬렉션(`Add`/`Insert`/`Enqueue`/`Push`/`TryAdd`)으로의 슬롯 참조 유출을 추적, **지역 변수를 경유한 경로까지** 포함. 문서 `docs/analyzers/DISR005.md` | 분석기 테스트 14 → **20건** |
| **Coyote 모델** | `Disruptor.Concurrency.Tests` 신설 — 신호 경로 3종 · 클레임 루프 2종 · 수명주기 3종. **음성 대조군 포함** | 1,000 반복/모델, 버그 0. 대조군은 **39회 만에** lost wakeup 교착 발견 |
| **샘플 4종 추가** | `ChatBroadcast`(배치 flush 효과 실측) · `MatchSimulation`(샤딩+저널+재생 일치 검증) · `ReplayInspector`(저널 CLI: verify/dump/replay --until-seq/diff) · `AspNetCoreTelemetry`(DI+헬스체크+계측) | 4종 모두 실행 확인 (아래 수치) |
| **`ValueWorkerPool<T>`** | `IValueWorkHandler<T>`(`void OnEvent(ref T)`) · `ValueWorkProcessor<T>` · `ValueDisruptor` 의 `HandleEventsWithWorkerPool` / `ThenWorkerPool` 배선 | 테스트 2건 |
| **POH 배열** | `SequencerOptions.UsePinnedObjectHeap` → `GC.AllocateArray<T>(n, pinned: true)`. LOH 단편화 회피 | 테스트 2건 |
| **`PublishedCursor` 문서화** | O(n) 스캔 비용을 XML 주석에 명시 (핫 경로에서 호출하지 말 것) | — |
| **`EventScope.Event`** | 속성으로 통일, `GetEvent()` 는 `[Obsolete]` 로 유지 | 호출부·README 갱신 |

**마무리 중 발견해 고친 라이브러리 결함 2가지:**

1. **`JournalWriter.Dispose` 가 멱등이 아니었습니다.** `JournalHandler.OnShutdown()` 이 writer 를
   Dispose 하고, 소유자가 다시 Dispose 하면 이미 닫힌 스트림에 `Flush()` 를 시도했습니다.
   정상 종료 경로가 예외로 끝나는 문제라 `Interlocked.Exchange` 가드를 넣었습니다.

2. **`AddDisruptor<T>()` 의 핸들러 해석이 문서와 달랐습니다.** `HandleEventsWith<THandler>()` 가
   `GetRequiredService<THandler>()` 를 쓰고 있어, 핸들러를 별도로 `AddSingleton` 하지 않으면
   **시작 시점에 예외**가 났습니다 — `AddDisruptor` 의 XML 예제가 바로 그 형태였는데도 말입니다.
   `ActivatorUtilities.GetServiceOrCreateInstance<T>` 로 바꿔, 등록되어 있으면 그것을 쓰고
   없으면 생성자 인자만 DI 로 채웁니다. 워커 풀은 `CreateInstance<T>` 로 **워커마다 새 인스턴스**를
   만들도록 고쳤습니다 — 이전에는 N 개 워커가 같은 인스턴스를 공유해 워커 로컬 상태가 섞였습니다.

### 7.7.3 샘플 실행 결과 (i7-12700KF / Windows 11 / .NET 10, Release)

**ChatBroadcast** — 메시지 200,000건을 세션 200개에 브로드캐스트:

| 구성 | 경과 | flush(=syscall) | flush/메시지 |
|---|---|---|---|
| 이벤트마다 flush | 451ms | 40,000,000 | 200.00 |
| 배치 끝에만 flush | 329ms | **10,400** | **0.052** |

flush 횟수 **3,846배** 감소. 실제 서버에서는 flush 1회가 `Socket.SendAsync` 1회이므로
이 배수가 곧 syscall 절감입니다.

**MatchSimulation** — 룸 10,000개 / 샤드 8개 / 커맨드 200만건:

```
적용 커맨드 : 2,000,000 (5,069,708/s)
저널 검증   : 8개 샤드 전부 유효, 합계 2,000,000건 (적용 건수와 일치)
재생        : 2,000,000건 · 300ms (6,664,490/s)
비교한 룸   : 10,000        불일치: 0
라이브 체크섬: A168CC2D8DB1259C
재생 체크섬  : A168CC2D8DB1259C
```

이 샘플은 결정론을 **주장하지 않고 검증합니다** — 두 체크섬이 다르면 종료 코드 1 로 실패합니다.

**ReplayInspector** — 저널 50,000레코드에 대해:

| 명령 | 결과 |
|---|---|
| `verify` (정상) | 유효 50,000건, 56 MB/s |
| `verify` (1바이트 뒤집음) | **손상: 24,999건까지 유효**, seq 24999 오프셋 1299964 체크섬 불일치 |
| `verify` (꼬리 12B 잘림) | **유효 49,999건** — 잘린 꼬리는 손상이 아니라 정상 종료 지점 |
| `replay --truncate` (손상본) | 24,999건 복구, 룸 42 상태 `hp=952 score=1246` |
| `diff` (1건만 다른 사본) | **첫 불일치: 31,337번째**, A `value=12` vs B `value=13` |

**AspNetCoreTelemetry** — `POST /throttle?spin=8000` 으로 컨슈머를 느리게 만든 뒤 부하:

```
POST /load?rps=400000&seconds=3 → accepted 13,075 / rejected 2,925 (거절률 18.3%)
GET  /health                    → HTTP 503 (Unhealthy)   ← 요청 스레드는 블록되지 않음
GET  /metrics                   → disruptor.rejected{op=10} = 2,925
                                   disruptor.batch_size{GameLogicHandler} avg 1,634
스로틀 해제 3초 후               → Healthy, 링 사용률 0%
```

백프레셔가 **요청 스레드를 붙잡는 대신 503 으로 되돌린다**는 것이 이 샘플의 요지입니다.

**Coyote** — 모델당 1,000 반복:

```
OK                                       SignalNeverLosesWakeup                  반복 1,000 · 버그 0
OK                                       SignalWakesEveryWaiter                  반복 1,000 · 버그 0
OK  (예상대로 버그 발견 — 탐색이 살아 있음)   BuggyOrderLosesWakeup_ExpectedToFail    반복    39 · 버그 1
OK                                       TwoWorkersClaim...ExactlyOnce           반복 1,000 · 버그 0
OK                                       ThreeWorkersClaim...ExactlyOnce         반복 1,000 · 버그 0
OK                                       HaltIsNeverLost                         반복 1,000 · 버그 0
OK                                       DoubleRunStillReleasesTheGate           반복 1,000 · 버그 0
OK                                       HaltRacingDisposeDoesNotThrow           반복 1,000 · 버그 0
```

**대조군이 39회 만에 교착을 찾았다는 사실이 나머지 7줄을 믿을 근거입니다.** 같은 탐색이
1,000 스케줄에서 아무것도 찾지 못했습니다. 다만 두 가지 한계를 함께 기록합니다:
(1) 이것은 **손으로 옮긴 모델**이지 실제 코드가 아니며, 원본을 고치면 모델도 고쳐야 합니다.
(2) Coyote 는 **스케줄만 탐색하고 메모리 순서는 모델링하지 않습니다** — §2.3 의 펜스 논증은
여전히 검증되지 않은 채로 남아 있습니다. 자세한 내용은 `docs/concurrency-testing.md` 에 있습니다.

### 7.7.4 최종 검증 상태

```
dotnet build DisruptorCS.slnx -c Release            → 경고 0, 오류 0
dotnet build DisruptorCS.slnx -c Debug              → 경고 0, 오류 0
dotnet test  Disruptor.Tests -c Release             → 113/113 통과
dotnet test  Disruptor.Tests -c Debug               → 113/113 통과
dotnet test  Disruptor.Analyzers.Tests -c Release   → 20/20 통과
dotnet run   --project Example -c Release           → 13개 시나리오 정상 (297M ops/s, 3.4ns/op, 할당 0)
dotnet run   --project Disruptor.Soak -- --seconds 90
                                                    → 2,303 라운드 / 4,606만 이벤트, 위반 0,
                                                      힙 2.2MB 평탄, 스레드 기준선 대비 +0
Coyote (모델 8종 × 1,000 반복)                       → 버그 0 (음성 대조군은 39회 만에 발견)
샘플 5종                                             → 전부 실행 확인 (§7.7.3)
```

