using System.IO.Compression;
using System.Net.Http.Json;
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
            return CheckResult.NeedsFix("아직 설치돼 있지 않습니다.", app.Summary);

        var lines = new List<string> { $"위치: {ExePath(app)}" };
        if (InstalledVersion(app) is { } v) lines.Add($"버전: {v}");
        if (app.ProvidesMcp) lines.Add("이 앱은 Teavel 에서 바로 부릴 수 있습니다(MCP).");
        lines.AddRange(app.Hints);

        return CheckResult.Ok("설치돼 있습니다.", lines.ToArray());
    }

    /// <summary>앱을 설치한다. 방식은 카탈로그의 install.kind 가 정한다.</summary>
    public async Task<FixResult> InstallAsync(TeaveloperApp app, CancellationToken ct = default)
    {
        if (IsInstalled(app))
            return FixResult.AlreadyOk("이미 설치돼 있습니다.");

        return app.Install.Kind.ToLowerInvariant() switch
        {
            "portal"   => InstallFromPortal(app),
            "winget"   => await InstallFromWingetAsync(app, ct).ConfigureAwait(false),
            "zip"      => await InstallFromZipAsync(app, ct).ConfigureAwait(false),
            "manifest" => await InstallFromManifestAsync(app, ct).ConfigureAwait(false),
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

    /// <summary>
    /// 배포 매니페스트를 읽어 설치한다 — zip 과 달리 <b>내려받은 것을 sha256 으로 검증</b>한다.
    /// </summary>
    /// <remarks>
    /// 앱에 코드 서명을 하지 않는 것이 방침이라, 이 해시가 "받은 파일이 포털이 낸 그 파일인지"
    /// 를 확인할 유일한 수단이다. 검증에 실패하면 <b>설치하지 않고 멈춘다</b> — 교사 PC 에
    /// 정체 모를 exe 를 푸는 것보다 설치가 안 되는 편이 낫다.
    /// </remarks>
    private async Task<FixResult> InstallFromManifestAsync(TeaveloperApp app, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.Install.ManifestUrl))
            return FixResult.Failed($"{app.Name} 의 배포 정보 주소가 카탈로그에 없습니다.");

        RunnerManifest? manifest;
        try
        {
            using var http = _httpFactory();
            manifest = await http.GetFromJsonAsync<RunnerManifest>(app.Install.ManifestUrl!, ct)
                                 .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return FixResult.Failed(
                $"{app.Name} 의 최신 버전 정보를 가져오지 못했습니다.",
                ex.Message,
                "인터넷 연결과 학교 방화벽을 확인해 주세요.");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Url) || string.IsNullOrWhiteSpace(manifest.Sha256))
            return FixResult.Failed($"{app.Name} 의 배포 정보가 올바르지 않습니다.");

        return await DownloadAndExtractAsync(app, manifest.Url!, manifest.Sha256, manifest.Version, ct)
                     .ConfigureAwait(false);
    }

    private async Task<FixResult> InstallFromZipAsync(TeaveloperApp app, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.Install.Url))
            return FixResult.Failed($"{app.Name} 의 내려받기 주소가 카탈로그에 없습니다.");

        // kind=zip 은 해시가 없다 — 검증 없이 받는다(옛 카탈로그 호환). 새 앱은 manifest 를 쓴다.
        return await DownloadAndExtractAsync(app, app.Install.Url!, expectedSha256: null, version: null, ct)
                     .ConfigureAwait(false);
    }

    private async Task<FixResult> DownloadAndExtractAsync(
        TeaveloperApp app, string url, string? expectedSha256, string? version, CancellationToken ct)
    {
        var target = _paths.Expand(app.Install.InstallDir);
        var temp = Path.Combine(Path.GetTempPath(), $"teavel-{app.Id}-{Guid.NewGuid():N}.zip");

        try
        {
            using (var http = _httpFactory())
            await using (var src = await http.GetStreamAsync(url, ct).ConfigureAwait(false))
            await using (var dst = File.Create(temp))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            if (expectedSha256 is { Length: > 0 })
            {
                var actual = await Sha256OfAsync(temp, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    return FixResult.Failed(
                        $"{app.Name} 을(를) 받았지만 파일이 손상됐거나 바뀌었습니다. 설치하지 않았습니다.",
                        "받는 도중 끊겼거나, 중간에서 파일이 바뀌었을 수 있습니다.",
                        "잠시 뒤 다시 시도해 보시고, 계속 그러면 학교 전산 담당 선생님께 알려 주세요.");
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

        if (!IsInstalled(app))
            return FixResult.Failed(
                $"{app.Name} 을(를) 풀었지만 실행 파일을 찾지 못했습니다.",
                $"기대한 위치: {ExePath(app)}");

        var what = version is { Length: > 0 } ? $"{app.Name} {version}" : app.Name;
        return FixResult.Fixed($"{what} 을(를) 설치했습니다. ({target})");
    }

    private static async Task<string> Sha256OfAsync(string path, CancellationToken ct)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        await using var fs = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>배포 매니페스트 — teaveloper-runner PORTAL_INTEGRATION.md §2.1.</summary>
    private sealed record RunnerManifest(
        string? Version,
        string? Url,
        string? Sha256,
        string? ExePath);
}
