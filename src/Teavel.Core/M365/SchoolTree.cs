using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Teavel.M365;

/// <summary>만들 것의 종류.</summary>
public enum GroupKind
{
    /// <summary>보안 그룹 — 권한·정책을 묶는다. 메일 주소가 없다.</summary>
    Security,

    /// <summary>Microsoft 365 그룹 — 공유 사서함·SharePoint 가 딸려 온다.</summary>
    M365,

    /// <summary>Teams 팀 — 만들면 M365 그룹이 함께 생긴다.</summary>
    Team,
}

/// <summary>선언된 그룹 하나(펼쳐진 뒤의 실물 하나).</summary>
/// <param name="Id">선언에서 온 id — 어느 선언에서 나왔는지 되짚을 때 쓴다.</param>
/// <param name="Kind">종류.</param>
/// <param name="DisplayName">화면에 보이는 이름. 대조의 기준이다.</param>
/// <param name="MailNickname">메일 주소가 되는 별칭. 보안 그룹은 비어 있을 수 있다.</param>
/// <param name="Description">설명.</param>
/// <param name="Template">team 일 때 standard · educationClass · educationStaff.</param>
/// <param name="Visibility">private · public.</param>
/// <param name="Values">
/// 이 항목이 펼쳐질 때 쓴 값들 — <c>{학년:"1", 반:"3"}</c>.
/// <b>명단의 학생을 어느 팀에 넣을지 정할 때 이것으로 잇는다.</b>
/// 이름('1학년 3반')으로 짐작하면 학교마다 표기가 달라 어긋난다.
/// generate 가 없으면 비어 있다.
/// </param>
/// <param name="Channels">
/// 팀 안에 둘 채널 이름들. team 이 아니면 비어 있다.
/// '일반'(General)은 팀을 만들면 저절로 생기므로 여기 적지 않는다.
/// </param>
public sealed record DeclaredGroup(
    string Id,
    GroupKind Kind,
    string DisplayName,
    string MailNickname,
    string Description,
    string Template,
    string Visibility,
    IReadOnlyList<string> Channels,
    IReadOnlyDictionary<string, string> Values);

/// <summary>선언을 읽다 만난 문제 하나.</summary>
/// <param name="Where">어느 선언에서 났는지.</param>
/// <param name="Problem">무엇이 잘못됐는지.</param>
public sealed record TreeProblem(string Where, string Problem);

/// <summary>
/// 학교 M365 기본 트리 — "이 학교는 이런 모양이어야 한다" 는 선언.
///
/// 명령형이 아니라 선언형이라는 점이 요점이다. Teavel 은 이것을 테넌트의 지금 상태와
/// 대조해 <b>없는 것만</b> 만든다. 그래서 중간에 끊겨도 다시 돌리면 되고,
/// 이미 있는 것을 덮어쓰지 않는다.
///
/// 펼치기·검증은 전부 여기(C#)서 한다. 테넌트가 없어도 확인할 수 있어야 하기 때문이다 —
/// PowerShell 쪽에는 '목록 가져오기' 와 '하나 만들기' 만 남긴다.
/// </summary>
public sealed class SchoolTree
{
    // mailNickname 은 메일 주소가 된다 — 영문·숫자·붙임표·밑줄·점만.
    private static readonly Regex NicknameOk = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    // {학년} 같은 자리표시자.
    private static readonly Regex Placeholder = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private SchoolTree(string school, IReadOnlyList<DeclaredGroup> groups,
                       IReadOnlyList<TreeProblem> problems, string source)
    {
        School = school;
        Groups = groups;
        Problems = problems;
        Source = source;
    }

    /// <summary>학교 이름(선언에 적힌 것).</summary>
    public string School { get; }

    /// <summary>펼쳐진 그룹들 — 이대로 만들면 된다.</summary>
    public IReadOnlyList<DeclaredGroup> Groups { get; }

    /// <summary>선언을 읽다 만난 문제들. 비어 있어야 적용할 수 있다.</summary>
    public IReadOnlyList<TreeProblem> Problems { get; }

    /// <summary>어느 파일에서 읽었는지.</summary>
    public string Source { get; }

    /// <summary>문제 없이 읽혔는지.</summary>
    public bool Ok => Problems.Count == 0;

