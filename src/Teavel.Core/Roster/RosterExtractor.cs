using System.Text.RegularExpressions;

namespace Teavel.Roster;

/// <summary>파일에 없어서 우리가 만들어 채운 값 하나.</summary>
/// <param name="Field">어느 자리인지.</param>
/// <param name="How">무엇으로 만들었는지. 화면에 그대로 나가므로 사실이어야 한다.</param>
public sealed record Derived(RosterField Field, string How);

/// <summary>명단 한 줄을 여섯 자리에 꽂아 낸 것.</summary>
/// <param name="Line">파일에서 몇 번째 줄이었는지(1부터). 고칠 곳을 짚어 주려면 필요하다.</param>
/// <param name="Grade">학년.</param>
/// <param name="ClassNo">반.</param>
/// <param name="Number">번호.</param>
/// <param name="StudentId">학번.</param>
/// <param name="Name">이름.</param>
/// <param name="DisplayName">표시 이름(학번+이름).</param>
/// <param name="Upn">로그인 아이디.</param>
/// <param name="Made">우리가 만들어 채운 자리들 — 파일에 없던 값이다.</param>
/// <param name="Problems">이 줄에서 걸리는 것. 비어 있으면 온전하다.</param>
public sealed record RosterRow(
    int Line,
    string Grade,
    string ClassNo,
    string Number,
    string StudentId,
    string Name,
    string DisplayName,
    string Upn,
    IReadOnlyList<Derived> Made,
    IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>명단 전체를 뽑아 낸 결과.</summary>
/// <param name="Rows">줄들.</param>
/// <param name="MadeCounts">
/// 어떻게 만든 값이 몇 줄이나 되는지. 자리가 아니라 <b>만든 방법</b>으로 센다 —
/// 같은 학번이라도 학년·반·번호를 이어 붙인 것과 표시 이름에서 갈라 낸 것은 다른 일이다.
/// </param>
public sealed record RosterResult(
    IReadOnlyList<RosterRow> Rows,
    IReadOnlyDictionary<string, int> MadeCounts)
{
    public IEnumerable<RosterRow> Good => Rows.Where(r => r.Ok);
    public IEnumerable<RosterRow> Bad => Rows.Where(r => !r.Ok);
}

/// <summary>
/// 꽂아 놓은 열에서 값을 뽑아 <b>모자란 자리를 만들어 채운다.</b>
///
/// <para>
/// 학교 파일에 일곱 자리가 다 있는 경우는 드물다. 그런데 없는 것 중 상당수는
/// <b>있는 것으로 만들 수 있다</b> — 학번은 학년·반·번호를 이어 붙인 것이고,
/// 표시 이름은 학번에 이름을 붙인 것이다.
/// </para>
/// <para>
/// 만들어 채운 자리는 <b>반드시 표시한다.</b> 파일에 있던 값과 우리가 지어낸 값은
/// 믿음의 무게가 다르고, 학번 규칙이 학교마다 다를 수 있기 때문이다.
/// </para>
/// </summary>
public static class RosterExtractor
{
    /// <summary>표에서 줄들을 뽑는다.</summary>
    public static RosterResult Extract(Table table, RosterMapping map)
    {
        var rows = new List<RosterRow>();
        var made = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var r = map.HeaderRow + 1; r < table.Rows.Count; r++)
        {
            var raw = table.Rows[r];
            if (raw.All(string.IsNullOrWhiteSpace)) continue;

            var grade = Cell(raw, map, RosterField.Grade);
            var cls   = Cell(raw, map, RosterField.ClassNo);
            var num   = Cell(raw, map, RosterField.Number);
            var sid   = Cell(raw, map, RosterField.StudentId);
            var name  = Cell(raw, map, RosterField.Name);
            var disp  = Cell(raw, map, RosterField.DisplayName);
            var upn   = Cell(raw, map, RosterField.Upn);

            var filled = new List<Derived>();
            var problems = new List<string>();

            // ① 학번이 없으면 학년·반·번호로 만든다. 10401 = 1학년 04반 01번.
            if (sid.Length == 0 && grade.Length > 0 && cls.Length > 0 && num.Length > 0)
            {
                if (int.TryParse(cls, out var c) && int.TryParse(num, out var n))
                {
                    sid = $"{grade}{c:D2}{n:D2}";
                    filled.Add(new Derived(RosterField.StudentId, "학번은 학년·반·번호를 이어 붙여"));
                }
            }

            // ② 표시 이름이 없으면 학번+이름으로 만든다. Teams 에서 이 이름이 보인다.
            if (disp.Length == 0 && sid.Length > 0 && name.Length > 0)
            {
                disp = sid + name;
                filled.Add(new Derived(RosterField.DisplayName, "표시이름은 학번과 이름을 이어 붙여"));
            }

            // ③ 표시 이름은 있는데 학번·이름이 없으면 거꾸로 갈라 낸다.
            //    관리자가 표시 이름만 들어 있는 목록을 가져오는 일이 있다.
            if (disp.Length > 0 && (sid.Length == 0 || name.Length == 0))
            {
                var m = Regex.Match(disp.Trim(), @"^(?<sid>\d{4,7})(?<name>[가-힣]{2,5})$");
                if (m.Success)
                {
                    if (sid.Length == 0)
                    { sid = m.Groups["sid"].Value; filled.Add(new Derived(RosterField.StudentId, "학번은 표시이름에서 갈라 내")); }
                    if (name.Length == 0)
                    { name = m.Groups["name"].Value; filled.Add(new Derived(RosterField.Name, "이름은 표시이름에서 갈라 내")); }
                }
            }

            // ④ 학년·반·번호가 없고 학번이 다섯 자리면 되짚는다 — 30105 = 3학년 01반 05번.
            //    이것이 없으면 '어느 반에 넣을지' 를 알 수 없어 배정 자체가 불가능하다.
            //    다섯 자리가 아니면 규칙을 알 수 없으므로 짐작하지 않는다.
            if (sid.Length == 5 && Regex.IsMatch(sid, @"^\d{5}$")
                && grade.Length == 0 && cls.Length == 0 && num.Length == 0)
            {
                grade = sid[..1];
                cls   = int.Parse(sid.Substring(1, 2)).ToString();
                num   = int.Parse(sid.Substring(3, 2)).ToString();
                filled.Add(new Derived(RosterField.Grade,   "학년·반·번호는 다섯 자리 학번을 갈라"));
                filled.Add(new Derived(RosterField.ClassNo, "학년·반·번호는 다섯 자리 학번을 갈라"));
                filled.Add(new Derived(RosterField.Number,  "학년·반·번호는 다섯 자리 학번을 갈라"));
            }

            // 사람을 가리키는 값이 하나도 없으면 명단 줄이 아니다(합계·비고 줄일 수 있다).
            if (name.Length == 0 && upn.Length == 0 && disp.Length == 0) continue;

            if (name.Length == 0) problems.Add("이름이 없습니다");
            if (upn.Length == 0)  problems.Add("아이디가 없습니다");
            else if (!upn.Contains('@')) problems.Add($"아이디에 '@' 가 없습니다: {upn}");

            foreach (var how in filled.Select(f => f.How).Distinct(StringComparer.Ordinal))
                made[how] = made.GetValueOrDefault(how) + 1;

            rows.Add(new RosterRow(r + 1, grade, cls, num, sid, name, disp, upn, filled, problems));
        }

        return new RosterResult(rows, made);
    }

    private static string Cell(IReadOnlyList<string> row, RosterMapping map, RosterField f)
    {
        var at = map[f]?.ColumnIndex ?? -1;
        return at >= 0 && at < row.Count ? (row[at] ?? "").Trim() : "";
    }

    /// <summary>같은 아이디가 두 줄에 있으면 만들 때 반드시 실패한다. 미리 짚는다.</summary>
    public static IReadOnlyList<string> FindDuplicateUpns(IReadOnlyList<RosterRow> rows)
        => rows.Where(r => r.Upn.Length > 0)
               .GroupBy(r => r.Upn, StringComparer.OrdinalIgnoreCase)
               .Where(g => g.Count() > 1)
               .Select(g => $"{g.Key} — {string.Join(", ", g.Select(r => r.Line + "번째 줄"))}")
               .ToList();
}
