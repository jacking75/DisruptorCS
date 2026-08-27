# 작업 로그

## 2026-08-27 15:57 KST — NuGet 배포 준비 (0.10.0)

`DisruptorCS` / `DisruptorCS.Extensions.Hosting` 두 패키지의 nuget.org 등록 준비를 마쳤다.
`Directory.Build.props` 로 공통 메타데이터(작성자·저작권·아이콘·라이선스·심볼·SourceLink)와
MinVer 버전 하한 `0.10` 을 한곳에 모아 두 패키지의 버전이 갈라질 수 없게 했고, 기본
`IsPackable=false` 로 샘플·테스트가 실수로 패키징되지 않게 했다. nuget.org 는 상대 링크를
복원하지 못하므로 27KB README 대신 절대 링크만 쓰는 패키지 전용 `PACKAGE.md` 를 프로젝트마다
따로 실었고, 링 버퍼 모티프 아이콘을 생성기(`build/generate-icon.cs`)와 함께 추가했다.
호스팅 어셈블리 이름을 패키지 ID 와 맞춰 `DisruptorCS.Extensions.Hosting` 으로 통일했고,
`AddDisruptor<T>()`·`AddDisruptorCheck<T>()` 를 MS 관례대로 `Microsoft.Extensions.DependencyInjection`
네임스페이스로 옮겼다 — 배포 후에는 breaking change 라 지금이 아니면 못 바꾼다. 이제 `Program.cs`
에서 별도 `using` 없이 `builder.Services` 에 바로 나타난다(샘플의 using 도 제거).
`build/pack.ps1` 이 빌드·팩 후 nupkg 를 열어 analyzer·README·아이콘·양쪽 TFM·XML 문서·snupkg
존재를 검증하며, analyzer DLL 경로가 어긋나면 pack 자체가 실패하도록 `Error` 조건을 넣었다.
검증: 로컬 피드로 실제 소비자 프로젝트에 설치해 DISR004 발생·`using` 없는 DI 등록·이벤트 소비까지 확인,
Release/Debug 빌드 경고 0, 테스트 113+20 전부 통과. CHANGELOG 를 `[0.10.0] - 2026-08-27` 로
확정하고 docs/RELEASE.md 를 9단계(소비자 게이트 추가)로 갱신했다.
배포까지 완료(16:13 KST): `v0.10.0` 태그 푸시 → 태그 기준 pack → nuget.org 에 두 패키지와
심볼 업로드, GitHub Release 생성. 인덱싱은 코어 220초·호스팅 300초 걸렸다. 배포 후 검증으로
로컬 피드를 끊고 nuget.org 에서만 복원해 코어 실행·DISR004 발생·호스팅의 `DisruptorCS 0.10.0`
전이 의존성 해석·`using` 없는 DI 등록까지 확인했다.
주의: 갓 올린 패키지는 flat container(200)보다 검색 인덱스가 늦게 따라와서, 그동안
`dotnet add package DisruptorCS` (버전 생략)는 "사용할 수 있는 버전이 없습니다"로 실패한다.
`--version 0.10.0` 을 명시하면 즉시 복원된다.

## 2026-08-27 15:05 KST — IMPROVEMENTS.md 미완 항목 마무리 (샘플 4종·DISR005·Coyote·ValueWorkerPool·POH)