    /// <summary>
    /// 이 학교의 선언을 읽는다.
    /// </summary>
    /// <remarks>
    /// <b>학교가 정한 것이 있으면 그것이 이긴다.</b> 없으면 묻어 온 원본을 쓴다.
    ///
    /// 묻어 온 쪽을 고쳐 봐야 소용이 없다 — <see cref="Platform.Payload"/> 가 켤 때마다
    /// 원본과 견줘 다르면 도로 덮어쓴다. 그래서 학교가 정한 것은 payload 가 손대지 않는
    /// 자리에 따로 둔다(<see cref="SchoolChoice"/>).
    /// </remarks>
    public static SchoolTree Load(string appDirectory)
        => SchoolChoice.Exists
            ? LoadFrom(SchoolChoice.Path)
            : LoadFrom(Path.Combine(Platform.Payload.Ensure(appDirectory, "catalog"), "m365-tree.json"));

    /// <summary>지정한 경로에서 읽는다. 파일이 없으면 빈 트리.</summary>
    public static SchoolTree LoadFrom(string path)
    {
        if (!File.Exists(path))
            return new SchoolTree("", Array.Empty<DeclaredGroup>(), Array.Empty<TreeProblem>(), path);

        TreeDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<TreeDto>(File.ReadAllText(path), JsonOpts);
        }
        catch (JsonException ex)
        {
            return new SchoolTree("", Array.Empty<DeclaredGroup>(),
                new[] { new TreeProblem(Path.GetFileName(path), $"파일을 읽지 못했습니다: {ex.Message}") }, path);
        }

        if (dto is null)
            return new SchoolTree("", Array.Empty<DeclaredGroup>(),
                new[] { new TreeProblem(Path.GetFileName(path), "내용이 비어 있습니다.") }, path);

        var groups = new List<DeclaredGroup>();
        var raw = new List<TreeProblem>();

        foreach (var d in dto.Groups ?? new List<GroupDto>())
            Expand(d, groups, raw);

        // 펼친 항목마다 같은 문제가 반복된다 — 18개 반이면 18번 나온다.
        // 선언 하나의 잘못은 한 줄로 말하고, 몇 개에 걸쳤는지만 덧붙인다.
        var problems = raw
            .GroupBy(p => (p.Where, p.Problem))
            .Select(g => g.Count() == 1
                ? g.First()
                : g.First() with { Problem = $"{g.First().Problem} ({g.Count()}개 항목에서)" })
            .ToList();

        // 같은 별칭이 둘이면 두 번째부터는 만들다 실패한다 — 적용 전에 잡는다.
        foreach (var dup in groups
            .Where(g => g.MailNickname.Length > 0)
            .GroupBy(g => g.MailNickname, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1))
        {
            problems.Add(new TreeProblem(dup.Key,
                $"별칭이 {dup.Count()}번 겹칩니다: {string.Join(" · ", dup.Select(g => g.DisplayName))}"));
        }

