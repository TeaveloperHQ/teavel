using Teavel.Apps;
using Teavel.Platform;
using Teavel.Runner;

namespace Teavel.Setup;

/// <summary>
/// Teaveloper 러너를 <b>쓸 수 있는 상태</b>로 만든다 — 설치 · 활성화 · 자동 실행 · 실행까지.
///
/// 러너만 내려받아 두면 아무 일도 일어나지 않는다. 교사가 포기하는 지점이 셋 있는데
/// (① 설정 파일이 없어 첫 실행이 오류 상자만 띄우고 끝난다, ② 내려받은 config.json 을
/// exe 옆으로 옮겨야 한다, ③ 포트가 쓰이고 있으면 또 오류 상자 후 종료된다) 이 항목이
/// 그 셋을 없앤다.
///
/// 교사가 직접 하는 일은 <b>브라우저에서 승인 한 번</b>뿐이다. 그건 자동화할 수 없다 —
/// 계정이 자기 것임을 증명하는 일이라서, 다른 로그인 항목들과 같은 규칙을 따른다.
/// </summary>
public sealed class RunnerActivationTask : ISetupTask
{
    /// <summary>카탈로그(apps.json)에서의 러너 id.</summary>
    public const string RunnerAppId = "teaveloper-runner";

    /// <summary>러너를 켠 뒤 연결을 기다리는 시간.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(40);

    private readonly AppCatalog _catalog;
    private readonly AppInstaller _installer;
    private readonly IProcessRunner _proc;
    private readonly Action<string>? _announce;
    private readonly Func<DeviceFlowClient> _flowFactory;

    /// <param name="announce">
    /// 진행 상황을 화면에 흘려보낼 통로. 활성화는 교사가 코드를 보고 승인할 때까지
    /// 기다려야 하므로, 결과만 돌려주는 <see cref="FixResult"/> 로는 부족하다.
    /// </param>
    /// <param name="flowFactory">테스트에서 가짜 포털로 바꿔 끼우기 위한 자리.</param>
    public RunnerActivationTask(
        AppCatalog catalog,
        AppInstaller installer,
        IProcessRunner proc,
        Action<string>? announce = null,
        Func<DeviceFlowClient>? flowFactory = null)
    {
        _catalog = catalog;
        _installer = installer;
        _proc = proc;
        _announce = announce;
        _flowFactory = flowFactory ?? (() => new DeviceFlowClient());
    }

    public string Id => "runner.activation";

    // 화면에서 "{Title} — {요약}" 으로 붙으므로 제목에 설명을 겹쳐 넣지 않는다.
    public string Title => "Teaveloper 러너";

    public string Why =>
        "활성화하면 학생이 밖에서 열 수 있는 주소가 생깁니다. 답안은 이 컴퓨터에만 저장됩니다.";

    // ─────────────────────────────── 점검 ───────────────────────────────

    public async Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (_catalog.Find(RunnerAppId) is not { } app)
            return CheckResult.NotApplicable("앱 카탈로그에 러너가 없습니다.");

        if (!_installer.IsInstalled(app))
            return CheckResult.NeedsFix(
                "아직 설치돼 있지 않습니다.",
                app.Summary,
                "설치와 활성화를 이어서 해 드립니다.");

        var exe = _installer.ExePath(app);
        var config = RunnerHost.ReadConfig(exe);

        if (config is not { IsUsable: true })
            return app.Activation is { IsUsable: true }
                ? CheckResult.NeedsFix(
                    "설치돼 있지만 활성화되지 않았습니다.",
                    "브라우저에서 승인 한 번만 하시면 됩니다.")
                : CheckResult.NeedsFix(
                    "설치돼 있지만 활성화되지 않았습니다.",
                    "포털에서 config.json 을 받아 다음 폴더에 두세요:",
                    $"  {Path.GetDirectoryName(exe)}");

        var status = await RunnerHost.QueryStatusAsync(config.LocalPort, ct: ct).ConfigureAwait(false);

        if (status is null)
            return CheckResult.NeedsFix(
                "활성화는 돼 있는데 지금 꺼져 있습니다.",
                $"공개 주소: {config.PublicUrl}",
                "'고침' 을 실행하면 켜 드립니다.");

