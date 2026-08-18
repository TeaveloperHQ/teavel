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
