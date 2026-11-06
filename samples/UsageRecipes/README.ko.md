# DisruptorCS 활용 예제 13선

> [English](README.md) · **한국어** · [전체 샘플](../README.ko.md)

각 예제는 **어떤 문제에 적용하는가 → 어떤 API로 구성하는가 → 결과가 맞는가**를 보여준다.
하나의 프로젝트에서 예제 이름을 골라 실행하며, 구현은 예제별 `.cs` 파일로 분리되어 있다.
외부 DB·네트워크 서버·별도 설정 없이 동작한다. 비동기 저장은 지연을 넣은 메모리 저장소로 모사한다.
성능 측정용이 아니라 API의 사용법과 데이터 소유권을 배우기 위한 예제다.

## 실행 방법

저장소 루트에서 .NET 10 SDK로 실행한다. 라이브러리는 프로젝트 참조로 연결된다.
기존 프로젝트에 적용할 때는 `DisruptorCS` NuGet 패키지를 사용하면 된다.

```bash
# 목록과 간단한 설명
dotnet run --project samples/UsageRecipes -c Release -- --list

# 개별 실행: 마지막 이름을 아래 표의 실행 이름으로 바꾼다
dotnet run --project samples/UsageRecipes -c Release -- pipeline

# 13개 전체 실행 및 결과 검증
dotnet run --project samples/UsageRecipes -c Release -- all
```

성공한 예제는 `PASS`를 출력한다. 프로그램 자체의 종료 코드는 검증 실패·대기 시간 초과 `1`,
잘못된 인자 `2`, 성공 `0`이다. `dotnet run`은 실패 코드를 `1`로 전달할 수 있으므로 세부 코드를
구분하려면 빌드한 DLL을 `dotnet`으로 직접 실행한다. 인자 없이 실행하면 목록만 표시한다.
배치 호출 수와 워커별 처리량은 스케줄링에
따라 달라지지만, 처리 건수·합계·순서·소유권 조건은 코드에서 검증한다.

## 목적별 선택

| 실행 이름 / 소스 | 적용할 상황 | 핵심 API | 검증하는 결과 |
|---|---|---|---|
| [`publish`](Publication.cs) | 점수·명령·로그를 처음 전달할 때 | scope, static translator, `PublishEvents` | 6건, 합계 210 |
| [`pipeline`](OrderPipeline.cs) | 독립 검사를 병렬 실행한 뒤 결과를 합칠 때 | `And(...).Then(...)` | 각 분기 3건, 승인 2건, 거절 1건, 합계 500 |
| [`multi-producer`](MultiProducer.cs) | 여러 연결·요청에서 한 처리기로 명령을 모을 때 | `ProducerType.Multi`, 취소 가능한 `Next` | 4,000건, 생산자별 순서 오류 0 |
| [`batch`](BatchAggregation.cs) | 텔레메트리·통계를 묶어서 집계할 때 | `IBatchEventHandler`, `MaxBatchSize` | 1,000건, 합계 500,500, span 길이 ≤ 16 |
| [`value`](ValueEvents.cs) | 작은 수치형 이벤트를 제자리 변환할 때 | `ValueDisruptor`, `ref T` | 이동 100건, 위치 합계 5,150 |
| [`backpressure`](Backpressure.cs) | 혼잡 시 버릴 데이터와 거절할 요청을 구별할 때 | `PublishGate`, 취소, 타임아웃 | 게시 5건, 드롭 1건, 거절 1건, 취소 관측 |
| [`poller`](FramePolling.cs) | 기존 게임 루프의 처리 예산을 유지할 때 | `NewPoller`, `TryNext`, `maxEvents` | 100건, 34프레임, 프레임당 최대 3건 |
| [`workers`](WorkerJobs.cs) | 전처리 후 독립 작업을 분배하고 결과를 모을 때 | `ThenWorkerPool(...).Then(...)` | 1,000개 결과, 누락·중복·미완료 결과 0 |
| [`async-batch`](AsyncSink.cs) | 느린 비동기 저장을 순서대로 기다릴 때 | `AsyncBatchEventProcessor`, `AsyncWaitStrategy` | 80건 순차 저장, await 중 슬롯 보존 |
| [`pooled-payload`](PooledPayload.cs) | 여러 소비자가 읽은 패킷 버퍼를 풀에 반환할 때 | `IClearable`, `AutoClearSlots`, `ArrayPool` | 양쪽 독자 100건, 버퍼 반환 100회 |
| [`errors`](ErrorHandling.cs) | 잘못된 입력을 기록하고 후속 처리를 계속할 때 | `ResilientExceptionHandler` | 정상 3건·합계 60, 거절 스냅샷 2건 |
| [`snapshots`](SnapshotStream.cs) | 관리자 화면·도구에 비동기 스트림을 노출할 때 | `ReadAllAsync`, 불변 값 스냅샷 | 100건, 슬롯 덮어쓰기 후에도 합계 49,500 |
| [`journal`](JournalReplay.cs) | 명령을 기록한 뒤 상태를 변경하고 재생할 때 | `JournalHandler`, `JournalReader.Verify/Replay` | 기록 40건, 라이브·재생 점수 모두 [190, 200, 210, 220] |

