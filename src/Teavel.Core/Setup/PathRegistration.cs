using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Teavel.Platform;

namespace Teavel.Setup;

/// <summary>
/// PowerShell 어디서나 <c>teavel</c> 만 쳐도 실행되도록, Teavel 폴더를 사용자 PATH 에 등록한다.
///
/// 사용자 PATH(HKCU\Environment\Path)만 건드리므로 <b>관리자 권한이 필요 없다</b>.
/// 컴퓨터 전체 PATH(HKLM)는 건드리지 않는다 — 교사 개인 PC 에서 전체 설정을 바꿀 이유가 없고,
/// 학교 관리 PC 라면 애초에 권한도 없다.
/// </summary>
public sealed class PathRegistration
{
    private const string EnvironmentKey = @"Environment";
    private const string PathValue = "Path";

    private readonly IRegistry _reg;
    private readonly ISystemPaths _paths;

    public PathRegistration(IRegistry reg, ISystemPaths paths)
    {
        _reg = reg;
        _paths = paths;
    }

    /// <summary>PATH 에 넣을 폴더 — teavel.exe 가 놓인 곳.</summary>
    public string TargetDirectory => _paths.AppDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>이미 등록돼 있는지.</summary>
    public bool IsRegistered()
    {
        var current = _reg.ReadStringValue(RegistryRoot.CurrentUser, EnvironmentKey, PathValue);
        return current is not null && Split(current.Value).Any(Same);
    }

    /// <summary>등록한다. 이미 돼 있으면 AlreadyOk.</summary>
    public FixResult Register()
    {
        if (!OperatingSystem.IsWindows())
            return FixResult.NotSupported("Windows 에서만 할 수 있습니다.");

        var dir = TargetDirectory;
        if (!File.Exists(Path.Combine(dir, "teavel.exe")) && !File.Exists(Path.Combine(dir, "teavel")))
            return FixResult.Failed(
                "실행 파일이 있는 폴더를 찾지 못했습니다.",
                $"확인한 곳: {dir}");

        var current = _reg.ReadStringValue(RegistryRoot.CurrentUser, EnvironmentKey, PathValue);

        // 값이 아예 없을 수도 있다(PATH 를 한 번도 안 건드린 계정). 그때는 새로 만든다.
        var existing = current?.Value ?? "";
        var parts = Split(existing).ToList();

        // 지난번에 다른 자리에 깔았다가 폴더째 지운 흔적을 먼저 치운다.
        // 이게 남아 있으면 옮겨 설치할 때마다 죽은 항목이 쌓이고,
        // 어느 것이 살아 있는지 알 수 없어 "지웠다 다시 깔았는데 안 된다" 가 된다.
        var stale = parts.Where(IsDeadTeavelEntry).ToList();
        foreach (var s in stale) parts.Remove(s);

        if (parts.Any(Same))
        {
            if (stale.Count == 0)
                return FixResult.AlreadyOk("이미 등록돼 있습니다. 새 PowerShell 창에서 teavel 을 쳐 보세요.");
        }
        else
        {
            parts.Add(dir);
        }

        var updated = string.Join(';', parts);

        // 종류를 그대로 유지한다. REG_EXPAND_SZ 를 REG_SZ 로 바꿔 쓰면
        // 사용자 PATH 에 있던 %VAR% 들이 더 이상 펼쳐지지 않는다.
        var expandable = current?.Expandable ?? updated.Contains('%');

        if (!_reg.WriteStringValue(RegistryRoot.CurrentUser, EnvironmentKey, PathValue, updated, expandable))
            return FixResult.Failed("PATH 에 등록하지 못했습니다.");

        NotifyEnvironmentChanged();

        return FixResult.Fixed("등록했습니다.") with
        {
            NextSteps = new[]
            {
                "지금 열려 있는 PowerShell 창에는 아직 반영되지 않습니다.",
                "새 PowerShell 창을 열고 teavel 을 쳐 보세요.",
                "",
                $"등록한 폴더: {dir}",
            },
        };
    }

    /// <summary>등록을 푼다.</summary>
    public FixResult Unregister()
    {
        if (!OperatingSystem.IsWindows())
            return FixResult.NotSupported("Windows 에서만 할 수 있습니다.");

        var current = _reg.ReadStringValue(RegistryRoot.CurrentUser, EnvironmentKey, PathValue);
        if (current is null) return FixResult.AlreadyOk("등록돼 있지 않습니다.");

        var parts = Split(current.Value).ToList();
        var kept = parts.Where(p => !Same(p)).ToList();

        if (kept.Count == parts.Count) return FixResult.AlreadyOk("등록돼 있지 않습니다.");

        if (!_reg.WriteStringValue(
                RegistryRoot.CurrentUser, EnvironmentKey, PathValue,
                string.Join(';', kept), current.Expandable))
            return FixResult.Failed("PATH 에서 지우지 못했습니다.");

        NotifyEnvironmentChanged();
        return FixResult.Fixed("PATH 에서 지웠습니다. 프로그램 파일은 그대로 있습니다.");
    }

    /// <summary>PATH 문자열을 항목으로 나눈다. 빈 항목과 끝의 역슬래시는 정리한다.</summary>
    private static IEnumerable<string> Split(string path)
        => path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(p => p.TrimEnd(Path.DirectorySeparatorChar));

    /// <summary>
    /// 예전 Teavel 자리인데 이제는 없는 항목인지.
    /// </summary>
    /// <remarks>
    /// 이름에 Teavel 이 들어가면서 실제로는 사라진 폴더만 지운다.
    /// 사라진 폴더를 무조건 지우면 선생님이 나중에 다시 꽂을 USB 나 네트워크 드라이브까지
    /// 걷어내게 된다 — 남의 PATH 를 함부로 손대지 않는다는 뜻이다.
    /// </remarks>
    private static bool IsDeadTeavelEntry(string candidate)
    {
        if (candidate.IndexOf("Teavel", StringComparison.OrdinalIgnoreCase) < 0) return false;

        try { return !Directory.Exists(candidate); }
        catch { return false; }
    }

    /// <summary>같은 폴더를 가리키는지. Windows 경로라 대소문자를 구분하지 않는다.</summary>
    private bool Same(string candidate)
        => string.Equals(
            candidate.TrimEnd(Path.DirectorySeparatorChar),
            TargetDirectory,
            StringComparison.OrdinalIgnoreCase);

    // ── 환경 변수가 바뀌었음을 알린다 ──
    // 이걸 안 하면 지금 열려 있는 프로그램(탐색기 등)은 다시 로그인할 때까지 새 PATH 를 모른다.

    private const int HwndBroadcast = 0xffff;
    private const int WmSettingChange = 0x001A;
    private const int SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);

    [SupportedOSPlatform("windows")]
    private static void NotifyEnvironmentChanged()
    {
        try
        {
            SendMessageTimeout(
                new IntPtr(HwndBroadcast), WmSettingChange, IntPtr.Zero, "Environment",
                SmtoAbortIfHung, 5000, out _);
        }
        catch { /* 알림에 실패해도 등록 자체는 됐다 — 새 창을 열면 반영된다 */ }
    }
}
