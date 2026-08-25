using Teavel.M365;
using Teavel.Roster;
using Teavel.Tools;

namespace Teavel.Cli;

/// <summary>
/// 학교 M365 그룹·Teams 를 정리하고 구성하는 한 판.
///
/// <para>
/// 대상은 학교의 M365 전역 관리자인데, 대개 전문가가 아니라 그 일을 떠맡은 선생님이다.
/// 그래서 이 흐름의 규칙은 하나다 — <b>아무것도 모르는 사람이 순서대로 따라오면
/// 끝나야 한다.</b> 무엇을 물어볼지 스스로 알아야 하는 대목이 있으면 그 자리에서 막힌다.
/// </para>
/// <para>
/// 순서에 뜻이 있다. <b>재고를 먼저 보고, 정리를 먼저 하고, 그다음에 만든다.</b>
/// 학교엔 지난 몇 년치 그룹이 어질러져 있어서, 그걸 두고 새로 만들면 이름이 겹치거나
/// 비슷한 것이 둘씩 생긴다. 그리고 정리는 <b>지우기보다 이름 바꾸기가 먼저다</b> —
/// 지우면 파일·대화가 함께 사라지지만 이름만 바꾸면 내용을 그대로 두고 새 체계에 넣을 수 있다.
/// </para>
/// </summary>
public sealed class M365Flow
{
    private readonly ToolRunner _tools;
    private readonly bool _assumeYes;

    /// <summary>팀에 붙었는지. 두 번째 로그인은 정말 필요할 때까지 미룬다.</summary>
    private bool _teamsReady;

    public M365Flow(ToolRunner tools, bool assumeYes)
    {
        _tools = tools;
        _assumeYes = assumeYes;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        Ui.Title("학교 Microsoft 365 구성");
        Ui.Dim("      학교의 그룹과 Teams 를 살펴보고, 정리하고, 모자란 것을 만듭니다.");
        Ui.Dim("      전역 관리자 계정이 필요합니다.");

        var shell = _tools.FindPowerShell();
        if (shell is null)
        {
            Ui.Error("PowerShell 을 찾지 못했습니다.");
            return 2;
        }

        // 선언을 먼저 읽는다. 잘못돼 있으면 로그인까지 시켜 놓고 무를 수는 없다.
        var tree = SchoolTree.Load(AppContext.BaseDirectory);
        if (!tree.Ok)
        {
            Ui.Error($"학교 구조 선언에 문제가 있습니다. ({tree.Source})");
            Ui.Details(tree.Problems.Select(p => $"{p.Where} — {p.Problem}"));
            Ui.Dim("      이 파일을 고친 뒤 다시 실행해 주세요.");
            return 2;
        }

        await using var host = await StartHostAsync(shell, ct).ConfigureAwait(false);
        if (host is null) return 2;

        if (!await EnsureModulesAsync(host, ct).ConfigureAwait(false)) return 2;
        if (!await ConnectAsync(host, tree, ct).ConfigureAwait(false)) return 2;

        var inventory = await ReadInventoryAsync(host, ct).ConfigureAwait(false);
        if (inventory is null) return 2;

        ShowInventory(inventory);
        await ShowPeopleAsync(host, ct).ConfigureAwait(false);

        // 이름이 나뉘어 있으면 여기서 짚고 넘어간다. 팀을 만들고 사람을 넣기 시작한 뒤에
        // 고치면 이미 여기저기에 뒤집힌 이름이 박혀 있다.
        await FixSplitNamesAsync(host, ct).ConfigureAwait(false);

        // ① 명단을 먼저 받는다. 명단은 학생 목록이기도 하지만 무엇보다
        //    이 학교가 몇 학년 몇 반까지 있는지를 담고 있다 — 구조의 출처다.
        var roster = await AskRosterAsync(ct).ConfigureAwait(false);

        // ② 명단이 있으면 그것으로 반 구조를 정한다. 없으면 선언 파일을 그대로 쓴다.
        var groups = ShapeFromRoster(tree, roster);

        // ③ 정리. 여기서 이름을 바꾼 것은 아래 대조에 곧바로 반영돼야 하므로 목록을 돌려받는다.
        inventory = await TidyAsync(host, inventory, groups, ct).ConfigureAwait(false);

        // ④ 그다음에 만들기.
        var code = await CreateMissingAsync(host, groups, inventory, ct).ConfigureAwait(false);

        // ⑤ 마지막으로 사람 넣기. 팀이 있어야 넣을 수 있으므로 순서가 여기다.
        //    만들기가 일부 실패했어도 만들어진 팀에는 넣을 수 있으니 멈추지 않는다.
        if (roster is not null)
            await AddMembersAsync(host, groups, roster, ct).ConfigureAwait(false);

        return code;
    }

    // ───────────────────────────── 준비 ─────────────────────────────

