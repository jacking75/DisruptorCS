using System.Buffers;
using System.Diagnostics;
using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;

// ─────────────────────────────────────────────────────────────────────────────
// 이 샘플이 답하는 질문:
//   "패킷 브로드캐스트에서 syscall 을 어떻게 1/N 로 줄이는가?"
//
// 채팅 서버는 메시지 1건을 방 안의 모든 세션에 보낸다. 순진하게 짜면
// 메시지 × 세션 만큼 Send 를 호출한다. 배치 경계를 이용하면 그 횟수가
// "배치 × 세션" 으로 줄어든다 — 이 샘플은 그 차이를 실제로 측정한다.
// ─────────────────────────────────────────────────────────────────────────────

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== 샘플: 채팅 브로드캐스트 (배치 flush 효과 측정) ===\n");

const int Sessions = 200;
const int Messages = 200_000;
const int RingSize = 8192;

Console.WriteLine($"세션 {Sessions}개 · 메시지 {Messages:N0}건 · 링 {RingSize}\n");

// ── 1) 배치를 무시하고 매 이벤트마다 flush 하는 구성 ────────────────────────
var naive = RunPipeline(flushPerEvent: true);

// ── 2) endOfBatch 에서만 flush 하는 구성 ────────────────────────────────────
var batched = RunPipeline(flushPerEvent: false);

Console.WriteLine();
Console.WriteLine($"{"구성",-20}{"경과",12}{"flush(=syscall)",20}{"flush/메시지",16}");
Console.WriteLine(new string('-', 68));
Console.WriteLine($"{"이벤트마다 flush",-20}{naive.ElapsedMs,10:F0}ms{naive.Flushes,20:N0}{(double)naive.Flushes / Messages,16:F2}");
Console.WriteLine($"{"배치 끝에만 flush",-20}{batched.ElapsedMs,10:F0}ms{batched.Flushes,20:N0}{(double)batched.Flushes / Messages,16:F4}");
Console.WriteLine();
Console.WriteLine($"-> flush 횟수 {(double)naive.Flushes / Math.Max(1, batched.Flushes):F0}배 감소, " +
                  $"처리 시간 {naive.ElapsedMs / Math.Max(1.0, batched.ElapsedMs):F1}배 단축");
Console.WriteLine("-> 실제 서버에서는 flush 1회가 Socket.SendAsync 1회다. 이 배수가 곧 syscall 절감이다.");
Console.WriteLine($"   (전달 건수 검증: naive {naive.Delivered:N0} == batched {batched.Delivered:N0})");

static Result RunPipeline(bool flushPerEvent)
{
    // 검열 → 브로드캐스트. 검열이 끝난 메시지만 나간다.
    var moderation = new ModerationHandler();
    var broadcast = new BroadcastHandler(Sessions, flushPerEvent);

    using var disruptor = new Disruptor<ChatMessage>(
        () => new ChatMessage(),
        RingSize,
        ProducerType.Multi,                                   // 수신 IO 스레드 여러 개
        new TimeoutBlockingWaitStrategy(TimeSpan.FromMilliseconds(1)),
        new DisruptorOptions { Name = flushPerEvent ? "chat-naive" : "chat-batched" });

    disruptor.HandleEventsWith(moderation).Then(broadcast);
    var ring = disruptor.Start();

    var sw = Stopwatch.StartNew();

    // 4개의 수신 스레드가 동시에 게시한다.
    var producers = new Thread[4];
    int perProducer = Messages / producers.Length;

    for (int p = 0; p < producers.Length; p++)
    {
        int producerId = p;
        producers[p] = new Thread(() =>
        {
            for (int i = 0; i < perProducer; i++)
            {
                long seq = ring.Next();
                try
                {
                    var msg = ring[seq];
                    msg.SessionId = producerId * 10_000 + (i % 50);
                    msg.RoomId = i % 8;
                    msg.Length = FillPayload(msg, i);
                }
                finally
                {
                    ring.Publish(seq);
                }
            }
        })
        { IsBackground = true };
    }

    foreach (var t in producers) t.Start();
    foreach (var t in producers) t.Join();

    disruptor.Shutdown(TimeSpan.FromSeconds(30));
    sw.Stop();

    return new Result(sw.Elapsed.TotalMilliseconds, broadcast.Flushes, broadcast.Delivered);
}

