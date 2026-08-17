namespace Teavel.Roster;

/// <summary>열 하나를 어느 자리로 봤는지.</summary>
/// <param name="Field">그 자리.</param>
/// <param name="ColumnIndex">몇 번째 열인지(0부터).</param>
/// <param name="Header">그 열의 이름. 비어 있을 수 있다.</param>
/// <param name="Score">얼마나 확실한지. 100이 완전 일치.</param>
/// <param name="Why">어떻게 그렇게 봤는지 — 관리자에게 그대로 보여 준다.</param>
public sealed record ColumnMatch(
    RosterField Field,
    int ColumnIndex,
    string Header,
    int Score,
    string Why);

/// <summary>표 하나를 읽어 낸 결과.</summary>
/// <param name="HeaderRow">머리글이 몇 번째 줄이었는지(0부터).</param>
/// <param name="Matches">자리별로 어느 열을 골랐는지.</param>
/// <param name="Missing">끝내 못 찾은 자리들.</param>
/// <param name="UnusedHeaders">쓰지 않은 열 이름들 — 관리자가 보고 잘못을 알아챌 수 있다.</param>
public sealed record RosterMapping(
    int HeaderRow,
    IReadOnlyList<ColumnMatch> Matches,
    IReadOnlyList<RosterField> Missing,
    IReadOnlyList<string> UnusedHeaders)
{
    public ColumnMatch? this[RosterField f] => Matches.FirstOrDefault(m => m.Field == f);

    /// <summary>배정을 하려면 이름과 아이디는 반드시 있어야 한다.</summary>
    public bool CanAssign => this[RosterField.Upn] is not null;
}

/// <summary>
/// 아무 표나 받아 <b>학년·반·번호·학번·이름·ID</b> 여섯 자리에 꽂는다.
///
/// <para>
/// 학교마다 엑셀 모양이 다르다. 양식을 정해 주고 맞춰 오라고 하면 그 순간
/// 아무것도 모르는 관리자는 막힌다 — 그래서 <b>맞추는 쪽은 Teavel</b> 이다.
/// </para>
/// <para>
/// 짐작이 틀릴 수 있으므로 <b>무엇을 어떻게 봤는지 반드시 보여 준다.</b>
/// "학년 ← '학 년' 열" 처럼 근거까지 적어야 관리자가 잘못을 알아챌 수 있다.
/// 조용히 틀리면 1학년 아이가 2025학년 반에 들어간다.
/// </para>
/// </summary>
public static class RosterMapper
{
    /// <summary>이 점수 아래는 짐작이 아니라 우연이다.</summary>
    public const int MinScore = 40;

    /// <summary>
    /// 표에서 머리글 줄을 찾아 열을 자리에 꽂는다.
    /// </summary>
    /// <param name="rows">표 전체. 첫 몇 줄은 제목·안내일 수 있다.</param>
    /// <param name="scanRows">머리글을 찾을 때 위에서부터 몇 줄이나 볼지.</param>
    public static RosterMapping Map(IReadOnlyList<IReadOnlyList<string>> rows, int scanRows = 20)
    {
        if (rows.Count == 0)
            return new RosterMapping(-1, Array.Empty<ColumnMatch>(),
                RosterSchema.Rules.Select(r => r.Field).ToList(), Array.Empty<string>());

        // 머리글이 첫 줄이라는 보장이 없다. 나이스에서 내보내면 위에 제목·학교명이 붙는다.
        // 줄마다 꽂아 보고 가장 많이 꽂히는 줄을 머리글로 본다.
        var bestRow = 0;
        List<ColumnMatch> best = new();
        var bestScore = -1;

        for (var r = 0; r < Math.Min(rows.Count, scanRows); r++)
        {
            var sample = SampleBelow(rows, r);
            var got = MatchHeaders(rows[r], sample);

            // 자리를 많이 채운 쪽이 이기고, 같으면 점수 합이 높은 쪽이 이긴다.
            var score = got.Count * 1000 + got.Sum(m => m.Score);
            if (score > bestScore) { bestScore = score; best = got; bestRow = r; }
        }

        var missing = RosterSchema.Rules
            .Select(x => x.Field)
            .Where(f => best.All(m => m.Field != f))
            .ToList();

        var used = best.Select(m => m.ColumnIndex).ToHashSet();
        var unused = bestRow < rows.Count
            ? rows[bestRow].Select((h, i) => (h, i))
                .Where(t => !used.Contains(t.i) && t.h.Trim().Length > 0)
                .Select(t => t.h.Trim())
                .ToList()
            : new List<string>();

        return new RosterMapping(bestRow, best, missing, unused);
    }

