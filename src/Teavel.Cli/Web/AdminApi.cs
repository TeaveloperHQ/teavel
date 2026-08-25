using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Teavel.M365;
using Teavel.Roster;

namespace Teavel.Cli.Web;

/// <summary>
/// 관리 화면이 부르는 것들.
///
/// <para>
/// <b>판단은 여전히 C# 에 있다.</b> 화면은 무엇을 보여 줄지와 무엇을 고를지를 맡고,
/// 무엇이 정리 후보인지 · 어느 반이 어느 팀인지 · 누가 선생님인지는 전부
/// <see cref="InventoryTriage"/> · <see cref="TreeReconciler"/> · <see cref="MemberPlanner"/> ·
/// <see cref="TeacherFinder"/> 가 정한다. 흐름(<see cref="M365Flow"/>)이 쓰는 것과 같은 것들이다.
/// </para>
/// <para>
/// 그래서 화면을 고쳐도 판단이 흔들리지 않고, 판단을 고치면 두 곳에 함께 반영된다.
/// </para>
/// </summary>
public sealed class AdminApi
{
    /// <remarks>
    /// 한글을 <c>\uXXXX</c> 로 바꾸지 않게 한다. 그대로 두면 답이 서너 배로 부풀고,
    /// 무엇보다 개발할 때 눈으로 읽을 수 없게 된다.
    /// </remarks>
    private static readonly JsonSerializerOptions Shape = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly M365Host _host;
    private readonly SchoolTree _tree;
    private readonly JobBoard _jobs = new();
    private readonly string _token;

    private List<ExistingGroup> _inventory = new();
    private IReadOnlyList<TenantUser> _people = Array.Empty<TenantUser>();
    private RosterResult? _roster;
    private string _rosterName = "";
    private readonly Dictionary<string, IReadOnlyList<TeamMember>> _members = new(StringComparer.OrdinalIgnoreCase);
    private bool _teamsReady;
    private bool _scanned;

    /// <summary>화면에서 [끝내기] 를 눌렀는지. 콘솔이 이것을 보고 판을 접는다.</summary>
    public bool Finished { get; private set; }

    public AdminApi(M365Host host, SchoolTree tree, string token)
    {
        _host = host;
        _tree = tree;
        _token = token;
    }

    /// <summary>
    /// 상주 PowerShell 이 흘려보낸 문구 한 줄. 돌고 있는 일이 있으면 그쪽에도 적는다.
    /// </summary>
    /// <remarks>
    /// 이게 없으면 <b>브라우저 로그인 안내가 콘솔에만 나온다.</b> 관리자는 관리 화면을
    /// 보고 있으니 아무 일도 안 일어나는 것처럼 보이고, 뒤에 뜬 로그인 창은 못 본 채
    /// 시간만 흐른다. 실기에서 이 모양으로 한 번 끝난 적이 있다.
    /// </remarks>
    public void Note(string line)
    {
        if (line.Trim().Length == 0) return;
        _jobs.Current?.Dim(line.Trim());
    }

    /// <summary>처음 한 번 읽어 둔다. 화면이 뜨자마자 빈 표를 보여 주지 않으려는 것이다.</summary>
    public async Task PrimeAsync(CancellationToken ct)
    {
        await ReadInventoryAsync(ct).ConfigureAwait(false);
        await ReadPeopleAsync(ct).ConfigureAwait(false);
    }

    // ───────────────────────────── 들어오는 길 ─────────────────────────────

    public async Task<HttpSay> HandleAsync(HttpAsk ask, CancellationToken ct)
    {
        if (!ask.Path.StartsWith("/api/", StringComparison.Ordinal))
            return Assets.Serve(ask.Path);

        // 같은 PC 의 다른 프로그램이 포트를 훑어 학교 테넌트를 만지는 일은 없어야 한다.
        // 화면은 첫 주소에서 받은 열쇠를 머리글에 붙여 온다.
        if (!string.Equals(ask.H("x-teavel-token"), _token, StringComparison.Ordinal))
            return HttpSay.Text(403, "열쇠가 맞지 않습니다.");

        _jobs.Sweep();

        return ask.Path switch
        {
            "/api/hello" => Ok(Hello()),
            "/api/overview" => Ok(Overview()),
            "/api/groups" => Ok(Groups()),
            "/api/plan" => Ok(PlanView()),
            "/api/people" => Ok(People()),
            "/api/classes" => Ok(Classes()),
            "/api/teachers" => Ok(Teachers(ask.Q("q"))),
            "/api/members" => await MembersAsync(ask.Q("groupId"), ct).ConfigureAwait(false),
            "/api/job" => Ok(JobView(ask.Q("id"), ask.Q("from"))),

            "/api/groups/rename" => await RenameAsync(ask, ct).ConfigureAwait(false),
            "/api/groups/archive" => Ok(StartArchive(ask, ct)),
            "/api/groups/delete" => Ok(StartDelete(ask, ct)),
            "/api/plan/create" => Ok(StartCreate(ct)),
            "/api/classes/scan" => Ok(StartScan(ct)),
            "/api/members/add" => Ok(StartAddMembers(ask, ct)),
            "/api/owners/assign" => Ok(StartAssignOwners(ask, ct)),
            "/api/people/rename" => await RenamePersonAsync(ask, ct).ConfigureAwait(false),
            "/api/roster" => Ok(TakeRoster(ask)),
            "/api/refresh" => Ok(StartRefresh(ct)),
            "/api/quit" => Quit(),

            _ => HttpSay.NotFound,
        };
    }

