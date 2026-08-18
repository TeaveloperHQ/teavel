namespace Teavel.Setup;

/// <summary>이 Windows 가 아직 보안 패치를 받는지.</summary>
public enum SupportState
{
    /// <summary>받고 있다.</summary>
    Supported,

    /// <summary>곧 끊긴다(90일 안).</summary>
    EndingSoon,

    /// <summary>이미 끊겼다 — 새 보안 구멍이 나와도 막아 주지 않는다.</summary>
    Ended,

    /// <summary>판을 알아보지 못했다. 모르는 것을 안다고 하지 않는다.</summary>
    Unknown,
}

/// <summary>지원 상태와, 교사에게 할 말.</summary>
/// <param name="State">상태.</param>
/// <param name="Version">"22H2" 같은 판. 못 읽었으면 빈 문자열.</param>
/// <param name="EndsOn">지원이 끝나는(끝난) 날. 모르면 null.</param>
public sealed record WindowsSupportInfo(SupportState State, string Version, DateOnly? EndsOn)
{
    /// <summary>끝난 지 며칠 됐는지. 아직 안 끝났으면 음수, 모르면 null.</summary>
    public int? DaysPastEnd(DateOnly today)
        => EndsOn is { } end ? today.DayNumber - end.DayNumber : null;
}

/// <summary>
/// Windows 판마다 보안 패치가 언제까지 오는지.
///
/// <para>
/// <b>왜 Teavel 이 이걸 봐야 하는가.</b> 학교는 컴퓨터 세팅을 업체에 맡기고, 업체는
/// 하드디스크에 만들어 둔 이미지를 그대로 복사한다. 그 이미지는 만든 날에 멈춰 있어서
/// 교사가 처음 켤 때 이미 두 해 넘게 묵은 Windows 인 일이 흔하다.
/// </para>
/// <para>
/// 그러면 <b>보안 패치가 안 오는 것으로 끝나지 않는다.</b> 학교 계정을 잇는 일부터 막힌다 —
/// 계정을 잇는 데 쓰는 부품이 Windows 안에 있는데 그게 옛날 것이기 때문이다.
/// 실기에서 그렇게 막혔다(22H2, 2024년 2월에 멈춘 이미지). 그래서 이 확인이
/// <b>계정보다 먼저</b> 와야 한다. 아래 것을 아무리 붙들어도 여기가 막혀 있으면 안 된다.
/// </para>
/// <para>
/// 날짜는 마이크로소프트가 정한 것이라 우리가 계산할 수 없다. 표로 둔다.
/// 모르는 판은 <see cref="SupportState.Unknown"/> 이다 — 짐작해서 '괜찮다' 고 하면
/// 정작 끊긴 컴퓨터를 그냥 넘기게 된다.
/// </para>
/// </summary>
public static class WindowsSupport
{
    /// <summary>이 안에 끝나면 미리 알린다.</summary>
    private const int SoonDays = 90;

    // Home·Pro 는 24개월, Enterprise·Education 은 36개월이라 표가 다르다.
    // 학교 컴퓨터는 대개 Home 이나 Pro 다(Education 판을 받은 학교는 드물다).

    private static readonly Dictionary<string, DateOnly> Win11Consumer = new(StringComparer.OrdinalIgnoreCase)
    {
        ["21H2"] = new(2023, 10, 10),
        ["22H2"] = new(2024, 10, 8),
        ["23H2"] = new(2025, 11, 11),
        ["24H2"] = new(2026, 10, 13),
        ["25H2"] = new(2027, 10, 12),
    };

    private static readonly Dictionary<string, DateOnly> Win11Business = new(StringComparer.OrdinalIgnoreCase)
    {
        ["21H2"] = new(2024, 10, 8),
        ["22H2"] = new(2025, 10, 14),
        ["23H2"] = new(2026, 11, 10),
        ["24H2"] = new(2027, 10, 12),
        ["25H2"] = new(2028, 10, 10),
    };

    /// <summary>Windows 10 은 판을 가리지 않고 2025-10-14 에 끝났다.</summary>
    private static readonly DateOnly Windows10End = new(2025, 10, 14);

    /// <summary>빌드 번호가 이 위면 Windows 11 이다.</summary>
    public const int Windows11Build = 22000;

    /// <summary>
    /// 지금 이 컴퓨터가 패치를 받는지 판단한다.
    /// </summary>
    /// <param name="displayVersion">"22H2" 같은 것. 레지스트리의 DisplayVersion.</param>
    /// <param name="build">CurrentBuild.</param>
    /// <param name="isBusinessEdition">Enterprise·Education 이면 true(더 오래 지원한다).</param>
    /// <param name="today">오늘. 시험에서 날짜를 고정할 수 있게 받는다.</param>
    public static WindowsSupportInfo Evaluate(
        string? displayVersion, int build, bool isBusinessEdition, DateOnly today)
    {
        var version = displayVersion?.Trim() ?? "";

        // Windows 10 은 판을 따질 것 없이 끝났다.
        if (build > 0 && build < Windows11Build)
            return Judge(version.Length > 0 ? version : "Windows 10", Windows10End, today);

        if (version.Length == 0) return new WindowsSupportInfo(SupportState.Unknown, "", null);

        var table = isBusinessEdition ? Win11Business : Win11Consumer;

        // 표에 없는 판 — 우리가 모르는 새 판일 수도, 아주 옛 판일 수도 있다.
        // 어느 쪽인지 모르면서 '괜찮다' 고 하지 않는다.
        if (!table.TryGetValue(version, out var end))
            return new WindowsSupportInfo(SupportState.Unknown, version, null);

        return Judge(version, end, today);
    }

    private static WindowsSupportInfo Judge(string version, DateOnly end, DateOnly today)
    {
        var state = today > end ? SupportState.Ended
                  : today.DayNumber + SoonDays >= end.DayNumber ? SupportState.EndingSoon
                  : SupportState.Supported;

        return new WindowsSupportInfo(state, version, end);
    }
}
