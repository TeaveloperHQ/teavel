namespace Teavel.Tools;

/// <summary>도구가 속한 영역 — 메뉴 묶음이자 LLM 프롬프트의 분류.</summary>
public enum ToolCategory
{
    /// <summary>엑셀 — 성적·명단 처리.</summary>
    Excel,

    /// <summary>파일·폴더 정리.</summary>
    Files,

    /// <summary>아웃룩 — 메일·일정.</summary>
    Outlook,

    /// <summary>워드 — 문서 생성·변환.</summary>
    Word,

    /// <summary>PC 세팅 — 프린터 등, 말로 시킬 수 있는 세팅 작업.</summary>
    Setup,

    /// <summary>
    /// 학교 전체 — 그룹·Teams 구성. 도구 하나가 아니라 <b>한 판</b>이 필요한 일이다.
    /// </summary>
    /// <remarks>
    /// 이것들은 PowerShell 함수 하나로 끝나지 않고 여러 단계를 사람과 주고받으며 간다.
    /// 그래서 Module 을 <c>@flow</c> 로 두어 도구가 아니라 흐름임을 표시한다.
    /// 목록에 함께 올려 두는 이유는 <b>언어 모델과 낱말 라우터가 이것도 후보로 보게</b> 하기 위해서다 —
    /// 따로 빼 두면 "작년 팀 백업해줘" 를 알아들을 방법이 없다.
    /// </remarks>
    School,
}

/// <summary>인자의 종류. CLI 입력 검증과 LLM 힌트에 함께 쓰인다.</summary>
public enum ToolParamKind
{
    /// <summary>자유 문자열.</summary>
    Text,

    /// <summary>기존 파일 경로 — 존재해야 한다.</summary>
    FilePath,

    /// <summary>기존 폴더 경로 — 존재해야 한다.</summary>
    FolderPath,

    /// <summary>만들어질 경로 — 없어도 된다(부모 폴더만 있으면 됨).</summary>
    OutputPath,

    /// <summary>정수.</summary>
    Number,

    /// <summary>예/아니오.</summary>
    Bool,

    /// <summary><see cref="ToolParam.Choices"/> 중 하나.</summary>
    Choice,
}

/// <summary>
/// 갈래 하나 — 넘길 값과, 교사에게 보여 줄 말과, 이렇게 쳐도 알아들을 말들.
/// </summary>
/// <remarks>
/// <para>
/// 예전에는 갈래를 <c>{ "school", "personal", "unknown" }</c> 처럼 값만 늘어놓았다.
/// 그 값은 PowerShell 매개변수라 영어인데, 화면에도 그대로 나가고 <b>그대로 쳐야만</b> 받아 줬다.
/// 그래서 이렇게 끝났다.
/// </para>
/// <code>
///   이 컴퓨터는 [school / personal / unknown]: 학교컴퓨터야
///   ✗ '이 컴퓨터는' 은(는) school / personal / unknown 중 하나여야 합니다.
/// </code>
/// <para>
/// 한국어로 묻고 영어로 답하라고 한 셈이고, 틀리면 하던 일이 통째로 날아갔다.
/// 이제 값과 <b>보여 줄 말</b>을 갈라 둔다 — 화면에는 우리말이 나가고, 함수에는 값이 간다.
/// </para>
/// </remarks>
/// <param name="Value">PowerShell 함수에 넘어갈 값. 이것만 함수에 닿는다.</param>
/// <param name="Label">교사에게 보여 줄 말.</param>
/// <param name="Words">번호 대신 이렇게 쳐도 이 갈래로 알아듣는다.</param>
public sealed record ToolChoice(string Value, string Label, params string[] Words);