    private static HttpSay Ok(object payload) => HttpSay.Json(JsonSerializer.Serialize(payload, Shape));

    private HttpSay Quit()
    {
        Finished = true;
        return Ok(new { ok = true });
    }

    // ───────────────────────────── 읽어 오는 것 ─────────────────────────────

    private async Task ReadInventoryAsync(CancellationToken ct)
    {
        var res = await _host.CallAsync("Get-TeavelM365Inventory",
            timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false);

        if (res.Ok) _inventory = M365Flow.ParseInventory(res.Details);
    }

    private async Task ReadPeopleAsync(CancellationToken ct)
    {
        var res = await _host.CallAsync("Get-TeavelTenantUser",
            timeout: TimeSpan.FromMinutes(10), ct: ct).ConfigureAwait(false);

        if (res.Ok) _people = UserDirectory.Parse(res.Details);
    }

    /// <summary>선언한 학교 모양. 명단이 있으면 그것으로 반을 만들고, 없으면 선언 파일 그대로.</summary>
    private IReadOnlyList<DeclaredGroup> Declared()
    {
        if (_roster is null) return _tree.Groups;

        var shape = SchoolShape.Read(_roster.Rows);
        if (shape.Classes.Count == 0) return _tree.Groups;

        var pattern = SchoolShape.FindClassPattern(_tree);
        var classes = SchoolShape.ToDeclarations(shape, pattern);
        return SchoolShape.WithoutClasses(_tree, pattern).Concat(classes).ToList();
    }

    // ───────────────────────────── 화면에 줄 것 ─────────────────────────────

    private object Hello() => new
    {
        school = _tree.Source,
        roster = _rosterName,
        rosterRows = _roster?.Rows.Count(r => r.Ok) ?? 0,
        teamsReady = _teamsReady,
        busy = _jobs.Busy,
    };

    /// <summary>
    /// 재고를 나눈 것. <b>선언에 있는 것은 정리 후보에서 뺀다.</b>
    /// </summary>
    /// <remarks>
    /// 팀 18개를 만든 직후 다시 보면 <b>그중 13개를 지우자고 한다.</b> 학생을 넣기 전이라
    /// 구성원이 0명이고, 분류는 그것을 '쓰이지 않는 것 같다' 로 읽기 때문이다.
    /// 관리자가 그대로 눌렀으면 방금 만든 것을 도로 지울 뻔했다(실기에서 그랬다).
    ///
    /// 선언에 있다는 것은 '이 학교에 있어야 하는 것' 이라는 뜻이므로 비어 있어도 정상이다.
    /// </remarks>
    private List<(TriagedGroup Item, bool Declared, bool Candidate)> Sorted()
    {
        var declared = Declared()
            .Select(g => TreeReconciler.Loosen(g.DisplayName))
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return InventoryTriage.Triage(_inventory).Select(t =>
        {
            var mine = declared.Contains(TreeReconciler.Loosen(t.Group.DisplayName));
            return (t, mine, t.Bucket == TriageBucket.Candidate && !mine);
        }).ToList();
    }

    private object Overview()
    {
        var triaged = Sorted();
        var plan = TreeReconciler.Plan(Declared(), _inventory);
        var clusters = UserDirectory.Cluster(_people);

        var split = _people.Count(p => UserDirectory.IsOutsider(p) == false
                                    && p.DisplayName.Trim().Length == 0);

        return new
        {
            groups = _inventory.Count,
            teams = _inventory.Count(g => g.IsTeam),
            people = _people.Count,
            unlicensed = clusters.Where(c => c.Unlicensed).Sum(c => c.Count),
            candidates = triaged.Count(t => t.Candidate),
            toCreate = plan.Count(p => p.Action == PlanAction.Create && p.Declared.Kind != GroupKind.Security),
            conflicts = plan.Count(p => p.Action == PlanAction.Conflict),
            security = plan.Count(p => p.Action == PlanAction.Create && p.Declared.Kind == GroupKind.Security),
            nameless = split,
            licenses = clusters.Select(c => new
            {
                count = c.Count,
                unlicensed = c.Unlicensed,
                sample = c.Sample(),
                departments = c.Departments,
            }),
        };
    }

