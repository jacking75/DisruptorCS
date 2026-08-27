using System.Buffers.Binary;
using Disruptor.Game;

namespace ReplayInspector;

/// <summary>
/// 이 도구가 해석할 줄 아는 페이로드 형식.
/// </summary>
/// <remarks>
/// <c>samples/MatchSimulation</c> 이 쓰는 24바이트 고정 레코드입니다.
/// 저널 자체는 페이로드를 불투명한 바이트로 다루므로, 형식을 아는 쪽은 항상 도구입니다 —
/// 자신의 이벤트 형식에 맞춰 이 클래스만 갈아 끼우면 됩니다.
/// <code>
/// [roomId 8B][playerId 8B][opCode 4B][value 4B]
/// </code>
/// </remarks>
public sealed class MatchCodec : IJournalSerializer<MatchCommand>, IJournalDeserializer<MatchCommand>
{
    public const int RecordSize = 24;

    public static MatchCodec Instance { get; } = new();

    public int Serialize(MatchCommand evt, Span<byte> destination)
    {
        if (destination.Length < RecordSize)
            return -1;

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

    /// <summary>이 페이로드를 해석할 수 있는가.</summary>
    public static bool CanDecode(ReadOnlySpan<byte> payload) => payload.Length == RecordSize;

    public static string Describe(ReadOnlySpan<byte> payload)
    {
        var command = new MatchCommand();
        Instance.Deserialize(payload, command);
        return command.ToString();
    }
}

/// <summary>매치 커맨드.</summary>
public sealed class MatchCommand
{
    public long RoomId;
    public long PlayerId;
    public int OpCode;
    public int Value;

    public string OpName => OpCode switch
    {
        0 => "DAMAGE",
        1 => "HEAL",
        2 => "SCORE",
        _ => $"OP{OpCode}",
    };

    public override string ToString()
        => $"room={RoomId,-6} player={PlayerId,-8} {OpName,-6} value={Value}";
}

/// <summary>룸 하나의 상태. 재생으로 복원되는 대상입니다.</summary>
public struct RoomState
{
    public int Hp;
    public long Score;
    public long Applied;

    public override readonly string ToString() => $"hp={Hp,-5} score={Score,-8} n={Applied}";
}

/// <summary>
/// 재생 대상 상태 기계. MatchSimulation 의 핸들러와 <b>같은 규칙</b>이어야 합니다.
/// </summary>
public sealed class MatchState
{
    private readonly Dictionary<long, RoomState> _rooms = new();

    public IReadOnlyDictionary<long, RoomState> Rooms => _rooms;

    public long Applied { get; private set; }

    public void Apply(MatchCommand command)
    {
        ref var state = ref System.Runtime.InteropServices.CollectionsMarshal
                              .GetValueRefOrAddDefault(_rooms, command.RoomId, out bool existed);

        if (!existed)
            state.Hp = 1000;

        switch (command.OpCode)
        {
            case 0: state.Hp = Math.Max(0, state.Hp - command.Value); break;
            case 1: state.Hp = Math.Min(1000, state.Hp + command.Value); break;
            default: state.Score += command.Value; break;
        }

        state.Applied++;
        Applied++;
    }

    /// <summary>상태 전체의 지문. 두 재생 결과가 같은지 한 줄로 비교할 때 씁니다.</summary>
    public ulong Fingerprint()
    {
        ulong hash = 1469598103934665603UL;

        foreach (long roomId in _rooms.Keys.Order())   // 딕셔너리 순회 순서에 의존하지 않는다
        {
            var state = _rooms[roomId];
            hash = Mix(hash, (ulong)roomId);
            hash = Mix(hash, (ulong)state.Hp);
            hash = Mix(hash, (ulong)state.Score);
            hash = Mix(hash, (ulong)state.Applied);
        }

        return hash;

        static ulong Mix(ulong hash, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                hash ^= (value >> (i * 8)) & 0xFF;
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }
}
