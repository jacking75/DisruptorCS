using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;
using System.Diagnostics;


Console.WriteLine("=== LMAX Disruptor C# Implementation ===\n");

/// <summary>기본 사용 예제</summary>
void BasicExample()
{
    Console.WriteLine("--- Example 1: Basic Usage ---");

    using var disruptor = new Disruptor<OrderEvent>(
        eventFactory: () => new OrderEvent(),
        ringBufferSize: 1024,
        producerType: ProducerType.Single,
        waitStrategy: new BlockingWaitStrategy());

    // 핸들러 등록
    disruptor.HandleEventsWith(new PrintHandler());

    // 시작
    var ringBuffer = disruptor.Start();

    // 이벤트 게시 — 방법 1: Next/Publish 패턴
    for (int i = 0; i < 5; i++)
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

    // 이벤트 게시 — 방법 2: Lambda 패턴
    ringBuffer.PublishEvent((evt, seq) =>
    {
        evt.OrderId = 999;
        evt.Symbol = "GOOG";
        evt.Price = 2800.0;
        evt.Quantity = 50;
    });

    // 이벤트 게시 — 방법 3: using Scope 패턴
    using (var scope = ringBuffer.PublishEventScope())
    {
        var evt = scope.Event();
        evt.OrderId = 1000;
        evt.Symbol = "MSFT";
        evt.Price = 300.0;
        evt.Quantity = 200;
    }

    Thread.Sleep(500); // 처리 대기
    disruptor.Shutdown();
    Console.WriteLine("Basic example completed.\n");
}

/// <summary>의존성 그래프 예제</summary>
void DependencyGraphExample()
{
    Console.WriteLine("--- Example 2: Dependency Graph ---");
    Console.WriteLine("  JournalHandler ──┐");
    Console.WriteLine("                    ├──> BusinessLogicHandler");
    Console.WriteLine("  ReplicationHandler ┘\n");

    using var disruptor = new Disruptor<OrderEvent>(
        () => new OrderEvent(),
        ringBufferSize: 1024,
        producerType: ProducerType.Single,
        waitStrategy: new YieldingWaitStrategy());

    var journalHandler = new JournalHandler();
    var replicationHandler = new ReplicationHandler();
    var businessHandler = new BusinessLogicHandler();

    // 의존성 그래프 구성:
    // Journal과 Replication이 먼저 처리 → 그 후에 BusinessLogic 처리
    disruptor.HandleEventsWith(journalHandler, replicationHandler)
             .Then(businessHandler);

    var ringBuffer = disruptor.Start();

    const int eventCount = 10_000;
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

    Thread.Sleep(1000);
    Console.WriteLine($"  BusinessLogicHandler processed: {businessHandler.Count} events");
    disruptor.Shutdown();
}

/// <summary>성능 벤치마크</summary>
void PerformanceBenchmark()
{
    Console.WriteLine("--- Example 3: Performance Benchmark ---");

    const int iterations = 10_000_000;
    var countdownEvent = new CountdownEvent(1);

    using var disruptor = new Disruptor<OrderEvent>(
        () => new OrderEvent(),
        ringBufferSize: 1024 * 1024, // 1M
        producerType: ProducerType.Single,
        waitStrategy: new YieldingWaitStrategy());

    var handler = new BenchmarkHandler(iterations, countdownEvent);
    disruptor.HandleEventsWith(handler);

    var ringBuffer = disruptor.Start();

    // 워밍업
    for (int i = 0; i < 1000; i++)
    {
        long seq = ringBuffer.Next();
        ringBuffer[seq].OrderId = i;
        ringBuffer.Publish(seq);
    }
    Thread.Sleep(100);

    // 벤치마크 시작
    countdownEvent.Reset();
    handler.Reset(iterations);

    var sw = Stopwatch.StartNew();

    for (int i = 0; i < iterations; i++)
    {
        long seq = ringBuffer.Next();
        ringBuffer[seq].OrderId = i;
        ringBuffer.Publish(seq);
    }

    countdownEvent.Wait();
    sw.Stop();

    double opsPerSec = iterations / sw.Elapsed.TotalSeconds;
    double latencyNs = sw.Elapsed.TotalMilliseconds * 1_000_000 / iterations;

    Console.WriteLine($"  Events:        {iterations:N0}");
    Console.WriteLine($"  Elapsed:       {sw.Elapsed.TotalMilliseconds:F2} ms");
    Console.WriteLine($"  Throughput:    {opsPerSec:N0} ops/sec");
    Console.WriteLine($"  Avg Latency:   {latencyNs:F1} ns/op");

    disruptor.Shutdown();
}

