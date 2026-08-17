using Teavel.Roster;

namespace Teavel.M365;

/// <summary>테넌트의 팀 하나에 이미 들어 있는 사람.</summary>
/// <param name="Upn">로그인 아이디.</param>
/// <param name="Role">Owner 또는 Member.</param>
public sealed record TeamMember(string Upn, string Role);

/// <summary>어느 반의 학생들을 어느 팀에 넣을지.</summary>
/// <param name="ClassKey">'1학년 3반' 처럼 사람이 읽을 반 이름.</param>
/// <param name="Team">넣을 팀. 못 찾았으면 null.</param>
/// <param name="ToAdd">넣어야 할 사람들(이미 들어 있는 사람은 뺐다).</param>
/// <param name="Already">이미 들어 있어 건너뛸 사람 수.</param>
/// <param name="Problem">넣을 수 없으면 그 까닭. 없으면 빈 문자열.</param>
public sealed record ClassAssignment(
    string ClassKey,
    ExistingGroup? Team,
    IReadOnlyList<RosterRow> ToAdd,
    int Already,
    string Problem)
{
    public bool CanApply => Team is not null && Problem.Length == 0 && ToAdd.Count > 0;
}

/// <summary>
/// <b>명단과 테넌트가 만나는 자리.</b>
///
/// <para>
/// 여기까지는 둘이 따로 놀았다. 명단은 파일만 읽고 끝났고 M365 는 테넌트만 봤다.
/// 그런데 정작 해야 할 일은 <b>명단 × 테넌트</b> 다 — 이 반 학생들을 저 팀에 넣는 것.
/// </para>
/// <para>
/// 반과 팀을 잇는 방법이 요점이다. 이름('1학년 3반')으로 짐작하면 학교마다 표기가 달라
/// 어긋난다. 그래서 <b>선언이 펼쳐질 때 쓴 값</b>(학년=1, 반=3)으로 잇는다.
/// 명단에서 읽은 학년·반과 그 값을 맞춰 보면 짐작이 끼어들 자리가 없다.
/// </para>
/// <para>
/// 이미 들어 있는 사람은 뺀다. 여러 번 돌려도 안전해야 하고, 학기 중에 전학생이
/// 한 명 왔을 때 <b>그 한 명만</b> 넣을 수 있어야 하기 때문이다.
/// </para>
/// </summary>
public static class MemberPlanner
{
    /// <summary>선언에서 학년·반을 읽어 낼 때 볼 이름들. 학교마다 다르게 적을 수 있다.</summary>
    private static readonly string[] GradeKeys = { "학년", "grade" };
    private static readonly string[] ClassKeys = { "반", "학급", "class" };

