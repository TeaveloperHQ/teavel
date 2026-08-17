using Teavel.Roster;

namespace Teavel.M365;

/// <summary>명단에서 읽어 낸 학교 모양.</summary>
/// <param name="Classes">(학년, 반) 짝들. 학년·반 순으로 정렬돼 있다.</param>
/// <param name="HeadCount">(학년, 반) → 그 반의 사람 수.</param>
public sealed record SchoolShapeResult(
    IReadOnlyList<(int Grade, int ClassNo)> Classes,
    IReadOnlyDictionary<(int Grade, int ClassNo), int> HeadCount)
{
    public IReadOnlyList<int> Grades => Classes.Select(c => c.Grade).Distinct().OrderBy(g => g).ToList();

    /// <summary>'1~3학년 · 학년당 9반' 처럼 한 줄로.</summary>
    public string Describe()
    {
        if (Classes.Count == 0) return "학년·반을 읽어 내지 못했습니다.";

        var perGrade = Classes.GroupBy(c => c.Grade)
                              .OrderBy(g => g.Key)
                              .Select(g => $"{g.Key}학년 {g.Count()}개 반")
                              .ToList();

        return $"반 {Classes.Count}개 — {string.Join(" · ", perGrade)}";
    }
}

/// <summary>
/// <b>명단에서 학교 구조를 거꾸로 읽어낸다.</b>
///
/// <para>
/// 지금까지 학교 구조는 <c>catalog/m365-tree.json</c> 에 손으로 적어야 했다.
/// 그런데 그 파일을 관리자가 고칠 리 없다 — 무엇을 적어야 하는지도 모르고,
/// 적으라고 하는 순간 그 자리에서 막힌다.
/// </para>
/// <para>
/// 그럴 필요가 없다. <b>명단에 이미 들어 있다.</b> 1학년이 9반까지 있으면 명단에
/// 1학년 1반부터 9반까지가 나온다. 그것을 세면 학교 모양이 나온다.
/// </para>
/// <para>
/// 다만 <b>짐작한 것을 반드시 보여 주고 승낙받는다.</b> 명단이 한 학년 것만 있을 수도 있고,
/// 그때 나머지 학년을 없는 것으로 치면 안 되기 때문이다.
/// </para>
/// </summary>
public static class SchoolShape
{
    /// <summary>사람이 이만큼도 안 되는 반은 오타로 본다 — 그래도 보여는 준다.</summary>
    public const int TinyClass = 2;

    /// <summary>명단에서 (학년, 반) 을 모은다.</summary>
    public static SchoolShapeResult Read(IReadOnlyList<RosterRow> roster)
    {
        var count = new Dictionary<(int, int), int>();

        foreach (var r in roster)
        {
            if (!int.TryParse(r.Grade.Trim(), out var g)) continue;
            if (!int.TryParse(r.ClassNo.Trim(), out var c)) continue;
            if (g is < 1 or > 6 || c is < 1 or > 30) continue;

            count[(g, c)] = count.GetValueOrDefault((g, c)) + 1;
        }

        var classes = count.Keys.OrderBy(k => k.Item1).ThenBy(k => k.Item2).ToList();
        return new SchoolShapeResult(classes, count);
    }

