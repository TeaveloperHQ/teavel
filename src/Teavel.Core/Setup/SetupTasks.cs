using Teavel.Apps;
using Teavel.Platform;
using Teavel.Tools;

namespace Teavel.Setup;

/// <summary>세팅 항목이 속한 단계. 교사에게 보여줄 때 이 순서로 묶는다.</summary>
public enum SetupStage
{
    /// <summary>① 계정 — 여기가 되면 나머지가 줄줄이 딸려 온다.</summary>
    Account,

    /// <summary>② 자료 지키기 — 자료를 잃지 않게.</summary>
    Backup,

    /// <summary>③ 프로그램 — 쓸 것들이 깔려 있게.</summary>
    Programs,

    /// <summary>④ 인쇄.</summary>
    Printing,
}

/// <summary>
/// PC 세팅 항목 전부. <b>순서가 곧 안내 순서다.</b>
///
/// 교사 PC 는 대개 로컬 계정으로만 쓰여서, Windows 에 학교 계정이 붙어 있지 않다.
/// 그것부터 해결하지 않으면 원드라이브·아웃룩·팀즈가 저마다 로그인을 요구하고
/// 선생님은 같은 비밀번호를 네 번 넣다가 포기한다.
/// 그래서 ① 계정 → ② 자료 지키기 → ③ 프로그램 → ④ 인쇄 순으로 세워 둔다.
/// </summary>
public sealed class SetupCatalog
{
    private readonly List<(SetupStage Stage, ISetupTask Task)> _tasks;

    /// <param name="apps">teaveloper 앱 카탈로그 — 러너처럼 세팅 항목이 직접 다루는 앱에 쓴다.</param>
    /// <param name="announce">
    /// 진행 상황을 화면에 흘려보낼 통로. 대부분의 항목은 결과만 돌려주면 되지만,
    /// 활성화처럼 교사의 승인을 기다리는 항목은 기다리는 동안 말을 걸어야 한다.
    /// </param>
    public SetupCatalog(
        WindowsFacts facts,
        IProcessRunner proc,
        ISystemPaths paths,
        ToolRunner runner,
        AppCatalog apps,
        AppInstaller installer,
        Action<string>? announce = null)
    {
        _tasks = new List<(SetupStage, ISetupTask)>
        {
            // ① 계정 — 이 하나가 나머지의 뿌리다.
            (SetupStage.Account, new WindowsAccountTask(runner)),
            (SetupStage.Account, new OneDriveSignInTask(facts, proc)),
            (SetupStage.Account, new OfficeSignInTask(facts, proc, paths)),
            (SetupStage.Account, new OutlookAccountTask(facts, proc)),

            // ② 자료 지키기
            (SetupStage.Backup, new OneDriveKnownFoldersTask(facts, proc)),

            // ③ 프로그램
            (SetupStage.Programs, new OfficeInstalledTask(facts, proc)),
            (SetupStage.Programs, new TeamsInstalledTask(facts, proc)),
            (SetupStage.Programs, new TodoInstalledTask(facts, proc)),
            // 러너는 '깔렸나' 로 끝나지 않는다 — 활성화까지 돼야 학생이 들어올 수 있다.
            (SetupStage.Programs, new RunnerActivationTask(apps, installer, proc, announce)),

            // ④ 인쇄
            (SetupStage.Printing, new PrinterDefaultTask(runner)),
        };
    }

    public IReadOnlyList<ISetupTask> All => _tasks.Select(t => t.Task).ToList();

    /// <summary>
    /// 세팅 항목이 이미 다루고 있는 앱 id — 점검 화면의 'teaveloper 앱' 목록에서
    /// 같은 앱이 두 번 보이지 않게 하는 데 쓴다.
    /// </summary>
    public IReadOnlyCollection<string> CoveredAppIds { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { RunnerActivationTask.RunnerAppId };

    /// <summary>단계별로 묶어 돌려준다(점검 화면이 이 순서로 나온다).</summary>
    public IEnumerable<IGrouping<SetupStage, ISetupTask>> ByStage()
        => _tasks.GroupBy(t => t.Stage, t => t.Task);

    public ISetupTask? Find(string id)
        => _tasks.Select(t => t.Task)
                 .FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>단계 이름.</summary>
    public static string StageName(SetupStage stage) => stage switch
    {
        SetupStage.Account => "① 계정 — 학교 계정으로 이어 두기",
        SetupStage.Backup => "② 자료 지키기 — 잃어버리지 않게",
        SetupStage.Programs => "③ 프로그램 — 쓸 것들 갖추기",
        SetupStage.Printing => "④ 인쇄",
        _ => stage.ToString(),
    };
}

/// <summary>winget 으로 프로그램을 설치하는 항목들이 공유하는 부분.</summary>
public abstract class WingetInstallTask : ISetupTask
{
    protected readonly IProcessRunner Proc;