        foreach (var dup in groups
            .GroupBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1))
        {
            problems.Add(new TreeProblem(dup.Key, $"같은 이름이 {dup.Count()}개 선언돼 있습니다."));
        }

        return new SchoolTree(dto.School ?? "", groups, problems, path);
    }

    /// <summary>선언 하나를 generate 목록만큼 펼친다.</summary>
    private static void Expand(GroupDto d, List<DeclaredGroup> into, List<TreeProblem> problems)
    {
        var where = d.Id ?? d.DisplayName ?? "(이름 없는 선언)";

        if (string.IsNullOrWhiteSpace(d.DisplayName))
        {
            problems.Add(new TreeProblem(where, "displayName 이 없습니다."));
            return;
        }

        if (!TryParseKind(d.Kind, out var kind))
        {
            problems.Add(new TreeProblem(where,
                $"kind 가 '{d.Kind}' 입니다. security · m365 · team 중 하나여야 합니다."));
            return;
        }

        foreach (var values in Combinations(d.Generate))
        {
            var name = Substitute(d.DisplayName!, values);
            var nick = Substitute(d.MailNickname ?? "", values);
            var desc = Substitute(d.Description ?? "", values);

            // 자리표시자가 남아 있으면 generate 에 그 이름이 없다는 뜻이다.
            if (Placeholder.IsMatch(name) || Placeholder.IsMatch(nick))
            {
                var missing = Placeholder.Matches(name + " " + nick)
                    .Select(m => m.Groups[1].Value).Distinct();
                problems.Add(new TreeProblem(where,
                    $"채우지 못한 자리가 있습니다: {string.Join(", ", missing.Select(m => "{" + m + "}"))}. "
                  + "generate 에 그 이름을 넣어 주세요."));
                continue;
            }

            // 보안 그룹은 메일 주소가 없어도 되지만, m365·team 은 반드시 있어야 한다.
            if (kind != GroupKind.Security && nick.Length == 0)
            {
                problems.Add(new TreeProblem(where, $"'{name}' 에 mailNickname 이 없습니다. "
                                                  + "m365·team 은 메일 주소가 필요합니다."));
                continue;
            }

            if (nick.Length > 0 && !NicknameOk.IsMatch(nick))
            {
                problems.Add(new TreeProblem(where,
                    $"'{nick}' 은 별칭으로 쓸 수 없습니다. 영문자·숫자·붙임표·밑줄·점만 됩니다"
                  + (Regex.IsMatch(nick, "[가-힣]") ? " (한글은 메일 주소가 될 수 없습니다)." : ".")));
                continue;
            }

            // 채널 이름에도 {학년}·{반} 을 쓸 수 있다 — '3학년 4반 알림' 같은 것.
            var channels = new List<string>();
            foreach (var raw in d.Channels ?? new List<string>())
            {
                var ch = Substitute(raw ?? "", values).Trim();
                if (ch.Length == 0) continue;

                // '일반'(General)은 팀을 만들면 저절로 생긴다. 또 만들려 하면 실패하므로 걸러 낸다.
                if (ch is "일반" or "General" || ch.Equals("general", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Placeholder.IsMatch(ch))
                {
                    problems.Add(new TreeProblem(where,
                        $"채널 '{ch}' 에 채우지 못한 자리가 있습니다. generate 에 그 이름을 넣어 주세요."));
                    continue;
                }

                // 한 팀 안에서 채널 이름이 겹치면 두 번째부터 만들다 실패한다.
                if (channels.Contains(ch, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add(new TreeProblem(where, $"채널 '{ch}' 이(가) 두 번 적혀 있습니다."));
                    continue;
                }

                channels.Add(ch);
            }

            if (channels.Count > 0 && kind != GroupKind.Team)
            {
                problems.Add(new TreeProblem(where,
                    "채널은 team 에만 둘 수 있습니다. kind 를 team 으로 바꾸거나 channels 를 지워 주세요."));
                continue;
            }

            into.Add(new DeclaredGroup(
                Id: d.Id ?? "",
                Kind: kind,
                DisplayName: name,
                MailNickname: nick,
                Description: desc,
                Template: string.IsNullOrWhiteSpace(d.Template) ? "standard" : d.Template!,
                Visibility: string.IsNullOrWhiteSpace(d.Visibility) ? "private" : d.Visibility!,
                Channels: channels,
                Values: values));
        }
    }

    /// <summary>
    /// generate 의 값들을 곱집합으로 펼친다.
    /// generate 가 없으면 빈 조합 하나 — 즉 선언 그대로 하나만 만든다.
    /// </summary>
    private static IEnumerable<Dictionary<string, string>> Combinations(
        Dictionary<string, List<JsonElement>>? generate)
    {
        if (generate is null || generate.Count == 0)
        {
            yield return new Dictionary<string, string>();
            yield break;
        }

        // 선언에 적힌 순서를 지킨다 — 1학년 1반, 1학년 2반… 순으로 나오게.
        var keys = generate.Keys.ToList();
        var lists = keys.Select(k => generate[k].Select(Stringify).ToList()).ToList();
        var index = new int[keys.Count];

        while (true)
        {
            var combo = new Dictionary<string, string>();
            for (var i = 0; i < keys.Count; i++) combo[keys[i]] = lists[i][index[i]];
            yield return combo;

            var pos = keys.Count - 1;
            while (pos >= 0 && ++index[pos] >= lists[pos].Count) { index[pos] = 0; pos--; }
            if (pos < 0) yield break;
        }
    }

    private static string Stringify(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.ToString(),
        _ => e.ToString(),
    };

    /// <summary>{이름} 자리를 값으로 바꾼다. 없는 이름은 그대로 둔다(위에서 문제로 잡는다).</summary>
    private static string Substitute(string template, IReadOnlyDictionary<string, string> values)
        => Placeholder.Replace(template, m =>
            values.TryGetValue(m.Groups[1].Value.Trim(), out var v) ? v : m.Value);

    private static bool TryParseKind(string? raw, out GroupKind kind)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "security": kind = GroupKind.Security; return true;
            case "m365": kind = GroupKind.M365; return true;
            case "team": kind = GroupKind.Team; return true;
            default: kind = GroupKind.Security; return false;
        }
    }

    // JSON 모양 그대로의 중간 타입.
    private sealed class TreeDto
    {
        public int SchemaVersion { get; set; }
        public string? School { get; set; }
        public List<GroupDto>? Groups { get; set; }
    }

    private sealed class GroupDto
    {
        public string? Id { get; set; }
        public string? Kind { get; set; }
        public string? DisplayName { get; set; }
        public string? MailNickname { get; set; }
        public string? Description { get; set; }
        public string? Template { get; set; }
        public string? Visibility { get; set; }
        public Dictionary<string, List<JsonElement>>? Generate { get; set; }
        public List<string>? Channels { get; set; }
    }
}
