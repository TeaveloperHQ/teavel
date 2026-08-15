using System.Text.Json;
using System.Text.Json.Serialization;
using Teavel.Platform;

namespace Teavel.Apps;

/// <summary>앱을 어떻게 설치하는지.</summary>
/// <param name="Kind">portal · zip · manifest · winget 중 하나.</param>
/// <param name="PortalPage">kind=portal 일 때 열어 줄 안내 페이지.</param>
/// <param name="Url">kind=zip 일 때 내려받을 zip 주소.</param>
/// <param name="ManifestUrl">
/// kind=manifest 일 때 읽을 배포 매니페스트 주소.
/// <c>{version, url, sha256, exePath}</c> 를 준다 — zip 과 달리 <b>내려받은 것을
/// sha256 으로 검증</b>한다. 앱에 코드 서명을 하지 않으므로 이게 위변조를 확인할
/// 유일한 수단이다.
/// </param>
/// <param name="PackageId">kind=winget 일 때 패키지 id.</param>
/// <param name="InstallDir">설치 폴더(%LOCALAPPDATA% 같은 변수를 써도 된다).</param>
/// <param name="Exe">설치 폴더 안의 실행 파일 이름. 설치 여부 판단 근거이기도 하다.</param>
public sealed record AppInstall(
    string Kind,
    string? PortalPage,
    string? Url,
    string? PackageId,
    string InstallDir,
    string Exe,
    string? ManifestUrl = null);

/// <summary>
/// 앱이 제공하는 MCP 서버. 앱에 MCP 가 아직 없으면 카탈로그에서 null 이다.
/// </summary>
/// <param name="Args">서버로 띄울 때 실행 파일에 붙일 인자(예: --mcp).</param>
/// <param name="Command">
/// 띄울 실행 파일. 비우면 앱의 <see cref="AppInstall.Exe"/> 를 쓴다
/// (대개 앱 자신이 --mcp 로 서버가 된다).
/// </param>
/// <param name="MinVersion">이 버전 이상일 때만 붙인다. 비우면 버전을 따지지 않는다.</param>
public sealed record McpServerSpec(
    string? Command,
    IReadOnlyList<string>? Args,
    string? MinVersion);

/// <summary>
/// 앱을 교사 계정에 붙이는 방법(활성화). 계정 증명이 필요 없는 앱은 카탈로그에서 null 이다.
/// </summary>
/// <param name="Kind">지금은 <c>deviceFlow</c> 하나뿐.</param>
/// <param name="CodeUrl">활성화를 시작해 짧은 코드를 받아 오는 주소.</param>
/// <param name="TokenUrl">승인됐는지 물어보는 주소.</param>
/// <remarks>
/// 비밀번호는 이 경로로 오가지 않는다. 교사는 브라우저에서 승인만 하고,
/// Teavel 은 코드 한 벌과 폴링만 다룬다.
/// </remarks>
public sealed record ActivationSpec(
    string Kind,
    string CodeUrl,
    string TokenUrl)
{
    /// <summary>Device Flow 로 활성화하는 앱인지.</summary>
    public bool IsDeviceFlow => string.Equals(Kind, "deviceFlow", StringComparison.OrdinalIgnoreCase);

    /// <summary>주소가 둘 다 채워져 있는지.</summary>
    public bool IsUsable =>
        IsDeviceFlow && !string.IsNullOrWhiteSpace(CodeUrl) && !string.IsNullOrWhiteSpace(TokenUrl);
}

/// <summary>teaveloper 앱 하나.</summary>
public sealed record TeaveloperApp(
    string Id,
    string Name,
    string Summary,
    AppInstall Install,
    IReadOnlyList<string>? Notes,
    McpServerSpec? Mcp,
    ActivationSpec? Activation = null)
{
    public IReadOnlyList<string> Hints => Notes ?? Array.Empty<string>();

    /// <summary>이 앱이 MCP 서버를 제공하는지.</summary>
    public bool ProvidesMcp => Mcp is not null;

    /// <summary>설치만으로는 부족하고 교사 계정에 붙여야 쓸 수 있는 앱인지.</summary>
    public bool NeedsActivation => Activation is not null;
}

