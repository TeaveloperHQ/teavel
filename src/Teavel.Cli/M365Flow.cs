using Teavel.M365;
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

        // ① 정리가 먼저. 여기서 이름을 바꾼 것은 아래 대조에 곧바로 반영돼야 하므로
        //    바뀐 목록을 돌려받는다.
        inventory = await TidyAsync(host, inventory, ct).ConfigureAwait(false);

        // ② 그다음에 만들기.
        return await CreateMissingAsync(host, tree, inventory, ct).ConfigureAwait(false);
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

        // 팀을 만들 일이 없으면 Teams 로그인은 시키지 않는다 — 로그인 한 번도 벅찬 분들이다.
        var needsTeams = tree.Groups.Any(g => g.Kind == GroupKind.Team);

        var args = new Dictionary<string, object?> { ["TeamsToo"] = needsTeams };
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

    // ───────────────────────────── 재고 ─────────────────────────────

    private async Task<List<ExistingGroup>?> ReadInventoryAsync(M365Host host, CancellationToken ct)
    {
        Ui.Title("③ 지금 학교에 있는 것");

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
    /// 한 줄이 <c>GROUP\t이름\t별칭\t메일\t팀여부\t인원\t만든날\t공개범위</c> 꼴이다.
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

            groups.Add(new ExistingGroup(
                DisplayName: f[1], MailNickname: f[2], IsTeam: isTeam,
                MemberCount: members, Created: created, Origin: privacy));
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

    // ──────────────────────────── 정리 ────────────────────────────

    /// <summary>정리 후보를 하나씩 보여 주고 어떻게 할지 묻는다. 바뀐 재고를 돌려준다.</summary>
    private async Task<List<ExistingGroup>> TidyAsync(
        M365Host host, List<ExistingGroup> inventory, CancellationToken ct)
    {
        var candidates = InventoryTriage.Triage(inventory)
            .Where(t => t.Bucket == TriageBucket.Candidate)
            .ToList();

        if (candidates.Count == 0) return inventory;

        Ui.Title("④ 정리");
        Ui.Dim($"      정리해 볼 만한 것이 {candidates.Count}개 있습니다. 하나씩 여쭙겠습니다.");
        Console.WriteLine();
        Ui.Dim("      지우면 그 안의 파일과 대화가 함께 사라집니다.");
        Ui.Dim("      이름만 바꾸면 내용은 그대로 남습니다. 잘 모르겠으면 [3] 그냥 두기를 고르세요.");

        // 자동 응답으로 돌 때는 아무것도 지우지 않는다. 사람이 없는 자리에서
        // 파일이 딸린 그룹을 지우는 일이 벌어져서는 안 된다.
        if (_assumeYes)
        {
            Console.WriteLine();
            Ui.Info("자동 모드에서는 정리를 건너뜁니다. 지우기는 사람이 봐야 합니다.");
            return inventory;
        }

        foreach (var t in candidates)
        {
            var g = t.Group;
            Console.WriteLine();
            Ui.Warn($"{g.DisplayName}");
            Ui.Dim($"      {(g.IsTeam ? "팀" : "그룹")} · 구성원 {(g.MemberCount >= 0 ? g.MemberCount + "명" : "모름")}"
                 + (g.Created.Length > 0 ? $" · {g.Created} 에 만듦" : ""));
            if (t.Note.Length > 0) Ui.Dim($"      {t.Note}");

            Ui.Plain("        [1] 이름 바꿔서 그대로 두기   [2] 지우기   [3] 그냥 두기");
            var pick = (Ui.Ask("        고르세요 [3] ") ?? "3").Trim();
            if (pick.Length == 0) pick = "3";

            if (pick == "1")
            {
                var newName = (Ui.Ask("        새 이름: ") ?? "").Trim();
                if (newName.Length == 0) { Ui.Info("이름을 받지 못해 그냥 둡니다."); continue; }

                var r = await host.CallAsync("Rename-TeavelM365Group", new Dictionary<string, object?>
                {
                    ["Identity"] = g.MailNickname,
                    ["NewDisplayName"] = newName,
                }, ct: ct).ConfigureAwait(false);

                if (r.Ok)
                {
                    Ui.Ok(r.Message);
                    Ui.Details(r.Details);
                    // 대조는 이름으로 하므로, 바꾼 이름을 재고에 곧바로 반영해야
                    // 아래에서 같은 이름을 또 만들지 않는다.
                    var i = inventory.IndexOf(g);
                    if (i >= 0) inventory[i] = g with { DisplayName = newName };
                }
                else { Ui.Error(r.Message); Ui.Details(r.Details); }
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

                var r = await host.CallAsync("Remove-TeavelM365Group", new Dictionary<string, object?>
                {
                    ["Identity"] = g.MailNickname,
                    ["Confirmed"] = true,
                }, ct: ct).ConfigureAwait(false);

                if (r.Ok) { Ui.Ok(r.Message); inventory.Remove(g); }
                else { Ui.Error(r.Message); Ui.Details(r.Details); }
            }
            else
            {
                Ui.Info("그냥 둡니다.");
            }
        }

        return inventory;
    }

    // ──────────────────────────── 만들기 ────────────────────────────

    private async Task<int> CreateMissingAsync(
        M365Host host, SchoolTree tree, IReadOnlyList<ExistingGroup> inventory, CancellationToken ct)
    {
        Ui.Title("⑤ 모자란 것 만들기");

        var plan = TreeReconciler.Plan(tree.Groups, inventory);
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

        if (toCreate.Count == 0)
        {
            Console.WriteLine();
            Ui.Ok("만들 것이 없습니다. 선언한 대로 이미 다 있습니다.");
            return 0;
        }

        Console.WriteLine();
        Ui.Dim($"      다음 {toCreate.Count}개를 만듭니다.");
        foreach (var p in toCreate)
            Ui.Plain($"        {KindName(p.Declared.Kind)}  {p.Declared.DisplayName}   [{p.Declared.MailNickname}]");

        Console.WriteLine();
        if (!_assumeYes && !Ui.Confirm("      만들까요?"))
        {
            Ui.Info("아무것도 만들지 않았습니다.");
            return 0;
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

            if (r.Ok) { Ui.Ok(r.Message); made++; }
            else { Ui.Error($"{d.DisplayName} — {r.Message}"); failed.Add(d.DisplayName); }
        }

        Console.WriteLine();
        Ui.Info($"{made}개를 만들었습니다.");
        if (failed.Count > 0)
        {
            Ui.Warn($"{failed.Count}개는 만들지 못했습니다: {string.Join(", ", failed)}");
            Ui.Dim("      다시 실행하면 만들어진 것은 건너뛰고 못 만든 것만 다시 시도합니다.");
        }

        Console.WriteLine();
        Ui.Dim("      Teams 와 아웃룩에 보이기까지 몇 분 걸릴 수 있습니다.");
        return failed.Count == 0 ? 0 : 1;
    }

    private static string KindName(GroupKind k) => k switch
    {
        GroupKind.Team => "팀  ",
        GroupKind.M365 => "그룹",
        _ => "보안",
    };
}