    /// <summary>
    /// 머리글 한 줄을 자리에 꽂는다.
    /// </summary>
    /// <remarks>
    /// 한 열이 두 자리를 차지할 수 없고, 한 자리에 두 열이 들어갈 수도 없다.
    /// 그래서 (자리, 열) 짝마다 점수를 매긴 뒤 <b>점수가 높은 짝부터</b> 확정한다.
    /// '학번' 과 '번호' 가 함께 있을 때 서로를 밀어내는 것이 이 때문이다.
    /// </remarks>
    private static List<ColumnMatch> MatchHeaders(
        IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> sample)
    {
        var candidates = new List<ColumnMatch>();

        foreach (var rule in RosterSchema.Rules)
        {
            for (var c = 0; c < headers.Count; c++)
            {
                var (score, why) = ScoreOne(rule, headers[c], ColumnValues(sample, c));
                if (score >= MinScore) candidates.Add(new ColumnMatch(rule.Field, c, headers[c].Trim(), score, why));
            }
        }

        var taken = new HashSet<int>();
        var done = new HashSet<RosterField>();
        var chosen = new List<ColumnMatch>();

        foreach (var m in candidates.OrderByDescending(m => m.Score).ThenBy(m => m.ColumnIndex))
        {
            if (taken.Contains(m.ColumnIndex) || done.Contains(m.Field)) continue;
            taken.Add(m.ColumnIndex);
            done.Add(m.Field);
            chosen.Add(m);
        }

        return chosen.OrderBy(m => (int)m.Field).ToList();
    }

    /// <summary>열 이름 하나가 어떤 자리에 얼마나 맞는지.</summary>
    private static (int Score, string Why) ScoreOne(
        FieldRule rule, string header, IReadOnlyList<string> values)
    {
        var norm = RosterSchema.Normalize(header);

        // '학년도' 를 학년으로 읽으면 1학년이 2025학년이 된다. 이건 점수 이전에 잘라 낸다.
        foreach (var bad in rule.Never)
            if (norm.Contains(RosterSchema.Normalize(bad), StringComparison.Ordinal))
                return (0, "");

        var byName = 0;
        var why = "";

        if (norm.Length > 0)
        {
            foreach (var alias in rule.Aliases)
            {
                var a = RosterSchema.Normalize(alias);
                if (a.Length == 0) continue;

                if (norm == a) { byName = 100; why = $"'{header.Trim()}' 은(는) {rule.Label} 입니다"; break; }

                // 부분 일치는 약하게 본다 — '일반계' 안의 '반' 같은 것이 있기 때문이다.
                // 한 글자짜리 별칭('반' · '번')은 부분 일치를 아예 인정하지 않는다.
                if (a.Length >= 2 && norm.Contains(a, StringComparison.Ordinal) && byName < 65)
                {
                    byName = 65;
                    why = $"'{header.Trim()}' 안에 '{alias}' 이(가) 있습니다";
                }
            }
        }

        // 값이 그 자리처럼 생겼는지도 본다. 머리글이 없거나 '열3' 처럼 쓸모없을 때
        // 이것만으로 자리를 찾을 수 있다.
        var byValue = 0;
        if (values.Count > 0)
        {
            var hit = values.Count(rule.Looks);
            var ratio = (double)hit / values.Count;
            if (ratio >= 0.9) byValue = 55;
            else if (ratio >= 0.7) byValue = 40;
        }

        if (byName > 0 && byValue > 0)
        {
            // 이름도 맞고 값도 맞으면 가장 믿을 만하다.
            return (Math.Min(100, byName + 10), why + ", 값도 맞습니다");
        }
        if (byName > 0) return (byName, why);
        if (byValue >= 55) return (byValue, $"열 이름은 알 수 없지만 값이 {rule.Label} 모양입니다");

        return (0, "");
    }

    /// <summary>머리글 아래 줄들에서 값 표본을 뜬다. 빈 줄은 뺀다.</summary>
    private static IReadOnlyList<IReadOnlyList<string>> SampleBelow(
        IReadOnlyList<IReadOnlyList<string>> rows, int headerRow, int take = 12)
        => rows.Skip(headerRow + 1)
               .Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))
               .Take(take)
               .ToList();

    private static IReadOnlyList<string> ColumnValues(IReadOnlyList<IReadOnlyList<string>> sample, int c)
        => sample.Where(r => c < r.Count)
                 .Select(r => r[c] ?? "")
                 .Where(v => v.Trim().Length > 0)
                 .ToList();

    /// <summary>관리자에게 보여 줄 줄들. 무엇을 어떻게 봤는지 그대로 적는다.</summary>
    public static IReadOnlyList<string> Explain(RosterMapping m)
    {
        var lines = new List<string>();

        foreach (var rule in RosterSchema.Rules)
        {
            var hit = m[rule.Field];
            if (hit is null)
            {
                lines.Add($"{rule.Label,-4} ← (못 찾음)");
                continue;
            }

            var mark = hit.Score >= 90 ? " " : "?";
            lines.Add($"{rule.Label,-4} ←{mark}{hit.ColumnIndex + 1}번째 열 '{hit.Header}'   ({hit.Why})");
        }

        if (m.UnusedHeaders.Count > 0)
        {
            lines.Add("");
            lines.Add($"쓰지 않은 열: {string.Join(", ", m.UnusedHeaders)}");
        }

        return lines;
    }
}
