using Disruptor.Game;

namespace ReplayInspector;

/// <summary>커맨드라인 옵션.</summary>
public sealed class Options
{
    public long From { get; private set; }

    public long To { get; private set; } = long.MaxValue;

    public int Limit { get; private set; } = 20;

    public long UntilSequence { get; private set; } = long.MaxValue;

    public long? Room { get; private set; }

    public bool Hex { get; private set; }

    public bool Truncate { get; private set; }

    public JournalReadOptions ReadOptions => new()
    {
        Mode = Truncate ? JournalReadMode.TruncateAtCorruption : JournalReadMode.ThrowAtCorruption,
    };

    public static Options Parse(string[] args)
    {
        var options = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from": options.From = Number(args, ref i); break;
                case "--to": options.To = Number(args, ref i); break;
                case "--limit": options.Limit = (int)Number(args, ref i); break;
                case "--until-seq": options.UntilSequence = Number(args, ref i); break;
                case "--room": options.Room = Number(args, ref i); break;
                case "--hex": options.Hex = true; break;
                case "--truncate": options.Truncate = true; break;

                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"알 수 없는 옵션: {args[i]}");
                    break;
            }
        }

        if (options.From > options.To)
            throw new ArgumentException("--from 이 --to 보다 큽니다.");

        return options;
    }

    private static long Number(string[] args, ref int index)
    {
        string flag = args[index];

        if (++index >= args.Length)
            throw new ArgumentException($"{flag} 에 값이 필요합니다.");

        return long.TryParse(args[index], out long value)
            ? value
            : throw new ArgumentException($"{flag} 의 값이 정수가 아닙니다: {args[index]}");
    }
}
