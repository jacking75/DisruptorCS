# DisruptorCS 게임 서버 적용성 평가 및 개선 설계서

> 대상 커밋: `8d79410` (main) · 평가일: 2026-08-27 · 평가 방식: 전체 소스 리뷰 + 실측 프로브 6종 실행
> 검증 환경: Intel i7-12700KF (12P/20L), 64GB RAM, Windows 11 26200, .NET 10.0.400, Release/x64

> ## ✅ 구현 완료 (2026-08-27)
>
> **이 문서의 P0~P3 항목이 전부 구현되었습니다.** 아래 본문은 개선 *이전* 상태에 대한 평가와 설계를
> 그대로 보존한 것이며, 실제 적용 결과와 검증 내역은 **[§9 구현 완료 현황](#9-구현-완료-현황)** 을 보세요.
>
> 요약: 결함 12건 + 구현 중 발견 2건 수정 · 신규 파일 23개 · xUnit 테스트 60개(반복 실행 전부 통과) ·
> 다중 프로듀서 유실 **794,332건 → 0건** · 단일 프로듀서 66.4M ops/s에 **할당 0 bytes**
>
> **그 다음 라운드**: 이 문서 이후의 고성능·안정성 정밀 조사와 채택 전략은
> **[`IMPROVEMENTS.md`](IMPROVEMENTS.md)** 에 있고, 그 항목들도 전부 구현되었습니다
> (처리량 66.4M → **250M ops/s**, 테스트 60 → **133개**, 샘플 5종, Analyzer·Hosting·Soak·Concurrency 신설).

---

## 0. 요약

### 0.1 한 줄 결론

> **현재 상태로는 게임 서버에 투입할 수 없다.** `ProducerType.Multi` 경로에 링 버퍼의 근본 계약(백프레셔)을 무력화하는 **치명적 결함**이 있어, 부하가 걸리면 **컨슈머가 영구 정지하고 이벤트가 유실**된다. 다만 결함 자체는 **수 줄 수준의 수정**으로 해결되며, 구조와 설계 방향은 원본 LMAX Disruptor를 충실히 따르고 있다. **정확성 수정(Phase 0) + 게임 서버 전용 기능(Phase 1)** 을 마치면, 게임 서버의 **중앙 집중형 이벤트 파이프라인** 영역에서 `Channel<T>` 대비 명확한 우위를 갖는 자산이 된다.

### 0.2 평가 점수

| 항목 | 점수 | 근거 |
|---|:---:|---|
| 설계 충실도 (LMAX 대비) | **B+** | 링 버퍼 / 시퀀서 / 배리어 / 대기전략 / DSL 분리가 정석대로 되어 있음 |
| 정확성 (Correctness) | **F** | Multi 프로듀서 백프레셔 완전 무력화(재현율 100%), 이벤트 유실 99.3% |
| 단일 프로듀서 성능 | **A** | 63M ops/s, 15.8ns/op 실측 (백프레셔 미발생 구간) |
| 지연 시간 예측 가능성 | **D** | 백프레셔 발생 시 p99.99 = **16ms**, max **26.5ms** (60Hz 틱 붕괴) |
| 리소스 효율 | **C** | Blocking 전략인데도 의존성 체인 하위 핸들러가 **1코어 상시 점유** 실측 |
| 게임 서버 기능 커버리지 | **D** | 타임아웃/틱 훅, 배치 훅, 워크 분배, 백프레셔 정책, 관측성 전부 없음 |
| 운영 준비도 | **F** | 테스트 0개, 벤치마크 프로젝트 0개, CI 없음, NuGet 패키징 없음 |

### 0.3 투자 대비 가치 판단

| 시나리오 | 판단 |
|---|---|
| **지금 그대로 도입** | 불가. 데이터 유실 + 서버 행(hang) |
| **Phase 0(정확성)만 수정 후 도입** | 제한적 가능. 유실 허용도가 낮지 않은 보조 경로(비동기 로깅 등)에 한정 |
| **Phase 0+1 완료 후 도입** | 권장. 중앙 패킷 파이프라인 / 저널·리플레이 / 랭킹 집계에서 실질 가치 발생 |
| **성숙한 `disruptor-net` NuGet 사용** | 가장 현실적인 대안. 자체 구현을 유지할 명분은 "게임 서버 도메인 특화 확장"이 붙을 때만 성립 |

**핵심 판단**: 범용 Disruptor를 처음부터 다시 만드는 것은 이미 성숙한 `disruptor-net`이 존재하므로 가치가 낮다.
이 코드베이스에 투자할 가치가 생기는 지점은 **게임 서버 도메인 특화 레이어**(틱 동기화, 룸 샤딩, 결정론적 리플레이, 패킷 버퍼 풀 연동)를 얹었을 때다. 그 설계를 5장에 상세 기술한다.

---

## 1. 검증 방법

원본 소스를 수정하지 않고, 별도 프로브 프로젝트에서 라이브러리를 참조해 실측했다.
수정 효과는 라이브러리를 임시 영역에 복사해 패치한 뒤 동일 프로브를 재실행하는 A/B 방식으로 검증했다.

| 프로브 | 목적 | 결과 |
|---|---|---|
| P1 | 멀티프로듀서 경합 시 tail latency | §3.1 |
| P2 | 링 1024 / 8 프로듀서 / 800K 건 무결성 검증 | §3.1 |
| P3 | 다이아몬드 토폴로지 + BlockingWaitStrategy CPU 사용량 | §3.3 |
| P4 | 백프레셔 계약 검증 (컨슈머 미시작 상태에서 버퍼 초과 게시) | §3.1 |
| P5 | 유휴 후 단발 게시 wake-up 지연 (20,000회) | §3.5 |
| P6 | 백프레셔 발생 시 `Next()` 지연 분포 (수정 전/후 A/B) | §3.2 |

---

## 2. 현재 구현에서 잘 되어 있는 점

먼저 **유지해야 할 자산**부터 정리한다.

1. **관심사 분리가 정석대로 되어 있다.** `ISequencer`(순번 주장) / `RingBuffer`(저장) / `ISequenceBarrier`+`IWaitStrategy`(알림)이 깔끔히 분리되어 있다. 이 구조 덕분에 아래 개선안 대부분이 **파일 1~2개 수정 범위**로 끝난다.

2. **`Sequence`의 false sharing 패딩이 실제로 유효하다.** `[StructLayout(LayoutKind.Explicit, Size=192)]` + `[FieldOffset(56)]` 배치는 앞 56B / 뒤 128B 패딩을 확보하므로, 객체 정렬과 무관하게 다른 객체의 데이터와 캐시 라인을 공유하지 않는다. (§3.8-f에서 128B 프리페치 대응 확장 권고)

3. **`SingleProducerSequencer`의 CAS-free 설계와 게이팅 값 캐시**가 원본과 동일하게 구현되어 있다. 실측 63M ops/s / 15.8ns per op이 이 최적화가 실제로 동작함을 보여준다.

4. **`MultiProducerSequencer`의 `availableBuffer` + 라운드 플래그 기법**(`seq >> indexShift`를 라운드 태그로 사용)이 정확히 구현되어 있다. 원자적 게시 판정에 추가 배리어가 필요 없다.

5. **`SequenceBarrier`의 단일 의존 최적화**(의존 시퀀스가 1개일 때 가상 디스패치 없이 직접 전달)는 원본에도 있는 유효한 최적화다.

6. **DSL의 리프 노드 게이팅 최적화**(`Then()` 호출 시 상위 단계 시퀀스를 게이팅에서 제거)가 올바르게 구현되어 있다. 다이아몬드 토폴로지에서 게이팅 최솟값 계산 비용이 O(전체 핸들러)가 아니라 O(리프)로 유지된다.

7. **`PublishEventScope()`의 `using` 패턴**은 Java 원본에 없는, C# 관용구에 맞는 좋은 추가다. `try/finally`를 잊어 링 버퍼를 영구 정지시키는 사고를 구조적으로 막는다.

---

## 3. 결함 목록

심각도 순. 각 항목은 **[현상] → [재현/실측] → [원인] → [수정]** 형식으로 기술한다.

### 3.1 CRITICAL — `MultiProducerSequencer.Next()`가 백프레셔를 완전히 무력화한다

#### 현상
`ProducerType.Multi`에서 컨슈머가 뒤처지면, 프로듀서가 **미소비 슬롯을 그대로 덮어쓴다.** 그 결과 `availableBuffer`의 라운드 플래그가 갱신되어 컨슈머가 기다리던 시퀀스는 **영원히 `IsAvailable == false`** 가 되고, **컨슈머 스레드가 영구 정지**한다.

#### 재현 (P4 — 100% 결정적)
```
게이팅 시퀀스 등록 수 = 1, 값 = [-1]
컨슈머 미시작 상태에서 1024건 게시 (버퍼 크기 = 1024) → cursor=1023, minGating=-1
1025번째 Next() 호출 → 백프레셔가 정상이면 영구 블록되어야 함
  !!! Next() 가 반환됨 -> seq=1024        ← 계약 위반
```

#### 재현 (P2 — 실사용 시나리오, 링 1024 / 프로듀서 8 / 각 100,000건)
```
[게시 완료] 800,000건, 249ms, cursor=799999
  t+1s  consumerSeq=5667  cursor=799999  consumed=5,668  진행=진행중
  t+2s  consumerSeq=5667  cursor=799999  consumed=5,668  진행=정지(STALL)
  >>> 소비자 영구 정지 확인
[결과] 기대=800,000  실제소비=5,668  손실=794,332      (유실률 99.3%)
```

#### 원인 — C# `do…while`에서 `continue`의 의미

`Disruptor/MultiProducerSequencer.cs:63-84`

```csharp
do
{
    current = _cursor.Value;
    next = current + n;
    long wrapPoint = next - _bufferSize;
    ...
    if (wrapPoint > gatingSequence)
    {
        spinWait.SpinOnce();
        continue;              // 의도: 루프 처음으로 되돌아가 용량 재확인
    }
    ...
}
while (!_cursor.CompareAndSet(current, next));   // 실제: continue 가 여기로 점프
```

C#에서 `do…while` 안의 `continue`는 **루프 본문의 처음이 아니라 조건식으로 점프**한다.
따라서 "용량 없음"으로 판정된 직후에도 `_cursor.CompareAndSet(current, next)`가 **그대로 실행**되고, 성공하면 시퀀스를 획득한 채 반환된다. 백프레셔가 존재하지 않는 것과 같다.

> Java 원본은 `for(;;) { … if (wrapPoint > gatingSequence) { LockSupport.parkNanos(1); continue; } … if (cursor.compareAndSet(current, next)) break; }` 형태의 무한 루프여서 이 함정이 없다. `do…while`로 옮기는 과정에서 생긴 이식 버그다.

#### 수정

```csharp
public long Next(int n = 1)
{
    if (n < 1) throw new ArgumentOutOfRangeException(nameof(n));
    if (n > _bufferSize) throw new ArgumentOutOfRangeException(nameof(n),
        "n must not exceed bufferSize");     // 영구 대기 방지 가드 (원본에도 있음)

    var spinWait = new SpinWait();

    while (true)                                   // do…while → while(true)
    {
        long current = _cursor.Value;
        long next = current + n;
        long wrapPoint = next - _bufferSize;
        long cachedGatingSequence = _gatingSequenceCache.Value;

        if (wrapPoint > cachedGatingSequence || cachedGatingSequence > current)
        {
            long gatingSequence = SequenceGroups.GetMinimumSequence(_gatingSequences, current);

            if (wrapPoint > gatingSequence)
            {
                spinWait.SpinOnce(sleep1Threshold: -1);   // §3.2 참조
                continue;                                  // 이제 CAS 를 건너뛴다
            }

            _gatingSequenceCache.SetValue(gatingSequence);
        }

        if (_cursor.CompareAndSet(current, next))
            return next;
        // CAS 실패 = 다른 프로듀서가 선점 → 처음부터 재시도
    }
}
```

#### 수정 검증 (동일 프로브 재실행)
```
P4: 1025번째 Next() → Next() 블록됨 -> 백프레셔 정상 동작

P2: [게시 완료] 800,000건, 99ms
    t+1s  consumerSeq=799999  consumed=800,000  >>> 정상 완료
    [결과] 기대=800,000  실제소비=800,000  손실=0
    [결과] 슬롯 덮어쓰기(Id != seq) 관측 횟수 = 0
```
유실 0건. 부수적으로 게시 시간도 249ms → **99ms**로 개선됐다(무의미한 CAS 경합 제거 효과).

#### 동일 패턴 점검
- `MultiProducerSequencer.TryNext()` — `do…while`이지만 실패 시 즉시 `return false`이므로 이 함정은 없다. 일관성을 위해 `while(true)` 형태로 통일 권장.
- `SingleProducerSequencer.Next()` — `while` 루프를 쓰고 있어 **정상**.

#### 왜 기존 예제에서 안 잡혔는가 (테스트 설계 교훈)
`Example/Program.cs`의 `MultiProducerExample`은
- 링 크기가 64K라 400K 이벤트 중 컨슈머가 한 바퀴 이상 뒤처지기 어렵고,
- `allDone.Wait(TimeSpan.FromSeconds(10))`의 **반환값을 검사하지 않은 채** 처리량을 출력한다.

즉 **타임아웃이 나도 "성공"처럼 보이는 벤치마크**다. §5.7의 테스트 설계에서 반드시 교정해야 한다.

---

### 3.2 HIGH — `SpinWait.SpinOnce()`의 `Thread.Sleep(1)`이 15ms 스톨을 만든다

#### 현상
`SingleProducerSequencer.Next()` / `MultiProducerSequencer.Next()`의 백프레셔 대기와 `SequenceBarrier`의 복합 의존 대기가 모두 `SpinWait.SpinOnce()`를 쓴다. .NET의 `SpinWait`는 일정 횟수 이후 `Thread.Sleep(1)`을 호출하는데, **Windows 기본 타이머 분해능(15.6ms)** 때문에 실제로는 **최소 15.6ms 잠든다.**

60Hz 게임 서버의 틱 예산은 **16.6ms**다. 백프레셔 한 번이 **틱 하나를 통째로 날린다.**

#### 실측 (P6, 원본 코드 · 링 1024 · 300,000건 · 느린 컨슈머)
```
SingleProducer Next() 백프레셔 지연
  p50=0.0us  p90=0.1us  p99=0.1us  p99.9=57.8us  p99.99=16,054.9us  max=26.51ms
  >  1ms :   298 건 (0.0993%)
  >  5ms :   294 건 (0.0980%)
  > 10ms :   289 건 (0.0963%)
  > 15ms :   284 건 (0.0947%)      ← 15.6ms 타이머 양자화의 명확한 지문
처리량: 64,404 ops/s
```
**1,000건 중 약 1건이 15ms 이상 프로듀서 스레드를 정지**시킨다. 프로듀서가 네트워크 수신 스레드라면 그 사이 수신 큐가 밀리고, 클라이언트는 랙으로 인식한다.

#### 원인
`SpinWait.SpinOnce()`는 `Thread.SpinWait` → `Thread.Yield()` → `Thread.Sleep(0)` → `Thread.Sleep(1)` 순으로 에스컬레이션한다. .NET Core 3.0부터 `SpinOnce(int sleep1Threshold)` 오버로드가 있고, **`-1`을 넘기면 `Sleep(1)` 단계가 완전히 비활성화**된다.

#### 수정
```csharp
// 전: spinWait.SpinOnce();
// 후:
spinWait.SpinOnce(sleep1Threshold: -1);
```
적용 위치 3곳:
- `Disruptor/SingleProducerSequencer.cs:57`
- `Disruptor/MultiProducerSequencer.cs:77`
- `Disruptor/SequenceBarrier.cs:65`

#### 수정 검증 (P6, 동일 조건)
```
  p50=0.0us  p90=0.1us  p99=0.1us  p99.9=3,985.4us  p99.99=7,281.3us  max=11.43ms
  >  1ms :   582 건
  >  5ms :   289 건
  > 10ms :     1 건 (0.0003%)
  > 15ms :     0 건 (0.0000%)      ← 15ms 절벽 소멸
처리량: 94,914 ops/s   (+47%)
```
15ms 초과 **284건 → 0건**, max 26.5ms → 11.4ms, 처리량 **+47%**.

#### 추가 권고 — 프로듀서 대기 정책을 주입 가능하게
`Next()`의 대기 방식은 현재 하드코딩돼 있다. 게임 서버는 "절대 잠들면 안 되는 틱 스레드"와 "CPU를 양보해야 하는 배경 로깅"이 공존하므로 정책을 분리한다.

```csharp
// Disruptor/IProducerBackoff.cs (신규)
public interface IProducerBackoff
{
    void Reset();
    /// <summary>백프레셔 대기 1회. false 를 반환하면 Next() 가 TimeoutException 을 던진다.</summary>
    bool Wait(long spinCount, long elapsedTicks);
}

/// <summary>틱 스레드용 — 절대 Sleep(1) 하지 않는다.</summary>
public sealed class SpinOnlyBackoff : IProducerBackoff
{
    private SpinWait _spin;
    public void Reset() => _spin = default;
    public bool Wait(long spinCount, long elapsedTicks)
    {
        _spin.SpinOnce(sleep1Threshold: -1);
        return true;
    }
}

/// <summary>배경 작업용 — 상한 시간을 넘기면 실패시켜 호출자가 드롭 판단을 하게 한다.</summary>
public sealed class TimeBoundedBackoff : IProducerBackoff
{
    private readonly long _limitTicks;
    private SpinWait _spin;
    public TimeBoundedBackoff(TimeSpan limit) => _limitTicks = limit.Ticks;
    public void Reset() => _spin = default;
    public bool Wait(long spinCount, long elapsedTicks)
    {
        if (elapsedTicks > _limitTicks) return false;
        _spin.SpinOnce(sleep1Threshold: -1);
        return true;
    }
}
```

---

### 3.3 HIGH — `BlockingWaitStrategy` + 의존성 체인 = 하위 컨슈머가 1코어를 상시 점유

#### 현상
`BlockingWaitStrategy`는 "CPU 사용량이 낮은" 전략으로 문서화돼 있으나, **의존성 체인(`.Then()`)의 하위 핸들러는 상위 핸들러를 hot-spin으로 기다린다.**

#### 실측 (P3)
상위 핸들러가 이벤트당 10ms 소요, 초당 ~300건 게시, `BlockingWaitStrategy` 사용:
```
처리 이벤트=300  실경과=4798ms  프로세스 CPU 시간=4688ms
→ CPU/실시간 비율 = 0.98 코어
```
**하는 일이 거의 없는데 코어 1개를 통째로 태운다.** 컨테이너에 2 vCPU를 할당한 게임 서버라면 절반이 증발한다. 클라우드 비용과 직결되고, 이웃 프로세스의 틱 지터도 키운다.

#### 원인
`Disruptor/WaitStrategy/BlockingWaitStrategy.cs:35-40`
```csharp
// 1단계: cursor 를 Monitor 로 대기 (정상)
// 2단계:
long availableSequence;
while ((availableSequence = dependentSequence.Value) < sequence)
{
    barrier.CheckAlert();
    Thread.SpinWait(1);       // 의존 시퀀스는 순수 스핀으로 대기
}
```
프로듀서 커서는 빠르게 전진하므로 1단계는 즉시 통과하고, 느린 상위 핸들러를 2단계에서 무한 스핀으로 기다린다. 나아가 의존 시퀀스가 2개 이상이면 `SequenceBarrier`가 대기 전략에 **커서를 넘기고** 최솟값 체크를 자기가 스핀으로 수행한다(`SequenceBarrier.cs:56-69`) — 대기 전략이 우회되는 구조적 문제다.

#### 수정 (1) — `DependentSequenceGroup` 도입

"의존 대상"을 1급 개념으로 승격시켜 대기 전략이 **항상 올바른 대상**을 보게 한다.

```csharp
// Disruptor/DependentSequenceGroup.cs (신규)
using System.Runtime.CompilerServices;

namespace Disruptor;

/// <summary>
/// 대기 전략에 넘길 "의존 대상".
/// 의존 핸들러가 없으면 커서를, 있으면 의존 시퀀스들의 최솟값을 노출한다.
/// 대기 전략은 이 값 하나만 보고 블로킹/스핀을 결정하면 되므로,
/// 하위 컨슈머가 상위 컨슈머를 기다릴 때도 정상적으로 블로킹할 수 있다.
/// </summary>
public sealed class DependentSequenceGroup
{
    private readonly Sequence _cursor;
    private readonly Sequence[] _dependencies;   // 길이 0 이면 커서만 추적
    private readonly Sequence? _single;          // 길이 1 최적화 (루프/분기 제거)

    public DependentSequenceGroup(Sequence cursor, params Sequence[] dependencies)
    {
        _cursor = cursor;
        _dependencies = dependencies;
        _single = dependencies.Length == 1 ? dependencies[0] : null;
    }

    /// <summary>의존 대상이 현재 어디까지 진행했는가.</summary>
    public long Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_dependencies.Length == 0) return _cursor.Value;
            if (_single is not null)       return _single.Value;

            long min = long.MaxValue;
            var deps = _dependencies;
            for (int i = 0; i < deps.Length; i++)
            {
                long v = deps[i].Value;
                if (v < min) min = v;
            }
            return min;
        }
    }

    /// <summary>프로듀서 커서(게시 상한).</summary>
    public long CursorValue => _cursor.Value;

    /// <summary>의존 핸들러 없이 커서만 추적하는가.</summary>
    public bool DependsOnCursor => _dependencies.Length == 0;
}
```

`IWaitStrategy`를 이 타입 기반으로 교체한다.

```csharp
public interface IWaitStrategy
{
    /// <summary>대기 시 블로킹(Monitor 등)을 사용하는가.
    /// Sequencer / BatchEventProcessor 가 신호 전송 필요 여부를 판단하는 데 쓴다.</summary>
    bool IsBlockingStrategy { get; }

    SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                               CancellationToken cancellationToken);

    void SignalAllWhenBlocking();
}
```

```csharp
public sealed class BlockingWaitStrategy : IWaitStrategy
{
    private readonly object _gate = new();
    public bool IsBlockingStrategy => true;

    public SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                                      CancellationToken ct)
    {
        long available = dependents.Value;
        if (available >= sequence) return available;      // 빠른 경로: 락 진입 없음

        lock (_gate)
        {
            while ((available = dependents.Value) < sequence)
            {
                if (ct.IsCancellationRequested) return SequenceWaitResult.Cancelled;
                // 의존 핸들러가 있어도 여기서 잠든다 → hot-spin 제거
                Monitor.Wait(_gate, millisecondsTimeout: 100);   // §3.5 lost-wakeup 방어
            }
        }
        return available;
    }

    public void SignalAllWhenBlocking()
    {
        lock (_gate) { Monitor.PulseAll(_gate); }
    }
}
```

#### 수정 (2) — 신호 전파 경로 보강 (필수)

하위 컨슈머가 **상위 컨슈머의 시퀀스**를 기다리며 잠들었다면, 상위 `BatchEventProcessor`가 자기 시퀀스를 갱신할 때 신호를 보내야 한다. 현재는 `Sequencer.Publish()`만 신호를 보낸다.

```csharp
// BatchEventProcessor.ProcessEvents() 내부, 배치 처리 완료 지점
_sequence.SetValue(availableSequence);

// 추가: 나를 기다리는 하위 컨슈머 깨우기
if (_waitStrategy.IsBlockingStrategy)
    _waitStrategy.SignalAllWhenBlocking();
```

이 두 가지를 적용하면 P3 시나리오의 CPU 사용률이 **0.98코어 → 0.0x코어**로 떨어진다.

---

### 3.4 HIGH — 기본 예외 핸들러가 게임 서버 프로세스를 죽인다

#### 현상
`BatchEventProcessor`의 기본 예외 핸들러는 `FatalExceptionHandler<T>`이고, 이것은 예외를 **다시 던진다.**

`Disruptor/FatalExceptionHandler.cs:12-17`
```csharp
public void HandleEventException(Exception ex, long sequence, T @event)
{
    Console.Error.WriteLine(...);
    throw new ApplicationException("Fatal exception in event handler", ex);   // 재throw
}
```

이 예외는 `ProcessEvents()`의 `catch (Exception ex)` **블록 안에서** 던져지므로 어디에도 잡히지 않고 `Run()` 밖으로 전파된다. `Run()`은 `Disruptor.Start()`가 만든 **백그라운드 스레드**에서 실행되고, .NET에서 스레드의 미처리 예외는 **프로세스를 즉시 종료**시킨다.

> **한 명의 유저가 보낸 비정상 패킷 하나로 서버 전체가 다운된다.** 게임 서버에서 이보다 나쁜 기본값은 없다.

#### 원인 (구조적)
`IExceptionHandler<T>`가 `void`를 반환하는 설계라 "이 이벤트는 버리고 계속" / "재시도" / "이 핸들러만 정지"를 표현할 수단이 없다. 그래서 "중단"을 예외 재throw로 표현할 수밖에 없다.

#### 수정 — 예외 정책을 반환값으로 표현

```csharp
// Disruptor/EventExceptionAction.cs (신규)
namespace Disruptor;

public enum EventExceptionAction
{
    /// <summary>이벤트를 버리고 다음 시퀀스로 진행 (게임 서버 기본값).</summary>
    Skip,
    /// <summary>같은 이벤트를 다시 처리. 핸들러가 멱등할 때만 사용.</summary>
    Retry,
    /// <summary>이 프로세서만 정지. 프로세스는 살려 둔다.</summary>
    Halt,
}

public interface IExceptionHandler<in T>
{
    EventExceptionAction HandleEventException(Exception ex, long sequence, T @event);
    void HandleOnStartException(Exception ex);
    void HandleOnShutdownException(Exception ex);
}
```

```csharp
// Disruptor/ResilientExceptionHandler.cs (신규) — 게임 서버 기본값
public sealed class ResilientExceptionHandler<T> : IExceptionHandler<T>
{
    private readonly Action<Exception, long, T?> _onError;
    private readonly int _maxConsecutiveFailures;
    private int _consecutive;

    public ResilientExceptionHandler(Action<Exception, long, T?> onError,
                                     int maxConsecutiveFailures = 100)
    {
        _onError = onError;
        _maxConsecutiveFailures = maxConsecutiveFailures;
    }

    public EventExceptionAction HandleEventException(Exception ex, long sequence, T @event)
    {
        _onError(ex, sequence, @event);      // 로깅 / 메트릭 / 데드레터 큐

        // 연속 실패가 임계치를 넘으면 무한 오류 루프로 보고 이 프로세서만 정지
        if (++_consecutive >= _maxConsecutiveFailures)
            return EventExceptionAction.Halt;

        return EventExceptionAction.Skip;
    }

    /// <summary>정상 처리 시 BatchEventProcessor 가 호출한다.</summary>
    public void ResetFailureStreak() => _consecutive = 0;

    public void HandleOnStartException(Exception ex)    => _onError(ex, -1, default);
    public void HandleOnShutdownException(Exception ex) => _onError(ex, -1, default);
}
```

`BatchEventProcessor.ProcessEvents()`를 그에 맞게 재작성한다. (§3.6·§5.1·§5.2의 개선을 함께 반영한 최종형)

```csharp
private void ProcessEvents(CancellationToken ct)
{
    long nextSequence = _sequence.Value + 1L;

    while (!ct.IsCancellationRequested)
    {
        var waitResult = _barrier.WaitFor(nextSequence, ct);

        if (waitResult.IsCancelled) break;

        if (waitResult.IsTimeout)
        {
            // §5.1: 유휴 상태에서 주기적으로 호출 → 틱 처리 / 송신 버퍼 플러시
            try { _eventHandler.OnTimeout(nextSequence - 1); }
            catch (Exception ex) { _exceptionHandler.HandleEventException(ex, nextSequence, default!); }
            continue;
        }

        long available = waitResult.AvailableSequence;
        if (available < nextSequence) continue;      // MP: 아직 연속 게시가 안 됨

        _eventHandler.OnBatchStart(available - nextSequence + 1);   // §5.2

        while (nextSequence <= available)
        {
            T evt = _ringBuffer[nextSequence];
            try
            {
                _eventHandler.OnEvent(evt, nextSequence, nextSequence == available);
                nextSequence++;
            }
            catch (Exception ex)
            {
                switch (_exceptionHandler.HandleEventException(ex, nextSequence, evt))
                {
                    case EventExceptionAction.Skip:
                        nextSequence++;
                        break;
                    case EventExceptionAction.Retry:
                        break;                       // nextSequence 유지 → 재시도
                    case EventExceptionAction.Halt:
                        _sequence.SetValue(nextSequence - 1);
                        _onHalted?.Invoke(this, ex); // §5.6: 상위에 치명 알림
                        return;                      // 프로세스는 살린다
                }
            }
        }

        _sequence.SetValue(available);
        if (_waitStrategy.IsBlockingStrategy) _waitStrategy.SignalAllWhenBlocking();
    }
}
```

> **주의**: `Halt`로 프로세서를 멈추면 그 시퀀스가 게이팅에 남아 **프로듀서가 곧 영구 블록**된다.
> 반드시 `Halt` 시 (a) 해당 시퀀스를 `RemoveGatingSequence`로 제거하거나, (b) 상위에 치명 알림을 올려 서버를 계획적으로 드레인해야 한다. §5.6과 함께 설계할 것.

---

### 3.5 MEDIUM — `BlockingWaitStrategy`의 `_numWaiters` 최적화에 lost-wakeup 경쟁이 있다

#### 현상 / 위험
```csharp
// 프로듀서 측
public void Publish(long sequence)
{
    _cursor.SetValue(sequence);                  // (A) volatile write
    _waitStrategy.SignalAllWhenBlocking();
}
public void SignalAllWhenBlocking()
{
    if (Volatile.Read(ref _numWaiters) > 0)      // (B) volatile read
    { lock (_gate) { Monitor.PulseAll(_gate); } }
}

// 컨슈머 측
lock (_gate)
{
    Interlocked.Increment(ref _numWaiters);      // (C) full fence
    while (cursor.Value < sequence)              // (D)
    { barrier.CheckAlert(); Monitor.Wait(_gate); }   // 타임아웃 없음
}
```

(A)→(B)는 **StoreLoad 순서**로, x86-64에서도 스토어 버퍼 때문에 **재배치가 허용**된다. 따라서 다음 인터리빙이 성립한다.

1. 프로듀서: (A) 커서 쓰기가 스토어 버퍼에 머문다
2. 프로듀서: (B) `_numWaiters == 0` 관측 → **PulseAll 하지 않고 반환**
3. 컨슈머: (C) `_numWaiters = 1`
4. 컨슈머: (D) 아직 전파되지 않은 옛 커서 값을 읽음 → 조건 참 → `Monitor.Wait()` **무기한 대기**

`Monitor.Wait`에 타임아웃이 없으므로 **다음 게시가 올 때까지 마지막 이벤트가 처리되지 않는다.** 버스트 직후 유휴 구간이 생기는 게임 서버 패턴(로그인 러시 후 조용, 보스 처치 후 조용)에서 정확히 이 형태로 드러난다. `Halt()` 경로에서도 같은 이유로 `Shutdown()`이 타임아웃(5초)까지 지연될 수 있다.

#### 실측 (P5, 20,000회 유휴 → 단발 게시)
```
p50=11.7us  p99=58.9us  p99.9=175.9us  max=2.39ms
> 1ms 발생: 3건 / 20,000건 (영구 손실은 관측되지 않음)
```
20,000회에서 영구 손실은 재현되지 않았다 — 경쟁 창이 매우 좁다. 그러나 창이 좁을 뿐 닫혀 있지 않으며, 게임 서버는 초당 수만 건을 몇 달간 처리한다.
부수적으로 이 실측은 **blocking 전략의 기본 wake-up 비용이 p50 11.7μs**임을 알려준다 — 틱 스레드에 부적합하다는 정량 근거다.

#### 수정 (세 가지, 셋 다 적용 권장)
```csharp
// (1) _numWaiters 최적화를 제거하고 무조건 PulseAll (Java 원본과 동일)
public void SignalAllWhenBlocking() { lock (_gate) { Monitor.PulseAll(_gate); } }

// (2) Monitor.Wait 에 타임아웃을 주어 lost-wakeup 을 자가 치유
Monitor.Wait(_gate, millisecondsTimeout: 100);

// (3) Publish 경로에서 blocking 전략일 때만 신호를 보내 (1)의 lock 비용을 회피
if (_waitStrategy.IsBlockingStrategy) _waitStrategy.SignalAllWhenBlocking();
```
`_numWaiters` 최적화를 유지하고 싶다면 읽기 **전에 full fence**를 넣어야 한다.
```csharp
_cursor.SetValue(sequence);
Interlocked.MemoryBarrier();                 // StoreLoad 재배치 차단
if (Volatile.Read(ref _numWaiters) > 0) { lock (_gate) Monitor.PulseAll(_gate); }
```
다만 `MemoryBarrier`가 게시 hot path에 들어가므로, (3)으로 blocking 전략에서만 비용을 내는 편이 낫다.

---

### 3.6 MEDIUM — `Start()` / `Shutdown()` 경쟁으로 종료가 5초 지연될 수 있다

`Disruptor<T>.Start()`는 `thread.Start()` 직후 반환하지만, `BatchEventProcessor.Run()`은 시작 시 `_sequenceBarrier.ClearAlert()`를 호출한다. `Start()` 직후 곧바로 `Shutdown()`이 호출되면:

1. `Shutdown()` → `Halt()` → `_running = 0`, `Alert()` → `_alerted = true`
2. 그제서야 스레드가 스케줄되어 `Run()` 진입 → `ClearAlert()` → `_alerted = false`
3. `_running`은 `Run()`의 `Interlocked.Exchange(ref _running, 1)`로 다시 1
4. 프로세서가 정지 신호를 잃고 계속 실행 → `Shutdown()`은 `Join` 타임아웃(기본 5초)까지 대기

매치 종료 시 룸을 파괴하는 게임 서버라면 룸마다 5초씩 지연된다.

#### 수정
```csharp
public sealed class Disruptor<T> : IDisposable where T : class
{
    private readonly CancellationTokenSource _cts = new();
    private int _startedFlag;

    public RingBuffer<T> Start()
    {
        if (Interlocked.Exchange(ref _startedFlag, 1) != 0)
            throw new InvalidOperationException("Already started");

        var started = new CountdownEvent(_processors.Count);
        foreach (var processor in _processors)
        {
            var thread = new Thread(() => processor.Run(_cts.Token, started))
            {
                IsBackground = true,
                Name = $"Disruptor-{typeof(T).Name}-{_threads.Count}",
            };
            _threads.Add(thread);
            thread.Start();
        }
        started.Wait();        // 모든 프로세서가 이벤트 루프에 진입한 뒤 반환
        return _ringBuffer;
    }
}

// BatchEventProcessor
public void Run(CancellationToken ct, CountdownEvent? started = null)
{
    if (Interlocked.Exchange(ref _running, 1) != 0)
        throw new InvalidOperationException("Already running");
    try
    {
        try { _eventHandler.OnStart(); }
        catch (Exception ex) { _exceptionHandler.HandleOnStartException(ex); }

        started?.Signal();     // "준비 완료" 신호
        ProcessEvents(ct);
    }
    finally
    {
        try { _eventHandler.OnShutdown(); }
        catch (Exception ex) { _exceptionHandler.HandleOnShutdownException(ex); }
        _running = 0;
    }
}
```
정지 신호는 `_alerted` 플래그 대신 **`CancellationToken` 하나로 단일화**한다(§3.7과 함께). 토큰은 한 번 취소되면 되돌릴 수 없으므로 `ClearAlert()`가 신호를 지우는 경쟁 자체가 사라진다.

---

### 3.7 LOW — `AlertException` 싱글턴을 여러 스레드가 공유한다

`AlertException.Instance`는 정적 단일 인스턴스이고 모든 프로세서가 이것을 `throw`한다.
.NET은 예외를 던질 때 **예외 객체의 stack trace를 갱신**한다. 여러 스레드가 동시에 같은 인스턴스를 던지면 stack trace가 서로를 덮어써 사고 분석이 불가능해진다. 또한 정상 종료 경로에 예외를 쓰는 것 자체가 비싸다(스택 언와인딩). 최신 `disruptor-net`이 `AlertException`을 제거하고 결과 값으로 전환한 이유다.

#### 수정 — `SequenceWaitResult` 도입
```csharp
// Disruptor/SequenceWaitResult.cs (신규)
namespace Disruptor;

public readonly struct SequenceWaitResult : IEquatable<SequenceWaitResult>
{
    private const long TimeoutValue   = -1;
    private const long CancelledValue = -2;

    private readonly long _value;
    private SequenceWaitResult(long value) => _value = value;

    public static SequenceWaitResult Timeout   => new(TimeoutValue);
    public static SequenceWaitResult Cancelled => new(CancelledValue);

    public static implicit operator SequenceWaitResult(long availableSequence)
        => new(availableSequence);

    public bool IsTimeout   => _value == TimeoutValue;
    public bool IsCancelled => _value == CancelledValue;

    /// <summary>IsTimeout / IsCancelled 가 false 일 때만 유효.</summary>
    public long AvailableSequence => _value;

    public bool Equals(SequenceWaitResult other) => _value == other._value;
    public override bool Equals(object? o) => o is SequenceWaitResult r && Equals(r);
    public override int GetHashCode() => _value.GetHashCode();
}
```
`readonly struct` 하나라 할당이 없고, 예외 대비 수백 배 저렴하며 스레드 간 상태 공유가 없다.

---

### 3.8 LOW — 그 밖의 항목

| # | 위치 | 문제 | 수정 |
|---|---|---|---|
| a | `Dsl/Disruptor.cs:142,156,173` | `DateTime.UtcNow`로 데드라인 계산. NTP 보정·수동 시간 변경에 취약 | `Stopwatch.GetTimestamp()` 또는 `Environment.TickCount64` |
| b | `WaitStrategy/YieldingWaitStrategy.cs:29` | 스핀 100회 동안 `pause` 힌트 없이 순수 루프 → 하이퍼스레드 형제 코어 성능 저하, 전력 낭비 | `else { counter--; Thread.SpinWait(1); }` |
| c | `WaitStrategy/SpinWaitWaitStrategy.cs:8` | XML 주석이 "SleepingWaitStrategy에 해당"이라 하나 실제 동작이 다름 | 주석 수정 + 진짜 `SleepingWaitStrategy` 별도 추가 |
| d | `Dsl/Disruptor.cs:169` | `DrainProcessors`가 `_ringBuffer.Cursor` 기준. Multi에서 Cursor는 **게시**가 아니라 **점유** 최대치 | `GetHighestPublishedSequence(-1, cursor)` 기준으로 드레인 |
| e | `RingBuffer.cs` | 슬롯의 이벤트 객체가 참조 필드(byte[], string, 세션 핸들)를 붙들어 **객체 수명이 최대 한 바퀴 연장**. 1M 슬롯 × 패킷 버퍼면 GB 단위 유지 | 처리 후 `Clear()`를 프레임워크가 강제하는 `IClearable` 훅 (§5.3) |
| f | `Sequence.cs` | Intel 스트리밍 프리페처는 128B 쌍 단위로 읽어오므로 좌측 56B 패딩이 부족할 수 있음 | `Size = 264`, `FieldOffset(128)`로 좌우 128B 확보 |
| g | `SingleProducerSequencer.cs:144` | `GetMinimumSequence(_gatingSequences, _cursor.Value)` — 원본은 `nextValue` 시드. 동작 문제는 없으나 의미가 다름 | `_nextValue` 시드로 통일 |
| h | 전역 | `Next()`에 취소/타임아웃 수단이 전혀 없음. 컨슈머가 죽으면 **모든 프로듀서 스레드가 영구 정지**하고 진단 정보가 0 | `Next(CancellationToken)`, `TryNext(out, TimeSpan)`, 스톨 감지 콜백 (§5.5) |
| i | `Dsl/Disruptor.cs:101-105` | 핸들러 추가 시 `Remove` → `Add` 순서. 시작 전이라 현재는 안전하나, 동적 추가를 지원하면 게이팅이 순간 비는 창이 생김 | `Add` → `Remove` 순서로 교정 |
| j | `Disruptor.csproj` | `net10.0` 단독 타깃. 상용 게임 서버는 LTS(net8.0)가 일반적 | `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>` |
| k | 전역 | 테스트 0개, 벤치마크 프로젝트 0개, CI 0개, NuGet 패키징 없음 | §5.7 |

---

## 4. 게임 서버 관점의 적합성 분석

### 4.1 Disruptor의 강점이 게임 서버의 어느 문제에 정확히 대응하는가

게임 서버가 겪는 고유한 고통은 웹 서버와 다르다.

| 게임 서버의 고통 | Disruptor의 대응 | 대응 강도 |
|---|---|---|
| **틱 지터(tick jitter)** — 60Hz면 16.6ms 안에 끝나야 하고, GC 정지 한 번이 곧 랙 | 사전 할당 링 버퍼로 정상 경로 할당 0 | 강함 |
| **결정론(determinism)** — 리플레이/롤백/치팅 검증을 위해 입력 순서가 고정돼야 함 | 시퀀스 번호가 곧 전역 순서. 저널에 시퀀스만 남기면 완벽 재현 | **매우 강함** |
| **동일 입력의 다중 소비** — 로직 / 저널 / 관전자 브로드캐스트 / 안티치트가 같은 커맨드를 봐야 함 | 멀티캐스트가 기본. 복사 0 | **매우 강함** |
| **단계 간 순서 보장** — "저널 기록 후에만 로직 커밋" 같은 제약 | 의존성 그래프(`.Then()`)로 선언적 표현 | 강함 |
| **버스트 흡수** — 보스 스폰/공성 시작 순간 트래픽 급증 | 배치 효과: 밀린 만큼 한 번에 처리, 동시성 비용은 배치당 1회 | 강함 |
| **락 경합으로 인한 tail latency** | lock-free, CAS 최소화 | 강함 |
| **수천 개 룸의 독립 처리** | 링 버퍼 하나당 스레드 하나 모델과 충돌 | **약함 (부적합)** |
| **비동기 소켓 I/O 통합** | 동기/스핀 모델. `async/await`와 자연스럽지 않음 | **약함** |
| **작업 분배(부하 분산)** | 멀티캐스트가 기본이라 워크 분배는 별도 구현 필요 | 약함 (`WorkerPool` 추가 시 해결) |

### 4.2 워크로드별 적합성 판정

#### 적합 — 도입 가치가 높은 곳

**(1) 중앙 집중형 패킷 처리 파이프라인**
```
[IO 스레드 N개] ──MP publish──> [RingBuffer] ──┬─> 저널 핸들러
                                                ├─> 안티치트 핸들러
                                                └─> (Then) 게임 로직 핸들러 ─> 응답 큐
```
IO 스레드가 프로듀서(Multi), 로직이 단일 컨슈머. 동일 패킷을 저널·안티치트·로직이 각자 보므로 멀티캐스트가 정확히 맞는다. `Channel<T>`로 하면 3개 채널에 3번 써야 한다.

**(2) 결정론적 저널링 / 리플레이 / 롤백**
LMAX의 원래 용도이자 게임 서버에서 가장 값이 큰 용도다. 시퀀스 번호가 전역 순서이므로:
- 저널에 `(sequence, payload)` 만 남기면 서버 상태를 정확히 재현할 수 있다
- 버그 리포트를 받으면 해당 시퀀스 구간만 재생해 디버깅
- 핫 스탠바이 노드에 시퀀스 스트림을 그대로 흘려 상태 복제

**(3) 비동기 로깅 / 메트릭 집계**
Log4j2가 Disruptor를 쓰는 것과 같은 이유. 로직 스레드가 로그 I/O 때문에 멈추면 안 된다. `BlockingWaitStrategy` + 큰 링 버퍼 조합이면 CPU 비용도 낮다.

**(4) 랭킹 / 매치메이킹 / 재화 정산 같은 집계 파이프라인**
초당 수십만 건의 이벤트를 순서대로 누적해야 하는 작업. 단일 컨슈머 + 배치 효과로 DB 쓰기를 자연스럽게 묶을 수 있다(`endOfBatch == true`일 때 flush).

**(5) 크로스 서버 릴레이 / 채팅 브로드캐스트 팬아웃**
하나의 메시지를 N개 대상에 전달하는 팬아웃 구조.

#### 부적합 — 쓰면 안 되는 곳

**(1) 룸/존 단위 커맨드 큐 (가장 흔한 오적용)**

Disruptor는 **컨슈머 1개당 전용 스레드 1개**를 요구한다. 룸마다 Disruptor를 만들면:

| 룸 수 | 링 크기 | 스레드 수 | 링 메모리(이벤트 128B 가정) | 스레드 스택 예약 |
|---:|---:|---:|---:|---:|
| 100 | 1024 | 100 | ~13 MB | ~100 MB |
| 1,000 | 1024 | 1,000 | ~131 MB | ~1 GB |
| 10,000 | 1024 | 10,000 | ~1.3 GB | ~10 GB |

10,000 룸 × 스레드 1개 = **커널 스케줄러 붕괴**. 컨텍스트 스위치만으로 CPU가 소진된다.
게다가 `BusySpin`/`Yielding` 전략이면 각 스레드가 코어를 태우므로 즉시 사망한다.

> **해결책은 Disruptor를 버리는 것이 아니라 "샤딩"이다.** 룸을 코어 수만큼의 샤드로 나누고, 샤드당 Disruptor 1개를 둔다. §6.3에 구현을 기술한다.

**(2) 요청-응답(RPC) 패턴**
Disruptor는 단방향 스트림이다. "요청을 보내고 응답을 기다린다"를 표현하려면 응답용 링 버퍼를 하나 더 두고 상관관계 ID로 매칭해야 하는데, 이 경우 `Channel<T>`나 `TaskCompletionSource`가 훨씬 간단하다.

**(3) `async/await` 기반 소켓 I/O 경계**
Disruptor 컨슈머는 동기 블로킹 스레드다. 핸들러 안에서 `await`를 하면 이벤트 순서 보장이 깨지고 스레드 모델이 무너진다. **핸들러 안에서는 절대 `await`하지 않는다**는 규칙을 강제해야 한다(§5.6의 진단 항목 참조).

**(4) 소비자 간 작업 분배가 목적인 경우**
멀티캐스트가 기본이라 "N명 중 아무나 하나가 처리"는 표현되지 않는다. `WorkerPool` 추가(§5.4) 전에는 부적합.

### 4.3 메모리 설계 지침 (게임 서버 실무)

링 버퍼 메모리 = `링크기 × (참조 8B + 이벤트 객체 크기)` + `Sequence 객체 수 × 192B`

게임 커맨드 이벤트를 이렇게 잡았다고 하자.
```csharp
public sealed class GameCommand
{
    public long   SessionId;      // 8
    public int    RoomId;         // 4
    public ushort OpCode;         // 2
    public int    PayloadLength;  // 4
    public byte[] Payload;        // 참조 8  (풀에서 대여한 버퍼)
    public long   ReceivedTicks;  // 8
}                                  // 헤더 16 + 필드 ~40 → 실제 ~64B
```

| 링 크기 | 슬롯 메모리 | 비고 |
|---:|---:|---|
| 256 | ~18 KB | 룸 샤드용 권장 |
| 1,024 | ~74 KB | 일반 파이프라인 |
| 65,536 | ~4.7 MB | 버스트 흡수용 |
| 1,048,576 | ~75 MB | 벤치마크용. 실서비스에 이 크기는 거의 불필요 |

**핵심 주의 — `Payload` 참조가 슬롯에 남아 있는 동안 풀 버퍼가 회수되지 않는다.**
1M 슬롯 × 4KB 패킷 버퍼면 **4GB가 붙들린다.** 반드시 핸들러 처리 후 슬롯을 비워야 한다(§5.3).

```csharp
public void OnEvent(GameCommand cmd, long seq, bool endOfBatch)
{
    try { Process(cmd); }
    finally
    {
        ArrayPool<byte>.Shared.Return(cmd.Payload!);   // 풀 반납
        cmd.Payload = null;                             // 슬롯의 참조 해제 (필수)
    }
}
```

### 4.4 스레드 예산 설계 지침

Disruptor 핸들러 스레드는 **전용 코어를 전제**로 한다. 게임 서버 프로세스 전체의 스레드 예산은 다음처럼 잡는다.

```
사용 가능 논리 코어 = C
  네트워크 I/O (SocketAsyncEventArgs 완료 포트)   : ~2
  Disruptor 핸들러 (스핀 전략)                    : ≤ C - 4   ← 절대 초과 금지
  .NET ThreadPool (타이머, DB, 외부 API)          : 나머지
  GC 스레드 (Server GC = 코어당 1)                : 백그라운드
```

**전략 선택 규칙**

| 핸들러 스레드 수 | 권장 전략 | 근거 |
|---|---|---|
| < 물리 코어 수 − 2 | `BusySpinWaitStrategy` | 최저 지연. 코어 독점 가능할 때만 |
| < 논리 코어 수 − 2 | `YieldingWaitStrategy` | 저지연 + 형제 스레드 배려 |
| ≥ 논리 코어 수 | `BlockingWaitStrategy` (§3.3 수정 후) | 초과 구독 상태에서 스핀은 자살 행위 |
| 컨테이너 (cgroup 제한) | `TimeoutBlockingWaitStrategy` (§5.1) | CPU 쿼터 소진(스로틀링) 방지 + 틱 훅 확보 |

> **컨테이너 경고**: Kubernetes에서 `cpu: "2"` 같은 제한을 걸면 `BusySpin` 핸들러 하나가 쿼터를 다 태우고 **CFS 스로틀링**을 유발해 프로세스 전체가 100ms 단위로 정지한다. 컨테이너 환경 기본값은 반드시 blocking 계열이어야 한다.

---

## 5. 개선 로드맵

### Phase 0 — 정확성 (필수, 이것 없이는 어떤 도입도 불가)

| # | 항목 | 참조 | 공수 |
|---|---|---|---|
| 0-1 | `MultiProducerSequencer.Next()` 백프레셔 수정 | §3.1 | 0.5h |
| 0-2 | `SpinOnce(sleep1Threshold: -1)` 3곳 적용 | §3.2 | 0.5h |
| 0-3 | 기본 예외 핸들러를 `ResilientExceptionHandler`로 교체 | §3.4 | 4h |
| 0-4 | `BlockingWaitStrategy` lost-wakeup 수정 | §3.5 | 1h |
| 0-5 | `Start()` 동기화 + `CancellationToken` 단일화 | §3.6 | 4h |
| 0-6 | `AlertException` → `SequenceWaitResult` 전환 | §3.7 | 4h |
| 0-7 | `DependentSequenceGroup` 도입 + 신호 전파 보강 | §3.3 | 6h |
| 0-8 | 회귀 테스트 작성 (아래 §5.7) | — | 12h |

---

### Phase 1 — 게임 서버 필수 기능

#### 5.1 타임아웃 대기 전략 + `OnTimeout` 훅 (최우선)

**왜 필요한가.** 게임 서버는 "이벤트가 없어도 주기적으로 해야 할 일"이 있다.
- 송신 버퍼 플러시 (Nagle 대신 직접 배칭)
- 틱 진행 (이벤트 유무와 무관하게 시뮬레이션 스텝)
- 세션 타임아웃 검사, 하트비트
- 배치로 모아둔 DB 쓰기 커밋

현재는 이런 일을 하려면 별도 타이머 스레드를 만들고 다시 락으로 동기화해야 한다 — Disruptor를 쓰는 의미가 사라진다.

```csharp
// Disruptor/WaitStrategy/TimeoutBlockingWaitStrategy.cs (신규)
namespace Disruptor.WaitStrategy;

public sealed class TimeoutBlockingWaitStrategy : IWaitStrategy
{
    private readonly object _gate = new();
    private readonly int _timeoutMs;

    public TimeoutBlockingWaitStrategy(TimeSpan timeout)
    {
        _timeoutMs = (int)timeout.TotalMilliseconds;
        if (_timeoutMs < 1) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public bool IsBlockingStrategy => true;

    public SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                                      CancellationToken ct)
    {
        long available = dependents.Value;
        if (available >= sequence) return available;

        lock (_gate)
        {
            while ((available = dependents.Value) < sequence)
            {
                if (ct.IsCancellationRequested) return SequenceWaitResult.Cancelled;
                if (!Monitor.Wait(_gate, _timeoutMs))
                    return SequenceWaitResult.Timeout;    // 핸들러의 OnTimeout 호출로 이어진다
            }
        }
        return available;
    }

    public void SignalAllWhenBlocking() { lock (_gate) { Monitor.PulseAll(_gate); } }
}
```

**틱 정밀도가 필요한 경우** — `Monitor.Wait`의 타이머 분해능(15.6ms)이 문제가 되면, 스핀 예산을 먼저 소모하고 남은 시간만 잠드는 하이브리드를 쓴다.

```csharp
// Disruptor/WaitStrategy/PhasedBackoffWaitStrategy.cs (신규)
public sealed class PhasedBackoffWaitStrategy : IWaitStrategy
{
    private readonly long _spinTicks;      // 이 시간까지는 스핀
    private readonly long _yieldTicks;     // 이 시간까지는 Yield
    private readonly IWaitStrategy _fallback;   // 그 이후는 blocking 계열에 위임

    public bool IsBlockingStrategy => _fallback.IsBlockingStrategy;

    public SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                                      CancellationToken ct)
    {
        long start = 0;
        while (true)
        {
            long available = dependents.Value;
            if (available >= sequence) return available;
            if (ct.IsCancellationRequested) return SequenceWaitResult.Cancelled;

            if (start == 0) { start = Stopwatch.GetTimestamp(); Thread.SpinWait(1); continue; }

            long elapsed = Stopwatch.GetTimestamp() - start;
            if (elapsed < _spinTicks)       Thread.SpinWait(64);
            else if (elapsed < _yieldTicks) Thread.Yield();
            else                            return _fallback.WaitFor(sequence, dependents, ct);
        }
    }

    public void SignalAllWhenBlocking() => _fallback.SignalAllWhenBlocking();
}
```

**핸들러 인터페이스 확장**
```csharp
public interface IEventHandler<in T>
{
    void OnEvent(T data, long sequence, bool endOfBatch);

    /// <summary>배치 처리 시작 시 1회. batchSize 만큼 이벤트가 연속 도착했음을 알린다.</summary>
    void OnBatchStart(long batchSize) { }

    /// <summary>대기 타임아웃 시 호출. 틱 진행 / 송신 플러시 / 하트비트 지점.</summary>
    void OnTimeout(long lastProcessedSequence) { }

    void OnStart() { }
    void OnShutdown() { }
}
```

#### 5.2 배치 인지 핸들러 — 게임 서버 처리량을 좌우하는 핵심

`OnBatchStart` / `endOfBatch`를 쓰면 **I/O 시스템 콜과 DB 왕복을 배치 크기만큼 줄일 수 있다.**

```csharp
/// <summary>배치 단위로 송신을 묶는 브로드캐스트 핸들러.</summary>
public sealed class BroadcastHandler : IEventHandler<GameCommand>
{
    private readonly Dictionary<long, PooledBufferWriter> _perSession = new();

    public void OnBatchStart(long batchSize)
    {
        // 배치 크기를 미리 알 수 있으므로 버퍼를 한 번에 확보한다
        foreach (var w in _perSession.Values) w.EnsureCapacity((int)batchSize * 64);
    }

    public void OnEvent(GameCommand cmd, long seq, bool endOfBatch)
    {
        Encode(cmd, _perSession);          // 메모리에만 쓴다 (syscall 없음)

        if (endOfBatch)                    // 배치 끝에서 한 번만 전송
            FlushAll();
    }

    public void OnTimeout(long lastSeq) => FlushAll();   // 유휴 시에도 지연 없이 flush

    private void FlushAll() { /* Socket.SendAsync 한 번에 */ }
}
```

이 패턴 하나로 **1,000건 처리 시 syscall이 1,000회 → 1회**가 된다. 게임 서버에서 Disruptor 도입 효과의 상당 부분이 여기서 나온다.

#### 5.3 슬롯 정리 강제 — 메모리 누수 방지

```csharp
// Disruptor/IClearable.cs (신규)
public interface IClearable
{
    /// <summary>슬롯이 가진 외부 참조를 해제한다. 프레임워크가 이벤트 처리 후 호출한다.</summary>
    void Clear();
}
```

```csharp
// BatchEventProcessor — 이벤트 처리 직후
_eventHandler.OnEvent(evt, nextSequence, nextSequence == available);

if (_clearAfterProcess && evt is IClearable clearable)
    clearable.Clear();     // Payload 참조 끊기 → 풀 버퍼 조기 회수
```

`_clearAfterProcess`는 **파이프라인의 마지막 핸들러에만** true로 설정해야 한다(중간에 지우면 하위 핸들러가 빈 이벤트를 본다). DSL에서 자동 판정한다.

```csharp
// Dsl/Disruptor.cs — Start() 시점
public RingBuffer<T> Start()
{
    // 게이팅에 남아 있는(= 리프 노드) 프로세서만 Clear 책임을 진다
    var leafSequences = _ringBuffer.GetGatingSequences();
    foreach (var p in _processors)
        p.ClearAfterProcess = leafSequences.Contains(p.Sequence) && _autoClear;
    ...
}
```
> 리프가 여러 개면 마지막 하나만 지워야 하므로, 다중 리프일 때는 `_autoClear`를 자동으로 끄고 경고를 남긴다.

#### 5.4 `WorkerPool` — 작업 분배 지원

멀티캐스트만으로는 "N개 워커가 나눠서 처리"를 표현할 수 없다. 게임 서버의 DB 쓰기, 외부 API 호출, 이미지/리플레이 인코딩 같은 **순서 무관 + 느린 작업**에 필요하다.

```csharp
// Disruptor/IWorkHandler.cs (신규)
public interface IWorkHandler<in T> { void OnEvent(T evt); }

// Disruptor/WorkProcessor.cs (신규)
public sealed class WorkProcessor<T> where T : class
{
    private readonly RingBuffer<T> _ringBuffer;
    private readonly ISequenceBarrier _barrier;
    private readonly IWorkHandler<T> _handler;
    private readonly Sequence _workSequence;   // 워커들이 공유하는 클레임 카운터
    private readonly Sequence _sequence = new();
    private readonly IExceptionHandler<T> _exceptionHandler;

    public Sequence Sequence => _sequence;

    public void Run(CancellationToken ct)
    {
        long nextSequence = _sequence.Value + 1L;
        bool processedSequence = true;
        long cachedAvailable = -1;

        while (!ct.IsCancellationRequested)
        {
            if (processedSequence)
            {
                // 워커들끼리 CAS 로 다음 시퀀스를 경쟁적으로 점유
                processedSequence = false;
                do { nextSequence = _workSequence.Value + 1L; _sequence.SetValue(nextSequence - 1L); }
                while (!_workSequence.CompareAndSet(nextSequence - 1L, nextSequence));
            }

            if (cachedAvailable >= nextSequence)
            {
                T evt = _ringBuffer[nextSequence];
                try { _handler.OnEvent(evt); processedSequence = true; }
                catch (Exception ex)
                {
                    if (_exceptionHandler.HandleEventException(ex, nextSequence, evt)
                        != EventExceptionAction.Retry)
                        processedSequence = true;
                }
            }
            else
            {
                var r = _barrier.WaitFor(nextSequence, ct);
                if (r.IsCancelled) break;
                if (r.IsTimeout) continue;
                cachedAvailable = r.AvailableSequence;
            }
        }
    }
}
```

DSL 연동:
```csharp
disruptor.HandleEventsWith(journal, antiCheat)
         .Then(gameLogic)
         .ThenWorkerPool(workerCount: 4, () => new DbWriteWorker(connFactory));
```

#### 5.5 백프레셔 정책 API — 게임 서버는 "버리는 것"도 정책이다

링 버퍼가 가득 찼을 때 프로듀서(=네트워크 I/O 스레드)를 블록시키면 **서버 전체가 굳는다.** 게임 서버는 상황에 따라 다른 정책이 필요하다.

| 이벤트 종류 | 가득 찼을 때 |
|---|---|
| 이동 패킷 (초당 20회, 최신값만 의미 있음) | **드롭** — 다음 패킷이 곧 온다 |
| 아이템 구매 / 재화 변동 | **블록 또는 오류 응답** — 절대 버릴 수 없다 |
| 채팅 | **드롭 + 클라이언트에 "혼잡" 알림** |
| 로그 / 메트릭 | **드롭 + 드롭 카운터 증가** |

```csharp
// RingBuffer<T> 확장
public sealed class RingBuffer<T> where T : class
{
    /// <summary>지금 게시 가능한 슬롯 수.</summary>
    public long RemainingCapacity =>
        _sequencer.BufferSize - (_sequencer.Cursor - _sequencer.GetMinimumSequence());

    /// <summary>버퍼가 지정 비율 이상 찼는가. 사전 드롭 판정에 쓴다.</summary>
    public bool IsCongested(double threshold = 0.8) =>
        RemainingCapacity < _sequencer.BufferSize * (1.0 - threshold);

    /// <summary>타임아웃까지 시퀀스를 점유해 본다.</summary>
    public bool TryNext(out long sequence, TimeSpan timeout)
        => _sequencer.TryNext(out sequence, 1, timeout);

    public long Next(CancellationToken ct) => _sequencer.Next(1, ct);
}
```

```csharp
// 게임 서버 게시부 — 이벤트 등급별 정책 적용
public enum BackpressurePolicy { Block, DropOldestSemantics, Fail }

public bool TryPublishCommand(in GameCommand src, BackpressurePolicy policy)
{
    switch (policy)
    {
        case BackpressurePolicy.Block:
            long s = _ring.Next(_shutdownToken);
            Fill(_ring[s], src); _ring.Publish(s);
            return true;

        case BackpressurePolicy.DropOldestSemantics:      // 이동 패킷 등
            if (_ring.IsCongested(0.9)) { _metrics.Dropped(src.OpCode); return false; }
            goto case BackpressurePolicy.Block;

        case BackpressurePolicy.Fail:                      // 결제 등
            if (!_ring.TryNext(out long s2, TimeSpan.FromMilliseconds(50)))
            {
                _metrics.Rejected(src.OpCode);
                return false;                              // 호출자가 오류 응답
            }
            Fill(_ring[s2], src); _ring.Publish(s2);
            return true;
    }
    return false;
}
```

#### 5.6 관측성 — 운영에서 이것이 없으면 사고를 못 푼다

현재 라이브러리는 **내부 상태를 밖으로 전혀 노출하지 않는다.** "서버가 멈췄는데 어느 핸들러 때문인지 모른다"는 최악의 상황이 그대로 발생한다.

```csharp
// Disruptor/DisruptorMetrics.cs (신규)
using System.Diagnostics.Metrics;

public sealed class DisruptorMetrics : IDisposable
{
    private static readonly Meter Meter = new("DisruptorCS", "1.0.0");

    private readonly Counter<long> _published;
    private readonly Counter<long> _processed;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _handlerErrors;
    private readonly Histogram<long> _batchSize;
    private readonly Histogram<double> _publishLatencyUs;

    public DisruptorMetrics(string pipelineName)
    {
        var tags = new KeyValuePair<string, object?>[] { new("pipeline", pipelineName) };
        _published        = Meter.CreateCounter<long>("disruptor.published");
        _processed        = Meter.CreateCounter<long>("disruptor.processed");
        _dropped          = Meter.CreateCounter<long>("disruptor.dropped");
        _handlerErrors    = Meter.CreateCounter<long>("disruptor.handler_errors");
        _batchSize        = Meter.CreateHistogram<long>("disruptor.batch_size");
        _publishLatencyUs = Meter.CreateHistogram<double>("disruptor.publish_latency_us");
    }

    /// <summary>가장 중요한 지표: 각 핸들러가 커서로부터 얼마나 뒤처졌는가.</summary>
    public void RegisterLagGauges(RingBuffer<object> ring,
                                  IReadOnlyList<(string name, Sequence seq)> handlers)
    {
        foreach (var (name, seq) in handlers)
        {
            var captured = seq;
            Meter.CreateObservableGauge("disruptor.handler_lag",
                () => new Measurement<long>(ring.Cursor - captured.Value,
                                            new KeyValuePair<string, object?>("handler", name)));
        }
    }

    /// <summary>링 버퍼 사용률(0~1). 0.8 이상이 지속되면 컨슈머 증설/최적화 신호.</summary>
    public void RegisterUtilizationGauge(RingBuffer<object> ring)
        => Meter.CreateObservableGauge("disruptor.utilization",
               () => 1.0 - (double)ring.RemainingCapacity / ring.BufferSize);

    public void OnBatch(long size)   => _batchSize.Record(size);
    public void OnDropped()          => _dropped.Add(1);
    public void OnHandlerError()     => _handlerErrors.Add(1);
    public void Dispose() { }
}
```

**스톨 감지** — 프로듀서가 오래 블록되면 원인을 자동으로 지목한다.
```csharp
// Sequencer 에 추가
private readonly Action<StallInfo>? _onStall;
private readonly long _stallThresholdTicks;

// Next() 의 대기 루프 안에서
long start = Stopwatch.GetTimestamp();
while (wrapPoint > (minSequence = GetMinimumGatingSequence()))
{
    spinWait.SpinOnce(sleep1Threshold: -1);

    long elapsed = Stopwatch.GetTimestamp() - start;
    if (elapsed > _stallThresholdTicks && !_stallReported)
    {
        _stallReported = true;
        _onStall?.Invoke(new StallInfo(
            ElapsedMs:        elapsed * 1000.0 / Stopwatch.Frequency,
            Cursor:           _cursor.Value,
            SlowestHandler:   FindSlowestHandlerName(),      // 게이팅 최솟값을 가진 핸들러
            SlowestSequence:  minSequence));
    }
}
```
```csharp
public readonly record struct StallInfo(
    double ElapsedMs, long Cursor, string SlowestHandler, long SlowestSequence);

// 사용:
// [WARN] Disruptor 'packet-pipeline' 프로듀서가 250ms 블록됨.
//        가장 느린 핸들러 = 'DbJournalHandler' (seq 1,204,331 / cursor 1,270,867, lag 66,536)
```

**핸들러 안 `await` 사용 탐지 (개발 빌드)**
```csharp
[Conditional("DEBUG")]
private void AssertNoAsyncContext()
{
    if (SynchronizationContext.Current != null || TaskScheduler.Current != TaskScheduler.Default)
        throw new InvalidOperationException(
            "Disruptor 핸들러 안에서 await 를 사용하면 이벤트 순서 보장이 깨집니다.");
}
```

**단일 프로듀서 오용 탐지 (개발 빌드)** — `ProducerType.Single`인데 여러 스레드가 게시하면 조용히 데이터가 깨진다. 잡기 매우 어려운 버그다.
```csharp
// SingleProducerSequencer
private int _producerThreadId;

[Conditional("DEBUG")]
private void AssertSingleProducerThread()
{
    int tid = Environment.CurrentManagedThreadId;
    int prev = Interlocked.CompareExchange(ref _producerThreadId, tid, 0);
    if (prev != 0 && prev != tid)
        throw new InvalidOperationException(
            $"ProducerType.Single 인데 여러 스레드가 게시했습니다 " +
            $"(최초 스레드 {prev}, 현재 {tid}). ProducerType.Multi 를 쓰세요.");
}
```

#### 5.7 테스트 / 벤치마크 / 패키징

**테스트 프로젝트 구성** (`Disruptor.Tests`, xUnit)

정확성 테스트는 **반드시 다음 4가지를 포함**해야 한다. 이번에 발견된 결함이 전부 여기서 잡힌다.

```csharp
// (1) 백프레셔 계약 — §3.1 을 잡는 테스트
[Theory]
[InlineData(ProducerType.Single)]
[InlineData(ProducerType.Multi)]
public void Next_MustBlock_WhenBufferIsFull(ProducerType type)
{
    var rb = RingBuffer<Evt>.Create(type, () => new Evt(), 8, new YieldingWaitStrategy());
    var gating = new Sequence();        // 컨슈머가 아무것도 소비하지 않은 상태
    rb.AddGatingSequences(gating);

    for (int i = 0; i < 8; i++) rb.Publish(rb.Next());          // 버퍼를 정확히 채운다

    var task = Task.Run(() => rb.Next());
    Assert.False(task.Wait(TimeSpan.FromMilliseconds(500)),
                 "버퍼가 가득 찼는데 Next() 가 반환되었다 — 백프레셔 계약 위반");

    gating.SetValue(0);                                          // 한 칸 비우면
    Assert.True(task.Wait(TimeSpan.FromSeconds(2)));             // 진행되어야 한다
}

// (2) 무손실 스트레스 — 프로듀서 N × 이벤트 M 을 모두 소비했는지 단언
[Theory]
[InlineData(1, 1_000_000, 1024)]
[InlineData(8,   100_000, 1024)]      // 링을 작게 잡아 백프레셔를 강제로 유발
[InlineData(16,   50_000,   64)]
public void AllPublishedEvents_AreConsumedExactlyOnce(int producers, int perProducer, int ring)
{
    // 각 슬롯에 자기 시퀀스를 기록하고, 컨슈머가 seq 일치 + 총건수를 검증
    // Assert 로 타임아웃을 반드시 실패 처리할 것 (기존 Example 의 실수를 반복하지 말 것)
}

// (3) 의존성 순서 보장 — 하위 핸들러는 상위보다 앞서지 못한다
[Fact]
public void Downstream_NeverOvertakes_Upstream() { /* 각 시퀀스에서 upstream.Seq >= downstream.Seq 단언 */ }

// (4) 종료 계약 — Start 직후 Shutdown 이 즉시 끝나야 한다 (§3.6)
[Fact]
public void Shutdown_ImmediatelyAfterStart_CompletesQuickly()
{
    var sw = Stopwatch.StartNew();
    using (var d = new Disruptor<Evt>(() => new Evt(), 1024)) { d.HandleEventsWith(new Noop()); d.Start(); }
    Assert.True(sw.ElapsedMilliseconds < 500, $"Shutdown 이 {sw.ElapsedMilliseconds}ms 걸렸다");
}
```

**CPU 예산 테스트** (§3.3 회귀 방지)
```csharp
[Fact]
public void BlockingStrategy_DoesNotBurnCpu_WhenIdle()
{
    // 다이아몬드 구성 + 느린 상위 핸들러 + BlockingWaitStrategy
    // TotalProcessorTime 증가분 / 실경과 < 0.2 코어 를 단언
}
```

**벤치마크 프로젝트** (`Disruptor.Benchmarks`, BenchmarkDotNet)

기존 `Example`의 자체 `Stopwatch` 측정은 워밍업/JIT/GC 통제가 없어 신뢰할 수 없다. 최소 다음 시나리오를 구성한다.

| 시나리오 | 비교 대상 |
|---|---|
| Unicast 1P→1C | `Channel<T>`(SPSC), `ConcurrentQueue<T>` |
| Multicast 1P→3C | `Channel<T>` 3개 |
| Pipeline 1P→3C(직렬) | `TPL Dataflow` |
| Diamond 1P→(2C)→1C | `TPL Dataflow` |
| MP 8P→1C, 링 1024 (백프레셔 유발) | `Channel<T>`(bounded) |
| **지연 분포 (p50/p99/p99.9/p99.99/max)** | 위 전부 — 게임 서버는 평균이 아니라 tail 이 중요 |

> 지연 측정은 `HdrHistogram.NET` 사용을 권장한다. 처리량보다 **p99.9 / max** 를 릴리스 게이트로 삼는 것이 게임 서버답다.

**패키징 / CI**
```xml
<!-- Disruptor.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>

    <!-- 성능 라이브러리 필수 설정 -->
    <ServerGarbageCollection>true</ServerGarbageCollection>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <IsAotCompatible>true</IsAotCompatible>
    <TieredPGO>true</TieredPGO>

    <!-- 패키징 -->
    <PackageId>DisruptorCS</PackageId>
    <Version>0.1.0</Version>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>
</Project>
```

- 단일 솔루션(`DisruptorCS.sln`)으로 `Disruptor` / `Disruptor.Tests` / `Disruptor.Benchmarks` / `Example` 통합. 현재 `.sln`이 2개로 쪼개져 있어 전체 빌드/테스트가 불가능하다.
- GitHub Actions: `dotnet test` + 스트레스 테스트 반복 실행(경쟁 조건은 1회 실행으로 안 잡힌다. `--filter Category=Stress`를 20회 반복).
  > 실제 적용 시점에는 CI를 쓰지 않기로 결정했습니다. 동일한 검증을 수동 절차로 대체했습니다 — [§9.3](#93-테스트--벤치마크) 참조.
- **정확성 테스트는 반드시 `Release` 구성에서도 실행할 것.** 메모리 순서 관련 결함은 JIT 최적화가 켜져야 드러난다.

---

### Phase 2 — 성능

#### 5.8 `ValueRingBuffer<T> where T : struct` — 게임 서버에 가장 값어치 있는 최적화

현재 `RingBuffer<T> where T : class` 는 슬롯이 **참조 배열**이다. 이벤트 하나를 읽을 때마다:
1. `_entries[index]` 로드 (캐시 라인 A)
2. 참조를 따라가 객체 헤더 + 필드 로드 (캐시 라인 B — **거의 확실히 캐시 미스**)

배치로 1,000건을 처리하면 **1,000번의 포인터 추적**이 일어난다. 이벤트가 값 타입이면 슬롯이 배열에 인라인으로 놓여 순차 접근이 되고, 하드웨어 프리페처가 정확히 동작한다.

```csharp
// Disruptor/ValueRingBuffer.cs (신규)
public sealed class ValueRingBuffer<T> where T : struct
{
    private readonly T[] _entries;
    private readonly int _indexMask;
    private readonly ISequencer _sequencer;

    /// <summary>슬롯을 ref 로 반환한다 — 복사 없음.</summary>
    public ref T this[long sequence]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _entries[(int)sequence & _indexMask];
    }
}

public interface IValueEventHandler<T> where T : struct
{
    void OnEvent(ref T data, long sequence, bool endOfBatch);
    void OnBatchStart(long batchSize) { }
    void OnTimeout(long lastSequence) { }
}
```

게임 커맨드를 값 타입으로 설계하면 이렇게 된다.
```csharp
[StructLayout(LayoutKind.Sequential)]
public struct GameCommandValue
{
    public long   SessionId;
    public int    RoomId;
    public ushort OpCode;
    public ushort PayloadLength;
    public long   ReceivedTicks;
    public int    PayloadPoolIndex;   // 참조 대신 풀 인덱스 → GC 추적 대상 0
}   // 정확히 32바이트 → 캐시 라인 하나에 2개
```

**핵심 이점**: 구조체에 참조 필드가 없으면 배열 전체가 **GC 스캔 대상에서 제외**된다. 1M 슬롯 링 버퍼가 Gen2 마킹 비용에 전혀 기여하지 않는다. 게임 서버의 GC 정지 시간에 직접적으로 기여하는 최적화다.

**슬롯 간 false sharing 주의** — 여러 컨슈머가 인접 슬롯을 동시에 읽고 쓰면 캐시 라인이 핑퐁한다. 구조체 크기를 **캐시 라인의 배수(64B, 128B)** 로 맞추거나 패딩을 넣는다.

#### 5.9 배열 경계 검사 제거

```csharp
// 전
public T this[long sequence] => _entries[(int)sequence & _indexMask];

// 후 — 마스크 연산으로 이미 범위가 보장되므로 경계 검사가 순수 낭비
public T this[long sequence]
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    get => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries),
                      (nint)((int)sequence & _indexMask));
}
```
> `_indexMask == BufferSize - 1` 이고 `BufferSize` 가 2의 거듭제곱임이 생성자에서 강제되므로 인덱스는 항상 유효하다. `AllowUnsafeBlocks` 필요.

#### 5.10 `Sequence` 패딩 조정

```csharp
// Intel 스트리밍 프리페처는 인접 캐시 라인 쌍(128B)을 함께 로드한다.
// 좌우 128B 를 확보하면 프리페치로 인한 간섭까지 차단된다.
[StructLayout(LayoutKind.Explicit, Size = 128 + 8 + 128)]
public sealed class Sequence
{
    public const long InitialCursorValue = -1L;

    [FieldOffset(128)]
    private long _value;
    // ...
}
```
> 개수가 많지 않은 객체(핸들러 수 + 시퀀서 수 정도)이므로 메모리 증가는 무시할 수 있다.

#### 5.11 스레드 제어 (우선순위 / 코어 고정)

```csharp
// Disruptor/ProcessorThreadOptions.cs (신규)
public sealed class ProcessorThreadOptions
{
    public ThreadPriority Priority { get; init; } = ThreadPriority.Normal;
    public int?  CpuAffinity      { get; init; }          // 고정할 논리 코어 번호
    public int   MaxStackSize     { get; init; } = 256 * 1024;   // 기본 1MB 는 과하다
    public string? NameSuffix     { get; init; }
}

internal static class ThreadAffinity
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr h, UIntPtr mask);

    /// <summary>현재 스레드를 지정 논리 코어에 고정한다. 지원하지 않는 플랫폼에서는 무시.</summary>
    public static void PinToCore(int coreIndex)
    {
        if (OperatingSystem.IsWindows())
            SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << coreIndex));
        // Linux: sched_setaffinity P/Invoke, macOS: 미지원(no-op)
    }
}
```
> **코어 고정은 양날의 검이다.** 고정한 코어에 다른 부하가 몰리면 오히려 느려진다. 전용 베어메탈 게임 서버에서 프로파일링으로 이득을 확인한 뒤에만 적용하고, 컨테이너 환경에서는 기본 비활성으로 둔다.

---

### Phase 3 — 게임 서버 도메인 레이어 (이 프로젝트의 존재 이유)

여기까지가 "범용 Disruptor를 제대로 만드는" 작업이라면, 아래는 **`disruptor-net`을 그대로 쓰지 않고 자체 구현을 유지할 명분**이 되는 부분이다.

#### 5.12 `ShardedDisruptor` — 수천 룸을 코어 수만큼의 스레드로

```csharp
// Disruptor/Game/ShardedDisruptor.cs (신규)
/// <summary>
/// 키(룸 ID, 세션 ID 등)를 샤드에 매핑해, 같은 키의 이벤트는 항상 같은 샤드에서
/// 같은 순서로 처리되도록 보장한다. 샤드 수 = 스레드 수 이므로 룸 수와 무관하게 확장된다.
/// </summary>
public sealed class ShardedDisruptor<T> : IDisposable where T : class
{
    private readonly Dsl.Disruptor<T>[] _shards;
    private readonly RingBuffer<T>[] _rings;
    private readonly int _shardMask;

    public ShardedDisruptor(
        int shardCount,                        // 2의 거듭제곱, 보통 물리코어수 - 2
        int ringSizePerShard,
        Func<T> eventFactory,
        Func<int, IEventHandler<T>> handlerFactory,   // 샤드 인덱스별 핸들러
        ProducerType producerType = ProducerType.Multi,
        Func<IWaitStrategy>? waitStrategyFactory = null)
    {
        if ((shardCount & (shardCount - 1)) != 0)
            throw new ArgumentException("shardCount must be a power of 2");

        _shardMask = shardCount - 1;
        _shards = new Dsl.Disruptor<T>[shardCount];
        _rings  = new RingBuffer<T>[shardCount];

        for (int i = 0; i < shardCount; i++)
        {
            var d = new Dsl.Disruptor<T>(eventFactory, ringSizePerShard, producerType,
                        waitStrategyFactory?.Invoke() ?? new TimeoutBlockingWaitStrategy(TimeSpan.FromMilliseconds(1)));
            d.HandleEventsWith(handlerFactory(i));
            _shards[i] = d;
        }
    }

    public void Start()
    {
        for (int i = 0; i < _shards.Length; i++) _rings[i] = _shards[i].Start();
    }

    /// <summary>키를 샤드에 고정 매핑. 같은 키는 항상 같은 샤드 = 순서 보장.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ShardOf(long key) => (int)(Mix(key) & (uint)_shardMask);

    /// <summary>룸 ID 가 연속 정수여도 균등 분산되도록 섞는다 (splitmix64 finalizer).</summary>
    private static ulong Mix(long key)
    {
        ulong z = (ulong)key + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public bool TryPublish(long key, Action<T, long> fill, TimeSpan timeout)
    {
        var ring = _rings[ShardOf(key)];
        if (!ring.TryNext(out long seq, timeout)) return false;
        try { fill(ring[seq], seq); } finally { ring.Publish(seq); }
        return true;
    }

    public void Dispose() { foreach (var s in _shards) s.Dispose(); }
}
```

**메모리/스레드 비교 (10,000 룸 기준)**

| 방식 | 스레드 | 링 메모리 | 판정 |
|---|---:|---:|---|
| 룸당 Disruptor (링 1024) | 10,000 | ~1.3 GB | 불가능 |
| 룸당 `Channel<T>` | 0 (ThreadPool) | ~수십 MB | 가능하지만 순서/배치 이점 없음 |
| **ShardedDisruptor (샤드 16, 링 4096)** | **16** | **~8 MB** | **권장** |

> 샤딩의 대가: 한 룸이 무거우면 같은 샤드의 다른 룸이 함께 밀린다(head-of-line blocking). 룸별 처리 시간 상한을 두고, 초과 시 별도의 "느린 작업" 워커 풀(§5.4)로 밀어내는 설계를 함께 둔다.

#### 5.13 틱 기반 게임 루프 통합

```csharp
/// <summary>
/// 이벤트 소비와 고정 틱 시뮬레이션을 한 스레드에서 결합한다.
/// 이벤트가 없어도 OnTimeout 이 주기적으로 호출되므로 틱이 밀리지 않는다.
/// </summary>
public sealed class TickedRoomHandler : IEventHandler<GameCommand>
{
    private const long TickIntervalTicks = TimeSpan.TicksPerMillisecond * 16;   // ~60Hz
    private readonly Dictionary<int, RoomState> _rooms = new();
    private long _nextTickAt = Stopwatch.GetTimestamp();
    private readonly List<GameCommand> _pending = new(256);

    public void OnBatchStart(long batchSize) => _pending.Clear();

    public void OnEvent(GameCommand cmd, long seq, bool endOfBatch)
    {
        // 커맨드는 즉시 실행하지 않고 현재 틱 버킷에 모은다 → 틱 내 결정론 확보
        _pending.Add(cmd.Snapshot());

        if (endOfBatch) TryAdvanceTick();
    }

    /// <summary>이벤트가 없어도 호출된다 (TimeoutBlockingWaitStrategy).</summary>
    public void OnTimeout(long lastSequence) => TryAdvanceTick();

    private void TryAdvanceTick()
    {
        long now = Stopwatch.GetTimestamp();
        while (now >= _nextTickAt)
        {
            foreach (var cmd in _pending) _rooms[cmd.RoomId].Apply(cmd);
            _pending.Clear();

            foreach (var room in _rooms.Values) room.Simulate();     // 고정 스텝
            foreach (var room in _rooms.Values) room.FlushSnapshot(); // 배치 송신

            _nextTickAt += TickIntervalTicks;

            // 틱이 크게 밀렸으면 따라잡기를 포기하고 현재 시각으로 리셋 (death spiral 방지)
            if (now - _nextTickAt > TickIntervalTicks * 5)
            {
                _metrics.TickSkipped();
                _nextTickAt = now + TickIntervalTicks;
                break;
            }
        }
    }
}
```

#### 5.14 결정론적 저널 / 리플레이

Disruptor를 게임 서버에 쓰는 가장 강력한 명분이다.

```csharp
/// <summary>
/// 시퀀스 번호 = 전역 실행 순서. 이 순서대로 재생하면 서버 상태가 정확히 재현된다.
/// 로직 핸들러보다 반드시 먼저 실행되어야 하므로 HandleEventsWith 의 첫 단계에 둔다.
/// </summary>
public sealed class JournalHandler : IEventHandler<GameCommand>
{
    private readonly IBufferWriter<byte> _writer;   // 메모리 매핑 파일 권장

    public void OnEvent(GameCommand cmd, long seq, bool endOfBatch)
    {
        WriteRecord(_writer, seq, cmd);       // (sequence, tickId, opCode, payload)

        if (endOfBatch)
            Flush();                          // 배치 끝에서만 fsync → I/O 비용 1/N
    }

    public void OnTimeout(long lastSeq) => Flush();   // 유휴 시에도 데이터 유실 방지
}

// 파이프라인 구성 — 저널이 기록된 뒤에만 로직이 커밋된다
disruptor.HandleEventsWith(new JournalHandler(writer), new ReplicationHandler(standby))
         .Then(new GameLogicHandler(world));
```

**리플레이**
```csharp
public static void Replay(string journalPath, GameLogicHandler logic)
{
    foreach (var (sequence, cmd) in JournalReader.Read(journalPath))
        logic.OnEvent(cmd, sequence, endOfBatch: true);   // 동일 순서 → 동일 상태
}
```
이 구조가 확보되면 다음이 전부 가능해진다.
- 버그 리포트의 정확한 재현 (시퀀스 구간만 재생)
- 핫 스탠바이 노드로의 상태 복제
- 치팅 검증 (클라이언트 주장 vs 서버 저널 대조)
- 서버 크래시 후 마지막 저널 시퀀스부터 복구

---

## 6. 도입 판단 기준

### 6.1 결정 순서도

```
1. 하나의 이벤트를 2개 이상의 소비자가 "각자" 봐야 하는가?
   ├─ Yes → Disruptor 후보  (Channel 은 소비자마다 복제 필요)
   └─ No  → 2번

2. 처리 순서가 전역적으로 고정되어야 하는가? (리플레이/롤백/저널)
   ├─ Yes → Disruptor 후보  (시퀀스 번호가 곧 전역 순서)
   └─ No  → 3번

3. p99.9 지연이 SLA 인가? 그리고 그 값이 1ms 미만인가?
   ├─ Yes → Disruptor 후보
   └─ No  → Channel<T> 를 쓴다.  (여기서 멈춰도 아무 문제 없다)

4. Disruptor 후보라면, 다음을 모두 만족하는가?
   ├─ 핸들러 스레드 총합 ≤ 논리코어 − 4        → 아니면 샤딩 필요 (§5.12)
   ├─ 핸들러 안에서 await / 블로킹 I/O 없음     → 아니면 WorkerPool 로 분리 (§5.4)
   └─ 링 버퍼 개수가 수십 개 이내               → 아니면 샤딩 필수
      └─ 모두 Yes → 도입
```

### 6.2 게임 서버 컴포넌트별 최종 권고

| 컴포넌트 | 권고 | 이유 |
|---|---|---|
| 패킷 수신 → 디코드 → 로직 파이프라인 | **Disruptor** | 멀티캐스트 + 순서 + 배치 |
| 저널링 / 리플레이 / 스탠바이 복제 | **Disruptor (최우선)** | 시퀀스 = 전역 순서 |
| 룸/존 커맨드 큐 (수천 개) | **ShardedDisruptor** | 룸당 Disruptor는 금지 |
| 비동기 로깅 / 메트릭 | **Disruptor** | Log4j2와 동일한 근거 |
| 랭킹 / 재화 정산 집계 | **Disruptor** | 배치 커밋 이점 |
| DB 쓰기 / 외부 API 호출 | **WorkerPool** 또는 `Channel<T>` | 순서 무관 + 느린 I/O |
| 요청-응답 RPC | `Channel<T>` / `TaskCompletionSource` | 단방향 모델과 불일치 |
| HTTP 관리 API, 어드민 | `ASP.NET Core` 기본 | 지연 요구 없음 |

---

## 7. 우선순위 및 공수 산정

| 단계 | 항목 | 심각도/가치 | 공수 | 누적 |
|---|---|:---:|---:|---:|
| **P0** | MP 백프레셔 수정 (§3.1) | 🔴 필수 | 0.5h | 0.5h |
| P0 | `SpinOnce(-1)` 적용 (§3.2) | 🔴 필수 | 0.5h | 1h |
| P0 | 예외 핸들러 재설계 (§3.4) | 🔴 필수 | 4h | 5h |
| P0 | lost-wakeup 수정 (§3.5) | 🟡 높음 | 1h | 6h |
| P0 | `SequenceWaitResult` + `CancellationToken` 전환 (§3.6, §3.7) | 🟡 높음 | 8h | 14h |
| P0 | `DependentSequenceGroup` + 신호 전파 (§3.3) | 🔴 필수 | 6h | 20h |
| P0 | 회귀 테스트 스위트 (§5.7) | 🔴 필수 | 12h | 32h |
| **P1** | `TimeoutBlockingWaitStrategy` + `OnTimeout`/`OnBatchStart` (§5.1, §5.2) | ⭐ 매우 높음 | 8h | 40h |
| P1 | 백프레셔 정책 API (§5.5) | ⭐ 매우 높음 | 6h | 46h |
| P1 | 관측성 / 스톨 감지 (§5.6) | ⭐ 매우 높음 | 12h | 58h |
| P1 | 슬롯 `IClearable` 정리 (§5.3) | 높음 | 4h | 62h |
| P1 | `WorkerPool` (§5.4) | 중간 | 10h | 72h |
| P1 | 멀티 타깃 + NuGet + CI (§5.7) | 높음 | 6h | 78h |
| **P2** | `ValueRingBuffer<T>` (§5.8) | ⭐ 높음 | 16h | 94h |
| P2 | 경계 검사 제거 / 패딩 조정 (§5.9, §5.10) | 중간 | 4h | 98h |
| P2 | 스레드 우선순위/코어 고정 (§5.11) | 낮음 | 6h | 104h |
| P2 | BenchmarkDotNet 스위트 (§5.7) | 높음 | 12h | 116h |
| **P3** | `ShardedDisruptor` (§5.12) | ⭐ 매우 높음 | 12h | 128h |
| P3 | 틱 루프 통합 헬퍼 (§5.13) | ⭐ 높음 | 10h | 138h |
| P3 | 저널/리플레이 프레임워크 (§5.14) | ⭐ 매우 높음 | 20h | 158h |

**최소 실용 지점**: P0 완료(약 32h, 4~5일) 시점부터 "안전하게 쓸 수 있는 라이브러리"가 된다.
**게임 서버 도입 지점**: P0 + P1 완료(약 78h, 2주) 시점부터 실서비스 파이프라인에 올릴 수 있다.
**차별화 지점**: P3 완료(약 158h, 4주) 시점부터 `disruptor-net`을 쓰지 않고 자체 구현을 유지할 명분이 생긴다.

---

## 8. 마무리 — 이 라이브러리를 계속 가져갈 것인가

**계속 가져갈 가치가 있다.** 단, 조건이 붙는다.

- **범용 Disruptor 클론으로 남긴다면 가치는 낮다.** `disruptor-net`이 이미 성숙해 있고, 이번에 확인된 것처럼 lock-free 코드는 미묘한 이식 실수 하나로 조용히 데이터를 잃는다. 그 리스크를 자체 부담할 이유가 없다.
- **게임 서버 도메인 레이어(§5.12~§5.14)를 얹는다면 가치가 크다.** 샤딩, 틱 통합, 결정론적 저널·리플레이는 범용 라이브러리가 제공하지 않는 영역이고, 조직의 게임 서버 전반에 재사용된다. 이 부분이 진짜 자산이다.
- **어느 쪽을 택하든 P0는 무조건 먼저다.** 지금 코드는 `ProducerType.Multi`에서 이벤트의 99%를 잃는다. 벤치마크 숫자가 좋아 보였던 것은 백프레셔를 건너뛰었기 때문이다.

가장 실용적인 경로는 **하부는 `disruptor-net`을 쓰고, 그 위에 §5.12~§5.14의 게임 서버 레이어를 이 저장소에 구현하는 것**이다. 자체 구현을 유지하기로 결정한다면, §7의 P0 32시간을 먼저 확보하고 회귀 테스트를 CI에 걸어야 한다.

---

### 부록 A. 검증에 사용한 프로브 요약

| 프로브 | 구성 | 핵심 결과 |
|---|---|---|
| P1 | MP 8P, 링 1024, 1.6M건 | 30초 타임아웃, max 441ms, 처리량 52K ops/s (결함 영향) |
| P2 | MP 8P, 링 1024, 800K건, 시퀀스 검증 | **유실 794,332건(99.3%), 컨슈머 영구 정지** → 수정 후 **유실 0** |
| P3 | 다이아몬드, Blocking, 느린 상위 핸들러 | CPU **0.98코어** 상시 점유 |
| P4 | 컨슈머 미시작, 링 1024, 1025번째 `Next()` | **반환됨(계약 위반)** → 수정 후 **정상 블록** |
| P5 | Blocking, 유휴 후 단발 게시 20,000회 | p50 11.7μs, max 2.39ms, 영구 손실 미관측 |
| P6 | SP, 링 1024, 300K건, 느린 컨슈머 | >15ms **284건** → `SpinOnce(-1)` 적용 후 **0건**, 처리량 +47% |

### 부록 B. 참고 자료

- LMAX Disruptor 기술 백서 — Martin Thompson 외, *"Disruptor: High performance alternative to bounded queues"*
- `disruptor-net` (C# 포팅, 활발히 유지보수 중) — `SequenceWaitResult`, `DependentSequenceGroup`, `ValueRingBuffer<T>` 등 이 문서의 여러 개선안이 이미 반영되어 있다
- .NET `SpinWait.SpinOnce(int sleep1Threshold)` — .NET Core 3.0+
- `System.Diagnostics.Metrics.Meter` — OpenTelemetry 호환 메트릭 API

---

## 9. 구현 완료 현황

> 구현일: 2026-08-27 · 검증 환경: Intel i7-12700KF (12P/20L), Windows 11, .NET 10.0.400, Release/x64, Server GC

§3의 결함 12건과 §5의 개선 항목 P0~P3을 **전부 구현**했습니다.
이 장은 "무엇을 어떻게 적용했고, 무엇으로 검증했는가"만 기록합니다. 설계 근거는 앞 장을 보세요.

### 9.1 결함 수정 결과

| # | 결함 | 수정 위치 | 검증 |
|---|---|---|---|
| 3.1 | **MP 백프레셔 무력화** (`do…while` + `continue`) | `MultiProducerSequencer.Next()` → `while(true)` + CAS 분리 | `Next_MustBlock_WhenBufferIsFull` (SP/MP), `AllPublishedEvents_AreConsumedExactlyOnce` 6개 구성 |
| 3.2 | `SpinOnce()`의 `Thread.Sleep(1)` 15ms 스톨 | 모든 대기 경로가 `SpinOnceWithoutSleep()` 사용, `IProducerBackoff`로 정책 주입 | 지연 분포 측정 (§9.4) |
| 3.3 | Blocking 전략 + 의존성 체인 → 1코어 상시 점유 | `DependentSequenceGroup` 도입 + `BatchEventProcessor`가 시퀀스 갱신 시 신호 전파 | `BlockingStrategy_DoesNotBurnCpu_WithSlowUpstream` (0.98코어 → **0.07코어**) |
| 3.4 | 기본 예외 핸들러가 프로세스를 죽임 | `IExceptionHandler`가 `EventExceptionAction` 반환, 기본값 `ResilientExceptionHandler` | `HandlerException_DoesNotKillProcess_AndSkipsEvent`, `HaltAction_StopsOnlyThatProcessor_AndRaisesEvent`, `RetryAction_ReprocessesSameEvent` |
| 3.5 | `_numWaiters` lost-wakeup 경쟁 | 최적화 제거 + 무조건 `PulseAll` + `Monitor.Wait(gate, 100)` 이중 방어 | `TimeoutStrategy_InvokesOnTimeout_WhenIdle`, wake-up 지연 측정 |
| 3.6 | Start/Shutdown 경쟁으로 종료 5초 지연 | `Start()`가 `CountdownEvent`로 프로세서 준비 완료를 대기, 정지 신호를 `CancellationToken`으로 단일화 | `Shutdown_ImmediatelyAfterStart_CompletesQuickly` (<1초 단언) |
| 3.7 | `AlertException` 싱글턴 공유 | `SequenceWaitResult` readonly struct 로 전면 교체, `AlertException.cs` 삭제 | `SequenceWaitResult_DistinguishesStates` |
| 3.8-a | `DateTime.UtcNow` 데드라인 | 전부 `Stopwatch.GetTimestamp()` | — |
| 3.8-b | `YieldingWaitStrategy`에 pause 힌트 없음 | 스핀 구간에 `Thread.SpinWait(1)` 추가 | — |
| 3.8-c | `SpinWaitWaitStrategy` 주석 오류 | 주석 수정 + 진짜 `SleepingWaitStrategy` 추가 | — |
| 3.8-d | Multi에서 드레인 기준이 "점유" 커서 | `GetHighestPublishedSequence` 기준으로 드레인 | `Shutdown_DrainsPendingEvents` |
| 3.8-e | 슬롯이 외부 참조를 붙들어 객체 수명 연장 | `IClearable` + 리프 프로세서 자동 `Clear()` | `LeafProcessor_ClearsSlotReferences_AfterProcessing` 외 2건 |
| 3.8-f | `Sequence` 패딩 부족 | `Size=264`, `FieldOffset(128)` — 좌우 128B | — |
| 3.8-g | `GetMinimumSequence` 시드 불일치 | `_nextValue` 시드로 통일 | — |
| 3.8-h | `Next()`에 취소/타임아웃 없음 | `Next(CancellationToken)`, `TryNext(out, TimeSpan)`, `IProducerBackoff`, 스톨 감지 | `Next_WithCancellation_ThrowsInsteadOfHangingForever`, `ProducerBackoff_CanFailFast_InsteadOfBlockingForever`, `StallDetection_ReportsSlowestHandler` |
| 3.8-i | 게이팅 갱신이 `Remove` → `Add` 순서 | `Add` → `Remove` 로 교정 (`UpdateGatingSequences`) | — |
| 3.8-j | `net10.0` 단독 타깃 | `net8.0;net10.0` 멀티 타깃 | 솔루션 전체 Release/Debug 빌드 경고 0개 |
| 3.8-k | 테스트/벤치마크/패키징 없음 | 4개 프로젝트 단일 솔루션 + NuGet 메타데이터 (CI는 사용하지 않기로 결정) | §9.3 |

#### 구현 중 추가로 발견해 수정한 결함 2건

테스트를 작성하는 과정에서 **설계서에 없던 결함 2건**이 새로 드러났습니다.

**(A) 워커 풀만 있는 파이프라인에서 종료 드레인이 통째로 건너뛰어짐**
`DrainProcessors`가 `RegisteredProcessors.Count == 0`이면 즉시 반환하는데, 워커 풀은 자체 스레드를 관리하므로 이 목록이 비어 있습니다. 그 결과 드레인 없이 곧바로 취소되어 **20,000건 중 908건이 유실**됐습니다.
또한 `MinimumProcessorSequence()`가 "추적 대상 없음"을 `-1`로 표현해, 재정의에서 `Math.Min`으로 좁히면 값이 `-1`에 고정되어 드레인이 영원히 완료되지 않는 문제도 함께 있었습니다.

```csharp
// 수정: "없음"을 long.MaxValue 로 표현해 Math.Min 합성이 성립하게 한다
protected virtual long MinimumProcessorSequence()
{
    long min = long.MaxValue;
    foreach (var p in RegisteredProcessors) min = Math.Min(min, p.Sequence.Value);
    return min;          // 이전: min == long.MaxValue ? -1 : min   ← 합성이 깨지는 원인
}
```
검증: `WorkerPool_ProcessesEachEventExactlyOnce` (20,000건, 중복 0 / 유실 0).

**(B) `HasRegisteredWork` 판정 누락**
`HandleEventsWithWorkerPool`만 등록하고 `Start()`하면 "등록된 핸들러가 없습니다" 예외가 났습니다. 워커 풀을 포함하도록 판정을 재정의했습니다.

> 두 결함 모두 §3.1과 같은 성격입니다 — **"드레인/게이팅 경로에 테스트가 없으면 조용히 데이터를 잃는다."**
> 설계서가 §5.7에서 지적한 대로, 테스트를 먼저 쓰지 않았다면 그대로 남았을 결함입니다.

### 9.2 신규 구현 목록

**코어 (P0~P2)**

| 파일 | 내용 |
|---|---|
| `SequenceWaitResult.cs` | 예외 없는 대기 결과 (8바이트 readonly struct) |
| `DependentSequenceGroup.cs` | 대기 전략이 보는 "의존 대상" 1급 개념 (길이 0/1 최적화 포함) |
| `SequencerBase.cs` | 게이팅 관리 / 배리어 생성 / 스톨 감지 공통 기반 |
| `IProducerBackoff.cs` | `ProducerBackoffState`(스택 상태) + `SpinOnly` / `TimeBounded` / `Sleeping` 정책 |
| `StallInfo.cs` · `SequencerOptions` | 스톨 진단 정보 및 시퀀서 옵션 |
| `IExceptionHandler.cs` · `ExceptionHandlers.cs` | `EventExceptionAction` 반환 + `Resilient`/`Ignore`/`HaltOn` 3종 |
| `IClearable.cs` | 슬롯 참조 해제 계약 |
| `IEventProcessor.cs` | 프로세서 공통 인터페이스 |
| `HandlerValidation.cs` | DEBUG 전용 `async` 핸들러 검출 (리플렉션, AOT 안전하게 억제) |
| `DisruptorMetrics.cs` | `System.Diagnostics.Metrics` 기반 8종 지표 + lag/utilization 게이지 |
| `ProcessorThreadOptions.cs` | 우선순위 / 스택 크기 / 코어 고정 (Windows·Linux P/Invoke) |
| `BackpressurePolicy.cs` | `PublishGate<T>` — Block / DropWhenCongested / FailAfterTimeout |
| `ValueRingBuffer.cs` · `IValueEventHandler.cs` · `ValueBatchEventProcessor.cs` | 값 타입 링 버퍼 (`ref T` 반환, GC 스캔 제외) |
| `IRingBufferCore.cs` | 참조/값 링 버퍼 공통 조정 인터페이스 |
| `WaitStrategy/TimeoutBlockingWaitStrategy.cs` | **게임 서버 기본 전략** — `OnTimeout` 훅 제공 |
| `WaitStrategy/SleepingWaitStrategy.cs` · `PhasedBackoffWaitStrategy.cs` | 저CPU / 적응형 전략 |
| `Dsl/DisruptorBase.cs` · `DisruptorOptions.cs` · `ValueDisruptor.cs` | 수명 주기 공통 기반 + 옵션 + 값 타입 DSL |
| `Work/IWorkHandler.cs` · `WorkProcessor.cs` · `WorkerPool.cs` | 작업 분배 |

**게임 서버 레이어 (P3)**

| 파일 | 내용 |
|---|---|
| `Game/ShardedDisruptor.cs` | 키→샤드 고정 매핑 (splitmix64 finalizer), 샤드별 사용률/드롭 집계 |
| `Game/FixedTickDriver.cs` | 고정 스텝 틱 + death spiral 방지 |
| `Game/Journal.cs` | `JournalWriter` / `JournalHandler<T>` / `JournalReader` / `Replay` |

**인터페이스 확장**

```csharp
public interface IEventHandler<in T>
{
    void OnEvent(T data, long sequence, bool endOfBatch);
    void OnBatchStart(long batchSize) { }          // 신규 — 배치 인지 처리
    void OnTimeout(long lastProcessedSequence) { } // 신규 — 틱 / 플러시 / 하트비트
    void OnStart() { }
    void OnShutdown() { }
}
```

### 9.3 테스트 / 벤치마크

**테스트 60개** (`Disruptor.Tests`, xUnit, Release + Debug 양쪽 실행)

| 스위트 | 검증 대상 |
|---|---|
| `BackpressureTests` (14) | 백프레셔 계약, 취소, 타임아웃, 잔여 용량, 백오프 정책, 스톨 감지, 단일 프로듀서 오용 검출 |
| `LosslessStressTests` (10) | 무손실 (6개 구성: 1~16 프로듀서 × 링 64~1024), 멀티캐스트, 배치 효과 |
| `LifecycleTests` (8) | Start/Shutdown 계약, 드레인, 예외 3정책, OnStart/OnShutdown 1회 |
| `DependencyAndCpuTests` (4) | 순서 보장, 다이아몬드, **CPU 예산 < 0.5코어**, OnTimeout 주기 |
| `WorkerPoolTests` (2) | 정확히 한 번 처리, 체인 뒤 워커 풀 |
| `ShardedDisruptorTests` (3) | 키→샤드 고정, 분산 균등도, 룸별 순서 |
| `FixedTickDriverTests` (2) | 틱 주기, death spiral 방지 |
| `JournalTests` (4) | 왕복 재현, 잘린 파일 안전 종료, 외부 파일 거부, 파이프라인 통합 |
| `ValueRingBufferTests` (4) · `PublishGateTests` (3) | 값 타입 전달, `ref` 반환, 백프레셔, 정책별 드롭/거절 |
| `SlotClearingTests` (3) · `SequenceTests` (3) | 슬롯 정리, 리프 판정, 패딩/CAS/의존 그룹 |

> **타임아웃을 반드시 실패로 처리합니다.** 기존 예제는 `allDone.Wait(10초)`의 반환값을 검사하지 않아
> 이벤트 99%가 유실되어도 "성공"처럼 보였습니다(§3.1). 새 스위트는 이 실수를 반복하지 않습니다.
>
> **경쟁 조건 대비 8회 반복 실행 → 8/8 전부 통과.**
> 타이밍 의존 테스트를 위해 `[assembly: CollectionBehavior(DisableTestParallelization = true)]`를 적용했습니다
> (`Process.TotalProcessorTime`은 프로세스 전체 값이라 병렬 실행 시 CPU 예산 테스트가 흔들림).

**벤치마크** (`Disruptor.Benchmarks`)
- BenchmarkDotNet: Unicast(vs `Channel<T>`), Multicast(vs `Channel<T>` ×3), MP 백프레셔
- 지연 분포 전용 러너: `dotnet run -- latency` → p50/p90/p99/p99.9/p99.99/max + `>1ms`·`>5ms`·`>15ms` 건수

**패키징**: `net8.0;net10.0`, `IsAotCompatible`, `TreatWarningsAsErrors`, XML 문서, snupkg

**검증은 수동 실행**입니다(이 저장소는 CI를 사용하지 않습니다). 릴리스 전에 아래를 직접 돌리세요.

```bash
dotnet build DisruptorCS.slnx -c Release          # 경고 0개여야 한다 (TreatWarningsAsErrors)
dotnet test  Disruptor.Tests  -c Release          # 메모리 순서 결함은 Release 에서만 드러난다
dotnet test  Disruptor.Tests  -c Debug            # 단일 프로듀서 오용 / async 핸들러 검출은 Debug 전용
dotnet run   --project Example -c Release         # "유실 0 / 덮어쓰기 0 / 체크섬 일치" 확인
```

> **스트레스 스위트는 반드시 여러 번 돌리세요.** 경쟁 조건은 1회 실행으로 잡히지 않습니다.
> `dotnet test Disruptor.Tests -c Release --filter "Category=Stress"` 를 최소 10~20회 반복하는 것을 권장합니다
> (이번 검증에서는 최종 코드로 전체 스위트를 Release 6회 + Debug 1회 실행해 전부 통과시켰습니다.
> 직전 상태까지 포함하면 Release 누적 14회 반복 실행에서 실패 0건).

### 9.4 최종 측정 결과

**처리량 / 무결성** (`Example` 실행)

| 시나리오 | 개선 전 | 개선 후 |
|---|---|---|
| 다중 프로듀서 무결성 (8P × 100K, 링 1024) | **유실 794,332건(99.3%)**, 컨슈머 영구 정지 | **유실 0**, 덮어쓰기 0, 20.6M ops/s |
| 단일 프로듀서 처리량 (10M, 링 64K) | 63.2M ops/s (백프레셔 미발생 구간) | **66.4M ops/s**, 15.1 ns/op, **할당 0 bytes** |
| Blocking + 느린 상위 핸들러 CPU | **0.98 코어** | **0.07 코어** |
| 의존성 그래프 (다이아몬드, 100K) | — | 3개 핸들러 전부 100,000건, 최대 배치 1,024 |
| 룸 샤딩 (5,000룸 × 40건) | (기능 없음) | 200,000건 / 58ms / **스레드 8개**, 룸별 순서 보장 |
| 저널 → 리플레이 (20,000건) | (기능 없음) | 상태 체크섬 **완전 일치** |
| 스톨 감지 | (기능 없음) | 병목 핸들러 이름·lag 즉시 보고 |

**메모리**: 10,000,000 이벤트 처리 중 **총 할당 0 bytes** (0.0000 B/event).
사전 할당 + 슬롯 재사용 + 스택 기반 백오프 상태(`ProducerBackoffState`)의 결과입니다.

**지연 분포** (`dotnet run --project Disruptor.Benchmarks -c Release -- latency`)

백프레셔 발생 시 `Next()` 지연 — 링 1024, 300,000건, 느린 컨슈머:

| 구성 | p99.9 | p99.99 | max | **&gt;15ms** | 처리량 |
|---|---:|---:|---:|---:|---:|
| **개선 전 원본 코드** | 57.8μs | **16,054μs** | **26.51ms** | **284건** | 64,404 ops/s |
| SP / `SpinOnlyBackoff` (기본값) | 5,413μs | 7,957μs | 11.05ms | **0건** | **96,915 ops/s** |
| MP / `SpinOnlyBackoff` | 3,040μs | 9,337μs | 10.29ms | **0건** | 95,424 ops/s |
| SP / `SleepingBackoff` (저CPU 정책) | 35μs | 16,104μs | 30.39ms | 271건 | 64,518 ops/s |

> **15ms 절벽이 기본 경로에서 사라졌습니다** (284건 → 0건, 처리량 +50%).
> `SleepingBackoff`에서 절벽이 다시 나타나는 것은 **의도된 거래**입니다 —
> 이 정책만 `Thread.Sleep(1)`을 사용하며, CPU를 아끼는 대신 60Hz 틱 예산을 잃습니다.
> 코드 주석에 이 수치를 명시해 두었으니, 틱 스레드에는 선택하지 마세요.

유휴 후 단발 게시 wake-up 지연 — 20,000회:

| 대기 전략 | p50 | p99 | p99.9 | max | &gt;1ms |
|---|---:|---:|---:|---:|---:|
| `BusySpinWaitStrategy` | **0.9μs** | 6.2μs | 119μs | 0.48ms | 0 |
| `YieldingWaitStrategy` | **0.7μs** | 13.6μs | 169μs | 2.04ms | 1 |
| `BlockingWaitStrategy` | 11.6μs | 65.3μs | 173μs | 0.72ms | 0 |
| `TimeoutBlockingWaitStrategy(1ms)` | 11.6μs | 68.7μs | 290μs | 12.50ms | 2 |
| `PhasedBackoffWaitStrategy` | 16.0μs | 69.5μs | 193μs | 1.21ms | 1 |

> 블로킹 계열의 wake-up 비용은 **약 12μs**로, 스핀 계열(0.7~0.9μs)의 13~17배입니다.
> 틱 스레드에는 스핀 계열을, 컨테이너·초과 구독 환경에는 블로킹 계열을 쓰라는 §4.4의 권고가 수치로 확인됩니다.
> `PhasedBackoffWaitStrategy`는 이 시나리오(완전 유휴 후 단발)에서는 스핀 예산을 헛되이 태우고
> 폴백하므로 오히려 `BlockingWaitStrategy`보다 느립니다 — **간헐적 버스트**에 쓸 때만 이득입니다.

릴리스 게이트는 처리량이 아니라 **p99.9 와 `>15ms` 건수**로 잡는 것을 권장합니다.

### 9.5 남은 위험과 후속 과제

구현을 마쳤지만 **아직 닫히지 않은 것들**을 명시합니다.

| 항목 | 상태 | 대응 |
|---|---|---|
| **실서비스 부하 검증** | 미완 | 이 측정은 전부 단일 장비 합성 부하입니다. 실제 트래픽 패턴(버스트 후 유휴, 롱테일 룸)에서 재측정이 필요합니다 |
| **Linux 코어 고정** | 미검증 | `sched_setaffinity` P/Invoke는 Windows에서만 실측했습니다. Linux 실기 확인 필요 |
| **`WorkProcessor` 취소 시 in-flight 이벤트** | 설계상 한계 | 워커가 점유했지만 처리 전 취소되면 그 이벤트는 유실됩니다(LMAX 동일). `DrainAndHalt`로 완화하되, 무손실이 필요하면 워커 풀 대신 일반 핸들러를 쓰세요 |
| **lost-wakeup 경쟁** | 완화 | 무조건 `PulseAll` + 100ms 재확인으로 이중 방어했습니다. 원리적으로 창을 닫았다고 단정하기보다, 100ms 상한이 보장된다고 이해하는 편이 정확합니다 |
| **`ShardedDisruptor` head-of-line blocking** | 설계상 대가 | 한 룸이 무거우면 같은 샤드의 다른 룸이 밀립니다. 룸별 처리 시간 상한 + 초과 작업의 워커 풀 이전이 필요합니다 |
| **`disruptor-net` 대비 우위** | 부분적 | 코어는 이제 대등합니다. 자체 구현을 유지할 명분은 여전히 §5.12~5.14의 게임 서버 레이어입니다 |

**다음 단계 권고**
1. 실제 게임 서버의 패킷 파이프라인 한 곳에 시범 적용하고 `handler_lag` / `utilization` 대시보드를 먼저 세운다
2. 릴리스 전 체크리스트에 "스트레스 스위트 Release 20회 반복"을 고정한다 (CI를 쓰지 않으므로 수동 절차로 명문화할 것)
3. 지연 분포(p99.9, `>15ms` 건수)를 기준선으로 저장하고 회귀 시 빌드를 실패시킨다