/// <summary>다중 프로듀서 예제</summary>
void MultiProducerExample()
{
    Console.WriteLine("--- Example 4: Multi-Producer ---");

    const int producerCount = 4;
    const int eventsPerProducer = 100_000;
    var allDone = new CountdownEvent(1);
    long totalExpected = producerCount * eventsPerProducer;

    using var disruptor = new Disruptor<OrderEvent>(
        () => new OrderEvent(),
        ringBufferSize: 1024 * 64,
        producerType: ProducerType.Multi, // 다중 프로듀서!
        waitStrategy: new BlockingWaitStrategy());

    var handler = new BenchmarkHandler(totalExpected, allDone);
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
                    evt.OrderId = producerId * 1_000_000L + i;
                    evt.Symbol = $"P{producerId}";
                    evt.Price = i;
                    evt.Quantity = 1;
                }
                finally
                {
                    ringBuffer.Publish(seq);
                }
            }
        });
        producers[p].Start();
    }

    allDone.Wait(TimeSpan.FromSeconds(10));
    sw.Stop();

    double opsPerSec = totalExpected / sw.Elapsed.TotalSeconds;
    Console.WriteLine($"  Producers:     {producerCount}");
    Console.WriteLine($"  Total Events:  {totalExpected:N0}");
    Console.WriteLine($"  Elapsed:       {sw.Elapsed.TotalMilliseconds:F2} ms");
    Console.WriteLine($"  Throughput:    {opsPerSec:N0} ops/sec");

    disruptor.Shutdown();
}


// 예제 1: 기본 사용법
BasicExample();

Console.WriteLine();

// 예제 2: 의존성 그래프
DependencyGraphExample();

Console.WriteLine();

// 예제 3: 성능 벤치마크
PerformanceBenchmark();

Console.WriteLine();

// 예제 4: 다중 프로듀서
MultiProducerExample();


// =====================================================
// 이벤트 및 핸들러 클래스 정의
// =====================================================

public class OrderEvent
{
    public long OrderId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public double Price { get; set; }
    public int Quantity { get; set; }

    public void Clear()
    {
        OrderId = 0;
        Symbol = string.Empty;
        Price = 0;
        Quantity = 0;
    }

    public override string ToString()
        => $"Order[{OrderId}] {Symbol} {Quantity}@{Price:F2}";
}

/// <summary>저널링 핸들러 - 이벤트를 로그에 기록</summary>
public class JournalHandler : IEventHandler<OrderEvent>
{
    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
    {
        // 실제로는 파일이나 DB에 기록
        // Console.WriteLine($"  [Journal] seq={sequence}: {data}");
    }
}

/// <summary>복제 핸들러 - 이벤트를 복제</summary>
public class ReplicationHandler : IEventHandler<OrderEvent>
{
    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
    {
        // 실제로는 다른 노드에 복제
        // Console.WriteLine($"  [Replication] seq={sequence}: {data}");
    }
}

/// <summary>비즈니스 로직 핸들러 - 실제 주문 처리</summary>
public class BusinessLogicHandler : IEventHandler<OrderEvent>
{
    private long _count;

    public long Count => _count;

    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
    {
        // 실제 비즈니스 로직
        Interlocked.Increment(ref _count);
    }
}

public class PrintHandler : IEventHandler<OrderEvent>
{
    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
    {
        Console.WriteLine($"  [Consumer] seq={sequence}: {data}");
    }
}

public class BenchmarkHandler : IEventHandler<OrderEvent>
{
    private long _remaining;
    private CountdownEvent _latch;

    public BenchmarkHandler(long expected, CountdownEvent latch)
    {
        _remaining = expected;
        _latch = latch;
    }

    public void Reset(long expected)
    {
        _remaining = expected;
    }

    public void OnEvent(OrderEvent data, long sequence, bool endOfBatch)
    {
        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            _latch.Signal();
        }
    }
}
