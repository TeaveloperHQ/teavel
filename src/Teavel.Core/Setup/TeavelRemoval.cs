using System.Diagnostics;
using Teavel.Intent;
using Teavel.Platform;

namespace Teavel.Setup;

/// <summary>지울 것 하나 — 무엇이고, 어디에 있고, 얼마나 큰지.</summary>
/// <param name="Title">교사에게 보여 줄 이름.</param>
/// <param name="Path">지울 자리.</param>
/// <param name="Bytes">크기. 잴 수 없으면 0.</param>
public sealed record RemovalItem(string Title, string Path, long Bytes)
{
    /// <summary>"1,066MB" 처럼 읽기 좋은 크기. 아주 작으면 빈 문자열.</summary>
    public string Size => Bytes < 1024 * 1024 ? "" : $"{Bytes / 1024 / 1024:N0}MB";
}

/// <summary>
/// Teavel 이 이 PC 에 남긴 것을 찾아서 지운다.
///
/// <para>
/// 원칙은 하나다 — <b>무엇을 지울지 먼저 보여 주고 지운다.</b> 교사 PC 에서 파일을
/// 지우는 일이라, 어디를 건드릴지 화면에 다 적지 않고 하는 것은 위험하다.
/// 특히 exe 는 선생님이 받아 둔 폴더에 다른 파일과 섞여 있을 수 있어,
/// 폴더를 통째로 지우지 않고 <b>우리가 놓은 것만</b> 골라 지운다.
/// </para>
/// </summary>
public static class TeavelRemoval
{
    /// <summary>배포본에서 exe 옆에 함께 놓이는 폴더들.</summary>
    private static readonly string[] AppFolders = { "scripts", "catalog" };

    /// <summary>내려받아 둔 것들 — 크고, 다시 받으려면 오래 걸린다. 따로 여쭙는다.</summary>
    public static IReadOnlyList<RemovalItem> Downloads(ISystemPaths paths)
    {
        var found = new List<RemovalItem>();

        var models = Path.Combine(paths.DataDirectory, "models");
        if (Directory.Exists(models))
            found.Add(new RemovalItem("언어 모델", models, SizeOf(models)));

        var kiwi = KiwiAssets.DefaultDirectory(paths);
        if (Directory.Exists(kiwi))
            found.Add(new RemovalItem("형태소 분석기", kiwi, SizeOf(kiwi)));

        return found;
    }

    /// <summary>설정·기록 — 작고, 남겨 둘 까닭이 없다.</summary>
    public static IReadOnlyList<RemovalItem> Settings(ISystemPaths paths)
    {
        var found = new List<RemovalItem>();
        var dir = paths.DataDirectory;

        try
        {
            if (!Directory.Exists(dir)) return found;

            foreach (var f in Directory.EnumerateFiles(dir))
                found.Add(new RemovalItem(Path.GetFileName(f), f, SizeOf(f)));

            // models·kiwi_model 은 Downloads 가 따로 다룬다.
            var keep = Downloads(paths).Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (!keep.Contains(sub))
                    found.Add(new RemovalItem(Path.GetFileName(sub), sub, SizeOf(sub)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return found;
    }

    /// <summary>프로그램 파일 — exe 와 함께 놓인 scripts·catalog.</summary>
    public static IReadOnlyList<RemovalItem> Program(string exePath)
    {
        var found = new List<RemovalItem>();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return found;

        found.Add(new RemovalItem(Path.GetFileName(exePath), exePath, SizeOf(exePath)));

        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath));
        if (dir is null) return found;

        foreach (var name in AppFolders)
        {
            var sub = Path.Combine(dir, name);
            if (Directory.Exists(sub)) found.Add(new RemovalItem(name + "\\", sub, SizeOf(sub)));
        }

        return found;
    }

    /// <summary>지운다. 지운 개수와 못 지운 것들을 돌려준다.</summary>
    public static (int Removed, IReadOnlyList<string> Failed) Delete(IEnumerable<RemovalItem> items)
    {
        var removed = 0;
        var failed = new List<string>();

        foreach (var item in items)
        {
            try
            {
                if (Directory.Exists(item.Path)) Directory.Delete(item.Path, recursive: true);
                else if (File.Exists(item.Path)) File.Delete(item.Path);
                else continue;

                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{item.Path} — {ex.Message}");
            }
        }

        return (removed, failed);
    }

    /// <summary>
    /// 우리가 끝난 뒤에 프로그램 파일을 지우도록 Windows 에 시킨다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>돌고 있는 실행 파일은 자기 자신을 지울 수 없다.</b> Windows 가 그 파일을
    /// 붙잡고 있기 때문이다. 그래서 잠깐 기다렸다 지우는 일을 따로 띄워 놓고 우리는 끝난다.
    /// </para>
    /// <para>
    /// 기다리는 데 <c>timeout</c> 이 아니라 <c>ping</c> 을 쓴다 — <c>timeout</c> 은 콘솔
    /// 입력을 요구해서, 창 없이 띄우면 <b>바로 실패하고 지우지 않는다.</b>
    /// </para>
    /// </remarks>
    /// <param name="emptyFoldersToRemove">
    /// 비어 있으면 함께 치울 폴더들. <b>비었을 때만</b> 지워진다.
    /// </param>
    /// <returns>시켰으면 true.</returns>
    public static bool ScheduleDelete(
        IEnumerable<RemovalItem> items, IEnumerable<string>? emptyFoldersToRemove = null)
    {
        if (!OperatingSystem.IsWindows()) return false;

        var steps = new List<string> { "ping 127.0.0.1 -n 3 >nul" };

        foreach (var item in items)
        {
            if (Directory.Exists(item.Path)) steps.Add($"rd /s /q \"{item.Path}\"");
            else if (File.Exists(item.Path)) steps.Add($"del /f /q \"{item.Path}\"");
        }

        if (steps.Count == 1) return false;   // 지울 것이 없다

        // 비어 있을 때만 지워진다(/s 를 안 붙였다). 선생님이 같은 폴더에 둔
        // 다른 파일까지 쓸어 가면 안 되므로 반드시 이 형태여야 한다.
        foreach (var folder in emptyFoldersToRemove ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(folder))
                steps.Add($"rd \"{folder.TrimEnd(Path.DirectorySeparatorChar)}\"");

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", steps))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;

            long bytes = 0;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(f).Length; } catch (IOException) { }
            }
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