    private object Groups() => new
    {
        rows = Sorted().Select(x => new
        {
            name = x.Item.Group.DisplayName,
            alias = x.Item.Group.MailNickname,
            groupId = x.Item.Group.GroupId,
            isTeam = x.Item.Group.IsTeam,
            members = x.Item.Group.MemberCount,
            created = x.Item.Group.Created,
            bucket = x.Item.Bucket == TriageBucket.System ? "건드리면 안 되는 것"
                   : x.Declared ? "이 학교에 있어야 하는 것"
                   : x.Candidate ? "정리 후보"
                   : "쓰이는 것",
            locked = x.Item.Bucket == TriageBucket.System,
            declared = x.Declared,
            candidate = x.Candidate,
            note = x.Item.Note,
            archiveName = ArchiveNameOf(x.Item.Group),
        }),
    };

    /// <summary>보관하면 붙일 이름. 만든 연도를 모르면 빈 문자열이고, 그러면 보관을 못 고른다.</summary>
    private static string ArchiveNameOf(ExistingGroup g)
    {
        var y = M365Flow.YearOf(g);
        return y.Length > 0 ? $"{y} {g.DisplayName}" : "";
    }

    private object PlanView()
    {
        var plan = TreeReconciler.Plan(Declared(), _inventory);

        return new
        {
            summary = TreeReconciler.Summarize(plan),
            fromRoster = _roster is not null,
            rows = plan.Select(p => new
            {
                name = p.Declared.DisplayName,
                alias = p.Declared.MailNickname,
                kind = p.Declared.Kind.ToString(),
                channels = p.Declared.Channels,
                action = p.Action switch
                {
                    PlanAction.Create => p.Declared.Kind == GroupKind.Security ? "손으로" : "만들 것",
                    PlanAction.Skip => "이미 있음",
                    _ => "확인 필요",
                },
                reason = p.Reason,
                existing = p.Existing?.DisplayName ?? "",
            }),
        };
    }

    private object People()
    {
        var clusters = UserDirectory.Cluster(_people);
        var faculty = UserDirectory.GuessFaculty(clusters)?.Bundle;

        return new
        {
            summary = UserDirectory.Summarize(clusters, _people),
            rows = _people.Select(p => new
            {
                upn = p.Upn,
                name = p.DisplayName,
                department = p.Department,
                licensed = !p.AccountType.Equals("IneligibleUser", StringComparison.OrdinalIgnoreCase),
                outsider = UserDirectory.IsOutsider(p),
                faculty = faculty is { Length: > 0 } && string.Equals(p.LicenseBundle, faculty, StringComparison.Ordinal),
            }),
        };
    }

    /// <summary>반별 현황. 구성원·소유자는 훑기(scan)를 한 뒤에야 채워진다.</summary>
    private object Classes()
    {
        var plan = TreeReconciler.Plan(Declared(), _inventory);
        var have = new Dictionary<string, IReadOnlyList<TeamMember>>(_members, StringComparer.OrdinalIgnoreCase);

        var assignments = _roster is null
            ? Array.Empty<ClassAssignment>()
            : MemberPlanner.Plan(_roster.Rows, plan, have).ToArray();

        return new
        {
            scanned = _scanned,
            hasRoster = _roster is not null,
            summary = _roster is null ? "명단을 올리시면 반별로 보여 드립니다." : MemberPlanner.Summarize(assignments),
            rows = assignments.Select(a => new
            {
                classKey = a.ClassKey,
                team = a.Team?.DisplayName ?? "",
                groupId = a.Team?.GroupId ?? "",
                toAdd = a.ToAdd.Count,
                already = a.Already,
                problem = a.Problem,
                owner = a.Team is null ? "" : OwnerNameOf(a.Team.GroupId),
                people = a.ToAdd.Select(r => new { number = r.Number, name = r.Name, upn = r.Upn }),
            }),
        };
    }

    private string OwnerNameOf(string groupId)
    {
        if (!_members.TryGetValue(groupId, out var members)) return "";

        var owners = members.Where(m => m.Role.Equals("Owner", StringComparison.OrdinalIgnoreCase)).ToList();
        if (owners.Count == 0) return "";

        var first = owners[0].Upn;
        var who = _people.FirstOrDefault(p => p.Upn.Equals(first, StringComparison.OrdinalIgnoreCase));
        var name = who?.DisplayName is { Length: > 0 } d ? d : first;

        return owners.Count > 1 ? $"{name} 외 {owners.Count - 1}명" : name;
    }

    /// <summary>선생님 찾기. 관리자는 아이디를 모르고 성함만 안다.</summary>
    private object Teachers(string query)
    {
        var faculty = UserDirectory.GuessFaculty(UserDirectory.Cluster(_people))?.Bundle;

        if (query.Trim().Length == 0)
            return new
            {
                rows = TeacherFinder.Faculty(_people, faculty)
                    .Select(p => new { upn = p.Upn, name = p.DisplayName, why = "" }),
                students = Array.Empty<object>(),
            };

        var found = TeacherFinder.Find(_people, query, faculty);

        return new
        {
            rows = found.Matches.Select(m => new { upn = m.User.Upn, name = m.User.DisplayName, why = m.Why }),

            // '없습니다' 라고 하면 안 된다 — 있는데 감춘 것과 아예 없는 것은 다르고,
            // 관리자는 그 차이를 알아야 다음 수를 안다.
            students = found.Students.Select(m => new { upn = m.User.Upn, name = m.User.DisplayName }),
        };
    }

