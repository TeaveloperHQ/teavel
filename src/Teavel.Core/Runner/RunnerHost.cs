using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Teavel.Platform;

namespace Teavel.Runner;

/// <summary>
/// 러너의 로컬 관리 API(<c>/_admin/api/status</c>)가 돌려주는 상태.
/// 이 주소는 <b>127.0.0.1 전용</b>이라 인증이 없다(터널로 들어온 요청에는 404 를 준다).
/// </summary>
public sealed record RunnerStatus
{
    /// <summary>사람에게 보여줄 상태 문구. 러너가 한국어로 준다("연결됨", "연결 중"…).</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "";

    /// <summary>
    /// 기계용 상태 코드("connected" 등). 러너가 아직 안 줄 수 있으므로 null 을 허용한다.
    /// </summary>
    [JsonPropertyName("code")] public string? Code { get; init; }

    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("publicUrl")] public string? PublicUrl { get; init; }
    [JsonPropertyName("slug")] public string? Slug { get; init; }
    [JsonPropertyName("localPort")] public int LocalPort { get; init; }

    /// <summary>
    /// 터널이 실제로 붙었는지.
    /// </summary>
    /// <remarks>
    /// 러너가 <c>code</c> 를 주면 그걸 쓰고, 없으면 한국어 <c>state</c> 로 되돌아간다.
    /// 문구 비교는 러너가 표현을 바꾸면 깨지므로 임시 방편이다 — 러너에 기계용 코드가
    /// 생기면 이 되돌림은 지워도 된다.
    /// </remarks>
    [JsonIgnore]
    public bool IsConnected => Code is { Length: > 0 } c
        ? string.Equals(c, "connected", StringComparison.OrdinalIgnoreCase)
        : State.Trim() == "연결됨";

    /// <summary>토큰이 무효라 재시도해도 소용없는 상태인지(재활성화가 필요하다).</summary>
    [JsonIgnore]
    public bool IsTokenRejected => Code is { Length: > 0 } c
        ? string.Equals(c, "forbidden", StringComparison.OrdinalIgnoreCase)
        : State.Trim() == "토큰 무효";
}

/// <summary>
/// 교사 PC 에서 러너를 다루는 일들 — 설정 파일, 포트, 자동 실행, 상태 확인.
///
/// 러너를 고치지 않고 이미 있는 접점만 쓴다:
///   · <c>config.json</c> 은 exe 와 같은 폴더에서 읽힌다
///   · 자동 실행은 작업 스케줄러 이름 <c>TeaveloperRunner</c> 하나로 통한다
///   · 연결 여부는 로컬 <c>/_admin/api/status</c> 로 확인한다
/// </summary>
public static class RunnerHost
{
    /// <summary>
    /// 로그온 자동 실행에 쓰는 작업 이름.
    /// <b>러너 트레이의 자동시작 토글이 같은 이름을 본다</b> — 바꾸면 교사가 트레이에서
    /// 끄고 켜는 것이 우리가 만든 작업과 어긋난다.
    /// </summary>
    public const string AutostartTaskName = "TeaveloperRunner";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    // ─────────────────────────── config.json ───────────────────────────

    /// <summary>exe 경로로부터 config.json 이 있어야 할 자리를 만든다.</summary>
    public static string ConfigPath(string exePath)
        => Path.Combine(Path.GetDirectoryName(exePath) ?? ".", "config.json");

    /// <summary>설정을 읽는다. 없거나 깨졌으면 null.</summary>
    public static RunnerConfig? ReadConfig(string exePath)
    {
        try
        {
            var path = ConfigPath(exePath);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<RunnerConfig>(File.ReadAllText(path), Json);
        }
        catch { return null; }
    }

