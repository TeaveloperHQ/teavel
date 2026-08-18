using System.Text.Json;
using System.Text.Json.Serialization;
using Teavel.Platform;

namespace Teavel.Tools;

/// <summary>도구 실행 결과 — PowerShell 래퍼가 돌려준 JSON 을 그대로 담는다.</summary>
/// <param name="Ok">성공했는지.</param>
/// <param name="Message">교사에게 보여줄 한 줄 요약.</param>
/// <param name="Details">자세한 줄들(처리한 파일 목록, 통계 표 등).</param>
public sealed record ToolRunResult(bool Ok, string Message, IReadOnlyList<string> Details)
{
    public static ToolRunResult Fail(string message, params string[] details)
        => new(false, message, details);
}

/// <summary>
/// 도구를 PowerShell 로 실행한다.
///
/// 인자는 <b>명령줄이 아니라 표준 입력의 JSON</b> 으로 넘긴다. 이유:
/// 로컬 모델이 채운 값이 명령줄에 문자열로 끼워지면 따옴표·백틱 하나에 명령이 갈라질 수 있다.
/// JSON + splatting 으로 넘기면 값은 끝까지 '값' 으로만 남는다.
/// 한글 경로·공백 있는 폴더 이름도 이 경로에서는 문제가 되지 않는다.
/// </summary>
public sealed class ToolRunner
{
    private readonly IProcessRunner _proc;
    private readonly ISystemPaths _paths;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ToolRunner(IProcessRunner proc, ISystemPaths paths)
    {
        _proc = proc;
        _paths = paths;
    }

    /// <summary>
    /// 도구 스크립트가 놓인 폴더.
    /// </summary>
    /// <remarks>
    /// exe 옆에 있으면 그것을 쓰고, 없으면 exe 안에 묻어 둔 것을 꺼내 놓는다 —
    /// 포털은 exe 하나만 배포하므로 옆 폴더가 없는 것이 오히려 보통이다.
    /// </remarks>
    public string ScriptsDirectory => Platform.Payload.Ensure(_paths.AppDirectory, "scripts");

    /// <summary>래퍼 스크립트 경로.</summary>
    public string WrapperPath => Path.Combine(ScriptsDirectory, "Invoke-TeavelTool.ps1");

    /// <summary>쓸 수 있는 PowerShell 실행 파일. 없으면 null.</summary>
    public string? FindPowerShell()
    {
        // Windows PowerShell 5.1 을 먼저 찾는다 — Office COM 자동화가 가장 안정적이고,
        // Windows 라면 반드시 깔려 있다. pwsh(7) 는 있으면 대안으로 쓴다.
        if (_proc.Exists("powershell.exe")) return "powershell.exe";
        if (_proc.Exists("powershell")) return "powershell";
        if (_proc.Exists("pwsh")) return "pwsh";
        return null;
    }

    /// <summary>
    /// 이 컴퓨터의 실행 정책이 Teavel 을 막는지 확인한다.
    /// </summary>
    /// <remarks>
    /// 우리는 PowerShell 을 <c>-ExecutionPolicy Bypass</c> 로 부르지만, 그건 Process 범위라
    /// <b>그룹 정책(MachinePolicy·UserPolicy)이 그 위에 있다.</b> 학교가 관리하는 PC 에서
    /// AllSigned 를 걸어 두면 우리 Bypass 는 무시되고 스크립트가 돌지 않는다.
    /// 그때 교사가 보는 것은 "왜인지 모르겠지만 아무것도 안 됨" 이므로, 미리 짚어 준다.
    /// </remarks>
    public async Task<Setup.CheckResult> CheckExecutionPolicyAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Setup.CheckResult.NotApplicable("Windows 에서만 확인할 수 있습니다.");

        var shell = FindPowerShell();
        if (shell is null)
            return Setup.CheckResult.Unknown("PowerShell 을 찾지 못해 확인하지 못했습니다.");

        var res = await _proc.RunAsync(shell, new[]
        {
            "-NoProfile", "-NonInteractive", "-Command",
            "Get-ExecutionPolicy -List | " +
            "ForEach-Object { \"$($_.Scope)=$($_.ExecutionPolicy)\" }",
        }, timeout: TimeSpan.FromSeconds(30), ct: ct).ConfigureAwait(false);

        if (!res.Ok) return Setup.CheckResult.Unknown("실행 정책을 확인하지 못했습니다.", res.FailureSummary);