    private async Task<HttpSay> MembersAsync(string groupId, CancellationToken ct)
    {
        if (groupId.Length == 0) return HttpSay.Text(400, "어느 팀인지 받지 못했습니다.");

        var res = await _host.CallAsync("Get-TeavelTeamMember",
            new Dictionary<string, object?> { ["GroupId"] = groupId },
            timeout: TimeSpan.FromMinutes(2), ct: ct).ConfigureAwait(false);

        if (!res.Ok) return Ok(new { ok = false, message = res.Message, details = res.Details });

        var members = M365Flow.ParseMembers(res.Details);
        _members[groupId] = members;

        var byUpn = _people.ToDictionary(p => p.Upn, p => p.DisplayName, StringComparer.OrdinalIgnoreCase);

        return Ok(new
        {
            ok = true,
            rows = members.Select(m => new
            {
                upn = m.Upn,
                role = m.Role,
                name = byUpn.TryGetValue(m.Upn, out var n) ? n : "",
            }),
        });
    }

    private object JobView(string id, string from)
    {
        var job = _jobs.Find(id);
        if (job is null) return new { ok = false, message = "그 일을 찾지 못했습니다." };

        var start = int.TryParse(from, out var n) ? n : 0;

        return new
        {
            ok = true,
            title = job.Title,
            done = job.Done,
            summary = job.Summary,
            next = job.Count,
            lines = job.Since(start),
        };
    }

    // ───────────────────────────── 바꾸는 것 ─────────────────────────────

    /// <summary>
    /// 이름 바꾸기는 곧바로 한다 — 한 번의 호출이고 몇 초면 끝난다.
    /// </summary>
    /// <remarks>
    /// 별칭은 건드리지 않는다. <b>별칭을 바꾸면 메일 주소가 바뀌어</b> 기존에 공유된 주소로
    /// 오던 메일이 끊긴다. 표시 이름만 바꾸는 것은 안전하고, 정리에 필요한 것은 그쪽뿐이다.
    /// </remarks>
    private async Task<HttpSay> RenameAsync(HttpAsk ask, CancellationToken ct)
    {
        var body = Body(ask);
        var alias = Str(body, "alias");
        var name = Str(body, "newName").Trim();

        var wrong = NameProblem(name);
        if (wrong is not null) return Ok(new { ok = false, message = wrong });

        var g = _inventory.FirstOrDefault(x => x.MailNickname.Equals(alias, StringComparison.OrdinalIgnoreCase));
        if (g is null) return Ok(new { ok = false, message = "그 그룹을 찾지 못했습니다. 새로 읽어 주세요." });

        var res = await _host.CallAsync("Rename-TeavelM365Group", new Dictionary<string, object?>
        {
            ["Identity"] = g.MailNickname,
            ["NewDisplayName"] = name,
        }, ct: ct).ConfigureAwait(false);

        if (!res.Ok) return Ok(new { ok = false, message = res.Message, details = res.Details });

        // 대조는 이름으로 하므로 바꾼 이름을 재고에 곧바로 반영해야
        // 아래에서 같은 이름을 또 만들지 않는다.
        var i = _inventory.IndexOf(g);
        if (i >= 0) _inventory[i] = g with { DisplayName = name };

        return Ok(new { ok = true, message = res.Message, details = res.Details });
    }

    /// <summary>
    /// 이름 같은지 본다.
    /// </summary>
    /// <remarks>
    /// 창에 파일을 끌어다 놓거나 다른 곳에서 복사한 것을 그대로 붙여넣는 일이 있는데,
    /// 그대로 두면 학교 그룹 이름이 파일 경로가 된다. 콘솔 쪽에서 실제로 그렇게 됐다.
    /// </remarks>
    private static string? NameProblem(string name)
        => name.Length == 0 ? "새 이름이 비어 있습니다."
         : name.Length > 60 ? "이름이 너무 깁니다(60자까지)."
         : name.IndexOfAny(new[] { '\\', '/' }) >= 0
             ? "이름에 \\ 나 / 는 쓸 수 없습니다. 파일 경로를 붙여넣으신 것 같습니다."
             : null;

    private object StartArchive(HttpAsk ask, CancellationToken ct)
    {
        var body = Body(ask);
        var alias = Str(body, "alias");
        var g = _inventory.FirstOrDefault(x => x.MailNickname.Equals(alias, StringComparison.OrdinalIgnoreCase));

        if (g is null) return new { ok = false, message = "그 그룹을 찾지 못했습니다." };

        var archived = ArchiveNameOf(g);
        if (archived.Length == 0) return new { ok = false, message = "만든 연도를 몰라 보관 이름을 정할 수 없습니다." };

