using Teavel.Platform;
using Teavel.Tools;

namespace Teavel.Setup;

/// <summary>Outlook 에 학교 메일 계정이 붙어 있는지.</summary>
public sealed class OutlookAccountTask : ISetupTask
{
    private readonly WindowsFacts _facts;
    private readonly IProcessRunner _proc;

    public OutlookAccountTask(WindowsFacts facts, IProcessRunner proc)
    {
        _facts = facts;
        _proc = proc;
    }

    public string Id => "outlook.account";
    public string Title => "아웃룩 학교 메일 계정";
    public string Why => "학교 메일을 아웃룩에서 받아야 첨부 파일 정리나 단체 메일을 쓸 수 있습니다.";

    public Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(CheckResult.NotApplicable("Windows 에서만 확인할 수 있습니다."));

        if (!_facts.HasComProgId("Outlook.Application"))
            return Task.FromResult(CheckResult.NeedsFix(
                "아웃룩이 설치돼 있지 않습니다.",
                "'Office 설치' 를 먼저 끝내 주세요."));

        return Task.FromResult(_facts.HasOutlookProfile
            ? CheckResult.Ok("아웃룩에 메일 계정이 설정돼 있습니다.")
            : CheckResult.NeedsFix("아웃룩에 메일 계정이 없습니다."));
    }

    public Task<FixResult> FixAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(FixResult.NotSupported("Windows 에서만 할 수 있습니다."));

        if (_facts.HasOutlookProfile)
            return Task.FromResult(FixResult.AlreadyOk("이미 설정돼 있습니다."));

        if (!_proc.Launch("outlook.exe"))
            return Task.FromResult(FixResult.Failed("아웃룩을 실행하지 못했습니다."));

        // Windows 에 학교 계정이 붙어 있으면 아웃룩이 주소를 알아서 채워 준다.
        // 그 경우 교사가 할 일은 [연결] 을 누르는 것뿐이다.
        return Task.FromResult(FixResult.Manual(
            "아웃룩을 띄웠습니다. 처음 실행이면 계정 설정 창이 뜹니다.",
            "① 메일 주소가 이미 적혀 있으면 그대로 [연결]",
            "   (Windows 에 학교 계정이 연결돼 있으면 자동으로 채워집니다)",
            "② 비어 있으면 학교 메일 주소를 넣고 [연결]",
            "③ 비밀번호를 넣고 마칩니다",
            "",
            "'추가 계정을 설정하시겠습니까' 가 나오면 [완료] 를 누르세요."));
    }
}

/// <summary>Microsoft To Do 설치 — 학교 업무 알림·할 일이 여기로 온다.</summary>
public sealed class TodoInstalledTask : WingetInstallTask
{
    private readonly WindowsFacts _facts;

    public TodoInstalledTask(WindowsFacts facts, IProcessRunner proc) : base(proc) => _facts = facts;

    public override string Id => "todo.installed";
    public override string Title => "To Do 설치";
    public override string Why => "아웃룩에서 깃발 단 메일과 팀즈의 할 일이 To Do 에 모입니다.";
    protected override string PackageId => "Microsoft.Todos";

    public override Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(CheckResult.NotApplicable("Windows 에서만 확인할 수 있습니다."));

        return Task.FromResult(_facts.HasStoreApp("Microsoft.Todos")
            ? CheckResult.Ok("To Do 가 설치돼 있습니다.")
            : CheckResult.NeedsFix("To Do 가 설치돼 있지 않습니다."));
    }
}

/// <summary>
/// 기본 프린터가 정해져 있는지.
///
/// 인쇄가 안 된다는 문의의 상당수는 프린터가 없어서가 아니라
/// Windows 가 '마지막에 쓴 프린터' 를 기본으로 계속 바꿔 놓기 때문이다.
/// </summary>
public sealed class PrinterDefaultTask : ISetupTask
{
    private readonly ToolRunner _runner;

    public PrinterDefaultTask(ToolRunner runner) => _runner = runner;

