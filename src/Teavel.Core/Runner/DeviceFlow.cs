using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Teavel.Runner;

/// <summary>
/// 러너 <c>config.json</c> — 포털이 활성화 때 발급하고 러너가 exe 옆에서 읽는 형식.
/// (teaveloper-runner / PORTAL_INTEGRATION.md §1)
///
/// 필드 이름은 러너 Go 코드가 그대로 읽으므로 <b>바꾸면 안 된다</b>.
/// 그래서 camelCase 를 규약에 맡기지 않고 이름을 하나씩 못 박는다.
/// </summary>
public sealed record RunnerConfig
{
    [JsonPropertyName("gatewayUrl")] public string GatewayUrl { get; init; } = "";
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("publicUrl")] public string PublicUrl { get; init; } = "";
    [JsonPropertyName("localPort")] public int LocalPort { get; init; }
    [JsonPropertyName("token")] public string Token { get; init; } = "";

    /// <summary>러너가 실제로 뜰 수 있는 설정인지(러너의 검증 규칙과 같은 조건).</summary>
    [JsonIgnore]
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(GatewayUrl) &&
        !string.IsNullOrWhiteSpace(Token) &&
        LocalPort is > 0 and <= 65535;
}

/// <summary>포털이 발급한 활성화 코드 한 벌.</summary>
public sealed record DeviceAuthorization
{
    /// <summary>CLI 만 보관한다. 화면·로그에 절대 내보내지 않는다.</summary>
    [JsonPropertyName("deviceCode")] public string DeviceCode { get; init; } = "";

    /// <summary>교사가 눈으로 보고 포털에 입력할 짧은 코드.</summary>
    [JsonPropertyName("userCode")] public string UserCode { get; init; } = "";

    [JsonPropertyName("verifyUrl")] public string VerifyUrl { get; init; } = "";
    [JsonPropertyName("verifyUrlComplete")] public string? VerifyUrlComplete { get; init; }
    [JsonPropertyName("interval")] public int Interval { get; init; } = 5;
    [JsonPropertyName("expiresIn")] public int ExpiresIn { get; init; } = 600;

    /// <summary>브라우저로 열 주소 — 코드가 미리 채워진 쪽이 있으면 그쪽.</summary>
    [JsonIgnore]
    public string OpenUrl => string.IsNullOrWhiteSpace(VerifyUrlComplete) ? VerifyUrl : VerifyUrlComplete!;
}

/// <summary>폴링 한 번의 결과.</summary>
public enum DevicePollStatus
{
    /// <summary>아직 승인 안 됨 — 기다렸다 다시.</summary>
    Pending,

    /// <summary>너무 자주 물었다 — 간격을 늘리고 다시.</summary>
    SlowDown,

    /// <summary>교사가 거부했다.</summary>
    Denied,

    /// <summary>코드가 만료됐다.</summary>
    Expired,

    /// <summary>승인됨 — config 가 함께 온다.</summary>
    Ok,

    /// <summary>모르는 status — 포털이 규격을 벗어났다.</summary>
    Unknown,
}

/// <summary>폴링 응답.</summary>
/// <param name="Status">상태.</param>
/// <param name="Config">승인됐을 때의 러너 설정.</param>
/// <param name="Raw">포털이 보낸 status 문자열 원본(알 수 없는 값 진단용).</param>
public sealed record DevicePollResult(DevicePollStatus Status, RunnerConfig? Config, string Raw);

/// <summary>활성화가 끝까지 가지 못한 경우 — 교사에게 그대로 보여줄 문장을 담는다.</summary>
public sealed class DeviceFlowException : Exception
{
    public DeviceFlowException(string message) : base(message) { }
    public DeviceFlowException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 포털과의 활성화 대화(Device Flow) — PORTAL_INTEGRATION.md §4.1.
///
/// 흐름: <c>StartAsync</c> 로 코드를 받고 → 교사가 브라우저에서 승인 →
/// <c>WaitForApprovalAsync</c> 가 폴링하다 <c>config.json</c> 내용을 돌려준다.
///
/// 비밀번호를 대신 받지 않는다. 이 클래스는 코드 한 벌과 폴링만 다루고,
/// 로그인·승인은 전적으로 교사의 브라우저에서 일어난다.
/// </summary>
public sealed class DeviceFlowClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public DeviceFlowClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>활성화를 시작하고 교사에게 보여줄 코드를 받는다.</summary>
    public async Task<DeviceAuthorization> StartAsync(string codeUrl, string clientVersion, CancellationToken ct = default)
    {
        HttpResponseMessage res;
        try
        {
            res = await _http.PostAsync(
                codeUrl,
                JsonBody(new { client = "teavel-cli", clientVersion }),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new DeviceFlowException("포털에 연결하지 못했습니다.", ex);
        }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
                throw new DeviceFlowException($"포털이 활성화 요청을 거절했습니다. (HTTP {(int)res.StatusCode})");

            var auth = await ReadAsync<DeviceAuthorization>(res, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(auth.DeviceCode) ||
                string.IsNullOrWhiteSpace(auth.UserCode) ||
                string.IsNullOrWhiteSpace(auth.VerifyUrl))
                throw new DeviceFlowException("포털이 보낸 활성화 정보가 올바르지 않습니다.");

            return auth;
        }
    }