        return Started(_jobs.Start($"'{g.DisplayName}' 보관", async (job, jct) =>
        {
            if (!await EnsureTeamsAsync(job, jct).ConfigureAwait(false)) { job.Finish("팀에 붙지 못했습니다."); return; }

            var res = await _host.CallAsync("Rename-TeavelM365Group", new Dictionary<string, object?>
            {
                ["Identity"] = g.MailNickname,
                ["NewDisplayName"] = archived,
            }, ct: jct).ConfigureAwait(false);

            if (!res.Ok) { job.Error(res.Message); job.Details(res.Details); job.Finish("이름을 바꾸지 못했습니다."); return; }

            job.Ok(res.Message);

            var i = _inventory.IndexOf(g);
            if (i >= 0) _inventory[i] = g with { DisplayName = archived };

            if (g.GroupId.Length == 0)
            {
                job.Warn("이름은 바꿨지만 학생을 내보내지 못했습니다(팀 id 를 모릅니다).");
                job.Finish("반만 됐습니다.");
                return;
            }

            var outed = await _host.CallAsync("Remove-TeavelTeamStudent", new Dictionary<string, object?>
            {
                ["GroupId"] = g.GroupId,
            }, timeout: TimeSpan.FromMinutes(10), ct: jct).ConfigureAwait(false);

            if (outed.Ok) { job.Ok(outed.Message); job.Details(outed.Details); }
            else { job.Error(outed.Message); job.Details(outed.Details); }

            job.Dim("팀과 그 안의 파일·대화는 그대로 있습니다. 담당 선생님은 계속 볼 수 있습니다.");
            job.Finish($"'{archived}' 로 보관했습니다.");
        }, ct));
    }

    /// <summary>
    /// 지우기.
    /// </summary>
    /// <remarks>
    /// <b>이름을 그대로 받아 적어야 실행한다.</b> 콘솔에서 쓰던 문을 그대로 옮겼다 —
    /// 단추 하나로 지워지면 잘못 눌러 파일과 대화가 함께 사라진다. 되살릴 방법은 없다.
    /// </remarks>
    private object StartDelete(HttpAsk ask, CancellationToken ct)
    {
        var body = Body(ask);
        var alias = Str(body, "alias");
        var typed = Str(body, "typed").Trim();

        var g = _inventory.FirstOrDefault(x => x.MailNickname.Equals(alias, StringComparison.OrdinalIgnoreCase));
        if (g is null) return new { ok = false, message = "그 그룹을 찾지 못했습니다." };

        if (!string.Equals(typed, g.DisplayName.Trim(), StringComparison.Ordinal))
            return new { ok = false, message = "적으신 이름이 다릅니다. 지우지 않았습니다." };

        if (InventoryTriage.IsSystemGroup(g))
            return new { ok = false, message = "이것은 테넌트가 만든 그룹이라 지울 수 없습니다." };

        return Started(_jobs.Start($"'{g.DisplayName}' 지우기", async (job, jct) =>
        {
            var res = await _host.CallAsync("Remove-TeavelM365Group", new Dictionary<string, object?>
            {
                ["Identity"] = g.MailNickname,
                ["Confirmed"] = true,
            }, ct: jct).ConfigureAwait(false);

            if (res.Ok) { job.Ok(res.Message); _inventory.Remove(g); job.Finish("지웠습니다."); }
            else { job.Error(res.Message); job.Details(res.Details); job.Finish("지우지 못했습니다."); }
        }, ct));
    }

    private object StartCreate(CancellationToken ct)
        => Started(_jobs.Start("모자란 것 만들기", async (job, jct) =>
        {
            var plan = TreeReconciler.Plan(Declared(), _inventory);

            var security = plan.Where(p => p.Action == PlanAction.Create && p.Declared.Kind == GroupKind.Security).ToList();
            var toCreate = plan.Where(p => p.Action == PlanAction.Create && p.Declared.Kind != GroupKind.Security).ToList();

            foreach (var s in security)
                job.Warn($"보안 그룹 '{s.Declared.DisplayName}' 은 관리 센터에서 손으로 만들어 주세요.");

            // 이미 있는 팀에도 선언한 채널이 다 있어야 한다. 만들 것이 없을 때도 반드시 돈다 —
            // 팀은 다 만들어졌는데 채널에서 끊긴 실행은 그때가 유일한 복구 지점이다.
            await SyncChannelsAsync(job, plan.Where(p => p.Action == PlanAction.Skip), jct).ConfigureAwait(false);

            if (toCreate.Count == 0) { job.Ok("만들 것이 없습니다. 선언한 대로 이미 다 있습니다."); job.Finish("만들 것이 없었습니다."); return; }

            if (toCreate.Any(p => p.Declared.Kind == GroupKind.Team)
                && !await EnsureTeamsAsync(job, jct).ConfigureAwait(false))
            {
                job.Finish("팀에 붙지 못해 멈췄습니다.");
                return;
            }

            var made = 0;
            var failed = 0;

            foreach (var p in toCreate)
            {
                var d = p.Declared;

                var res = await _host.CallAsync("New-TeavelM365Group", new Dictionary<string, object?>
                {
                    ["DisplayName"] = d.DisplayName,
                    ["MailNickname"] = d.MailNickname,
                    ["Description"] = d.Description,
                    ["Kind"] = d.Kind.ToString().ToLowerInvariant(),
                    ["Template"] = d.Template,
                    ["Visibility"] = d.Visibility,
                }, timeout: TimeSpan.FromMinutes(5), ct: jct).ConfigureAwait(false);

                if (!res.Ok) { job.Error($"{d.DisplayName} — {res.Message}"); job.Details(res.Details); failed++; continue; }

                job.Ok(res.Message);
                made++;

                var id = M365Flow.ExtractGroupId(res.Details);

                _inventory.Add(new ExistingGroup(
                    d.DisplayName, d.MailNickname, d.Kind == GroupKind.Team,
                    MemberCount: 0, Created: DateTime.Now.ToString("yyyy-MM-dd"), Origin: "teavel", GroupId: id));

                if (d.Channels.Count > 0 && id.Length > 0)
                    await SyncOneChannelSetAsync(job, id, d, jct).ConfigureAwait(false);
            }

            job.Dim("만든 팀은 담당 선생님이 [활성화] 를 눌러야 학생에게 보입니다.");
            job.Dim("새 채널도 접혀 있어 [...] → [표시] 를 눌러야 목록에 나옵니다.");
            job.Finish(failed == 0 ? $"{made}개를 만들었습니다." : $"{made}개를 만들고 {failed}개는 실패했습니다.");
        }, ct));