/// <summary>도구 인자 하나.</summary>
/// <param name="Name">PowerShell 함수의 매개변수 이름과 정확히 같아야 한다.</param>
/// <param name="Kind">값의 종류.</param>
/// <param name="Label">교사에게 보여줄 이름(메뉴 모드에서 물어볼 때).</param>
/// <param name="Description">무엇인지 — LLM 이 값을 뽑을 때 쓰는 설명이기도 하다.</param>
/// <param name="Required">필수인지. false면 <paramref name="Default"/> 가 쓰인다.</param>
/// <param name="Default">기본값(문자열 표현). 필수가 아닐 때만 의미가 있다.</param>
/// <param name="Choices"><see cref="ToolParamKind.Choice"/> 일 때 고를 수 있는 갈래들.</param>
public sealed record ToolParam(
    string Name,
    ToolParamKind Kind,
    string Label,
    string Description,
    bool Required = true,
    string? Default = null,
    IReadOnlyList<ToolChoice>? Choices = null)
{
    /// <summary>갈래들. 갈래형이 아니면 빈 목록.</summary>
    public IReadOnlyList<ToolChoice> Options => Choices ?? Array.Empty<ToolChoice>();

    /// <summary>PowerShell 에 넘어갈 수 있는 값들 — 검증과 LLM 힌트에 쓴다.</summary>
    public IEnumerable<string> Values => Options.Select(c => c.Value);

    /// <summary>
    /// 교사가 친 말을 갈래로 맞춘다. 못 맞추면 null.
    /// </summary>
    /// <remarks>
    /// 값 그대로 친 것도, 우리말로 친 것도 받는다. 언어 모델이 뽑아 온 값도 여기를 지나므로
    /// 모델이 "학교" 라고 적어 와도 school 로 내려간다.
    /// </remarks>
    public ToolChoice? Match(string said)
    {
        var t = said.Trim();
        if (t.Length == 0) return null;

        foreach (var c in Options)
            if (string.Equals(c.Value, t, StringComparison.OrdinalIgnoreCase)) return c;

        // 우리말로 친 경우. 붙여 쓰거나 조사가 붙어도 걸리도록 '품고 있는지' 로 본다
        // ("학교컴퓨터야" 안에 "학교" 가 있다).
        var packed = t.Replace(" ", "").ToLowerInvariant();
        foreach (var c in Options)
            foreach (var w in c.Words)
                if (packed.Contains(w.Replace(" ", "").ToLowerInvariant(), StringComparison.Ordinal)) return c;

        return null;
    }
}

/// <summary>
/// 도구 하나의 선언. 실제 동작은 PowerShell 모듈의 함수가 하고,
/// 이 선언은 (1) 교사용 메뉴, (2) LLM 프롬프트, (3) 인자 검증 세 곳에 공통으로 쓰인다.
/// </summary>
/// <param name="Id">"excel.merge_workbooks" 형태의 고유 id. LLM 이 이 값을 고른다.</param>
/// <param name="Title">교사에게 보여줄 한 줄 이름.</param>
/// <param name="Category">영역.</param>
/// <param name="Description">무엇을 하는지 — LLM 이 고를 때 읽는 설명.</param>
/// <param name="Examples">교사가 이렇게 말할 법한 문장들. LLM 프롬프트의 few-shot 이 된다.</param>
/// <param name="Parameters">인자들.</param>
/// <param name="Module">PowerShell 모듈 이름(scripts/&lt;Module&gt;.psm1).</param>
/// <param name="Function">호출할 PowerShell 함수 이름.</param>
/// <param name="Mutating">파일을 만들거나 바꾸는지. true면 실행 전에 교사 확인을 받는다.</param>
/// <param name="TimeoutSeconds">제한 시간. Office COM 은 느릴 수 있어 넉넉히 잡는다.</param>
public sealed record ToolSpec(
    string Id,
    string Title,
    ToolCategory Category,
    string Description,
    IReadOnlyList<string> Examples,
    IReadOnlyList<ToolParam> Parameters,
    string Module,
    string Function,
    bool Mutating,
    int TimeoutSeconds = 600)
{
    /// <summary>
    /// 이 도구를 가리킬 때 교사가 쓸 법한 다른 말들.
    /// </summary>
    /// <remarks>
    /// 낱말 라우터가 틀리는 이유는 대부분 조사·어미가 아니라 <b>유의어</b> 다 —
    /// 카탈로그가 "합치기" 라고 적어 둔 것을 교사는 "묶어줘", "모아줘", "한 장으로" 라고 말한다.
    /// 여기에 그 말들을 적어 두면 낱말 라우터와 언어 모델 프롬프트가 함께 덕을 본다.
    ///
    /// 여러 도구에 같은 말이 겹쳐도 된다(실제로 모호한 말이 있다). 겹치면 점수가 나뉠 뿐이고,
    /// 그때는 확신이 낮아져 교사에게 되묻게 되므로 넘겨짚는 것보다 낫다.
    /// </remarks>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    /// <summary>이름으로 인자 선언을 찾는다.</summary>
    public ToolParam? Param(string name)
        => Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
