using System.Diagnostics;
using Teavel.M365;
using Teavel.Tools;

namespace Teavel.Cli.Web;

/// <summary>
/// <b>teavel 관리센터</b> — 학교 M365 를 브라우저에서 손본다.
///
/// <para>
/// <c>teavel m365</c> 는 처음부터 끝까지 한 줄로 가는 흐름이다. 그건 <b>처음 한 번</b>에 맞는
/// 모양이다 — 무엇을 해야 하는지 모르는 사람을 순서대로 데려가야 하니까. 그런데 학기 중에
/// 다시 열 때 필요한 것은 그게 아니다. <b>전학생 한 명을 넣는 것</b>, <b>담임 한 반을 고치는 것</b>,
/// <b>지난 학년도 팀 하나를 보관하는 것</b> — 그때마다 아홉 단계를 처음부터 지나갈 수는 없다.
/// </para>
/// <para>
/// 그래서 이 화면은 <b>단계가 없다.</b> 왼쪽에서 할 일을 고르고 그것만 한다.
/// </para>
/// <para>
/// <b>정식 관리 센터를 대신하려는 것이 아니다.</b> admin.microsoft.com 은 학교가 쓰지 않는
/// 것까지 다 들어 있어서, 그 앞에 앉으면 대개 겁을 먹고 아무것도 못 한다. 여기서는
/// <b>학교가 실제로 하는 일만</b> 보여 주고, 정식 관리 센터로 가는 길은 위에 적어만 둔다.
/// </para>
/// </summary>
public sealed class AdminFlow
{
    private readonly ToolRunner _tools;

    public AdminFlow(ToolRunner tools) => _tools = tools;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        Ui.Title("학교 M365 관리 화면");
        Ui.Dim("      브라우저에 화면을 띄웁니다. 이 창은 그동안 켜 두세요.");
        Ui.Dim("      전역 관리자 계정이 필요합니다.");

        var shell = _tools.FindPowerShell();
        if (shell is null) { Ui.Error("PowerShell 을 찾지 못했습니다."); return 2; }

        // 선언을 먼저 읽는다. 잘못돼 있으면 로그인까지 시켜 놓고 무를 수는 없다.
        var tree = SchoolTree.Load(AppContext.BaseDirectory);
        if (!tree.Ok)
        {
            Ui.Error($"학교 구조 선언에 문제가 있습니다. ({tree.Source})");
            Ui.Details(tree.Problems.Select(p => $"{p.Where} — {p.Problem}"));
            return 2;
        }

        AdminApi? api = null;

        await using var server = new LocalServer((ask, c) =>
            api is null ? Task.FromResult(HttpSay.Text(503, "아직 준비 중입니다.")) : api.HandleAsync(ask, c));

        M365Host host;
        try
        {
            // 상주 세션이 흘려보내는 문구를 콘솔과 화면 둘 다에 적는다.
            // 브라우저만 보고 있는 관리자에게 로그인 안내가 닿아야 한다.
            host = await M365Host.StartAsync(shell, _tools.ScriptsDirectory, line =>
            {
                Ui.Plain(line);
                api?.Note(line);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Ui.Error(ex.Message); return 2; }

        await using (host)
        {
            api = new AdminApi(host, tree, server.Token);

            if (!await ReadyAsync(host, ct).ConfigureAwait(false)) return 2;
            if (!await ConnectAsync(host, ct).ConfigureAwait(false)) return 2;

            Ui.Title("학교를 읽는 중");
            Ui.Dim("      그룹·팀과 사람 목록을 한 번 읽어 둡니다. 잠시 걸립니다.");
            await api.PrimeAsync(ct).ConfigureAwait(false);

            server.Start(ct);
            Open(server.Url);

            Ui.Title("화면이 열렸습니다");
            Ui.Plain($"      {server.Url}");
            Console.WriteLine();
            Ui.Dim("      브라우저가 안 열리면 위 주소를 그대로 붙여 넣으세요.");
            Ui.Dim("      이 컴퓨터에서만 열립니다. 다른 사람은 이 주소로 들어올 수 없습니다.");
            Console.WriteLine();
            Ui.Info("다 쓰시면 화면에서 [끝내기] 를 누르시거나, 이 창에서 Ctrl+C 를 누르세요.");

            await WaitAsync(api, ct).ConfigureAwait(false);

            Console.WriteLine();
            Ui.Ok("관리 화면을 닫았습니다.");
            return 0;
        }
    }

    private static async Task WaitAsync(AdminApi api, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !api.Finished)
                await Task.Delay(300, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* Ctrl+C */ }
    }

    private static async Task<bool> ReadyAsync(M365Host host, CancellationToken ct)
    {
        Ui.Title("① 준비 확인");

        var ready = await host.CallAsync("Get-TeavelM365Readiness", ct: ct).ConfigureAwait(false);
        Ui.Info(ready.Message);
        Ui.Details(ready.Details);

        if (ready.Ok) return true;

        Console.WriteLine();
        Ui.Warn("필요한 PowerShell 모듈이 갖춰지지 않았습니다.");
        Ui.Dim("      'teavel m365' 를 한 번 실행하시면 모듈을 대신 설치해 드립니다.");
        return false;
    }

    /// <remarks>
    /// 메일·그룹(Exchange)만 붙인다. 팀(Teams)은 <b>실제로 만들거나 사람을 넣을 때</b> 붙는다 —
    /// 처음부터 둘 다 붙이면 로그인 창이 연달아 두 번 뜨고, 두 번째 창이 뒤에 숨어
    /// 못 보고 지나치면 거기서 통째로 끝난다.
    /// </remarks>
    private static async Task<bool> ConnectAsync(M365Host host, CancellationToken ct)
    {
        Ui.Title("② 학교 계정으로 로그인");

        var res = await host.CallAsync("Connect-TeavelM365",
            new Dictionary<string, object?> { ["TeamsToo"] = false },
            timeout: TimeSpan.FromMinutes(20), ct: ct).ConfigureAwait(false);

        if (!res.Ok) { Ui.Error(res.Message); Ui.Details(res.Details); return false; }

        Ui.Ok(res.Message);
        Ui.Details(res.Details);
        return true;
    }

    /// <summary>기본 브라우저로 연다. 안 열려도 주소를 적어 두었으니 판이 끝나지는 않는다.</summary>
    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Ui.Warn("브라우저를 자동으로 열지 못했습니다.");
            Ui.Dim($"      {ex.Message}");
        }
    }
}