    private async Task SyncChannelsAsync(Job job, IEnumerable<PlanItem> existing, CancellationToken ct)
    {
        foreach (var p in existing)
        {
            if (p.Declared.Channels.Count == 0) continue;
            if (p.Existing is not { IsTeam: true, GroupId.Length: > 0 }) continue;

            await SyncOneChannelSetAsync(job, p.Existing.GroupId, p.Declared, ct).ConfigureAwait(false);
        }
    }

    private async Task SyncOneChannelSetAsync(Job job, string groupId, DeclaredGroup d, CancellationToken ct)
    {
        if (!await EnsureTeamsAsync(job, ct).ConfigureAwait(false)) return;

        var res = await _host.CallAsync("Sync-TeavelTeamChannel", new Dictionary<string, object?>
        {
            ["GroupId"] = groupId,
            ["Channels"] = d.Channels.ToList(),
        }, timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false);

        if (res.Ok) { if (res.Details.Count > 0 || res.Message.Length > 0) job.Dim($"{d.DisplayName}: {res.Message}"); }
        else job.Warn($"{d.DisplayName} 채널 — {res.Message}");
    }

    /// <summary>반별 구성원·소유자를 한 바퀴 읽는다. 팀 수만큼 호출이라 시간이 걸린다.</summary>
    private object StartScan(CancellationToken ct)
        => Started(_jobs.Start("반별 현황 읽기", async (job, jct) =>
        {
            var plan = TreeReconciler.Plan(Declared(), _inventory);
            var teams = plan.Where(p => p.Existing is { IsTeam: true, GroupId.Length: > 0 })
                            .Select(p => (p.Declared.DisplayName, p.Existing!.GroupId))
                            .Distinct().ToList();

            if (teams.Count == 0) { job.Info("읽을 팀이 없습니다."); job.Finish("팀이 없습니다."); return; }

            var n = 0;
            foreach (var (name, id) in teams)
            {
                jct.ThrowIfCancellationRequested();

                var res = await _host.CallAsync("Get-TeavelTeamMember",
                    new Dictionary<string, object?> { ["GroupId"] = id },
                    timeout: TimeSpan.FromMinutes(2), ct: jct).ConfigureAwait(false);

                _members[id] = res.Ok ? M365Flow.ParseMembers(res.Details) : Array.Empty<TeamMember>();
                n++;

                if (res.Ok) job.Dim($"{name} — {_members[id].Count}명");
                else job.Warn($"{name} — {res.Message}");
            }

            _scanned = true;
            job.Finish($"{n}개 팀을 읽었습니다.");
        }, ct));

    private object StartAddMembers(HttpAsk ask, CancellationToken ct)
    {
        var body = Body(ask);
        var groupId = Str(body, "groupId");
        var role = Str(body, "role") is { Length: > 0 } r ? r : "Member";
        var upns = Arr(body, "upns");
        var label = Str(body, "label");

        if (groupId.Length == 0 || upns.Count == 0)
            return new { ok = false, message = "누구를 어느 팀에 넣을지 받지 못했습니다." };