"다한 것인가?" 확인 요청에 따라 감사한 결과, 앞선 로그의 "Wave 1~4 전부 구현"은 과장이었다.
실제로 §4.6 샘플은 5종 중 1종만, §3.7 DISR005는 ID만 선언, §3.8 Coyote 모델·§4.9 `ValueWorkerPool`·
§2.8 POH 배열은 미구현이었다. 이번에 전부 마무리했다: `BatchSpanEscapeAnalyzer`(DISR005, 지역 변수
경유 유출까지 추적), Coyote 모델 8종(`Disruptor.Concurrency.Tests`, **음성 대조군 포함**),
샘플 4종(ChatBroadcast·MatchSimulation·ReplayInspector·AspNetCoreTelemetry), `ValueWorkerPool<T>`,
`UsePinnedObjectHeap`, `EventScope.Event` 명명 통일, `PublishedCursor` 비용 문서화.
작업 중 라이브러리 결함 2건을 발견해 고쳤다 — `JournalWriter.Dispose` 비멱등(정상 종료가 예외로 끝남),
`AddDisruptor<T>()`가 핸들러를 별도 등록해야만 시작되던 문제(문서 예제와 불일치) 및 워커 풀이
N개 워커에 같은 인스턴스를 공유하던 문제.
실측: 브로드캐스트 flush 4,000만→**10,400회**(약 3,800배 감소), 샤딩 저널 재생 체크섬 완전 일치(불일치 룸 0),
Coyote 1,000 스케줄/모델 버그 0(대조군은 39회 만에 교착 발견). 검증은 Release/Debug 빌드 경고 0,
테스트 113+20 전부 통과, Soak 90초 2,303라운드 4,606만 이벤트 위반 0. IMPROVEMENTS.md에 §7.7을 추가해
과장이었던 부분을 명시하고 실제 완료 내역과 수치를 기록했다.

## 2026-08-27 13:52 KST — IMPROVEMENTS.md Wave 1~4 전면 구현

설계서의 성능·안정성·채택 항목을 전부 구현했다. 코어에는 소비 루프 배치 재구성(+87%),
`SequencerDispatcher`(sealed 구체 타입 분기로 인터페이스 디스패치 제거), Blocking 게시 경로의
lock 제거(39.1ns→4.1ns), 백프레셔 QPC 절감, `StripedCounter`, `MaxBatchSize`, 배치 핸들러 API를 적용했다.
안정성은 저널 v2 무결성(XxHash3+길이상한+단조검증+복구모드), `HighResolutionTimerScope`(유휴 틱 15.40→1.52ms),
`ResilientExceptionHandler.Default` 공유 스트릭 결함 수정, 스톨 rate-limit, 수명주기 가드,
정지 샤드 fail-fast를 넣었다. 신규 프로젝트로 Roslyn Analyzer(DISR001~004, 패키지 동봉),
Extensions.Hosting(DI/HostedService/HealthCheck), Soak 하니스, Analyzer 테스트, 샘플을 추가했다.
구현 중 `DrainProcessors`/`WorkerPool.DrainAndHalt`가 `Thread.Sleep(1)`을 쓰고 있던 것을 발견해 고쳤다
— 라이브러리의 다른 모든 대기 경로에서 금지한 호출이다.
실측: 처리량 69M→**250M ops/s**(4.0ns/op, 할당 0), Soak 2분 2,480라운드 4,960만 이벤트 위반 0,
테스트 60→123개 전부 통과. disruptor-net 비교는 **큰 링에서 1.5배 우세, 작은 링(백프레셔 포화)에서
2.9배 열세**로 나왔고, 배제한 가설 6가지와 함께 `docs/benchmarks/`에 미해결 항목으로 공개했다.
문서는 README를 영어 우선(한국어 병행)으로 전환하고 CHANGELOG·CONTRIBUTING·RELEASE·analyzer 규칙 문서를 작성했다.

## 2026-08-27 11:51 KST — 고성능·안정성 정밀 재조사 및 채택 전략 설계서(IMPROVEMENTS.md) 작성

