using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>학교 구조 선언. 화면에서 손보면 다시 읽으므로 붙박이가 아니다.</summary>
    private SchoolTree _tree;
    private readonly JobBoard _jobs = new();
    private readonly string _token;

    private List<ExistingGroup> _inventory = new();
    private IReadOnlyList<TenantUser> _people = Array.Empty<TenantUser>();
    private RosterResult? _roster;
    private string _rosterName = "";
    private readonly Dictionary<string, IReadOnlyList<TeamMember>> _members = new(StringComparer.OrdinalIgnoreCase);
    private bool _teamsReady;
    private bool _graphReady;
    private bool _scanned;

    /// <summary>사람 목록을 실제로 읽었는지. 처음에는 아니다 — 팀 로그인 전이기 때문이다.</summary>
    private bool _peopleRead;
    private string _peopleProblem = "";

    /// <summary>화면에서 [끝내기] 를 눌렀는지. 콘솔이 이것을 보고 판을 접는다.</summary>
    public bool Finished { get; private set; }

    public AdminApi(M365Host host, SchoolTree tree, string token, Func<CancellationToken, Task<M365Host>>? graphHost = null)
    {
        _host = host;
        _tree = tree;
        _token = token;
        _newGraphHost = graphHost;
    }

    /// <summary>
    /// Graph 는 <b>다른 프로세스에서</b> 산다.
    /// </summary>
    /// <remarks>
    /// 한 프로세스에 둘을 같이 두면 붙지 않는다. Exchange 3.10.1 은 Azure.Core 1.50 을,
    /// Graph 2.39 는 1.51 을 들고 오는데, 먼저 들어온 쪽이 이긴다. Exchange 가 먼저 붙는
    /// 우리 세션에서는 Graph 를 부르는 순간 이렇게 끝났다(2026-08-28).
    ///
    ///     'UserProvidedTokenCredential' 형식의 'GetTokenAsync' 메서드에 구현이 없습니다.
    ///
    /// 순서를 뒤집어 Graph 를 먼저 부르면 붙기는 한다. 그러나 그러면 Exchange 가 자기가
    /// 들고 온 적 없는 Azure.Core 위에서 돌게 된다 — <b>되는 길을 걸고 하는 도박</b>이다.
    /// 세션을 가르면 둘 다 자기 것 위에서 돈다.
    ///
    /// 로그인이 늘지는 않는다. Graph 는 어차피 동의 화면이 따로 뜨는 별개의 로그인이고,
    /// 이 세션은 한 번 붙으면 화면을 닫을 때까지 그대로 산다.
    /// </remarks>
    private readonly Func<CancellationToken, Task<M365Host>>? _newGraphHost;
    private M365Host? _graph;
    private readonly SemaphoreSlim _graphGate = new(1, 1);

    private async Task<M365Host> GraphAsync(CancellationToken ct)
    {
        if (_graph is { IsAlive: true }) return _graph;

        await _graphGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_graph is { IsAlive: true }) return _graph;

            // 띄우지 못하는 판이면 하던 대로 한 세션에서 해 본다.
            // 안 될 수도 있지만, 못 한다고 손 놓는 것보다는 낫다.
            if (_newGraphHost is null) return _host;

            _graph = await _newGraphHost(ct).ConfigureAwait(false);
            return _graph;
        }
        finally { _graphGate.Release(); }
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

        // 코드를 적어 넣는 로그인이면, 그 자리를 사람이 덜 하게 만든다 —
        // 페이지를 대신 열어 주고 코드는 따로 크게 보여 준다.
        if (DeviceLogin.TryRead(line, out var url, out var code) && code != _lastCode)
        {
            _lastCode = code;
            _jobs.Current?.Say("code", url + "\t" + code);
            DeviceLogin.Open(url);
            return;
        }

        _jobs.Current?.Dim(line.Trim());
    }

    /// <summary>같은 코드를 두 번 보여 주거나 창을 두 번 열지 않게.</summary>
    private string _lastCode = "";

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
            "/api/plan/create" => Ok(StartCreate(Str(Body(ask), "kind"), ct)),
            "/api/classes/scan" => Ok(StartScan(ct)),
            "/api/members/add" => Ok(StartAddMembers(ask, ct)),
            "/api/members/assign" => Ok(StartAssign(ask, ct)),
            "/api/teams/new" => Ok(StartNewTeam(ask, ct)),
            "/api/owners/assign" => Ok(StartAssignOwners(ask, ct)),
            "/api/people/rename" => await RenamePersonAsync(ask, ct).ConfigureAwait(false),
            "/api/people/block" => Ok(StartBlock(ask, ct)),
            "/api/people/delete" => Ok(StartRemovePeople(ask, ct)),
            "/api/people/read" => Ok(StartReadPeople(ct)),
            "/api/roster" => Ok(TakeRoster(ask)),
            "/api/tree/drop" => Ok(DropDeclared(ask)),
            "/api/tree/reset" => Ok(ResetDeclared()),
            "/api/password/ready" => await GraphReadyAsync(ct).ConfigureAwait(false),
            "/api/password/install" => Ok(StartGraphInstall(ct)),
            "/api/password/reset" => Ok(StartReset(ask, ct)),
            "/api/password/result" => Ok(ResetResult(ask.Q("id"))),
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

    /// <summary>
    /// 학교 사람 목록.
    /// </summary>
    /// <remarks>
    /// <b>이것은 Teams 쪽에서 온다</b>(<c>Get-CsOnlineUser</c>). 그런데 화면은 시작할 때
    /// 메일·그룹(Exchange)만 붙는다 — 로그인 창이 연달아 두 번 뜨는 것을 피하려는 것이다.
    /// 그래서 처음에는 <b>읽히지 않는 것이 정상</b>이고, 관리자가 구성원을 볼 때 그때 붙는다.
    ///
    /// 실기에서 이것 때문에 구성원 화면이 <b>아무 말 없이 텅 비어</b> 있었다(2026-08-27).
    /// 가짜 테넌트는 연결을 따지지 않아 드러나지 않았다. 그래서 왜 비었는지를 들고 있는다.
    /// </remarks>
    private async Task ReadPeopleAsync(CancellationToken ct)
    {
        var res = await _host.CallAsync("Get-TeavelTenantUser",
            timeout: TimeSpan.FromMinutes(10), ct: ct).ConfigureAwait(false);

        if (res.Ok)
        {
            var read = UserDirectory.Parse(res.Details);

            // 지운 사람이 아직 딸려 온다.
            //
            // 계정은 Graph 로 지우지만 이 목록은 Exchange 에서 온다. 지운 것이 그쪽까지
            // 퍼지는 데 시간이 걸려서, 지운 직후 다시 읽어도 그대로 나온다. 실기에서
            // '지우는 것까지는 됐는데 이름이 남았다' 가 그것이다(2026-08-28).
            //
            // 우리는 지운 것을 안다. 화면이 그걸 따라가야 한다 — 남아 있으면 관리자는
            // 안 지워진 줄 알고 한 번 더 누르시게 된다.
            _people = _deleted.Count == 0
                ? read
                : read.Where(p => !_deleted.Contains(p.Upn)).ToList();

            _peopleRead = true;
            _peopleProblem = "";
            return;
        }

        _peopleRead = false;
        _peopleProblem = res.Message;
    }

    /// <remarks>
    /// 이 판에서 지운 사람들. 화면을 닫으면 잊는다 — 다시 켜면 그때의 진짜 목록을 본다.
    /// 되살리셨을 때 계속 감추고 있지 않으려면 그 편이 맞다.
    /// </remarks>
    private readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);

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
        school = _tree.School,
        own = SchoolChoice.Own(_tree),
        roster = _rosterName,
        rosterRows = _roster?.Rows.Count(r => r.Ok) ?? 0,
        teamsReady = _teamsReady,
        graphReady = _graphReady,
        busy = _jobs.Busy,
    };

    /// <summary>
    /// 선언 하나를 이 학교에서 뺀다.
    /// </summary>
    /// <remarks>
    /// <b>테넌트는 건드리지 않는다.</b> '이 학교에는 이것이 없어도 된다' 고 정하는 것뿐이고,
    /// 이미 만들어져 있는 그룹은 그대로 있다. 그래서 되돌리기도 쉽다.
    /// </remarks>
    private object DropDeclared(HttpAsk ask)
    {
        var body = Body(ask);
        var id = Str(body, "id");
        var name = Str(body, "name");

        if (id.Length == 0 && name.Length == 0)
            return new { ok = false, message = "무엇을 뺄지 받지 못했습니다." };

        if (!SchoolChoice.Drop(AppContext.BaseDirectory, id, name))
            return new { ok = false, message = "그 선언을 찾지 못했습니다." };

        _tree = SchoolTree.Load(AppContext.BaseDirectory);
        return new { ok = true, message = $"'{(name.Length > 0 ? name : id)}' 을(를) 이 학교 목록에서 뺐습니다." };
    }

    /// <summary>학교가 정한 것을 버리고 처음 상태로 돌아간다.</summary>
    private object ResetDeclared()
    {
        var had = SchoolChoice.Reset();
        _tree = SchoolTree.Load(AppContext.BaseDirectory);

        return new
        {
            ok = true,
            message = had ? "처음 상태로 되돌렸습니다." : "이미 처음 상태입니다.",
        };
    }

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

        var make = plan.Where(p => p.Action == PlanAction.Create).ToList();

        return new
        {
            groups = _inventory.Count,
            teams = _inventory.Count(g => g.IsTeam),
            people = _people.Count,
            peopleRead = _peopleRead,
            unlicensed = clusters.Where(c => c.Unlicensed).Sum(c => c.Count),

            candidates = triaged.Count(t => t.Candidate),

            // 낱장이 팀과 그룹으로 갈렸으므로 왼쪽 메뉴의 숫자도 갈라 준다.
            // 한 숫자를 두 곳에 걸어 두면 어느 쪽을 눌러야 하는지 알 수 없다.
            candidateTeams = triaged.Count(t => t.Candidate && t.Item.Group.IsTeam),
            candidateGroups = triaged.Count(t => t.Candidate && !t.Item.Group.IsTeam),

            toCreate = make.Count(p => p.Declared.Kind != GroupKind.Security),
            toCreateTeams = make.Count(p => p.Declared.Kind == GroupKind.Team),
            toCreateGroups = make.Count(p => p.Declared.Kind == GroupKind.M365),

            conflicts = plan.Count(p => p.Action == PlanAction.Conflict),
            security = make.Count(p => p.Declared.Kind == GroupKind.Security),
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

            // 읽어 둔 것이 있으면 그것을 쓴다.
            //
            // Group.MemberCount 는 Exchange 가 세어 둔 값이라 <b>한참 뒤처진다.</b> 예순 명을
            // 넣고 나서도 한동안 옛 수를 말한다. 실기에서 팀에 150명이 들어 있는데 목록에는
            // 80명으로 떠 있었다(2026-08-28). 그 숫자를 보고 '안 들어갔구나' 하시게 된다.
            //
            // 우리가 방금 링크로 읽은 것이 있으면 그쪽이 지금의 사실이다.
            members = _members.TryGetValue(x.Item.Group.GroupId, out var read)
                ? read.Count
                : x.Item.Group.MemberCount,

            // 읽어 본 것인지 아닌지. 아직 안 읽었으면 화면이 그렇게 밝힌다.
            counted = _members.ContainsKey(x.Item.Group.GroupId),
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
            own = SchoolChoice.Own(_tree),
            rows = plan.Select(p => new
            {
                // 선언의 id — 화면에서 '이 학교엔 필요 없습니다' 를 누를 때 이것으로 되짚는다.
                id = p.Declared.Id,
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

    /// <summary>
    /// 학교 사람 명부.
    /// </summary>
    /// <remarks>
    /// <b>교사인지 학생인지 Teavel 이 단정하지 않는다.</b> 라이선스 묶음과 아이디 생김새를
    /// 나란히 보여 주면 관리자가 한눈에 가른다 — 학생 아이디는 학번 꼴이고 교사는 아니다.
    /// SKU 이름을 알아내려 들지 않는 것이 이 방식의 요점이라, 묶음에는 크기로 이름을 붙인다.
    /// </remarks>
    /// <summary>표시 이름이 <c>10101홍길동</c> 꼴인지. 학생 계정의 아주 또렷한 표시다.</summary>
    private static readonly Regex StudentName = new(@"^(\d{3,7})\s*[가-힣]{2,5}$", RegexOptions.Compiled);

    private object People()
    {
        var clusters = UserDirectory.Cluster(_people);
        var faculty = UserDirectory.GuessFaculty(clusters)?.Bundle;

        // 교사 묶음을 뺀 나머지 중 가장 큰 것이 대개 학생이다.
        var students = clusters
            .Where(c => !c.Unlicensed)
            .Where(c => !string.Equals(c.Bundle, faculty, StringComparison.Ordinal))
            .OrderByDescending(c => c.Count)
            .FirstOrDefault()?.Bundle;

        var size = clusters.ToDictionary(c => c.Bundle, c => c.Count, StringComparer.Ordinal);
        var mine = Membership();

        // 학년·반은 명단이 가장 확실하다. 명단이 없거나 그 사람이 명단에 없으면
        // 표시 이름 앞의 학번을 가른다.
        var byRoster = new Dictionary<string, (string Grade, string ClassNo)>(StringComparer.OrdinalIgnoreCase);
        if (_roster is not null)
            foreach (var r in _roster.Rows.Where(r => r.Ok && r.Upn.Length > 0))
                byRoster[r.Upn] = (r.Grade, r.ClassNo);

        var format = GuessIdFormat();

        return new
        {
            summary = UserDirectory.Summarize(clusters, _people),
            scanned = _scanned,
            read = _peopleRead,
            problem = _peopleProblem,
            hasRoster = _roster is not null,
            rows = _people.Select(p =>
            {
                // 라이선스는 Teams 에만 있다. 팀에 안 붙어 있으면 알 길이 없는데,
                // 그때 '없음' 이라고 하면 멀쩡한 계정이 죄다 라이선스 없는 것처럼 보이고
                // 넣기·비밀번호 대상에서 통째로 빠진다. 모르면 모른다고 한다.
                var unknown = p.AccountType.Length == 0 && p.LicenseBundle.Length == 0;

                var licensed = unknown
                    || (!p.AccountType.Equals("IneligibleUser", StringComparison.OrdinalIgnoreCase)
                        && p.LicenseBundle.Length > 0);

                var digits = StudentName.Match(p.DisplayName.Trim());
                var outsider = UserDirectory.IsOutsider(p);

                // 갈래를 정하는 순서가 있다. 라이선스 묶음이 가장 믿을 만하지만,
                // 학번+이름 표시는 그 자체로 또렷해서 묶음을 못 알아봐도 학생을 가른다.
                var role = outsider ? "학교 밖"
                    : !licensed ? "라이선스 없음"
                    : unknown ? (digits.Success ? "학생" : "그 밖")
                    : faculty is { Length: > 0 } && string.Equals(p.LicenseBundle, faculty, StringComparison.Ordinal) ? "교사"
                    : digits.Success ? "학생"
                    : students is { Length: > 0 } && string.Equals(p.LicenseBundle, students, StringComparison.Ordinal) ? "학생"
                    : "그 밖";

                var grade = "";
                var classNo = "";

                if (byRoster.TryGetValue(p.Upn, out var got)) (grade, classNo) = got;
                else if (digits.Success && format is not null
                      && format.TryDecompose(digits.Groups[1].Value, out var g, out var c, out _))
                { grade = g; classNo = c; }

                return new
                {
                    upn = p.Upn,
                    name = p.DisplayName,
                    department = p.Department,
                    licensed,
                    outsider,
                    role,
                    grade,
                    classNo,
                    license = unknown ? "모름"
                            : role is "교사" or "학생" ? role
                            : role == "라이선스 없음" ? "없음" : "그 밖",
                    licenseCount = size.TryGetValue(p.LicenseBundle, out var n) ? n : 0,
                    created = p.Created,
                    blocked = p.Blocked,
                    groups = mine.TryGetValue(p.Upn, out var gs) ? gs : new List<string>(),
                };
            }),
        };
    }

    // ───────────────────────────── 비밀번호 ─────────────────────────────
    //
    // 이것 하나만 Microsoft Graph 를 쓴다. Exchange 에도 Teams 에도 비밀번호 cmdlet 이
    // 없고(실측), MSOnline·AzureAD 는 2025-05-30 에 은퇴했다. 남은 길이 이것뿐이다.
    //
    // 그래서 <b>관리자가 실제로 비밀번호를 바꾸려 할 때</b> 그때 연결한다.
    // 읽기만 하다 끝내는 관리자는 동의 화면을 아예 보지 않는다.

    /// <summary>바꾼 비밀번호. <b>메모리에만, 한 판 동안만</b> 둔다.</summary>
    /// <remarks>
    /// 디스크에 적지 않는다. 화면이 한 번 받아 가 종이로 옮기면 그것으로 끝이고,
    /// 프로그램이 꺼지면 사라진다. 다시 알아낼 방법은 없으며 그게 맞다 —
    /// 남아 있으면 언젠가 새 나간다.
    /// </remarks>
    private readonly Dictionary<string, List<object>> _slips = new(StringComparer.Ordinal);

    private async Task<HttpSay> GraphReadyAsync(CancellationToken ct)
    {
        var g = await GraphAsync(ct).ConfigureAwait(false);
        var res = await g.CallAsync("Get-TeavelGraphReadiness", ct: ct).ConfigureAwait(false);
        return Ok(new { ok = res.Ok, ready = res.Ok && !res.Message.Contains("갖춰야", StringComparison.Ordinal),
                        message = res.Message, details = res.Details });
    }

    private object StartGraphInstall(CancellationToken ct)
        => Started(_jobs.Start("비밀번호 기능 갖추기", async (job, jct) =>
        {
            job.Info("모듈을 내려받습니다. 몇 분 걸릴 수 있습니다.");

            var g = await GraphAsync(jct).ConfigureAwait(false);
            var res = await g.CallAsync("Install-TeavelGraphModule",
                timeout: TimeSpan.FromMinutes(15), ct: jct).ConfigureAwait(false);

            if (res.Ok) { job.Ok(res.Message); job.Details(res.Details); job.Finish("갖췄습니다."); }
            else { job.Error(res.Message); job.Details(res.Details); job.Finish("갖추지 못했습니다."); }
        }, ct));

    /// <summary>
    /// 임시 비밀번호로 바꾼다.
    /// </summary>
    /// <remarks>
    /// <b>한 사람이 막혀도 나머지는 마저 한다.</b> 자기 자신이나 상급 관리자의 비밀번호는
    /// 못 바꾸는데, 반 서른 명 중 하나가 그런 계정이라고 스물아홉을 못 하면 안 된다.
    /// </remarks>
    private object StartReset(HttpAsk ask, CancellationToken ct)
    {
        var body = Body(ask);
        var upns = Arr(body, "upns");
        var mustChange = !body.TryGetProperty("mustChange", out var mc) || mc.ValueKind != JsonValueKind.False;
        var label = Str(body, "label");

        if (upns.Count == 0) return new { ok = false, message = "누구의 비밀번호를 바꿀지 받지 못했습니다." };

        // 비밀번호는 여기서 만든다. 판단은 C# 에 두고 PowerShell 은 받은 값을 넣기만 한다.
        var made = PasswordMaker.Many(upns.Count);
        var byUpn = _people.ToDictionary(p => p.Upn, p => p.DisplayName, StringComparer.OrdinalIgnoreCase);

        var job = _jobs.Start(label.Length > 0 ? label : $"비밀번호 {upns.Count}명", async (j, jct) =>
        {
            if (!await EnsureGraphAsync(j, jct).ConfigureAwait(false)) { j.Finish("연결하지 못했습니다."); return; }

            var slips = new List<object>();
            var failed = 0;

            for (var i = 0; i < upns.Count; i++)
            {
                jct.ThrowIfCancellationRequested();

                var upn = upns[i];
                var pw = made[i];
                var name = byUpn.TryGetValue(upn, out var n) ? n : "";

                var res = await (await GraphAsync(jct).ConfigureAwait(false)).CallAsync("Reset-TeavelPassword", new Dictionary<string, object?>
                {
                    ["Identity"] = upn,
                    ["Password"] = pw,
                    ["MustChange"] = mustChange,
                }, timeout: TimeSpan.FromMinutes(2), ct: jct).ConfigureAwait(false);

                if (res.Ok)
                {
                    // 진행에는 비밀번호를 적지 않는다. 진행 줄은 콘솔에도 흐르고
                    // 관리자가 화면을 남에게 보여 줄 수도 있다.
                    j.Ok($"{(name.Length > 0 ? name : upn)} — 바꿨습니다.");
                    slips.Add(new { upn, name, password = pw });
                }
                else
                {
                    j.Warn($"{(name.Length > 0 ? name : upn)} — {res.Message}");
                    failed++;
                }
            }

            _slips[j.Id] = slips;

            if (slips.Count > 0)
            {
                j.Dim("바뀐 비밀번호는 이 화면에서 한 번만 받아 가실 수 있습니다.");
                j.Dim("내려받아 종이로 옮기신 뒤에는 그 파일을 지워 주세요.");
            }

            j.Finish(failed == 0
                ? $"{slips.Count}명의 비밀번호를 바꿨습니다."
                : $"{slips.Count}명은 바꾸고 {failed}명은 못 바꿨습니다.");
        }, ct);

        return new { ok = true, jobId = job.Id, title = job.Title };
    }

    /// <summary>
    /// 바뀐 비밀번호를 <b>한 번만</b> 내준다.
    /// </summary>
    /// <remarks>
    /// 내주고 곧바로 지운다. 화면이 종이로 옮길 파일을 만들 수 있으면 그것으로 할 일은
    /// 끝났고, 그 뒤로도 들고 있으면 언젠가 새 나간다.
    /// </remarks>
    private object ResetResult(string jobId)
    {
        if (!_slips.Remove(jobId, out var slips))
            return new { ok = false, message = "받아 갈 것이 없습니다. 이미 한 번 받아 가셨거나, 바뀐 것이 없습니다." };

        return new { ok = true, rows = slips };
    }

    /// <summary>
    /// Graph 에 붙는다. 비밀번호를 정말 바꾸려 할 때만 부른다.
    /// </summary>
    /// <remarks>
    /// 여기서 처음 보는 <b>동의 화면</b>이 뜬다. 그 화면에서 겁을 먹고 [취소] 를 누르면
    /// 이 기능이 통째로 막히므로, 무엇에 동의하는지는 PowerShell 쪽이 미리 적어 흘려보낸다.
    /// </remarks>
    private async Task<bool> EnsureGraphAsync(Job job, CancellationToken ct)
        => await EnsureGraphAsync(job, PasswordScope, ct).ConfigureAwait(false);

    /// <summary>비밀번호를 바꿀 때 받는 권한. 그것 말고는 아무것도 못 한다.</summary>
    private const string PasswordScope = "User-PasswordProfile.ReadWrite.All";

    /// <summary>계정을 지울 때 받는 권한. 위의 것으로는 못 지운다.</summary>
    private const string DeleteScope = "User.ReadWrite.All";

    /// <remarks>
    /// 권한마다 따로 센다. 비밀번호로 한 번 붙었다고 해서 지울 수 있는 것이 아니고,
    /// 그때 붙었으니 됐다고 넘겨 버리면 삭제가 <b>권한 없음</b>으로 조용히 실패한다.
    /// 지우는 쪽은 동의 화면이 한 번 더 뜨는 것이 맞다.
    /// </remarks>
    private async Task<bool> EnsureGraphAsync(Job job, string scope, CancellationToken ct)
    {
        if (_graphScopes.Contains(scope)) return true;

        var g = await GraphAsync(ct).ConfigureAwait(false);
        var res = await g.CallAsync("Connect-TeavelGraph",
            new Dictionary<string, object?> { ["Scopes"] = new[] { scope } },
            timeout: TimeSpan.FromMinutes(20), ct: ct).ConfigureAwait(false);

        if (res.Ok)
        {
            _graphScopes.Add(scope);
            _graphReady = true;
            job.Ok(res.Message);
            job.Details(res.Details);
            return true;
        }

        job.Error(res.Message);
        job.Details(res.Details);
        return false;
    }

    private readonly HashSet<string> _graphScopes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 학번을 학년·반·번호로 가르는 형식.
    /// </summary>
    /// <remarks>
    /// 학교마다 다르다 — <c>10301</c> 인 곳도 있고 <c>1301</c> 인 곳도 있다. 짐작하면 반드시 틀리므로
    /// 자료에서 알아낸다. <b>명단이 있으면 그쪽이 가장 확실하다</b> — 학번과 학년·반·번호가
    /// 나란히 있어 맞춰 볼 수 있다. 없으면 표시 이름 앞의 숫자만으로 후보를 좁힌다.
    /// </remarks>
    private StudentIdFormat? GuessIdFormat()
    {
        if (_roster is not null)
        {
            var full = _roster.Rows
                .Where(r => r.Ok)
                .Select(r => (r.StudentId, r.Grade, r.ClassNo, r.Number));

            var guess = StudentIdFormats.Detect(full);
            if (guess.Format is not null) return guess.Format;
        }

        var sids = _people
            .Select(p => StudentName.Match(p.DisplayName.Trim()))
            .Where(m => m.Success)
            .Select(m => (m.Groups[1].Value, "", "", ""))
            .Take(300)
            .ToList();

        return sids.Count == 0 ? null : StudentIdFormats.Detect(sids).Format;
    }

    /// <summary>
    /// 아이디 → 그 사람이 속한 팀 이름들.
    /// </summary>
    /// <remarks>
    /// 읽어 둔 것에서 뒤집어 만든다. <b>사람마다 물어볼 길이 없기 때문이다</b> —
    /// 상주 세션이 내주는 것은 '팀 하나의 구성원' 뿐이라, 팀을 다 훑어야 사람 쪽이 채워진다.
    /// 훑기 전에는 비어 있고, 화면이 그것을 '아직 안 읽음' 으로 말한다.
    /// </remarks>
    private Dictionary<string, List<string>> Membership()
    {
        var byId = _inventory
            .Where(g => g.GroupId.Length > 0)
            .GroupBy(g => g.GroupId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DisplayName, StringComparer.OrdinalIgnoreCase);

        var mine = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (groupId, members) in _members)
        {
            if (!byId.TryGetValue(groupId, out var name)) continue;

            foreach (var m in members)
            {
                if (!mine.TryGetValue(m.Upn, out var list)) mine[m.Upn] = list = new List<string>();

                // 소유자는 따로 표시한다 — 담임인지 아닌지가 여기서 드러난다.
                list.Add(m.Role.Equals("Owner", StringComparison.OrdinalIgnoreCase) ? name + " (소유자)" : name);
            }
        }

        foreach (var list in mine.Values) list.Sort(StringComparer.CurrentCulture);
        return mine;
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

    /// <summary>
    /// 없는 것을 만든다.
    /// </summary>
    /// <param name="kind">
    /// <c>team</c> 이면 팀만, <c>group</c> 이면 팀이 아닌 것만. 비어 있으면 둘 다.
    /// 낱장이 팀과 그룹으로 갈려 있어, 누른 자리의 것만 만들어야 관리자가 짐작하지 않는다.
    /// </param>
    private object StartCreate(string kind, CancellationToken ct)
        => Started(_jobs.Start(kind switch
        {
            "team" => "반 팀 만들기",
            "group" => "그룹 만들기",
            _ => "모자란 것 만들기",
        }, async (job, jct) =>
        {
            var plan = TreeReconciler.Plan(Declared(), _inventory);

            var security = plan.Where(p => p.Action == PlanAction.Create && p.Declared.Kind == GroupKind.Security).ToList();

            var toCreate = plan
                .Where(p => p.Action == PlanAction.Create && p.Declared.Kind != GroupKind.Security)
                .Where(p => kind switch
                {
                    "team" => p.Declared.Kind == GroupKind.Team,
                    "group" => p.Declared.Kind != GroupKind.Team,
                    _ => true,
                })
                .ToList();

            // 보안 그룹 안내는 그룹 쪽에서만 낸다. 팀을 만드는데 그 이야기가 끼면 산만해진다.
            if (kind != "team")
                foreach (var s in security)
                    job.Warn($"보안 그룹 '{s.Declared.DisplayName}' 은 관리 센터에서 손으로 만들어 주세요.");

            // 이미 있는 팀에도 선언한 채널이 다 있어야 한다. 만들 것이 없을 때도 반드시 돈다 —
            // 팀은 다 만들어졌는데 채널에서 끊긴 실행은 그때가 유일한 복구 지점이다.
            //
            // 다만 그룹만 만들 때는 건너뛴다. 채널 맞추기는 팀 로그인을 부르는데,
            // 그룹 하나 만들자고 로그인 창을 띄우면 관리자는 무슨 일인지 알 수 없다.
            if (kind != "group")
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

    /// <summary>
    /// 누가 어느 팀에 있는지 한 바퀴 읽는다. 팀 수만큼 호출이라 시간이 걸린다.
    /// </summary>
    /// <remarks>
    /// <b>선언에 있는 반 팀만이 아니라 테넌트의 팀 전부를 읽는다.</b> 사람 명부의
    /// '속해 있는 그룹' 칸이 반 팀만 보여 주면, 교사가 다른 팀에 들어 있는 것이 안 보여
    /// 아무 데도 안 속한 사람처럼 나온다.
    /// </remarks>
    private object StartScan(CancellationToken ct)
        => Started(_jobs.Start("누가 어느 팀에 있는지 읽기", async (job, jct) =>
        {
            var teams = _inventory
                .Where(g => g.IsTeam && g.GroupId.Length > 0)
                .Select(g => (g.DisplayName, g.GroupId))
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

                if (res.Ok)
                {
                    job.Dim($"{name} — {_members[id].Count}명");

                    // 읽은 수가 학교 명부와 다르면 그 자리에서 말한다.
                    // 조용히 적은 숫자를 보여 주면 '안 들어갔구나' 하고 또 넣으시게 된다.
                    foreach (var note in Notes(res.Details)) job.Warn($"{name} — {note}");
                    continue;
                }

                job.Warn($"{name} — {res.Message}");
            }

            _scanned = true;
            job.Finish($"{n}개 팀을 읽었습니다.");
        }, ct));

    /// <summary>사람 목록에 섞여 오는 <c>NOTE</c> 줄만 골라낸다 — 관리자에게 그대로 보여 줄 말이다.</summary>
    private static IEnumerable<string> Notes(IEnumerable<string> details)
        => details
            .Where(d => d.StartsWith("NOTE\t", StringComparison.Ordinal))
            .Select(d => d[5..].Trim());

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

            if (back.Ok)
            {
                _members[groupId] = M365Flow.ParseMembers(back.Details);

                // 넣은 뒤 다시 읽었는데 수가 학교 명부와 다르면 그대로 말한다.
                foreach (var note in Notes(back.Details)) job.Warn(note);
            }

            job.Dim("학생 화면에 보이기까지 몇 분 걸릴 수 있습니다.");
            job.Finish($"{upns.Count}명을 넣었습니다.");
        }, ct));
    }

    /// <summary>
    /// 선언에 없는 팀을 하나 만든다.
    /// </summary>
    /// <remarks>
    /// 학교가 하는 일이 선언에 다 적혀 있지는 않다. <b>'1학년 과학' 처럼 그때그때 생기는 팀</b>이
    /// 있고, 그것 때문에 선언 파일을 고치게 하면 그 자리에서 막힌다. 만든 뒤 재고에 곧바로
    /// 넣어 두어야 <b>구성원 화면에서 바로 고를 수 있다.</b>
    /// </remarks>
    private object StartNewTeam(HttpAsk ask, CancellationToken ct)
    {
        var body = Body(ask);
        var name = Str(body, "displayName").Trim();
        var alias = Str(body, "mailNickname").Trim();
        var note = Str(body, "description").Trim();
        var template = Str(body, "template") is { Length: > 0 } t ? t : "educationClass";

        var wrong = NameProblem(name);
        if (wrong is not null) return new { ok = false, message = wrong };

        if (!AliasOk.IsMatch(alias))
            return new { ok = false, message = "별칭에는 영문자·숫자·붙임표·밑줄·점만 쓸 수 있습니다. 이것이 메일 주소가 됩니다." };

        if (_inventory.Any(g => string.Equals(g.DisplayName, name, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(g.MailNickname, alias, StringComparison.OrdinalIgnoreCase)))
            return new { ok = false, message = $"'{name}' 또는 별칭 '{alias}' 이(가) 이미 있습니다." };

        return Started(_jobs.Start($"'{name}' 만들기", async (job, jct) =>
        {
            if (!await EnsureTeamsAsync(job, jct).ConfigureAwait(false)) { job.Finish("팀에 붙지 못했습니다."); return; }

            var res = await _host.CallAsync("New-TeavelM365Group", new Dictionary<string, object?>
            {
                ["DisplayName"] = name,
                ["MailNickname"] = alias,
                ["Description"] = note,
                ["Kind"] = "team",
                ["Template"] = template,
                ["Visibility"] = "private",
            }, timeout: TimeSpan.FromMinutes(5), ct: jct).ConfigureAwait(false);

            if (!res.Ok) { job.Error(res.Message); job.Details(res.Details); job.Finish("만들지 못했습니다."); return; }

            job.Ok(res.Message);
            job.Details(res.Details);

            var id = M365Flow.ExtractGroupId(res.Details);
            _inventory.Add(new ExistingGroup(name, alias, IsTeam: true,
                MemberCount: 0, Created: DateTime.Now.ToString("yyyy-MM-dd"), Origin: "teavel", GroupId: id));

            job.Dim("담당 선생님이 Teams 에서 [활성화] 를 눌러야 학생에게 보입니다.");
            job.Dim("이제 구성원 화면에서 이 팀에 사람을 넣으실 수 있습니다.");
            job.Finish($"'{name}' 을(를) 만들었습니다.");
        }, ct));
    }

    /// <summary>메일 주소가 되는 별칭. 한글을 넣으면 뜻이 날아가고 주소가 엉킨다.</summary>
    private static readonly Regex AliasOk = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    /// <summary>
    /// 고른 사람들을 그룹 하나에 넣는다.
    /// </summary>
    /// <remarks>
    /// <b>이미 들어 있는 사람은 먼저 빼고 센다.</b> 1학년 예순 명을 넣는데 마흔이 이미
    /// 들어 있으면 스무 명만 넣어야 한다 — 그러지 않으면 넣을 때마다 실패 마흔 줄이 쌓이고,
    /// 관리자는 무엇이 진짜 문제인지 못 가린다.
    /// </remarks>
    private object StartAssign(HttpAsk ask, CancellationToken ct)
    {
        var body = Body(ask);
        var groupId = Str(body, "groupId");
        var role = Str(body, "role") is { Length: > 0 } r ? r : "Member";
        var upns = Arr(body, "upns");
        var label = Str(body, "label");

        if (groupId.Length == 0 || upns.Count == 0)
            return new { ok = false, message = "누구를 어느 팀에 넣을지 받지 못했습니다." };

        var team = _inventory.FirstOrDefault(g => g.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase));

        return Started(_jobs.Start(label.Length > 0 ? label : "그룹에 넣기", async (job, jct) =>
        {
            job.Info($"{team?.DisplayName ?? groupId} 에 지금 누가 들어 있는지 봅니다.");

            var have = await _host.CallAsync("Get-TeavelTeamMember",
                new Dictionary<string, object?> { ["GroupId"] = groupId },
                timeout: TimeSpan.FromMinutes(2), ct: jct).ConfigureAwait(false);

            var already = have.Ok
                ? M365Flow.ParseMembers(have.Details).Select(m => m.Upn).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var todo = upns.Where(u => !already.Contains(u)).ToList();

            if (todo.Count == 0)
            {
                job.Ok($"{upns.Count}명이 이미 다 들어 있습니다. 넣을 사람이 없습니다.");
                job.Finish("이미 다 들어 있습니다.");
                return;
            }

            if (todo.Count < upns.Count)
                job.Dim($"{upns.Count - todo.Count}명은 이미 들어 있어 건너뜁니다.");

            job.Info($"{todo.Count}명을 넣습니다. 사람이 많으면 몇 분 걸립니다.");

            var res = await _host.CallAsync("Add-TeavelTeamMember", new Dictionary<string, object?>
            {
                ["GroupId"] = groupId,
                ["Users"] = todo,
                ["Role"] = role,
            }, timeout: TimeSpan.FromMinutes(20), ct: jct).ConfigureAwait(false);

            if (!res.Ok) { job.Error(res.Message); job.Details(res.Details); job.Finish("넣지 못했습니다."); return; }

            job.Ok(res.Message);

            var bad = res.Details.Where(d => d.StartsWith("실패:", StringComparison.Ordinal)).ToList();
            foreach (var d in bad.Take(10)) job.Warn(d);
            if (bad.Count > 10) job.Warn($"…그 밖에 {bad.Count - 10}명 더");

            // 다시 읽어 둔다. 사람 명부의 '속해 있는 그룹' 칸이 곧바로 맞아야 한다.
            var back = await _host.CallAsync("Get-TeavelTeamMember",
                new Dictionary<string, object?> { ["GroupId"] = groupId },
                timeout: TimeSpan.FromMinutes(2), ct: jct).ConfigureAwait(false);

            if (back.Ok)
            {
                _members[groupId] = M365Flow.ParseMembers(back.Details);

                // 넣은 뒤 다시 읽었는데 수가 학교 명부와 다르면 그대로 말한다.
                foreach (var note in Notes(back.Details)) job.Warn(note);
            }

            job.Dim("학생 화면에 보이기까지 몇 분 걸릴 수 있습니다.");
            job.Finish($"{todo.Count - bad.Count}명을 넣었습니다.");
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

    /// <summary>
    /// 사람 목록을 읽는다.
    /// </summary>
    /// <remarks>
    /// <b>로그인은 더 필요하지 않다.</b> 사람 목록은 Exchange 에서 오고, 그것은 화면이
    /// 뜰 때 이미 붙어 있다. 사람이 많으면 오래 걸려서 시작할 때 자동으로 하지 않을 뿐이다.
    /// </remarks>
    private object StartReadPeople(CancellationToken ct)
        => Started(_jobs.Start("사람 목록 읽기", async (job, jct) =>
        {
            job.Info("학교 사람 목록을 읽습니다. 사람이 많으면 몇 분 걸립니다.");
            await ReadPeopleAsync(jct).ConfigureAwait(false);

            if (!_peopleRead)
            {
                job.Error(_peopleProblem);
                job.Finish("읽지 못했습니다.");
                return;
            }

            job.Ok($"{_people.Count}명을 읽었습니다.");
            job.Finish($"{_people.Count}명");
        }, ct));

    /// <summary>
    /// 계정을 막거나 푼다.
    /// </summary>
    /// <remarks>
    /// <b>졸업생 정리는 지우는 것이 아니라 막는 것이다.</b> 지우면 그 아이의 과제·파일·대화가
    /// 함께 사라지고 되돌릴 수 없다. 막아 두면 로그인만 안 될 뿐 자료는 그대로 있고,
    /// 잘못 골랐어도 풀면 그만이다. Exchange 로 되므로 동의 화면도 없다.
    ///
    /// <b>한 사람이 막혀도 나머지는 마저 한다</b> — 자기 자신은 막을 수 없는데,
    /// 예순 명 중에 그 계정이 섞였다고 쉰아홉을 못 하면 안 된다.
    /// </remarks>
    private object StartBlock(HttpAsk ask, CancellationToken ct)
    {
        var body = Body(ask);
        var upns = Arr(body, "upns");
        var blocked = !body.TryGetProperty("blocked", out var b) || b.ValueKind != JsonValueKind.False;
        var label = Str(body, "label");

        if (upns.Count == 0) return new { ok = false, message = "누구를 막거나 풀지 받지 못했습니다." };

        var byUpn = _people.ToDictionary(p => p.Upn, p => p.DisplayName, StringComparer.OrdinalIgnoreCase);
        var what = blocked ? "차단" : "차단 풀기";

        return Started(_jobs.Start(label.Length > 0 ? label : $"{what} {upns.Count}명", async (job, jct) =>
        {
            var done = 0;
            var failed = 0;

            foreach (var upn in upns)
            {
                jct.ThrowIfCancellationRequested();

                var res = await _host.CallAsync("Set-TeavelAccountBlocked", new Dictionary<string, object?>
                {
                    ["Identity"] = upn,
                    ["Blocked"] = blocked,
                }, timeout: TimeSpan.FromMinutes(2), ct: jct).ConfigureAwait(false);

                var name = byUpn.TryGetValue(upn, out var n) && n.Length > 0 ? n : upn;

                if (res.Ok) { job.Ok($"{name} — {what}했습니다."); done++; }
                else { job.Warn($"{name} — {res.Message}"); failed++; }
            }

            // 화면의 차단 칸이 곧바로 맞아야 한다. 사람 목록을 다시 읽는다.
            await ReadPeopleAsync(jct).ConfigureAwait(false);

            if (blocked)
            {
                job.Dim("막힌 계정은 로그인만 안 됩니다. 과제·파일·대화는 그대로 있습니다.");
                job.Dim("잘못 고르셨으면 같은 자리에서 [차단 풀기] 로 되돌리실 수 있습니다.");
            }

            job.Finish(failed == 0 ? $"{done}명을 {what}했습니다." : $"{done}명은 {what}하고 {failed}명은 못 했습니다.");
        }, ct));
    }

    /// <summary>
    /// 계정을 지운다.
    /// </summary>
    /// <remarks>
    /// <b>이 화면에서 가장 되돌리기 어려운 일이다.</b> 메일·과제·파일·원드라이브가 함께
    /// 사라진다. 30일 안에는 관리 센터에서 되살릴 수 있고, 그 뒤에는 아무도 못 되살린다.
    /// 그래서 그룹 지우기와 같은 문을 세운다 — 몇 명을 지우는지 <b>숫자를 그대로 적어야</b>
    /// 실행한다. 단추 하나로 예순 명이 사라지면 안 된다.
    ///
    /// <b>한 사람이 막혀도 나머지는 마저 한다</b> — 자기 자신이나 상급 관리자는 못 지우는데,
    /// 그 계정이 섞였다고 나머지를 못 하면 안 된다.
    /// </remarks>
    private object StartRemovePeople(HttpAsk ask, CancellationToken ct)
    {
        var body = Body(ask);
        var upns = Arr(body, "upns");
        var typed = Str(body, "typed").Trim();
        var label = Str(body, "label");

        if (upns.Count == 0) return new { ok = false, message = "누구를 지울지 받지 못했습니다." };

        var byUpn = _people.ToDictionary(p => p.Upn, p => p.DisplayName, StringComparer.OrdinalIgnoreCase);

        // 무엇을 적어야 여는가.
        //
        // 여럿이면 몇 명인지다 — 적는 동안 그 수를 한 번 더 보게 된다. 그런데 한 사람일 때
        // 그 문은 '1' 이라 사실상 없는 문이다. 줄 끝의 휴지통은 이름 바꾸기 바로 옆에 있어
        // 잘못 누르기도 쉽다. 그래서 한 사람이면 <b>그 사람 이름</b>을 적게 한다 —
        // 그룹 지우기와 같은 문이다.
        var only = upns.Count == 1
            ? _people.FirstOrDefault(p => p.Upn.Equals(upns[0], StringComparison.OrdinalIgnoreCase))
            : null;

        var wanted = only is not null && only.DisplayName.Trim().Length > 0
            ? only.DisplayName.Trim()
            : upns.Count == 1 ? upns[0] : upns.Count.ToString(CultureInfo.InvariantCulture);

        if (!string.Equals(typed, wanted, StringComparison.Ordinal))
            return new { ok = false, message = $"적으신 것이 다릅니다. 지우지 않았습니다 — '{wanted}' 을(를) 적어 주세요." };

        return Started(_jobs.Start(label.Length > 0 ? label : $"계정 지우기 {upns.Count}명", async (job, jct) =>
        {
            if (!await EnsureGraphAsync(job, DeleteScope, jct).ConfigureAwait(false))
            {
                job.Finish("연결하지 못했습니다.");
                return;
            }

            var done = 0;
            var failed = 0;

            foreach (var upn in upns)
            {
                jct.ThrowIfCancellationRequested();

                var res = await (await GraphAsync(jct).ConfigureAwait(false)).CallAsync("Remove-TeavelAccount",
                    new Dictionary<string, object?> { ["Identity"] = upn },
                    timeout: TimeSpan.FromMinutes(2), ct: jct).ConfigureAwait(false);

                var name = byUpn.TryGetValue(upn, out var n) && n.Length > 0 ? n : upn;

                if (res.Ok) { job.Ok($"{name} — 지웠습니다."); _deleted.Add(upn); done++; }
                else { job.Warn($"{name} — {res.Message}"); failed++; }
            }

            // 사람 목록이 곧바로 맞아야 한다. 지운 사람이 화면에 남아 있으면
            // 관리자는 안 지워진 줄 알고 한 번 더 누른다.
            await ReadPeopleAsync(jct).ConfigureAwait(false);

            if (done > 0)
                job.Dim("잘못 지우셨으면 30일 안에 관리 센터 › 사용자 › 삭제된 사용자 에서 되살리실 수 있습니다.");

            job.Finish(failed == 0 ? $"{done}명을 지웠습니다." : $"{done}명은 지우고 {failed}명은 못 지웠습니다.");
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
        if (_teamsReady) { job.Dim("팀에 이미 붙어 있는 것으로 보고 넘어갑니다."); return true; }

        job.Info("팀 작업을 위해 로그인이 한 번 더 필요합니다.");
        job.Info("창은 저절로 뜨지 않습니다 — 아래에 나오는 주소와 코드를 인터넷 창에 직접 넣으셔야 합니다.");

        var res = await _host.CallAsync("Connect-TeavelM365",
            new Dictionary<string, object?> { ["TeamsToo"] = true },
            timeout: TimeSpan.FromMinutes(20), ct: ct).ConfigureAwait(false);

        if (!res.Ok)
        {
            job.Error(res.Message);
            job.Details(res.Details);
            return false;
        }

        job.Ok(res.Message);

        // 연결이 무슨 상태였는지 묻히지 않게 앞으로 끌어낸다.
        //
        // 예전에는 이 줄들을 아예 안 찍었다. 그래서 '팀: 이미 연결돼 있었습니다' 인데 정작
        // 팀 작업이 전부 터지는 것을 보고도, <b>무엇이 그렇게 판단했는지 알 수가 없었다.</b>
        job.Details(res.Details);

        _teamsReady = true;
        return true;
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

        // 명단 양식. 파일에 BOM 이 들어 있어야 엑셀이 한글을 제대로 연다 —
        // 없으면 CP949 로 읽어 이름이 통째로 깨진다.
        ".csv" => "text/csv; charset=utf-8",
        _ => "application/octet-stream",
    };
}