    private async Task<M365Host?> StartHostAsync(string shell, CancellationToken ct)
    {
        try
        {
            return await M365Host.StartAsync(
                shell, _tools.ScriptsDirectory, Ui.Plain, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Ui.Error(ex.Message);
            return null;
        }
    }

    private async Task<bool> EnsureModulesAsync(M365Host host, CancellationToken ct)
    {
        Ui.Title("① 준비 확인");

        var ready = await host.CallAsync("Get-TeavelM365Readiness", ct: ct).ConfigureAwait(false);
        Ui.Info(ready.Message);
        Ui.Details(ready.Details);

        // 준비확인은 모자랄 때도 ok=true 로 돌아온다(그건 실패가 아니라 사실 보고다).
        // 무엇이 모자란지는 문구로 판단하지 않고, 설치를 돌린 뒤 다시 확인해서 가른다.
        if (!ready.Ok) { Ui.Error(ready.Message); return false; }
        if (!ready.Message.Contains("손봐야", StringComparison.Ordinal)) return true;

        Console.WriteLine();
        Ui.Dim("      모자란 것은 Teavel 이 대신 설치합니다.");
        Ui.Dim("      내 계정에만 설치되고 관리자 권한은 필요 없습니다.");
        if (!_assumeYes && !Ui.Confirm("      지금 설치할까요?"))
        {
            Ui.Info("여기까지 하겠습니다. 모듈이 없으면 다음 단계로 갈 수 없습니다.");
            return false;
        }

        Console.WriteLine();
        Ui.Dim("      내려받는 중입니다. 몇 분 걸릴 수 있습니다.");
        var install = await host.CallAsync("Install-TeavelM365Module",
            timeout: TimeSpan.FromMinutes(15), ct: ct).ConfigureAwait(false);
        Ui.Details(install.Details);

        var again = await host.CallAsync("Get-TeavelM365Readiness", ct: ct).ConfigureAwait(false);
        if (again.Message.Contains("손봐야", StringComparison.Ordinal))
        {
            Ui.Error("설치했는데도 아직 모자랍니다.");
            Ui.Details(again.Details);
            return false;
        }

        Ui.Ok("준비됐습니다.");
        return true;
    }

    private async Task<bool> ConnectAsync(M365Host host, SchoolTree tree, CancellationToken ct)
    {
        Ui.Title("② 학교 계정으로 로그인");

        // 여기서는 메일·그룹(Exchange)만 붙인다.
        //
        // 팀(Teams)은 실제로 만들거나 사람을 넣을 때만 필요하다. 그런데 처음부터 붙이면
        // 시작하자마자 로그인 창이 두 번 뜬다 — 로그인 한 번도 버거운 분들에게 그건 벽이다.
        // 게다가 그 두 번째 창이 뒤에 숨어 못 보고 지나치면 거기서 통째로 끝나 버린다.
        // 실기에서 그랬다(2026-08-17): 재고도 못 보고 'User canceled authentication' 로 끝났다.
        //
        // 그래서 미룬다. 읽기만 하다 끝내는 관리자는 로그인을 한 번만 하면 된다.
        var args = new Dictionary<string, object?> { ["TeamsToo"] = false };
        var res = await host.CallAsync("Connect-TeavelM365", args,
            timeout: TimeSpan.FromMinutes(20), ct: ct).ConfigureAwait(false);

        if (!res.Ok)
        {
            Ui.Error(res.Message);
            Ui.Details(res.Details);
            return false;
        }

        Ui.Ok(res.Message);
        Ui.Details(res.Details);
        return true;
    }

    /// <summary>
    /// 팀에 붙는다. 이미 붙었으면 아무것도 하지 않는다.
    /// </summary>
    /// <remarks>
    /// 정말 필요한 자리에서만 부른다 — 팀을 만들기 직전, 사람을 넣기 직전.
    /// 실패해도 부르는 쪽이 판단하게 <c>false</c> 를 돌려준다. 팀이 없어도 할 수 있는 일이 있다.
    /// </remarks>
    private async Task<bool> EnsureTeamsAsync(M365Host host, CancellationToken ct)
    {
        if (_teamsReady) return true;

        Console.WriteLine();
        Ui.Info("팀 작업을 위해 한 번 더 로그인이 필요합니다.");

        var res = await host.CallAsync("Connect-TeavelM365",
            new Dictionary<string, object?> { ["TeamsToo"] = true },
            timeout: TimeSpan.FromMinutes(20), ct: ct).ConfigureAwait(false);

        if (res.Ok) { _teamsReady = true; return true; }

        Ui.Error(res.Message);
        Ui.Details(res.Details);
        Console.WriteLine();
        Ui.Dim("      팀에 붙지 못했습니다. 팀을 만들거나 사람을 넣는 일은 할 수 없습니다.");
        return false;
    }

    // ───────────────────────────── 재고 ─────────────────────────────

    /// <param name="quiet">
    /// 제목을 찍지 않는다. 학생을 넣기 전에 재고를 다시 읽을 때 쓴다 —
    /// 거기서 '③ 지금 학교에 있는 것' 이 또 나오면 화면이 뒤로 돌아간 것처럼 보인다.
    /// </param>
    private async Task<List<ExistingGroup>?> ReadInventoryAsync(
        M365Host host, CancellationToken ct, bool quiet = false)
    {
        if (!quiet) Ui.Title("③ 지금 학교에 있는 것");

        var res = await host.CallAsync("Get-TeavelM365Inventory",
            timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false);

        if (!res.Ok)
        {
            Ui.Error(res.Message);
            Ui.Details(res.Details);
            return null;
        }

        return ParseInventory(res.Details);
    }

    /// <summary>
    /// PowerShell 이 낸 재고 줄들을 읽는다.
    /// </summary>
    /// <remarks>
    /// 한 줄이 <c>GROUP\t이름\t별칭\t메일\t팀여부\t인원\t만든날\t공개범위\t그룹id</c> 꼴이다.
    /// 이름에 탭이 들어갈 일은 없으므로 탭으로 가른다.
    /// 모양이 다른 줄은 <b>버리지 않고 넘어간다</b> — 한 줄 때문에 재고 전체를
    /// 못 보게 되면 관리자는 아무것도 할 수 없다.
    /// </remarks>
    internal static List<ExistingGroup> ParseInventory(IEnumerable<string> lines)
    {
        var groups = new List<ExistingGroup>();

        foreach (var line in lines)
        {
            var f = line.Split('\t');
            if (f.Length < 5 || !string.Equals(f[0], "GROUP", StringComparison.Ordinal)) continue;

            var isTeam = bool.TryParse(f[4], out var t) && t;
            var members = f.Length > 5 && int.TryParse(f[5], out var m) ? m : -1;
            var created = f.Length > 6 ? f[6] : "";
            var privacy = f.Length > 7 ? f[7] : "";
            var groupId = f.Length > 8 ? f[8] : "";

            groups.Add(new ExistingGroup(
                DisplayName: f[1], MailNickname: f[2], IsTeam: isTeam,
                MemberCount: members, Created: created, Origin: privacy, GroupId: groupId));
        }

        return groups;
    }

    private static void ShowInventory(IReadOnlyList<ExistingGroup> inventory)
    {
        if (inventory.Count == 0)
        {
            Ui.Info("그룹이 하나도 없습니다. 깨끗한 상태에서 시작합니다.");
            return;
        }

        var triaged = InventoryTriage.Triage(inventory);
        Ui.Info(InventoryTriage.Summarize(triaged));

        foreach (var bucket in new[] { TriageBucket.InUse, TriageBucket.Candidate, TriageBucket.System })
        {
            var rows = triaged.Where(t => t.Bucket == bucket).ToList();
            if (rows.Count == 0) continue;

            Console.WriteLine();
            Ui.Dim("      " + BucketTitle(bucket));
            foreach (var r in rows) Ui.Plain("        " + Describe(r));
        }
    }

    private static string BucketTitle(TriageBucket b) => b switch
    {
        TriageBucket.InUse => "쓰이고 있는 것 — 사람이 여럿 들어 있습니다",
        TriageBucket.Candidate => "정리해 볼 만한 것 — 이름이나 인원이 수상합니다",
        _ => "건드리면 안 되는 것 — Microsoft 365 가 만든 것입니다",
    };

    private static string Describe(TriagedGroup t)
    {
        var g = t.Group;
        var kind = g.IsTeam ? "팀 " : "그룹";
        var members = g.MemberCount >= 0 ? $"{g.MemberCount,4}명" : "   ?명";
        var line = $"{kind} {members}  {g.DisplayName}";
        if (t.Note.Length > 0) line += $"   ({t.Note})";
        return line;
    }

    /// <summary>
    /// 학교에 누가 있는지 보여 준다. 라이선스가 같은 사람끼리 묶어서.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 교사와 학생은 라이선스가 다르지만 그 <b>이름</b>(SKU)을 읽을 방법이 마땅치 않다 —
    /// Get-MsolUser 는 서버가 닫혔고 Graph 는 동의 화면을 부른다.
    /// 그래서 이름을 알아내는 대신 <b>같은 라이선스끼리 묶기만 한다.</b>
    /// 학교라면 큰 묶음 둘이 나오고, 어느 쪽이 교사인지는 이름 몇 개만 보면 사람이 안다.
    /// </para>
    /// <para>
    /// 여기서는 아직 아무것도 배정하지 않는다. 보여 주기만 한다 —
    /// 누구를 어느 반에 넣을지는 명단이 있어야 정할 수 있다.
    /// 다만 <b>라이선스 없는 계정</b>은 지금 알려 줄 값어치가 있다.
    /// 그 계정은 팀에 넣어도 Teams 에 들어오지 못하는데, 관리자는 대개 모르고 있다.
    /// </para>
    /// </remarks>
    private async Task ShowPeopleAsync(M365Host host, CancellationToken ct)
    {
        // 사람 목록은 Teams 쪽에서 온다. 그것 하나 보자고 로그인을 한 번 더 시키지 않는다 —
        // 나중에 팀을 만들 때 어차피 붙게 되고, 그때 보여 줘도 늦지 않다.
        if (!_teamsReady)
        {
            Ui.Dim("      (학교 사람 목록은 팀에 연결한 뒤에 보여 드립니다)");
            return;
        }

        var res = await host.CallAsync("Get-TeavelTenantUser",
            timeout: TimeSpan.FromMinutes(10), ct: ct).ConfigureAwait(false);

        // 사람을 못 읽어도 그룹 작업은 할 수 있다. 여기서 멈추지 않는다.
        if (!res.Ok)
        {
            Ui.Dim($"      (사람 목록은 읽지 못했습니다: {res.Message})");
            return;
        }

        var people = UserDirectory.Parse(res.Details);
        if (people.Count == 0) return;

        var clusters = UserDirectory.Cluster(people);

        Console.WriteLine();
        Ui.Info(UserDirectory.Summarize(clusters, people));

        foreach (var c in clusters.Where(c => !c.Unlicensed && c.Count >= UserDirectory.SmallCluster))
        {
            Ui.Plain($"        {c.Count,5}명   {c.Sample()} …");
            if (c.Departments.Count > 0)
                Ui.Dim($"                부서: {string.Join(" · ", c.Departments)}");
        }

        var unlicensed = clusters.Where(c => c.Unlicensed).SelectMany(c => c.People).ToList();
        if (unlicensed.Count > 0)
        {
            Console.WriteLine();
            Ui.Warn($"라이선스가 없는 계정이 {unlicensed.Count}개 있습니다. 팀에 넣어도 들어오지 못합니다.");
            foreach (var u in unlicensed.Take(10))
                Ui.Plain($"        {u.DisplayName}   {u.Upn}");
            if (unlicensed.Count > 10) Ui.Dim($"        … 그 밖에 {unlicensed.Count - 10}개");
        }

        Console.WriteLine();
        Ui.Dim("      라이선스가 같은 사람끼리 묶은 것입니다. 대개 큰 쪽이 학생, 작은 쪽이 교사입니다.");
        Ui.Dim("      누구를 어느 반에 넣을지는 명단이 있어야 정할 수 있어, 여기서는 보여 주기만 합니다.");
    }

    /// <summary>
    /// 선생님을 이름으로 찾는다. 학생과 달리 명단 파일이 필요 없다 —
    /// 교사 계정은 이미 만들어져 있으므로 찾기만 하면 된다.
    /// </summary>
    public static async Task<int> FindTeacherAsync(ToolRunner tools, string? name, CancellationToken ct)
    {
        Ui.Title("선생님 찾기");

        if (string.IsNullOrWhiteSpace(name))
        {
            Ui.Error("누구를 찾을지 알려 주세요.");
            Ui.Dim("      teavel 선생님 김하늘");
            return 2;
        }

        var shell = tools.FindPowerShell();
        if (shell is null) { Ui.Error("PowerShell 을 찾지 못했습니다."); return 2; }

        await using var host = await M365Host.StartAsync(
            shell, tools.ScriptsDirectory, Ui.Plain, ct).ConfigureAwait(false);

        var conn = await host.CallAsync("Connect-TeavelM365",
            new Dictionary<string, object?> { ["TeamsToo"] = true },
            timeout: TimeSpan.FromMinutes(20), ct: ct).ConfigureAwait(false);
        if (!conn.Ok) { Ui.Error(conn.Message); return 2; }

        var res = await host.CallAsync("Get-TeavelTenantUser",
            timeout: TimeSpan.FromMinutes(10), ct: ct).ConfigureAwait(false);
        if (!res.Ok) { Ui.Error(res.Message); return 2; }

        var people = UserDirectory.Parse(res.Details);
        var clusters = UserDirectory.Cluster(people);
        var faculty = UserDirectory.GuessFaculty(clusters);

        var search = TeacherFinder.Find(people, name, faculty?.Bundle);
        var matches = search.Matches;

        Console.WriteLine();
        if (matches.Count == 0)
        {
            // 있는데 감춘 것과 아예 없는 것은 다르다. 뭉뚱그리면 관리자가 헛짚는다.
            if (search.Students.Count > 0)
            {
                Ui.Warn($"'{name}' 은(는) 학생 계정으로만 나옵니다. 선생님 계정이 아닙니다.");
                foreach (var st in search.Students.Take(5))
                    Ui.Plain($"        {st.User.DisplayName}   {st.User.Upn}");
                Console.WriteLine();
                Ui.Dim("      학생은 팀 소유자로 넣으면 안 됩니다 — 반 전체를 지울 수 있습니다.");
                Ui.Dim("      찾으시는 선생님 성함이 맞는지 확인해 주세요.");
                return 1;
            }

            Ui.Warn($"'{name}' 으로 찾은 계정이 없습니다.");
            Ui.Dim("      성만 넣어 보셔도 됩니다. 예: 김");
            Ui.Dim("      그래도 없으면 그 선생님은 아직 학교 계정을 못 받으신 것일 수 있습니다.");
            return 1;
        }

        Ui.Ok($"{matches.Count}명 찾았습니다.");
        foreach (var m in matches.Take(10))
        {
            Ui.Plain($"        {m.User.DisplayName}   {m.User.Upn}");
            Ui.Dim($"            {m.Why}"
                 + (m.User.Department.Length > 0 ? $" · {m.User.Department}" : ""));
        }
        if (matches.Count > 10) Ui.Dim($"        … 그 밖에 {matches.Count - 10}명");

        if (!TeacherFinder.IsCertain(matches))
        {
            Console.WriteLine();
            Ui.Warn("한 사람으로 좁혀지지 않았습니다. 같은 이름이 여럿일 수 있습니다.");
            Ui.Dim("      팀 소유자로 넣을 때는 아이디까지 보고 고르셔야 합니다.");
        }

        return 0;
    }

    /// <summary>
    /// 성과 이름이 나뉘어 있는 계정을 찾아 <b>합치자고 권한다.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 교육청 포털로 교사 계정을 만들면 성과 이름이 나뉘어 들어가고, 화면에 '하늘 김' 처럼
    /// 뒤집혀 보인다. 김·이·박이 학교마다 수십 명이라 이대로 두면 <b>실제 운영에서 헷갈린다</b> —
    /// 팀 소유자를 정하거나 사람을 찾을 때마다 누가 누구인지 알 수 없다.
    /// </para>
    /// <para>
    /// 관리자는 이게 무슨 문제인지 모른다. 그래서 <b>묻지 말고 권한다</b> —
    /// 무엇이 어떻게 바뀌는지 눈으로 보여 주고, 기본값을 '고침' 으로 둔다.
    /// 되돌릴 수 있는 일이고(이름만 바뀐다) 안 고치면 두고두고 걸린다.
    /// </para>
    /// </remarks>
    private async Task FixSplitNamesAsync(M365Host host, CancellationToken ct)
    {
        var res = await host.CallAsync("Get-TeavelUserName",
            timeout: TimeSpan.FromMinutes(10), ct: ct).ConfigureAwait(false);
        if (!res.Ok) return;

        var todo = new List<(string Upn, string Now, MergedName Fix)>();

        foreach (var line in res.Details)
        {
            var f = line.Split('\t');
            if (f.Length < 5 || !string.Equals(f[0], "NAME", StringComparison.Ordinal)) continue;

            var merged = KoreanName.Merge(f[3], f[4]);
            if (!merged.Certain) continue;                       // 가릴 수 없으면 건드리지 않는다
            if (!KoreanName.NeedsFixing(f[2], merged)) continue;

            todo.Add((f[1], f[2], merged));
        }

        if (todo.Count == 0) return;

        Console.WriteLine();
        Ui.Warn($"이름이 나뉘어 있는 계정이 {todo.Count}개 있습니다.");
        Ui.Plain("""
              교육청 포털로 계정을 만들면 성과 이름이 따로 들어갑니다.
              그러면 Teams 화면에 이름이 뒤집혀 보이고, 김·이·박 선생님이 여럿일 때
              누가 누구인지 알 수 없습니다. 붙여 두는 편이 낫습니다.
        """);

        Console.WriteLine();
        foreach (var t in todo.Take(8))
            Ui.Plain($"        {t.Now,-14} →  {t.Fix.Merged,-10}   {t.Upn}");
        if (todo.Count > 8) Ui.Dim($"        … 그 밖에 {todo.Count - 8}개");

        Console.WriteLine();
        Ui.Dim("      바뀌는 것은 화면에 보이는 이름뿐입니다. 아이디·비밀번호·파일은 그대로입니다.");

        if (!_assumeYes && !Ui.Confirm("      이렇게 고칠까요?"))
        {
            Ui.Info("그냥 두겠습니다.");
            return;
        }

        var done = 0;
        var failed = new List<string>();

        foreach (var t in todo)
        {
            var r = await host.CallAsync("Set-TeavelDisplayName", new Dictionary<string, object?>
            {
                ["Identity"] = t.Upn,
                ["DisplayName"] = t.Fix.Merged,
            }, ct: ct).ConfigureAwait(false);

            if (r.Ok) done++;
            else failed.Add($"{t.Upn} — {r.Message}");
        }

        Console.WriteLine();
        Ui.Ok($"{done}개를 고쳤습니다.");
        if (failed.Count > 0)
        {
            Ui.Warn($"{failed.Count}개는 고치지 못했습니다.");
            foreach (var f2 in failed.Take(5)) Ui.Plain($"        {f2}");
        }
        Ui.Dim("      Teams 앱에 반영되기까지 시간이 걸립니다.");
    }

    // ──────────────────────────── 정리 ────────────────────────────

    /// <summary>
    /// 정리 후보를 하나씩 보여 주고 어떻게 할지 묻는다. 바뀐 재고를 돌려준다.
    /// </summary>
    /// <remarks>
    /// <b>선언에 있는 것은 후보에서 뺀다.</b> 방금 만든 반 팀은 학생을 넣기 전이라
    /// 구성원이 없는데, 그것을 '쓰이지 않는 것 같다' 며 지우자고 권하면 안 된다.
    /// 실제로 시험에서 팀 18개를 만든 직후 다시 돌리니 그중 13개를 지우자고 했다 —
    /// 관리자가 그대로 눌렀으면 방금 만든 것을 도로 지울 뻔했다.
    ///
    /// 선언에 있다는 것은 '이 학교에 있어야 하는 것' 이라는 뜻이므로, 비어 있어도 정상이다.
    /// </remarks>
    private async Task<List<ExistingGroup>> TidyAsync(
        M365Host host, List<ExistingGroup> inventory,
        IReadOnlyList<DeclaredGroup> groups, CancellationToken ct)
    {
        var declared = groups
            .Select(g => TreeReconciler.Loosen(g.DisplayName))
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = InventoryTriage.Triage(inventory)
            .Where(t => t.Bucket == TriageBucket.Candidate)
            .Where(t => !declared.Contains(TreeReconciler.Loosen(t.Group.DisplayName)))
            .ToList();

        if (candidates.Count == 0) return inventory;

        Ui.Title("⑤ 정리");
        Ui.Dim($"      정리해 볼 만한 것이 {candidates.Count}개 있습니다.");
        Console.WriteLine();
        Ui.Dim("      지우면 그 안의 파일과 대화가 함께 사라집니다.");
        Ui.Dim("      이름만 바꾸면 내용은 그대로 남습니다. 잘 모르겠으면 그냥 두기를 고르세요.");

        // 자동 응답으로 돌 때는 아무것도 지우지 않는다. 사람이 없는 자리에서
        // 파일이 딸린 그룹을 지우는 일이 벌어져서는 안 된다.
        if (_assumeYes)
        {
            Console.WriteLine();
            Ui.Info("자동 모드에서는 정리를 건너뜁니다. 지우기는 사람이 봐야 합니다.");
            return inventory;
        }

        // 정리는 견주어 보는 일인데 콘솔은 하나씩 묻고 넘어간다 — 앞의 판단을 고칠 수 없다.
        // 목록을 놓고 견주는 것은 관리 화면(teavel 관리센터)에서 한다.
        Console.WriteLine();
        Ui.Dim("      하나씩 여쭙겠습니다. 한눈에 보고 고르시려면 'teavel 관리센터' 를 쓰세요.");

        foreach (var t in candidates)
        {
            var g = t.Group;
            Console.WriteLine();
            Ui.Warn($"{g.DisplayName}");
            Ui.Dim($"      {(g.IsTeam ? "팀" : "그룹")} · 구성원 {(g.MemberCount >= 0 ? g.MemberCount + "명" : "모름")}"
                 + (g.Created.Length > 0 ? $" · {g.Created} 에 만듦" : ""));
            if (t.Note.Length > 0) Ui.Dim($"      {t.Note}");

            // 보관은 지난 학년도 팀을 다루는 실제 방식이다 —
            // 이름 앞에 연도를 붙이고 학생을 내보내되, 팀과 자료는 그대로 둔다.
            // 지우는 것과 전혀 다르므로 갈래를 따로 둔다.
            var year = YearOf(g);
            var archived = year.Length > 0 ? $"{year} {g.DisplayName}" : "";

            var options = new List<Ui.Choice>
            {
                new("1", "[1] 이름 바꿔서 그대로 두기", "이름", "이름바꿔", "이름변경", "바꿔", "개명"),
                new("2", "[2] 지우기", "지워", "지우", "삭제", "없애", "빼", "제거"),
                new("3", "[3] 그냥 두기", "그냥", "놔둬", "두기", "넘어가", "패스", "건너뛰", "몰라"),
            };
            if (archived.Length > 0)
                options.Add(new Ui.Choice("4",
                    $"[4] 지난 학년도로 보관   →  '{archived}' 로 바꾸고 학생만 내보냅니다",
                    "보관", "백업", "작년", "지난", "묵은", "archive"));

            var pick = Ui.Choose("고르세요", options, "3");

            if (pick == "1")
            {
                var newName = (Ui.Ask("        새 이름: ") ?? "").Trim();
                if (newName.Length == 0) { Ui.Info("이름을 받지 못해 그냥 둡니다."); continue; }

                // 엉뚱한 것이 들어오면 여기서 막는다. 창에 파일을 끌어다 놓거나 다른 곳에서
                // 복사한 것을 그대로 붙여넣는 일이 있는데, 그대로 두면 학교 그룹 이름이
                // 파일 경로가 되어 버린다. 실제로 시험 중에 그렇게 됐다.
                if (newName.Length > 60 || newName.IndexOfAny(new[] { '\\', '/' }) >= 0)
                {
                    Ui.Warn("그건 이름 같지 않습니다. 파일 경로를 붙여넣으신 것 같습니다.");
                    Ui.Dim($"      받은 것: {(newName.Length > 40 ? newName[..40] + "…" : newName)}");
                    Ui.Info("그냥 둡니다. 다시 실행해서 이름만 적어 주세요.");
                    continue;
                }

                await RenameOneAsync(host, inventory, g, newName, ct).ConfigureAwait(false);
            }
            else if (pick == "2")
            {
                // 미리보기부터. 무엇이 사라지는지 보여 준 다음에 다시 묻는다.
                var preview = await host.CallAsync("Remove-TeavelM365Group", new Dictionary<string, object?>
                {
                    ["Identity"] = g.MailNickname,
                }, ct: ct).ConfigureAwait(false);

                Ui.Details(preview.Details);

                // 실수로 Enter 를 눌러 지워지는 일이 없게, 여기서만 이름을 받아 적게 한다.
                Ui.Dim("        정말 지우려면 그룹 이름을 그대로 적어 주세요. 아니면 그냥 Enter.");
                var typed = (Ui.Ask("        > ") ?? "").Trim();
                if (!string.Equals(typed, g.DisplayName.Trim(), StringComparison.Ordinal))
                {
                    Ui.Info("지우지 않았습니다.");
                    continue;
                }

                await DeleteOneAsync(host, inventory, g, ct).ConfigureAwait(false);
            }
            else if (pick == "4" && archived.Length > 0)
            {
                await ArchiveOneAsync(host, inventory, g, archived, ct).ConfigureAwait(false);
            }
            else
            {
                Ui.Info("그냥 둡니다.");
            }
        }

        return inventory;
    }

    /// <summary>
    /// 창에서 정한 것을 그대로 실행한다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>지우기를 맨 뒤로 미룬다.</b> 중간에 끊기는 일은 늘 있고, 그때 되돌릴 수 있는 것부터
    /// 되어 있어야 한다. 이름 바꾸기와 보관은 다시 돌리면 되지만 지우기는 그렇지 않다.
    /// </para>
    /// <para>
    /// 실행은 콘솔에 그대로 찍는다. 창은 이미 닫혔고, 무엇을 했는지는 남아 있어야 한다 —
    /// 관리자가 나중에 "내가 뭘 지웠더라" 하고 되짚을 곳이 여기뿐이다.
    /// </para>
    /// </remarks>
    private async Task ApplyTidyAsync(
        M365Host host, List<ExistingGroup> inventory,
        IReadOnlyList<TidyDecision> decided, CancellationToken ct)
    {
        if (decided.Count == 0)
        {
            Console.WriteLine();
            Ui.Info("정리할 것을 고르지 않으셨습니다. 그대로 둡니다.");
            return;
        }

        Console.WriteLine();
        Ui.Info($"창에서 정하신 {decided.Count}개를 손봅니다.");

        foreach (var d in decided.OrderBy(x => x.Action == TidyAction.Delete ? 1 : 0))
        {
            Console.WriteLine();
            Ui.Plain($"      {d.Group.DisplayName}");

            switch (d.Action)
            {
                case TidyAction.Rename:
                    await RenameOneAsync(host, inventory, d.Group, d.NewName, ct).ConfigureAwait(false);
                    break;

                case TidyAction.Archive:
                    await ArchiveOneAsync(host, inventory, d.Group, d.NewName, ct).ConfigureAwait(false);
                    break;

                case TidyAction.Delete:
                    await DeleteOneAsync(host, inventory, d.Group, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>이름만 바꾼다. 안의 파일·대화는 그대로 남는다.</summary>
    private static async Task RenameOneAsync(
        M365Host host, List<ExistingGroup> inventory,
        ExistingGroup g, string newName, CancellationToken ct)
    {
        var r = await host.CallAsync("Rename-TeavelM365Group", new Dictionary<string, object?>
        {
            ["Identity"] = g.MailNickname,
            ["NewDisplayName"] = newName,
        }, ct: ct).ConfigureAwait(false);

        if (!r.Ok) { Ui.Error(r.Message); Ui.Details(r.Details); return; }

        Ui.Ok(r.Message);
        Ui.Details(r.Details);

        // 대조는 이름으로 하므로, 바꾼 이름을 재고에 곧바로 반영해야
        // 아래에서 같은 이름을 또 만들지 않는다.
        var i = inventory.IndexOf(g);
        if (i >= 0) inventory[i] = g with { DisplayName = newName };
    }

    /// <summary>지운다. 되돌릴 수 없다 — 부르기 전에 사람이 한 번 더 확인한 뒤라야 한다.</summary>
    private static async Task DeleteOneAsync(
        M365Host host, List<ExistingGroup> inventory, ExistingGroup g, CancellationToken ct)
    {
        var r = await host.CallAsync("Remove-TeavelM365Group", new Dictionary<string, object?>
        {
            ["Identity"] = g.MailNickname,
            ["Confirmed"] = true,
        }, ct: ct).ConfigureAwait(false);

        if (r.Ok) { Ui.Ok(r.Message); inventory.Remove(g); }
        else { Ui.Error(r.Message); Ui.Details(r.Details); }
    }

    /// <summary>이름 앞에 연도를 붙이고 학생만 내보낸다. 팀과 자료는 그대로 둔다.</summary>
    private async Task ArchiveOneAsync(
        M365Host host, List<ExistingGroup> inventory,
        ExistingGroup g, string archived, CancellationToken ct)
    {
        if (!await EnsureTeamsAsync(host, ct).ConfigureAwait(false)) return;

        var r = await host.CallAsync("Rename-TeavelM365Group", new Dictionary<string, object?>
        {
            ["Identity"] = g.MailNickname,
            ["NewDisplayName"] = archived,
        }, ct: ct).ConfigureAwait(false);

        if (!r.Ok) { Ui.Error(r.Message); Ui.Details(r.Details); return; }
        Ui.Ok(r.Message);

        var i = inventory.IndexOf(g);
        if (i >= 0) inventory[i] = g with { DisplayName = archived };

        if (g.GroupId.Length == 0)
        {
            Ui.Warn("이름은 바꿨지만 학생을 내보내지 못했습니다(팀 id 를 모릅니다).");
            return;
        }

        var outed = await host.CallAsync("Remove-TeavelTeamStudent", new Dictionary<string, object?>
        {
            ["GroupId"] = g.GroupId,
        }, timeout: TimeSpan.FromMinutes(10), ct: ct).ConfigureAwait(false);

        if (outed.Ok) { Ui.Ok(outed.Message); Ui.Details(outed.Details); }
        else { Ui.Error(outed.Message); Ui.Details(outed.Details); }

        Ui.Dim("      팀과 그 안의 파일·대화는 그대로 있습니다. 담당 선생님은 계속 볼 수 있습니다.");
    }

    /// <summary>만든 날의 연도. 모르면 빈 문자열.</summary>
    /// <remarks>
    /// 지난 학년도 팀을 보관할 때 이름 앞에 붙일 연도다. 지금 연도가 아니라
    /// <b>그 팀이 만들어진 연도</b>를 쓴다 — 재작년 것을 올해 연도로 붙이면 거짓이 된다.
    /// </remarks>
    internal static string YearOf(ExistingGroup g)
    {
        var c = g.Created.Trim();
        return c.Length >= 4 && int.TryParse(c[..4], out var y) && y is > 2000 and < 2100
            ? y.ToString()
            : "";
    }

    // ──────────────────────────── 만들기 ────────────────────────────

    private async Task<int> CreateMissingAsync(
        M365Host host, IReadOnlyList<DeclaredGroup> groups,
        IReadOnlyList<ExistingGroup> inventory, CancellationToken ct)
    {
        Ui.Title("⑥ 모자란 것 만들기");

        var plan = TreeReconciler.Plan(groups, inventory);
        Ui.Info(TreeReconciler.Summarize(plan));

        // 보안 그룹은 Graph 가 필요해 아직 못 만든다(고급). 만들 것에 섞어 두면
        // "만들 것 1개" 라고 해 놓고 아무것도 안 만드는 꼴이 되므로 여기서 갈라 놓는다.
        var security = plan.Where(p => p.Action == PlanAction.Create
                                    && p.Declared.Kind == GroupKind.Security).ToList();
        var toCreate = plan.Where(p => p.Action == PlanAction.Create
                                    && p.Declared.Kind != GroupKind.Security).ToList();
        var conflicts = plan.Where(p => p.Action == PlanAction.Conflict).ToList();

        if (security.Count > 0)
        {
            Console.WriteLine();
            Ui.Warn($"보안 그룹 {security.Count}개는 Teavel 이 아직 만들지 못합니다. 관리 센터에서 손으로 만들어 주세요.");
            foreach (var s in security) Ui.Plain($"        {s.Declared.DisplayName}");
        }

        if (conflicts.Count > 0)
        {
            Console.WriteLine();
            Ui.Warn($"사람이 봐야 할 것이 {conflicts.Count}개 있습니다. 이것들은 건너뜁니다.");
            foreach (var c in conflicts)
                Ui.Plain($"        {c.Declared.DisplayName}   ({c.Reason})");
        }

        // 이미 있는 팀에도 선언한 채널이 다 있어야 한다.
        // 만들 것이 없을 때도 반드시 해야 한다 — 팀은 다 만들어졌는데 채널에서 끊긴 실행을
        // 다시 돌리면 여기가 유일한 복구 지점이다. 이것을 만들기 뒤에만 두었더니
        // "만들 것이 없습니다" 로 먼저 나가면서 영영 채워지지 않았다.
        await SyncExistingChannelsAsync(host, plan, ct).ConfigureAwait(false);

        if (toCreate.Count == 0)
        {
            Console.WriteLine();
            Ui.Ok("만들 것이 없습니다. 선언한 대로 이미 다 있습니다.");
            return 0;
        }

        Console.WriteLine();
        Ui.Dim($"      다음 {toCreate.Count}개를 만듭니다.");
        foreach (var p in toCreate)
        {
            var ch = p.Declared.Channels.Count > 0 ? $"  + 채널 {p.Declared.Channels.Count}개" : "";
            Ui.Plain($"        {KindName(p.Declared.Kind)}  {p.Declared.DisplayName}   [{p.Declared.MailNickname}]{ch}");
        }

        Console.WriteLine();
        if (!_assumeYes && !Ui.Confirm("      만들까요?"))
        {
            Ui.Info("아무것도 만들지 않았습니다.");
            return 0;
        }

        // 여기서부터가 실제로 바꾸는 일이다. 팀 로그인은 여기까지 미뤄 두었다.
        if (toCreate.Any(p => p.Declared.Kind == GroupKind.Team)
            && !await EnsureTeamsAsync(host, ct).ConfigureAwait(false))
        {
            Ui.Warn("팀에 붙지 못해 만들기를 멈춥니다. 그룹은 그대로 있습니다.");
            return 1;
        }

        var made = 0;
        var failed = new List<string>();

        foreach (var p in toCreate)
        {
            var d = p.Declared;

            var r = await host.CallAsync("New-TeavelM365Group", new Dictionary<string, object?>
            {
                ["DisplayName"] = d.DisplayName,
                ["MailNickname"] = d.MailNickname,
                ["Description"] = d.Description,
                ["Kind"] = d.Kind == GroupKind.Team ? "team" : "m365",
                ["Template"] = d.Template,
                ["Visibility"] = d.Visibility,
            }, timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false);

            if (!r.Ok)
            {
                Ui.Error($"{d.DisplayName} — {r.Message}");
                failed.Add(d.DisplayName);
                continue;
            }

            Ui.Ok(r.Message);
            made++;

            // 팀을 만든 직후에 채널을 붙인다. 만들기 결과에 실려 온 id 로만 갈 수 있다 —
            // 방금 만든 것은 재고에 아직 없다.
            if (d.Channels.Count > 0 && ExtractGroupId(r.Details) is { Length: > 0 } gid)
                await SyncChannelsAsync(host, gid, d, ct).ConfigureAwait(false);
        }



        Console.WriteLine();
        Ui.Info($"{made}개를 만들었습니다.");
        if (failed.Count > 0)
        {
            Ui.Warn($"{failed.Count}개는 만들지 못했습니다: {string.Join(", ", failed)}");
            Ui.Dim("      다시 실행하면 만들어진 것은 건너뛰고 못 만든 것만 다시 시도합니다.");
        }

        if (made > 0) ShowActivationNotice();

        return failed.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// 이미 있는 팀들의 채널을 선언대로 맞춘다.
    /// </summary>
    /// <remarks>
    /// 채널이 이미 다 있으면 아무 말도 하지 않는다 — 팀이 스무 개면 스무 줄이
    /// "이미 다 있습니다" 로 채워져 정작 봐야 할 줄이 묻힌다.
    /// </remarks>
    private static async Task SyncExistingChannelsAsync(
        M365Host host, IReadOnlyList<PlanItem> plan, CancellationToken ct)
    {
        var targets = plan.Where(p => p.Action == PlanAction.Skip
                                   && p.Declared.Channels.Count > 0
                                   && p.Existing is { IsTeam: true, GroupId.Length: > 0 }).ToList();
        if (targets.Count == 0) return;

        var added = 0;
        foreach (var p in targets)
        {
            var r = await host.CallAsync("Sync-TeavelTeamChannel", new Dictionary<string, object?>
            {
                ["GroupId"] = p.Existing!.GroupId,
                ["Channels"] = p.Declared.Channels,
            }, timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false);

            if (!r.Ok)
            {
                Ui.Warn($"      {p.Declared.DisplayName} 의 채널 — {r.Message}");
                continue;
            }

            // 이미 다 있으면 조용히 넘어간다. 모자라서 채운 것만 말한다.
            if (r.Message.Contains("만들었습니다", StringComparison.Ordinal))
            {
                Console.WriteLine();
                Ui.Ok($"{p.Declared.DisplayName} — {r.Message}");
                added++;
            }
        }

        if (added > 0)
            Ui.Dim($"      이미 있던 팀 {added}개에 모자란 채널을 채웠습니다.");
    }

    /// <summary>팀 하나의 채널을 선언대로 맞춘다. 실패해도 다음 팀으로 넘어간다.</summary>
    private static async Task SyncChannelsAsync(
        M365Host host, string groupId, DeclaredGroup d, CancellationToken ct)
    {
        var r = await host.CallAsync("Sync-TeavelTeamChannel", new Dictionary<string, object?>
        {
            ["GroupId"] = groupId,
            ["Channels"] = d.Channels,
        }, timeout: TimeSpan.FromMinutes(5), ct: ct).ConfigureAwait(false);

        // 채널을 못 만들어도 팀은 이미 있다. 여기서 멈추면 나머지 팀이 통째로 밀린다 —
        // 다시 돌리면 모자란 채널만 채워지므로 말만 하고 넘어간다.
        if (r.Ok) Ui.Dim($"        └ {d.DisplayName}: {r.Message}");
        else Ui.Warn($"      {d.DisplayName} 의 채널 — {r.Message}");
    }

    /// <summary>만들기 결과에 실려 온 그룹 id. 없으면 빈 문자열.</summary>
    internal static string ExtractGroupId(IEnumerable<string> details)
    {
        foreach (var line in details)
        {
            if (line.StartsWith("GROUPID\t", StringComparison.Ordinal))
                return line["GROUPID\t".Length..].Trim();
        }
        return "";
    }

    /// <summary>
    /// 만들고 나서 반드시 해야 할 것을 알린다.
    /// </summary>
    /// <remarks>
    /// PowerShell 로 만든 팀은 <b>활성화하기 전까지 학생에게 보이지 않고</b>,
    /// 채널도 기본이 접힌 상태다. 이것을 말하지 않으면 관리자는 다 됐다고 생각하는데
    /// 선생님들은 "아무것도 안 보인다" 고 한다 — 만든 팀 수만큼 문의가 온다.
    /// </remarks>
    private static void ShowActivationNotice()
    {
        Console.WriteLine();
        Ui.Warn("아직 한 가지가 남았습니다. 이것을 안 하면 학생에게 보이지 않습니다.");
        // 여는 따옴표 아래 줄들의 들여쓰기는 닫는 따옴표 위치를 기준으로 깎인다.
        // 화면에 여백을 남기려면 닫는 자리보다 더 들여써야 한다.
        Ui.Plain("""
              ① 팀 활성화
                 PowerShell 로 만든 팀은 잠자는 상태입니다.
                 담당 선생님이 Teams 앱에서 그 팀을 한 번 열고 [활성화] 를 누르면
                 그때부터 학생에게 보입니다.

              ② 채널 표시
                 새로 만든 채널은 접혀 있습니다.
                 채널 이름 옆 [...] → [표시] 를 누르면 목록에 나옵니다.

              둘 다 담당 선생님이 각자 하시는 일입니다.
              만들었다고 알리실 때 이 두 가지를 함께 알려 주세요.
        """);
        Console.WriteLine();
        Ui.Dim("      Teams 와 아웃룩에 반영되기까지 몇 분 걸릴 수 있습니다.");
    }

    // ──────────────────────────── 구성원 ────────────────────────────

    /// <summary>
    /// 명단을 받아 학생들을 반 팀에 넣는다. <b>명단과 테넌트가 만나는 자리다.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 여기까지 둘은 따로 놀았다 — 명단은 파일만 읽고 끝났고 M365 는 테넌트만 봤다.
    /// 그런데 관리자가 실제로 하고 싶은 일은 <b>이 반 학생들을 저 팀에 넣는 것</b> 하나다.
    /// </para>
    /// <para>
    /// 그래서 별도 명령으로 두지 않고 이 흐름 안에 넣었다. 관리자는
    /// <c>teavel m365</c> 하나만 알면 끝까지 간다 — 명단 명령이 따로 있다는 것을
    /// 알아내야 한다면 그 자리에서 막힌다.
    /// </para>
    /// </remarks>
    private async Task AddMembersAsync(
        M365Host host, IReadOnlyList<DeclaredGroup> groups, RosterResult roster, CancellationToken ct)
    {
        Ui.Title("⑦ 학생 넣기");

        // 대조를 다시 한다 — 방금 만든 팀이 재고에 반영돼 있어야 하기 때문이다.
        // 이건 메일·그룹 쪽이라 팀 로그인 없이 된다.
        var fresh = await ReadInventoryAsync(host, ct, quiet: true).ConfigureAwait(false);
        if (fresh is null) return;

        var plan = TreeReconciler.Plan(groups, fresh);

        // 넣을 팀이 하나도 없으면 로그인을 시키지 않는다.
        // 할 일도 없는데 두 번째 로그인을 요구하는 것은 그 자체로 벽이다.
        var dry = MemberPlanner.Plan(roster.Rows, plan,
            new Dictionary<string, IReadOnlyList<TeamMember>>(StringComparer.OrdinalIgnoreCase));

        if (dry.All(a => a.Team is null))
        {
            Console.WriteLine();
            Ui.Info("명단의 반과 맞는 팀이 없어 넣을 곳이 없습니다.");
            foreach (var a in dry.Where(a => a.Problem.Length > 0).Take(5))
                Ui.Dim($"      {a.ClassKey} — {a.Problem}");
            return;
        }

        if (!await EnsureTeamsAsync(host, ct).ConfigureAwait(false))
        {
            Ui.Warn("팀에 붙지 못해 학생을 넣지 못합니다. 팀은 그대로 있습니다.");
            return;
        }

        // 이미 들어 있는 사람을 알아야 여러 번 돌려도 안전하다.
        var teams = plan.Where(p => p.Existing is { IsTeam: true, GroupId.Length: > 0 })
                        .Select(p => p.Existing!.GroupId).Distinct().ToList();

        var have = new Dictionary<string, IReadOnlyList<TeamMember>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in teams)
        {
            var r = await host.CallAsync("Get-TeavelTeamMember",
                new Dictionary<string, object?> { ["GroupId"] = id },
                timeout: TimeSpan.FromMinutes(2), ct: ct).ConfigureAwait(false);
            have[id] = r.Ok ? ParseMembers(r.Details) : Array.Empty<TeamMember>();
        }

        var assignments = MemberPlanner.Plan(roster.Rows, plan, have);

        Console.WriteLine();
        Ui.Info(MemberPlanner.Summarize(assignments));
        Console.WriteLine();

        foreach (var a in assignments)
        {
            if (a.Problem.Length > 0) { Ui.Warn($"{a.ClassKey} — {a.Problem}"); continue; }

            var already = a.Already > 0 ? $"  (이미 {a.Already}명 들어 있음)" : "";
            if (a.ToAdd.Count == 0) Ui.Dim($"      {a.ClassKey}  →  넣을 사람 없음{already}");
            else Ui.Plain($"        {a.ClassKey}  →  {a.Team!.DisplayName}   {a.ToAdd.Count}명{already}");
        }

        // 학생을 넣든 안 넣든 담임은 정해야 한다. 여기서 먼저 나가면 담임 단계가 통째로 건너뛰어진다 —
        // 채널 맞추기에서 똑같은 실수를 한 적이 있다. 일찍 나가는 길마다 뒷단계를 잃는다.
        var total = assignments.Sum(a => a.ToAdd.Count);

        if (total == 0)
        {
            Console.WriteLine();
            Ui.Ok("넣을 사람이 없습니다. 이미 다 들어 있습니다.");
            await AssignOwnersAsync(host, assignments, have, ct).ConfigureAwait(false);
            return;
        }

        // 여기까지는 '전부 넣거나 전부 안 넣거나' 둘뿐이었다. 한 반만 빼거나 전학 간 아이
        // 하나를 빼려면 명단 파일을 고쳐 다시 돌리는 수밖에 없었는데, 그건 관리자가 할 일이 아니다.
        // 반 하나만 빼거나 전학 간 아이 하나를 빼는 것은 관리 화면(teavel 관리센터)에서 한다.
        List<MemberPick>? picks = null;

        if (picks is null)
        {
            Console.WriteLine();
            if (!_assumeYes && !Ui.Confirm($"      {total}명을 넣을까요?"))
            {
                Ui.Info("넣지 않았습니다.");
                await AssignOwnersAsync(host, assignments, have, ct).ConfigureAwait(false);
                return;
            }

            picks = assignments.Where(x => x.CanApply)
                               .Select(a => new MemberPick(a.ClassKey, a.Team!, a.ToAdd))
                               .ToList();
        }

        if (picks.Count == 0)
        {
            Console.WriteLine();
            Ui.Info("넣기로 고르신 사람이 없습니다.");
            await AssignOwnersAsync(host, assignments, have, ct).ConfigureAwait(false);
            return;
        }

        var added = 0;
        var failed = 0;

        foreach (var p in picks)
        {
            var r = await host.CallAsync("Add-TeavelTeamMember", new Dictionary<string, object?>
            {
                ["GroupId"] = p.Team.GroupId,
                ["Users"] = p.People.Select(x => x.Upn).ToList(),
                ["Role"] = "Member",
            }, timeout: TimeSpan.FromMinutes(10), ct: ct).ConfigureAwait(false);

            if (r.Ok)
            {
                Ui.Ok($"{p.ClassKey} — {r.Message}");
                added += p.People.Count - r.Details.Count(d => d.StartsWith("실패:", StringComparison.Ordinal));
            }
            else
            {
                Ui.Error($"{p.ClassKey} — {r.Message}");
                failed++;
            }

            foreach (var d in r.Details.Where(d => d.StartsWith("실패:", StringComparison.Ordinal)).Take(3))
                Ui.Dim($"      {d}");
        }

        Console.WriteLine();
        Ui.Info($"{added}명을 넣었습니다.");
        if (failed > 0) Ui.Warn($"{failed}개 반은 넣지 못했습니다. 다시 실행하면 못 넣은 사람만 다시 시도합니다.");
        Ui.Dim("      학생 화면에 보이기까지 몇 분 걸릴 수 있습니다.");

        await AssignOwnersAsync(host, assignments, have, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 반마다 담임 선생님을 팀 소유자로 넣는다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 학생만 넣고 끝내면 <b>주인 없는 팀</b>이 된다. 담당 선생님이 팀 설정을 바꾸거나
    /// 과제를 낼 수 없고, 관리자가 반마다 대신 해 주게 된다.
    /// </para>
    /// <para>
    /// 선생님은 명단이 필요 없다 — 계정이 이미 있으므로 <b>성함만 받아 찾는다.</b>
    /// 관리자에게 아이디를 묻지 않는다. 아이디는 몰라도 같이 근무하는 선생님 이름은 안다.
    /// </para>
    /// <para>
    /// 반이 스무 개면 스무 번 묻게 되므로 <b>그냥 Enter 로 건너뛸 수 있게</b> 한다.
    /// 지금 모르는 반은 나중에 다시 실행하면 된다 — 이미 소유자인 분은 건너뛴다.
    /// </para>
    /// </remarks>
    private async Task AssignOwnersAsync(
        M365Host host,
        IReadOnlyList<ClassAssignment> assignments,
        IReadOnlyDictionary<string, IReadOnlyList<TeamMember>> have,
        CancellationToken ct)
    {
        var teams = assignments.Where(a => a.Team is { GroupId.Length: > 0 }).ToList();
        if (teams.Count == 0 || _assumeYes) return;

        // 이미 소유자가 있는 반은 묻지 않는다.
        var need = teams.Where(a =>
            !have.TryGetValue(a.Team!.GroupId, out var m)
            || !m.Any(x => x.Role.Equals("Owner", StringComparison.OrdinalIgnoreCase))).ToList();

        Console.WriteLine();
        Ui.Title("⑧ 담임 선생님");

        if (need.Count == 0)
        {
            Ui.Ok("모든 반에 담임 선생님이 이미 지정돼 있습니다.");
            return;
        }

        Ui.Plain($"""
              담임 선생님이 없는 반이 {need.Count}개 있습니다.

              담임을 정해 두면 그 선생님이 팀 설정을 바꾸고 과제를 낼 수 있습니다.
              정해 두지 않으면 그 일을 관리자가 반마다 대신 해야 합니다.

              성함만 적어 주시면 계정은 Teavel 이 찾습니다.
              모르는 반은 그냥 Enter 로 넘기시고, 나중에 다시 실행하시면 됩니다.
        """);

        Console.WriteLine();
        if (!Ui.Confirm("      지금 정하시겠습니까?")) { Ui.Info("나중에 하셔도 됩니다."); return; }

        // 선생님을 찾으려면 학교 사람 목록이 있어야 한다.
        var people = await ReadPeopleAsync(host, ct).ConfigureAwait(false);
        if (people.Count == 0)
        {
            Ui.Warn("학교 사람 목록을 읽지 못해 선생님을 찾을 수 없습니다.");
            return;
        }

        var faculty = UserDirectory.GuessFaculty(UserDirectory.Cluster(people))?.Bundle;
        var done = 0;


        foreach (var a in need)
        {
            Console.WriteLine();
            var typed = (Ui.Ask($"      {a.ClassKey} 담임 선생님 성함 (모르면 Enter): ") ?? "").Trim();
            if (typed.Length == 0) { Ui.Dim("      넘어갑니다."); continue; }

            var found = TeacherFinder.Find(people, typed, faculty);

            if (found.Matches.Count == 0)
            {
                if (found.Students.Count > 0)
                    Ui.Warn($"'{typed}' 은(는) 학생 계정으로만 나옵니다. 성함을 다시 확인해 주세요.");
                else
                    Ui.Warn($"'{typed}' 으로 찾은 선생님이 없습니다. 성만 넣어 보셔도 됩니다.");
                continue;
            }

            var who = found.Matches[0];

            // 한 사람으로 안 좁혀지면 넘겨짚지 않는다 — 엉뚱한 선생님이 남의 반 주인이 된다.
            if (!TeacherFinder.IsCertain(found.Matches))
            {
                Ui.Warn("같은 이름이 여럿입니다. 골라 주세요.");
                for (var i = 0; i < Math.Min(found.Matches.Count, 5); i++)
                    Ui.Plain($"        [{i + 1}] {found.Matches[i].User.DisplayName}   {found.Matches[i].User.Upn}");

                var p2 = (Ui.Ask("        번호 (그냥 Enter 면 건너뜀): ") ?? "").Trim();
                if (!int.TryParse(p2, out var n2) || n2 < 1 || n2 > Math.Min(found.Matches.Count, 5))
                { Ui.Dim("      넘어갑니다."); continue; }
                who = found.Matches[n2 - 1];
            }

            var r = await host.CallAsync("Add-TeavelTeamMember", new Dictionary<string, object?>
            {
                ["GroupId"] = a.Team!.GroupId,
                ["Users"] = new[] { who.User.Upn },
                ["Role"] = "Owner",
            }, timeout: TimeSpan.FromMinutes(2), ct: ct).ConfigureAwait(false);

            if (r.Ok) { Ui.Ok($"{a.ClassKey} 담임 — {who.User.DisplayName} ({who.User.Upn})"); done++; }
            else { Ui.Error($"{a.ClassKey} — {r.Message}"); Ui.Details(r.Details); }
        }

        Console.WriteLine();
        Ui.Info($"담임 {done}명을 지정했습니다.");
        if (done < need.Count)
            Ui.Dim($"      {need.Count - done}개 반은 나중에 다시 실행해서 정하시면 됩니다.");
    }

    /// <summary>
    /// 그 팀의 소유자 이름. 없으면 빈 문자열.
    /// </summary>
    /// <remarks>
    /// 테넌트가 주는 것은 아이디뿐이라 사람 목록에서 이름을 찾아 붙인다. 못 찾으면
    /// <b>아이디를 그대로 보여 준다</b> — '있음' 이라고만 하면 누구인지 모르고,
    /// 관리자가 알고 싶은 것은 바로 그 누구인지다.
    /// </remarks>
    private static string OwnerNameOf(
        string groupId,
        IReadOnlyDictionary<string, IReadOnlyList<TeamMember>> have,
        IReadOnlyList<TenantUser> people)
    {
        if (!have.TryGetValue(groupId, out var members)) return "";

        var owners = members
            .Where(m => m.Role.Equals("Owner", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (owners.Count == 0) return "";

        var first = owners[0].Upn;
        var who = people.FirstOrDefault(p => p.Upn.Equals(first, StringComparison.OrdinalIgnoreCase));
        var name = who?.DisplayName is { Length: > 0 } d ? d : first;

        return owners.Count > 1 ? $"{name} 외 {owners.Count - 1}명" : name;
    }

    /// <summary>학교 사람 목록을 읽는다. 실패하면 빈 목록.</summary>
    private static async Task<IReadOnlyList<TenantUser>> ReadPeopleAsync(M365Host host, CancellationToken ct)
    {
        var res = await host.CallAsync("Get-TeavelTenantUser",
            timeout: TimeSpan.FromMinutes(10), ct: ct).ConfigureAwait(false);
        return res.Ok ? UserDirectory.Parse(res.Details) : Array.Empty<TenantUser>();
    }

    /// <summary>
    /// 명단을 받는다. <b>학생 목록이면서 동시에 학교 구조의 출처다.</b>
    /// </summary>
    /// <remarks>
    /// 처음에는 만들기 뒤에 두었는데 순서가 틀렸다. 명단에는 이 학교가 몇 학년 몇 반까지
    /// 있는지가 들어 있고, 그것을 알아야 무엇을 만들지 정할 수 있다.
    /// </remarks>
    private async Task<RosterResult?> AskRosterAsync(CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        Ui.Title("④ 명단");

        if (_assumeYes)
        {
            Ui.Info("자동 모드에서는 명단을 받지 않습니다. 파일은 사람이 골라야 합니다.");
            return null;
        }

        Ui.Plain("""
              학생 명단이 있으면 훨씬 많은 것을 대신 해 드릴 수 있습니다.
              몇 학년 몇 반까지 있는지도 명단을 보면 알 수 있어, 따로 여쭙지 않아도 됩니다.

              엑셀·한셀·한글·csv 어느 것이든 됩니다. 양식은 맞추지 않으셔도 됩니다.
        """);

        var pick = Ui.Choose("고르세요", new[]
        {
            new Ui.Choice("1", "[1] 명단 파일이 있습니다", "있", "가지고", "줄게", "여기"),
            new Ui.Choice("2", "[2] 없습니다 — 팀만 만들겠습니다", "없", "나중", "팀만", "안가져", "몰라"),
        }, "1");

        if (pick != "1")
        {
            Ui.Info("적어 두신 학교 구조대로 팀만 만들겠습니다.");
            return null;
        }

        Ui.Dim("      파일을 이 창에 끌어다 놓으시면 경로가 적힙니다.");
        var path = (Ui.Ask("      명단 파일: ") ?? "").Trim().Trim('"');

        if (path.Length == 0 || !File.Exists(path))
        {
            Ui.Warn(path.Length == 0 ? "파일을 받지 못했습니다." : $"그 자리에 파일이 없습니다: {path}");
            Ui.Dim("      명단 없이 이어 갑니다. 적어 두신 학교 구조대로 팀만 만듭니다.");
            return null;
        }

        return ReadRoster(path);
    }

    /// <summary>
    /// 명단에서 읽은 학교 모양으로 반 팀 선언을 만든다. 명단이 없으면 선언 파일을 그대로 쓴다.
    /// </summary>
    /// <remarks>
    /// <c>catalog/m365-tree.json</c> 을 관리자가 고칠 리 없다 — 무엇을 적어야 하는지도 모르고,
    /// 적으라고 하는 순간 그 자리에서 막힌다. 그런데 그럴 필요가 없다.
    /// <b>명단에 이미 들어 있다.</b>
    ///
    /// 다만 짐작한 것은 반드시 보여 주고 승낙받는다 — 명단이 한 학년 것만 있을 수도 있고,
    /// 그때 나머지 학년을 없는 것으로 치면 안 되기 때문이다.
    /// </remarks>
    private IReadOnlyList<DeclaredGroup> ShapeFromRoster(SchoolTree tree, RosterResult? roster)
    {
        if (roster is null) return tree.Groups;

        var shape = SchoolShape.Read(roster.Rows);
        if (shape.Classes.Count == 0)
        {
            Ui.Dim("      명단에서 학년·반을 읽어 내지 못해, 적어 두신 학교 구조를 씁니다.");
            return tree.Groups;
        }

        var pattern = SchoolShape.FindClassPattern(tree);
        var classes = SchoolShape.ToDeclarations(shape, pattern);

        Console.WriteLine();
        Ui.Ok($"명단을 보니 이 학교는 이렇습니다 — {shape.Describe()}");
        Console.WriteLine();

        foreach (var g in shape.Grades)
        {
            var row = shape.Classes.Where(c => c.Grade == g)
                .Select(c => $"{c.ClassNo}반({shape.HeadCount[c]}명)");
            Ui.Plain($"        {g}학년   {string.Join("  ", row)}");
        }

        Console.WriteLine();
        Ui.Dim($"      이대로면 팀 {classes.Count}개를 만들게 됩니다. 이름은 이렇습니다:");
        foreach (var c in classes.Take(3)) Ui.Plain($"        {c.DisplayName}   [{c.MailNickname}]");
        if (classes.Count > 3) Ui.Dim($"        … 그 밖에 {classes.Count - 3}개");

        Console.WriteLine();
        if (!_assumeYes && !Ui.Confirm("      이 구조가 맞습니까?"))
        {
            Ui.Info("적어 두신 학교 구조를 그대로 쓰겠습니다.");
            return tree.Groups;
        }

        // 반 팀만 갈아 끼운다. 교직원 그룹 같은 나머지 선언은 그대로 둔다.
        return SchoolShape.WithoutClasses(tree, pattern).Concat(classes).ToList();
    }

    /// <summary>명단 파일을 읽어 준다. 읽지 못하면 까닭을 말하고 null.</summary>
    private static RosterResult? ReadRoster(string path)
    {
        if (!TableReader.CanReadDirectly(path))
        {
            Ui.Warn($"'{Path.GetExtension(path)}' 파일은 아직 그대로 읽지 못합니다.");
            Ui.Dim("      엑셀·한셀은 [다른 이름으로 저장] 에서 CSV 나 xlsx 로,");
            Ui.Dim("      한글은 HWPX 로 한 번 저장해 주시면 읽습니다.");
            return null;
        }

        Table table;
        try { table = TableReader.Read(path); }
        catch (Exception ex) { Ui.Error($"파일을 읽지 못했습니다: {ex.Message}"); return null; }

        var map = RosterMapper.Map(table.Rows);
        var guess = RosterExtractor.DetectIdFormat(table, map);
        var result = RosterExtractor.Extract(table, map, guess.Certain ? guess.Format : null);

        Console.WriteLine();
        Ui.Ok($"{table.Source} — 명단 {result.Rows.Count}줄");
        Ui.Details(RosterMapper.Explain(map));

        var bad = result.Bad.ToList();
        if (bad.Count > 0)
        {
            Console.WriteLine();
            Ui.Warn($"쓸 수 없는 줄이 {bad.Count}개 있습니다. 그 줄은 빼고 넣습니다.");
            foreach (var b in bad.Take(5))
                Ui.Plain($"        {b.Line}번째 줄 — {string.Join(" · ", b.Problems)}");
        }

        return result;
    }

    /// <summary>PowerShell 이 낸 구성원 줄들을 읽는다.</summary>
    internal static List<TeamMember> ParseMembers(IEnumerable<string> lines)
    {
        var members = new List<TeamMember>();
        foreach (var line in lines)
        {
            var f = line.Split('\t');
            if (f.Length < 2 || !string.Equals(f[0], "MEMBER", StringComparison.Ordinal)) continue;
            members.Add(new TeamMember(f[1].Trim(), f.Length > 2 ? f[2].Trim() : ""));
        }
        return members;
    }

    private static string KindName(GroupKind k) => k switch
    {
        GroupKind.Team => "팀  ",
        GroupKind.M365 => "그룹",
        _ => "보안",
    };
}
