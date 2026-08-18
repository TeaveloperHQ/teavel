using Teavel.Platform;

namespace Teavel.Setup;

/// <summary>OneDrive 폴더 안의 항목 하나.</summary>
/// <param name="Name">폴더 이름.</param>
/// <param name="Path">전체 경로.</param>
/// <param name="Files">안에 든 파일 수(하위 폴더 포함). 세지 못했으면 -1.</param>
/// <param name="OnlineOnly">그중 <b>온라인 전용</b>(자리를 차지하지 않는) 파일 수.</param>
/// <param name="Bytes">이 컴퓨터가 실제로 쓰고 있는 크기.</param>
/// <param name="KnownFolder">바탕 화면·문서·사진처럼 Windows 가 옮겨 둔 폴더면 그 이름.</param>
public sealed record OneDriveItem(
    string Name, string Path, int Files, int OnlineOnly, long Bytes, string? KnownFolder = null)
{
    /// <summary>"12MB" 처럼 읽기 좋은 크기.</summary>
    public string Size => Bytes < 1024 * 1024 ? $"{Math.Max(Bytes / 1024, 0):N0}KB" : $"{Bytes / 1024 / 1024:N0}MB";
}

/// <summary>
/// OneDrive 가 지금 <b>무엇을</b> 동기화하고 있는지 읽는다.
///
/// <para>
/// 왜 이것이 따로 필요한가 — 선생님들이 OneDrive 를 어려워하는 지점은 로그인이 아니라
/// <b>"그래서 내 파일이 어디 있는 건데"</b> 다. 로그인은 됐다고 나오는데 무엇이 올라가고
/// 무엇이 안 올라가는지 알 수 없으니, 중요한 자료를 어디에 둬야 하는지 판단할 수가 없다.
/// </para>
/// <para>
/// 그래서 상태를 <b>보여 준다.</b> 폴더가 어디이고, 그 안에 무엇이 있고, 바탕 화면·문서·사진이
/// 백업되고 있는지, 그리고 어느 것이 이 컴퓨터에 실제로 내려와 있고 어느 것이 온라인에만
/// 있는지까지. 여기까지 보이면 나머지 판단은 선생님이 하실 수 있다.
/// </para>
/// <para>
/// <b>고르는 것은 대신 해 줄 수 없다.</b> 어떤 폴더를 이 컴퓨터에 내려받을지 정하는 창은
/// OneDrive 가 직접 띄우는 것이고, 자동으로 바꿀 수 있는 길(명령줄·API·레지스트리)이 없다.
/// 그래서 상태를 읽어 설명하고 그 창을 정확히 띄워 드리는 데까지 한다.
/// </para>
/// </summary>
public sealed class OneDriveDetail
{
    /// <summary>온라인 전용 파일에 붙는 표식. .NET 의 FileAttributes 에는 이 이름이 없다.</summary>
    private const int RecallOnDataAccess = 0x0040_0000;

    /// <summary>속을 들여다볼 폴더 수 상한. 큰 폴더에서 점검이 하염없이 길어지지 않게.</summary>
    private const int MaxFilesPerFolder = 20_000;

    private readonly WindowsFacts _facts;
    private readonly ISystemPaths _paths;

    public OneDriveDetail(WindowsFacts facts, ISystemPaths paths)
    {
        _facts = facts;
        _paths = paths;
    }

    /// <summary>학교 계정 동기화 폴더. 없으면 개인 계정 폴더, 그것도 없으면 null.</summary>
    public string? Folder => _facts.OneDriveBusinessFolder ?? _facts.OneDrivePersonalFolder;

    /// <summary>학교 계정으로 이어져 있는지.</summary>
    public bool IsSchoolAccount => _facts.OneDriveBusinessFolder is not null;

    /// <summary>연결된 계정(대개 메일 주소).</summary>
    public string? Account => _facts.OneDriveBusinessAccount;