    protected WingetInstallTask(IProcessRunner proc) => Proc = proc;

    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Why { get; }

    /// <summary>winget 패키지 id.</summary>
    protected abstract string PackageId { get; }

    public abstract Task<CheckResult> CheckAsync(CancellationToken ct = default);

    public virtual async Task<FixResult> FixAsync(CancellationToken ct = default)
    {
        var check = await CheckAsync(ct).ConfigureAwait(false);
        if (check.State == CheckState.Ok) return FixResult.AlreadyOk(check.Summary);
        if (check.State == CheckState.NotApplicable) return FixResult.NotSupported(check.Summary);

        if (!Proc.Exists("winget"))
            return FixResult.Failed(
                "winget(앱 설치 도구)이 없어 자동으로 설치할 수 없습니다.",
                "Microsoft Store 에서 '앱 설치 관리자'를 설치하면 winget 이 생깁니다.",
                "또는 학교 전산 담당 선생님께 설치를 요청하세요.");

        var res = await Proc.RunAsync("winget", new[]
        {
            "install", "--id", PackageId, "--exact", "--silent",
            "--accept-package-agreements", "--accept-source-agreements",
        }, timeout: TimeSpan.FromMinutes(20), ct: ct).ConfigureAwait(false);

        if (!res.Ok)
            return FixResult.Failed(
                $"{Title} 설치에 실패했습니다.",
                res.FailureSummary);

        // 설치 직후 레지스트리가 아직 안 보일 수 있으니 결과를 단정하지 않는다.
        return FixResult.Fixed($"{Title} 설치를 마쳤습니다. 컴퓨터를 다시 시작한 뒤 확인해 주세요.");
    }
}

// ─────────────────────────────── OneDrive ───────────────────────────────

/// <summary>학교 계정으로 OneDrive 에 로그인돼 있는지.</summary>
public sealed class OneDriveSignInTask : ISetupTask
{
    private readonly WindowsFacts _facts;
    private readonly IProcessRunner _proc;

    public OneDriveSignInTask(WindowsFacts facts, IProcessRunner proc)
    {
        _facts = facts;
        _proc = proc;
    }

    public string Id => "onedrive.signin";
    public string Title => "OneDrive 학교 계정 로그인";
    public string Why => "여기에 로그인해야 자료가 학교 계정에 백업되고, 다른 컴퓨터에서도 열립니다.";

    public Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(CheckResult.NotApplicable("Windows 에서만 확인할 수 있습니다."));

        var folder = _facts.OneDriveBusinessFolder;
        if (folder is not null)
        {
            var account = _facts.OneDriveBusinessAccount;
            var lines = new List<string> { $"동기화 폴더: {folder}" };
            if (account is not null) lines.Add($"계정: {account}");
            if (!Directory.Exists(folder))
                return Task.FromResult(CheckResult.NeedsFix(
                    "계정은 연결돼 있는데 동기화 폴더가 없습니다.",
                    $"레지스트리가 가리키는 곳: {folder}",
                    "OneDrive 를 다시 설정해야 합니다."));

            return Task.FromResult(CheckResult.Ok("학교 계정으로 로그인돼 있습니다.", lines.ToArray()));
        }

        // 개인 계정만 붙어 있는 경우가 흔하다 — 그건 학교 자료 백업이 아니다.
        var personal = _facts.OneDrivePersonalFolder;
        return Task.FromResult(personal is not null
            ? CheckResult.NeedsFix(
                "개인 OneDrive 만 연결돼 있습니다. 학교 계정이 없습니다.",
                $"지금 연결된 개인 폴더: {personal}",
                "학교 자료는 학교 계정에 저장해야 합니다.")
            : CheckResult.NeedsFix("OneDrive 에 로그인돼 있지 않습니다."));
    }

    public Task<FixResult> FixAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(FixResult.NotSupported("Windows 에서만 할 수 있습니다."));

        if (_facts.OneDriveBusinessFolder is { } f && Directory.Exists(f))
            return Task.FromResult(FixResult.AlreadyOk("이미 학교 계정으로 로그인돼 있습니다."));

        var exe = _facts.OneDriveExe;
        if (exe is null)
            return Task.FromResult(FixResult.Failed(
                "이 컴퓨터에서 OneDrive 를 찾지 못했습니다.",
                "Microsoft Store 에서 'OneDrive' 를 설치한 뒤 다시 시도해 주세요."));

        // 로그인은 비밀번호가 필요해 대신 해 줄 수 없다. 창만 띄우고 순서를 알려 준다.
        if (!_proc.Launch(exe))
            return Task.FromResult(FixResult.Failed("OneDrive 를 실행하지 못했습니다."));

