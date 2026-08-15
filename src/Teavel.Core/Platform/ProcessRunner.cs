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
            try
            {
                // PowerShell 래퍼가 JSON 을 UTF-8 로 읽는다.
                await using var w = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false));
                await w.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (IOException) { /* 자식이 stdin 을 먼저 닫은 경우 — 무시 */ }
        }
        proc.StandardInput.Close();

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
