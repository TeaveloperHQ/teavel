using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Teavel.Tools;

namespace Teavel.M365;

/// <summary>
/// M365 작업을 처리하는 상주 PowerShell 과 이야기하는 창구.
///
/// <para>
/// 보통 도구는 <see cref="ToolRunner"/> 가 호출마다 PowerShell 을 새로 띄운다.
/// 한 도구가 망가져도 다음 도구에 옮지 않아 그 편이 안전하다.
/// </para>
/// <para>
/// M365 만 예외다. <c>Connect-ExchangeOnline</c> 은 그 프로세스 안에서만 살아 있어서,
/// 새로 띄울 때마다 브라우저 로그인을 다시 해야 한다. 재고 보고 · 이름 바꾸고 · 만들고 하는
/// 사이에 로그인이 예닐곱 번 뜬다는 뜻이다 — 로그인 창 하나도 버거워하는 분들에게는
/// 사실상 못 쓰는 기능이 된다. 그래서 여기서는 프로세스를 하나 띄워 두고 계속 쓴다.
/// </para>
/// <para>
/// 대신 값을 치른다. 프로세스가 오래 사니 반드시 <see cref="DisposeAsync"/> 로 닫아야 하고,
/// 한 명령이 죽어도 세션은 살아야 한다(그건 상주 스크립트 쪽에서 받아 낸다).
/// </para>
/// </summary>
public sealed class M365Host : IAsyncDisposable
{
    /// <summary>결과 한 줄임을 알리는 표시자. 이것이 없는 줄은 전부 진행 상황이다.</summary>
    private const string Marker = "##TEAVEL##";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Process _proc;
    private readonly Action<string> _onProgress;
    private readonly StringBuilder _stderr = new();

    /// <summary>한 번에 한 명령만 흘려보낸다 — 답이 섞이면 누구 답인지 알 수 없다.</summary>
    private readonly SemaphoreSlim _turn = new(1, 1);

    private bool _closed;

    private M365Host(Process proc, Action<string> onProgress)
    {
        _proc = proc;
        _onProgress = onProgress;
    }

    /// <summary>상주 PowerShell 이 아직 살아 있는지.</summary>
    public bool IsAlive
    {
        get { try { return !_closed && !_proc.HasExited; } catch { return false; } }
    }

    /// <summary>
    /// 상주 PowerShell 을 띄운다.
    /// </summary>
    /// <param name="shell">powershell.exe 등. <see cref="ToolRunner.FindPowerShell"/> 이 고른 것.</param>
    /// <param name="scriptsDirectory">Teavel.M365.psm1 이 있는 폴더.</param>
    /// <param name="onProgress">진행 상황 한 줄이 올 때마다 부른다(브라우저 로그인 안내 등).</param>
    public static async Task<M365Host> StartAsync(
        string shell,
        string scriptsDirectory,
        Action<string> onProgress,
        CancellationToken ct = default)
    {
        var hostScript = Path.Combine(scriptsDirectory, "Invoke-TeavelM365Host.ps1");

        var psi = new ProcessStartInfo(shell)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        // -NoExit 는 쓰지 않는다. 상주 스크립트가 스스로 반복하다 __bye 에 끝난다.
        // -NonInteractive 도 쓰지 않는다 — 로그인 창이 떠야 하기 때문이다.
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        if (shell.StartsWith("powershell", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add("-STA");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(hostScript);
        psi.ArgumentList.Add("-ScriptsDirectory");
        psi.ArgumentList.Add(scriptsDirectory);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("PowerShell 을 띄우지 못했습니다.");

        var host = new M365Host(proc, onProgress);

        // 표준 오류는 따로 모아 둔다. 죽었을 때 이유를 말해 줄 유일한 근거다.
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 }) lock (host._stderr) host._stderr.AppendLine(e.Data);
        };
        proc.BeginErrorReadLine();