        return Task.FromResult(FixResult.Manual(
            "OneDrive 설정 창을 띄웠습니다. 로그인은 직접 해 주셔야 합니다.",
            "① 학교에서 받은 메일 주소를 입력하고 [로그인]",
            "② 비밀번호 입력 (학교 포털과 같은 비밀번호입니다)",
            "③ '내 OneDrive 폴더' 위치는 그대로 두고 [다음]",
            "④ 끝까지 [다음] 을 눌러 마칩니다",
            "",
            "마친 뒤 '점검' 을 다시 실행하면 확인됩니다."));
    }
}

/// <summary>바탕 화면·문서·사진이 OneDrive 로 백업되고 있는지(알려진 폴더 이동).</summary>
public sealed class OneDriveKnownFoldersTask : ISetupTask
{
    private readonly WindowsFacts _facts;
    private readonly IProcessRunner _proc;

    public OneDriveKnownFoldersTask(WindowsFacts facts, IProcessRunner proc)
    {
        _facts = facts;
        _proc = proc;
    }

    public string Id => "onedrive.kfm";
    public string Title => "바탕 화면·문서·사진 백업(알려진 폴더 이동)";
    public string Why => "컴퓨터가 고장 나거나 바뀌어도 바탕 화면과 문서가 그대로 남습니다.";

    public Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(CheckResult.NotApplicable("Windows 에서만 확인할 수 있습니다."));

        var oneDrive = _facts.OneDriveBusinessFolder;
        if (oneDrive is null)
            return Task.FromResult(CheckResult.NeedsFix(
                "먼저 OneDrive 에 학교 계정으로 로그인해야 합니다.",
                "'OneDrive 학교 계정 로그인' 을 먼저 끝내 주세요."));

        var backed = new List<string>();
        var notBacked = new List<string>();

        foreach (var (name, path) in _facts.KnownFolders)
        {
            if (path is null) { notBacked.Add($"{name} — 위치를 확인하지 못했습니다"); continue; }

            if (path.StartsWith(oneDrive, StringComparison.OrdinalIgnoreCase)) backed.Add($"{name} — 백업 중");
            else notBacked.Add($"{name} — {path}");
        }

        if (notBacked.Count == 0)
            return Task.FromResult(CheckResult.Ok("바탕 화면·문서·사진이 모두 백업되고 있습니다.", backed.ToArray()));

        var lines = new List<string>();
        if (backed.Count > 0) { lines.AddRange(backed); lines.Add(""); }
        lines.Add("백업되지 않는 폴더:");
        lines.AddRange(notBacked.Select(x => "  " + x));

        return Task.FromResult(CheckResult.NeedsFix(
            $"{notBacked.Count}개 폴더가 백업되지 않고 있습니다.", lines.ToArray()));
    }

    public Task<FixResult> FixAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(FixResult.NotSupported("Windows 에서만 할 수 있습니다."));

        var exe = _facts.OneDriveExe;
        if (exe is null)
            return Task.FromResult(FixResult.Failed("이 컴퓨터에서 OneDrive 를 찾지 못했습니다."));

        // 폴더 백업 켜기는 OneDrive 클라이언트가 직접 물어봐야 하는 절차다.
        // (관리자 권한으로 정책을 심는 방법도 있지만 학교 테넌트 ID 가 필요하고,
        //  교사 개인 PC 에서 몰래 정책을 바꾸는 건 옳지 않다.)
        _proc.Launch(exe, new[] { "/settings" });

        return Task.FromResult(FixResult.Manual(
            "OneDrive 설정 창을 띄웠습니다. 다음 순서로 켜 주세요.",
            "① [동기화 및 백업] 탭",
            "② [백업 관리] 단추",
            "③ 바탕 화면 · 문서 · 사진 을 모두 켬으로",
            "④ [변경 내용 저장]",
            "",
            "폴더가 크면 처음 한 번은 시간이 걸립니다.",
            "마친 뒤 '점검' 을 다시 실행하면 확인됩니다."));
    }
}

// ────────────────────────────── Office ──────────────────────────────

/// <summary>Word·Excel·PowerPoint 가 깔려 있는지.</summary>
public sealed class OfficeInstalledTask : WingetInstallTask
{
    private readonly WindowsFacts _facts;

    public OfficeInstalledTask(WindowsFacts facts, IProcessRunner proc) : base(proc) => _facts = facts;

    public override string Id => "office.installed";
    public override string Title => "Office(워드·엑셀·파워포인트) 설치";
    public override string Why => "Teavel 의 엑셀·워드·아웃룩 기능은 이 프로그램들을 직접 부려서 동작합니다.";
    protected override string PackageId => "Microsoft.Office";

