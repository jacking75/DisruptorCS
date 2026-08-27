using System.Buffers.Binary;
using System.Diagnostics;
using Disruptor;
using Disruptor.Dsl;
using Disruptor.Game;
using Disruptor.WaitStrategy;

// ─────────────────────────────────────────────────────────────────────────────
// 이 샘플이 답하는 질문:
//   "서버가 죽었을 때, 죽기 직전 상태를 정말 복원할 수 있는가?"
//
// 답: 시퀀스 번호가 곧 전역 실행 순서이므로, 저널을 그 순서대로 재생하면
// 상태가 비트 단위로 재현됩니다. 이 샘플은 그것을 주장하지 않고 검증합니다 —
// 라이브 상태의 해시와 재생 상태의 해시를 룸 단위로 비교합니다.
//
// 함께 보여주는 것:
//   · ShardedDisruptor  — 룸 10,000개를 스레드 8개로 처리 (룸당 순서는 보장)
//   · FixedTickDriver   — 60Hz 고정 스텝 시뮬레이션
//   · JournalWriter     — 샤드별 저널, 배치 경계에서만 flush
//   · 크래시 복구       — 잘린 저널을 TruncateAtCorruption 으로 안전하게 읽기
// ─────────────────────────────────────────────────────────────────────────────

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== 샘플: 매치 시뮬레이션 + 저널 크래시 복구 ===\n");

const int ShardCount = 8;
const int RingPerShard = 4096;
const int RoomCount = 10_000;
const int ProducerThreads = 4;
const int CommandsPerProducer = 500_000;   // 총 200만 건 -> 저널 약 100MB

string journalDir = Path.Combine(Path.GetTempPath(), "disruptor-match-" + Environment.ProcessId);
Directory.CreateDirectory(journalDir);
Console.WriteLine($"저널 디렉터리: {journalDir}");
Console.WriteLine($"룸 {RoomCount:N0}개 · 샤드 {ShardCount}개 · 샤드당 링 {RingPerShard}\n");

using var timerScope = new HighResolutionTimerScope();

// ── 1) 라이브 실행 ───────────────────────────────────────────────────────────
var handlers = new MatchShardHandler[ShardCount];

using var sharded = new ShardedDisruptor<MatchCommand>(
    ShardCount,
    RingPerShard,
    () => new MatchCommand(),
    shard => handlers[shard] = new MatchShardHandler(
        shard, JournalWriter.CreateFile(Path.Combine(journalDir, $"shard{shard}.djrn"))),
    ProducerType.Multi,
    waitStrategyFactory: _ => new TimeoutBlockingWaitStrategy(TimeSpan.FromMilliseconds(1)),
    options: new DisruptorOptions
    {
        Name = "match",
        ShutdownTimeout = TimeSpan.FromSeconds(15),
    });

sharded.ProcessorHalted += (p, ex) => Console.Error.WriteLine($"[정지] {p.Name}: {ex?.Message}");
sharded.Start();

using var stop = new CancellationTokenSource();

var totalTimer = Stopwatch.StartNew();
var producers = StartProducers(sharded, stop.Token, ProducerThreads, RoomCount, CommandsPerProducer);
foreach (var t in producers) t.Join();

// "크래시"가 아니라 계획된 종료. 드레인이 끝나야 저널과 상태가 일치한다.
sharded.Shutdown(TimeSpan.FromSeconds(15));
totalTimer.Stop();

long liveApplied = handlers.Sum(h => h.Applied);
Console.WriteLine($"실행 시간   : {totalTimer.Elapsed.TotalSeconds:F1}초");
Console.WriteLine($"적용 커맨드 : {liveApplied:N0} ({liveApplied / totalTimer.Elapsed.TotalSeconds:N0}/s)");
Console.WriteLine($"틱          : {handlers.Sum(h => h.Ticks):N0} (샤드별 60Hz)");
Console.WriteLine();

Console.WriteLine("샤드별 부하 (샤딩이 실제로 균등한지 확인):");
foreach (var h in handlers)
    Console.WriteLine($"  샤드 {h.Shard}: 룸 {h.RoomCount,5:N0}개 · 커맨드 {h.Applied,9:N0} · 저널 {h.JournalRecords,9:N0}건");
