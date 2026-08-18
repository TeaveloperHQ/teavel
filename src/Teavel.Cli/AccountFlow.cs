using Teavel.Platform;
using Teavel.Setup;
using Teavel.Tools;

namespace Teavel.Cli;

/// <summary>연결해야 할 것 하나의 지금 상태.</summary>
/// <param name="Title">화면에 보여 줄 이름.</param>
/// <param name="Connected">학교 계정으로 이어져 있는지.</param>
/// <param name="Note">한 줄 설명 — 이어져 있으면 계정, 아니면 까닭.</param>
/// <param name="Applicable">이 컴퓨터에 해당하는지(안 깔린 앱은 false).</param>
internal sealed record AccountState(string Title, bool Connected, string Note, bool Applicable = true);

/// <summary>
/// 학교 계정을 이 컴퓨터의 Microsoft 앱들에 잇는다. <b>세팅의 첫 단추다.</b>
///
/// <para>
/// 학교에서 컴퓨터를 처음 켜면 할 일이 열 가지쯤 보이지만, 실제로는 <b>계정 하나를 잇는 것</b>이
/// 대부분을 해결한다. Edge·원드라이브·오피스·아웃룩·팀즈가 저마다 로그인을 요구하는 것처럼
/// 보여도, Windows 에 학교 계정이 한 번 붙으면 나머지는 그것을 물어다 쓴다.
/// </para>
/// <para>
/// 그래서 순서가 중요하다. 앱마다 따로 로그인시키면 같은 비밀번호를 다섯 번 넣게 되고,
/// 그러다 한 번 틀리면 무엇이 되고 무엇이 안 됐는지 알 수 없게 된다.
/// <b>Windows 를 먼저, 그다음 안 따라온 것만</b> 손본다.
/// </para>
/// <para>
/// 원드라이브는 따로 시간을 들인다. 로그인은 쉬운데 <b>"그래서 내 파일이 어디 있는 건데"</b>
/// 가 어렵고, 그것을 모르면 중요한 자료를 어디 둬야 할지 판단할 수 없기 때문이다.
/// </para>
/// </summary>
public sealed class AccountFlow
{
    private readonly WindowsFacts _facts;
    private readonly EdgeFacts _edge;
    private readonly OneDriveDetail _oneDrive;
    private readonly ToolRunner _tools;
    private readonly IProcessRunner _proc;
    private readonly IRegistry _reg;

    private readonly bool _assumeYes;

    /// <summary>학교 테넌트 id. 계정이 붙은 뒤에야 알 수 있고, 백업 폴더를 켤 때 쓴다.</summary>
    private string? _tenantId;

    public AccountFlow(
        WindowsFacts facts, EdgeFacts edge, OneDriveDetail oneDrive,
        ToolRunner tools, IProcessRunner proc, IRegistry registry, bool assumeYes)
    {
        _facts = facts;
        _edge = edge;
        _oneDrive = oneDrive;
        _tools = tools;
        _proc = proc;
        _reg = registry;
        _assumeYes = assumeYes;
    }

    // ─────────────────────────────── 한 판 ───────────────────────────────

    public async Task<int> RunAsync(CancellationToken ct)
    {
        Brand.PrintMark("학교 계정 연결");

        if (!OperatingSystem.IsWindows())
        {
            Ui.Error("Windows 에서만 할 수 있습니다.");
            return 2;
        }

        Ui.Plain("""

              학교에서 받은 계정 하나를 이 컴퓨터에 이어 둡니다.
              그러면 아래 것들이 따로 로그인하지 않아도 열립니다.

                Edge        나이스·업무포털이 로그인 없이 열립니다
                원드라이브   만든 자료가 자동으로 백업됩니다
                오피스      정품 인증이 되고, 저장이 원드라이브로 이어집니다
                아웃룩      학교 메일을 받습니다
                팀즈        학교 팀에 들어갑니다
        """);

        var windowsOk = await ShowStatusAsync(ct).ConfigureAwait(false);

        // ① Windows — 여기가 뿌리다.
        //
        // 여기서 그만두셔도 나머지는 이어서 보여 드린다. Windows 계정은 나중에 하고
        // 원드라이브만 먼저 보고 싶으실 수도 있는데, 그때 흐름이 끊기면 그것을 볼 길이 없다.
        if (!windowsOk && await ConnectWindowsAsync(ct).ConfigureAwait(false))
            windowsOk = true;   // 지켜보다 붙은 것을 확인하고 왔다

        // ①-b 계정이 붙었으면, 원드라이브는 손대지 않고도 붙게 할 수 있다.
        OfferOneDriveAutoSignIn(windowsOk);

        // ② 안 따라온 앱만.
        ConnectApps(windowsOk);

        // ③ 원드라이브는 따로.
        ExplainOneDrive();

        Console.WriteLine();
        Ui.Info("'점검' 을 실행하면 지금 상태를 다시 확인할 수 있습니다.");
        return 0;
    }