    /// <summary>
    /// 동기화 폴더 안의 최상위 폴더들. 폴더가 없으면 빈 목록.
    /// </summary>
    public IReadOnlyList<OneDriveItem> Items()
    {
        var root = Folder;
        if (root is null || !Directory.Exists(root)) return Array.Empty<OneDriveItem>();

        // 바탕 화면·문서·사진이 이 안으로 옮겨져 있으면 그 사실을 이름 옆에 적어 준다.
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, path) in _facts.KnownFolders)
            if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                known[path.TrimEnd(Path.DirectorySeparatorChar)] = name;

        var items = new List<OneDriveItem>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var (files, online, bytes) = Measure(dir);
                known.TryGetValue(dir.TrimEnd(Path.DirectorySeparatorChar), out var kf);
                items.Add(new OneDriveItem(Path.GetFileName(dir), dir, files, online, bytes, kf));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return items.OrderByDescending(i => i.Bytes).ToList();
    }

    /// <summary>
    /// 팀즈 팀·부서 문서고로 동기화 중인 폴더들.
    /// </summary>
    /// <remarks>
    /// SharePoint 문서고는 OneDrive 폴더 <b>안이 아니라 그 옆</b>에 <c>%USERPROFILE%\학교이름\</c>
    /// 으로 붙는다. 선생님들이 "팀즈에 올린 파일이 내 OneDrive 에 안 보인다" 고 하는 것이
    /// 대부분 이것이다 — 없는 게 아니라 다른 자리에 있다.
    /// </remarks>
    public IReadOnlyList<OneDriveItem> TeamLibraries()
    {
        var found = new List<OneDriveItem>();
        var oneDrive = Folder?.TrimEnd(Path.DirectorySeparatorChar);

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_paths.UserProfile))
            {
                // OneDrive 폴더 자신은 뺀다.
                if (oneDrive is not null && dir.TrimEnd(Path.DirectorySeparatorChar)
                        .Equals(oneDrive, StringComparison.OrdinalIgnoreCase)) continue;

                // SharePoint 로 동기화된 폴더에는 이 표식이 붙는다(탐색기에서 파란 구름으로 보이는 것).
                if (!IsSyncRoot(dir)) continue;

                var (files, online, bytes) = Measure(dir);
                found.Add(new OneDriveItem(Path.GetFileName(dir), dir, files, online, bytes));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return found;
    }

    /// <summary>바탕 화면·문서·사진이 백업되고 있는지 — (이름, 백업중, 지금 자리).</summary>
    public IReadOnlyList<(string Name, bool Backed, string? Path)> KnownFolders()
    {
        var root = Folder;
        var outp = new List<(string, bool, string?)>();

        foreach (var (name, path) in _facts.KnownFolders)
        {
            var backed = root is not null && path is not null
                      && path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            outp.Add((name, backed, path));
        }
        return outp;
    }

    /// <summary>
    /// 이 폴더가 클라우드와 이어진 자리인지.
    /// </summary>
    /// <remarks>
    /// 재분석 지점(reparse point)이면서 디렉터리인 것을 본다. OneDrive 가 SharePoint 문서고를
    /// 붙일 때 이 형태로 만든다. 이름으로 짐작하지 않는 것이 요점이다 — 학교 이름이
    /// 무엇일지 우리가 알 수 없다.
    /// </remarks>
    private static bool IsSyncRoot(string dir)
    {
        try
        {
            var a = (int)File.GetAttributes(dir);
            return (a & (int)FileAttributes.ReparsePoint) != 0
                || (a & RecallOnDataAccess) != 0;
        }
        catch { return false; }
    }

    /// <summary>폴더 하나를 재 본다 — (파일 수, 온라인 전용 수, 이 컴퓨터가 쓰는 크기).</summary>
    private static (int Files, int OnlineOnly, long Bytes) Measure(string dir)
    {
        var files = 0;
        var online = 0;
        long bytes = 0;

        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (++files > MaxFilesPerFolder) break;

                try
                {
                    var info = new FileInfo(f);

                    // 온라인 전용 파일은 자리를 차지하지 않는다. 크기에 세면
                    // "1GB 쓰고 있다" 고 잘못 알려 주게 된다.
                    if (((int)info.Attributes & RecallOnDataAccess) != 0) { online++; continue; }

                    bytes += info.Length;
                }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (-1, 0, 0); }

        return (files, online, bytes);
    }
}