        return Started(_jobs.Start(label.Length > 0 ? label : "구성원 넣기", async (job, jct) =>
        {
            if (!await EnsureTeamsAsync(job, jct).ConfigureAwait(false)) { job.Finish("팀에 붙지 못했습니다."); return; }

            var res = await _host.CallAsync("Add-TeavelTeamMember", new Dictionary<string, object?>
            {
                ["GroupId"] = groupId,
                ["Users"] = upns,
                ["Role"] = role,
            }, timeout: TimeSpan.FromMinutes(10), ct: jct).ConfigureAwait(false);

            if (!res.Ok) { job.Error(res.Message); job.Details(res.Details); job.Finish("넣지 못했습니다."); return; }

            job.Ok(res.Message);
            foreach (var d in res.Details.Where(d => d.StartsWith("실패:", StringComparison.Ordinal))) job.Warn(d);

            // 다시 읽어 둔다. 화면이 방금 넣은 사람을 또 넣자고 하면 안 된다.
            var back = await _host.CallAsync("Get-TeavelTeamMember",
                new Dictionary<string, object?> { ["GroupId"] = groupId },
                timeout: TimeSpan.FromMinutes(2), ct: jct).ConfigureAwait(false);

            if (back.Ok) _members[groupId] = M365Flow.ParseMembers(back.Details);

            job.Dim("학생 화면에 보이기까지 몇 분 걸릴 수 있습니다.");
            job.Finish($"{upns.Count}명을 넣었습니다.");
        }, ct));
    }

    private object StartAssignOwners(HttpAsk ask, CancellationToken ct)
    {
        var body = Body(ask);
        var picks = new List<(string ClassKey, string GroupId, string Upn)>();

        if (body.TryGetProperty("picks", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var p in arr.EnumerateArray())
                picks.Add((Str(p, "classKey"), Str(p, "groupId"), Str(p, "upn")));

        picks = picks.Where(p => p.GroupId.Length > 0 && p.Upn.Length > 0).ToList();
        if (picks.Count == 0) return new { ok = false, message = "정하신 담임이 없습니다." };

        return Started(_jobs.Start($"담임 {picks.Count}명 지정", async (job, jct) =>
        {
            if (!await EnsureTeamsAsync(job, jct).ConfigureAwait(false)) { job.Finish("팀에 붙지 못했습니다."); return; }

            var done = 0;
            foreach (var p in picks)
            {
                var who = _people.FirstOrDefault(x => x.Upn.Equals(p.Upn, StringComparison.OrdinalIgnoreCase));

                var res = await _host.CallAsync("Add-TeavelTeamMember", new Dictionary<string, object?>
                {
                    ["GroupId"] = p.GroupId,
                    ["Users"] = new[] { p.Upn },
                    ["Role"] = "Owner",
                }, timeout: TimeSpan.FromMinutes(2), ct: jct).ConfigureAwait(false);

                if (res.Ok) { job.Ok($"{p.ClassKey} 담임 — {who?.DisplayName ?? p.Upn} ({p.Upn})"); done++; _members.Remove(p.GroupId); }
                else { job.Error($"{p.ClassKey} — {res.Message}"); job.Details(res.Details); }
            }

            job.Finish($"담임 {done}명을 지정했습니다.");
        }, ct));
    }

    /// <summary>표시 이름 붙이기. 성·이름이 나뉘어 있으면 Teams 에서 사람을 못 찾는다.</summary>
    private async Task<HttpSay> RenamePersonAsync(HttpAsk ask, CancellationToken ct)
    {
        var body = Body(ask);
        var upn = Str(body, "upn");
        var name = Str(body, "displayName").Trim();

        if (upn.Length == 0 || name.Length == 0) return Ok(new { ok = false, message = "누구를 무엇으로 바꿀지 받지 못했습니다." });

        var res = await _host.CallAsync("Set-TeavelDisplayName", new Dictionary<string, object?>
        {
            ["Identity"] = upn,
            ["DisplayName"] = name,
        }, ct: ct).ConfigureAwait(false);

        if (res.Ok)
            _people = _people.Select(p => p.Upn.Equals(upn, StringComparison.OrdinalIgnoreCase)
                ? p with { DisplayName = name } : p).ToList();

        return Ok(new { ok = res.Ok, message = res.Message, details = res.Details });
    }

    /// <summary>
    /// 명단 파일을 받는다. 몸통이 파일 그대로다.
    /// </summary>
    /// <remarks>
    /// multipart 를 다루지 않는다 — 화면이 <c>fetch(url, {body: file})</c> 로 날것 그대로 보낸다.
    /// 파일 이름은 머리글로 온다. 확장자를 봐야 어떻게 읽을지 정하기 때문이다.
    /// </remarks>
    private object TakeRoster(HttpAsk ask)
    {
        // 이름은 퍼센트 인코딩으로 온다. HTTP 머리글은 라틴 문자만 실을 수 있어서
        // '명단.xlsx' 를 그대로 붙이면 브라우저의 fetch 가 그 자리에서 거부한다.
        // 학교 명단 파일 이름은 거의 늘 한글이므로 이 길이 기본이다.
        var name = ask.H("x-teavel-filename");
        try { name = Uri.UnescapeDataString(name); } catch (UriFormatException) { /* 온 그대로 쓴다 */ }

        if (name.Length == 0) return new { ok = false, message = "파일 이름을 받지 못했습니다." };
        if (ask.Body.Length == 0) return new { ok = false, message = "파일이 비어 있습니다." };

        // 이름은 브라우저에서 온 것이다. 경로가 섞여 있으면 엉뚱한 곳에 쓰게 된다.
        name = Path.GetFileName(name);

