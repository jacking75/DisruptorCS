using System.Diagnostics;
using Disruptor.Game;

namespace ReplayInspector;

/// <summary>CLI 명령 구현.</summary>
public static class Commands
{
    /// <summary>체크섬과 시퀀스 단조성을 검증합니다.</summary>
    public static int Verify(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException(null, path);

        using var stream = File.OpenRead(path);
        var sw = Stopwatch.StartNew();
        var result = JournalReader.Verify(stream);
        sw.Stop();

        Console.WriteLine($"파일    : {info.FullName}");
        Console.WriteLine($"크기    : {info.Length:N0}B");
        Console.WriteLine($"결과    : {result}");
        Console.WriteLine($"검사시간: {sw.Elapsed.TotalMilliseconds:F0}ms " +
                          $"({info.Length / Math.Max(0.001, sw.Elapsed.TotalSeconds) / (1 << 20):F0} MB/s)");

        if (result.IsValid)
            return 0;

        Console.WriteLine();
        Console.WriteLine("이 저널은 마지막 유효 지점까지만 신뢰할 수 있습니다.");
        Console.WriteLine($"복구하려면: djrn replay {path} --truncate");
        return 1;
    }

    /// <summary>레코드를 사람이 읽을 수 있게 덤프합니다.</summary>
    public static int Dump(string path, Options options)
    {
        using var stream = File.OpenRead(path);

        Console.WriteLine($"{"SEQ",12}  {"TIMESTAMP(UTC)",-27} {"LEN",4}  PAYLOAD");
        Console.WriteLine(new string('-', 100));

        int shown = 0;
        long scanned = 0;

        foreach (var record in JournalReader.Read(stream, options.ReadOptions))
        {
            scanned++;

            if (record.Sequence < options.From)
                continue;

            if (record.Sequence > options.To)
                break;

            var payload = record.Payload.Span;
            string rendered = !options.Hex && MatchCodec.CanDecode(payload)
                ? MatchCodec.Describe(payload)
                : Hex(payload);

            var timestamp = new DateTime(record.TimestampTicks, DateTimeKind.Utc);

            Console.WriteLine($"{record.Sequence,12}  {timestamp:yyyy-MM-dd HH:mm:ss.fffffff} " +
                              $"{payload.Length,4}  {rendered}");

            if (++shown >= options.Limit)
            {
                Console.WriteLine($"... (--limit {options.Limit} 도달, 여기서 중단)");
                break;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"출력 {shown:N0}건 / 훑은 레코드 {scanned:N0}건");
        return 0;
    }

    /// <summary>
    /// 특정 시퀀스까지만 재생해 "그 순간의 상태"를 복원합니다.
    /// </summary>
    /// <remarks>
    /// 버그 리포트가 "시퀀스 812,443 에서 HP 가 음수였다"면, 그 직전까지 재생하고 멈춰
    /// 상태를 눈으로 확인할 수 있습니다. 서버를 다시 띄울 필요가 없습니다.
    /// </remarks>
    public static int Replay(string path, Options options)
    {
        using var stream = File.OpenRead(path);

        var state = new MatchState();
        var command = new MatchCommand();
        var sw = Stopwatch.StartNew();

        long applied = 0;
        long lastSequence = -1;
        int skippedForeign = 0;

        foreach (var record in JournalReader.Read(stream, options.ReadOptions))
        {
            if (record.Sequence > options.UntilSequence)
                break;

            if (!MatchCodec.CanDecode(record.Payload.Span))
            {
                skippedForeign++;
                continue;
            }

            MatchCodec.Instance.Deserialize(record.Payload.Span, command);
            state.Apply(command);

            applied++;
            lastSequence = record.Sequence;
        }

        sw.Stop();

        Console.WriteLine($"재생 범위 : 0 ~ {lastSequence}" +
                          (options.UntilSequence == long.MaxValue ? " (끝까지)" : $" (--until-seq {options.UntilSequence})"));
        Console.WriteLine($"적용      : {applied:N0}건 · {sw.Elapsed.TotalMilliseconds:F0}ms " +
                          $"({applied / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0}/s)");
        Console.WriteLine($"룸        : {state.Rooms.Count:N0}개");
        Console.WriteLine($"지문      : {state.Fingerprint():X16}");

        if (skippedForeign > 0)
            Console.WriteLine($"해석 불가 : {skippedForeign:N0}건 (다른 이벤트 형식)");

        Console.WriteLine();

        if (options.Room is { } roomId)
        {
            Console.WriteLine(state.Rooms.TryGetValue(roomId, out var room)
                ? $"룸 {roomId}: {room}"
                : $"룸 {roomId}: 이 범위에는 기록이 없습니다.");
        }
        else
        {
            Console.WriteLine("상위 10개 룸 (적용 건수 순):");
            foreach (var (id, room) in state.Rooms.OrderByDescending(r => r.Value.Applied)
                                                  .ThenBy(r => r.Key)
                                                  .Take(10))
            {
                Console.WriteLine($"  룸 {id,-6} {room}");
            }
        }

        return 0;
    }

    /// <summary>
    /// 두 저널이 갈라지는 첫 지점을 찾습니다.
    /// </summary>
    /// <remarks>
    /// "핫 스탠바이가 프라이머리와 달라졌다"를 조사할 때 쓰는 명령입니다.
    /// 저널이 곧 실행 순서이므로, 첫 불일치 시퀀스가 곧 버그가 들어온 지점입니다.
    /// </remarks>
    public static int Diff(string pathA, string pathB)
    {
        using var streamA = File.OpenRead(pathA);
        using var streamB = File.OpenRead(pathB);

        using var a = JournalReader.Read(streamA, JournalReadOptions.Default).GetEnumerator();
        using var b = JournalReader.Read(streamB, JournalReadOptions.Default).GetEnumerator();

        long index = 0;

        while (true)
        {
            bool hasA = a.MoveNext();
            bool hasB = b.MoveNext();

            if (!hasA && !hasB)
            {
                Console.WriteLine($"동일: 두 저널 모두 {index:N0}건이며 내용이 일치합니다.");
                return 0;
            }

            if (!hasA || !hasB)
            {
                Console.WriteLine($"길이 불일치: {(hasA ? Path.GetFileName(pathA) : Path.GetFileName(pathB))} " +
                                  $"쪽이 더 깁니다 (공통 {index:N0}건까지는 동일).");
                return 1;
            }

            if (a.Current.Sequence != b.Current.Sequence ||
                !a.Current.Payload.Span.SequenceEqual(b.Current.Payload.Span))
            {
                Console.WriteLine($"첫 불일치: {index:N0}번째 레코드");
                Console.WriteLine($"  A seq={a.Current.Sequence} {Render(a.Current.Payload.Span)}");
                Console.WriteLine($"  B seq={b.Current.Sequence} {Render(b.Current.Payload.Span)}");
                Console.WriteLine();
                Console.WriteLine($"이 시퀀스 직전 상태를 보려면: djrn replay {pathA} --until-seq {a.Current.Sequence - 1}");
                return 1;
            }

            index++;
        }

        static string Render(ReadOnlySpan<byte> payload)
            => MatchCodec.CanDecode(payload) ? MatchCodec.Describe(payload) : Hex(payload);
    }

    private static string Hex(ReadOnlySpan<byte> payload)
    {
        const int MaxBytes = 32;
        ReadOnlySpan<byte> head = payload[..Math.Min(MaxBytes, payload.Length)];
        string text = Convert.ToHexString(head);

        return payload.Length > MaxBytes ? text + $"... (+{payload.Length - MaxBytes}B)" : text;
    }
}
