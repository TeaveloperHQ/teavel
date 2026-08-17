using Teavel.Platform;

namespace Teavel.Setup;

/// <summary>
/// 탐색기 우클릭에 "여기서 Teavel 열기" 를 넣는다.
///
/// 교사 업무는 거의 다 폴더 단위다 — "이 폴더 엑셀 합쳐줘", "이 폴더 미제출자 찾아줘".
/// 그런데 콘솔에서 <c>C:\Users\김선생\OneDrive - ○○고등학교\2학년\2반 수행평가</c> 를
/// 손으로 치는 선생님은 없다. 폴더에서 바로 열 수 있으면 그 벽이 통째로 사라진다.
///
/// HKCU\Software\Classes 에만 쓰므로 <b>관리자 권한이 필요 없다</b>.
/// </summary>
public sealed class ExplorerRegistration
{
    // 폴더 아이콘을 우클릭했을 때 / 폴더 안 빈 공간을 우클릭했을 때 — 둘 다 필요하다.
    private const string FolderKey = @"Software\Classes\Directory\shell\Teavel";
    private const string BackgroundKey = @"Software\Classes\Directory\Background\shell\Teavel";

    private const string MenuLabel = "여기서 Teavel 열기";

    private readonly IRegistry _reg;
    private readonly ISystemPaths _paths;

    public ExplorerRegistration(IRegistry reg, ISystemPaths paths)
    {
        _reg = reg;
        _paths = paths;
    }

    /// <summary>이름을 짐작하지 않는다 — 부르는 쪽이 정확한 경로를 준다.</summary>

    /// <summary>등록돼 있는지.</summary>
    public bool IsRegistered()
        => _reg.KeyExists(RegistryRoot.CurrentUser, FolderKey + @"\command");

    /// <summary>우클릭 메뉴를 넣는다.</summary>
    /// <param name="exePath">등록할 실행 파일. 판 번호가 붙은 이름이어도 된다.</param>
    public FixResult Register(string exePath)
    {
        if (!OperatingSystem.IsWindows())
            return FixResult.NotSupported("Windows 에서만 할 수 있습니다.");

        var exe = exePath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return FixResult.Failed("실행 파일을 찾지 못했습니다.", $"확인한 곳: {exe}");

        // %V = 우클릭한 폴더. Directory 와 Directory\Background 모두에서 폴더 경로를 준다
        //      (%1 은 Background 에서 비어 있어 쓸 수 없다).
        var command = $"\"{exe}\" --here \"%V\"";

        foreach (var key in new[] { FolderKey, BackgroundKey })
        {
            if (!_reg.WriteString(RegistryRoot.CurrentUser, key, "", MenuLabel))
                return FixResult.Failed("우클릭 메뉴를 넣지 못했습니다.");

            // 메뉴에 Teavel 아이콘을 함께 띄운다.
            _reg.WriteString(RegistryRoot.CurrentUser, key, "Icon", exe);

            if (!_reg.WriteString(RegistryRoot.CurrentUser, key + @"\command", "", command))
                return FixResult.Failed("우클릭 메뉴를 넣지 못했습니다.");
        }

        return FixResult.Fixed("우클릭 메뉴를 넣었습니다.") with
        {
            NextSteps = new[]
            {
                "폴더를 오른쪽 클릭하면 [여기서 Teavel 열기] 가 보입니다.",
                "그 폴더에서 시작하므로 폴더 경로를 치지 않아도 됩니다.",
                "",
                "Windows 11 이라면 [추가 옵션 표시] 를 눌러야 나옵니다.",
            },
        };
    }

    /// <summary>우클릭 메뉴를 뺀다.</summary>
    public FixResult Unregister()
    {
        if (!OperatingSystem.IsWindows())
            return FixResult.NotSupported("Windows 에서만 할 수 있습니다.");

        if (!IsRegistered()) return FixResult.AlreadyOk("우클릭 메뉴가 등록돼 있지 않습니다.");

        var removed = false;
        foreach (var key in new[] { FolderKey, BackgroundKey })
        {
            // command 하위 키부터 지워야 부모가 지워진다.
            if (_reg.DeleteKey(RegistryRoot.CurrentUser, key + @"\command")) removed = true;
            if (_reg.DeleteKey(RegistryRoot.CurrentUser, key)) removed = true;
        }

        return removed
            ? FixResult.Fixed("우클릭 메뉴를 뺐습니다.")
            : FixResult.Failed("우클릭 메뉴를 빼지 못했습니다.");
    }
}