    /// <summary>
    /// 읽어 낸 모양으로 반 팀 선언을 만든다.
    /// </summary>
    /// <param name="shape">명단에서 읽은 모양.</param>
    /// <param name="pattern">
    /// 본보기로 삼을 선언. 이름·별칭·채널을 여기서 가져온다.
    /// 트리에 반 팀 선언이 있으면 그것을 주고, 없으면 null — 그때는 기본 모양을 쓴다.
    /// </param>
    public static IReadOnlyList<DeclaredGroup> ToDeclarations(
        SchoolShapeResult shape, DeclaredGroup? pattern)
    {
        var nameTemplate = "{학년}학년 {반}반";
        var nickTemplate = "class-{학년}-{반}";
        var descTemplate = "{학년}학년 {반}반 수업 팀";
        var template = "educationClass";
        var visibility = "private";
        var channels = Array.Empty<string>() as IReadOnlyList<string>;
        var id = "class-from-roster";

        // 트리에 본보기가 있으면 그 학교가 정한 이름 규칙을 따른다.
        // 우리가 정한 모양을 밀어붙이면, 이미 '1-3' 처럼 쓰던 학교와 어긋난다.
        if (pattern is not null)
        {
            id = pattern.Id.Length > 0 ? pattern.Id : id;
            template = pattern.Template;
            visibility = pattern.Visibility;
            channels = pattern.Channels;

            // 펼쳐진 뒤라 이름에 값이 이미 박혀 있다. 되돌려 자리표시자로 만든다.
            var n = Unfill(pattern.DisplayName, pattern.Values);
            var k = Unfill(pattern.MailNickname, pattern.Values);
            var d = Unfill(pattern.Description, pattern.Values);

            // 되돌린 것에 본보기의 값을 다시 넣어 원래 이름이 나오는지 본다.
            // 안 나오면 제대로 못 되돌린 것이므로 본보기를 버리고 기본 모양을 쓴다 —
            // 반쯤 되돌린 틀로 열두 반을 만들면 이름이 전부 같아진다.
            var roundTrips = Fill(n, pattern.Values) == pattern.DisplayName
                          && Fill(k, pattern.Values) == pattern.MailNickname;

            if (roundTrips)
            {
                nameTemplate = n;
                nickTemplate = k;
                descTemplate = d;
            }
        }

        var result = new List<DeclaredGroup>();

        foreach (var (g, c) in shape.Classes)
        {
            var values = new Dictionary<string, string> { ["학년"] = g.ToString(), ["반"] = c.ToString() };

            result.Add(new DeclaredGroup(
                Id: id,
                Kind: GroupKind.Team,
                DisplayName: Fill(nameTemplate, values),
                MailNickname: Fill(nickTemplate, values),
                Description: Fill(descTemplate, values),
                Template: template,
                Visibility: visibility,
                Channels: channels,
                Values: values));
        }

        return result;
    }

    /// <summary>트리에서 반 팀 본보기를 고른다. 없으면 null.</summary>
    /// <remarks>
    /// <para>학년·반 값을 달고 있는 팀 선언이 반 팀이다. 이름으로 짐작하지 않는다.</para>
    /// <para>
    /// <b>값이 서로 다른 것을 고른다.</b> '1학년 1반' 을 본보기로 삼으면 이름 안의 두 '1' 중
    /// 어느 것이 학년이고 어느 것이 반인지 알 수 없어, 되돌릴 때 둘 다 같은 자리표시자가 된다.
    /// 실제로 그렇게 해서 열두 반이 전부 '1학년 1반' 으로 나왔다.
    /// </para>
    /// </remarks>
    public static DeclaredGroup? FindClassPattern(SchoolTree tree)
    {
        var classes = tree.Groups.Where(g =>
               g.Kind == GroupKind.Team
            && g.Values.Keys.Any(k => k is "학년" or "grade")
            && g.Values.Keys.Any(k => k is "반" or "학급" or "class")).ToList();

        if (classes.Count == 0) return null;

        // 값이 겹치지 않는 것이 있으면 그것을 쓴다.
        var distinct = classes.FirstOrDefault(g =>
            g.Values.Values.Distinct(StringComparer.Ordinal).Count() == g.Values.Count);

        return distinct ?? classes[0];
    }

    /// <summary>본보기에서 온 반 팀 선언을 걷어낸다 — 명단에서 만든 것으로 갈아 끼운다.</summary>
    public static IReadOnlyList<DeclaredGroup> WithoutClasses(SchoolTree tree, DeclaredGroup? pattern)
    {
        if (pattern is null) return tree.Groups;
        return tree.Groups.Where(g => !string.Equals(g.Id, pattern.Id, StringComparison.Ordinal)).ToList();
    }

    /// <summary>값이 박힌 이름을 자리표시자로 되돌린다. '1학년 3반' → '{학년}학년 {반}반'.</summary>
    private static string Unfill(string text, IReadOnlyDictionary<string, string> values)
    {
        if (text.Length == 0) return text;

        // 긴 값부터 바꿔야 '1' 이 '10' 안의 1 을 먼저 먹지 않는다.
        foreach (var kv in values.OrderByDescending(kv => kv.Value.Length))
        {
            if (kv.Value.Length == 0) continue;
            text = text.Replace(kv.Value, "{" + kv.Key + "}", StringComparison.Ordinal);
        }
        return text;
    }

    private static string Fill(string template, IReadOnlyDictionary<string, string> values)
    {
        foreach (var kv in values)
            template = template.Replace("{" + kv.Key + "}", kv.Value, StringComparison.Ordinal);
        return template;
    }
}
