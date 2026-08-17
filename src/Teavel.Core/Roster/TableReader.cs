using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Teavel.Roster;

/// <summary>표 하나를 읽어 온 것.</summary>
/// <param name="Rows">칸들. 줄마다 길이가 다를 수 있다.</param>
/// <param name="Source">어디서 읽었는지(파일 이름과 시트 이름).</param>
/// <param name="Note">읽으면서 알아 둘 것. 없으면 빈 문자열.</param>
/// <param name="PositionsAreExact">
/// 칸이 제자리에 놓였다고 믿어도 되는지.
///
/// xlsx 는 칸마다 'C5' 같은 자리표가 붙어 있어 빈 칸이 빠져도 제자리에 꽂을 수 있다.
/// csv 는 쉼표 수로만 세므로 가운데 칸이 빠지면 <b>그 뒤가 통째로 한 칸 앞으로 밀린다.</b>
/// 밀린 것과 그냥 끝이 짧은 것을 구별할 수 없어, csv 일 때만 사람에게 확인을 구한다.
/// </param>
public sealed record Table(
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string Source,
    string Note = "",
    bool PositionsAreExact = false);

/// <summary>
/// 명단 파일을 읽는다. <b>Office 가 없어도 읽는다.</b>
///
/// <para>
/// 지금까지 표 읽기는 엑셀 COM 하나였다. 그런데 학교 PC 에는 한셀만 깔린 경우가 있고,
/// 그런 PC 에서는 엑셀 COM 이 아예 없어 아무것도 못 한다.
/// </para>
/// <para>
/// 그래서 <b>csv 와 xlsx 는 직접 읽는다</b> — csv 는 글자일 뿐이고 xlsx 는 zip 안의 xml 이라
/// 둘 다 프로그램 없이 열 수 있다. 한셀도 xlsx·csv 로 저장할 수 있으므로 이 둘만 되면
/// 대부분 통한다. 옛 .xls 와 한셀 원본(.cell)만 프로그램이 필요하다.
/// </para>
/// </summary>
public static class TableReader
{
    /// <summary>이 확장자는 Office 없이 읽는다.</summary>
    public static bool CanReadDirectly(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".csv" or ".txt" or ".tsv" or ".xlsx" or ".xlsm" or ".hwpx";
    }