    /// <summary>
    /// 명단을 반별로 갈라 팀에 맞춘다.
    /// </summary>
    /// <param name="roster">명단에서 읽은 줄들.</param>
    /// <param name="plan">대조 결과 — 선언과 실제 팀이 짝지어져 있다.</param>
    /// <param name="existingMembers">팀 id → 이미 들어 있는 사람들. 모르는 팀은 빈 것으로 본다.</param>
    public static IReadOnlyList<ClassAssignment> Plan(
        IReadOnlyList<RosterRow> roster,
        IReadOnlyList<PlanItem> plan,
        IReadOnlyDictionary<string, IReadOnlyList<TeamMember>> existingMembers)
    {
        // 선언에 학년·반 값이 붙어 있는 팀만 반 팀으로 본다.
        // 그 값이 없으면 어느 반인지 알 방법이 없고, 이름으로 짐작해서는 안 된다.
        var byClass = new Dictionary<(string G, string C), PlanItem>();

        foreach (var p in plan)
        {
            if (p.Declared.Kind != GroupKind.Team) continue;

            // '확인 필요' 로 세운 것은 사람이 판단하기 전까지 손대지 않는다.
            //
            // 실기에서 이럴 뻔했다(2026-08-17): 명단의 '3학년 4반' 이 테넌트의 '3학년_4반'
            // 과 빈칸만 달라 확인 필요로 세워 놓고서는, 학생 넣기에서 그 진짜 반(30명)에
            // 명단의 학생을 넣으려 했다. 만들지는 않으면서 사람은 넣는 꼴이다.
            //
            // 확인 필요라는 것은 '이것이 같은 반인지 우리가 모른다' 는 뜻이다.
            // 모르는 채로 아이를 넣으면 엉뚱한 반에 들어간다.
            if (p.Action != PlanAction.Skip) continue;

            if (p.Existing is not { IsTeam: true }) continue;

            var g = Pick(p.Declared.Values, GradeKeys);
            var c = Pick(p.Declared.Values, ClassKeys);
            if (g.Length == 0 || c.Length == 0) continue;

            byClass.TryAdd((Norm(g), Norm(c)), p);
        }

        var result = new List<ClassAssignment>();

        var groups = roster
            .Where(r => r.Ok && r.Upn.Length > 0)
            .GroupBy(r => (Norm(r.Grade), Norm(r.ClassNo)))
            .OrderBy(g => g.Key.Item1, StringComparer.Ordinal)
            .ThenBy(g => Pad(g.Key.Item2), StringComparer.Ordinal);

        foreach (var g in groups)
        {
            var key = g.Key.Item1.Length > 0 && g.Key.Item2.Length > 0
                ? $"{g.Key.Item1}학년 {g.Key.Item2}반"
                : "(학년·반을 모르는 사람들)";

            if (g.Key.Item1.Length == 0 || g.Key.Item2.Length == 0)
            {
                result.Add(new ClassAssignment(key, null, Array.Empty<RosterRow>(), 0,
                    $"{g.Count()}명의 학년·반을 알 수 없어 어느 팀에 넣을지 정하지 못합니다."));
                continue;
            }

            if (!byClass.TryGetValue(g.Key, out var hit))
            {
                result.Add(new ClassAssignment(key, null, Array.Empty<RosterRow>(), 0,
                    "이 반에 해당하는 팀이 없습니다. 먼저 만들어야 합니다."));
                continue;
            }

            var team = hit.Existing!;
            var have = existingMembers.TryGetValue(team.GroupId, out var m)
                ? m.Select(x => x.Upn).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var add = g.Where(r => !have.Contains(r.Upn))
                       .OrderBy(r => Pad(r.Number), StringComparer.Ordinal)
                       .ToList();

            result.Add(new ClassAssignment(key, team, add, g.Count() - add.Count, ""));
        }

        return result;
    }

    /// <summary>한 줄 요약.</summary>
    public static string Summarize(IReadOnlyList<ClassAssignment> plan)
    {
        var add = plan.Sum(a => a.ToAdd.Count);
        var already = plan.Sum(a => a.Already);
        var stuck = plan.Count(a => a.Problem.Length > 0);

        var parts = new List<string>();
        if (add > 0) parts.Add($"넣을 사람 {add}명");
        if (already > 0) parts.Add($"이미 들어 있음 {already}명");
        if (stuck > 0) parts.Add($"넣을 수 없는 반 {stuck}개");

        return parts.Count == 0 ? "넣을 사람이 없습니다." : string.Join(" · ", parts);
    }

    private static string Pick(IReadOnlyDictionary<string, string> values, string[] keys)
    {
        foreach (var k in keys)
            foreach (var kv in values)
                if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
        return "";
    }

    /// <summary>'03' 과 '3' 은 같은 반이다. 앞의 0 을 떼고 견준다.</summary>
    private static string Norm(string s)
        => int.TryParse(s.Trim(), out var n) ? n.ToString() : s.Trim();

    /// <summary>번호순으로 줄 세울 때 쓴다. 글자로 정렬하면 10번이 2번 앞에 온다.</summary>
    private static string Pad(string s)
        => int.TryParse(s.Trim(), out var n) ? n.ToString("D4") : s.Trim();
}
