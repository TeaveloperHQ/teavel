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
    public static int Run(string? path, bool assumeYes = false)
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

        // 학번 형식은 학교마다 다르다. 박아 두지 않고 자료에서 알아낸 뒤,
        // 확신이 없으면 사람에게 확인받는다 — 잘못 가르면 아이가 없는 반에 배정된다.
        var guess = RosterExtractor.DetectIdFormat(table, map);
        var format = ConfirmIdFormat(guess, assumeYes);

        var result = RosterExtractor.Extract(table, map, format);
        ShowRows(result);

        if (result.Rows.Count == 0)
        {
            Ui.Warn("명단으로 볼 줄이 없습니다.");
            return 1;
        }

        return ChooseWhatToDo(result, path, assumeYes);
    }

    /// <summary>
    /// 알아낸 학번 형식을 보여 주고, 확신이 없으면 사람에게 확인받는다.
    /// </summary>
    /// <remarks>
    /// 파일에 학번과 학년·반·번호가 함께 있으면 맞춰 보면 알 수 있어 묻지 않는다.
    /// 학번만 있으면 자릿수로 후보를 좁히는 것까지만 되므로 반드시 묻는다 —
    /// 네 자리 1301 은 1학년 3반 1번일 수도, 1학년 30반 1번일 수도 있다.
    /// </remarks>
    private static StudentIdFormat? ConfirmIdFormat(StudentIdGuess guess, bool assumeYes)
    {
        if (guess.Format is null)
        {
            if (guess.Why.Length > 0)
            {
                Console.WriteLine();
                Ui.Dim($"      학번 형식: {guess.Why}");
                Ui.Dim("      학번을 만들거나 가르지 않고, 파일에 있는 값만 씁니다.");
            }
            return null;
        }

        Console.WriteLine();
        if (guess.Certain)
        {
            Ui.Ok($"학번 형식: {guess.Format}");
            Ui.Dim($"      {guess.Why}");
            return guess.Format;
        }

        Ui.Warn($"학번 형식을 하나로 정하지 못했습니다.");
        Ui.Dim($"      {guess.Why}");
        Console.WriteLine();

        var options = new List<StudentIdFormat> { guess.Format };
        options.AddRange(guess.Alternatives);

        for (var i = 0; i < options.Count; i++)
            Ui.Plain($"        [{i + 1}] {options[i].Example()}      ({options[i]})");
        Ui.Plain($"        [0] 모르겠습니다 — 학번은 파일에 있는 것만 씁니다");

        // 사람이 없는 자리에서는 짐작으로 넘어가지 않는다.
        if (assumeYes)
        {
            Ui.Info("자동 모드에서는 학번을 만들거나 가르지 않습니다.");
            return null;
        }

        while (true)
        {
            var pick = (Ui.Ask($"      고르세요 [1] ") ?? "1").Trim();
            if (pick.Length == 0) pick = "1";
            if (pick == "0") return null;
            if (int.TryParse(pick, out var n) && n >= 1 && n <= options.Count) return options[n - 1];
        }
    }

    /// <summary>
    /// 여기가 갈림길이다 — <b>계정을 한꺼번에 만들 것인가, 이미 있는 계정을 쓸 것인가.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 학생 계정은 두 갈래로 생긴다. 학생이 스스로 만들거나, 관리자가 한꺼번에 만들거나.
    /// 이미 있는 계정을 쓰는 자리에서 또 만들면 같은 사람이 둘이 되고,
    /// 없는 계정에 배정하려 들면 한 줄도 못 넣는다. 그래서 반드시 사람이 정해야 한다.
    /// </para>
    /// <para>
    /// 다만 <b>"벌크로 만들까요" 라고 물으면 안 된다.</b> 그 말을 아는 사람이면 우리가 필요 없다.
    /// 자기 경험으로 답할 수 있는 것으로 바꿔 묻는다 — "학생들이 로그인해 본 적이 있습니까".
    /// </para>
    /// </remarks>
    private static int ChooseWhatToDo(RosterResult result, string sourcePath, bool assumeYes)
    {
        var usable = result.Good.Where(r => r.Upn.Length > 0).ToList();

        Console.WriteLine();
        Ui.Title("다음에 할 일을 정하겠습니다");

        if (assumeYes)
        {
            Ui.Info("자동 모드에서는 여기까지만 합니다. 이 선택은 사람이 해야 합니다.");
            return 0;
        }

        // 전문 용어로 묻지 않는다. 이건 누구나 자기 학교를 떠올려 답할 수 있다.
        Ui.Plain("""
              한 가지만 여쭙겠습니다.

              이 명단에 있는 학생들이 지금까지 학교 계정으로
              Teams 나 아웃룩에 로그인해 본 적이 있습니까?
        """);

        var pick = Ui.Choose("고르세요", new[]
        {
            new Ui.Choice("1", "[1] 아니요 — 계정이 아직 없습니다        (계정부터 만들어야 합니다)",
                          "아니", "없", "안만들", "처음", "새로", "만들어야"),
            new Ui.Choice("2", "[2] 예 — 이미 쓰고 있습니다             (반에 넣기만 하면 됩니다)",
                          "있", "쓰고", "예", "네", "이미", "넣기만"),
            new Ui.Choice("3", "[3] 잘 모르겠습니다                     (확인하는 방법을 알려 드립니다)",
                          "몰라", "모르", "글쎄", "확인"),
        }, "3");

        return pick switch
        {
            "1" => MakeBulkCsv(usable, sourcePath),
            "2" => ExplainAssign(usable),
            _   => ExplainHowToCheck(usable, sourcePath),
        };
    }

    /// <summary>
    /// 모르겠다고 한 사람을 그냥 돌려보내지 않는다. 확인하는 방법을 알려 주고 다시 데려온다.
    /// </summary>
    private static int ExplainHowToCheck(IReadOnlyList<RosterRow> rows, string sourcePath)
    {
        Ui.Title("계정이 있는지 확인하는 법");

        var sample = rows.FirstOrDefault();

        Ui.Plain("""
              가장 빠른 방법은 한 명만 찾아보는 것입니다.

              ① 관리 센터(admin.microsoft.com)를 엽니다
              ② 왼쪽에서 [사용자] → [활성 사용자]
              ③ 오른쪽 위 검색 칸에 아래 아이디를 붙여 넣습니다
        """);

        if (sample is not null)
        {
            Console.WriteLine();
            Ui.Plain($"                 {sample.Upn}");
            Console.WriteLine();
        }

        Ui.Plain("""
              나오면 — 계정이 이미 있습니다. 반에 넣기만 하면 됩니다.
              안 나오면 — 계정이 없습니다. 한꺼번에 만들어야 합니다.

              확인하신 뒤 같은 명령을 한 번 더 실행해 주세요.
        """);

        Console.WriteLine();
        Ui.Dim($"      teavel 명단 \"{sourcePath}\"");
        return 0;
    }

    private static int MakeBulkCsv(IReadOnlyList<RosterRow> rows, string sourcePath)
    {
        Ui.Title("한꺼번에 만들 파일");

        if (rows.Count == 0)
        {
            Ui.Error("아이디가 있는 줄이 하나도 없어 만들 수 없습니다.");
            return 1;
        }

        // 열 이름은 짐작하지 않는다. 관리 센터 견본이 있으면 그것을 그대로 쓴다 —
        // 문서가 '견본과 정확히 같아야 한다' 고 못 박고 있고, 틀리면 올릴 때 통째로 거부당한다.
        //
        // 그런데 '견본 파일 경로' 를 맨입으로 물으면 안 된다. 견본이 무엇인지 모르는 분이
        // 대부분이고, 빈 칸 앞에서 그대로 멈춘다. 있는지부터 갈라 주고 없으면 우리가 정한다.
        Console.WriteLine();
        Ui.Plain("""
              먼저 한 가지만 확인하겠습니다.

              관리 센터에서 '여러 사용자 추가' 를 눌러 보시면
              [샘플 파일 다운로드] 가 있습니다. 그 파일을 이미 받아 두셨습니까?

                [1] 아니요 / 모르겠습니다   (그대로 만들겠습니다 — 대개 이대로 됩니다)
                [2] 예 — 파일이 있습니다    (그 파일의 열 이름을 그대로 쓰겠습니다)
        """);

        var template = "";
        var has = (Ui.Ask("      고르세요 [1] ") ?? "1").Trim();
        if (has == "2")
        {
            Ui.Dim("      그 파일을 이 창에 끌어다 놓으시면 경로가 적힙니다.");
            template = (Ui.Ask("      파일: ") ?? "").Trim().Trim('"');

            if (template.Length > 0 && !File.Exists(template))
            {
                Ui.Warn($"그 자리에 파일이 없습니다: {template}");
                Ui.Dim("      그대로 만들겠습니다. 올릴 때 열 이름이 다르다고 하면 그때 다시 하시면 됩니다.");
                template = "";
            }
        }

        var (text, headers, note) = BulkUserCsv.Build(rows, template.Length > 0 ? template : null);

        var outPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".",
            Path.GetFileNameWithoutExtension(sourcePath) + "-계정만들기.csv");

        try { BulkUserCsv.Write(outPath, text); }
        catch (Exception ex) { Ui.Error($"파일을 쓰지 못했습니다: {ex.Message}"); return 2; }

        // 손으로 한 명씩 만들 때는 15열짜리 파일이 필요 없다 — 이름과 계정만 있으면 된다.
        // 몇 명 안 되거나 파일 올리기가 겁나는 분들이 이쪽을 쓴다.
        var listPath = Path.ChangeExtension(outPath, null) + "-한명씩.txt";
        try
        {
            var lines = new List<string> { "표시 이름\t로그인 아이디", new string('─', 50) };
            lines.AddRange(rows.Select(r => $"{(r.DisplayName.Length > 0 ? r.DisplayName : r.Name)}\t{r.Upn}"));
            File.WriteAllText(listPath, string.Join(Environment.NewLine, lines) + Environment.NewLine,
                new System.Text.UTF8Encoding(true));
        }
        catch { listPath = ""; }

        Console.WriteLine();
        Ui.Ok($"{rows.Count}명 분을 만들었습니다.");
        Ui.Plain($"        {outPath}");
        Ui.Dim($"      {note}");
        Ui.Dim($"      열: {string.Join(", ", headers.Take(5))}{(headers.Count > 5 ? " …" : "")}");

        Console.WriteLine();
        Ui.Plain("""
              올리는 순서

              ① 관리 센터(admin.microsoft.com)를 엽니다
              ② 왼쪽에서 [사용자] → [활성 사용자]
              ③ 위쪽 [여러 사용자 추가] 를 누릅니다
              ④ 방금 만든 파일을 올립니다
              ⑤ 라이선스를 고르고 [추가]

              비밀번호는 관리 센터가 만들어 줍니다. 그 목록을 꼭 내려받아 두세요 —
              그 화면을 닫으면 다시 볼 수 없습니다.
        """);

        if (listPath.Length > 0)
        {
            Console.WriteLine();
            Ui.Plain("""
                  한 명씩 손으로 만드셔도 됩니다.

                  계정을 만드는 데 꼭 필요한 것은 이름과 로그인 아이디 둘뿐입니다.
                  관리 센터에서 [사용자 추가] 를 눌러 아래 목록을 보고 하나씩 넣으시면 됩니다.
                  파일 올리기가 부담스럽거나 사람이 몇 명 안 될 때 이 편이 낫습니다.
            """);
            Console.WriteLine();
            Ui.Plain($"        {listPath}");
        }

        Console.WriteLine();
        Ui.Dim("      계정이 다 만들어진 뒤에 'teavel m365' 로 반에 넣으시면 됩니다.");
        return 0;
    }

    private static int ExplainAssign(IReadOnlyList<RosterRow> rows)
    {
        Ui.Title("반에 넣기");
        Ui.Info($"넣을 수 있는 사람: {rows.Count}명");

        var byClass = rows
            .Where(r => r.Grade.Length > 0 && r.ClassNo.Length > 0)
            .GroupBy(r => $"{r.Grade}학년 {r.ClassNo}반")
            .OrderBy(g => g.Key, StringComparer.CurrentCulture)
            .ToList();

        foreach (var g in byClass) Ui.Plain($"        {g.Key}   {g.Count()}명");

        if (byClass.Count == 0)
            Ui.Warn("학년·반을 못 찾아 어느 반에 넣을지 알 수 없습니다.");

        Console.WriteLine();
        Ui.Warn("반에 실제로 넣는 것은 아직 만들지 않았습니다.");
        Ui.Dim("      팀을 먼저 만들어야 하고(teavel m365), 그다음 이 명단으로 넣게 됩니다.");
        return 0;
    }

    /// <summary>뽑아 낸 줄들을 보여 준다. 만들어 채운 자리와 걸리는 줄을 반드시 짚는다.</summary>
    private static void ShowRows(RosterResult result)
    {
        Console.WriteLine();
        Ui.Info($"명단 {result.Rows.Count}줄을 읽었습니다.");

        if (result.MadeCounts.Count > 0)
        {
            // 무엇을 무엇으로 만들었는지 그대로 적는다. 자리 이름만 말하면
            // '학년·반·번호를 이어 붙였다' 는 문장이 그 열이 없는 파일에도 나가 거짓말이 된다.
            foreach (var (how, n) in result.MadeCounts.OrderByDescending(kv => kv.Value))
                Ui.Dim($"      {how} 만든 줄이 {n}개 있습니다(파일에 없던 값입니다).");
        }

        Console.WriteLine();
        Ui.Dim("      학년  반   번호  학번      이름      표시이름          ID");
        foreach (var r in result.Rows.Take(5))
        {
            Ui.Plain($"        {Pad(r.Grade, 5)} {Pad(r.ClassNo, 4)} {Pad(r.Number, 5)} "
                   + $"{Pad(r.StudentId, 9)} {Pad(r.Name, 9)} {Pad(r.DisplayName, 17)} {r.Upn}");
        }
        if (result.Rows.Count > 5) Ui.Dim($"        … 그 밖에 {result.Rows.Count - 5}줄");

        var dups = RosterExtractor.FindDuplicateUpns(result.Rows);
        if (dups.Count > 0)
        {
            Console.WriteLine();
            Ui.Error($"같은 아이디가 두 번 이상 나옵니다 ({dups.Count}개). 이대로는 만들 수 없습니다.");
            foreach (var d in dups.Take(5)) Ui.Plain($"        {d}");
        }

        var bad = result.Bad.ToList();
        if (bad.Count > 0)
        {
            Console.WriteLine();
            Ui.Warn($"쓸 수 없는 줄이 {bad.Count}개 있습니다.");
            foreach (var r in bad.Take(8))
                Ui.Plain($"        {r.Line}번째 줄 — {string.Join(" · ", r.Problems)}"
                       + (r.Name.Length > 0 ? $"  ({r.Name})" : ""));
            if (bad.Count > 8) Ui.Dim($"        … 그 밖에 {bad.Count - 8}줄");
        }
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
        // 어느 프로그램에서 무엇을 누르면 되는지까지 적는다.
        if (ext == ".hwp")
        {
            Ui.Plain("""
                  한글에서 한 번만 저장해 주시면 읽을 수 있습니다.

                  ① 그 파일을 한글에서 엽니다
                  ② [파일] → [다른 이름으로 저장하기]
                  ③ 파일 형식을 'HWPX 문서 (*.hwpx)' 로 고릅니다
                  ④ 저장한 뒤 그 파일로 다시 실행해 주세요

                  명단이 표로 되어 있으면 그대로 읽습니다.
            """);
        }
        else
        {
            Ui.Plain("""
                  쓰고 계신 프로그램에서 한 번만 저장해 주시면 됩니다.

                  ① 그 파일을 엽니다
                  ② [파일] → [다른 이름으로 저장]
                  ③ 파일 형식에서 'CSV' 또는 'Excel 통합 문서(*.xlsx)' 를 고릅니다
                  ④ 저장한 뒤 그 파일로 다시 실행해 주세요

                  한셀·엑셀 어느 쪽이든 같습니다.
            """);
        }
    }
}