/// <summary>
/// catalog/apps.json 을 읽어 앱 목록을 준다.
///
/// 앱에 MCP 를 순차적으로 붙일 계획이므로, 새 앱이 MCP 를 갖췄을 때
/// <b>이 JSON 만 갱신하면</b> Teavel 은 그대로 두고도 연결된다.
/// </summary>
public sealed class AppCatalog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private AppCatalog(IReadOnlyList<TeaveloperApp> apps, string source)
    {
        Apps = apps;
        Source = source;
    }

    /// <summary>선언된 앱들.</summary>
    public IReadOnlyList<TeaveloperApp> Apps { get; }

    /// <summary>어느 파일에서 읽었는지(문제를 알릴 때 쓴다).</summary>
    public string Source { get; }

    public TeaveloperApp? Find(string id)
        => Apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>MCP 서버를 제공한다고 선언된 앱들.</summary>
    public IEnumerable<TeaveloperApp> WithMcp => Apps.Where(a => a.ProvidesMcp);

    /// <summary>exe 옆의 catalog\apps.json 을 읽는다. 파일이 없으면 빈 카탈로그.</summary>
    public static AppCatalog Load(ISystemPaths paths)
    {
        var path = Path.Combine(paths.AppDirectory, "catalog", "apps.json");
        return LoadFrom(path);
    }

    /// <summary>지정한 경로에서 읽는다.</summary>
    public static AppCatalog LoadFrom(string path)
    {
        if (!File.Exists(path))
            return new AppCatalog(Array.Empty<TeaveloperApp>(), path);

        var dto = JsonSerializer.Deserialize<CatalogDto>(File.ReadAllText(path), JsonOpts)
                  ?? throw new InvalidDataException($"{path} 를 읽지 못했습니다.");

        var apps = new List<TeaveloperApp>();
        foreach (var a in dto.Apps ?? new List<AppDto>())
        {
            // 최소한의 필드가 없으면 그 항목만 건너뛴다 — 카탈로그 하나 때문에 전체가 죽지 않게.
            if (string.IsNullOrWhiteSpace(a.Id) || a.Install is null || string.IsNullOrWhiteSpace(a.Install.Exe))
                continue;

            apps.Add(new TeaveloperApp(
                a.Id!,
                a.Name ?? a.Id!,
                a.Summary ?? "",
                new AppInstall(
                    a.Install.Kind ?? "portal",
                    a.Install.PortalPage,
                    a.Install.Url,
                    a.Install.PackageId,
                    a.Install.InstallDir ?? "",
                    a.Install.Exe!,
                    a.Install.ManifestUrl),
                a.Notes,
                a.Mcp is null ? null : new McpServerSpec(a.Mcp.Command, a.Mcp.Args, a.Mcp.MinVersion),
                a.Activation is null
                    ? null
                    : new ActivationSpec(
                        a.Activation.Kind ?? "deviceFlow",
                        a.Activation.CodeUrl ?? "",
                        a.Activation.TokenUrl ?? "")));
        }

        return new AppCatalog(apps, path);
    }

    // JSON 모양 그대로의 중간 타입. 모르는 필드는 무시되므로 카탈로그를 나중에 넓혀도 옛 Teavel 이 죽지 않는다.
    private sealed class CatalogDto
    {
        public int SchemaVersion { get; set; }
        public List<AppDto>? Apps { get; set; }
    }

    private sealed class AppDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Summary { get; set; }
        public InstallDto? Install { get; set; }
        public List<string>? Notes { get; set; }
        public McpDto? Mcp { get; set; }
        public ActivationDto? Activation { get; set; }
    }

    private sealed class ActivationDto
    {
        public string? Kind { get; set; }
        public string? CodeUrl { get; set; }
        public string? TokenUrl { get; set; }
    }

    private sealed class InstallDto
    {
        public string? Kind { get; set; }
        public string? PortalPage { get; set; }
        public string? Url { get; set; }
        public string? PackageId { get; set; }
        public string? InstallDir { get; set; }
        public string? Exe { get; set; }
        public string? ManifestUrl { get; set; }
    }

    private sealed class McpDto
    {
        public string? Command { get; set; }
        public List<string>? Args { get; set; }
        public string? MinVersion { get; set; }
    }
}
