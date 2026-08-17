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
    /// <summary>
    /// 자료를 훑어 학번 형식을 알아낸다. 뽑아내기 전에 이것부터 해야 한다.
    /// </summary>
    /// <remarks>
    /// 학번을 만들거나 가르려면 형식을 알아야 하는데, <b>학교마다 다르다.</b>
    /// 1학년 3반 1번이 10301 인 학교도 있고 1301 인 학교도 있다.
    /// </remarks>
    public static StudentIdGuess DetectIdFormat(Table table, RosterMapping map)
    {
        var samples = new List<(string, string, string, string)>();

        for (var r = map.HeaderRow + 1; r < table.Rows.Count && samples.Count < 200; r++)
        {
            var raw = table.Rows[r];
            if (raw.All(string.IsNullOrWhiteSpace)) continue;

            samples.Add((
                Cell(raw, map, RosterField.StudentId),
                Cell(raw, map, RosterField.Grade),
                Cell(raw, map, RosterField.ClassNo),
                Cell(raw, map, RosterField.Number)));
        }

        if (samples.Any(s => s.Item1.Length > 0)) return StudentIdFormats.Detect(samples);

        // 학번 열이 없다. 그렇다고 곧바로 물으면 안 된다 —
        // 학번은 대개 표시이름이나 아이디 안에 그대로 들어 있다.
        //   30105강도윤        → 30105
        //   s10203@abc.hs.kr  → 10203
        // 여기서 찾아내면 사람에게 아무것도 묻지 않고 형식을 확인할 수 있다.
        samples.Clear();
        for (var r = map.HeaderRow + 1; r < table.Rows.Count && samples.Count < 200; r++)
        {
            var raw = table.Rows[r];
            if (raw.All(string.IsNullOrWhiteSpace)) continue;

            samples.Add((
                DigitsIn(Cell(raw, map, RosterField.DisplayName)) ?? DigitsIn(LocalPart(Cell(raw, map, RosterField.Upn))) ?? "",
                Cell(raw, map, RosterField.Grade),
                Cell(raw, map, RosterField.ClassNo),
                Cell(raw, map, RosterField.Number)));
        }

        var found = StudentIdFormats.Detect(samples);
        if (found.Format is not null && found.Certain)
            return found with { Why = found.Why + " (학번은 아이디·표시이름 안에서 찾았습니다)" };

        return found;
    }

    /// <summary>메일 주소에서 @ 앞부분.</summary>
    private static string LocalPart(string upn)
    {
        var at = upn.IndexOf('@');
        return at > 0 ? upn[..at] : upn;
    }

    /// <summary>
    /// 글자 안의 숫자 덩어리 하나. 덩어리가 여럿이면 학번으로 볼 수 없어 null.
    /// </summary>
    /// <remarks>
    /// 's10203' 은 10203 하나뿐이라 쓸 수 있지만, '2026-10203' 처럼 둘이면
    /// 어느 쪽이 학번인지 알 수 없다. 그때는 짐작하지 않는다.
    /// </remarks>
    private static string? DigitsIn(string text)
    {
        if (text.Length == 0) return null;
        var runs = Regex.Matches(text, @"\d+").Select(m => m.Value).ToList();
        return runs.Count == 1 && runs[0].Length >= 3 ? runs[0] : null;
    }

    /// <summary>표에서 줄들을 뽑는다.</summary>
    /// <param name="format">
    /// 학번 형식. null 이면 학번을 만들지도 가르지도 않는다 —
    /// 모르는 채로 지어내면 아이가 없는 반에 배정된다.
    /// </param>
    public static RosterResult Extract(Table table, RosterMapping map, StudentIdFormat? format = null)
    {
        var rows = new List<RosterRow>();
        var made = new Dictionary<string, int>(StringComparer.Ordinal);

        // 보고서 형식이면 학년·반이 제목에만 있다. 줄마다 물려받게 미리 훑어 둔다.
        var section = SectionHeadings(table);

        // 보고서 형식은 반이 바뀔 때마다 열 이름 줄을 다시 찍는다.
        // 그것을 학생으로 읽으면 이름이 '성명' 인 아이가 생긴다.
        var headerCells = map.HeaderRow >= 0 && map.HeaderRow < table.Rows.Count
            ? table.Rows[map.HeaderRow].Select(RosterSchema.Normalize).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        for (var r = map.HeaderRow + 1; r < table.Rows.Count; r++)
        {
            var raw = table.Rows[r];
            if (raw.All(string.IsNullOrWhiteSpace)) continue;
            if (LooksLikeHeaderAgain(raw, headerCells)) continue;

            var grade = Cell(raw, map, RosterField.Grade);
            var cls   = Cell(raw, map, RosterField.ClassNo);
            var num   = Cell(raw, map, RosterField.Number);
            var sid   = Cell(raw, map, RosterField.StudentId);
            var name  = Cell(raw, map, RosterField.Name);
            var disp  = Cell(raw, map, RosterField.DisplayName);
            var upn   = Cell(raw, map, RosterField.Upn);

            var filled = new List<Derived>();
            var problems = new List<string>();

            // ⓪ 줄에 학년·반이 없으면 위쪽 제목에서 물려받는다.
            if (grade.Length == 0 && section[r].Grade.Length > 0)
            {
                grade = section[r].Grade;
                filled.Add(new Derived(RosterField.Grade, "학년·반은 위쪽 제목줄에서 물려받아"));
            }
            if (cls.Length == 0 && section[r].ClassNo.Length > 0)
            {
                cls = section[r].ClassNo;
                filled.Add(new Derived(RosterField.ClassNo, "학년·반은 위쪽 제목줄에서 물려받아"));
            }

            // ① 학번이 없으면 학년·반·번호로 만든다. 형식은 자료에서 알아낸 것을 쓴다 —
            //    학교마다 다르므로 여기에 박아 두면 절반의 학교에서 틀린다.
            if (sid.Length == 0 && format is not null
                && grade.Length > 0 && cls.Length > 0 && num.Length > 0)
            {
                if (format.Compose(grade, cls, num) is { Length: > 0 } made2)
                {
                    sid = made2;
                    filled.Add(new Derived(RosterField.StudentId, $"학번은 학년·반·번호를 {format.Length}자리로 이어 붙여"));
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

            // ④ 학년·반·번호가 없으면 학번을 갈라 되짚는다. 이것이 없으면
            //    '어느 반에 넣을지' 를 알 수 없어 배정 자체가 불가능하다.
            //    형식을 모르면 하지 않는다 — 잘못 가르면 아이가 없는 반에 들어간다.
            if (format is not null && grade.Length == 0 && cls.Length == 0 && num.Length == 0
                && format.TryDecompose(sid, out var g2, out var c2, out var n2))
            {
                grade = g2; cls = c2; num = n2;
                var how = $"학년·반·번호는 {format.Length}자리 학번을 갈라";
                filled.Add(new Derived(RosterField.Grade, how));
                filled.Add(new Derived(RosterField.ClassNo, how));
                filled.Add(new Derived(RosterField.Number, how));
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

    /// <summary>
    /// '보고서 형식' 을 알아본다 — 학년·반이 줄마다 있지 않고 <b>제목으로 한 번만</b> 나오는 것.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 선생님들이 주는 명단은 두 모양이다.
    /// </para>
    /// <list type="bullet">
    /// <item><b>데이터 형식</b> — 줄마다 학년·반이 다 적혀 있다. 그대로 읽으면 된다.</item>
    /// <item><b>보고서 형식</b> — "1학년 3반" 이 제목으로 한 번 나오고 그 아래에 번호·이름만 있다.
    ///       사람이 보기엔 당연하지만, 줄만 읽으면 그 학생들이 몇 학년 몇 반인지 알 수 없다.</item>
    /// </list>
    /// <para>
    /// 그래서 제목처럼 생긴 줄에서 학년·반을 읽어 <b>다음 제목이 나올 때까지 아래로 물려준다.</b>
    /// 한 파일에 여러 반이 이어 붙어 있는 경우도 이것으로 갈린다.
    /// </para>
    /// </remarks>
    internal static (string Grade, string ClassNo)[] SectionHeadings(Table table)
    {
        var carry = new (string, string)[table.Rows.Count];
        var g = "";
        var c = "";

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var filled = row.Count(x => !string.IsNullOrWhiteSpace(x));

            // 제목 줄은 칸이 몇 개 안 찬다. 줄마다 값이 꽉 찬 명단 줄에서 찾으면 안 된다 —
            // '3학년_4반' 같은 이름이 들어 있는 칸을 제목으로 오해하게 된다.
            if (filled is > 0 and <= 3)
            {
                foreach (var cell in row)
                {
                    if (!TryHeading(cell ?? "", out var gg, out var cc)) continue;
                    g = gg;
                    c = cc;
                    break;
                }
            }

            carry[r] = (g, c);
        }

        return carry;
    }

    // '1학년 3반' · '제1학년 제3반' · '1학년3반' 을 먼저 보고, 없으면 '1-3' 을 본다.
    private static readonly Regex HeadingWords = new(
        @"(?<!\d)(?<g>\d)\s*학\s*년\s*제?\s*(?<c>\d{1,2})\s*반(?!\d)", RegexOptions.Compiled);

    // '1-3' 은 날짜(2026-08-17)와 헷갈린다. 앞뒤에 다른 숫자가 붙지 않은 것만 본다.
    private static readonly Regex HeadingDash = new(
        @"^\s*(?<g>\d)\s*-\s*(?<c>\d{1,2})\s*(반)?\s*$", RegexOptions.Compiled);

    private static bool TryHeading(string text, out string grade, out string classNo)
    {
        grade = classNo = "";
        var m = HeadingWords.Match(text);
        if (!m.Success) m = HeadingDash.Match(text);
        if (!m.Success) return false;

        var g = int.Parse(m.Groups["g"].Value);
        var c = int.Parse(m.Groups["c"].Value);
        if (g is < 1 or > 6 || c is < 1 or > 30) return false;

        grade = g.ToString();
        classNo = c.ToString();
        return true;
    }

    /// <summary>열 이름 줄이 다시 나온 것인지. 보고서 형식은 반이 바뀔 때마다 다시 찍는다.</summary>
    private static bool LooksLikeHeaderAgain(IReadOnlyList<string> row, IReadOnlySet<string> headerCells)
    {
        if (headerCells.Count == 0) return false;

        var cells = row.Select(RosterSchema.Normalize).Where(x => x.Length > 0).ToList();
        if (cells.Count == 0) return false;

        // 값이 든 칸이 전부 열 이름과 같으면 그건 명단 줄이 아니다.
        return cells.All(headerCells.Contains);
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
