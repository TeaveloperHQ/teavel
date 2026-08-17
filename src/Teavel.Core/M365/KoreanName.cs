using System.Text.RegularExpressions;

namespace Teavel.M365;

/// <summary>성과 이름을 합쳐 본 결과.</summary>
/// <param name="Merged">합친 이름. 합칠 수 없으면 빈 문자열.</param>
/// <param name="Why">어떻게 합쳤는지 — 관리자에게 그대로 보여 준다.</param>
/// <param name="Certain">확실한지. false 면 사람이 봐야 한다.</param>
public sealed record MergedName(string Merged, string Why, bool Certain);

/// <summary>
/// 나뉘어 있는 성과 이름을 <b>한국식 한 덩어리</b>로 합친다.
///
/// <para>
/// 교육청 포털로 교사 계정을 만들면 성(LastName)과 이름(FirstName)이 나뉘어 들어간다.
/// 서양식 규격을 그대로 따른 것인데, <b>한국에서는 이렇게 두면 못 쓴다.</b>
/// 김·이·박이 학교마다 수십 명이라 성만으로는 아무도 못 찾고,
/// 화면에 '하늘 김' 처럼 뒤집혀 보이기도 한다.
/// </para>
/// <para>
/// 그래서 표시 이름을 <c>성+이름</c> 한 덩어리로 만든다 — <c>김하늘</c>.
/// 학생 표시 이름이 <c>10101김하늘</c> 인 것과 짝을 이뤄, 이름으로 찾을 때
/// 교사와 학생이 깔끔하게 갈린다.
/// </para>
/// </summary>
public static class KoreanName
{
    /// <summary>두 글자 성. 이것 말고는 모두 한 글자로 본다.</summary>
    private static readonly string[] TwoLetterFamily =
    {
        "남궁", "황보", "제갈", "사공", "선우", "서문", "독고", "동방", "望", "어금",
    };

    private static readonly Regex HangulOnly = new(@"^[가-힣]+$", RegexOptions.Compiled);

    /// <summary>
    /// 성과 이름을 합친다.
    /// </summary>
    /// <remarks>
    /// 어느 칸에 성이 들어 있는지는 학교·포털마다 다르다. 규격 이름(FirstName/LastName)을
    /// 믿지 않고 <b>글자 수로 판단한다</b> — 한국 성은 거의 한 글자이고 이름은 두 글자다.
    /// 둘 다 같은 길이라 가릴 수 없으면 규격대로 성+이름으로 두되 확실하지 않다고 표시한다.
    /// </remarks>
    public static MergedName Merge(string? firstName, string? lastName)
    {
        var f = (firstName ?? "").Trim();
        var l = (lastName ?? "").Trim();

        if (f.Length == 0 && l.Length == 0)
            return new MergedName("", "성과 이름이 모두 비어 있습니다.", false);

        if (f.Length == 0) return new MergedName(l, "이름 칸이 비어 있어 성만 씁니다.", false);
        if (l.Length == 0) return new MergedName(f, "성 칸이 비어 있어 이름만 씁니다.", false);

        // 한글이 아니면 손대지 않는다. 외국인 교사 계정이 섞여 있을 수 있다.
        if (!HangulOnly.IsMatch(f) || !HangulOnly.IsMatch(l))
            return new MergedName($"{l}{f}", "한글 이름이 아니라 그대로 이어 붙였습니다.", false);

        // 두 글자 성이면 그쪽이 성이다.
        if (TwoLetterFamily.Contains(l)) return new MergedName($"{l}{f}", $"'{l}' 은(는) 두 글자 성입니다.", true);
        if (TwoLetterFamily.Contains(f)) return new MergedName($"{f}{l}", $"'{f}' 은(는) 두 글자 성입니다. 칸이 바뀌어 있습니다.", true);

        // 한국 성은 거의 한 글자다. 한 글자인 쪽을 성으로 본다.
        if (l.Length == 1 && f.Length >= 2) return new MergedName($"{l}{f}", $"성 '{l}' + 이름 '{f}'", true);
        if (f.Length == 1 && l.Length >= 2) return new MergedName($"{f}{l}", $"성 '{f}' + 이름 '{l}' — 칸이 바뀌어 들어가 있습니다.", true);

        // 둘 다 한 글자거나 둘 다 여러 글자다. 규격대로 두되 사람이 봐야 한다.
        return new MergedName($"{l}{f}", $"'{l}' 과 '{f}' 중 어느 쪽이 성인지 가릴 수 없었습니다.", false);
    }

    /// <summary>
    /// 표시 이름을 손봐야 하는지.
    /// </summary>
    /// <remarks>
    /// 이미 합쳐져 있으면 건드리지 않는다. 빈칸만 다른 경우('김 하늘')도 손봐야 한다 —
    /// 이름으로 찾을 때 빈칸은 걷어내지만, 화면에 보이는 모양은 고쳐 두는 편이 낫다.
    /// </remarks>
    public static bool NeedsFixing(string displayName, MergedName merged)
    {
        if (merged.Merged.Length == 0) return false;
        return !string.Equals(displayName.Trim(), merged.Merged, StringComparison.Ordinal);
    }
}
