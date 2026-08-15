namespace Teavel.Platform;

/// <summary>외부 프로세스 실행 결과.</summary>
/// <param name="ExitCode">종료 코드. 실행 자체가 실패하면 -1.</param>
/// <param name="StdOut">표준 출력 전체.</param>
/// <param name="StdErr">표준 오류 전체.</param>
/// <param name="TimedOut">제한 시간을 넘겨 강제 종료됐는지.</param>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;

    /// <summary>실패 원인을 사람이 읽을 한 줄로. 성공이면 빈 문자열.</summary>
    public string FailureSummary => TimedOut
        ? "제한 시간 안에 끝나지 않아 중단했습니다."
        : ExitCode == 0
            ? ""
            : (StdErr.Trim() is { Length: > 0 } e ? e : $"종료 코드 {ExitCode}");
}

/// <summary>
/// 외부 프로세스 실행. PowerShell·winget 호출이 전부 이 인터페이스를 지난다.
/// 비Windows 개발 환경에서는 <see cref="FakeProcessRunner"/> 로 교체해 로직을 검증한다.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// 프로세스를 실행하고 끝날 때까지 기다린다.
    /// </summary>
    /// <param name="fileName">실행 파일(경로 또는 PATH 상의 이름).</param>
    /// <param name="arguments">인자 목록. 셸을 거치지 않으므로 따옴표를 직접 붙이지 않는다.</param>
    /// <param name="stdin">표준 입력으로 흘려보낼 내용. null이면 즉시 닫는다.</param>
    /// <param name="timeout">제한 시간. null이면 무제한.</param>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdin = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    /// <summary>실행 파일이 PATH 또는 알려진 위치에 있는지.</summary>
    bool Exists(string fileName);

    /// <summary>결과를 기다리지 않고 띄우기만 한다(설치 마법사·앱 실행 등). 성공 여부만 반환.</summary>
    bool Launch(string fileName, IReadOnlyList<string>? arguments = null, bool useShellExecute = true);
}