    /// <summary>한 번 물어본다.</summary>
    public async Task<DevicePollResult> PollAsync(string tokenUrl, string deviceCode, CancellationToken ct = default)
    {
        HttpResponseMessage res;
        try
        {
            res = await _http.PostAsync(tokenUrl, JsonBody(new { deviceCode }), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 폴링 중 한 번 끊기는 것은 흔하다 — 실패로 단정하지 않고 다음 차례에 다시 묻는다.
            return new DevicePollResult(DevicePollStatus.Pending, null, "네트워크 일시 오류");
        }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
                throw new DeviceFlowException($"포털이 오류를 돌려주었습니다. (HTTP {(int)res.StatusCode})");

            var dto = await ReadAsync<PollDto>(res, ct).ConfigureAwait(false);
            var status = dto.Status?.Trim().ToLowerInvariant() switch
            {
                "pending" or "authorization_pending" => DevicePollStatus.Pending,
                "slow_down" or "slowdown" => DevicePollStatus.SlowDown,
                "denied" or "access_denied" => DevicePollStatus.Denied,
                "expired" or "expired_token" => DevicePollStatus.Expired,
                "ok" or "success" => DevicePollStatus.Ok,
                _ => DevicePollStatus.Unknown,
            };
            return new DevicePollResult(status, dto.Config, dto.Status ?? "");
        }
    }

    /// <summary>
    /// 교사가 승인할 때까지 기다린다. 승인되면 러너 설정을 돌려준다.
    /// </summary>
    /// <param name="onWaiting">남은 시간을 알려 주는 콜백(화면 갱신용). 폴링 직전마다 불린다.</param>
    public async Task<RunnerConfig> WaitForApprovalAsync(
        string tokenUrl,
        DeviceAuthorization auth,
        Action<TimeSpan>? onWaiting = null,
        CancellationToken ct = default)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(auth.Interval, 1, 60));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(auth.ExpiresIn > 0 ? auth.ExpiresIn : 600);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new DeviceFlowException("승인 시간이 지났습니다. 다시 시도해 주세요.");

            onWaiting?.Invoke(remaining);

            // 먼저 기다린다 — 교사가 브라우저를 열기도 전에 묻는 것은 의미가 없다.
            await Task.Delay(interval < remaining ? interval : remaining, ct).ConfigureAwait(false);

            var poll = await PollAsync(tokenUrl, auth.DeviceCode, ct).ConfigureAwait(false);
            switch (poll.Status)
            {
                case DevicePollStatus.Ok:
                    if (poll.Config is not { IsUsable: true } cfg)
                        throw new DeviceFlowException("포털이 보낸 설정이 올바르지 않습니다.");
                    return cfg;

                case DevicePollStatus.Pending:
                    continue;

                case DevicePollStatus.SlowDown:
                    interval += TimeSpan.FromSeconds(5);
                    continue;

                case DevicePollStatus.Denied:
                    throw new DeviceFlowException("포털에서 승인이 거부되었습니다.");

                case DevicePollStatus.Expired:
                    throw new DeviceFlowException("승인 시간이 지났습니다. 다시 시도해 주세요.");

                default:
                    throw new DeviceFlowException($"포털이 알 수 없는 응답을 보냈습니다: {poll.Raw}");
            }
        }
    }

    /// <summary>
    /// 요청 본문을 만든다.
    /// </summary>
    /// <remarks>
    /// 미리 문자열로 만들어 보내는 이유는 <b>Content-Length 를 붙이기 위해서</b>다.
    /// 스트림으로 넘기면 HttpClient 가 <c>Transfer-Encoding: chunked</c> 로 보내는데,
    /// 포털은 우리가 만들지 않는 코드라 Content-Length 만 읽는 구현을 만나면 본문이
    /// 통째로 빈 것으로 보인다. 본문이 수십 바이트뿐이라 청크로 얻을 이득도 없다.
    /// </remarks>
    private static StringContent JsonBody<T>(T value)
        => new(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            return await res.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
                   ?? throw new DeviceFlowException("포털이 빈 응답을 보냈습니다.");
        }
        catch (JsonException ex)
        {
            throw new DeviceFlowException("포털 응답을 이해하지 못했습니다.", ex);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private sealed record PollDto
    {
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("config")] public RunnerConfig? Config { get; init; }
    }
}