    /// <summary>파일을 읽는다. 확장자를 보고 알아서 고른다.</summary>
    public static Table Read(string path, int sheet = 1)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" or ".xlsm" => ReadXlsx(path, sheet),
            ".hwpx" => ReadHwpx(path),
            _ => ReadDelimited(path),
        };
    }

    // ─────────────────────────────── csv ───────────────────────────────

    /// <summary>
    /// csv·tsv 를 읽는다. 글자 인코딩과 구분자를 알아서 고른다.
    /// </summary>
    /// <remarks>
    /// 한국 학교에서 나오는 csv 는 대개 <b>CP949</b> 다. UTF-8 로 읽으면 이름이 전부 깨진다.
    /// 반대로 UTF-8 파일을 CP949 로 읽어도 깨진다. 그래서 BOM 을 먼저 보고,
    /// 없으면 UTF-8 로 읽어 보고 깨진 글자가 나오면 CP949 로 다시 읽는다.
    /// </remarks>
    public static Table ReadDelimited(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (text, encName) = DecodeKorean(bytes);

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var delim = GuessDelimiter(lines);

        var rows = new List<IReadOnlyList<string>>();
        foreach (var line in lines)
        {
            if (line.Length == 0 && rows.Count == 0) continue;   // 앞머리 빈 줄만 버린다
            rows.Add(SplitCsvLine(line, delim));
        }

        // 끝의 빈 줄들은 표가 아니다.
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrWhiteSpace)) rows.RemoveAt(rows.Count - 1);

        var what = delim == '\t' ? "탭" : delim.ToString();
        return new Table(rows, Path.GetFileName(path), $"{encName} · 구분자 '{what}'",
            PositionsAreExact: false);
    }

    /// <summary>
    /// 바이트를 한국어가 깨지지 않게 글자로 바꾼다.
    /// </summary>
    /// <remarks>
    /// UTF-8 로 읽었을 때 <c>U+FFFD</c>(모르는 글자)가 나오면 UTF-8 이 아니라는 뜻이다.
    /// 그때 CP949 로 다시 읽는다. 순서가 반대면 안 된다 — CP949 는 아무 바이트나
    /// 받아들여서 깨진 줄도 모르고 넘어간다.
    /// </remarks>
    public static (string Text, string Encoding) DecodeKorean(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3), "UTF-8");

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16");

        var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(bytes);
        if (!utf8.Contains('�')) return (utf8, "UTF-8");

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return (Encoding.GetEncoding(949).GetString(bytes), "CP949");
        }
        catch
        {
            // CP949 를 못 쓰는 환경이면 UTF-8 결과라도 준다. 깨진 글자는 화면에 그대로 보인다.
            return (utf8, "UTF-8(추정)");
        }
    }

    /// <summary>쉼표·탭·세미콜론 중 줄마다 가장 고르게 나오는 것.</summary>
    private static char GuessDelimiter(IReadOnlyList<string> lines)
    {
        var sample = lines.Where(l => l.Trim().Length > 0).Take(10).ToList();
        if (sample.Count == 0) return ',';

        var best = ',';
        var bestScore = -1;

        foreach (var d in new[] { ',', '\t', ';', '|' })
        {
            var counts = sample.Select(l => SplitCsvLine(l, d).Count).ToList();
            if (counts.Max() < 2) continue;

            // 줄마다 칸 수가 같을수록 좋은 구분자다.
            var same = counts.GroupBy(x => x).Max(g => g.Count());
            var score = same * 100 + counts.Max();
            if (score > bestScore) { bestScore = score; best = d; }
        }
        return best;
    }

    /// <summary>따옴표 안의 구분자는 가르지 않는다. 안의 <c>""</c> 는 따옴표 한 개다.</summary>
    public static List<string> SplitCsvLine(string line, char delim)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
                continue;
            }

            if (c == '"') { quoted = true; continue; }
            if (c == delim) { cells.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(c);
        }

        cells.Add(sb.ToString().Trim());
        return cells;
    }

    // ────────────────────────────── 한글 ──────────────────────────────

    /// <summary>
    /// 한글 문서(.hwpx)에서 표를 읽는다. <b>한글이 없어도 읽는다.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 선생님들이 명단을 한글 파일로 주는 일이 흔하다. 그런데 한글에는 두 가지 형식이 있다.
    /// </para>
    /// <list type="bullet">
    /// <item><c>.hwpx</c> — xlsx 처럼 zip 안의 xml 이다. <b>그래서 여기서 읽는다.</b></item>
    /// <item><c>.hwp</c> — 압축된 이진 덩어리라 규격을 구현해야 한다. 아직 못 읽는다.</item>
    /// </list>
    /// <para>
    /// 문서 안에 표가 여럿이면 <b>칸이 가장 많은 표</b>를 명단으로 본다.
    /// 머리글·안내표는 대개 작고, 명단은 사람 수만큼 길기 때문이다.
    /// </para>
    /// <para>
    /// 이름공간 접두어는 판마다 달라서(hp·hs 등) 따지지 않는다 — 태그의 끝 이름만 본다.
    /// </para>
    /// </remarks>
    public static Table ReadHwpx(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        var best = new List<IReadOnlyList<string>>();
        var tables = 0;

        // 본문은 Contents/section0.xml, section1.xml … 으로 나뉜다.
        foreach (var entry in zip.Entries
                     .Where(e => e.FullName.Contains("section", StringComparison.OrdinalIgnoreCase)
                              && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            XDocument doc;
            try { using var st = entry.Open(); doc = XDocument.Load(st); }
            catch (System.Xml.XmlException) { continue; }

            foreach (var tbl in doc.Descendants().Where(e => e.Name.LocalName == "tbl"))
            {
                tables++;
                var rows = new List<IReadOnlyList<string>>();

                foreach (var tr in tbl.Elements().Where(e => e.Name.LocalName == "tr"))
                {
                    var cells = tr.Elements()
                        .Where(e => e.Name.LocalName == "tc")
                        .Select(TextOf)
                        .ToList();
                    if (cells.Count > 0) rows.Add(cells);
                }

                if (rows.Sum(r => r.Count) > best.Sum(r => r.Count)) best = rows;
            }
        }

        if (best.Count == 0)
            throw new InvalidDataException(
                "이 한글 문서 안에서 표를 찾지 못했습니다. 명단이 표가 아니라 글로 적혀 있으면 읽을 수 없습니다.");

        var note = tables > 1 ? $"한글 문서 직접 읽음 (표 {tables}개 중 가장 큰 것)" : "한글 문서 직접 읽음";
        return new Table(best, Path.GetFileName(path), note, PositionsAreExact: true);
    }

    /// <summary>칸 하나의 글자. 한 칸 안에 문단·글자 조각이 여럿일 수 있어 모두 이어 붙인다.</summary>
    private static string TextOf(XElement cell)
    {
        var parts = cell.Descendants()
                        .Where(e => e.Name.LocalName == "t")
                        .Select(e => e.Value);
        return string.Join("", parts).Replace("\u000b", " ").Trim();
    }

    // ────────────────────────────── xlsx ──────────────────────────────

    /// <summary>
    /// xlsx 를 엑셀 없이 읽는다. zip 안의 xml 을 직접 푼다.
    /// </summary>
    /// <remarks>
    /// 글자는 대개 sharedStrings.xml 에 따로 모여 있고, 시트에는 그 번호만 들어 있다.
    /// 그래서 두 파일을 다 봐야 한다. 서식·수식은 보지 않는다 — 수식 칸은 마지막으로
    /// 계산된 값(<c>&lt;v&gt;</c>)을 쓴다.
    /// </remarks>
    public static Table ReadXlsx(string path, int sheet = 1)
    {
        using var zip = ZipFile.OpenRead(path);

        var shared = ReadSharedStrings(zip);
        var (entry, sheetName) = FindSheet(zip, sheet);

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        var rows = new List<IReadOnlyList<string>>();

        foreach (var row in doc.Descendants(ns + "row"))
        {
            // 아무것도 없는 줄은 xml 에서 통째로 빠진다. 그대로 이어 붙이면 줄 번호가 밀려,
            // "3번째 줄" 이라고 했는데 엑셀에서는 5행인 일이 생긴다.
            // 파일을 열어 확인하실 분들이라 번호가 맞아야 한다.
            var at = (int?)row.Attribute("r") - 1;
            while (at.HasValue && at.Value >= 0 && rows.Count < at.Value)
                rows.Add(Array.Empty<string>());

            var cells = new List<string>();

            foreach (var c in row.Elements(ns + "c"))
            {
                // 빈 칸도 마찬가지로 빠진다. r="C5" 에서 열 위치를 되짚어 자리를 맞춘다.
                var col = ColumnIndexOf((string?)c.Attribute("r"));
                while (col >= 0 && cells.Count < col) cells.Add("");

                cells.Add(CellText(c, ns, shared));
            }

            rows.Add(cells);
        }

        while (rows.Count > 0 && rows[^1].All(string.IsNullOrWhiteSpace)) rows.RemoveAt(rows.Count - 1);

        return new Table(rows, $"{Path.GetFileName(path)} [{sheetName}]", "xlsx 직접 읽음",
            PositionsAreExact: true);
    }

    private static string CellText(XElement c, XNamespace ns, IReadOnlyList<string> shared)
    {
        var type = (string?)c.Attribute("t");

        // t="s" 면 값이 아니라 sharedStrings 의 번호다.
        if (type == "s")
        {
            var v = c.Element(ns + "v")?.Value;
            return int.TryParse(v, out var i) && i >= 0 && i < shared.Count ? shared[i] : "";
        }

        // t="inlineStr" 이면 글자가 칸 안에 그대로 있다.
        if (type == "inlineStr")
            return string.Concat(c.Element(ns + "is")?.Descendants(ns + "t").Select(t => t.Value) ?? Array.Empty<string>());

        return c.Element(ns + "v")?.Value ?? "";
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return Array.Empty<string>();

        using var s = entry.Open();
        var doc = XDocument.Load(s);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        // <si> 하나가 문자열 하나인데, 글자 서식이 섞이면 <t> 가 여러 개로 쪼개져 온다.
        return doc.Descendants(ns + "si")
                  .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value)))
                  .ToList();
    }

    private static (ZipArchiveEntry Entry, string Name) FindSheet(ZipArchive zip, int sheet)
    {
        var sheets = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)
                     && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();

        if (sheets.Count == 0) throw new InvalidDataException("이 xlsx 안에 시트가 없습니다.");

        var i = Math.Clamp(sheet - 1, 0, sheets.Count - 1);
        return (sheets[i], NameOfSheet(zip, i) ?? $"시트{i + 1}");
    }

    /// <summary>workbook.xml 에 적힌 시트 이름. 못 찾으면 null.</summary>
    private static string? NameOfSheet(ZipArchive zip, int index)
    {
        var wb = zip.GetEntry("xl/workbook.xml");
        if (wb is null) return null;

        try
        {
            using var s = wb.Open();
            var doc = XDocument.Load(s);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var names = doc.Descendants(ns + "sheet").Select(e => (string?)e.Attribute("name")).ToList();
            return index < names.Count ? names[index] : null;
        }
        catch { return null; }
    }

    /// <summary><c>"C5"</c> → 2. 못 읽으면 -1.</summary>
    private static int ColumnIndexOf(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return -1;

        var n = 0;
        var any = false;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') { n = n * 26 + (ch - 'A' + 1); any = true; }
            else if (ch is >= 'a' and <= 'z') { n = n * 26 + (ch - 'a' + 1); any = true; }
            else break;
        }
        return any ? n - 1 : -1;
    }
}
