using System.Diagnostics;
using System.Text;

namespace Teavel.Platform;

/// <summary>실제 프로세스 실행기.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdin = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            // 한글 출력이 깨지지 않도록 UTF-8 로 읽는다(PowerShell 쪽도 UTF-8 로 맞춘다).
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        try
        {
            if (!proc.Start())
                return new ProcessResult(-1, "", $"'{fileName}' 을(를) 실행하지 못했습니다.", false);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", $"'{fileName}' 실행 실패: {ex.Message}", false);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (stdin != null)
        {
            // 자식이 stdin 을 다 읽기 전에 끝나 버리면 파이프가 먼저 닫힌다.
            // 그때 나는 예외는 IOException 만이 아니다 — 리눅스에서는 ObjectDisposedException 으로 온다.
            // 여기서 새어 나가면 도구 하나가 실패하는 것이 아니라 Teavel 이 통째로 죽는다.
            // 자식이 무엇을 내놓았는지는 아래에서 어차피 읽으므로, 못 쓴 것은 조용히 넘긴다.
            try
            {
                // PowerShell 래퍼가 JSON 을 UTF-8 로 읽는다. StreamWriter 로 감싸면
                // 그 Dispose 가 BaseStream 을 닫아 아래 Close() 와 이중으로 닫히므로 직접 쓴다.
                var payload = new UTF8Encoding(false).GetBytes(stdin);
                await proc.StandardInput.BaseStream.WriteAsync(payload, ct).ConfigureAwait(false);
                await proc.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (NotSupportedException) { }
        }

        try { proc.StandardInput.Close(); } catch { }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) timeoutCts.CancelAfter(t);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            // 취소가 호출자 요청이면 그대로 전파, 제한 시간 초과면 결과로 돌려준다.
            ct.ThrowIfCancellationRequested();
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        // 비동기 리다이렉트 버퍼가 다 비워지도록 한 번 더 기다린다.
        proc.WaitForExit();

        return new ProcessResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), TimedOut: false);
    }

    public bool Exists(string fileName)
    {
        if (Path.IsPathRooted(fileName)) return File.Exists(fileName);

        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : new[] { "" };

        foreach (var dir in paths)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in exts)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, fileName + ext))) return true;
                }
                catch { /* 잘못된 PATH 항목 */ }
            }
        }
        return false;
    }

    public bool Launch(string fileName, IReadOnlyList<string>? arguments = null, bool useShellExecute = true)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName) { UseShellExecute = useShellExecute };
            if (arguments != null)
                foreach (var a in arguments) psi.ArgumentList.Add(a);
            return Process.Start(psi) != null;
        }
        catch { return false; }
    }
}