    public override Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(CheckResult.NotApplicable("Windows 에서만 확인할 수 있습니다."));

        // COM 등록 여부가 '실제로 부릴 수 있는지' 에 가장 가까운 근거다.
        var apps = new[]
        {
            ("엑셀", "Excel.Application"),
            ("워드", "Word.Application"),
            ("아웃룩", "Outlook.Application"),
        };

        var present = apps.Where(a => _facts.HasComProgId(a.Item2)).Select(a => a.Item1).ToList();
        var missing = apps.Where(a => !_facts.HasComProgId(a.Item2)).Select(a => a.Item1).ToList();

        var lines = new List<string>();
        if (_facts.OfficeVersion is { } v) lines.Add($"버전: {v}");
        if (_facts.OfficeProducts.Count > 0) lines.Add($"제품: {string.Join(", ", _facts.OfficeProducts)}");

        if (missing.Count == 0)
            return Task.FromResult(CheckResult.Ok(
                $"{string.Join("·", present)} 모두 쓸 수 있습니다.", lines.ToArray()));

        if (present.Count == 0)
            return Task.FromResult(CheckResult.NeedsFix("Office 가 설치돼 있지 않습니다.", lines.ToArray()));

        lines.Insert(0, $"쓸 수 있음: {string.Join("·", present)}");
        return Task.FromResult(CheckResult.NeedsFix(
            $"{string.Join("·", missing)} 을(를) 쓸 수 없습니다.", lines.ToArray()));
    }
}

/// <summary>Office 에 학교 계정으로 로그인(라이선스 활성화)돼 있는지.</summary>
public sealed class OfficeSignInTask : ISetupTask
{
    private readonly WindowsFacts _facts;
    private readonly IProcessRunner _proc;
    private readonly ISystemPaths _paths;

    public OfficeSignInTask(WindowsFacts facts, IProcessRunner proc, ISystemPaths paths)
    {
        _facts = facts;
        _proc = proc;
        _paths = paths;
    }

    public string Id => "office.signin";
    public string Title => "Office 학교 계정 로그인";
    public string Why => "로그인해야 정품으로 쓸 수 있고, 저장한 문서가 OneDrive 와 이어집니다.";

    public Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(CheckResult.NotApplicable("Windows 에서만 확인할 수 있습니다."));

        var ids = _facts.OfficeIdentities;
        return Task.FromResult(ids.Count > 0
            ? CheckResult.Ok($"Office 에 계정 {ids.Count}개가 연결돼 있습니다.")
            : CheckResult.NeedsFix(
                "Office 에 로그인돼 있지 않습니다.",
                "로그인하지 않으면 며칠 뒤 '정품 인증' 알림이 뜨고 편집이 막힐 수 있습니다."));
    }

    public Task<FixResult> FixAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(FixResult.NotSupported("Windows 에서만 할 수 있습니다."));

        if (_facts.OfficeIdentities.Count > 0)
            return Task.FromResult(FixResult.AlreadyOk("이미 로그인돼 있습니다."));

        // 워드를 띄워 교사가 직접 로그인하게 한다(비밀번호가 필요하다).
        if (!_proc.Launch("winword.exe"))
            return Task.FromResult(FixResult.Failed(
                "워드를 실행하지 못했습니다.",
                "'Office 설치' 항목을 먼저 확인해 주세요."));

        return Task.FromResult(FixResult.Manual(
            "워드를 띄웠습니다. 로그인은 직접 해 주셔야 합니다.",
            "① 오른쪽 위 [로그인] 을 누릅니다",
            "② 학교에서 받은 메일 주소와 비밀번호를 입력합니다",
            "③ 워드를 닫습니다",
            "",
            "한 번만 하면 엑셀·아웃룩에도 함께 적용됩니다."));
    }
}

// ─────────────────────────────── Teams ───────────────────────────────

/// <summary>Teams 가 깔려 있는지.</summary>
public sealed class TeamsInstalledTask : WingetInstallTask
{
    private readonly WindowsFacts _facts;

    public TeamsInstalledTask(WindowsFacts facts, IProcessRunner proc) : base(proc) => _facts = facts;

    public override string Id => "teams.installed";
    public override string Title => "Teams 설치";
    public override string Why => "학교 공지·화상 수업·협업 파일이 Teams 로 오는 경우가 많습니다.";
    protected override string PackageId => "Microsoft.Teams";

    public override Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(CheckResult.NotApplicable("Windows 에서만 확인할 수 있습니다."));

        var found = _facts.FindInstalledPrograms("Teams");
        return Task.FromResult(found.Count > 0
            ? CheckResult.Ok("Teams 가 설치돼 있습니다.", found.ToArray())
            : CheckResult.NeedsFix("Teams 가 설치돼 있지 않습니다."));
    }
}
