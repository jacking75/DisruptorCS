using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using Disruptor;
using Disruptor.Dsl;
using Disruptor.Game;
using Disruptor.WaitStrategy;
using Disruptor.Work;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== DisruptorCS — 게임 서버 예제 ===\n");

// 유휴 구간에도 틱 정밀도를 유지한다 (Windows 기본 타이머는 15.6ms 로 양자화됨).
using var timerScope = new HighResolutionTimerScope();
Console.WriteLine($"고분해능 타이머: {(timerScope.IsActive ? "활성 (유휴 OnTimeout 약 1.5ms)" : "비활성 (Windows 아님)")}\n");

BasicExample();
Console.WriteLine();

DependencyGraphExample();
Console.WriteLine();

BatchHandlerExample();
Console.WriteLine();

EventPollerExample();
Console.WriteLine();

BackpressurePolicyExample();
Console.WriteLine();

TickLoopExample();
Console.WriteLine();

JournalReplayExample();
Console.WriteLine();

JournalIntegrityExample();
Console.WriteLine();

ShardedRoomExample();
Console.WriteLine();

WorkerPoolExample();
Console.WriteLine();

ObservabilityExample();
Console.WriteLine();

PerformanceBenchmark();
Console.WriteLine();

MultiProducerIntegrityCheck();

// =====================================================
// 예제 1: 기본 사용법
// =====================================================
void BasicExample()
{
    Console.WriteLine("--- 예제 1: 기본 사용법 ---");

    using var disruptor = new Disruptor<OrderEvent>(
        eventFactory: () => new OrderEvent(),
        ringBufferSize: 1024,
        producerType: ProducerType.Single,
        waitStrategy: new BlockingWaitStrategy(),
        options: new DisruptorOptions { Name = "basic" });

    disruptor.HandleEventsWith(new PrintHandler());

    var ringBuffer = disruptor.Start();

    // 방법 1: Next/Publish 패턴
    for (int i = 0; i < 3; i++)
    {
        long sequence = ringBuffer.Next();
        try
        {
            var evt = ringBuffer[sequence];
            evt.OrderId = i;
            evt.Symbol = "AAPL";
            evt.Price = 150.0 + i;
            evt.Quantity = 100;
        }
        finally
        {
            ringBuffer.Publish(sequence);
        }
    }

    // 방법 2: static 람다 + 인자 (캡처하지 않으므로 할당 0)
    ringBuffer.PublishEvent(static (evt, seq, id) =>
    {
        evt.OrderId = id;
        evt.Symbol = "GOOG";
        evt.Price = 2800.0;
        evt.Quantity = 50;
    }, 999L);

    // 방법 3: 구조체 translator (델리게이트조차 만들지 않음)
    ringBuffer.PublishEvent(default(FillMsftOrder), 1000L);

    // 방법 4: using 스코프 — try/finally 를 잊어 링 버퍼를 막는 사고를 구조적으로 방지
    using (var scope = ringBuffer.PublishEventScope())
    {
        var evt = scope.Event();
        evt.OrderId = 1001;
        evt.Symbol = "NVDA";
        evt.Price = 900.0;
        evt.Quantity = 10;
    }

    // Shutdown 이 미처리 이벤트를 드레인하므로 Thread.Sleep 이 필요 없다.
    disruptor.Shutdown();
    Console.WriteLine("  완료.");
}

// =====================================================
// 예제 2: 의존성 그래프 (저널 + 복제 → 비즈니스 로직)
// =====================================================
void DependencyGraphExample()
{
    Console.WriteLine("--- 예제 2: 의존성 그래프 ---");
    Console.WriteLine("  JournalHandler ──┐");
    Console.WriteLine("                   ├──> BusinessLogicHandler");
    Console.WriteLine("  ReplicationHandler ┘\n");

    using var disruptor = new Disruptor<OrderEvent>(
        () => new OrderEvent(),
        ringBufferSize: 1024,
        producerType: ProducerType.Single,
        waitStrategy: new YieldingWaitStrategy(),
        options: new DisruptorOptions { Name = "pipeline" });

    var journalHandler = new JournalCountHandler();
    var replicationHandler = new ReplicationHandler();
    var businessHandler = new BusinessLogicHandler();

    // 따로 등록한 두 그룹을 And() 로 합쳐 "양쪽 모두" 기다리게 할 수도 있다.
    var journal = disruptor.HandleEventsWith(journalHandler);
    var replication = disruptor.HandleEventsWith(replicationHandler);
    journal.And(replication).Then(businessHandler);

    var ringBuffer = disruptor.Start();

    const int eventCount = 100_000;
    for (int i = 0; i < eventCount; i++)
    {
        long seq = ringBuffer.Next();
        try
        {
            var evt = ringBuffer[seq];
            evt.OrderId = i;
            evt.Symbol = "TEST";
            evt.Price = i * 1.1;
            evt.Quantity = i;
        }
        finally
        {
            ringBuffer.Publish(seq);
        }
    }

    disruptor.Shutdown(TimeSpan.FromSeconds(10));

    Console.WriteLine($"  Journal      : {journalHandler.Count:N0}건");
    Console.WriteLine($"  Replication  : {replicationHandler.Count:N0}건");
    Console.WriteLine($"  BusinessLogic: {businessHandler.Count:N0}건 (최대 배치 {businessHandler.MaxBatch:N0})");
    Console.WriteLine(businessHandler.Count == eventCount
        ? "  → 유실 없음. And() 로 두 그룹을 합쳐 양쪽을 모두 기다렸다"
        : "  → 유실 발생!");
}

