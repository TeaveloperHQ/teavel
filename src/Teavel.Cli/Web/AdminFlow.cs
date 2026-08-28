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

    /// <summary>
    /// 이 자리에서 화면을 띄워도 되는지.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 화면이 <b>본길</b>이지만, 화면을 못 여는 자리가 있고 그때는 콘솔 흐름으로 간다.
    /// 기능을 못 쓰게 되는 것이 아니라 길이 하나 더 있는 것이다.
    /// </para>
    /// <list type="bullet">
    /// <item><b>입력이 파이프로 들어오면</b> 사람이 없다는 뜻이다. 가짜 테넌트 검증이
    ///       답을 흘려 넣는 방식이라 여기서 화면을 띄우면 그 검증이 통째로 막힌다.</item>
    /// <item><c>--yes</c> 도 사람이 없는 자리다.</item>
    /// <item><c>TEAVEL_NO_GUI</c> 는 원격 접속처럼 브라우저가 곤란한 자리를 위한 구멍.</item>
    /// </list>
    /// </remarks>
    public static bool Usable(bool assumeYes)
        => !assumeYes
        && Environment.UserInteractive
        && !Console.IsInputRedirected
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEAVEL_NO_GUI"));

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

        // 상주 세션이 흘려보내는 문구를 콘솔과 화면 둘 다에 적는다.
        // 브라우저만 보고 있는 관리자에게 로그인 안내가 닿아야 한다.
        void Sink(string line)
        {
            Ui.Plain(line);
            api?.Note(line);
        }

        M365Host host;
        try { host = await M365Host.StartAsync(shell, _tools.ScriptsDirectory, Sink, ct).ConfigureAwait(false); }
        catch (Exception ex) { Ui.Error(ex.Message); return 2; }

        // Graph 는 자기 세션에서 산다 — 까닭은 AdminApi.GraphAsync 에 적었다.
        // 처음 쓸 때 한 번 뜨고, 화면을 닫을 때 함께 닫힌다.
        var graphs = new List<M365Host>();

        async Task<M365Host> NewGraphHost(CancellationToken c)
        {
            var g = await M365Host.StartAsync(shell, _tools.ScriptsDirectory, Sink, c).ConfigureAwait(false);
            graphs.Add(g);
            return g;
        }

        // 상주 세션은 판 중간에 갈릴 수 있다(모듈을 깐 뒤 새로 띄운다).
        // using 으로 묶으면 처음 것만 붙잡혀 새 세션이 닫히지 않는다.
        try
        {
            api = new AdminApi(host, tree, server.Token, NewGraphHost);

            // 모듈 설치는 흐름 쪽 것을 그대로 쓴다. 여기서 '모자라니 teavel m365 를
            // 실행하세요' 라고 하면, 그 명령도 이 화면으로 오므로 제자리를 돈다.
            var check = await M365Flow.EnsureModulesAsync(host, assumeYes: false, ct).ConfigureAwait(false);
            if (check == M365Flow.ModuleCheck.Failed) return 2;

            if (check == M365Flow.ModuleCheck.Restart)
            {
                await host.DisposeAsync().ConfigureAwait(false);

                try { host = await M365Host.StartAsync(shell, _tools.ScriptsDirectory, Sink, ct).ConfigureAwait(false); }
                catch (Exception ex) { Ui.Error(ex.Message); return 2; }

                // 화면이 옛 세션을 들고 있으면 첫 명령부터 '끊어졌습니다' 가 된다.
                api = new AdminApi(host, tree, server.Token, NewGraphHost);

                if (!await M365Flow.ConfirmModulesAsync(host, ct).ConfigureAwait(false)) return 2;
            }

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
        finally
        {
            await host.DisposeAsync().ConfigureAwait(false);

            // Graph 세션도 함께 닫는다. 안 닫으면 화면을 닫아도 PowerShell 이 남는다.
            foreach (var g in graphs)
            {
                try { await g.DisposeAsync().ConfigureAwait(false); } catch { /* 닫는 길이다 */ }
            }
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
