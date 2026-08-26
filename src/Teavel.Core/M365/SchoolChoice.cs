using System.Text.Json;
using System.Text.Json.Nodes;

namespace Teavel.M365;

/// <summary>
/// <b>학교가 정한 것</b>을 두는 자리.
///
/// <para>
/// 선언(<c>m365-tree.json</c>)은 exe 안에 묻혀 오고, 처음 쓸 때
/// <c>…\Teavel\runtime\catalog\</c> 로 꺼내진다. 그런데 <b>그 꺼낸 것을 고쳐 봐야 소용이 없다</b> —
/// <see cref="Platform.Payload"/> 가 켤 때마다 묻어 둔 원본과 견줘 다르면 도로 덮어쓴다.
/// 실기로 확인했다: <c>school</c> 값을 고치고 한 번 돌리니 빈 값으로 돌아왔다.
/// </para>
/// <para>
/// 그래서 학교가 정한 것은 <b>payload 가 손대지 않는 곳</b>에 따로 둔다.
/// 이것이 있으면 이것이 이기고, 없으면 묻어 둔 원본을 쓴다.
/// </para>
/// <para>
/// <b>관리자에게 파일 이야기를 하지 않는다.</b> 선언 파일을 고치라고 하는 순간 그 자리에서
/// 막힌다는 것이 이 프로젝트가 처음부터 정해 둔 것이고, 여기는 그것을 지키기 위한 뒷일이다.
/// 관리 화면이 단추를 주고, 그 결과가 여기 남는다.
/// </para>
/// </summary>
public static class SchoolChoice
{
    /// <summary>학교가 정한 선언이 놓이는 자리.</summary>
    /// <remarks>
    /// <c>runtime\</c> 의 이웃이지 그 아래가 아니다. payload 는 <c>runtime\</c> 안만 건드린다.
    /// </remarks>
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Teaveloper", "Teavel", "school", "m365-tree.json");

    /// <summary>학교가 정한 것이 있는지.</summary>
    public static bool Exists => File.Exists(Path);

    /// <summary>이 트리가 학교가 정한 것에서 왔는지.</summary>
    public static bool Own(SchoolTree tree)
        => string.Equals(tree.Source, Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 학교 사본이 없으면 묻어 둔 원본을 그대로 떠 온다.
    /// </summary>
    /// <remarks>
    /// <b>펼친 결과가 아니라 파일을 그대로 복사한다.</b> 펼친 것을 되돌려 쓰면
    /// <c>generate</c> 가 열여덟 줄로 풀려 버려서, 나중에 반이 늘어도 따라오지 못한다.
    /// </remarks>
    public static string Adopt(string appDirectory)
    {
        var mine = Path;
        if (File.Exists(mine)) return mine;

        var origin = System.IO.Path.Combine(
            Platform.Payload.Ensure(appDirectory, "catalog"), "m365-tree.json");

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(mine)!);
        File.Copy(origin, mine, overwrite: false);
        return mine;
    }

    /// <summary>
    /// 선언 하나를 뺀다. 뺐으면 <c>true</c>.
    /// </summary>
    /// <param name="id">선언의 id. 비어 있으면 이름으로 찾는다.</param>
    /// <param name="displayName">선언의 displayName. id 로 못 찾을 때 쓴다.</param>
    /// <remarks>
    /// JSON 을 <see cref="JsonNode"/> 로 열어 그 항목만 들어낸다. 모델로 읽었다 다시 쓰면
    /// <c>$comment</c> · <c>generate</c> · <c>$note</c> 같은 것이 통째로 날아간다.
    /// </remarks>
    public static bool Drop(string appDirectory, string id, string displayName)
    {
        var path = Adopt(appDirectory);

        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); }
        catch (JsonException) { return false; }

        if (root?["groups"] is not JsonArray groups) return false;

        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (g is null) continue;

            var gid = g["id"]?.GetValue<string>() ?? "";
            var name = g["displayName"]?.GetValue<string>() ?? "";

            var hit = id.Length > 0 && string.Equals(gid, id, StringComparison.OrdinalIgnoreCase);
            if (!hit && displayName.Length > 0)
                hit = string.Equals(name, displayName, StringComparison.Ordinal);

            if (!hit) continue;

            groups.RemoveAt(i);
            Write(path, root);
            return true;
        }

        return false;
    }

    /// <summary>학교가 정한 것을 버리고 묻어 둔 원본으로 돌아간다.</summary>
    public static bool Reset()
    {
        if (!File.Exists(Path)) return false;
        File.Delete(Path);
        return true;
    }

    private static void Write(string path, JsonNode root)
    {
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        // BOM 을 붙인다. Windows PowerShell 5.1 이 BOM 없는 파일을 CP949 로 읽어
        // 한글이 깨지는 일을 이 저장소에서 이미 한 번 겪었다.
        File.WriteAllText(path, json, new System.Text.UTF8Encoding(true));
    }
}
