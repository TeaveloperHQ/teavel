using System.Text.RegularExpressions;

namespace Teavel.Roster;

/// <summary>
/// 명단에서 우리가 필요로 하는 것. 이 여섯 가지가 전부다.
/// </summary>
/// <remarks>
/// 학교마다 엑셀 모양이 다르므로 <b>양식을 정해 주고 맞춰 오라고 하지 않는다.</b>
/// 어떤 표가 오든 열 이름을 읽어 이 틀에 꽂는 것이 Teavel 의 일이다.
/// </remarks>
public enum RosterField
{
    /// <summary>학년. 1~6.</summary>
    Grade,

    /// <summary>반. 학급 번호.</summary>
    ClassNo,

    /// <summary>번호. 반 안에서의 출석 번호.</summary>
    Number,

    /// <summary>학번. 대개 학년+반+번호를 이어 붙인 것.</summary>
    StudentId,

    /// <summary>이름.</summary>
    Name,

    /// <summary>
    /// 표시 이름 — Teams 에서 보이는 이름. 학번+이름 꼴이다(예: 10101홍길동).
    /// 열이 없으면 학번과 이름으로 만들어 채운다.
    /// </summary>
    DisplayName,

    /// <summary>로그인 아이디. 팀에 넣을 때 쓰는 값이라 이것이 없으면 배정을 못 한다.</summary>
    Upn,
}

/// <summary>열 이름 하나를 어떤 자리로 볼지 판단하는 규칙.</summary>
/// <param name="Field">그 자리.</param>
/// <param name="Label">화면에 보여 줄 이름.</param>
/// <param name="Aliases">이 자리로 볼 이름들. 정규화한 뒤 견준다.</param>
/// <param name="Never">
/// 이 낱말이 들어 있으면 <b>절대</b> 이 자리가 아니다.
/// '학년도' 는 학년이 아니라 연도이고, '일반' 안의 '반' 은 학급이 아니다.
/// </param>
/// <param name="Looks">값이 이 모양이면 그 자리가 맞다고 본다. 열 이름이 쓸모없을 때 쓴다.</param>
public sealed record FieldRule(
    RosterField Field,
    string Label,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Never,
    Func<string, bool> Looks);

/// <summary>
/// 열 이름을 어떤 자리로 볼지 정해 놓은 규칙 모음.
///
/// <para>
/// 여기서 조심할 것은 <b>짧은 낱말이 다른 낱말 안에 들어 있는 경우</b>다.
/// '반' 은 '일반계' · '반편성' 안에도 있고, '학년' 은 '학년도' 안에도 있다.
/// 부분 일치만으로 판단하면 연도 열을 학년으로 읽어 1학년이 2025학년이 된다.
/// 그래서 정규화한 <b>완전 일치</b>를 가장 세게 보고, 부분 일치는 약하게 본다.
/// </para>
/// </summary>
public static class RosterSchema
{
    public static readonly IReadOnlyList<FieldRule> Rules = new[]
    {
        new FieldRule(RosterField.Grade, "학년",
            Aliases: new[] { "학년", "학년구분", "grade", "학년명" },
            Never:   new[] { "학년도", "년도", "연도", "year" },
            Looks:   v => IsIntBetween(v, 1, 6)),

        new FieldRule(RosterField.ClassNo, "반",
            Aliases: new[] { "반", "학급", "분반", "반번호", "학급번호", "class", "반명", "학급명" },
            Never:   new[] { "반편성", "일반", "반장", "반영" },
            Looks:   v => IsIntBetween(v, 1, 30)),

        new FieldRule(RosterField.Number, "번호",
            Aliases: new[] { "번호", "번", "출석번호", "학생번호", "no", "번호순", "출석" },
            Never:   new[] { "학번", "전화번호", "휴대번호", "연락처", "전화" },
            Looks:   v => IsIntBetween(v, 1, 60)),

        new FieldRule(RosterField.StudentId, "학번",
            Aliases: new[] { "학번", "학생학번", "studentid", "sid", "학적번호" },
            Never:   new[] { "전화", "연락처" },
            // 학번은 대개 학년(1)+반(2)+번호(2) = 5자리다. 4~7자리 숫자면 학번으로 본다.
            Looks:   v => Regex.IsMatch(v.Trim(), @"^\d{4,7}$")),

        new FieldRule(RosterField.Name, "이름",
            Aliases: new[] { "이름", "성명", "학생명", "학생이름", "성함", "name", "학생" },
            // '표시이름' 안에도 '이름' 이 있다. 그쪽은 따로 자리가 있으므로 여기서 잘라 낸다.
            Never:   new[] { "표시", "display", "교사명", "담당교사", "교사", "학부모", "보호자",
                             "학교명", "과목명", "반명", "학급명", "파일명" },
            Looks:   v => Regex.IsMatch(v.Trim(), @"^[가-힣]{2,5}$")),

        new FieldRule(RosterField.DisplayName, "표시이름",
            Aliases: new[] { "표시이름", "표시명", "displayname", "display", "화면이름",
                             "teams이름", "표시되는이름", "계정표시이름" },
            Never:   new[] { "파일", "교사" },
            // 학번+이름 꼴이면 그것이 표시 이름이다 — 아주 또렷한 표시라 이름만으로 못 찾아도 잡힌다.
            Looks:   v => Regex.IsMatch(v.Trim(), @"^\d{4,7}[가-힣]{2,5}$")),

        new FieldRule(RosterField.Upn, "ID",
            Aliases: new[] { "id", "아이디", "계정", "메일", "이메일", "email", "mail", "upn",
                             "로그인", "로그인아이디", "사용자이름", "userprincipalname", "계정명", "ms계정" },
            Never:   new[] { "학번", "번호", "비밀번호", "암호", "password", "학교id" },
            Looks:   v => v.Contains('@') && v.Trim().Length > 3),
    };

    /// <summary>
    /// 견주기 전에 열 이름을 다듬는다.
    /// </summary>
    /// <remarks>
    /// 실제 학교 파일의 머리글은 '학  년' · '학년\n(1~3)' · '반 ' · '성 명' 처럼 온다.
    /// 빈칸·줄바꿈·괄호 안 설명·마침표를 걷어내야 같은 것을 같다고 볼 수 있다.
    /// </remarks>
    public static string Normalize(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) return "";

        var s = header.Trim();

        // 괄호 안은 대개 설명이다 — '학년(1~3)' · '이름(한글)'.
        s = Regex.Replace(s, @"[\(（\[［].*?[\)）\]］]", "");

        // 빈칸·줄바꿈·구분자를 전부 걷어낸다.
        s = Regex.Replace(s, @"[\s ._·・\-/]+", "");

        return s.ToLowerInvariant();
    }

    private static bool IsIntBetween(string v, int lo, int hi)
        => int.TryParse(v.Trim(), out var n) && n >= lo && n <= hi;
}
