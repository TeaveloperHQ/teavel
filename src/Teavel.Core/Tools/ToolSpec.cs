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

/// <summary>도구 인자 하나.</summary>
/// <param name="Name">PowerShell 함수의 매개변수 이름과 정확히 같아야 한다.</param>
/// <param name="Kind">값의 종류.</param>
/// <param name="Label">교사에게 보여줄 이름(메뉴 모드에서 물어볼 때).</param>
/// <param name="Description">무엇인지 — LLM 이 값을 뽑을 때 쓰는 설명이기도 하다.</param>
/// <param name="Required">필수인지. false면 <paramref name="Default"/> 가 쓰인다.</param>
/// <param name="Default">기본값(문자열 표현). 필수가 아닐 때만 의미가 있다.</param>
/// <param name="Choices"><see cref="ToolParamKind.Choice"/> 일 때 허용되는 값들.</param>
public sealed record ToolParam(
    string Name,
    ToolParamKind Kind,
    string Label,
    string Description,
    bool Required = true,
    string? Default = null,
    IReadOnlyList<string>? Choices = null);

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
