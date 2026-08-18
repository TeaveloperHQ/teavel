using Teavel.Platform;

namespace Teavel.Setup;

/// <summary>
/// Windows 가 설정 상태를 어디에 적어 두는지 아는 곳.
///
/// 레지스트리 경로를 진단 코드 곳곳에 흩어 놓으면 나중에 무엇을 근거로 판단했는지 알 수 없다.
/// 여기 한곳에 모으고, 각 경로가 무엇인지 주석으로 남긴다.
/// </summary>
public sealed class WindowsFacts
{
    private readonly IRegistry _reg;
    private readonly ISystemPaths _paths;

    public WindowsFacts(IRegistry reg, ISystemPaths paths)
    {
        _reg = reg;
        _paths = paths;
    }

    // ─────────────────────────── Windows 자신 ───────────────────────────

    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>"22H2" 같은 판. 못 읽으면 null.</summary>
    public string? WindowsVersion
        => _reg.ReadString(RegistryRoot.LocalMachine, CurrentVersionKey, "DisplayVersion");

    /// <summary>빌드 번호. 못 읽으면 0.</summary>
    public int WindowsBuild
        => int.TryParse(_reg.ReadString(RegistryRoot.LocalMachine, CurrentVersionKey, "CurrentBuild"), out var b)
            ? b
            : 0;

    /// <summary>EditionID — "Core"(Home) · "Professional" · "Education" 등.</summary>
    public string? WindowsEdition
        => _reg.ReadString(RegistryRoot.LocalMachine, CurrentVersionKey, "EditionID");

