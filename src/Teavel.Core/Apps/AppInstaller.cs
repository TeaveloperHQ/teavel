using System.IO.Compression;
using Teavel.Platform;
using Teavel.Setup;

namespace Teavel.Apps;

/// <summary>teaveloper 앱을 확인하고 설치한다.</summary>
public sealed class AppInstaller
{
    private readonly IProcessRunner _proc;
    private readonly ISystemPaths _paths;
    private readonly Func<HttpClient> _httpFactory;

    public AppInstaller(IProcessRunner proc, ISystemPaths paths, Func<HttpClient>? httpFactory = null)
    {
        _proc = proc;
        _paths = paths;
        _httpFactory = httpFactory ?? (() => new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
    }

    /// <summary>앱 실행 파일이 있어야 할 전체 경로.</summary>
    public string ExePath(TeaveloperApp app)
        => Path.Combine(_paths.Expand(app.Install.InstallDir), app.Install.Exe);

    /// <summary>설치돼 있는지 — 실행 파일이 실제로 있는지로 판단한다.</summary>
    public bool IsInstalled(TeaveloperApp app)
    {
        try { return File.Exists(ExePath(app)); } catch { return false; }
    }

    /// <summary>설치된 앱의 파일 버전. 알 수 없으면 null.</summary>
    public string? InstalledVersion(TeaveloperApp app)
    {
        try
        {
            var path = ExePath(app);
            if (!File.Exists(path)) return null;
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch { return null; }
    }

    /// <summary>앱 상태를 진단한다.</summary>
    public CheckResult Check(TeaveloperApp app)
    {
        if (!IsInstalled(app))
            return CheckResult.NeedsFix($"{app.Name} 이(가) 설치돼 있지 않습니다.", app.Summary);

        var lines = new List<string> { $"위치: {ExePath(app)}" };
        if (InstalledVersion(app) is { } v) lines.Add($"버전: {v}");
        if (app.ProvidesMcp) lines.Add("이 앱은 Teavel 에서 바로 부릴 수 있습니다(MCP).");
        lines.AddRange(app.Hints);

        return CheckResult.Ok($"{app.Name} 이(가) 설치돼 있습니다.", lines.ToArray());
    }

    /// <summary>앱을 설치한다. 방식은 카탈로그의 install.kind 가 정한다.</summary>
    public async Task<FixResult> InstallAsync(TeaveloperApp app, CancellationToken ct = default)
    {
        if (IsInstalled(app))
            return FixResult.AlreadyOk($"{app.Name} 은(는) 이미 설치돼 있습니다.");

        return app.Install.Kind.ToLowerInvariant() switch
        {
            "portal" => InstallFromPortal(app),
            "winget" => await InstallFromWingetAsync(app, ct).ConfigureAwait(false),
            "zip"    => await InstallFromZipAsync(app, ct).ConfigureAwait(false),
            _ => FixResult.Failed(
                     $"{app.Name} 의 설치 방식('{app.Install.Kind}')을 알지 못합니다.",
                     "Teavel 을 최신 버전으로 올리면 지원될 수 있습니다."),
        };
    }

    private FixResult InstallFromPortal(TeaveloperApp app)
    {
        var page = app.Install.PortalPage;
        if (string.IsNullOrWhiteSpace(page))
            return FixResult.Failed($"{app.Name} 의 안내 페이지 주소가 카탈로그에 없습니다.");

        // 내려받기는 로그인이 필요해 대신 해 줄 수 없다 — 페이지만 열고 순서를 알려 준다.
        _proc.Launch(page);

        var steps = new List<string>
        {
            "① 포털에 로그인합니다",
            $"② {app.Name} 을(를) 내려받습니다",
            $"③ 압축을 풀어 다음 폴더에 둡니다: {_paths.Expand(app.Install.InstallDir)}",
        };
        if (app.Hints.Count > 0)
        {
            steps.Add("");
            steps.AddRange(app.Hints);
        }
        steps.Add("");
        steps.Add("마친 뒤 '점검' 을 다시 실행하면 확인됩니다.");

        return FixResult.Manual($"{app.Name} 내려받기 페이지를 띄웠습니다.", steps.ToArray());
    }

    private async Task<FixResult> InstallFromWingetAsync(TeaveloperApp app, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.Install.PackageId))
            return FixResult.Failed($"{app.Name} 의 winget 패키지 id 가 카탈로그에 없습니다.");

        if (!_proc.Exists("winget"))
            return FixResult.Failed(
                "winget(앱 설치 도구)이 없어 자동으로 설치할 수 없습니다.",
                "Microsoft Store 에서 '앱 설치 관리자'를 설치하면 winget 이 생깁니다.");

        var res = await _proc.RunAsync("winget", new[]
        {
            "install", "--id", app.Install.PackageId!, "--exact", "--silent",
            "--accept-package-agreements", "--accept-source-agreements",
        }, timeout: TimeSpan.FromMinutes(20), ct: ct).ConfigureAwait(false);

        return res.Ok
            ? FixResult.Fixed($"{app.Name} 을(를) 설치했습니다.")
            : FixResult.Failed($"{app.Name} 설치에 실패했습니다.", res.FailureSummary);
    }

    private async Task<FixResult> InstallFromZipAsync(TeaveloperApp app, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.Install.Url))
            return FixResult.Failed($"{app.Name} 의 내려받기 주소가 카탈로그에 없습니다.");

        var target = _paths.Expand(app.Install.InstallDir);
        var temp = Path.Combine(Path.GetTempPath(), $"teavel-{app.Id}-{Guid.NewGuid():N}.zip");

        try
        {
            using (var http = _httpFactory())
            await using (var src = await http.GetStreamAsync(app.Install.Url!, ct).ConfigureAwait(false))
            await using (var dst = File.Create(temp))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            Directory.CreateDirectory(target);
            ZipFile.ExtractToDirectory(temp, target, overwriteFiles: true);
        }
        catch (HttpRequestException ex)
        {
            return FixResult.Failed(
                $"{app.Name} 을(를) 내려받지 못했습니다.",
                ex.Message,
                "인터넷 연결과 학교 방화벽을 확인해 주세요.");
        }
        catch (Exception ex)
        {
            return FixResult.Failed($"{app.Name} 설치 중 문제가 생겼습니다.", ex.Message);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }

        return IsInstalled(app)
            ? FixResult.Fixed($"{app.Name} 을(를) 설치했습니다. ({target})")
            : FixResult.Failed(
                $"{app.Name} 을(를) 풀었지만 실행 파일을 찾지 못했습니다.",
                $"기대한 위치: {ExePath(app)}");
    }
}