// =====================================================
// 예제 3: 배치 핸들러 — 이벤트 구간을 span 으로 통째로 받는다
// =====================================================
void BatchHandlerExample()
{
    Console.WriteLine("--- 예제 3: 배치 핸들러 (span 수신) ---");

    const int count = 200_000;
    var handler = new SpanSumHandler();

    using var disruptor = new Disruptor<OrderEvent>(
        () => new OrderEvent(), 4096, ProducerType.Single, new YieldingWaitStrategy(),
        new DisruptorOptions { Name = "batch", AutoClearSlots = false });

    disruptor.HandleEventsWith(handler);
    var ring = disruptor.Start();

    for (int i = 0; i < count; i++)
    {
        long seq = ring.Next();
        ring[seq].OrderId = i;
        ring.Publish(seq);
    }

    disruptor.Shutdown(TimeSpan.FromSeconds(10));

    Console.WriteLine($"  이벤트 {handler.Events:N0}건을 OnBatch 호출 {handler.BatchCalls:N0}회로 처리");
    Console.WriteLine($"  최대 조각 길이: {handler.MaxSegment:N0}, 합계 검증: {(handler.Sum == SumTo(count - 1) ? "일치" : "불일치")}");
    Console.WriteLine("  → 호출 경계가 이벤트당 1회에서 조각당 1회로 줄어든다 (SIMD·일괄 직렬화 가능)");

    static long SumTo(long n) => n * (n + 1) / 2;
}

// =====================================================
// 예제 4: EventPoller — 전용 스레드 없이 기존 루프에서 소비
// =====================================================
void EventPollerExample()
{
    Console.WriteLine("--- 예제 4: EventPoller (스레드 0개 추가) ---");

    const int count = 100_000;
    const int frameBudget = 256;

    var ring = RingBuffer<OrderEvent>.CreateSingleProducer(
        () => new OrderEvent(), 4096, new YieldingWaitStrategy());

    using var poller = ring.NewPoller();

    int published = 0;
    int consumed = 0;
    int frames = 0;
    long checksum = 0;

    var sw = Stopwatch.StartNew();

    // 기존 게임 루프 그대로 — 소비 전용 스레드를 만들지 않는다.
    //
    // 주의: 폴러는 이 스레드가 Poll() 을 호출할 때만 소비한다. 따라서 한 프레임에
    // 소비량보다 많이 게시하면 링이 차고 Next() 가 영구 대기한다 (자기 자신을 기다리는 셈).
    // 프레임당 게시량 <= 프레임당 소비 예산으로 잡거나, TryNext 로 게시를 제한해야 한다.
    while (consumed < count)
    {
        for (int i = 0; i < frameBudget && published < count; i++)
        {
            if (!ring.TryNext(out long seq))
                break;   // 이번 프레임 예산 소진 — 다음 프레임에 이어서

            ring[seq].OrderId = published++;
            ring.Publish(seq);
        }

        poller.Poll((evt, seq, endOfBatch) =>
        {
            checksum += evt.OrderId;
            consumed++;
            return true;
        }, maxEvents: frameBudget);

        frames++;
    }

    sw.Stop();

    Console.WriteLine($"  이벤트 {consumed:N0}건 / 프레임 {frames:N0}회 / {sw.Elapsed.TotalMilliseconds:F0}ms");
    Console.WriteLine($"  프레임당 최대 {frameBudget}건 — 밀린 이벤트가 한 프레임을 독점하지 않는다");
    Console.WriteLine($"  체크섬: {(checksum == (long)(count - 1) * count / 2 ? "일치" : "불일치")}");
    Console.WriteLine("  → Unity Update() / 자체 프레임 루프에 스레드 모델 변경 없이 통합된다");
}