    /// <summary>
    /// Enterprise·Education 판인지. 이 판들은 지원 기간이 1년 더 길다.
    /// </summary>
    public bool IsBusinessEdition
        => WindowsEdition is { } e
        && (e.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase)
         || e.StartsWith("Education", StringComparison.OrdinalIgnoreCase));

    /// <summary>보안 패치를 아직 받는 판인지.</summary>
    public WindowsSupportInfo Support(DateOnly today)
        => WindowsSupport.Evaluate(WindowsVersion, WindowsBuild, IsBusinessEdition, today);

    // ─────────────────────────── OneDrive ───────────────────────────

    /// <summary>업무·학교 계정(Business1)이 연결돼 있으면 그 동기화 폴더 경로. 없으면 null.</summary>
    public string? OneDriveBusinessFolder
        => Nullify(_reg.ReadString(RegistryRoot.CurrentUser,
               @"Software\Microsoft\OneDrive\Accounts\Business1", "UserFolder"));

    /// <summary>업무·학교 계정에 연결된 계정 표시 이름(대개 메일 주소). 없으면 null.</summary>
    public string? OneDriveBusinessAccount
        => Nullify(_reg.ReadString(RegistryRoot.CurrentUser,
               @"Software\Microsoft\OneDrive\Accounts\Business1", "UserEmail"))
           ?? Nullify(_reg.ReadString(RegistryRoot.CurrentUser,
               @"Software\Microsoft\OneDrive\Accounts\Business1", "DisplayName"));

    /// <summary>개인 계정 동기화 폴더. 없으면 null.</summary>
    public string? OneDrivePersonalFolder
        => Nullify(_reg.ReadString(RegistryRoot.CurrentUser,
               @"Software\Microsoft\OneDrive\Accounts\Personal", "UserFolder"));

    /// <summary>OneDrive.exe 경로. 못 찾으면 null.</summary>
    public string? OneDriveExe
    {
        get
        {
            // OneDrive 는 사용자별 설치(LOCALAPPDATA)가 기본이고, 컴퓨터별 설치도 있다.
            var candidates = new[]
            {
                Path.Combine(_paths.LocalAppData, "Microsoft", "OneDrive", "OneDrive.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             "Microsoft OneDrive", "OneDrive.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                             "Microsoft OneDrive", "OneDrive.exe"),
            };
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return null;
        }
    }

    /// <summary>
    /// 바탕 화면·문서·사진이 실제로 어디를 가리키는지.
    /// 알려진 폴더 이동(KFM)이 켜져 있으면 이 값들이 OneDrive 폴더 아래를 가리킨다.
    /// </summary>
    public IReadOnlyDictionary<string, string?> KnownFolders
    {
        get
        {
            // User Shell Folders 는 %USERPROFILE% 같은 변수가 그대로 들어 있는 원본 값이다.
            const string key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
            return new Dictionary<string, string?>
            {
                ["바탕 화면"] = Expand(_reg.ReadString(RegistryRoot.CurrentUser, key, "Desktop")),
                ["문서"]      = Expand(_reg.ReadString(RegistryRoot.CurrentUser, key, "Personal")),
                ["사진"]      = Expand(_reg.ReadString(RegistryRoot.CurrentUser, key, "My Pictures")),
            };
        }
    }

    // ──────────────────────────── Office ────────────────────────────

    /// <summary>클릭하여 실행(Click-to-Run) 으로 깔린 Office 제품 id 들. 없으면 빈 배열.</summary>
    public IReadOnlyList<string> OfficeProducts
    {
        get
        {
            var ids = _reg.ReadString(RegistryRoot.LocalMachine,
                @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration", "ProductReleaseIds");
            return string.IsNullOrWhiteSpace(ids)
                ? Array.Empty<string>()
                : ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    /// <summary>Office 버전 문자열. 없으면 null.</summary>
    public string? OfficeVersion
        => Nullify(_reg.ReadString(RegistryRoot.LocalMachine,
               @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration", "VersionToReport"));

    /// <summary>Office 에 로그인된 계정 id 들(비어 있으면 로그인 안 됨).</summary>
    public IReadOnlyList<string> OfficeIdentities
        => _reg.SubKeyNames(RegistryRoot.CurrentUser,
               @"Software\Microsoft\Office\16.0\Common\Identity\Identities");

    /// <summary>Outlook 에 메일 계정(프로필)이 만들어져 있는지.</summary>
    public bool HasOutlookProfile
        => _reg.SubKeyNames(RegistryRoot.CurrentUser,
               @"Software\Microsoft\Office\16.0\Outlook\Profiles").Count > 0;

    /// <summary>
    /// Microsoft Store 앱(MSIX)이 깔려 있는지.
    /// </summary>
    /// <remarks>
    /// Teams·To Do 같은 Store 앱은 '프로그램 추가/제거' 목록에 잘 안 잡힌다.
    /// 사용자 패키지 폴더가 가장 확실한 근거다.
    /// </remarks>
    /// <param name="packagePrefix">패키지 이름 앞부분. 예: "Microsoft.Todos", "MSTeams".</param>
    public bool HasStoreApp(string packagePrefix)
    {
        try
        {
            var packages = Path.Combine(_paths.LocalAppData, "Packages");
            if (!Directory.Exists(packages)) return false;
            return Directory.EnumerateDirectories(packages, packagePrefix + "_*").Any();
        }
        catch { return false; }
    }

    /// <summary>
    /// 예전 판 Teams(사용자 폴더에 설치되던 것)가 있는지.
    /// </summary>
    /// <remarks>
    /// 새 Teams 는 MSIX 라 <see cref="HasStoreApp"/>("MSTeams") 로 본다.
    /// 둘 다 아니면 없는 것이다 — '프로그램 추가/제거' 는 보지 않는다(회의 추가 기능이 잡힌다).
    /// </remarks>
    public bool HasClassicTeams
    {
        get
        {
            try
            {
                return File.Exists(Path.Combine(
                    _paths.LocalAppData, "Microsoft", "Teams", "current", "Teams.exe"));
            }
            catch { return false; }
        }
    }

    /// <summary>Excel/Word/Outlook 이 COM 으로 열리는지(설치 여부의 실질적 근거).</summary>
    public bool HasComProgId(string progId)
        => _reg.KeyExists(RegistryRoot.LocalMachine, $@"SOFTWARE\Classes\{progId}\CLSID")
           || _reg.KeyExists(RegistryRoot.CurrentUser, $@"Software\Classes\{progId}\CLSID");

    // ───────────────────────── 설치 프로그램 ─────────────────────────

    /// <summary>
    /// '프로그램 추가/제거' 목록에서 이름에 <paramref name="contains"/> 가 들어간 항목을 찾는다.
    /// 사용자별·컴퓨터별·32비트 목록을 모두 본다.
    /// </summary>
    public IReadOnlyList<string> FindInstalledPrograms(string contains)
    {
        var roots = new (RegistryRoot Root, string Path)[]
        {
            (RegistryRoot.CurrentUser,  @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryRoot.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryRoot.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        var found = new List<string>();
        foreach (var (root, path) in roots)
        {
            foreach (var sub in _reg.SubKeyNames(root, path))
            {
                var name = _reg.ReadString(root, $@"{path}\{sub}", "DisplayName");
                if (!string.IsNullOrWhiteSpace(name)
                    && name.Contains(contains, StringComparison.OrdinalIgnoreCase)
                    && !found.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(name);
                }
            }
        }
        return found;
    }

    // ───────────────────────────── 도우미 ─────────────────────────────

    private static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private string? Expand(string? s) => string.IsNullOrWhiteSpace(s) ? null : _paths.Expand(s);
}
