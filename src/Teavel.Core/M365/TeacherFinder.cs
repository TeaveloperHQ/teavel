using System.Text.RegularExpressions;

namespace Teavel.M365;

/// <summary>이름으로 찾은 결과.</summary>
/// <param name="Matches">선생님으로 볼 만한 사람들. 잘 맞는 순.</param>
/// <param name="Students">
/// 이름은 맞지만 학생으로 보여 감춘 사람들.
/// <b>'없습니다' 라고 하면 안 되기 때문에</b> 따로 들고 있는다 — 있는데 감춘 것과
/// 아예 없는 것은 다르고, 관리자는 그 차이를 알아야 다음 수를 안다.
/// </param>
public sealed record TeacherSearch(
    IReadOnlyList<TeacherMatch> Matches,
    IReadOnlyList<TeacherMatch> Students);

/// <summary>이름으로 찾아낸 후보 하나.</summary>
/// <param name="User">그 사람.</param>
/// <param name="Score">얼마나 잘 맞는지. 100이 이름이 그대로 같은 것.</param>
/// <param name="Why">어떻게 찾았는지 — 관리자에게 그대로 보여 준다.</param>
public sealed record TeacherMatch(TenantUser User, int Score, string Why);

/// <summary>
/// 선생님을 <b>이름으로</b> 찾는다.
///
/// <para>
/// 학생은 명단 파일이 있어야 하지만 <b>선생님은 그럴 필요가 없다.</b>
/// 교사 계정은 교육청에서 받아 이미 만들어져 있으므로, 테넌트에서 찾기만 하면 된다.
/// 그래서 관리자에게 아이디를 묻지 않는다 — <b>성함만 받는다.</b>
/// 아이디는 모를 수 있어도 같이 근무하는 선생님 이름은 안다.
/// </para>
/// <para>
/// 학생을 걸러 내는 것이 요점이다. 학생 표시 이름은 <c>10301홍길동</c> 꼴이라
/// '홍길동' 으로 찾으면 학생이 함께 걸린다. 학생을 팀 소유자로 만들면
/// 그 아이가 반 전체를 지울 수 있다.
/// </para>
/// </summary>
public static class TeacherFinder
{
    // 학생 계정의 표시 이름. 숫자가 앞에 붙는다.
    private static readonly Regex StudentLike = new(@"^\d{3,7}\s*[가-힣]{2,5}$", RegexOptions.Compiled);

    /// <summary>이 점수 아래는 찾은 것이 아니다.</summary>
    public const int MinScore = 50;

    /// <summary>
    /// 이름으로 찾는다. 잘 맞는 순서로 준다.
    /// </summary>
    /// <param name="people">테넌트의 사람들.</param>
    /// <param name="typed">관리자가 적은 이름.</param>
    /// <param name="facultyBundle">
    /// 교사 라이선스 꾸러미. 알면 그쪽을 먼저 올린다. 몰라도 된다 —
    /// 모를 때는 학생처럼 생긴 계정을 뒤로 미는 것으로 대신한다.
    /// </param>
    public static TeacherSearch Find(
        IReadOnlyList<TenantUser> people, string typed, string? facultyBundle = null)
    {
        var want = Squeeze(typed);
        if (want.Length == 0)
            return new TeacherSearch(Array.Empty<TeacherMatch>(), Array.Empty<TeacherMatch>());

        var found = new List<TeacherMatch>();
        var students = new List<TeacherMatch>();

        foreach (var p in people)
        {
            if (UserDirectory.IsOutsider(p)) continue;

            // 라이선스가 없는 계정은 팀에 넣어도 못 들어온다. 소유자로 삼으면 더 나쁘다.
            if (p.AccountType.Equals("IneligibleUser", StringComparison.OrdinalIgnoreCase)) continue;

            var name = Squeeze(p.DisplayName);
            var local = Squeeze(LocalPart(p.Upn));
            var looksStudent = StudentLike.IsMatch(p.DisplayName.Trim());

            var score = 0;
            var why = "";

            if (name == want) { score = 100; why = "이름이 같습니다"; }
            // 성만 치는 것이 가장 흔한 검색이다 — '김' 으로 김씨 선생님을 다 보고 고른다.
            else if (name.StartsWith(want, StringComparison.Ordinal))
            { score = want.Length >= 2 ? 80 : 70; why = $"'{p.DisplayName}' 으로 시작합니다"; }
            else if (name.Contains(want, StringComparison.Ordinal) && want.Length >= 2)
            { score = 65; why = $"'{p.DisplayName}' 안에 있습니다"; }
            else if (local.Contains(want, StringComparison.Ordinal) && want.Length >= 2)
            { score = 55; why = $"아이디 '{p.Upn}' 안에 있습니다"; }

            if (score == 0) continue;

            // 학생 표시 이름이 학번+이름인 덕분에 여기서 갈린다.
            // 이 규칙이 없으면 '홍길동' 으로 찾을 때 학생과 선생님이 뒤섞이고,
            // 학생을 팀 소유자로 넣으면 그 아이가 반 전체를 지울 수 있다.
            if (looksStudent)
            {
                students.Add(new TeacherMatch(p, score, "학생 계정입니다(표시 이름이 학번+이름)"));
                continue;
            }

            if (facultyBundle is { Length: > 0 })
            {
                // 라이선스가 가장 믿을 만한 표시다. 이름 생김새보다 이쪽을 먼저 본다 —
                // 학생 표시 이름 규칙은 학교마다 다르지만 라이선스는 다르지 않다.
                if (string.Equals(p.LicenseBundle, facultyBundle, StringComparison.Ordinal))
                {
                    score += 10;
                    why += ", 교사 라이선스입니다";
                }
                else
                {
                    score -= 35;
                    why += " (교사 라이선스가 아닙니다)";
                }
            }

            if (score >= MinScore) found.Add(new TeacherMatch(p, score, why));
        }

        return new TeacherSearch(
            found.OrderByDescending(m => m.Score)
                 .ThenBy(m => m.User.DisplayName, StringComparer.CurrentCulture).ToList(),
            students.OrderBy(m => m.User.DisplayName, StringComparer.CurrentCulture).ToList());
    }

    /// <summary>
    /// 하나로 정해졌는지. <b>둘 이상이면 사람이 골라야 한다.</b>
    /// </summary>
    /// <remarks>
    /// 동명이인은 학교에 흔하다. 점수가 같은 사람이 둘이면 그냥 앞엣것을 쓰면 안 된다 —
    /// 엉뚱한 선생님이 남의 반 소유자가 된다.
    /// </remarks>
    public static bool IsCertain(IReadOnlyList<TeacherMatch> matches)
        => matches.Count == 1
        || (matches.Count > 1 && matches[0].Score >= 100 && matches[1].Score < 100);

    private static string LocalPart(string upn)
    {
        var at = upn.IndexOf('@');
        return at > 0 ? upn[..at] : upn;
    }

    /// <summary>빈칸을 걷어내고 견준다. '홍 길동' 과 '홍길동' 은 같은 사람이다.</summary>
    private static string Squeeze(string s)
        => Regex.Replace(s ?? "", @"\s+", "").ToLowerInvariant();
}
