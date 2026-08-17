namespace Teavel.Intent;

/// <summary>말을 걸어 온 결.</summary>
public enum SmallTalkKind
{
    /// <summary>인사. "안녕", "안녕하세요", "처음이야"</summary>
    Greeting,

    /// <summary>고마움·인사치레. "고마워", "수고했어"</summary>
    Thanks,

    /// <summary>막막해한다. "뭘 해야 할지 모르겠어"</summary>
    Lost,

    /// <summary>그 밖의 말 — 우리가 해 줄 수 없는 이야기를 포함한다.</summary>
    Other,
}

/// <summary>
/// 도구를 부르는 말이 아닐 때, 어떤 결의 말인지 <b>낱말로</b> 가려낸다.
///
/// <para>
/// <b>여기서 언어 모델을 부르지 않는다.</b> 라우터가 이미 '이건 도구 요청이 아니다' 까지
/// 판단해서 여기로 보냈다. 남은 일은 인사냐 감사냐를 가르는 것뿐인데, 그러자고 문맥을
/// 하나 더 만들면 KV 캐시(RAM)와 호출 1~2초를 매번 치른다. 되돌아오는 것은 <b>미리 써 둔
/// 문장 넷 중 하나</b>고, 못 갈라도 손해가 없다 — 그때는 할 수 있는 일을 보여 주면 된다.
/// </para>
/// <para>
/// 어차피 화면에 나가는 문장은 사람이 쓴 것이다. 고르는 데까지 모델을 쓸 값어치가 없다.
/// </para>
/// </summary>
public static class SmallTalk
{
    // 끝바꿈은 형태소 분석기가 먹어 주므로 어간만 적어 둔다.
    // ('고마워' · '고맙습니다' · '고마웠어' → 모두 '고맙')
    private static readonly string[] GreetingWords =
        { "안녕", "하이", "반갑", "처음", "이야기", "대화", "잡담", "말", "심심" };

    private static readonly string[] ThanksWords =
        { "고맙", "고마", "감사", "수고", "잘했", "훌륭", "최고" };

    private static readonly string[] LostWords =
        { "모르", "막막", "어렵", "헷갈", "어떻게", "뭐부터", "뭐 부터", "처음이라" };

    /// <summary>말의 결을 가른다. 애매하면 <see cref="SmallTalkKind.Other"/>.</summary>
    public static SmallTalkKind Classify(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return SmallTalkKind.Other;

        var text = utterance.Trim();

        // 형태소가 있으면 어간으로, 없으면 글자 그대로 본다.
        var stems = Morphemes.Content(text);
        var hay = stems.Count > 0 ? string.Join(' ', stems) + ' ' + text : text;

        // 막막함이 먼저다 — "뭘 해야 할지 모르겠는데 안녕" 같은 말에서
        // 인사보다 도와 달라는 쪽이 진짜 용건이다.
        if (Has(hay, LostWords)) return SmallTalkKind.Lost;
        if (Has(hay, ThanksWords)) return SmallTalkKind.Thanks;
        if (Has(hay, GreetingWords)) return SmallTalkKind.Greeting;

        return SmallTalkKind.Other;
    }

    private static bool Has(string hay, string[] needles)
    {
        foreach (var n in needles)
            if (hay.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }
}
