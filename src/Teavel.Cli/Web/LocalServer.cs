using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Teavel.Cli.Web;

/// <summary>브라우저가 보낸 것.</summary>
/// <param name="Method">GET · POST.</param>
/// <param name="Path">물음표 앞까지.</param>
/// <param name="Query">물음표 뒤.</param>
/// <param name="Headers">머리글. 이름은 소문자로 맞춰 둔다.</param>
/// <param name="Body">POST 의 몸통. GET 이면 비어 있다.</param>
public sealed record HttpAsk(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body)
{
    public string Text => Encoding.UTF8.GetString(Body);

    public string Q(string name) => Query.TryGetValue(name, out var v) ? v : "";

    public string H(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : "";
}

/// <summary>브라우저에 돌려줄 것.</summary>
public sealed record HttpSay(int Status, string ContentType, byte[] Body)
{
    public static HttpSay Json(string json) => new(200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));

    public static HttpSay Text(int status, string text)
        => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    public static HttpSay Asset(string contentType, byte[] bytes) => new(200, contentType, bytes);

    public static HttpSay NotFound => Text(404, "없습니다.");
}

/// <summary>
/// 관리 화면을 띄우는 아주 작은 웹 서버.
///
/// <para>
/// <b>왜 <see cref="System.Net.HttpListener"/> 가 아닌가.</b> 그쪽은 Windows 의 http.sys 를 거치는데,
/// 127.0.0.1 이라도 URL 예약(<c>netsh http add urlacl</c>)이 필요할 수 있고 그건 관리자 권한이다.
/// 학교가 관리하는 PC 에서 정책이 어떻게 걸려 있을지는 알 수 없다. <b>여기서 막히면
/// 관리 화면이 아예 안 뜨고, 교사는 그 까닭을 알 길이 없다.</b>
/// </para>
/// <para>
/// 소켓을 직접 열면 그 갈래가 통째로 사라진다. 권한도, 예약도, 방화벽 물음도 없다 —
/// 루프백은 방화벽을 거치지 않는다. 대신 HTTP 를 우리가 조금 짜야 하는데,
/// 여기서 필요한 것은 한 사람이 쓰는 한 페이지분이라 그 '조금' 이 정말 조금이다.
/// </para>
/// <para>
/// <b>바깥에서 못 들어온다.</b> <see cref="IPAddress.Loopback"/> 에만 붙으므로 이 PC 밖에서는
/// 닿지 않는다. 그 위에 토큰을 하나 더 둔다 — 같은 PC 의 다른 프로그램이 포트를 훑어
/// 학교 테넌트를 만지는 일은 없어야 한다.
/// </para>
/// </summary>
public sealed class LocalServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<HttpAsk, CancellationToken, Task<HttpSay>> _handle;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <summary>붙은 포트. 0 으로 열어 운영체제가 남는 것을 주게 한다.</summary>
    public int Port { get; }

    /// <summary>이 판에만 쓰는 열쇠. 프로그램이 끝나면 사라진다.</summary>
    public string Token { get; } = Guid.NewGuid().ToString("N");

    /// <summary>브라우저에 띄울 주소.</summary>
    public string Url => $"http://127.0.0.1:{Port}/?t={Token}";

    public LocalServer(Func<HttpAsk, CancellationToken, Task<HttpSay>> handle)
    {
        _handle = handle;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Start(CancellationToken ct)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, ct);
        _loop = Task.Run(() => AcceptAsync(linked.Token), CancellationToken.None);
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }

            // 한 연결이 터져도 서버는 살아 있어야 한다. 관리자가 쓰는 도중에
            // 화면이 죽으면 지금 무엇이 됐고 무엇이 안 됐는지 알 수 없게 된다.
            _ = Task.Run(async () =>
            {
                try { await ServeAsync(client, ct).ConfigureAwait(false); }
                catch { /* 이 연결만 버린다 */ }
                finally { client.Dispose(); }
            }, CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        await using var stream = client.GetStream();

        var ask = await ReadAskAsync(stream, ct).ConfigureAwait(false);
        if (ask is null) return;

        HttpSay say;

        // DNS 재바인딩 막기. 바깥 이름이 이 포트를 가리키게 해 놓고 브라우저로 부르는 수법이
        // 있는데, Host 를 못 박아 두면 통하지 않는다.
        var host = ask.H("host");
        if (!host.StartsWith("127.0.0.1:", StringComparison.Ordinal) && !host.StartsWith("localhost:", StringComparison.Ordinal))
            say = HttpSay.Text(400, "이 주소로는 받지 않습니다.");
        else
            try { say = await _handle(ask, ct).ConfigureAwait(false); }
            catch (Exception ex) { say = HttpSay.Text(500, ex.Message); }

        await WriteSayAsync(stream, say, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 요청 한 개를 읽는다. 못 읽으면 <c>null</c>.
    /// </summary>
    /// <remarks>
    /// 머리글은 <c>\r\n\r\n</c> 까지 읽고, 몸통은 <c>Content-Length</c> 만큼 더 읽는다.
    /// 청크 전송은 다루지 않는다 — 이 화면이 보내는 것은 우리가 짠 <c>fetch</c> 뿐이고
    /// 그것은 늘 길이를 붙인다.
    /// </remarks>
    private static async Task<HttpAsk?> ReadAskAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var head = new MemoryStream();
        var headEnd = -1;

        while (headEnd < 0)
        {
            if (head.Length > 64 * 1024) return null;   // 머리글이 이만큼일 리 없다

            var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) return null;

            head.Write(buffer, 0, n);
            headEnd = Find(head.GetBuffer(), (int)head.Length);
        }

        var raw = head.GetBuffer();
        var text = Encoding.UTF8.GetString(raw, 0, headEnd);
        var lines = text.Split("\r\n");
        if (lines.Length == 0) return null;

        var start = lines[0].Split(' ');
        if (start.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }

        var target = start[1];
        var mark = target.IndexOf('?');
        var path = Uri.UnescapeDataString(mark < 0 ? target : target[..mark]);
        var query = ParseQuery(mark < 0 ? "" : target[(mark + 1)..]);

        // 머리글 뒤에 몸통이 이미 딸려 와 있을 수 있다. 그만큼은 다시 읽지 않는다.
        var already = (int)head.Length - (headEnd + 4);
        var length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var want) ? want : 0;

        // 명단 파일이 몸통으로 온다. 진짜 학교 명단이 커도 이 안에 들어온다.
        if (length > 64 * 1024 * 1024) return null;

        var body = new byte[length];
        if (length > 0)
        {
            var copy = Math.Min(already, length);
            Array.Copy(raw, headEnd + 4, body, 0, copy);

            var got = copy;
            while (got < length)
            {
                var n = await stream.ReadAsync(body.AsMemory(got, length - got), ct).ConfigureAwait(false);
                if (n == 0) break;
                got += n;
            }
        }

        return new HttpAsk(start[0].ToUpperInvariant(), path, query, headers, body);

        static int Find(byte[] b, int len)
        {
            for (var i = 0; i + 3 < len; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) map[Uri.UnescapeDataString(pair)] = "";
            else map[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return map;
    }

    private static async Task WriteSayAsync(NetworkStream stream, HttpSay say, CancellationToken ct)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(say.Status).Append(' ').Append(Reason(say.Status)).Append("\r\n");
        head.Append("Content-Type: ").Append(say.ContentType).Append("\r\n");
        head.Append("Content-Length: ").Append(say.Body.Length).Append("\r\n");

        // 이 화면은 우리가 지금 띄운 것이다. 브라우저가 지난 판의 것을 들고 있으면 안 된다.
        head.Append("Cache-Control: no-store\r\n");

        // 바깥으로 새 나갈 자리를 아예 막는다.
        head.Append("Referrer-Policy: no-referrer\r\n");
        head.Append("X-Content-Type-Options: nosniff\r\n");
        head.Append("Content-Security-Policy: default-src 'self'; img-src 'self' data:; connect-src 'self'\r\n");

        // 한 요청에 한 연결. 살려 두면 관리가 늘고, 여기서 얻을 것은 없다.
        head.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        if (say.Body.Length > 0) await stream.WriteAsync(say.Body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "OK",
    };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* 끝내는 중이다 */ }
        }

        _stop.Dispose();
    }
}
