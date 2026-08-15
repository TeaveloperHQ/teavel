namespace Teavel.Platform;

/// <summary>
/// 교사 PC의 주요 경로. 모든 파일 접근이 여기를 지나므로,
/// 비Windows 개발 환경에서는 임시 폴더를 가리키게 바꿔 끼울 수 있다.
/// </summary>
public interface ISystemPaths
{
    /// <summary>Teavel 실행 파일이 놓인 폴더. 도구 스크립트·카탈로그를 여기서 찾는다.</summary>
    string AppDirectory { get; }

    /// <summary>설정·로그·기록이 쌓이는 폴더(%LOCALAPPDATA%\Teaveloper\Teavel). 없으면 만든다.</summary>
    string DataDirectory { get; }

    /// <summary>교사 홈 폴더.</summary>
    string UserProfile { get; }

    /// <summary>바탕 화면.</summary>
    string Desktop { get; }

    /// <summary>문서 폴더.</summary>
    string Documents { get; }

    /// <summary>%LOCALAPPDATA% (앱 설치 기본 위치).</summary>
    string LocalAppData { get; }

    /// <summary>%VAR% 형태를 실제 경로로 펼친다. `~` 도 홈으로 바꾼다.</summary>
    string Expand(string path);
}

/// <summary>실제 환경 경로.</summary>
public sealed class SystemPaths : ISystemPaths
{
    private readonly Lazy<string> _dataDir;

    public SystemPaths()
    {
        _dataDir = new Lazy<string>(() =>
        {
            var dir = Path.Combine(LocalAppData, "Teaveloper", "Teavel");
            Directory.CreateDirectory(dir);
            return dir;
        });
    }

    public string AppDirectory => AppContext.BaseDirectory;

    public string DataDirectory => _dataDir.Value;

    public string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public string LocalAppData
    {
        get
        {
            var p = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // 리눅스에서는 비어 있을 수 있다 — 홈 아래로 떨군다.
            return string.IsNullOrEmpty(p) ? Path.Combine(UserProfile, ".local", "share") : p;
        }
    }

    public string Expand(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded.StartsWith('~'))
            expanded = UserProfile + expanded[1..];
        return expanded;
    }
}