    // ─────────────────────────────── 지금 상태 ───────────────────────────────

    /// <summary>지금 무엇이 이어져 있는지 한눈에. Windows 계정이 이어져 있으면 true.</summary>
    private async Task<bool> ShowStatusAsync(CancellationToken ct)
    {
        Ui.Title("지금 상태");

        var windows = await IsWindowsConnectedAsync(ct).ConfigureAwait(false);
        var states = new List<AccountState> { new("Windows 계정", windows, windows ? "연결돼 있습니다" : "아직 연결 안 됐습니다") };
        states.AddRange(AppStates());

        foreach (var s in states)
        {
            // 한글은 화면에서 두 칸이라 글자 수로 맞추면 표가 어긋난다(Ui.Pad 가 칸으로 센다).
            var title = Ui.Pad(s.Title, 16);

            if (!s.Applicable) { Ui.Dim($"  - {title} {s.Note}"); continue; }
            if (s.Connected) Ui.Ok($"{title} {s.Note}");
            else Ui.Warn($"{title} {s.Note}");
        }

        return windows;
    }

    /// <summary>앱마다의 지금 상태.</summary>
    private IReadOnlyList<AccountState> AppStates()
    {
        var states = new List<AccountState>();

        // Edge
        if (!_edge.Installed)
        {
            states.Add(new("Edge", false, "이 컴퓨터에 없습니다", Applicable: false));
        }
        else if (_edge.SchoolProfile() is { } p)
        {
            states.Add(new("Edge", true, p.Email ?? p.Display));
        }
        else
        {
            var personal = _edge.Profiles().FirstOrDefault(x => x.Email is not null);
            states.Add(new("Edge", false, personal?.Email is { } e
                ? $"개인 계정입니다 ({e})"
                : "로그인돼 있지 않습니다"));
        }

        // 원드라이브
        if (_oneDrive.IsSchoolAccount)
            states.Add(new("원드라이브", true, _oneDrive.Account ?? "연결돼 있습니다"));
        else if (_oneDrive.Folder is not null)
            states.Add(new("원드라이브", false, "개인 계정으로 로그인돼 있습니다"));
        else
            states.Add(new("원드라이브", false, "로그인돼 있지 않습니다"));

        // 오피스
        var officeInstalled = _facts.OfficeProducts.Count > 0 || _facts.HasComProgId("Word.Application");
        states.Add(officeInstalled
            ? new("오피스", _facts.OfficeIdentities.Count > 0,
                  _facts.OfficeIdentities.Count > 0 ? "로그인돼 있습니다" : "로그인돼 있지 않습니다")
            : new("오피스", false, "이 컴퓨터에 없습니다", Applicable: false));

        // 아웃룩
        states.Add(_facts.HasComProgId("Outlook.Application")
            ? new("아웃룩", _facts.HasOutlookProfile,
                  _facts.HasOutlookProfile ? "메일 계정이 있습니다" : "메일 계정이 없습니다")
            : new("아웃룩", false, "이 컴퓨터에 없습니다", Applicable: false));

        // 팀즈 — 깔려 있는지까지만 본다. 로그인 여부는 밖에서 알 길이 없다.
        var teams = _facts.HasStoreApp("MSTeams") || _facts.HasClassicTeams;
        states.Add(teams
            ? new("팀즈", true, "깔려 있습니다 (로그인은 직접 확인해 주세요)")
            : new("팀즈", false, "이 컴퓨터에 없습니다", Applicable: false));

        return states;
    }