// =====================================================
// 예제 5: 백프레셔 정책 — 게임 서버는 "버리는 것"도 정책이다
// =====================================================
void BackpressurePolicyExample()
{
    Console.WriteLine("--- 예제 5: 백프레셔 정책 ---");

    using var disruptor = new Disruptor<OrderEvent>(
        () => new OrderEvent(), 256, ProducerType.Multi, new YieldingWaitStrategy(),
        new DisruptorOptions { Name = "backpressure" });

    disruptor.HandleEventsWith(new SlowBusinessHandler(sleepMs: 1));
    var ring = disruptor.Start();

    var gate = new PublishGate<OrderEvent>(ring) { CongestionThreshold = 0.8 };

    // 이동 패킷처럼 "최신값만 의미 있는" 이벤트 — 혼잡하면 버린다
    for (int i = 0; i < 2_000; i++)
        gate.TryPublish(BackpressurePolicy.DropWhenCongested,
                        static (e, s) => e.OrderId = s, opCode: 10);

    // 결제처럼 "절대 버릴 수 없는" 이벤트 — 타임아웃 후 거절해 호출자가 오류 응답
    int rejected = 0;
    for (int i = 0; i < 200; i++)
    {
        if (!gate.TryPublish(BackpressurePolicy.FailAfterTimeout,
                             static (e, s) => e.OrderId = s,
                             opCode: 20, timeout: TimeSpan.FromMilliseconds(5)))
            rejected++;
    }

    Console.WriteLine($"  게시 {gate.PublishedCount:N0} / 드롭 {gate.DroppedCount:N0} / 거절 {gate.RejectedCount:N0}");
    Console.WriteLine($"  → 이동 패킷은 버리고, 결제는 거절해 호출자가 오류 응답을 보낼 수 있다 ({rejected}건)");

    disruptor.Shutdown(TimeSpan.FromSeconds(10));
}

// =====================================================
// 예제 6: 틱 기반 게임 루프 — 이벤트 소비와 시뮬레이션을 한 스레드에서
// =====================================================
void TickLoopExample()
{
    Console.WriteLine("--- 예제 6: 60Hz 틱 루프 통합 ---");

    var handler = new TickedRoomHandler();

    using var disruptor = new Disruptor<OrderEvent>(
        () => new OrderEvent(), 1024, ProducerType.Multi,
        // 이벤트가 없어도 1ms마다 OnTimeout 이 호출되어 틱이 밀리지 않는다
        new TimeoutBlockingWaitStrategy(TimeSpan.FromMilliseconds(1)),
        new DisruptorOptions { Name = "tick-loop" });

    disruptor.HandleEventsWith(handler);
    var ring = disruptor.Start();

    var sw = Stopwatch.StartNew();
    int published = 0;

    // 500ms 동안 불규칙하게 커맨드를 보낸다 (유휴 구간 포함)
    while (sw.Elapsed < TimeSpan.FromMilliseconds(500))
    {
        for (int i = 0; i < 20; i++)
        {
            long seq = ring.Next();
            ring[seq].OrderId = published++;
            ring[seq].RoomId = published % 4;
            ring.Publish(seq);
        }
        Thread.Sleep(20); // 유휴 구간 — 여기서도 틱은 계속 진행되어야 한다
    }

    disruptor.Shutdown(TimeSpan.FromSeconds(5));

    Console.WriteLine($"  커맨드 {published:N0}건, 틱 {handler.TickCount:N0}회 " +
                      $"({sw.Elapsed.TotalMilliseconds:F0}ms 동안, 60Hz면 약 {sw.Elapsed.TotalMilliseconds / 16.6:F0}회 기대)");
    Console.WriteLine($"  건너뛴 틱: {handler.SkippedTicks} (death spiral 방지)");
    Console.WriteLine("  → 유휴 구간에도 OnTimeout 으로 틱이 진행된다");
}

