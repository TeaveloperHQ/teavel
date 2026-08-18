namespace Teavel.Setup;

/// <summary>진단 결과의 상태.</summary>
public enum CheckState
{
    /// <summary>제대로 돼 있음.</summary>
    Ok,

    /// <summary>손봐야 함.</summary>
    NeedsFix,

    /// <summary>이 컴퓨터에는 해당 없음(예: Windows 가 아님).</summary>
    NotApplicable,

    /// <summary>확인하지 못함.</summary>
    Unknown,
}

/// <summary>한 항목의 진단 결과.</summary>
/// <param name="State">상태.</param>
/// <param name="Summary">교사에게 보여줄 한 줄.</param>
/// <param name="Details">자세한 줄들.</param>
public sealed record CheckResult(CheckState State, string Summary, IReadOnlyList<string>? Details = null)
{
    public IReadOnlyList<string> Lines => Details ?? Array.Empty<string>();

    public static CheckResult Ok(string summary, params string[] details) => new(CheckState.Ok, summary, details);
    public static CheckResult NeedsFix(string summary, params string[] details) => new(CheckState.NeedsFix, summary, details);
    public static CheckResult NotApplicable(string summary) => new(CheckState.NotApplicable, summary);
    public static CheckResult Unknown(string summary, params string[] details) => new(CheckState.Unknown, summary, details);
}

/// <summary>수정 시도의 결과.</summary>
public enum FixOutcome
{
    /// <summary>이미 돼 있어서 할 일이 없었음.</summary>
    AlreadyOk,

    /// <summary>Teavel 이 끝까지 처리함.</summary>
    Fixed,

    /// <summary>
    /// 여기까지가 자동으로 되는 한계 — 창을 띄웠고, 나머지는 교사가 직접 해야 함.
    /// 로그인처럼 비밀번호가 필요한 일이 여기 해당한다.
    /// </summary>
    ManualStepStarted,

    /// <summary>
    /// 관리자 권한이 있어야 함 — <b>고칠 수 없는 것이 아니라 권한만 올리면 되는 것</b>이다.
    /// </summary>
    /// <remarks>
    /// 이것을 Failed 로 뭉뚱그리면 화면에 '실패' 로 나가고, 선생님은 안 되는 일인 줄 안다.
    /// 실제로는 승인 창 한 번이면 되는 경우가 대부분이다(학교 PC 의 선생님 계정은
    /// 대개 이미 관리자 그룹에 있다). 그래서 따로 둔다 — 부르는 쪽이 이걸 보고
    /// 권한을 올려 드리겠다고 여쭐 수 있게.
    /// </remarks>
    NeedsElevation,

    /// <summary>실패.</summary>
    Failed,

    /// <summary>이 환경에서는 할 수 없음.</summary>
    NotSupported,
}

/// <summary>수정 결과.</summary>
/// <param name="Outcome">어떻게 끝났는지.</param>
/// <param name="Summary">교사에게 보여줄 한 줄.</param>
/// <param name="NextSteps">교사가 직접 해야 할 일(있다면) — 클릭 순서를 그대로 적는다.</param>
public sealed record FixResult(FixOutcome Outcome, string Summary, IReadOnlyList<string>? NextSteps = null)
{
    public IReadOnlyList<string> Steps => NextSteps ?? Array.Empty<string>();

    public static FixResult AlreadyOk(string summary) => new(FixOutcome.AlreadyOk, summary);
    public static FixResult Fixed(string summary, params string[] steps) => new(FixOutcome.Fixed, summary, steps);
    public static FixResult Manual(string summary, params string[] steps) => new(FixOutcome.ManualStepStarted, summary, steps);
    public static FixResult NeedsAdmin(string summary, params string[] steps) => new(FixOutcome.NeedsElevation, summary, steps);
    public static FixResult Failed(string summary, params string[] steps) => new(FixOutcome.Failed, summary, steps);
    public static FixResult NotSupported(string summary) => new(FixOutcome.NotSupported, summary);
}

/// <summary>
/// 기반 설정 항목 하나 — 진단(Check)과 수정(Fix)이 짝을 이룬다.
///
/// 두 가지를 지킨다:
///   · 몇 번을 돌려도 안전하다(이미 돼 있으면 AlreadyOk 로 끝난다).
///   · 자동으로 할 수 없는 일은 그런 척하지 않는다 — 창을 띄우고 ManualStepStarted 로 알린다.
/// </summary>
public interface ISetupTask
{
    /// <summary>"onedrive.signin" 형태의 고유 id.</summary>
    string Id { get; }

    /// <summary>교사에게 보여줄 이름.</summary>
    string Title { get; }

    /// <summary>왜 필요한지 — 교사가 "이걸 왜 해야 하나" 를 알 수 있게.</summary>
    string Why { get; }

    /// <summary>지금 상태를 확인한다. 아무것도 바꾸지 않는다.</summary>
    Task<CheckResult> CheckAsync(CancellationToken ct = default);

    /// <summary>손볼 수 있는 만큼 손본다.</summary>
    Task<FixResult> FixAsync(CancellationToken ct = default);
}