    /// <summary>Windows 에 학교 계정이 붙어 있는지 — PowerShell 이 dsregcmd 로 본다.</summary>
    private async Task<bool> IsWindowsConnectedAsync(CancellationToken ct)
    {
        var res = await _tools.InvokeAsync(
            "Teavel.Setup", "Get-TeavelAccountGuide",
            new Dictionary<string, object> { ["Ownership"] = "unknown" },
            60, "계정 상태 확인", ct).ConfigureAwait(false);

        return res.Ok && res.Message.Contains("이미 연결", StringComparison.Ordinal);
    }

    // ─────────────────────────────── ① Windows ───────────────────────────────

    /// <summary>학교 계정을 Windows 에 잇도록 안내한다. 이어서 진행하면 true.</summary>
    private async Task<bool> ConnectWindowsAsync(CancellationToken ct)
    {
        Ui.Title("① Windows 에 학교 계정 잇기");

        Ui.Plain("""
              이것 하나가 나머지의 뿌리입니다.
              여기에 한 번 이어 두면 Edge·원드라이브·오피스가 이 계정을 물어다 씁니다.
        """);
        Console.WriteLine();

        // 이 컴퓨터가 학교 것인지 개인 것인지에 따라 안내가 완전히 달라진다.
        var ownership = Ui.Choose("이 컴퓨터는", new[]
        {
            new Ui.Choice("1", "[1] 학교에서 준 컴퓨터입니다", "학교", "지급", "관용", "업무"),
            new Ui.Choice("2", "[2] 제 개인 컴퓨터입니다", "개인", "제것", "내것", "집"),
            new Ui.Choice("3", "[3] 잘 모르겠습니다", "모르", "글쎄", "몰라"),
        }, "3");

        var value = ownership switch { "1" => "school", "2" => "personal", _ => "unknown" };

        var guide = await _tools.InvokeAsync(
            "Teavel.Setup", "Get-TeavelAccountGuide",
            new Dictionary<string, object> { ["Ownership"] = value, ["Account"] = "school" },
            60, "계정 안내", ct).ConfigureAwait(false);

        Console.WriteLine();
        if (!guide.Ok)
        {
            Ui.Error("안내를 준비하지 못했습니다.");
            Ui.Details(guide.Details);
            return false;
        }

        Ui.Ok(guide.Message);
        Ui.Details(guide.Details);

        Console.WriteLine();
        if (!_assumeYes && !Ui.Confirm("      지금 설정 화면을 열까요?"))
        {
            Ui.Info("나중에 하시려면 '계정' 이라고 다시 치시면 됩니다.");
            return false;
        }

        await _tools.InvokeAsync("Teavel.Setup", "Open-TeavelAccountSetting",
            new Dictionary<string, object>(), 60, "설정 화면 열기", ct).ConfigureAwait(false);

        Console.WriteLine();
        Ui.Dim("      설정 화면을 띄웠습니다. 학교 메일 주소와 비밀번호를 넣어 주세요.");

        return await WatchForAccountAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 계정이 붙을 때까지 <b>지켜본다.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 예전에는 "다 하셨으면 Enter 를 눌러 주세요" 였다. 그러면 선생님이 창 두 개를 오가며
    /// <b>언제 Enter 를 눌러야 하는지를 스스로 판단</b>해야 한다. 설정 창에서 로그인이
    /// 끝났는지 아닌지가 늘 분명한 것도 아니다 — 화면이 조용히 닫히기도 한다.
    /// </para>
    /// <para>
    /// 비밀번호를 대신 넣어 드릴 수는 없다. 그건 대신할 수 있으면 안 되는 일이다.
    /// 하지만 <b>끝났는지 지켜보는 것</b>은 우리가 할 수 있고, 그것만으로 선생님이 할 일이
    /// '비밀번호 넣기' 하나로 줄어든다.
    /// </para>
    /// </remarks>
    /// <returns>붙었으면 true. 그만두셨거나 오래 걸리면 false.</returns>
    private async Task<bool> WatchForAccountAsync(CancellationToken ct)
    {
        var limit = TimeSpan.FromMinutes(10);
        var started = DateTime.UtcNow;

        Console.WriteLine();
        Ui.Dim("      다 되면 알아서 넘어갑니다 — 이 창은 그냥 두셔도 됩니다.");
        Ui.Dim("      (기다리지 않으시려면 Enter)");
        Console.WriteLine();

        while (DateTime.UtcNow - started < limit)
        {
            if (ct.IsCancellationRequested) return false;

            var state = await AccountStateAsync(ct).ConfigureAwait(false);
            if (state.Connected)
            {
                Console.Write('\r');
                Ui.Ok($"학교 계정이 연결됐습니다.{(state.Account is null ? "" : $"  {state.Account}")}");
                _tenantId = state.Tenant;
                return true;
            }

            // 5초를 잘게 쪼개 기다린다 — 그 사이에 Enter 를 누르시면 바로 넘어간다.
            for (var tick = 0; tick < 10; tick++)
            {
                if (SkipRequested())
                {
                    Console.Write('\r');

                    // 마침 방금 붙었을 수도 있다. 넘어가기 전에 한 번만 더 본다 —
                    // 안 그러면 다 해 놓고도 '아직 안 됐다' 는 화면을 보시게 된다.
                    var last = await AccountStateAsync(ct).ConfigureAwait(false);
                    if (last.Connected)
                    {
                        Ui.Ok($"학교 계정이 연결됐습니다.{(last.Account is null ? "" : $"  {last.Account}")}");
                        _tenantId = last.Tenant;
                        return true;
                    }

                    Ui.Info("기다리지 않고 넘어갑니다.");
                    return false;
                }

                var secs = (int)(DateTime.UtcNow - started).TotalSeconds;
                Console.Write($"\r      기다리는 중… {secs / 60}분 {secs % 60,2}초   ");

                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        Console.Write('\r');
        Ui.Warn("아직 연결되지 않았습니다. 나중에 '계정' 을 다시 실행하시면 이어서 합니다.");
        return false;
    }

    /// <summary>Enter 를 누르셨는지. 입력이 콘솔이 아니면 언제나 false.</summary>
    private static bool SkipRequested()
    {
        try
        {
            if (Console.IsInputRedirected || !Console.KeyAvailable) return false;
            return Console.ReadKey(intercept: true).Key == ConsoleKey.Enter;
        }
        catch (InvalidOperationException) { return false; }   // 콘솔이 아닌 자리
    }

    /// <summary>지금 계정 상태 — 지켜보기용이라 안내문을 만들지 않는 가벼운 쪽을 부른다.</summary>
    private async Task<(bool Connected, string? Tenant, string? Account)> AccountStateAsync(CancellationToken ct)
    {
        var res = await _tools.InvokeAsync(
            "Teavel.Setup", "Get-TeavelAccountState", new Dictionary<string, object>(),
            60, "계정 상태", ct).ConfigureAwait(false);

        if (!res.Ok) return (false, null, null);

        var connected = res.Details.Any(d => d.Equals("connected=True", StringComparison.OrdinalIgnoreCase));
        var tenant = Value(res.Details, "tenant=");
        var account = Value(res.Details, "account=");

        return (connected, tenant, account);

        static string? Value(IEnumerable<string> lines, string prefix)
            => lines.FirstOrDefault(d => d.StartsWith(prefix, StringComparison.Ordinal)) is { } hit
               && hit.Length > prefix.Length
                ? hit[prefix.Length..]
                : null;
    }

    // ────────────────────── ①-b 원드라이브를 스스로 붙게 ──────────────────────

    /// <summary>정책을 두는 자리. 컴퓨터 전체에 적용되므로 관리자 권한이 필요하다.</summary>
    private const string OneDrivePolicyKey = @"SOFTWARE\Policies\Microsoft\OneDrive";

    /// <summary>
    /// 원드라이브가 <b>스스로</b> 학교 계정으로 로그인하게 해 둔다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 여기가 우리가 정말로 '대신 해 줄 수 있는' 자리다. Windows 에 학교 계정이 붙어 있으면,
    /// 이 설정 하나로 원드라이브가 그 계정을 가져다 알아서 로그인한다 —
    /// <b>선생님이 아무것도 누르지 않는다.</b> 백업 폴더(바탕 화면·문서·사진)도 같이 켜진다.
    /// </para>
    /// <para>
    /// <b>묻지 않고 켜지 않는다.</b> 컴퓨터 전체에 걸리는 정책이고, 켜는 순간 바탕 화면과
    /// 문서 폴더가 원드라이브 안으로 옮겨 간다. 되돌릴 수 있는 일이지만 놀랄 만한 일이고,
    /// 남의 컴퓨터에서 말없이 할 일은 아니다.
    /// </para>
    /// </remarks>
    /// <param name="windowsConnected">
    /// Windows 에 학교 계정이 붙어 있는지. 안 붙어 있으면 켤 것을 권하지 않는다 —
    /// 가져다 쓸 계정이 없으니 켜 봐야 아무 일도 일어나지 않는다.
    /// 다만 <b>이미 켜 둔 것을 끄는 길</b>은 그때도 열어 둔다.
    /// </param>
    private void OfferOneDriveAutoSignIn(bool windowsConnected)
    {
        if (_facts.OneDriveExe is null) return;         // 원드라이브가 없는 컴퓨터
        if (!windowsConnected && !AutoSignInOn()) return;

        // 이미 켜 둔 컴퓨터에서는 끌 수 있게 한다. 켜 주기만 하고 끄는 길을 안 만들면
        // 되돌릴 수 없는 설정을 남의 컴퓨터에 심는 셈이 된다.
        if (AutoSignInOn())
        {
            Ui.Title("①-b 원드라이브 자동 로그인");
            Ui.Ok("켜져 있습니다 — 원드라이브가 학교 계정으로 알아서 로그인합니다.");

            if (_assumeYes || !Ui.Confirm("      끌까요?", defaultYes: false)) return;

            if (!IsAdministrator())
            {
                Ui.Warn("끄는 것도 관리자 권한이 필요합니다.");
                Ui.Dim("      PowerShell 을 [관리자 권한으로 실행] 한 뒤 다시 해 주세요.");
                return;
            }

            _reg.WriteDword(RegistryRoot.LocalMachine, OneDrivePolicyKey, "SilentAccountConfig", 0);
            _reg.WriteDword(RegistryRoot.LocalMachine, OneDrivePolicyKey, "KFMSilentOptInWithNotification", 0);
            Ui.Ok("껐습니다. 이미 로그인된 것과 옮겨진 폴더는 그대로 둡니다.");
            Ui.Dim("      원드라이브 설정에서 직접 로그아웃하거나 백업을 끄실 수 있습니다.");
            return;
        }

        if (_oneDrive.IsSchoolAccount) return;          // 이미 손으로 붙여 두셨다

        Ui.Title("①-b 원드라이브를 스스로 붙게 하기");

        Ui.Plain("""
              원드라이브는 창을 띄우고 클릭을 시키는 대신, <b>알아서 로그인하게</b> 해 둘 수 있습니다.
              Windows 에 이어 둔 학교 계정을 그대로 가져다 씁니다 — 누르실 것이 없습니다.

              함께 켜지는 것:
                · 바탕 화면 · 문서 · 사진 이 자동으로 백업됩니다
                  (그 폴더들이 원드라이브 안으로 옮겨 갑니다. 파일은 그대로 있습니다)
        """.Replace("<b>", "").Replace("</b>", ""));

        Console.WriteLine();

        if (!IsAdministrator())
        {
            Ui.Warn("이건 컴퓨터 전체 설정이라 관리자 권한이 필요합니다.");
            Ui.Dim("      PowerShell 을 [관리자 권한으로 실행] 한 뒤 '계정' 을 다시 실행해 주세요.");
            Ui.Dim("      지금은 넘어가고, 원드라이브는 아래에서 손으로 로그인하셔도 됩니다.");
            return;
        }

        if (!_assumeYes && !Ui.Confirm("      켤까요?", defaultYes: false))
        {
            Ui.Info("그냥 두겠습니다.");
            return;
        }

        // SilentAccountConfig — Windows 에 붙어 있는 학교 계정으로 알아서 로그인한다.
        if (!_reg.WriteDword(RegistryRoot.LocalMachine, OneDrivePolicyKey, "SilentAccountConfig", 1))
        {
            Ui.Error("설정을 쓰지 못했습니다. 관리자 권한으로 다시 실행해 주세요.");
            return;
        }
        Ui.Ok("원드라이브가 학교 계정으로 알아서 로그인합니다.");

        // 백업 폴더 — 어느 학교인지(테넌트) 알아야 켤 수 있다.
        if (_tenantId is { Length: > 0 })
        {
            // 알림과 함께 켠다. 말없이 바탕 화면이 옮겨 가면 놀라시기 때문이다.
            _reg.WriteString(RegistryRoot.LocalMachine, OneDrivePolicyKey, "KFMSilentOptIn", _tenantId);
            _reg.WriteDword(RegistryRoot.LocalMachine, OneDrivePolicyKey, "KFMSilentOptInWithNotification", 1);
            Ui.Ok("바탕 화면 · 문서 · 사진 백업도 켰습니다.");
        }
        else
        {
            Ui.Dim("      백업 폴더는 학교 테넌트를 알아야 켤 수 있어 건너뛰었습니다.");
            Ui.Dim("      계정을 이으신 뒤 '계정' 을 다시 실행하시면 그때 켜 드립니다.");
        }

        // 정책은 원드라이브가 시작할 때 읽는다. 지금 돌고 있으면 다시 띄워야 한다.
        Restart(_facts.OneDriveExe);

        Console.WriteLine();
        Ui.Dim("      원드라이브를 다시 띄웠습니다. 몇 분 안에 스스로 로그인합니다.");
        Ui.Dim("      끄시려면 '계정' 을 다시 실행하시면 됩니다 — 그때 끌지 여쭤봅니다.");
    }

    /// <summary>자동 로그인이 켜져 있는지.</summary>
    private bool AutoSignInOn()
        => _reg.ReadDword(RegistryRoot.LocalMachine, OneDrivePolicyKey, "SilentAccountConfig") == 1;

    /// <summary>관리자 권한으로 돌고 있는지.</summary>
    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>돌고 있으면 끄고 다시 띄운다.</summary>
    private void Restart(string exe)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(exe);
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }

        _proc.Launch(exe, new[] { "/background" });
    }

    // ─────────────────────────────── ② 앱들 ───────────────────────────────

    /// <summary>Windows 계정을 안 따라온 앱만 하나씩 손본다.</summary>
    private void ConnectApps(bool windowsConnected)
    {
        var pending = AppStates().Where(s => s.Applicable && !s.Connected).ToList();

        Ui.Title("② 앱마다 확인");

        if (pending.Count == 0)
        {
            Ui.Ok("앱들이 모두 학교 계정을 물어다 쓰고 있습니다. 더 할 일이 없습니다.");
            return;
        }

        if (windowsConnected)
        {
            Ui.Dim("      Windows 에는 이었는데 아직 안 따라온 것들입니다.");
            Ui.Dim("      대개 앱을 한 번 열어 주면 알아서 이어집니다.");
        }
        else
        {
            Ui.Warn("Windows 계정이 아직 안 이어져 있어, 앱마다 따로 로그인하셔야 합니다.");
        }
        Console.WriteLine();

        foreach (var s in pending)
        {
            switch (s.Title)
            {
                case "Edge": OpenEdge(); break;
                case "원드라이브": OpenOneDriveSignIn(); break;
                case "오피스": OpenOffice(); break;
                case "아웃룩": OpenOutlook(); break;
            }
        }
    }

    private void OpenEdge()
    {
        Ui.Plain("      Edge");
        Ui.Dim("        나이스·업무포털이 로그인 없이 열리게 됩니다.");

        if (!_assumeYes && !Ui.Confirm("        지금 열까요?")) return;

        // 프로필 화면으로 바로 보낸다 — 여기서 [로그인] 이 눈에 보인다.
        var exe = _edge.ExePath;
        if (exe is null || !_proc.Launch(exe, new[] { "edge://settings/profiles" }))
        {
            Ui.Warn("        Edge 를 열지 못했습니다. 직접 열어 주세요.");
            return;
        }

        Ui.Details(new[]
        {
            "① 오른쪽 위 사람 모양 → [로그인]",
            "② 학교 메일 주소 선택 (Windows 에 이어 두셨으면 목록에 보입니다)",
            "③ '동기화' 를 켜면 즐겨찾기·비밀번호가 다른 컴퓨터에서도 쓰입니다",
        });
        Wait("        다 하셨으면 Enter");
    }

    private void OpenOneDriveSignIn()
    {
        Ui.Plain("      원드라이브");
        Ui.Dim("        만드신 자료가 자동으로 백업됩니다. 컴퓨터가 바뀌어도 그대로 남습니다.");

        if (!_assumeYes && !Ui.Confirm("        지금 열까요?")) return;

        var exe = _facts.OneDriveExe;
        if (exe is null || !_proc.Launch(exe))
        {
            Ui.Warn("        원드라이브를 찾지 못했습니다. Microsoft Store 에서 받으실 수 있습니다.");
            return;
        }

        Ui.Details(new[]
        {
            "① 학교 메일 주소를 넣고 [로그인]",
            "② 폴더 위치는 그대로 두고 [다음]",
            "③ 끝까지 [다음]",
        });
        Wait("        다 하셨으면 Enter");
    }

    private void OpenOffice()
    {
        Ui.Plain("      오피스");
        Ui.Dim("        로그인해야 정품으로 쓸 수 있고, 저장이 원드라이브로 이어집니다.");

        if (!_assumeYes && !Ui.Confirm("        워드를 열까요?")) return;

        if (!_proc.Launch("winword.exe"))
        {
            Ui.Warn("        워드를 열지 못했습니다. 시작 메뉴에서 직접 열어 주세요.");
        }

        Ui.Details(new[]
        {
            "① 오른쪽 위 [로그인]",
            "② 학교 메일 주소 선택",
            "",
            "워드에 로그인하면 엑셀·파워포인트도 함께 됩니다.",
        });
        Wait("        다 하셨으면 Enter");
    }

    private void OpenOutlook()
    {
        Ui.Plain("      아웃룩");
        Ui.Dim("        학교 메일을 받습니다.");

        if (!_assumeYes && !Ui.Confirm("        지금 열까요?")) return;

        if (!_proc.Launch("outlook.exe"))
        {
            Ui.Warn("        아웃룩을 열지 못했습니다. 시작 메뉴에서 직접 열어 주세요.");
        }

        Ui.Details(new[]
        {
            "① 학교 메일 주소가 이미 적혀 있으면 [연결]",
            "② 비밀번호 입력",
            "",
            "처음 한 번은 메일을 내려받느라 시간이 걸립니다.",
        });
        Wait("        다 하셨으면 Enter");
    }

    // ─────────────────────────────── ③ 원드라이브 ───────────────────────────────

    /// <summary>
    /// 원드라이브가 <b>어떻게 도는지</b> 보여 준다.
    /// </summary>
    /// <remarks>
    /// 로그인시키는 것으로 끝내면 안 되는 자리다. 선생님들이 막히는 지점은 로그인이 아니라
    /// "그래서 내 파일이 어디 있느냐" 이고, 그것을 모르면 <b>중요한 자료를 어디에 둘지</b>
    /// 판단할 수가 없다. 지금 무엇이 올라가고 있는지 눈으로 보여 주는 것이 설명보다 낫다.
    /// </remarks>
    private void ExplainOneDrive()
    {
        var folder = _oneDrive.Folder;
        if (folder is null)
        {
            Ui.Title("③ 원드라이브");
            Ui.Info("아직 로그인되지 않아 보여 드릴 것이 없습니다.");
            Ui.Dim("      로그인하신 뒤 '계정' 을 다시 실행하시면 여기서 자세히 알려 드립니다.");
            return;
        }

        Ui.Title("③ 원드라이브 — 무엇이 올라가고 있는지");

        Ui.Plain($"""
              이 폴더가 원드라이브 폴더입니다.

                {folder}

              여기 넣은 것은 자동으로 인터넷에도 저장됩니다. 컴퓨터가 고장 나도 남습니다.
              여기 '밖에' 있는 것은 백업되지 않습니다 — 그것 하나만 기억하시면 됩니다.
        """);

        // ── 바탕 화면·문서·사진 ──
        Console.WriteLine();
        Ui.Plain("      바탕 화면 · 문서 · 사진");

        var known = _oneDrive.KnownFolders();
        foreach (var (name, backed, path) in known)
        {
            if (backed) Ui.Ok($"{name} — 백업되고 있습니다");
            else Ui.Warn($"{name} — 백업 안 됨  ({path ?? "위치를 확인하지 못했습니다"})");
        }

        if (known.Any(k => !k.Backed))
        {
            Ui.Dim("      이 폴더들은 원드라이브 밖에 있어, 컴퓨터가 고장 나면 함께 사라집니다.");
            Ui.Dim("      아래에서 [백업 관리] 로 켜실 수 있습니다.");
        }

        // ── 안에 무엇이 있는지 ──
        var items = _oneDrive.Items();
        if (items.Count > 0)
        {
            Console.WriteLine();
            Ui.Plain("      원드라이브 폴더 안");
            foreach (var i in items.Take(12))
            {
                var kf = i.KnownFolder is null ? "" : $"  ← {i.KnownFolder}";
                var online = i.OnlineOnly > 0 ? $"  (온라인에만 {i.OnlineOnly:N0}개)" : "";
                Ui.Dim($"        {Ui.Pad(i.Name, 28)} {i.Size,8}{online}{kf}");
            }
            if (items.Count > 12) Ui.Dim($"        … 그 밖에 {items.Count - 12}개");

            if (items.Any(i => i.OnlineOnly > 0))
            {
                Console.WriteLine();
                Ui.Dim("      '온라인에만' 은 인터넷에는 있고 이 컴퓨터에는 안 내려와 있는 것입니다.");
                Ui.Dim("      자리를 차지하지 않고, 열면 그때 내려받습니다. 지워진 것이 아닙니다.");
            }
        }

        // ── 팀 문서고 ──
        var teams = _oneDrive.TeamLibraries();
        if (teams.Count > 0)
        {
            Console.WriteLine();
            Ui.Plain("      팀즈·부서 문서고 (원드라이브 폴더 '옆' 에 붙습니다)");
            foreach (var t in teams.Take(8)) Ui.Dim($"        {Ui.Pad(t.Name, 28)} {t.Size,8}");
            Ui.Dim("      팀즈에 올린 파일이 원드라이브에 안 보이는 것은 여기 있기 때문입니다.");
        }

        // ── 폴더 고르기 ──
        Console.WriteLine();
        Ui.Plain("      어떤 폴더를 이 컴퓨터에 둘지 고르기");
        Ui.Dim("      원드라이브가 직접 물어봐야 하는 것이라 대신 눌러 드릴 수는 없습니다.");
        Ui.Dim("      창을 띄워 드리고 어디를 누르면 되는지 짚어 드리겠습니다.");
        Console.WriteLine();

        if (!_assumeYes && !Ui.Confirm("      설정 창을 열까요?")) return;

        var exe = _facts.OneDriveExe;
        if (exe is null || !_proc.Launch(exe, new[] { "/settings" }))
        {
            Ui.Warn("      원드라이브 설정 창을 열지 못했습니다.");
            Ui.Dim("      작업 표시줄 오른쪽 아래 구름 모양 → 톱니바퀴 → [설정] 으로도 들어가실 수 있습니다.");
            return;
        }

        Ui.Details(new[]
        {
            "[동기화 및 백업] 탭에서 두 가지를 하실 수 있습니다.",
            "",
            "  · [백업 관리]        바탕 화면 · 문서 · 사진 을 켜면 그 폴더가 백업됩니다",
            "  · [고급 설정] →",
            "    [폴더 선택]        어떤 폴더를 이 컴퓨터에 내려둘지 고릅니다",
            "",
            "폴더 선택에서 체크를 풀어도 인터넷의 파일은 지워지지 않습니다.",
            "이 컴퓨터에서만 안 보이게 될 뿐이고, 언제든 다시 켜실 수 있습니다.",
        });

        Wait("      다 하셨으면 Enter");
    }

    // ─────────────────────────────── 도우미 ───────────────────────────────

    /// <summary>사람이 다른 창에서 일을 마칠 때까지 기다린다.</summary>
    private void Wait(string prompt)
    {
        if (_assumeYes) return;
        Ui.Ask($"{prompt}: ");
        Console.WriteLine();
    }
}
