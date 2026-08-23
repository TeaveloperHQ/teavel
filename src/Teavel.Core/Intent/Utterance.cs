namespace Teavel.Intent;

/// <summary>
/// 라우터에 넘기기 전에 <b>말이 되는 말인지</b> 먼저 본다.
///
/// <para>
/// 실기에서 이렇게 됐다.
/// </para>
/// <code>
///   > 1
///     파일 이름 일괄 바꾸기
///         이름을 바꿀 파일들이 있는 폴더:      ← 한 글자에 도구가 열렸다
///
///   > ㅋㅋ
///         언어 모델을 읽는 중입니다(1,065MB)…  ← 오타 하나에 1GB 를 읽는다
/// </code>
/// <para>
/// 앞엣것이 더 나쁘다. 도구 중에는 <b>지우는 것</b>도 있는데, 오타 한 글자가 그 문 앞까지
/// 데려가면 안 된다. 뒤엣것도 그냥 두면 오타 한 번에 수십 초를 기다리게 된다 —
/// 그 시간이면 교사는 프로그램이 멈춘 줄 안다.
/// </para>
/// <para>
/// 그래서 <b>낱말 라우터도 언어 모델도 부르기 전에</b> 여기서 끊는다.
/// 넉넉하게 통과시키되(모호하면 통과), 명백히 말이 아닌 것만 막는다 —
/// 진짜 요청을 막는 쪽이 오타를 통과시키는 쪽보다 훨씬 나쁘다.
/// </para>
/// </summary>
public static class Utterance
{
    /// <summary>요청으로 볼 만한 말인지.</summary>
    public static bool LooksLikeRequest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var t = text.Trim();

        // 한 글자로 부탁할 수 있는 일은 없다. '점검' 같은 명령은 여기 오기 전에 처리된다.
        if (t.Length < 2) return false;

        var hangul = 0;      // 완성된 한글 글자 (가 ~ 힣)
        var latin = 0;       // 로마자

        foreach (var c in t)
        {
            // 낱자(ㄱ ~ ㅣ)는 세지 않는다. 'ㅋㅋ' · 'ㅁ' 은 글자가 아니라 소리다.
            if (c is >= '가' and <= '힣') hangul++;
            else if (c is < 'ㄱ' or > 'ㅣ' && char.IsLetter(c)) latin++;
        }

        // 한글이 한 글자라도 있으면 말로 본다. '3반' · '2반 성적' 을 막으면 안 된다.
        if (hangul > 0) return true;

        // 한글이 없다면 로마자 두 자 이상은 있어야 한다. 'pdf' · 'csv' · 'm365' · 'onedrive'.
        return latin >= 2;
    }
}