        if (status.IsTokenRejected)
            return CheckResult.NeedsFix(
                "설정이 더 이상 유효하지 않습니다. 다시 활성화해야 합니다.",
                "포털에서 서버를 지웠거나 설정을 새로 받으신 경우입니다.");

        if (!status.IsConnected)
            return CheckResult.Unknown(
                $"켜져 있지만 아직 연결되지 않았습니다. ({status.State})",
                string.IsNullOrWhiteSpace(status.Message) ? "잠시 뒤 다시 확인해 주세요." : status.Message!);

        var lines = new List<string> { $"공개 주소: {Coalesce(status.PublicUrl, config.PublicUrl)}" };
        lines.Add(await RunnerHost.IsAutostartEnabledAsync(_proc, ct).ConfigureAwait(false)
            ? "로그온할 때 자동으로 켜집니다."
            : "자동 실행은 꺼져 있습니다 — 컴퓨터를 켤 때마다 직접 실행하셔야 합니다.");

        return CheckResult.Ok("연결돼 있습니다 — 학생이 지금 들어올 수 있습니다.", lines.ToArray());
    }

    // ─────────────────────────────── 손보기 ───────────────────────────────

    public async Task<FixResult> FixAsync(CancellationToken ct = default)
    {
        if (_catalog.Find(RunnerAppId) is not { } app)
            return FixResult.NotSupported("앱 카탈로그에 러너가 없습니다.");

        // ① 설치 — 포털에서 직접 받아야 하는 카탈로그면 여기서 안내로 끝난다.
        if (!_installer.IsInstalled(app))
        {
            var install = await _installer.InstallAsync(app, ct).ConfigureAwait(false);
            if (install.Outcome is not (FixOutcome.Fixed or FixOutcome.AlreadyOk))
                return install;
        }

        var exe = _installer.ExePath(app);
        var done = new List<string>();

        // ② 활성화 — 이미 설정이 있으면 건드리지 않는다.
        var config = RunnerHost.ReadConfig(exe);
        int? movedPort = null;   // 포털이 제안한 포트가 막혀 바꿔 잡았으면 그 원래 값
        if (config is not { IsUsable: true })
        {
            if (app.Activation is not { IsUsable: true } spec)
                return FixResult.Manual(
                    "자동 활성화 방법이 카탈로그에 없어 여기까지가 한계입니다.",
                    "① 포털(teaveloper.com)에 로그인합니다",
                    "② '내 서버' 에서 활성화하고 config.json 을 내려받습니다",
                    $"③ 그 파일을 다음 폴더에 둡니다: {Path.GetDirectoryName(exe)}",
                    "",
                    "마친 뒤 '점검' 을 다시 실행하면 확인됩니다.");

            try
            {
                var activated = await ActivateAsync(spec, exe, ct).ConfigureAwait(false);
                config = activated.Config;
                movedPort = activated.MovedFromPort;
            }
            catch (DeviceFlowException ex)
            {
                return FixResult.Failed(ex.Message, "'고침' 을 다시 실행하면 처음부터 다시 해 드립니다.");
            }

            done.Add($"활성화됐습니다 — {config.PublicUrl}");
            if (movedPort is { } from)
                done.Add($"포트 {from} 이(가) 쓰이고 있어 {config.LocalPort} 로 잡았습니다.");
        }

        // ③ 자동 실행 — 이미 돼 있으면 그대로 둔다(교사가 꺼 뒀을 수도 있으니 새로 켜지 않는다).
        if (await RunnerHost.IsAutostartEnabledAsync(_proc, ct).ConfigureAwait(false))
        {
            done.Add("로그온 자동 실행은 이미 켜져 있습니다.");
        }
        else if (await RunnerHost.EnableAutostartAsync(_proc, exe, ct).ConfigureAwait(false))
        {
            done.Add("로그온할 때 자동으로 켜지도록 등록했습니다.");
        }
        else
        {
            done.Add("자동 실행 등록에는 실패했습니다. 트레이 아이콘을 우클릭해 켜실 수 있습니다.");
        }

        // ④ 실행 + 연결 확인 — 파일이 놓였는지가 아니라 터널이 실제로 붙었는지로 확인한다.
        var status = await RunnerHost.QueryStatusAsync(config.LocalPort, ct: ct).ConfigureAwait(false);
        if (status is null)
        {
            if (!_proc.Launch(exe))
                return FixResult.Failed("러너를 실행하지 못했습니다.", done.ToArray());

            Say("      러너를 켜는 중…");
            status = await RunnerHost.WaitUntilConnectedAsync(config.LocalPort, ConnectTimeout, ct)
                                     .ConfigureAwait(false);
        }

        if (status is { IsConnected: true })
        {
            done.Add($"공개 주소: {Coalesce(status.PublicUrl, config.PublicUrl)}");
            done.Add("이 주소를 학생에게 알려 주시면 됩니다.");
            return FixResult.Fixed("러너가 연결됐습니다.", done.ToArray());
        }

        if (status is { IsTokenRejected: true })
        {
            done.Add("포털에서 이 서버를 지웠거나 설정을 새로 받으신 경우입니다.");
            return FixResult.Failed("설정이 포털에서 더 이상 유효하지 않습니다.", done.ToArray());
        }

        done.Add($"지금 상태: {(status is null ? "응답 없음" : status.State)}");
        done.Add("잠시 뒤 '점검' 을 다시 실행해 보세요. 학교 방화벽이 막고 있을 수도 있습니다.");
        return FixResult.Failed("러너를 켰지만 아직 연결되지 않았습니다.", done.ToArray());
    }

    // ─────────────────────────────── 활성화 ───────────────────────────────

    /// <param name="Config">저장된 설정.</param>
    /// <param name="MovedFromPort">포털이 제안한 포트가 막혀 바꿔 잡았으면 그 원래 값.</param>
    private sealed record Activated(RunnerConfig Config, int? MovedFromPort);

    private async Task<Activated> ActivateAsync(ActivationSpec spec, string exePath, CancellationToken ct)
    {
        using var flow = _flowFactory();

        var auth = await flow.StartAsync(spec.CodeUrl, ClientVersion, ct).ConfigureAwait(false);

        // 브라우저를 열어 주되, 못 열어도 계속한다 — 휴대폰으로 승인해도 되기 때문이다.
        var opened = _proc.Launch(auth.OpenUrl);

        Say("");
        Say(opened
            ? "      브라우저를 열었습니다. 이 코드를 넣고 [승인] 을 눌러 주세요."
            : "      아래 주소를 열어 이 코드를 넣고 [승인] 을 눌러 주세요.");
        Say("");
        Say($"          {auth.UserCode}");
        Say("");
        Say($"      주소: {auth.VerifyUrl}");
        Say("      (이 컴퓨터에서 안 열리면 휴대폰으로 여셔도 됩니다)");
        Say("");

        var lastMinuteShown = -1;
        var config = await flow.WaitForApprovalAsync(
            spec.TokenUrl, auth,
            onWaiting: remaining =>
            {
                // 남은 시간이 분 단위로 바뀔 때만 알린다 — 매번 찍으면 화면이 시끄럽다.
                var minutes = (int)remaining.TotalMinutes;
                if (minutes == lastMinuteShown) return;
                lastMinuteShown = minutes;
                Say($"      기다리는 중… (약 {minutes + 1}분 안에 승인해 주세요)");
            },
            ct).ConfigureAwait(false);

        // 포털은 교사 PC 에서 어느 포트가 비었는지 알 수 없다 — 그 판단만 여기서 덮어쓴다.
        var suggested = config.LocalPort;
        var picked = RunnerHost.PickLocalPort(suggested);
        config = config with { LocalPort = picked };

        RunnerHost.WriteConfig(exePath, config);
        return new Activated(config, picked == suggested ? null : suggested);
    }

    private void Say(string line) => _announce?.Invoke(line);

    private static string Coalesce(string? a, string b) => string.IsNullOrWhiteSpace(a) ? b : a!;

    private static string ClientVersion =>
        typeof(RunnerActivationTask).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