처음에는 `publish → pipeline → multi-producer → backpressure` 순서로 읽으면 된다.
그 뒤 게임 루프에는 `poller`, 독립 작업에는 `workers`, 비동기 저장에는 `async-batch`를 연결해 본다.

## 1. 안전한 게시: publish

한 핸들러가 점수를 합산한다. scope, 캡처하지 않는 static 람다, 구조체 translator, 일괄 게시를
같은 입력 경로에서 비교할 수 있다. 예제는 6건만 넣으므로 16칸 링에서 게시 자체가 포화되지 않는다.

`PublishEventScope()`는 scope가 끝날 때 게시한다. `using var`를 너무 바깥 범위에 선언하면
게시가 늦어지므로, 예제처럼 이벤트 작성 범위만 감싸는 `using (...)` 블록을 사용한다.
`PublishEvents`에 전달하는 입력 개수는 링 크기 이하여야 한다.

입력 파싱·검증은 슬롯을 점유하기 전에 끝낸다. translator나 scope 내부에서 예외가 나도 슬롯은
게시되며 자동 롤백되지 않는다. 일부만 채운 이벤트가 정상 입력으로 보이지 않도록 설계해야 한다.

## 2. 병렬 분기와 합류: pipeline

```text
                  ┌─ 수량 검증 ─┐
주문 게시 ────────┤             ├─ 승인/거절 집계
                  └─ 가격 계산 ─┘
```

검증과 가격 계산은 **같은 주문을 모두 받는다**. 후속 핸들러는 두 단계가 끝난 뒤에만 실행된다.
병렬 핸들러는 각각 `Valid`, `Total`이라는 다른 필드만 수정한다. 같은 필드나 공유 컬렉션을
동시에 수정하면 이 구조만으로 경쟁이 방지되지 않는다.

현재 예제의 검증 실패는 `Valid = false`라는 데이터로 전달한다. 예외로 건너뛰는 것과 업무상
거절은 다르다. 주문 승인·보안 검사·인코딩 전처리 등 단계별 책임을 드러낼 때 활용할 수 있다.

코드에서는 두 분기를 따로 등록하고 `And`로 그룹을 합친다. 함께 등록할 때는
`HandleEventsWith(validate, price).Then(accept)`로 같은 의존 관계를 표현할 수 있다.

## 3. 여러 생산자: multi-producer

연결 4개를 작업으로 모사하고, 각각 명령 1,000개를 작은 링에 게시한다. 소비자 하나가 연결별
다음 번호를 검증한다. 한 연결의 명령은 그 연결을 담당하는 생산자 하나가 순서대로 게시한다.

서로 다른 연결 사이의 실행 순서는 매번 달라질 수 있다. 여러 생산자가 동일 플레이어의 명령을
만드는 경우 업무 순서까지 자동으로 정해지지 않으므로, 명령 번호나 별도의 정렬 정책이 필요하다.
먼저 `Task.WhenAll`로 생산자를 끝내고 나서 `Shutdown`으로 소비자를 드레인한다.

## 4. 배치 집계: batch

`IBatchEventHandler<T>`는 연속된 슬롯 구간을 `ReadOnlySpan<T>`로 받는다. 호출마다 값을
합산하고 `endOfBatch`에서 집계 결과를 반영한다. 링 끝을 넘는 배치는 여러 span으로 나뉠 수 있다.