Console.WriteLine();

// ── 2) 저널 무결성 검증 ──────────────────────────────────────────────────────
Console.WriteLine("저널 검증:");
long totalRecords = 0;
foreach (var h in handlers)
{
    string path = Path.Combine(journalDir, $"shard{h.Shard}.djrn");
    using var fs = File.OpenRead(path);
    var result = JournalReader.Verify(fs);
    totalRecords += result.RecordCount;

    Console.WriteLine($"  샤드 {h.Shard}: {result} · {new FileInfo(path).Length / 1024.0:F0}KB");

    if (!result.IsValid)
        Console.Error.WriteLine("  !! 손상 감지 — 아래 재생 결과를 신뢰하면 안 됩니다.");
}
Console.WriteLine($"  합계: {totalRecords:N0}건 (적용 {liveApplied:N0}건과 일치해야 함)");
Console.WriteLine();

// ── 3) 재생: 빈 상태에서 저널만으로 상태를 복원한다 ──────────────────────────
var replaySw = Stopwatch.StartNew();
var replayed = new MatchShardHandler[ShardCount];
long replayCount = 0;

for (int shard = 0; shard < ShardCount; shard++)
{
    // 저널 쓰기 없이, 상태 적용만 하는 핸들러.
    replayed[shard] = new MatchShardHandler(shard, writer: null);

    using var fs = File.OpenRead(Path.Combine(journalDir, $"shard{shard}.djrn"));
    replayCount += JournalReader.Replay(
        fs,
        MatchCommandCodec.Instance,
        () => new MatchCommand(),
        replayed[shard],
        // 크래시로 마지막 레코드가 잘려 있어도 마지막 유효 지점까지 복구한다.
        new JournalReadOptions { Mode = JournalReadMode.TruncateAtCorruption });
}

replaySw.Stop();

Console.WriteLine($"재생: {replayCount:N0}건 · {replaySw.Elapsed.TotalMilliseconds:F0}ms " +
                  $"({replayCount / Math.Max(0.001, replaySw.Elapsed.TotalSeconds):N0}/s)");
Console.WriteLine();

// ── 4) 검증: 라이브 상태 == 재생 상태 ────────────────────────────────────────
int mismatched = 0;
int comparedRooms = 0;

for (int shard = 0; shard < ShardCount; shard++)
{
    foreach (var (roomId, liveState) in handlers[shard].Rooms)
    {
        comparedRooms++;

        if (!replayed[shard].Rooms.TryGetValue(roomId, out var replayState) || !liveState.Equals(replayState))
        {
            if (mismatched < 5)
                Console.Error.WriteLine($"  불일치 룸 {roomId}: 라이브={liveState} 재생={replayState}");
            mismatched++;
        }
    }
}

Console.WriteLine("결정론 검증:");
Console.WriteLine($"  비교한 룸    : {comparedRooms:N0}");
Console.WriteLine($"  불일치       : {mismatched}");
Console.WriteLine($"  라이브 체크섬: {Checksum(handlers):X16}");
Console.WriteLine($"  재생 체크섬  : {Checksum(replayed):X16}");
Console.WriteLine();

bool ok = mismatched == 0 && Checksum(handlers) == Checksum(replayed) && comparedRooms > 0;
Console.WriteLine(ok
    ? "-> 재생 상태가 라이브 상태와 완전히 일치합니다. 크래시 시 저널만으로 복구할 수 있습니다."
    : "-> 불일치가 있습니다. 핸들러에 비결정적 입력(시계·난수·해시 순회)이 섞였는지 확인하세요.");

// 정리
foreach (var h in handlers) h.Dispose();
try { Directory.Delete(journalDir, recursive: true); } catch (IOException) { }

Environment.ExitCode = ok ? 0 : 1;