// =====================================================
// 예제 7: 결정론적 저널 + 리플레이
// =====================================================
void JournalReplayExample()
{
    Console.WriteLine("--- 예제 7: 결정론적 저널 / 리플레이 ---");

    const int eventCount = 20_000;
    string path = Path.Combine(Path.GetTempPath(), $"disruptorcs-demo-{Environment.ProcessId}.journal");

    var serializer = new OrderEventCodec();
    var liveWorld = new WorldState();

    try
    {
        using (var writer = JournalWriter.CreateFile(path))
        using (var disruptor = new Disruptor<OrderEvent>(
                   () => new OrderEvent(), 4096, ProducerType.Single, new YieldingWaitStrategy(),
                   new DisruptorOptions { Name = "journal" }))
        {
            // 저널이 먼저 기록된 뒤에만 로직이 커밋된다
            disruptor.HandleEventsWith(new JournalHandler<OrderEvent>(writer, serializer))
                     .Then(new WorldUpdateHandler(liveWorld));

            var ring = disruptor.Start();

            for (int i = 0; i < eventCount; i++)
            {
                long seq = ring.Next();
                var evt = ring[seq];
                evt.OrderId = i;
                evt.RoomId = i % 8;
                evt.Quantity = (i * 37) % 101;
                ring.Publish(seq);
            }

            disruptor.Shutdown(TimeSpan.FromSeconds(10));
        }

        // 리플레이: 같은 순서로 재생하면 상태가 정확히 재현되어야 한다
        var replayedWorld = new WorldState();
        using var fs = File.OpenRead(path);
        long replayed = JournalReader.Replay(
            fs, serializer, () => new OrderEvent(), new WorldUpdateHandler(replayedWorld));

        Console.WriteLine($"  기록 {eventCount:N0}건 → 재생 {replayed:N0}건");
        Console.WriteLine($"  원본 체크섬 : {liveWorld.Checksum}");
        Console.WriteLine($"  재생 체크섬 : {replayedWorld.Checksum}");
        Console.WriteLine(liveWorld.Checksum == replayedWorld.Checksum
            ? "  → 상태 완전 일치 (버그 재현 / 핫 스탠바이 복제 / 치팅 검증에 사용 가능)"
            : "  → 불일치! 결정론이 깨졌습니다");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

// =====================================================
// 예제 8: 저널 무결성 — 손상된 파일은 조용히 재생되면 안 된다
// =====================================================
void JournalIntegrityExample()
{
    Console.WriteLine("--- 예제 8: 저널 무결성 검증 ---");

    var codec = new OrderEventCodec();
    using var stream = new MemoryStream();

    using (var writer = new JournalWriter(stream, ownsStream: false))
    {
        var evt = new OrderEvent();
        Span<byte> buf = stackalloc byte[16];

        for (int i = 0; i < 1_000; i++)
        {
            evt.OrderId = i;
            evt.RoomId = i % 8;
            evt.Quantity = i;
            int n = codec.Serialize(evt, buf);
            writer.Write(i, buf[..n]);
        }

        writer.Flush();
    }

    byte[] healthy = stream.ToArray();

    using (var ok = new MemoryStream(healthy))
        Console.WriteLine($"  정상 파일  : {JournalReader.Verify(ok)}");

    // 페이로드 1바이트를 뒤집는다 — 길이는 그대로라 체크섬이 없으면 감지되지 않는다
    byte[] corrupted = (byte[])healthy.Clone();
    int payloadOffset = 16 + 20 + 4;
    corrupted[payloadOffset] ^= 0xFF;

    using (var bad = new MemoryStream(corrupted))
        Console.WriteLine($"  손상 파일  : {JournalReader.Verify(bad)}");

    // 크래시로 잘린 파일은 마지막 유효 지점까지 복구되어야 한다
    using (var truncated = new MemoryStream(healthy[..^10]))
    {
        int recovered = JournalReader.Read(truncated,
            new JournalReadOptions { Mode = JournalReadMode.TruncateAtCorruption }).Count();
        Console.WriteLine($"  잘린 파일  : {recovered:N0}건 복구 (마지막 유효 레코드까지)");
    }

    Console.WriteLine("  → 손상이 조용히 잘못된 상태로 재생되는 일이 없다");
}

// =====================================================
// 예제 9: 샤딩 — 수천 룸을 코어 수만큼의 스레드로
// =====================================================
void ShardedRoomExample()
{
    Console.WriteLine("--- 예제 9: ShardedDisruptor (룸 샤딩) ---");

    const int rooms = 5_000;
    const int eventsPerRoom = 40;
    const int shardCount = 8;

    var handlers = new RoomShardHandler[shardCount];

    using var sharded = new ShardedDisruptor<OrderEvent>(
        shardCount: shardCount,
        ringSizePerShard: 4096,
        eventFactory: () => new OrderEvent(),
        handlerFactory: i => handlers[i] = new RoomShardHandler(),
        producerType: ProducerType.Multi,
        waitStrategyFactory: _ => new TimeoutBlockingWaitStrategy(TimeSpan.FromMilliseconds(1)),
        options: new DisruptorOptions { Name = "rooms" });

    sharded.Start();

    var sw = Stopwatch.StartNew();
    Parallel.For(0, rooms, room =>
    {
        for (int i = 0; i < eventsPerRoom; i++)
        {
            int order = i;
            sharded.Publish(room, (evt, seq) =>
            {
                evt.RoomId = room;
                evt.OrderId = order;
            });
        }
    });

    ShardHealth[] health = sharded.HealthSnapshot();
    sharded.Shutdown(TimeSpan.FromSeconds(20));
    sw.Stop();

    long total = handlers.Sum(h => h.Count);
    long distinctRooms = handlers.Sum(h => h.RoomCount);

    Console.WriteLine($"  룸 {rooms:N0}개 × {eventsPerRoom}건 = {(long)rooms * eventsPerRoom:N0}건");
    Console.WriteLine($"  처리 {total:N0}건, 관측된 룸 {distinctRooms:N0}개, {sw.Elapsed.TotalMilliseconds:F0}ms");
    Console.WriteLine($"  샤드별 처리량: [{string.Join(", ", handlers.Select(h => h.Count.ToString("N0")))}]");
    Console.WriteLine($"  샤드 사용률  : [{string.Join(", ", health.Select(h => h.Utilization.ToString("P0")))}]");
    Console.WriteLine($"  스레드 {shardCount}개로 룸 {rooms:N0}개 처리 " +
                      $"(룸당 Disruptor 였다면 스레드 {rooms:N0}개 + 링 메모리 수백 MB)");
    Console.WriteLine(handlers.All(h => h.OrderViolations == 0)
        ? "  → 룸별 순서 보장 확인"
        : "  → 순서 위반 발생!");
}

// =====================================================
// 예제 10: WorkerPool — 순서 무관 + 느린 작업 분배
// =====================================================
void WorkerPoolExample()
{
    Console.WriteLine("--- 예제 10: WorkerPool (DB 쓰기 분배) ---");

    const int count = 20_000;
    var workers = new DbWriteWorker[4];

    using var disruptor = new Disruptor<OrderEvent>(
        () => new OrderEvent(), 1024, ProducerType.Single, new YieldingWaitStrategy(),
        new DisruptorOptions { Name = "worker-pool", AutoClearSlots = false });

    var logic = new BusinessLogicHandler();

    disruptor.HandleEventsWith(logic)
             .ThenWorkerPool(4, i => workers[i] = new DbWriteWorker(i));

    var ring = disruptor.Start();

    for (int i = 0; i < count; i++)
    {
        long seq = ring.Next();
        ring[seq].OrderId = i;
        ring.Publish(seq);
    }

    disruptor.Shutdown(TimeSpan.FromSeconds(20));

    Console.WriteLine($"  로직 처리 {logic.Count:N0}건");
    Console.WriteLine($"  워커별 분배: [{string.Join(", ", workers.Select(w => w.Handled.ToString("N0")))}] " +
                      $"= {workers.Sum(w => w.Handled):N0}건");
    Console.WriteLine("  → 순서가 필요 없는 느린 I/O 는 워커 풀로 분리한다");
}

// =====================================================
// 예제 11: 관측성 — 스톨 감지
// =====================================================
void ObservabilityExample()
{
    Console.WriteLine("--- 예제 11: 스톨 감지 ---");

    var stalls = new List<StallInfo>();

    using var disruptor = new Disruptor<OrderEvent>(
        () => new OrderEvent(), 64, ProducerType.Single, new YieldingWaitStrategy(),
        new DisruptorOptions
        {
            Name = "packet-pipeline",
            StallThreshold = TimeSpan.FromMilliseconds(50),
            StallReportInterval = TimeSpan.FromMilliseconds(200),
            OnStall = info => { lock (stalls) stalls.Add(info); },
            EnableMetrics = true,
        });

    // 일부러 아주 느린 핸들러를 둔다
    disruptor.HandleEventsWith(new SlowBusinessHandler(sleepMs: 5));
    var ring = disruptor.Start();

    for (int i = 0; i < 200; i++)
    {
        long seq = ring.Next();
        ring[seq].OrderId = i;
        ring.Publish(seq);
    }

    disruptor.Shutdown(TimeSpan.FromSeconds(20));

    Console.WriteLine($"  스톨 감지 {stalls.Count}회 (rate-limit 200ms 로 로그 폭주 방지)");
    if (stalls.Count > 0)
        Console.WriteLine($"  {stalls[0]}");
    Console.WriteLine("  → 어느 핸들러가 병목인지 즉시 지목된다 (이게 없으면 사고를 못 푼다)");
}

// =====================================================
// 예제 12: 성능 벤치마크
// =====================================================
void PerformanceBenchmark()
{
    Console.WriteLine("--- 예제 12: 처리량 (단일 프로듀서) ---");

    const int iterations = 10_000_000;
    using var completion = new ManualResetEventSlim(false);

    using var disruptor = new Disruptor<OrderEvent>(
        () => new OrderEvent(), 1024 * 64, ProducerType.Single, new YieldingWaitStrategy(),
        new DisruptorOptions { Name = "benchmark", AutoClearSlots = false });

    var handler = new BenchmarkHandler(iterations, completion);
    disruptor.HandleEventsWith(handler);

    var ringBuffer = disruptor.Start();

    // 워밍업
    for (int i = 0; i < 10_000; i++)
    {
        long warm = ringBuffer.Next();
        ringBuffer[warm].OrderId = i;
        ringBuffer.Publish(warm);
    }
    Thread.Sleep(100);
    handler.Reset(iterations);
    completion.Reset();

    long before = GC.GetTotalAllocatedBytes(precise: false);
    var sw = Stopwatch.StartNew();

    for (int i = 0; i < iterations; i++)
    {
        long seq = ringBuffer.Next();
        ringBuffer[seq].OrderId = i;
        ringBuffer.Publish(seq);
    }

    bool done = completion.Wait(TimeSpan.FromSeconds(60));
    sw.Stop();
    long allocated = GC.GetTotalAllocatedBytes(precise: false) - before;

    Console.WriteLine($"  이벤트     : {iterations:N0}");
    Console.WriteLine($"  경과       : {sw.Elapsed.TotalMilliseconds:F2} ms");
    Console.WriteLine($"  처리량     : {iterations / sw.Elapsed.TotalSeconds:N0} ops/sec");
    Console.WriteLine($"  평균 지연  : {sw.Elapsed.TotalMilliseconds * 1_000_000 / iterations:F1} ns/op");
    Console.WriteLine($"  할당       : {allocated:N0} bytes ({(double)allocated / iterations:F4} B/event)");
    Console.WriteLine(done ? "  → 전량 처리 확인" : "  → 타임아웃! 유실 발생");

    disruptor.Shutdown(TimeSpan.FromSeconds(10));
}

// =====================================================
// 예제 13: 다중 프로듀서 무결성 검증 (백프레셔 회귀 테스트)
// =====================================================
void MultiProducerIntegrityCheck()
{
    Console.WriteLine("--- 예제 13: 다중 프로듀서 무결성 (링을 작게 잡아 백프레셔 유발) ---");

    const int producerCount = 8;
    const int eventsPerProducer = 100_000;
    const int ringSize = 1024;   // 일부러 작게 — 여기가 결함이 드러나는 조건

    long totalExpected = (long)producerCount * eventsPerProducer;
    using var completion = new ManualResetEventSlim(false);

    using var disruptor = new Disruptor<OrderEvent>(
        () => new OrderEvent(), ringSize, ProducerType.Multi, new YieldingWaitStrategy(),
        new DisruptorOptions { Name = "mp-integrity", AutoClearSlots = false });

    var handler = new IntegrityHandler(totalExpected, completion);
    disruptor.HandleEventsWith(handler);

    var ringBuffer = disruptor.Start();

    var sw = Stopwatch.StartNew();
    var producers = new Thread[producerCount];

    for (int p = 0; p < producerCount; p++)
    {
        int producerId = p;
        producers[p] = new Thread(() =>
        {
            for (int i = 0; i < eventsPerProducer; i++)
            {
                long seq = ringBuffer.Next();
                try
                {
                    var evt = ringBuffer[seq];
                    evt.OrderId = seq;                // 슬롯에 자기 시퀀스를 기록
                    evt.Symbol = "P";
                    evt.Quantity = producerId;
                }
                finally
                {
                    ringBuffer.Publish(seq);
                }
            }
        })
        { IsBackground = true };
        producers[p].Start();
    }

    foreach (var t in producers) t.Join();

    bool done = completion.Wait(TimeSpan.FromSeconds(30));
    sw.Stop();

    disruptor.Shutdown(TimeSpan.FromSeconds(10));

    Console.WriteLine($"  프로듀서   : {producerCount}, 링 크기: {ringSize}");
    Console.WriteLine($"  기대       : {totalExpected:N0}건");
    Console.WriteLine($"  소비       : {handler.Count:N0}건 (유실 {totalExpected - handler.Count:N0})");
    Console.WriteLine($"  슬롯 덮어쓰기: {handler.Mismatches:N0}회");
    Console.WriteLine($"  경과       : {sw.Elapsed.TotalMilliseconds:F2} ms " +
                      $"({handler.Count / sw.Elapsed.TotalSeconds:N0} ops/sec)");
    Console.WriteLine(done && handler.Count == totalExpected && handler.Mismatches == 0
        ? "  → 유실 0, 덮어쓰기 0"
        : "  → 결함 회귀! 백프레셔가 동작하지 않습니다");
}

// =====================================================
// 이벤트 및 핸들러 정의
// =====================================================

public sealed class OrderEvent : IClearable
{
    public long OrderId { get; set; }
    public int RoomId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public double Price { get; set; }
    public int Quantity { get; set; }
    public byte[]? Payload { get; set; }

    /// <summary>
    /// 슬롯이 재사용되기 전까지 외부 참조를 붙들지 않도록 프레임워크가 호출한다.
    /// 이것이 없으면 객체 수명이 링 버퍼 한 바퀴만큼 연장된다.
    /// </summary>
    public void Clear()
    {
        if (Payload is not null)
        {
            ArrayPool<byte>.Shared.Return(Payload);
            Payload = null;
        }
    }

    public override string ToString()
        => $"Order[{OrderId}] {Symbol} {Quantity}@{Price:F2}";
}

/// <summary>델리게이트조차 만들지 않는 구조체 translator.</summary>
public readonly struct FillMsftOrder : IEventTranslator<OrderEvent, long>
{
    public void Translate(OrderEvent evt, long sequence, long id)
    {
        evt.OrderId = id;
        evt.Symbol = "MSFT";
        evt.Price = 300.0;
        evt.Quantity = 200;
    }
}

public sealed class PrintHandler : IEventHandler<OrderEvent>
{
    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
        => Console.WriteLine($"  [Consumer] seq={sequence}: {data}");
}

public sealed class JournalCountHandler : IEventHandler<OrderEvent>
{
    private long _count;
    public long Count => Volatile.Read(ref _count);
    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
        => Interlocked.Increment(ref _count);
}

public sealed class ReplicationHandler : IEventHandler<OrderEvent>
{
    private long _count;
    public long Count => Volatile.Read(ref _count);
    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
        => Interlocked.Increment(ref _count);
}

public sealed class BusinessLogicHandler : IEventHandler<OrderEvent>
{
    private long _count;

    public long Count => Volatile.Read(ref _count);
    public long MaxBatch { get; private set; }

    public void OnBatchStart(long batchSize)
    {
        if (batchSize > MaxBatch) MaxBatch = batchSize;
    }

    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
        => Interlocked.Increment(ref _count);
}

/// <summary>이벤트 구간을 span 으로 통째로 받아 합계를 낸다.</summary>
public sealed class SpanSumHandler : IBatchEventHandler<OrderEvent>
{
    public long Sum { get; private set; }
    public long Events { get; private set; }
    public long BatchCalls { get; private set; }
    public int MaxSegment { get; private set; }

    public void OnBatch(ReadOnlySpan<OrderEvent> events, long startSequence, bool endOfBatch)
    {
        BatchCalls++;
        if (events.Length > MaxSegment) MaxSegment = events.Length;

        for (int i = 0; i < events.Length; i++)
            Sum += events[i].OrderId;

        Events += events.Length;
    }
}

public sealed class SlowBusinessHandler : IEventHandler<OrderEvent>
{
    private readonly int _sleepMs;
    private long _count;

    public SlowBusinessHandler(int sleepMs) => _sleepMs = sleepMs;

    public long Count => Volatile.Read(ref _count);

    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
    {
        Thread.Sleep(_sleepMs);
        Interlocked.Increment(ref _count);
    }
}

public sealed class BenchmarkHandler : IEventHandler<OrderEvent>
{
    private readonly ManualResetEventSlim _completion;
    private long _remaining;

    public BenchmarkHandler(long expected, ManualResetEventSlim completion)
    {
        _remaining = expected;
        _completion = completion;
    }

    public void Reset(long expected) => Volatile.Write(ref _remaining, expected);

    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
    {
        // 단일 컨슈머이므로 Interlocked 없이 감소시켜도 안전하다 (측정 오버헤드 제거).
        if (--_remaining == 0)
            _completion.Set();
    }
}

public sealed class IntegrityHandler : IEventHandler<OrderEvent>
{
    private readonly long _expected;
    private readonly ManualResetEventSlim _completion;
    private long _count;
    private long _mismatches;

    public IntegrityHandler(long expected, ManualResetEventSlim completion)
    {
        _expected = expected;
        _completion = completion;
    }

    public long Count => Volatile.Read(ref _count);
    public long Mismatches => Volatile.Read(ref _mismatches);

    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
    {
        if (data.OrderId != sequence)
            _mismatches++;

        if (++_count == _expected)
            _completion.Set();
    }
}

/// <summary>
/// 이벤트 소비와 고정 틱 시뮬레이션을 한 스레드에서 결합한다.
/// 이벤트가 없어도 OnTimeout 이 호출되므로 틱이 밀리지 않는다.
/// </summary>
public sealed class TickedRoomHandler : IEventHandler<OrderEvent>
{
    private readonly Dictionary<int, int> _roomCommandCounts = new();
    private readonly List<(int room, long orderId)> _pending = new(256);
    private readonly FixedTickDriver _driver;

    public TickedRoomHandler()
    {
        _driver = FixedTickDriver.At60Hz(_ => Simulate());
    }

    public long TickCount => _driver.CurrentTick;
    public long SkippedTicks => _driver.SkippedTicks;

    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
    {
        // 커맨드를 즉시 실행하지 않고 현재 틱 버킷에 모은다 -> 틱 내 결정론 확보
        _pending.Add((data.RoomId, data.OrderId));

        if (endOfBatch)
            _driver.TryAdvance();
    }

    /// <summary>이벤트가 없어도 호출된다 (TimeoutBlockingWaitStrategy).</summary>
    public void OnTimeout(long lastProcessedSequence) => _driver.TryAdvance();

    private void Simulate()
    {
        foreach (var (room, _) in _pending)
            _roomCommandCounts[room] = _roomCommandCounts.GetValueOrDefault(room) + 1;

        _pending.Clear();
        // 실제 게임 서버라면 여기서 룸별 시뮬레이션 스텝 + 스냅샷 배치 송신을 수행한다.
    }
}

/// <summary>샤드 하나를 담당하는 핸들러. 룸별 순서 위반을 검출한다.</summary>
public sealed class RoomShardHandler : IEventHandler<OrderEvent>
{
    private readonly Dictionary<int, long> _lastOrderPerRoom = new();
    private long _count;
    private long _violations;

    public long Count => Volatile.Read(ref _count);
    public long OrderViolations => Volatile.Read(ref _violations);
    public int RoomCount => _lastOrderPerRoom.Count;

    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
    {
        // 핸들러는 샤드당 하나이므로 이 사전에 대한 접근은 단일 스레드다.
        if (_lastOrderPerRoom.TryGetValue(data.RoomId, out long last) && data.OrderId <= last)
            _violations++;

        _lastOrderPerRoom[data.RoomId] = data.OrderId;
        _count++;
    }
}

/// <summary>순서가 필요 없는 느린 I/O 를 담당하는 워커.</summary>
public sealed class DbWriteWorker : IWorkHandler<OrderEvent>
{
    private readonly int _index;
    private long _handled;

    public DbWriteWorker(int index) => _index = index;

    public long Handled => Volatile.Read(ref _handled);

    public void OnEvent(OrderEvent evt)
    {
        // 실제로는 DB 배치 INSERT / 외부 API 호출
        Thread.SpinWait(20);
        Interlocked.Increment(ref _handled);
    }
}

/// <summary>리플레이 검증용 월드 상태.</summary>
public sealed class WorldState
{
    private readonly long[] _rooms = new long[8];

    public void Apply(int roomId, long orderId, int quantity)
        => _rooms[roomId & 7] = _rooms[roomId & 7] * 31 + orderId * quantity + 7;

    public long Checksum
    {
        get
        {
            long h = 17;
            foreach (long v in _rooms) h = h * 31 + v;
            return h;
        }
    }
}

public sealed class WorldUpdateHandler : IEventHandler<OrderEvent>
{
    private readonly WorldState _world;

    public WorldUpdateHandler(WorldState world) => _world = world;

    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
        => _world.Apply(data.RoomId, data.OrderId, data.Quantity);
}

/// <summary>저널 직렬화/역직렬화.</summary>
public sealed class OrderEventCodec : IJournalSerializer<OrderEvent>, IJournalDeserializer<OrderEvent>
{
    private const int RecordSize = 16;

    public int Serialize(OrderEvent evt, Span<byte> destination)
    {
        if (destination.Length < RecordSize)
            return -1;

        BinaryPrimitives.WriteInt64LittleEndian(destination, evt.OrderId);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], evt.RoomId);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], evt.Quantity);
        return RecordSize;
    }

    public void Deserialize(ReadOnlySpan<byte> source, OrderEvent target)
    {
        target.OrderId = BinaryPrimitives.ReadInt64LittleEndian(source);
        target.RoomId = BinaryPrimitives.ReadInt32LittleEndian(source[8..]);
        target.Quantity = BinaryPrimitives.ReadInt32LittleEndian(source[12..]);
    }
}