    public string Id => "printer.default";
    public string Title => "기본 프린터";
    public string Why => "기본 프린터가 정해져 있어야 인쇄 단추 한 번으로 나갑니다.";

    private static readonly Dictionary<string, object> NoArgs = new();

    public async Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return CheckResult.NotApplicable("Windows 에서만 확인할 수 있습니다.");

        var res = await _runner
            .InvokeAsync("Teavel.Setup", "Get-PrinterStatus", NoArgs, 60, "프린터 확인", ct)
            .ConfigureAwait(false);

        if (!res.Ok) return CheckResult.Unknown("프린터 상태를 확인하지 못했습니다.", res.Details.ToArray());

        // 스크립트가 상태를 문장으로 알려 준다 — 판단은 여기서 한다.
        var needsFix = res.Message.Contains("정해져 있지 않습니다", StringComparison.Ordinal)
                    || res.Message.Contains("하나도 없습니다", StringComparison.Ordinal)
                    || res.Details.Any(d => d.Contains("마지막에 쓴 프린터", StringComparison.Ordinal));

        return needsFix
            ? CheckResult.NeedsFix(res.Message, res.Details.ToArray())
            : CheckResult.Ok(res.Message, res.Details.ToArray());
    }

    public async Task<FixResult> FixAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return FixResult.NotSupported("Windows 에서만 할 수 있습니다.");

        var status = await _runner
            .InvokeAsync("Teavel.Setup", "Get-PrinterStatus", NoArgs, 60, "프린터 확인", ct)
            .ConfigureAwait(false);

        if (!status.Ok)
            return FixResult.Failed("프린터 상태를 확인하지 못했습니다.", status.Details.ToArray());

        // 프린터가 하나뿐이면 물어볼 것이 없다 — 그냥 그것을 기본으로 정한다.
        var names = status.Details
            .Where(d => d.Contains('[') && (d.StartsWith("★", StringComparison.Ordinal) || d.StartsWith("  ", StringComparison.Ordinal)))
            .Select(ExtractName)
            .Where(n => n.Length > 0)
            .ToList();

        if (names.Count == 1)
        {
            var set = await _runner.InvokeAsync(
                "Teavel.Setup", "Set-TeavelDefaultPrinter",
                new Dictionary<string, object> { ["Name"] = names[0] }, 60, "기본 프린터 설정", ct)
                .ConfigureAwait(false);

            return set.Ok
                ? FixResult.Fixed(set.Message)
                : FixResult.Failed(set.Message, set.Details.ToArray());
        }

        if (names.Count == 0)
            return FixResult.Manual(
                "이 컴퓨터에 프린터가 없습니다.",
                "학교에서 쓰는 프린터 주소를 알아 오세요. 예: \\\\print-server\\3층복도",
                "그다음 이렇게 말씀하시면 됩니다: \"프린터 추가해줘\"");

        // 여러 대면 교사가 골라야 한다 — 도구로 안내한다.
        return FixResult.Manual(
            $"프린터가 {names.Count}대 있습니다. 어느 것을 기본으로 할지 정해 주세요.",
            new[] { "" }
                .Concat(names.Select(n => "  · " + n))
                .Concat(new[]
                {
                    "",
                    "이렇게 말씀하시면 됩니다:",
                    $"  \"기본 프린터를 {names[0]} 으로 해줘\"",
                })
                .ToArray());
    }

    /// <summary>"★ 기본  이름   [포트]" 형태의 줄에서 프린터 이름만 꺼낸다.</summary>
    private static string ExtractName(string line)
    {
        var bracket = line.LastIndexOf('[');
        if (bracket < 0) return "";

        var body = line[..bracket].Trim();
        if (body.StartsWith("★", StringComparison.Ordinal))
            body = body[1..].TrimStart();
        if (body.StartsWith("기본", StringComparison.Ordinal))
            body = body[2..].TrimStart();

        return body.Trim();
    }
}
