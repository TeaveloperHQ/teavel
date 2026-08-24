using Teavel.Tools;

namespace Teavel.Setup;

/// <summary>
/// 컴퓨터 이름이 아직 공장 기본값(DESKTOP-A1B2C3D 같은 것)인지 본다.
///
/// 그대로 두면 학교에서 어느 기계인지 분간이 안 되고, Teams·원드라이브·자산 목록에도
/// 그 이름이 그대로 뜬다. 선생님이 스스로 바꾸는 일은 거의 없다.
///
/// 이름을 실제로 바꾸는 것은 <c>setup.rename_computer</c> 도구가 한다 —
/// 새 이름을 교사에게 물어봐야 하는데 세팅 항목은 되묻는 자리가 없기 때문이다.
/// </summary>
public sealed class ComputerNameTask : ISetupTask
{
    private readonly ToolRunner _runner;

    public ComputerNameTask(ToolRunner runner) => _runner = runner;

    public string Id => "windows.computername";
    public string Title => "컴퓨터 이름";
    public string Why => "학교에서 어느 컴퓨터인지 알아볼 수 있어야 합니다. 팀즈·원드라이브에도 이 이름이 뜹니다.";

    private static readonly Dictionary<string, object> NoArgs = new();

    public async Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return CheckResult.NotApplicable("Windows 에서만 확인할 수 있습니다.");

        var res = await _runner
            .InvokeAsync("Teavel.Setup", "Get-TeavelComputerName", NoArgs, 60, "컴퓨터 이름 확인", ct)
            .ConfigureAwait(false);

        if (!res.Ok) return CheckResult.Unknown("컴퓨터 이름을 확인하지 못했습니다.", res.Details.ToArray());

        // 판단은 여기서 한다 — 스크립트는 상태를 문장으로 알려 줄 뿐이다.
        if (res.Message.Contains("아직 정하지 않은 이름", StringComparison.Ordinal))
            return CheckResult.NeedsFix(res.Message, res.Details.ToArray());

        return CheckResult.Ok(res.Message, res.Details.ToArray());
    }

    public async Task<FixResult> FixAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return FixResult.NotSupported("Windows 에서만 할 수 있습니다.");

        var check = await CheckAsync(ct).ConfigureAwait(false);
        if (check.State != CheckState.NeedsFix) return FixResult.AlreadyOk(check.Summary);

        // 새 이름은 학교마다 규칙이 달라 우리가 지어 줄 수 없다. 화면을 열어 드리고 맡긴다.
        var res = await _runner
            .InvokeAsync("Teavel.Setup", "Open-TeavelComputerNameSetting", NoArgs, 60, "이름 바꾸기 화면", ct)
            .ConfigureAwait(false);

        if (!res.Ok) return FixResult.Failed(res.Message, res.Details.ToArray());

        return FixResult.Manual(res.Message, res.Details.ToArray());
    }
}
