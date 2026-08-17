using Teavel.Platform;

namespace Teavel.Setup;

/// <summary>실행 파일을 찾아 본 결과.</summary>
/// <param name="Path">찾은 파일. 못 찾았거나 여럿이면 빈 문자열.</param>
/// <param name="Candidates">그 폴더에서 보이는 후보들. 여럿일 때 골라 달라고 보여 준다.</param>
/// <param name="How">어떻게 찾았는지 — 화면에 그대로 나간다.</param>
public sealed record ExeLookup(string Path, IReadOnlyList<string> Candidates, string How);

/// <summary>
/// Teavel 실행 파일이 어디 있는지 찾는다.
///
/// <para>
/// 예전에는 <c>teavel.exe</c> 라는 이름을 박아 두고 찾았다. 그런데 포털이 배포하는 파일에는
/// <b>판 번호가 붙는다</b> — <c>teavel-0.1.0.exe</c> 처럼. 그래서 교사가 받아서 처음 실행하고
/// "등록할까요?" 에 '예' 를 누르면 이렇게 끝났다.
/// </para>
/// <code>
///   ✗ 등록 — 실행 파일이 있는 폴더를 찾지 못했습니다.
///         확인한 곳: C:\Users\aramo\Downloads
/// </code>
/// <para>
/// 처음 하는 일이 바로 실패하는 자리였다.
/// </para>
/// <para>
/// 지금은 세 걸음으로 찾는다.
/// </para>
/// <list type="number">
/// <item><b>지금 돌고 있는 그 파일</b> — 가장 확실하다. 짐작할 것이 없다.</item>
/// <item>그게 안 되면 폴더에서 <c>teavel*.exe</c> 를 찾는다.</item>
/// <item>없거나 여럿이면 <b>사람에게 묻는다.</b> 넘겨짚어 엉뚱한 파일을 등록하면
///       나중에 그 파일을 지웠을 때 왜 안 되는지 알 수 없다.</item>
/// </list>
/// </summary>
public static class TeavelExe
{
    /// <summary>이 이름으로 시작하는 것을 우리 것으로 본다.</summary>
    public const string NamePrefix = "teavel";

    /// <summary>
    /// 지금 돌고 있는 실행 파일. 개발 중(<c>dotnet run</c>)이거나 알 수 없으면 null.
    /// </summary>
    /// <remarks>
    /// 단일 파일로 묶은 배포본에서도 실제 <c>.exe</c> 경로를 준다.
    /// <c>dotnet teavel.dll</c> 로 돌리면 <c>dotnet</c> 이 잡히므로 그때는 쓰지 않는다.
    /// </remarks>
    public static string? Running
    {
        get
        {
            try
            {
                var p = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) return null;

                var name = Path.GetFileNameWithoutExtension(p);
                return name.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase) ? p : null;
            }
            catch { return null; }
        }
    }

    /// <summary>폴더 안의 <c>teavel*.exe</c> 들. 이름순.</summary>
    public static IReadOnlyList<string> Candidates(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return Array.Empty<string>();

            var found = Directory.EnumerateFiles(directory, NamePrefix + "*.exe")
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            // 리눅스·개발 환경에서는 확장자가 없다.
            if (found.Count == 0 && !OperatingSystem.IsWindows())
            {
                var bare = Path.Combine(directory, NamePrefix);
                if (File.Exists(bare)) found.Add(bare);
            }

            return found;
        }
        catch (IOException) { return Array.Empty<string>(); }
    }

    /// <summary>세 걸음으로 찾는다. 사람에게 물어야 하면 <see cref="ExeLookup.Path"/> 가 비어 있다.</summary>
    public static ExeLookup Find(ISystemPaths paths)
    {
        if (Running is { Length: > 0 } running)
            return new ExeLookup(running, new[] { running }, "지금 돌고 있는 파일입니다");

        var dir = paths.AppDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var candidates = Candidates(dir);

        return candidates.Count switch
        {
            1 => new ExeLookup(candidates[0], candidates, $"{dir} 에서 찾았습니다"),
            0 => new ExeLookup("", candidates, $"{dir} 에서 찾지 못했습니다"),
            _ => new ExeLookup("", candidates, $"{dir} 에 {candidates.Count}개가 있습니다"),
        };
    }

    /// <summary>
    /// 여기에 두면 곤란한 자리인지. 곤란하면 까닭, 아니면 빈 문자열.
    /// </summary>
    /// <remarks>
    /// 받은 자리 그대로 등록하면 나중에 그 폴더를 치울 때 조용히 깨진다.
    /// 특히 <b>다운로드 폴더</b>는 교사가 주기적으로 비우는 곳이다.
    /// 옮기라고 강요하지는 않는다 — 알려 주고 정하게 한다.
    /// </remarks>
    public static string RiskyLocation(string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";
            var name = Path.GetFileName(dir);

            if (name.Equals("Downloads", StringComparison.OrdinalIgnoreCase)
                || name.Equals("다운로드", StringComparison.Ordinal))
                return "다운로드 폴더는 나중에 비우시는 일이 많습니다. 그때 Teavel 이 사라집니다.";

            var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
            if (dir.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                return "임시 폴더는 Windows 가 지웁니다.";

            return "";
        }
        catch { return ""; }
    }
}
