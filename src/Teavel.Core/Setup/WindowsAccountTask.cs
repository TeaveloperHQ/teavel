using Teavel.Tools;

namespace Teavel.Setup;

/// <summary>
/// 학교 계정을 Windows 에 잇는다. <b>세팅의 뿌리</b> 다.
///
/// 교사 PC 는 대개 로컬 계정(`user`)으로만 쓰이고 학교 계정이 Windows 에 붙어 있지 않다.
/// 그러면 원드라이브·아웃룩·팀즈·To Do 가 저마다 로그인을 요구하고,
/// 선생님은 같은 비밀번호를 네 번 넣다가 포기한다.
///
/// 그런데 "넣는 방법" 이 하나가 아니다.
///   · 계정 추가(등록) — 앱만 이어짐. 개인 컴퓨터에 맞다. <b>Home 에서도 된다.</b>
///   · 장치 연결(조인) — Windows 로그인부터 바뀌고 학교가 이 PC 를 관리한다.
///                      학교 지급 컴퓨터에 맞다. <b>Home 에서는 불가능하다.</b>
/// 잘못 고르면 개인 컴퓨터를 학교가 관리하게 되거나, Home 에 없는 메뉴를 찾아 헤맨다.
/// 그래서 이 항목은 '고쳐 주는' 것보다 <b>상황을 설명하는</b> 일이 더 크다.
///
/// 판단과 안내문은 PowerShell(Teavel.Setup) 에 있다 — 레지스트리·dsregcmd·설정 화면 열기 모두
/// PowerShell 이 네이티브로 하는 일이라, C# 으로 옮겨 적으면 같은 것을 두 번 쓰는 셈이 된다.
/// </summary>
public sealed class WindowsAccountTask : ISetupTask
{
    private readonly ToolRunner _runner;

    public WindowsAccountTask(ToolRunner runner) => _runner = runner;

    public string Id => "windows.account";
    public string Title => "Windows 에 학교 계정 잇기";
    public string Why => "이것만 해 두면 원드라이브·아웃룩·팀즈·To Do 가 따로 로그인하지 않아도 됩니다.";

    private static readonly Dictionary<string, object> Unknown = new() { ["Ownership"] = "unknown" };

    public async Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return CheckResult.NotApplicable("Windows 에서만 확인할 수 있습니다.");

        var res = await _runner
            .InvokeAsync("Teavel.Setup", "Get-TeavelAccountGuide", Unknown, 60, "계정 상태 확인", ct)
            .ConfigureAwait(false);

        if (!res.Ok) return CheckResult.Unknown("계정 연결 상태를 확인하지 못했습니다.", res.Details.ToArray());

        // 점검 화면에서는 '지금 상태' 까지만 보여 준다. 긴 안내는 '고침' 때 나온다.
        var status = res.Details.TakeWhile(d => d.Length > 0).ToArray();

        return res.Message.Contains("이미 연결", StringComparison.Ordinal)
            ? CheckResult.Ok("학교 계정이 연결돼 있습니다.", status)
            : CheckResult.NeedsFix(res.Message, status);
    }

    public async Task<FixResult> FixAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return FixResult.NotSupported("Windows 에서만 할 수 있습니다.");

        var guide = await _runner
            .InvokeAsync("Teavel.Setup", "Get-TeavelAccountGuide", Unknown, 60, "계정 안내", ct)
            .ConfigureAwait(false);

        if (!guide.Ok)
            return FixResult.Failed("안내를 준비하지 못했습니다.", guide.Details.ToArray());

        if (guide.Message.Contains("이미 연결", StringComparison.Ordinal))
            return FixResult.AlreadyOk(guide.Message);

        // 비밀번호가 필요한 일이라 대신 해 줄 수 없다.
        // 대신 (1) 이 컴퓨터에서 무엇이 되고 안 되는지 설명하고, (2) 그 화면을 열어 준다.
        var opened = await _runner
            .InvokeAsync("Teavel.Setup", "Open-TeavelAccountSetting", new Dictionary<string, object>(),
                         60, "설정 화면 열기", ct)
            .ConfigureAwait(false);

        var steps = guide.Details.ToList();
        if (opened.Ok)
        {
            steps.Add("");
            steps.Add("설정 화면을 띄워 두었습니다.");
        }
        steps.Add("");
        steps.Add("이 컴퓨터가 학교 것인지 개인 것인지 알려 주시면 더 정확히 안내합니다 —");
        steps.Add("  \"학교에서 받은 컴퓨터야\"  또는  \"내 개인 컴퓨터야\"");

        return FixResult.Manual(guide.Message, steps.ToArray());
    }
}
