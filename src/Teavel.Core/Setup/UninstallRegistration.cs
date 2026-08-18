using Teavel.Platform;

namespace Teavel.Setup;

/// <summary>
/// Windows 설정 &gt; 앱 목록에 Teavel 을 올린다.
///
/// <para>
/// <b>왜 필요한가.</b> Teavel 은 설치 프로그램 없이 exe 하나로 받는다. 편하지만 대신
/// Windows 가 그것을 '설치된 앱' 으로 알지 못한다. 그래서 지우려고 설정 &gt; 앱 을 열면
/// <b>목록에 아예 없다.</b> 교사가 지울 방법을 찾지 못하고, 실제로 그렇게 막혔다.
/// </para>
/// <para>
/// Windows 는 이 레지스트리 키 하나만 보고 목록을 만든다. 우리가 그 자리를 채워 주면
/// 다른 프로그램과 똑같이 목록에 뜨고, [제거] 를 누르면 Windows 가
/// <c>UninstallString</c> 에 적힌 대로 <b>Teavel 자신을</b> 부른다.
/// </para>
/// <para>
/// HKCU 에만 쓰므로 <b>관리자 권한이 필요 없다</b> — PATH·탐색기 등록과 같은 원칙이다.
/// </para>
/// </summary>
public sealed class UninstallRegistration
{
    /// <summary>Windows 가 '설치된 앱' 목록을 만들 때 읽는 자리.</summary>
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Teavel";

    private readonly IRegistry _reg;

    public UninstallRegistration(IRegistry reg) => _reg = reg;

    /// <summary>설정 앱 목록에 올라가 있는지.</summary>
    public bool IsRegistered() => _reg.KeyExists(RegistryRoot.CurrentUser, Key);

    /// <summary>
    /// 목록에 올린다.
    /// </summary>
    /// <param name="exePath">지금 쓰고 있는 실행 파일. 판 번호가 붙은 이름이어도 된다.</param>
    /// <param name="version">보여 줄 판 번호.</param>
    public FixResult Register(string exePath, string version)
    {
        if (!OperatingSystem.IsWindows())
            return FixResult.NotSupported("Windows 에서만 할 수 있습니다.");

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return FixResult.Failed("실행 파일을 찾지 못했습니다.", $"확인한 곳: {exePath}");

        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";

        // 설정 앱이 부를 명령. **한글이 아니라 영문 별칭을 쓴다** —
        // 이 문자열은 우리가 아니라 Windows 가 풀어서 실행하므로,
        // 어떤 코드 페이지에서도 상하지 않을 글자만 쓰는 것이 안전하다.
        var uninstall = $"\"{exePath}\" uninstall";

        var ok = _reg.WriteString(RegistryRoot.CurrentUser, Key, "DisplayName", "Teavel")
              && _reg.WriteString(RegistryRoot.CurrentUser, Key, "UninstallString", uninstall);

        if (!ok) return FixResult.Failed("Windows 설정 목록에 올리지 못했습니다.");

        // 나머지는 있으면 좋은 것들이라 실패해도 넘어간다 — 목록에 뜨는 것이 본질이다.
        _reg.WriteString(RegistryRoot.CurrentUser, Key, "QuietUninstallString", uninstall + " --yes");
        _reg.WriteString(RegistryRoot.CurrentUser, Key, "DisplayIcon", exePath);
        _reg.WriteString(RegistryRoot.CurrentUser, Key, "DisplayVersion", version);
        _reg.WriteString(RegistryRoot.CurrentUser, Key, "Publisher", "Teaveloper");
        _reg.WriteString(RegistryRoot.CurrentUser, Key, "InstallLocation", dir);

        // 고칠 것도 바꿀 것도 없는 프로그램이다. 이 둘을 안 적으면 설정 앱에
        // 아무 일도 하지 않는 [수정] 단추가 함께 뜬다.
        _reg.WriteDword(RegistryRoot.CurrentUser, Key, "NoModify", 1);
        _reg.WriteDword(RegistryRoot.CurrentUser, Key, "NoRepair", 1);

        if (DirectorySizeKb(dir) is { } kb)
            _reg.WriteDword(RegistryRoot.CurrentUser, Key, "EstimatedSize", kb);

        return FixResult.Fixed("Windows 설정 목록에 올렸습니다.") with
        {
            NextSteps = new[]
            {
                "설정 > 앱 > 설치된 앱 에서 Teavel 이 보입니다.",
                "거기서 [제거] 를 누르면 지울 수 있습니다.",
            },
        };
    }

    /// <summary>목록에서 내린다.</summary>
    public FixResult Unregister()
    {
        if (!OperatingSystem.IsWindows())
            return FixResult.NotSupported("Windows 에서만 할 수 있습니다.");

        if (!IsRegistered()) return FixResult.AlreadyOk("설정 목록에 올라가 있지 않습니다.");

        return _reg.DeleteKey(RegistryRoot.CurrentUser, Key)
            ? FixResult.Fixed("Windows 설정 목록에서 내렸습니다.")
            : FixResult.Failed("Windows 설정 목록에서 내리지 못했습니다.");
    }

    /// <summary>폴더 크기(KB). 설정 앱이 보여 주는 용량이다. 재지 못하면 null.</summary>
    private static int? DirectorySizeKb(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;

            long bytes = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(f).Length; } catch (IOException) { }
            }

            return (int)Math.Min(bytes / 1024, int.MaxValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