`MaxBatchSize = 16`은 **상한**이며, 16개가 쌓일 때까지 기다리라는 설정이 아니다. 입력과 소비
속도에 따라 1개만 받는 호출도 있다. span과 내부 객체 참조는 콜백 밖에 보관하지 않는다.
배치 핸들러의 예외 정책은 실패한 이벤트 하나가 아니라 해당 조각 전체에 적용된다.
Retry는 앞서 완료한 조각을 반복하지 않으며 Skip은 실패한 조각만 건너뛴다.
[복구·정리 규칙](../../docs/processing-semantics.ko.md#동기-예외-정책)을 함께 확인한다.

## 5. 구조체 이벤트: value

첫 핸들러가 `Position += Velocity`를 수행하고 다음 핸들러가 결과를 합산한다.
생산자는 `ref Movement movement = ref ring[sequence]`로 슬롯 자체를 초기화한다.
`var movement = ring[sequence]`로 받으면 복사본이므로 슬롯 수정이 되지 않는다.

수치만 담은 작은 구조체에 적용해 볼 수 있다. 큰 구조체나 참조 필드가 많은 구조체까지 일률적으로
유리한 것은 아니므로 실제 데이터로 측정한다. 이 예제는 처리량이나 할당량을 주장하지 않는다.

## 6. 혼잡 정책: backpressure

폴러를 잠시 호출하지 않아 4칸 링의 혼잡을 재현한다. 사용률 75%에서 임계값 50%를 넘겨 드롭하고,
마지막 칸까지 채운 뒤 제한 시간 거절과 대기 취소를 확인한다. 이후 폴링하면 다시 게시할 수 있다.

`DropWhenCongested`는 사용률을 먼저 검사한 뒤 `TryNext`로 슬롯을 확보한다. 그 사이 다른 생산자가
링을 채워도 소비자가 공간을 비울 때까지 기다리지 않고 드롭한다. 용량만 확인하려면 `TryNext`,
드롭 대신 거절로 집계하려면 `FailAfterTimeout`과 `TimeSpan.Zero`를 사용한다. 드롭은 최신값 병합 기능이 아니므로,
다음 데이터가 이전 상태를 대체할 수 있는 경우인지 별도로 판단한다.

## 7. 프레임 예산: poller

전용 소비 스레드를 만들지 않고 100개 입력을 프레임당 최대 3개씩 처리한다. 같은 스레드가
게시와 소비를 수행하므로 꽉 찬 링에서 `Next()`로 기다리면 자기 자신을 기다리게 된다.
예제는 `TryNext`가 실패하면 게시를 멈추고 폴링으로 넘어간다.

폴러는 등록 시점의 커서부터 시작하므로 게시 전에 만든다. 더 이상 사용하지 않으면 반드시
`Dispose`하여 게이팅에서 제거한다. 실제 게임에서는 폴링 이후에 시뮬레이션과 렌더링을 호출한다.

## 8. 작업 분배: workers

상위 핸들러가 계산할 값을 준비한 뒤 `ThenWorkerPool`로 작업을 분배한다.
워커 3개가 1,000개 정수의 제곱을 계산한다. 각 작업은 워커 하나가 받으며, 뒤의 일반 핸들러가
완성된 결과를 링 순서대로 수집한다. 분기 예제와 달리 모든 워커가 모든 이벤트를 받지 않는다.

워커 사이의 완료 순서·균등 분배는 보장하지 않는다. 느린 작업 하나 때문에 후속 순차 단계도
기다릴 수 있다. 예제의 중복 검증은 이번 정상 실행에 대한 검증이며, 장애·재시도·외부 시스템까지
포함하는 exactly-once 보장은 아니다. 같은 워커 객체를 여러 워커에 공유하지 않는다.

## 9. await 가능한 배치: async-batch

`AsyncBatchEventProcessor`는 콜백의 `ValueTask`가 끝날 때까지 처리 시퀀스를 전진시키지 않는다.
따라서 콜백 안에서는 await 이후에도 해당 구간의 슬롯을 읽을 수 있다. 일반 `IEventHandler`나
`IWorkHandler`를 `async void`로 만드는 방식과는 별도의 API다.

저수준 API를 사용하므로 `AddGatingSequences(processor.Sequence)`를 직접 호출한다.
종료할 때는 생산 완료 → 마지막 처리 시퀀스 확인 → `Halt` → 실행 Task 대기 → 게이팅 제거 순서다.
`Halt` 자체는 드레인이 아니다. 실제 저장소를 붙일 때에는 실패 정책·타임아웃·부분 저장 재시도의
멱등성을 추가해야 한다. 이 예제는 DB 연결 없이 지연과 메모리 목록으로 저장을 모사한다.

## 10. 풀 버퍼 소유권: pooled-payload

생산자가 준비한 `ArrayPool<byte>` 버퍼를 슬롯으로 넘기고, 독자 2개가 읽는다. 두 독자 뒤에
마지막 핸들러 하나를 두어 모두 읽은 다음에만 `IClearable.Clear()`가 호출되게 한다.
게시 슬롯 확보 실패 시에는 생산자가 버퍼를 반환한다. 성공 후에는 슬롯이 반환 책임을 가진다.

자동 정리는 단일 최종 소비자일 때 사용할 수 있다. 병렬 최종 소비자나 최종 워커 풀이 있는
구성에서는 자동 정리가 꺼진다. 처리 후에도 데이터를 보관해야 한다면 값이나 버퍼 내용을 복사하고,
반환된 배열을 비동기 송신 등에 계속 사용해서는 안 된다. 민감한 데이터라면 풀 반환 시
`clearArray: true` 사용 여부도 결정한다.

## 11. 실패 입력 격리: errors

음수 점수만 실패하게 하고, 예외 콜백에서 시퀀스·점수·사유를 별도 레코드로 복사한다.
`ResilientExceptionHandler`는 실패를 건너뛰고 연속 실패가 임계값에 도달하면 처리기를 정지한다.
이 예제에서는 정상 입력이 중간에 들어가므로 연속 실패 3회에 도달하지 않는다.

업무 상태 변경 전에 검증해야 한다. Skip은 롤백이나 재시도가 아니며 후속 의존 핸들러도 진행할 수 있다.
실제 오류 보관소는 지속성·용량 제한을 별도로 갖춰야 한다. 연속 실패 횟수를 가진 예외 핸들러 인스턴스는
여러 처리기가 공유하지 않는다. 정지 정책을 채택한다면 `ProcessorHalted`에서 신규 유입을 중단하고
별도 제어 흐름에서 종료를 진행해야 한다.

## 12. 관리자 스트림: snapshots

`ReadAllAsync`로 점수를 불변 값 스냅샷으로 복사해 `await foreach`에 전달한다. 구독 종료 후
링의 모든 슬롯을 덮어써도 이미 저장한 스냅샷이 변하지 않는지 확인한다.

비동기 열거는 지연 실행된다. enumerable을 만들기만 해서는 구독되지 않으므로 첫 열거가 시작된
다음 게시한다. 생산자를 시작하기 전에 `ConsumeAsync()`를 직접 호출하는 이유다.
배열·변경 가능한 객체가 들어 있는 이벤트는 필요한 깊이까지 복사해야 한다. 이 브리지는 복사와
폴링 비용이 있고 느린 구독자가 생산자를 지연시킬 수 있으므로, 관리자 도구·검증 스트림에 적합하다.

## 13. 저널 기록과 재생: journal

`JournalHandler`가 명령을 기록한 뒤에만 후속 상태 핸들러가 점수를 변경한다. 플레이어 ID와
점수 변화량을 8바이트로 직렬화한다. 드레인 후 저널을 검증하고 스트림을 처음으로 돌려 새 상태에
재생한다. 기록 수, 시퀀스 순서, 네 플레이어의 점수를 라이브 상태 및 기대값과 비교한다.

`MemoryStream`으로 순서와 재생을 배우는 예제이며 크래시 내구성을 제공하지는 않는다.
파일 저장 시에는 `flushToDiskOnBatchEnd` 적용 시점을 결정하고, 시간·난수를 포함한 상태 변경
입력을 재현 가능하게 만들어야 한다. 기록 실패를 건너뛰고 상태만 변경하지 않도록 정지 정책과
게시 대기 제한을 사용한다. 실서비스에서는 `ProcessorHalted`를 통한 종료 조정도 필요하다.
파일·샤딩은 MatchSimulation, 손상 처리와 운영 명령은 ReplayInspector로 이어서 학습한다.

## 더 큰 시스템으로 확장하기

| 다음 목표 | 기존 실행 샘플 |
|---|---|
| 실제 시간 흐름과 프레임 시간 분포 확인 | [GameLoopPoller](../GameLoopPoller/Program.cs) |
| 채팅 브로드캐스트의 flush 묶기 | [ChatBroadcast](../ChatBroadcast/Program.cs) |
| 방 샤딩·고정 틱·저널·재생 결과 비교 | [MatchSimulation](../MatchSimulation/Program.cs) |
| 저널 검증·덤프·특정 시퀀스까지 재생·실행 비교 | [ReplayInspector](../ReplayInspector/Program.cs) |
| ASP.NET Core DI·수명주기·상태 확인·메트릭 | [AspNetCoreTelemetry](../AspNetCoreTelemetry/Program.cs) |

공통 종료 원칙은 **생산자 중단 및 완료 → 소비 드레인 → 자원 해제**다. `Shutdown(timeout)`은
드레인 시간 초과 시 경고를 출력하고 정지를 시도하며, 전량 처리 성공을 예외로 보장하지 않는다.
그래서 이 예제들은 종료 후에도 처리 건수와 결과를 확인한다. 실서비스에서도 최종 시퀀스와
미처리 건수, 정지된 처리기를 관측해야 한다.
