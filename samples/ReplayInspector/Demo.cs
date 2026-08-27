using Disruptor.Game;

namespace ReplayInspector;

/// <summary>
/// 데모 저널을 만들고 이 도구의 모든 기능을 한 번에 시연합니다.
/// </summary>
public static class Demo
{
    private const int Records = 50_000;

    public static int Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "djrn-demo-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);

        try
        {
            string good = Path.Combine(dir, "match.djrn");
            string corrupted = Path.Combine(dir, "match-corrupted.djrn");
            string truncated = Path.Combine(dir, "match-truncated.djrn");
            string divergent = Path.Combine(dir, "match-divergent.djrn");

            Write(good, divergeAtRecord: -1);
            Write(divergent, divergeAtRecord: 31_337);

            File.Copy(good, corrupted);
            File.Copy(good, truncated);
            FlipByteInTheMiddle(corrupted);
            CutTail(truncated, bytes: 12);

            Section("1) 정상 저널 검증", $"djrn verify {Short(good)}");
            Commands.Verify(good);

            Section("2) 레코드 들여다보기", $"djrn dump {Short(good)} --from 1000 --limit 5");
            Commands.Dump(good, Options.Parse(["dump", good, "--from", "1000", "--limit", "5"]));

            Section("3) 특정 시점까지만 되감기", $"djrn replay {Short(good)} --until-seq 9999 --room 42");
            Commands.Replay(good, Options.Parse(["replay", good, "--until-seq", "9999", "--room", "42"]));

            Section("4) 끝까지 재생 (같은 룸이 어떻게 달라지는지)", $"djrn replay {Short(good)} --room 42");
            Commands.Replay(good, Options.Parse(["replay", good, "--room", "42"]));

            Section("5) 내용이 손상된 저널", $"djrn verify {Short(corrupted)}");
            Commands.Verify(corrupted);

            Section("6) 손상 지점까지 복구", $"djrn replay {Short(corrupted)} --truncate --room 42");
            Commands.Replay(corrupted, Options.Parse(["replay", corrupted, "--truncate", "--room", "42"]));

            Section("7) 크래시로 마지막 레코드가 잘린 저널", $"djrn verify {Short(truncated)}");
            Console.WriteLine("잘린 꼬리는 손상이 아니라 정상적인 종료 지점입니다 — 리더가 그 앞에서 안전하게 멈춥니다.");
            Console.WriteLine();
            Commands.Verify(truncated);

            Section("8) 두 저널이 갈라지는 지점 찾기", $"djrn diff {Short(good)} {Short(divergent)}");
            Commands.Diff(good, divergent);

            Console.WriteLine();
            Console.WriteLine(new string('=', 78));
            Console.WriteLine("저널 하나만 있으면 서버를 다시 띄우지 않고도 임의 시점의 상태를 복원하고,");
            Console.WriteLine("두 실행이 어디서 갈라졌는지 정확한 시퀀스로 지목할 수 있습니다.");

            return 0;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>결정론적인 데모 저널을 만듭니다.</summary>
    /// <param name="divergeAtRecord">이 레코드에서만 값을 1 바꿉니다. -1 이면 바꾸지 않습니다.</param>
    private static void Write(string path, long divergeAtRecord)
    {
        using var writer = JournalWriter.CreateFile(path);

        var command = new MatchCommand();
        var buffer = new byte[MatchCodec.RecordSize];
        var rng = new Random(20260827);   // 시드 고정 — 두 파일이 이 지점 전까지 동일해야 한다

        for (long i = 0; i < Records; i++)
        {
            command.RoomId = rng.Next(64);
            command.PlayerId = command.RoomId * 8 + (i % 8);
            command.OpCode = (int)(i % 3);
            command.Value = rng.Next(1, 20);

            if (i == divergeAtRecord)
                command.Value += 1;   // 여기서 두 실행이 갈라진다

            MatchCodec.Instance.Serialize(command, buffer);
            writer.Write(i, buffer);
        }
    }

    /// <summary>파일 중간의 한 바이트를 뒤집습니다 — 체크섬이 잡아내야 합니다.</summary>
    private static void FlipByteInTheMiddle(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        fs.Seek(fs.Length / 2, SeekOrigin.Begin);

        int b = fs.ReadByte();
        fs.Seek(-1, SeekOrigin.Current);
        fs.WriteByte((byte)(b ^ 0x01));
    }

    /// <summary>파일 끝을 잘라냅니다 — 크래시로 마지막 쓰기가 미완인 상황.</summary>
    private static void CutTail(string path, int bytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
        fs.SetLength(fs.Length - bytes);
    }

    private static void Section(string title, string command)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"  {title}");
        Console.WriteLine($"  $ {command}");
        Console.WriteLine(new string('=', 78));
    }

    private static string Short(string path) => Path.GetFileName(path);
}
