using System.Security.Cryptography;

namespace Teavel.M365;

/// <summary>
/// 학생에게 건네줄 <b>임시</b> 비밀번호를 만든다.
///
/// <para>
/// 요점은 <b>종이에 적어 아이에게 주고, 아이가 그것을 보고 친다</b>는 것이다.
/// 그래서 무작위 문자열은 답이 아니다 — <c>xK9#mQ2$</c> 는 초등학생이 못 친다.
/// 그렇다고 <c>school123</c> 처럼 하면 서로의 것을 짐작할 수 있다.
/// </para>
/// <para>
/// 그래서 <b>낱말 둘 + 숫자 넷</b>으로 한다. 읽어 주기 쉽고, 자리 수가 넉넉해
/// 옆자리 것을 짐작할 수 없다.
/// </para>
/// </summary>
public static class PasswordMaker
{
    /// <summary>
    /// 헷갈리는 글자를 뺀 낱말들.
    /// </summary>
    /// <remarks>
    /// <b>l·I·O 가 들어간 낱말을 쓰지 않는다.</b> 종이에 적힌 <c>Ill</c> 과 <c>III</c> 은
    /// 구별되지 않고, 그것을 아이가 세 번 틀리면 결국 담임이 관리자에게 다시 온다.
    /// 그리고 뜻이 나쁘게 읽힐 낱말이 섞이지 않게 자연·동물로만 골랐다.
    /// </remarks>
    private static readonly string[] Words =
    {
        "Sky", "Sun", "Star", "Wave", "Wind", "Snow", "Rain", "Tree", "Rock", "Sand",
        "Bear", "Deer", "Duck", "Fish", "Frog", "Hawk", "Swan", "Whale", "Zebra", "Tiger",
        "Apple", "Berry", "Cake", "Grape", "Peach", "Beans", "Bread", "Honey",
        "Green", "Amber", "Aqua", "Sunset", "Summer", "Autumn", "Spring", "Water",
    };

    /// <summary>임시 비밀번호 하나. <c>Sky-Tiger-2847</c> 같은 모양.</summary>
    /// <remarks>
    /// Entra 의 복잡도(대문자·소문자·숫자·기호 중 셋 이상, 8자 이상)를 늘 넘긴다 —
    /// 낱말이 대문자로 시작하고 소문자가 이어지며, 붙임표와 숫자가 들어가기 때문이다.
    /// </remarks>
    public static string One()
    {
        var a = Words[RandomNumberGenerator.GetInt32(Words.Length)];

        string b;
        do { b = Words[RandomNumberGenerator.GetInt32(Words.Length)]; }
        while (string.Equals(a, b, StringComparison.Ordinal));

        // 1000~9999. 앞자리가 0 이면 종이에서 빠뜨리고 적는 일이 생긴다.
        var n = RandomNumberGenerator.GetInt32(1000, 10000);

        return $"{a}-{b}-{n}";
    }

    /// <summary>
    /// 사람 수만큼. <b>서로 다른 것이 나온다.</b>
    /// </summary>
    /// <remarks>
    /// 한 반에 같은 비밀번호가 둘 나오면 아이들이 그것을 알아채고, 그 순간
    /// 비밀번호가 비밀이 아니게 된다. 낱말 짝만 <c>36 × 35</c> 가지라 겹칠 일이 드물지만
    /// 드문 것과 없는 것은 다르다.
    /// </remarks>
    public static IReadOnlyList<string> Many(int count)
    {
        var made = new HashSet<string>(StringComparer.Ordinal);
        while (made.Count < count) made.Add(One());
        return made.ToList();
    }
}
