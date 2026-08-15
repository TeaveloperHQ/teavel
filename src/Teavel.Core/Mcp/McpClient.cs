using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Teavel.Mcp;

/// <summary>MCP 서버가 제공하는 도구 하나.</summary>
/// <param name="Name">도구 이름(서버 안에서 고유).</param>
/// <param name="Description">무엇을 하는지.</param>
/// <param name="InputSchema">인자의 JSON 스키마. 그대로 보관해 인자를 만들 때 참고한다.</param>
public sealed record McpTool(string Name, string Description, JsonNode? InputSchema);

/// <summary>MCP 서버에 연결하지 못했거나 규약을 어겼을 때.</summary>
public sealed class McpException : Exception
{
    public McpException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// MCP 서버 하나와의 연결(stdio 전송).
///
/// Teavel 은 MCP <b>호스트</b> 다 — teaveloper 앱들이 서버가 되고, Teavel 이 그 도구를 부른다.
/// 전송은 줄바꿈으로 구분된 JSON-RPC 2.0 이다.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    // 서버가 더 낮은 버전만 안다면 응답으로 알려 주고, 그때는 서버 쪽에 맞춘다.
    private const string ProtocolVersion = "2025-06-18";

    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _readLoopCts = new();
    private readonly Task _readLoop;

    private int _nextId;
    private volatile bool _disposed;

    /// <summary>이 연결이 어느 앱의 것인지(오류 문장에 쓴다).</summary>
    public string ServerLabel { get; }

    private McpClient(string label, Process proc)
    {
        ServerLabel = label;
        _proc = proc;
        _stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
        _readLoop = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));
    }

    /// <summary>서버를 띄우고 initialize 까지 마친다.</summary>
    /// <param name="label">앱 이름 등 사람이 읽을 이름.</param>
    /// <param name="command">서버 실행 파일.</param>
    /// <param name="arguments">실행 인자.</param>
    public static async Task<McpClient> ConnectAsync(
        string label, string command, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(command) ?? Environment.CurrentDirectory,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new McpException($"{label} 을(를) 실행하지 못했습니다.");
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException($"{label} 을(를) 실행하지 못했습니다: {ex.Message}", ex);
        }

        // 서버가 stderr 로 쏟아내는 로그가 파이프를 채워 멈추지 않도록 계속 비워 준다.
        _ = Task.Run(async () =>
        {
            try { while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { } }
            catch { }
        });

        var client = new McpClient(label, proc);
        try
        {
            await client.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return client;
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "Teavel",
                ["version"] = typeof(McpClient).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            },
        }, ct).ConfigureAwait(false);

        if (result is null) throw new McpException($"{ServerLabel} 이(가) initialize 에 응답하지 않았습니다.");

        // 규약상 초기화가 끝났음을 알려야 서버가 요청을 받기 시작한다.
        await NotifyAsync("notifications/initialized", null, ct).ConfigureAwait(false);
    }

    /// <summary>서버가 제공하는 도구 목록.</summary>
    public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken ct = default)
    {
        var result = await RequestAsync("tools/list", new JsonObject(), ct).ConfigureAwait(false);

        var tools = new List<McpTool>();
        if (result?["tools"] is JsonArray arr)
        {
            foreach (var t in arr)
            {
                var name = t?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;
                tools.Add(new McpTool(
                    name!,
                    t?["description"]?.GetValue<string>() ?? "",
                    t?["inputSchema"]?.DeepClone()));
            }
        }
        return tools;
    }

    /// <summary>도구를 부르고, 돌아온 내용을 사람이 읽을 글로 합쳐 돌려준다.</summary>
    public async Task<string> CallToolAsync(
        string name, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var args = new JsonObject();
        foreach (var (k, v) in arguments)
            args[k] = v is null ? null : JsonSerializer.SerializeToNode(v);

        var result = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = args,
        }, ct).ConfigureAwait(false);

        var text = new StringBuilder();
        if (result?["content"] is JsonArray content)
        {
            foreach (var item in content)
            {
                if (item?["type"]?.GetValue<string>() == "text" && item["text"]?.GetValue<string>() is { } s)
                    text.AppendLine(s);
            }
        }

        // 서버가 '도구 실행이 실패했다' 고 표시한 경우 — 전송 오류와 구분해서 알린다.
        if (result?["isError"]?.GetValue<bool>() == true)
            throw new McpException($"{ServerLabel} 의 '{name}' 이(가) 실패했습니다: {text.ToString().Trim()}");

        return text.ToString().TrimEnd();
    }

    // ─────────────────────────── JSON-RPC ───────────────────────────

    private async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        if (_disposed) throw new McpException($"{ServerLabel} 과의 연결이 이미 닫혔습니다.");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null) message["params"] = parameters;

        try
        {
            await _stdin.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            throw new McpException($"{ServerLabel} 에 요청을 보내지 못했습니다: {ex.Message}", ex);
        }

        // 서버가 답하지 않을 수도 있으므로 무한정 기다리지 않는다.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        await using var reg = timeout.Token.Register(() =>
            tcs.TrySetException(new McpException($"{ServerLabel} 이(가) '{method}' 에 응답하지 않았습니다.")));

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task NotifyAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) message["params"] = parameters;

        try { await _stdin.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false); }
        catch (Exception ex) { throw new McpException($"{ServerLabel} 에 알림을 보내지 못했습니다: {ex.Message}", ex); }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _proc.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;                       // 서버가 끝났다
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch (JsonException) { continue; }            // JSON 이 아닌 줄(로그)은 흘려보낸다
                if (node is null) continue;

                // 서버가 우리에게 보내는 요청·알림은 아직 다루지 않는다(id 없는 메시지).
                if (node["id"] is not { } idNode) continue;

                int id;
                try { id = idNode.GetValue<int>(); } catch { continue; }
                if (!_pending.TryGetValue(id, out var tcs)) continue;

                if (node["error"] is { } err)
                {
                    var msg = err["message"]?.GetValue<string>() ?? "알 수 없는 오류";
                    tcs.TrySetException(new McpException($"{ServerLabel}: {msg}"));
                }
                else
                {
                    tcs.TrySetResult(node["result"]);
                }
            }
        }
        catch (Exception ex)
        {
            FailAllPending(new McpException($"{ServerLabel} 과의 연결이 끊겼습니다: {ex.Message}", ex));
            return;
        }

        FailAllPending(new McpException($"{ServerLabel} 이(가) 예고 없이 종료됐습니다."));
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var kv in _pending)
            kv.Value.TrySetException(ex);
        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _readLoopCts.Cancel();
        try { _stdin.Dispose(); } catch { }

        try
        {
            if (!_proc.HasExited && !_proc.WaitForExit(2000))
                _proc.Kill(entireProcessTree: true);
        }
        catch { }

        try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }

        _readLoopCts.Dispose();
        _proc.Dispose();
    }
}
