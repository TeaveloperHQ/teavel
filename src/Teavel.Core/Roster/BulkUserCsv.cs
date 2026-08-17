using System.Text;

namespace Teavel.Roster;

/// <summary>
/// 관리 센터에 올릴 '한꺼번에 사용자 추가' csv 를 만든다.
///
/// <para>
/// 계정 만들기는 Exchange·Teams 모듈로 못 한다. Graph 아니면 관리 센터인데,
/// Graph 는 관리자 동의 화면을 부른다. 그래서 <b>관리 센터에 올릴 파일을 만들어 주는 쪽</b>을 골랐다.
/// 동의 화면이 없고, 학생 개인 정보가 우리 코드를 거쳐 어디로도 나가지 않는다.
/// </para>
/// <para>
/// 문제는 열 이름이다. 마이크로소프트 문서가 <b>"견본과 열 이름이 정확히 같아야 한다"</b> 고
/// 못 박고 있는데, 그 이름은 포털 언어와 판에 따라 다를 수 있다.
/// 그래서 <b>관리자가 내려받은 견본 파일을 받아 그 머리글을 그대로 쓴다.</b>
/// 우리가 이름을 짐작하지 않는다 — 짐작이 틀리면 올리는 순간 통째로 거부당한다.
/// </para>
/// <para>
/// 견본이 없으면 문서에 적힌 이름으로 만들되, 화면에서 견본을 받아 오라고 권한다.
/// </para>
/// </summary>
public static class BulkUserCsv
{
    /// <summary>견본이 없을 때 쓰는 열 이름. 마이크로소프트 문서의 것이다.</summary>
    public static readonly IReadOnlyList<string> DefaultHeaders = new[]
    {
        "User Name", "First Name", "Last Name", "Display Name", "Job Title",
        "Department", "Office Number", "Office Phone", "Mobile Phone", "Fax",
        "Address", "City", "State or Province", "ZIP or Postal Code", "Country or Region",
    };

    /// <summary>
    /// 견본의 열 하나가 우리 값 중 무엇을 받아야 하는지.
    /// </summary>
    /// <remarks>
    /// 열 이름을 다듬어 견준다 — 'User Name' · 'Username' · '사용자 이름' 이 다 같은 자리다.
    /// 못 알아본 열은 빈칸으로 둔다. 관리 센터가 필수로 요구하는 것은
    /// 아이디와 표시 이름 둘뿐이라 나머지는 비어도 된다.
    /// </remarks>
    private static string ValueFor(string header, RosterRow row, string? department)
    {
        var h = RosterSchema.Normalize(header);

        if (h is "username" or "userprincipalname" or "upn" or "사용자이름" or "사용자계정" or "로그인이름")
            return row.Upn;

        if (h is "displayname" or "표시이름" or "표시명")
            return row.DisplayName.Length > 0 ? row.DisplayName : row.Name;

        // 한국 이름은 성과 이름을 가르지 않는다. 억지로 쪼개면 '홍' 과 '길동' 이 되어
        // 과제 화면에서 이상하게 보인다. 성 자리에 통이름을 넣는 편이 낫다.
        if (h is "lastname" or "surname" or "성" or "성씨")
            return row.Name;

        if (h is "firstname" or "givenname" or "이름")
            return "";

        if (h is "department" or "부서")
            return department ?? DepartmentOf(row);

        return "";
    }

    /// <summary>부서를 안 주면 '1학년 4반' 처럼 넣어 둔다. 나중에 사람을 찾을 때 쓸모가 있다.</summary>
    private static string DepartmentOf(RosterRow row)
        => row.Grade.Length > 0 && row.ClassNo.Length > 0 ? $"{row.Grade}학년 {row.ClassNo}반" : "";

    /// <summary>
    /// csv 를 만든다.
    /// </summary>
    /// <param name="rows">명단 줄들. 아이디가 없는 줄은 부르는 쪽이 미리 걸러야 한다.</param>
    /// <param name="templatePath">관리 센터에서 내려받은 견본. null 이면 문서의 열 이름을 쓴다.</param>
    /// <param name="department">부서 칸에 넣을 값. null 이면 '1학년 4반' 처럼 만들어 넣는다.</param>
    public static (string Text, IReadOnlyList<string> Headers, string Note) Build(
        IReadOnlyList<RosterRow> rows, string? templatePath = null, string? department = null)
    {
        var headers = DefaultHeaders;
        var note = "마이크로소프트 문서의 열 이름으로 만들었습니다.";

        if (templatePath is { Length: > 0 } && File.Exists(templatePath))
        {
            var t = TableReader.Read(templatePath);
            var head = t.Rows.FirstOrDefault(r => r.Count(c => c.Trim().Length > 0) >= 3);
            if (head is { Count: > 0 })
            {
                headers = head.Select(h => h.Trim()).ToList();
                note = $"내려받으신 견본({Path.GetFileName(templatePath)})의 열 이름을 그대로 썼습니다.";
            }
        }

        var sb = new StringBuilder();
        sb.Append(string.Join(",", headers.Select(Quote))).Append("\r\n");

        foreach (var row in rows)
            sb.Append(string.Join(",", headers.Select(h => Quote(ValueFor(h, row, department))))).Append("\r\n");

        return (sb.ToString(), headers, note);
    }

    /// <summary>
    /// 파일로 쓴다. <b>BOM 을 붙인다.</b>
    /// </summary>
    /// <remarks>
    /// BOM 이 없으면 엑셀이 CP949 로 열어 이름이 전부 깨진다. 관리자가 올리기 전에
    /// 한 번 열어 볼 텐데, 거기서 깨져 보이면 잘못된 줄 알고 손대게 된다.
    /// </remarks>
    public static void Write(string path, string text)
        => File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    private static string Quote(string v)
    {
        v ??= "";
        return v.Contains(',') || v.Contains('"') || v.Contains('\n')
            ? '"' + v.Replace("\"", "\"\"") + '"'
            : v;
    }
}
