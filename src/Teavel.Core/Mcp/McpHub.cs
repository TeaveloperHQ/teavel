using Teavel.Apps;

namespace Teavel.Mcp;

/// <summary>연결된 앱 하나와 그 도구들.</summary>
/// <param name="App">어느 teaveloper 앱인지.</param>
/// <param name="Client">그 앱과의 연결.</param>
/// <param name="Tools">그 앱이 제공하는 도구들.</param>
public sealed record ConnectedApp(TeaveloperApp App, McpClient Client, IReadOnlyList<McpTool> Tools);

/// <summary>어느 앱에 왜 못 붙었는지.</summary>
public sealed record McpConnectionFailure(TeaveloperApp App, string Reason);

/// <summary>
/// 설치된 teaveloper 앱들의 MCP 서버에 붙어, 그 도구들을 한자리에 모은다.
///
/// 앱에 MCP 를 순차적으로 붙일 계획이므로 여기서는 아무 앱도 특별 취급하지 않는다 —
/// 카탈로그에 mcp 가 선언돼 있고 실제로 설치돼 있으면 붙고, 아니면 조용히 건너뛴다.
/// 한 앱이 안 붙어도 나머지는 그대로 쓸 수 있어야 한다.
/// </summary>
public sealed class McpHub : IAsyncDisposable
{
    private readonly AppCatalog _catalog;
    private readonly AppInstaller _installer;
    private readonly List<ConnectedApp> _connected = new();
    private readonly List<McpConnectionFailure> _failures = new();

    public McpHub(AppCatalog catalog, AppInstaller installer)
    {
        _catalog = catalog;
        _installer = installer;
    }

    /// <summary>붙는 데 성공한 앱들.</summary>
    public IReadOnlyList<ConnectedApp> Connected => _connected;

    /// <summary>붙지 못한 앱들과 그 이유.</summary>
    public IReadOnlyList<McpConnectionFailure> Failures => _failures;

    /// <summary>모든 앱의 도구를 "앱id.도구이름" 형태로 펼쳐 돌려준다.</summary>
    public IEnumerable<(ConnectedApp Owner, McpTool Tool, string QualifiedName)> AllTools
        => _connected.SelectMany(c => c.Tools.Select(t => (c, t, $"{c.App.Id}.{t.Name}")));

    /// <summary>카탈로그에서 MCP 를 선언한 앱들 중 설치된 것에 모두 붙어 본다.</summary>
    public async Task ConnectAllAsync(CancellationToken ct = default)
    {
        foreach (var app in _catalog.WithMcp)
        {
            if (!_installer.IsInstalled(app))
                continue;   // 안 깔린 앱은 실패가 아니다 — 조용히 넘어간다

            var spec = app.Mcp!;

            if (spec.MinVersion is { } min && _installer.InstalledVersion(app) is { } have)
            {
                if (CompareVersions(have, min) < 0)
                {
                    _failures.Add(new McpConnectionFailure(app,
                        $"설치된 버전({have})이 낮아 연결하지 않았습니다. {min} 이상이 필요합니다."));
                    continue;
                }
            }

            // command 를 비워 두면 앱 자신이 서버다(대개 --mcp 인자로).
            var command = string.IsNullOrWhiteSpace(spec.Command)
                ? _installer.ExePath(app)
                : spec.Command!;

            try
            {
                var client = await McpClient.ConnectAsync(
                    app.Name, command, spec.Args ?? Array.Empty<string>(), ct).ConfigureAwait(false);

                var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);
                _connected.Add(new ConnectedApp(app, client, tools));
            }
            catch (McpException ex)
            {
                _failures.Add(new McpConnectionFailure(app, ex.Message));
            }
            catch (Exception ex)
            {
                _failures.Add(new McpConnectionFailure(app, ex.Message));
            }
        }
    }

    /// <summary>"앱id.도구이름" 으로 도구를 부른다.</summary>
    public async Task<string> CallAsync(
        string qualifiedName, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var match = AllTools.FirstOrDefault(x =>
            string.Equals(x.QualifiedName, qualifiedName, StringComparison.OrdinalIgnoreCase));

        if (match.Owner is null)
            throw new McpException($"'{qualifiedName}' 이라는 도구를 찾지 못했습니다.");

        return await match.Owner.Client.CallToolAsync(match.Tool.Name, arguments, ct).ConfigureAwait(false);
    }

    /// <summary>"1.2.0" 같은 버전 문자열 비교. 숫자가 아닌 부분은 무시한다.</summary>
    internal static int CompareVersions(string a, string b)
    {
        static int[] Parse(string s) => s
            .Split('.', ',', '-', '+')
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0)
            .ToArray();

        var x = Parse(a);
        var y = Parse(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var xi = i < x.Length ? x[i] : 0;
            var yi = i < y.Length ? y[i] : 0;
            if (xi != yi) return xi.CompareTo(yi);
        }
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _connected)
            await c.Client.DisposeAsync().ConfigureAwait(false);
        _connected.Clear();
    }
}