    /// <summary>
    /// 설정을 exe 옆에 쓴다.
    /// </summary>
    /// <remarks>
    /// 토큰이 담기지만 별도 ACL 을 걸지 않는다 — 설치 위치가 <c>%LOCALAPPDATA%</c> 아래라
    /// 이미 그 사용자만 접근할 수 있다. 거기가 아닌 곳에 설치했다면 그 폴더의 권한을 따른다.
    /// </remarks>
    public static void WriteConfig(string exePath, RunnerConfig config)
    {
        var path = ConfigPath(exePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, Json));
    }

    // ─────────────────────────────── 포트 ───────────────────────────────

    /// <summary>
    /// 쓸 수 있는 로컬 포트를 고른다. 포털이 준 값이 비어 있으면 그대로 쓰고,
    /// 이미 누가 쓰고 있으면 빈 포트를 새로 받는다.
    /// </summary>
    /// <remarks>
    /// 포털은 교사 PC 에서 어느 포트가 비었는지 알 수 없다. 그래서 이 판단은 여기서 한다 —
    /// 러너는 포트를 못 열면 오류 상자를 띄우고 그냥 종료해 버리는데, 교사가 할 수 있는 일이
    /// config.json 을 메모장으로 여는 것뿐이라 그 지점에서 대부분 포기한다.
    /// </remarks>
    public static int PickLocalPort(int preferred)
    {
        if (preferred is > 0 and <= 65535 && IsPortFree(preferred)) return preferred;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally { listener.Stop(); }
    }

    private static bool IsPortFree(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException) { return false; }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    // ─────────────────────────────── 상태 ───────────────────────────────

    /// <summary>러너에게 지금 상태를 묻는다. 안 떠 있으면 null.</summary>
    public static async Task<RunnerStatus?> QueryStatusAsync(
        int localPort, HttpClient? http = null, CancellationToken ct = default)
    {
        var own = http is null;
        var client = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            return await client.GetFromJsonAsync<RunnerStatus>(
                $"http://127.0.0.1:{localPort}/_admin/api/status", Json, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }   // 안 떠 있는 것과 오류를 구분할 필요가 없다 — 둘 다 "아직"이다
        finally { if (own) client.Dispose(); }
    }

    /// <summary>연결될 때까지 기다린다. 시간 안에 못 붙으면 마지막으로 본 상태를 돌려준다.</summary>
    public static async Task<RunnerStatus?> WaitUntilConnectedAsync(
        int localPort, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        RunnerStatus? last = null;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            last = await QueryStatusAsync(localPort, http, ct).ConfigureAwait(false) ?? last;
            if (last is { IsConnected: true }) return last;

            // 토큰이 무효면 기다려도 달라지지 않는다.
            if (last is { IsTokenRejected: true }) return last;

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
        return last;
    }

    // ───────────────────────────── 자동 실행 ─────────────────────────────

    /// <summary>로그온 자동 실행이 등록돼 있는지.</summary>
    public static async Task<bool> IsAutostartEnabledAsync(IProcessRunner proc, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return false;

        var res = await proc.RunAsync("schtasks",
            new[] { "/Query", "/TN", AutostartTaskName },
            timeout: TimeSpan.FromSeconds(20), ct: ct).ConfigureAwait(false);
        return res.Ok;
    }

    /// <summary>로그온할 때 러너가 켜지도록 등록한다. 관리자 권한이 필요 없다.</summary>
    public static async Task<bool> EnableAutostartAsync(
        IProcessRunner proc, string exePath, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return false;

        // /TR 은 명령줄 한 줄을 통째로 받는 자리라, 공백 있는 경로를 위해 따옴표를 값 안에 넣는다.
        // (러너가 트레이에서 스스로 등록할 때 쓰는 인자와 같은 모양이다.)
        var res = await proc.RunAsync("schtasks", new[]
        {
            "/Create", "/F",
            "/SC", "ONLOGON",
            "/RL", "LIMITED",
            "/TN", AutostartTaskName,
            "/TR", $"\"{exePath}\"",
        }, timeout: TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);

        return res.Ok;
    }
}