static ulong Checksum(MatchShardHandler[] shards)
{
    ulong hash = 1469598103934665603UL;   // FNV-1a 64

    foreach (var shard in shards)
    {
        // 룸 ID 로 정렬해야 딕셔너리 순회 순서에 의존하지 않는다.
        foreach (long roomId in shard.Rooms.Keys.Order())
        {
            var state = shard.Rooms[roomId];
            hash = Fnv(hash, (ulong)roomId);
            hash = Fnv(hash, (ulong)state.Hp);
            hash = Fnv(hash, (ulong)state.Score);
            hash = Fnv(hash, (ulong)state.Applied);
        }
    }

    return hash;

    static ulong Fnv(ulong hash, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            hash ^= (value >> (i * 8)) & 0xFF;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}

static Thread[] StartProducers(ShardedDisruptor<MatchCommand> sharded, CancellationToken ct,
                               int threadCount, int roomCount, int commandsPerThread)
{
    var threads = new Thread[threadCount];

    for (int t = 0; t < threadCount; t++)
    {
        int seed = t + 1;

        threads[t] = new Thread(() =>
        {
            // 결정론을 위해 시드 고정 — 저널을 재생하면 같은 결과가 나와야 한다.
            var rng = new Random(seed);

            for (long n = 0; n < commandsPerThread && !ct.IsCancellationRequested; n++)
            {
                long roomId = rng.Next(roomCount);
                int op = (int)(n % 3);
                int value = rng.Next(1, 20);
                long playerId = roomId * 8 + (n % 8);

                // 게임 커맨드는 유실되면 안 되므로 Block 정책. 단, 종료 시 탈출 가능해야 한다.
                if (!sharded.TryPublish(
                        roomId,
                        (cmd, seq) =>
                        {
                            cmd.RoomId = roomId;
                            cmd.PlayerId = playerId;
                            cmd.OpCode = op;
                            cmd.Value = value;
                        },
                        BackpressurePolicy.FailAfterTimeout,
                        timeout: TimeSpan.FromMilliseconds(200),
                        cancellationToken: ct))
                {
                    // 샤드가 밀리는 중. 실서비스라면 여기서 지표를 올린다.
                }
            }
        })
        { IsBackground = true, Name = $"match-producer-{t}" };

        threads[t].Start();
    }

    return threads;
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>룸 커맨드. 슬롯이 재사용되므로 참조 필드를 두지 않았습니다.</summary>
public sealed class MatchCommand
{
    public long RoomId;
    public long PlayerId;
    public int OpCode;
    public int Value;
}

/// <summary>룸 하나의 시뮬레이션 상태.</summary>
public struct RoomState : IEquatable<RoomState>
{
    public int Hp;
    public long Score;
    public long Applied;

    public bool Equals(RoomState other)
        => Hp == other.Hp && Score == other.Score && Applied == other.Applied;

    public override bool Equals(object? obj) => obj is RoomState s && Equals(s);

    public override int GetHashCode() => HashCode.Combine(Hp, Score, Applied);

    public override string ToString() => $"(hp={Hp}, score={Score}, n={Applied})";
}

/// <summary>
/// 커맨드 ↔ 바이트 변환. 저널 쓰기와 재생이 <b>같은 코덱</b>을 써야 재현이 성립합니다.
/// </summary>
public sealed class MatchCommandCodec : IJournalSerializer<MatchCommand>, IJournalDeserializer<MatchCommand>
{
    public const int RecordSize = 8 + 8 + 4 + 4;

    public static MatchCommandCodec Instance { get; } = new();

    public int Serialize(MatchCommand evt, Span<byte> destination)
    {
        if (destination.Length < RecordSize)
            return -1;   // 호출자가 더 큰 버퍼로 재시도한다

        BinaryPrimitives.WriteInt64LittleEndian(destination, evt.RoomId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], evt.PlayerId);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], evt.OpCode);
        BinaryPrimitives.WriteInt32LittleEndian(destination[20..], evt.Value);
        return RecordSize;
    }

    public void Deserialize(ReadOnlySpan<byte> source, MatchCommand target)
    {
        target.RoomId = BinaryPrimitives.ReadInt64LittleEndian(source);
        target.PlayerId = BinaryPrimitives.ReadInt64LittleEndian(source[8..]);
        target.OpCode = BinaryPrimitives.ReadInt32LittleEndian(source[16..]);
        target.Value = BinaryPrimitives.ReadInt32LittleEndian(source[20..]);
    }
}

