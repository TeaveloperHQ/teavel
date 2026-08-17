using Teavel.Roster;

namespace Teavel.Cli;

/// <summary>
/// 명단 파일을 읽어 <b>학년·반·번호·학번·이름·ID</b> 여섯 자리에 꽂아 보여 준다.
///
/// <para>
/// 학교마다 엑셀 모양이 다르다. 양식을 정해 주고 맞춰 오라고 하면 아무것도 모르는
/// 관리자는 그 자리에서 막힌다 — 그래서 <b>맞추는 쪽은 Teavel</b> 이다.
/// </para>
/// <para>
/// 대신 짐작한 것을 <b>반드시 보여 준다.</b> 조용히 틀리면 1학년 아이가 엉뚱한 반에 들어간다.
/// </para>
/// </summary>
public static class RosterFlow
{
    public static int Run(string? path)
    {
        Ui.Title("명단 읽기");

        if (string.IsNullOrWhiteSpace(path))
        {
            Ui.Error("어떤 파일인지 알려 주세요.");
            Ui.Dim("      teavel 명단 \"C:\\Users\\...\\1학년.xlsx\"");
            return 2;
        }

        if (!File.Exists(path))
        {
            Ui.Error($"그런 파일이 없습니다: {path}");
            return 2;
        }

        if (!TableReader.CanReadDirectly(path))
        {
            ShowUnsupported(path);
            return 2;
        }

        Table table;
        try
        {
            table = TableReader.Read(path);
        }
        catch (Exception ex)
        {
            Ui.Error($"파일을 읽지 못했습니다: {ex.Message}");
            return 2;
        }

        Ui.Ok($"{table.Source} — {table.Rows.Count}줄");
        if (table.Note.Length > 0) Ui.Dim($"      {table.Note}");

        if (table.Rows.Count == 0)
        {
            Ui.Warn("표가 비어 있습니다.");
            return 1;
        }

        var map = RosterMapper.Map(table.Rows);

        Console.WriteLine();
        Ui.Info($"{map.HeaderRow + 1}번째 줄을 열 이름으로 봤습니다.");
        Ui.Details(RosterMapper.Explain(map));

        if (map.Missing.Count > 0)
        {
            Console.WriteLine();
            var names = map.Missing.Select(f => RosterSchema.Rules.First(r => r.Field == f).Label);
            Ui.Warn($"못 찾은 자리: {string.Join(", ", names)}");
        }

        WarnRaggedRows(table, map);
        ShowSample(table, map);

        Console.WriteLine();
        if (map.CanAssign)
        {
            Ui.Ok("이 명단으로 팀에 넣을 수 있습니다.");
        }
        else
        {
            Ui.Warn("ID 열이 없어 팀에 넣지 못합니다.");
            Ui.Dim("      로그인 아이디(메일 주소)가 있는 열이 필요합니다.");
            Ui.Dim("      학번으로 아이디를 만드는 규칙이 있다면 알려 주세요 — 그 규칙으로 만들어 드릴 수 있습니다.");
        }

        return map.CanAssign ? 0 : 1;
    }

    /// <summary>
    /// 칸 수가 열 이름 줄과 다른 줄들을 짚는다.
    /// </summary>
    /// <remarks>
    /// 칸이 하나 모자란 줄은 <b>그 뒤 값이 통째로 한 칸씩 앞으로 밀린다.</b>
    /// 그러면 이름 자리에 아이디가, 학번 자리에 이름이 들어가는데 화면만 보면 그럴듯하다.
    /// 밀린 채로 배정하면 아이가 다른 반에 들어간다. 반드시 사람이 보게 한다.
    /// </remarks>
    private static void WarnRaggedRows(Table table, RosterMapping map)
    {
        // xlsx 는 칸마다 자리표가 붙어 있어 밀릴 수가 없다. 거기서 경고하면 잔소리다.
        if (table.PositionsAreExact) return;
        if (map.HeaderRow < 0 || map.HeaderRow >= table.Rows.Count) return;

        var width = table.Rows[map.HeaderRow].Count;
        var bad = new List<int>();

        for (var r = map.HeaderRow + 1; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (row.Count != width) bad.Add(r + 1);
        }

        if (bad.Count == 0) return;

        Console.WriteLine();
        Ui.Warn($"칸 수가 맞지 않는 줄이 {bad.Count}개 있습니다. 값이 한 칸씩 밀려 있을 수 있습니다.");
        Ui.Dim($"      열 이름 줄은 {width}칸인데, {string.Join(", ", bad.Take(10).Select(n => n + "번째 줄"))}"
             + (bad.Count > 10 ? " 등" : "") + "이 다릅니다.");
        Ui.Dim("      아래 '꽂아 본 결과' 에서 그 줄들이 제대로 들어갔는지 꼭 확인해 주세요.");
    }

    /// <summary>
    /// 꽂은 대로 실제 값이 어떻게 나오는지 몇 줄 보여 준다.
    /// </summary>
    /// <remarks>
    /// 설명만 보고는 잘못을 알아채기 어렵다. '학년' 자리에 2025 가 들어 있는 것을
    /// 눈으로 보면 그 자리에서 안다.
    /// </remarks>
    private static void ShowSample(Table table, RosterMapping map)
    {
        var fields = RosterSchema.Rules.Where(r => map[r.Field] is not null).ToList();
        if (fields.Count == 0) return;

        Console.WriteLine();
        Ui.Dim("      꽂아 본 결과 (앞에서 5줄)");
        Ui.Plain("        " + string.Join("  ", fields.Select(f => f.Label.PadRight(8))));

        var shown = 0;
        for (var r = map.HeaderRow + 1; r < table.Rows.Count && shown < 5; r++)
        {
            var row = table.Rows[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            var cells = fields.Select(f =>
            {
                var c = map[f.Field]!.ColumnIndex;
                var v = c < row.Count ? row[c] : "";
                return Pad(v, 8);
            });

            Ui.Plain("        " + string.Join("  ", cells));
            shown++;
        }
    }

    /// <summary>한글은 자리를 두 칸 먹는다. 그걸 세지 않으면 표가 어긋난다.</summary>
    private static string Pad(string s, int width)
    {
        s = s.Trim();
        var w = s.Sum(c => c >= 0x1100 ? 2 : 1);
        return w >= width ? s : s + new string(' ', width - w);
    }

    private static void ShowUnsupported(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        Ui.Warn($"'{ext}' 파일은 아직 그대로 읽지 못합니다.");
        Console.WriteLine();

        // 여기서 그냥 '안 됩니다' 로 끝내면 아무것도 모르는 분은 막힌다.
        // 무엇을 어떻게 누르면 되는지까지 적는다.
        Ui.Plain("""
              쓰고 계신 프로그램에서 이렇게 하시면 됩니다.

              ① 그 파일을 엽니다
              ② [파일] → [다른 이름으로 저장]
              ③ 파일 형식에서 'CSV' 또는 'Excel 통합 문서(*.xlsx)' 를 고릅니다
              ④ 저장한 뒤 그 파일을 다시 알려 주세요

              한셀·엑셀 어느 쪽이든 같습니다.
        """);
    }
}