커밋 5d19229 전체 소스를 정밀 재리뷰하고 가설 검증 프로브 3종을 실측해 `IMPROVEMENTS.md`(약 1,850줄)를 작성했다.
3대 실측 발견: (1) 소비 루프의 per-event try/catch·부대 검사 제거로 소비자 처리량 **+87%**(339M→649M ops/s),
이때 struct 제네릭 디버추얼라이제이션은 .NET 10 Dynamic PGO 덕에 **효과 없음**을 확인,
(2) BlockingWaitStrategy의 게시당 lock+PulseAll이 39.1ns로 fence 방식(4.1ns)의 10배이며
per-event 게시 처리량을 1/6~1/7로 깎음(8.6M vs 50~60M ops/s), (3) 유휴 OnTimeout(1ms)이
Windows 타이머 양자화로 실측 15.40ms 간격 → timeBeginPeriod(1) 적용 시 1.52ms.
그 외 ResilientExceptionHandler.Default 공유 스트릭 결함, 저널 무결성 공백(해시/길이상한/단조 검증)
등을 문서화하고 각 항목의 구현 코드를 상세 기술했다. 채택 전략으로 영어 우선 문서화, EventPoller(스레드 없는
폴링 소비), Microsoft.Extensions 통합, Roslyn Analyzer, disruptor-net 정직 비교·포지셔닝("게임 서버 배포판")을
설계하고 Wave 1~4 로드맵(약 195h)으로 정리했다. README에 문서 링크를 추가했다.

## 2026-08-27 11:15 KST — EVALUATION.md 개선 항목 P0~P3 전면 구현

설계서의 결함 12건과 개선 로드맵 P0(정확성)~P3(게임 서버 도메인 레이어)를 모두 구현했다.
핵심은 `MultiProducerSequencer.Next()`의 `do…while` + `continue` 이식 버그 수정으로,
링 1024 / 8 프로듀서 / 800K건 기준 유실 794,332건(99.3%) → **0건**이 되었다.
`DependentSequenceGroup`·`SequenceWaitResult`·`CancellationToken` 도입으로 대기/종료 경로를 재설계했고,
예외 정책(Skip/Retry/Halt)·`OnTimeout`/`OnBatchStart` 훅·백프레셔 정책 API·스톨 감지·`ValueRingBuffer<T>`·
`WorkerPool`·`ShardedDisruptor`·`FixedTickDriver`·결정론적 저널/리플레이를 새로 추가했다.
구현 중 설계서에 없던 결함 2건(워커 풀 전용 파이프라인의 종료 드레인 누락으로 908건 유실,
`HasRegisteredWork` 판정 누락)을 테스트가 추가로 잡아내 함께 수정했다.
단일 솔루션(`DisruptorCS.slnx`)으로 재구성하고 xUnit 테스트 60개·BenchmarkDotNet 벤치마크·
GitHub Actions CI(Release/Debug × Ubuntu/Windows, 스트레스 20회 반복)를 갖췄다.
실측: 단일 프로듀서 66.4M ops/s·15.1ns/op·**할당 0 bytes**, 백프레셔 시 `>15ms` 284건 → 0건(처리량 +50%),
Blocking 전략 CPU 0.98코어 → 0.07코어, 룸 5,000개를 스레드 8개로 처리, 저널 리플레이 체크섬 완전 일치.

## 2026-08-27 10:21 KST — DisruptorCS 게임 서버 적용성 평가 및 개선 설계서 작성

전체 소스(15개 파일)를 리뷰하고, 원본을 수정하지 않은 별도 프로브 프로젝트 6종으로 실측 검증을 수행했다.
`MultiProducerSequencer.Next()`의 `do…while` + `continue` 이식 버그로 백프레셔가 완전히 무력화되어,
링 1024 / 8 프로듀서 / 800K건 테스트에서 **이벤트 99.3% 유실 + 컨슈머 영구 정지**가 재현율 100%로 발생함을 확인했다.
수정본을 임시 복사본에 적용해 A/B 검증한 결과 유실 0건 / 게시 시간 249ms→99ms로 정상화됨을 확인했다.
그 외 `SpinWait.SpinOnce()`의 15ms 스톨(수정 후 >15ms 284건→0건, 처리량 +47%),
BlockingWaitStrategy 의존성 체인의 1코어 상시 점유, 기본 예외 핸들러의 프로세스 종료 등 결함 12건을 정리하고
게임 서버 도메인 레이어(샤딩·틱 통합·결정론적 저널) 설계까지 포함해 `EVALUATION.md`로 문서화했다.