static int FillPayload(ChatMessage msg, int i)
{
    msg.Payload ??= ArrayPool<byte>.Shared.Rent(64);

    // 실제라면 여기서 UTF-8 인코딩. 샘플에서는 길이만 흉내낸다.
    int length = 16 + (i % 32);
    msg.Payload[0] = (byte)(i & 0xFF);
    return length;
}

internal readonly record struct Result(double ElapsedMs, long Flushes, long Delivered);

/// <summary>채팅 메시지. 슬롯이 재사용되므로 풀 버퍼를 반드시 반납한다.</summary>
public sealed class ChatMessage : IClearable
{
    public long SessionId;
    public int RoomId;
    public int Length;
    public byte[]? Payload;

    public void Clear()
    {
        if (Payload is not null)
        {
            ArrayPool<byte>.Shared.Return(Payload);
            Payload = null;
        }

        Length = 0;
    }
}

/// <summary>검열 단계. 브로드캐스트보다 먼저 실행되어야 한다.</summary>
public sealed class ModerationHandler : IEventHandler<ChatMessage>
{
    private long _checked;

    public long Checked => Volatile.Read(ref _checked);

    public void OnEvent(ChatMessage data, long sequence, bool endOfBatch)
    {
        // 실제라면 금칙어 검사 / rate limit. 여기서는 비용만 흉내낸다.
        if ((data.SessionId & 63) == 0)
            Thread.SpinWait(4);

        Interlocked.Increment(ref _checked);
    }
}

/// <summary>
/// 브로드캐스트 단계. 세션별 송신 버퍼에 인코딩만 하고, flush 시점에만 "전송"한다.
/// </summary>
public sealed class BroadcastHandler : IEventHandler<ChatMessage>
{
    private readonly SessionBuffer[] _sessions;
    private readonly bool _flushPerEvent;

    private long _flushes;
    private long _delivered;

    public BroadcastHandler(int sessionCount, bool flushPerEvent)
    {
        _flushPerEvent = flushPerEvent;
        _sessions = new SessionBuffer[sessionCount];

        for (int i = 0; i < sessionCount; i++)
            _sessions[i] = new SessionBuffer();
    }

    public long Flushes => Volatile.Read(ref _flushes);
    public long Delivered => Volatile.Read(ref _delivered);

    /// <summary>배치 크기를 미리 알 수 있으므로 버퍼를 한 번에 확보한다.</summary>
    public void OnBatchStart(long batchSize)
    {
        int needed = (int)Math.Min(batchSize, 4096) * 64;

        foreach (var session in _sessions)
            session.EnsureCapacity(needed);
    }

    public void OnEvent(ChatMessage data, long sequence, bool endOfBatch)
    {
        // 메모리에만 쓴다. syscall 없음.
        foreach (var session in _sessions)
            session.Append(data);

        _delivered += _sessions.Length;

        if (_flushPerEvent || endOfBatch)
            FlushAll();
    }

    /// <summary>유휴 상태에서도 쌓인 것을 내보낸다 — 지연이 무한정 늘어나지 않게.</summary>
    public void OnTimeout(long lastProcessedSequence) => FlushAll();

    private void FlushAll()
    {
        bool flushedAny = false;

        foreach (var session in _sessions)
        {
            if (session.Flush())          // 실제라면 Socket.SendAsync 1회
                flushedAny = true;
        }

        if (flushedAny)
            _flushes += _sessions.Length;
    }

    /// <summary>세션 하나의 송신 버퍼.</summary>
    private sealed class SessionBuffer
    {
        private byte[] _buffer = new byte[4096];
        private int _length;

        public void EnsureCapacity(int extra)
        {
            if (_length + extra <= _buffer.Length)
                return;

            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + extra));
        }

        public void Append(ChatMessage message)
        {
            if (message.Payload is null)
                return;

            EnsureCapacity(message.Length);
            message.Payload.AsSpan(0, Math.Min(message.Length, message.Payload.Length))
                   .CopyTo(_buffer.AsSpan(_length));
            _length += message.Length;
        }

        /// <summary>보낼 것이 있으면 "전송"하고 true 를 반환한다.</summary>
        public bool Flush()
        {
            if (_length == 0)
                return false;

            _length = 0;   // 실제라면 여기서 Socket.SendAsync
            return true;
        }
    }
}
