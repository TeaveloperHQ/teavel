using System.Text;
using Teavel.Cli;

// 한글이 깨지지 않도록. PowerShell 창의 기본 코드 페이지는 UTF-8 이 아닌 경우가 많다.
try
{
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.InputEncoding = new UTF8Encoding(false);
}
catch { /* 리디렉션된 콘솔에서는 설정할 수 없다 */ }

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;          // 곧바로 죽이지 않고, 하던 일을 정리할 틈을 준다
    cancel.Cancel();
};

var assumeYes = args.Any(a => a is "--yes" or "-y");

// --here <폴더> : 탐색기 우클릭에서 들어온 경우. 그 폴더에서 시작한다.
string? startFolder = null;
var hereIndex = Array.FindIndex(args, a => a == "--here");
if (hereIndex >= 0 && hereIndex + 1 < args.Length)
{
    var folder = args[hereIndex + 1];
    if (Directory.Exists(folder))
    {
        startFolder = Path.GetFullPath(folder);
        // 상대 경로로 말해도 그 폴더 기준이 되도록 작업 폴더까지 옮긴다.
        // (도구를 실행하는 PowerShell 도 이 작업 폴더를 물려받는다)
        try { Directory.SetCurrentDirectory(startFolder); } catch { }
    }
}

// --here 의 값(폴더 경로)은 명령어로 오해되면 안 된다.
// hereIndex 가 -1 일 때 hereIndex+1 은 0 이므로, --here 가 있을 때만 걸러야 한다.
var skipIndex = hereIndex >= 0 ? hereIndex + 1 : -1;
var positional = args
    .Where((a, i) => !a.StartsWith('-') && i != skipIndex)
    .ToArray();

var command = positional.FirstOrDefault()?.ToLowerInvariant();
var argument = positional.Skip(1).FirstOrDefault();

await using var session = new TeavelSession { AssumeYes = assumeYes, StartFolder = startFolder };

try
{
    return command switch
    {
        null                          => await session.RunInteractiveAsync(cancel.Token),
        "점검" or "check"             => await session.RunCheckAsync(cancel.Token),
        "고침" or "손보기" or "fix"   => await session.RunFixAsync(argument, cancel.Token),
        "목록" or "list"              => Run(session.RunList),
        "모델" or "model"             => await session.RunModelAsync(cancel.Token),
        "설치" or "등록" or "install" => session.RunRegister(),
        "제거" or "등록해제" or "uninstall" => session.RunUnregister(),
        "자가점검" or "selfcheck"     => await session.RunSelfCheckAsync(cancel.Token),
        "m365" or "그룹" or "teams"   => await session.RunM365Async(cancel.Token),
        "도움말" or "help" or "--help" => Run(PrintHelp),
        _                             => await session.HandleOneShotAsync(string.Join(' ', positional), cancel.Token),
    };
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Ui.Info("중단했습니다.");
    return 130;
}

static int Run(Action action) { action(); return 0; }

static void PrintHelp()
{
    Brand.PrintMark("교사를 위한 Windows 도우미");
    Ui.Plain("""

      teavel                하고 싶은 일을 말로 적으면 알아서 처리합니다
      teavel 점검           OneDrive·Office·Teams·teaveloper 앱 상태를 확인합니다
      teavel 고침 [항목]    확인된 문제를 손봅니다
      teavel 목록           할 수 있는 일을 보여줍니다
      teavel 모델           말을 알아듣는 언어 모델을 내려받습니다(한 번만)
      teavel 자가점검       Teavel 자신이 온전한지 확인합니다

      teavel m365           학교 그룹·Teams 를 살펴보고 정리하고 만듭니다
                            (학교 M365 전역 관리자 전용)

      teavel 설치           어디서나 teavel 로 실행되게 등록합니다
      teavel 제거           그 등록을 풉니다(프로그램은 지우지 않습니다)

      teavel "2반 엑셀 다 합쳐줘"     한 번만 실행하고 끝냅니다

    옵션
      -y, --yes             확인을 묻지 않고 진행합니다
    """);
}
