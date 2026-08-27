using System.Buffers.Binary;
using System.Diagnostics;
using Disruptor.Game;
using ReplayInspector;

// ─────────────────────────────────────────────────────────────────────────────
// 이 샘플이 답하는 질문:
//   "저널이 있다고 하는데, 그걸로 실제로 무엇을 할 수 있는가?"
//
// 답: 운영 도구가 됩니다. 이 CLI 는 저널을 검증하고, 들여다보고, 특정 시퀀스까지만
// 재생해 "그 순간의 상태"를 복원합니다. 버그 리포트에 저널 파일 하나만 첨부되면
// 서버를 그 시점까지 되감아 재현할 수 있습니다.
//
//   djrn demo                          데모 저널 생성 후 전 기능 시연
//   djrn verify <file>                 체크섬·시퀀스 단조성 검증
//   djrn dump   <file> [옵션]          레코드 덤프
//   djrn replay <file> --until-seq N   N 번 시퀀스까지 재생하고 상태 출력
//   djrn diff   <fileA> <fileB>        두 저널이 갈라지는 첫 지점을 찾는다
// ─────────────────────────────────────────────────────────────────────────────

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return args.Length == 0 ? Demo.Run() : 0;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "demo" => Demo.Run(),
        "verify" => Commands.Verify(Arg(1, "file")),
        "dump" => Commands.Dump(Arg(1, "file"), Options.Parse(args)),
        "replay" => Commands.Replay(Arg(1, "file"), Options.Parse(args)),
        "diff" => Commands.Diff(Arg(1, "fileA"), Arg(2, "fileB")),
        _ => Fail($"알 수 없는 명령: {args[0]}"),
    };
}
catch (FileNotFoundException ex)
{
    return Fail($"파일을 찾을 수 없습니다: {ex.FileName}");
}
catch (InvalidDataException ex)
{
    // ThrowAtCorruption 모드에서 손상을 만난 경우 — 도구로서는 정상적인 결과다.
    return Fail($"저널이 손상되었습니다: {ex.Message}");
}
catch (ArgumentException ex)
{
    return Fail(ex.Message);
}

string Arg(int index, string name)
    => index < args.Length && !args[index].StartsWith('-')
        ? args[index]
        : throw new ArgumentException($"인자 <{name}> 가 필요합니다.");

static int Fail(string message)
{
    Console.Error.WriteLine($"오류: {message}");
    Console.Error.WriteLine("사용법은 `djrn --help` 를 보세요.");
    return 2;
}

static void PrintUsage()
{
    Console.WriteLine("""
        djrn — Disruptor 저널 검사 도구

          djrn demo                              데모 저널을 만들어 전 기능을 시연합니다
          djrn verify <file>                     체크섬·시퀀스 단조성을 검증합니다
          djrn dump   <file> [옵션]              레코드를 덤프합니다
          djrn replay <file> --until-seq <N>     N 번 시퀀스까지 재생하고 상태를 출력합니다
          djrn diff   <fileA> <fileB>            두 저널이 갈라지는 첫 지점을 찾습니다

        옵션
          --from <N>        이 시퀀스부터 (기본 0)
          --to <N>          이 시퀀스까지 (기본 끝까지)
          --limit <N>       최대 출력 건수 (기본 20)
          --until-seq <N>   replay 종료 시퀀스
          --room <N>        replay 결과에서 이 룸만 출력
          --hex             페이로드를 해석하지 않고 16진수로 출력
          --truncate        손상 지점에서 예외 대신 조용히 종료 (크래시 복구 모드)

        """);
}