        var ext = Path.GetExtension(name);
        if (!TableReader.CanReadDirectly("x" + ext))
            return new
            {
                ok = false,
                message = $"'{ext}' 파일은 아직 그대로 읽지 못합니다.",
                details = new[]
                {
                    "엑셀·한셀은 [다른 이름으로 저장] 에서 CSV 나 xlsx 로,",
                    "한글은 HWPX 로 한 번 저장해 주시면 읽습니다.",
                },
            };

        var temp = Path.Combine(Path.GetTempPath(), $"teavel-roster-{Guid.NewGuid():N}{ext}");

        try
        {
            File.WriteAllBytes(temp, ask.Body);

            var table = TableReader.Read(temp);
            var map = RosterMapper.Map(table.Rows);
            var guess = RosterExtractor.DetectIdFormat(table, map);
            var result = RosterExtractor.Extract(table, map, guess.Certain ? guess.Format : null);

            _roster = result;
            _rosterName = name;

            var shape = SchoolShape.Read(result.Rows);

            return new
            {
                ok = true,
                message = $"{name} — 명단 {result.Rows.Count}줄",
                how = RosterMapper.Explain(map),
                good = result.Rows.Count(r => r.Ok),
                bad = result.Bad.Select(b => new { line = b.Line, problems = b.Problems }).Take(20),

                // 명단에는 이 학교가 몇 학년 몇 반까지 있는지가 들어 있다.
                // 그것을 알아야 무엇을 만들지 정한다 — 관리자에게 따로 묻지 않는다.
                describe = shape.Describe(),
                shape = shape.Classes.Select(c => new
                {
                    grade = c.Grade,
                    classNo = c.ClassNo,
                    count = shape.HeadCount.TryGetValue(c, out var head) ? head : 0,
                }),
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = $"파일을 읽지 못했습니다: {ex.Message}" };
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 임시 파일이다 */ }
        }
    }

    private object StartRefresh(CancellationToken ct)
        => Started(_jobs.Start("다시 읽기", async (job, jct) =>
        {
            job.Info("학교의 그룹과 팀을 다시 읽습니다.");
            await ReadInventoryAsync(jct).ConfigureAwait(false);
            job.Ok($"그룹 {_inventory.Count}개 (팀 {_inventory.Count(g => g.IsTeam)}개)");

            job.Info("사람 목록을 다시 읽습니다.");
            await ReadPeopleAsync(jct).ConfigureAwait(false);
            job.Ok($"{_people.Count}명");

            _members.Clear();
            _scanned = false;

            job.Finish("다시 읽었습니다.");
        }, ct));

    /// <summary>
    /// 팀에 붙는다. 두 번째 로그인이라 정말 필요할 때까지 미룬다.
    /// </summary>
    /// <remarks>
    /// 로그인 창은 브라우저가 아니라 <b>따로 뜬다.</b> 관리 화면만 보고 있으면 그 창을
    /// 못 보고 지나칠 수 있으므로 진행에 먼저 적어 둔다.
    /// </remarks>
    private async Task<bool> EnsureTeamsAsync(Job job, CancellationToken ct)
    {
        if (_teamsReady) return true;

        job.Info("팀 작업을 위해 로그인이 한 번 더 필요합니다. 로그인 창이 따로 뜹니다.");

        var res = await _host.CallAsync("Connect-TeavelM365",
            new Dictionary<string, object?> { ["TeamsToo"] = true },
            timeout: TimeSpan.FromMinutes(20), ct: ct).ConfigureAwait(false);

        if (res.Ok) { _teamsReady = true; job.Ok(res.Message); return true; }

        job.Error(res.Message);
        job.Details(res.Details);
        return false;
    }

    // ───────────────────────────── 잔손 ─────────────────────────────

    private static object Started(Job job) => new { ok = true, jobId = job.Id, title = job.Title };

    private static JsonElement Body(HttpAsk ask)
    {
        if (ask.Body.Length == 0) return default;
        try { return JsonDocument.Parse(ask.Body).RootElement.Clone(); }
        catch { return default; }
    }

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static List<string> Arr(JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s) list.Add(s);
        return list;
    }
}

/// <summary>
/// 화면 파일(html·css·js)을 exe 안에서 꺼내 준다.
/// </summary>
/// <remarks>
/// 포털은 publish 결과에서 <c>.exe</c> 하나만 집어 배포한다. 옆에 둔 폴더는 버려지므로
/// 스크립트·카탈로그와 같은 방식으로 <b>묻어서</b> 간다.
/// </remarks>
internal static class Assets
{
    private const string Prefix = "Teavel.Web.";

    public static HttpSay Serve(string path)
    {
        var name = path is "/" or "" ? "index.html" : path.TrimStart('/');
        if (name.Contains("..", StringComparison.Ordinal)) return HttpSay.NotFound;

        var stream = typeof(Assets).Assembly.GetManifestResourceStream(Prefix + name.Replace('/', '.'));
        if (stream is null) return HttpSay.NotFound;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        stream.Dispose();

        return HttpSay.Asset(TypeOf(name), memory.ToArray());
    }

    private static string TypeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
}