/// <summary>
/// 샤드 하나의 핸들러. <b>저널에 먼저 쓰고</b> 그 다음에 상태를 바꿉니다.
/// </summary>
/// <remarks>
/// 순서가 반대면 "상태는 바뀌었는데 저널에는 없는" 이벤트가 생겨 복구가 어긋납니다.
/// <para>
/// <paramref name="writer"/>가 null 이면 저널을 쓰지 않습니다 — 재생 시 이 모드를 씁니다.
/// 라이브와 재생이 <b>같은 클래스</b>를 쓰기 때문에, 두 경로의 로직이 갈라질 여지가 없습니다.
/// </para>
/// </remarks>
public sealed class MatchShardHandler : IEventHandler<MatchCommand>, IDisposable
{
    private readonly JournalWriter? _writer;
    private readonly byte[] _scratch = new byte[MatchCommandCodec.RecordSize];
    private readonly FixedTickDriver _tick;
    private readonly Dictionary<long, RoomState> _rooms = new();

    private long _applied;
    private long _journalRecords;
    private long _pendingFlush;
    private int _disposed;

    public MatchShardHandler(int shard, JournalWriter? writer)
    {
        Shard = shard;
        _writer = writer;
        _tick = FixedTickDriver.At60Hz(OnTick);
    }

    public int Shard { get; }

    public IReadOnlyDictionary<long, RoomState> Rooms => _rooms;

    public int RoomCount => _rooms.Count;

    public long Applied => _applied;

    public long JournalRecords => _journalRecords;

    public long Ticks => _tick.CurrentTick;

    public void OnEvent(MatchCommand data, long sequence, bool endOfBatch)
    {
        // (1) 먼저 기록한다. 여기서 죽으면 이 이벤트는 없었던 것이 된다 — 그것이 옳다.
        if (_writer is not null)
        {
            MatchCommandCodec.Instance.Serialize(data, _scratch);
            _writer.Write(sequence, _scratch);
            _journalRecords++;
            _pendingFlush++;
        }

        // (2) 그 다음 상태를 바꾼다.
        Apply(data);

        // (3) 배치 경계에서만 flush + 틱. syscall 이 이벤트당 1회에서 배치당 1회로 줄어든다.
        if (endOfBatch)
        {
            FlushJournal();
            _tick.TryAdvance();
        }
    }

    private void Apply(MatchCommand cmd)
    {
        ref var state = ref System.Runtime.InteropServices.CollectionsMarshal
                              .GetValueRefOrAddDefault(_rooms, cmd.RoomId, out bool existed);

        if (!existed)
            state.Hp = 1000;   // 룸이 처음 생성되는 지점

        switch (cmd.OpCode)
        {
            case 0:  state.Hp = Math.Max(0, state.Hp - cmd.Value); break;   // 피해
            case 1:  state.Hp = Math.Min(1000, state.Hp + cmd.Value); break; // 회복
            default: state.Score += cmd.Value; break;                        // 점수
        }

        state.Applied++;
        _applied++;
    }

    /// <summary>유휴 구간에서도 저널을 내리고 틱을 진행한다.</summary>
    public void OnTimeout(long lastProcessedSequence)
    {
        FlushJournal();
        _tick.TryAdvance();
    }

    public void OnShutdown() => Dispose();

    private void FlushJournal()
    {
        if (_writer is null || _pendingFlush == 0)
            return;

        _writer.Flush();
        _pendingFlush = 0;
    }

    private void OnTick(long tickId)
    {
        // 실제라면 여기서 물리·쿨다운·리스폰을 고정 스텝으로 처리한다.
        // 주의: 이 안에서 시계나 난수를 읽으면 재생이 어긋난다. 필요하면 틱 ID 로 시드를 만든다.
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        FlushJournal();
        _writer?.Dispose();
    }
}