        var scopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in res.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('=', 2);
            if (parts.Length == 2) scopes[parts[0].Trim()] = parts[1].Trim();
        }

        var lines = scopes.Select(kv => $"{kv.Key}: {kv.Value}").ToArray();

        // 그룹 정책 두 범위만 우리를 이긴다.
        foreach (var scope in new[] { "MachinePolicy", "UserPolicy" })
        {
            if (!scopes.TryGetValue(scope, out var policy)) continue;

            switch (policy.ToLowerInvariant())
            {
                case "restricted":
                    return Setup.CheckResult.NeedsFix(
                        "학교 정책이 PowerShell 스크립트 실행을 모두 막고 있습니다.",
                        lines.Concat(new[]
                        {
                            "",
                            "Teavel 의 엑셀·워드·아웃룩 기능이 동작하지 않습니다.",
                            "전산 담당 선생님께 문의해 주세요.",
                        }).ToArray());

                case "allsigned":
                    return Setup.CheckResult.NeedsFix(
                        "학교 정책이 서명된 스크립트만 실행하도록 돼 있습니다 (AllSigned).",
                        lines.Concat(new[]
                        {
                            "",
                            "Teavel 스크립트에 서명이 없으면 기능이 동작하지 않습니다.",
                            "포털에서 서명된 판을 받으셨는지 확인해 주세요.",
                        }).ToArray());

                case "remotesigned":
                    return Setup.CheckResult.NeedsFix(
                        "학교 정책이 RemoteSigned 입니다. 인터넷에서 받은 파일은 서명이 필요합니다.",
                        lines.Concat(new[]
                        {
                            "",
                            "Teavel 을 압축으로 내려받았다면 스크립트에 '다른 컴퓨터에서 온 파일' 표시가",
                            "붙어 있어 막힐 수 있습니다.",
                            "",
                            "해결: 서명된 판을 받거나, scripts 폴더의 파일마다",
                            "속성 → 아래쪽 [차단 해제] 를 체크해 주세요.",
                        }).ToArray());
            }
        }

        return Setup.CheckResult.Ok("실행 정책이 Teavel 을 막지 않습니다.", lines);
    }

    /// <summary>도구를 실행한다. 인자는 이미 검증·정규화된 것이어야 한다.</summary>
    public Task<ToolRunResult> RunAsync(ToolInvocation call, CancellationToken ct = default)
        => InvokeAsync(call.Tool.Module, call.Tool.Function, call.Arguments,
                       call.Tool.TimeoutSeconds, call.Tool.Title, ct);

    /// <summary>
    /// PowerShell 함수를 직접 부른다.
    /// 도구 카탈로그를 거치지 않는 호출(업데이트 확인 같은 세팅 작업)이 쓴다.
    /// </summary>
    /// <param name="label">실패했을 때 교사에게 보여줄 이름.</param>
    public async Task<ToolRunResult> InvokeAsync(
        string module,
        string function,
        IReadOnlyDictionary<string, object> arguments,
        int timeoutSeconds,
        string label,
        CancellationToken ct = default)
    {
        var shell = FindPowerShell();
        if (shell is null)
            return ToolRunResult.Fail(
                "PowerShell 을 찾지 못했습니다.",
                "Windows 에서 실행해야 하는 기능입니다.");

        if (!File.Exists(WrapperPath))
            return ToolRunResult.Fail(
                "도구 스크립트를 찾지 못했습니다.",
                $"있어야 할 위치: {WrapperPath}",
                "Teavel 폴더의 scripts 폴더가 지워졌는지 확인해 주세요.");

        var payload = JsonSerializer.Serialize(new RequestDto
        {
            Module = module,
            Function = function,
            ScriptsDirectory = ScriptsDirectory,
            Args = arguments.ToDictionary(kv => kv.Key, kv => kv.Value),
        }, JsonOpts);

        var args = new List<string>
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        };
        // -STA 는 Windows PowerShell 5.1 전용 스위치. COM 자동화에 필요하다.
        if (shell.StartsWith("powershell", StringComparison.OrdinalIgnoreCase)) args.Add("-STA");
        args.Add("-File");
        args.Add(WrapperPath);

        var res = await _proc.RunAsync(
            shell, args, stdin: payload,
            timeout: TimeSpan.FromSeconds(timeoutSeconds), ct).ConfigureAwait(false);

        return Parse(res, label, timeoutSeconds);
    }

    private static ToolRunResult Parse(ProcessResult res, string label, int timeoutSeconds)
    {
        // 래퍼는 성공·실패 모두 JSON 한 덩어리를 stdout 으로 낸다.
        // 그 앞에 경고 등 다른 출력이 섞일 수 있어 마지막 '{' 부터 읽는다.
        var text = res.StdOut;
        var start = text.LastIndexOf('{');
        if (start >= 0)
        {
            try
            {
                var dto = JsonSerializer.Deserialize<ResponseDto>(text[start..], JsonOpts);
                if (dto is not null)
                    return new ToolRunResult(dto.Ok, dto.Message ?? "", dto.Details ?? Array.Empty<string>());
            }
            catch (JsonException) { /* 아래 폴백 */ }
        }

        // JSON 을 못 받았다 = 스크립트가 죽었거나 제한 시간을 넘겼다.
        var details = new List<string>();
        if (res.TimedOut)
            details.Add($"{timeoutSeconds}초 안에 끝나지 않았습니다. "
                      + "Excel·Word 창이 열린 채 멈춰 있지 않은지 확인해 주세요.");
        if (res.StdErr.Trim() is { Length: > 0 } err) details.Add(err.Trim());
        if (res.StdOut.Trim() is { Length: > 0 } outp && start < 0) details.Add(outp.Trim());

        return ToolRunResult.Fail($"'{label}' 을(를) 끝내지 못했습니다.", details.ToArray());
    }

    private sealed class RequestDto
    {
        public string Module { get; set; } = "";
        public string Function { get; set; } = "";
        public string ScriptsDirectory { get; set; } = "";
        public Dictionary<string, object> Args { get; set; } = new();
    }

    private sealed class ResponseDto
    {
        public bool Ok { get; set; }
        public string? Message { get; set; }
        public string[]? Details { get; set; }
    }
}