        // 상주 스크립트가 뜨자마자 '준비됐습니다' 를 한 번 낸다. 그것까지 받아야 준비 끝이다.
        var ready = await host.ReadReplyAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!ready.Ok)
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(ready.Message);
        }

        return host;
    }

    /// <summary>
    /// 상주 세션에 명령 하나를 보내고 답을 기다린다.
    /// </summary>
    /// <param name="function">PowerShell 함수 이름. 상주 스크립트가 허용 목록으로 한 번 더 거른다.</param>
    /// <param name="args">이름 있는 인자들. splatting 으로 넘어가 끝까지 '값' 으로만 남는다.</param>
    /// <param name="timeout">
    /// 넉넉하게 준다. 로그인은 사람이 브라우저에서 하는 일이라 몇 분이 걸릴 수 있고,
    /// 여기서 먼저 끊으면 애써 뜬 로그인 창이 헛것이 된다.
    /// </param>
    public async Task<ToolRunResult> CallAsync(
        string function,
        IReadOnlyDictionary<string, object?>? args = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (!IsAlive)
            return ToolRunResult.Fail("M365 세션이 끊어졌습니다.", StderrTail());

        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payload = JsonSerializer.Serialize(
                new RequestDto { Function = function, Args = args }, JsonOpts);

            await _proc.StandardInput.WriteLineAsync(payload).ConfigureAwait(false);
            await _proc.StandardInput.FlushAsync().ConfigureAwait(false);

            return await ReadReplyAsync(timeout ?? TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // 상주 쪽이 먼저 죽으면 파이프가 닫힌다.
            return ToolRunResult.Fail("M365 세션이 끊어졌습니다.", StderrTail());
        }
        catch (ObjectDisposedException)
        {
            return ToolRunResult.Fail("M365 세션이 끊어졌습니다.", StderrTail());
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>
    /// 표시자가 붙은 줄이 나올 때까지 읽는다. 그 앞의 줄들은 진행 상황이라 그대로 흘려보낸다.
    /// </summary>
    private async Task<ToolRunResult> ReadReplyAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (true)
        {
            string? line;
            try
            {
                line = await _proc.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ToolRunResult.Fail(
                    $"{(int)timeout.TotalSeconds}초 안에 답이 오지 않았습니다.", StderrTail());
            }

            // stdout 이 끝났다 — 상주 쪽이 죽었다는 뜻이다.
            if (line is null)
                return ToolRunResult.Fail("M365 세션이 끊어졌습니다.", StderrTail());

            if (!line.StartsWith(Marker, StringComparison.Ordinal))
            {
                // 결과가 아니라 사람에게 보여 줄 안내다. 곧바로 흘려보낸다 —
                // 브라우저 로그인 설명은 나중에 몰아 보여 주면 소용이 없다.
                if (line.Trim().Length > 0) _onProgress(line.TrimEnd());
                continue;
            }

            var json = line[Marker.Length..];
            try
            {
                var dto = JsonSerializer.Deserialize<ReplyDto>(json, JsonOpts);
                if (dto is not null)
                    return new ToolRunResult(dto.Ok, dto.Message ?? "", dto.Details ?? Array.Empty<string>());
            }
            catch (JsonException) { }

            return ToolRunResult.Fail("답을 알아보지 못했습니다.", json);
        }
    }

    private string[] StderrTail()
    {
        string text;
        lock (_stderr) text = _stderr.ToString();
        if (text.Trim().Length == 0) return Array.Empty<string>();

        // 스택 추적이 길어 그대로 보이면 아무 도움이 안 된다. 끝 몇 줄만.
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                   .Select(l => l.TrimEnd())
                   .TakeLast(5)
                   .ToArray();
    }

    /// <summary>상주 PowerShell 을 정중히 끝낸다. 안 나가면 끊는다.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;

        try
        {
            if (!_proc.HasExited)
            {
                await _proc.StandardInput.WriteLineAsync("{\"function\":\"__bye\"}").ConfigureAwait(false);
                await _proc.StandardInput.FlushAsync().ConfigureAwait(false);
                _proc.StandardInput.Close();

                // 나갈 틈은 준다. Disconnect 를 못 하고 죽으면 서버에 세션이 남는다.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await _proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        catch { }

        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        try { _proc.Dispose(); } catch { }
        _turn.Dispose();
    }

    private sealed class RequestDto
    {
        public string Function { get; set; } = "";
        public IReadOnlyDictionary<string, object?>? Args { get; set; }
    }

    private sealed class ReplyDto
    {
        public bool Ok { get; set; }
        public string? Message { get; set; }
        public string[]? Details { get; set; }
    }
}
