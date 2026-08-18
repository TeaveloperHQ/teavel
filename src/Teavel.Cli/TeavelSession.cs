using Teavel.Apps;
using Teavel.Intent;
using Teavel.Mcp;
using Teavel.Model;
using Teavel.Platform;
using Teavel.Setup;
using Teavel.Tools;

namespace Teavel.Cli;

/// <summary>Teavel 한 번의 실행 — 모든 부품을 엮고 명령을 처리한다.</summary>
public sealed class TeavelSession : IAsyncDisposable
{
    private readonly ISystemPaths _paths;
    private readonly IProcessRunner _proc;
    private readonly ToolRunner _tools;
    private readonly ToolArgumentValidator _validator;
    private readonly SetupCatalog _setup;
    private readonly AppCatalog _apps;
    private readonly AppInstaller _installer;
    private readonly LayeredIntentRouter _router;
    private readonly LocalLlmIntentRouter? _llm;
    private readonly McpHub _mcp;
    private readonly PathRegistration _path;
    private readonly ExplorerRegistration _explorer;
    private readonly UninstallRegistration _uninstall;
    private readonly IRegistry _registry;

    /// <summary>확인 없이 바로 실행할지(--yes).</summary>
    public bool AssumeYes { get; init; }

    /// <summary>
    /// 시작할 때의 폴더(탐색기에서 --here 로 열었을 때 그 폴더).
    /// 폴더를 물어볼 때 기본값으로 쓴다 — 교사가 긴 경로를 치지 않아도 되게.
    /// </summary>
    public string? StartFolder { get; init; }

    public TeavelSession()
    {
        _paths = new SystemPaths();
        _proc = new ProcessRunner();

        _registry = OperatingSystem.IsWindows()
            ? new WindowsRegistry()
            : new InMemoryRegistry();   // 개발용 — Windows 가 아니면 아무것도 못 찾는 상태로 동작한다
        var registry = _registry;

        var facts = new WindowsFacts(registry, _paths);

        _tools = new ToolRunner(_proc, _paths);
        _validator = new ToolArgumentValidator(_paths);
        _apps = AppCatalog.Load(_paths);
        _installer = new AppInstaller(_proc, _paths);
        // 활성화는 교사의 승인을 기다리는 동안 말을 걸어야 해서 화면 통로를 넘긴다.
        _setup = new SetupCatalog(facts, _proc, _paths, _tools, _apps, _installer, Ui.Plain);
        _mcp = new McpHub(_apps, _installer);
        _path = new PathRegistration(registry, _paths);
        _explorer = new ExplorerRegistration(registry, _paths);
        _uninstall = new UninstallRegistration(registry);

        var modelPath = LocalLlmIntentRouter.FindModel(_paths);
        _llm = modelPath is null ? null : new LocalLlmIntentRouter(modelPath);

        // 모델을 처음 읽는 데 GPU 없는 교사 PC 에서 수십 초가 걸린다. 그동안 아무 말도
        // 없으면 <b>멈춘 것으로 보인다</b> — llama.cpp 이 쏟아내는 진단문마저 껐기 때문에
        // 화면이 정말 아무것도 안 움직인다. 한 줄이라도 먼저 띄운다.
        if (_llm is not null)
            _llm.OnLoading = note =>
            {
                // '> ' 프롬프트 바로 뒤라 줄을 바꾸지 않으면 교사가 친 말에 붙어 나온다.
                Console.WriteLine();
                Ui.Dim($"      {note}");
            };

        _router = new LayeredIntentRouter(new KeywordIntentRouter(), _llm);
    }

    // ─────────────────────────────── 점검 ───────────────────────────────

    /// <summary>기반 설정과 teaveloper 앱을 모두 진단한다.</summary>
    public async Task<int> RunCheckAsync(CancellationToken ct)
    {
        var needsFix = 0;
        var first = true;

        foreach (var stage in _setup.ByStage())
        {
            Ui.Title(SetupCatalog.StageName(stage.Key));

            foreach (var task in stage)
            {
                var result = await task.CheckAsync(ct).ConfigureAwait(false);
                Ui.Check(task.Title, result);
                if (result.State == CheckState.NeedsFix) needsFix++;

                // 계정이 안 돼 있으면 그 아래가 다 걸린다 — 여기서 한 번 짚어 준다.
                if (first && result.State == CheckState.NeedsFix)
                    Ui.Dim("        ↑ 이것부터 하시면 아래 것들이 대부분 함께 해결됩니다.");
                first = false;
            }
        }

        // 세팅 항목이 직접 다루는 앱(러너)은 위에서 이미 보여 줬으므로 뺀다.
        var extraApps = _apps.Apps.Where(a => !_setup.CoveredAppIds.Contains(a.Id)).ToList();

        Ui.Title("teaveloper 앱");
        if (_apps.Apps.Count == 0)
        {
            Ui.Info($"앱 카탈로그가 비어 있습니다. ({_apps.Source})");
        }
        else if (extraApps.Count == 0)
        {
            Ui.Dim("      (다른 앱은 아직 없습니다)");
        }
        else
        {
            foreach (var app in extraApps)
            {
                var result = _installer.Check(app);
                Ui.Check(app.Name, result);
                if (result.State == CheckState.NeedsFix) needsFix++;
            }
        }

        Console.WriteLine();
        if (needsFix == 0)
        {
            Ui.Ok("세팅이 다 돼 있습니다.");
        }
        else
        {
            Ui.Warn($"{needsFix}가지를 손봐야 합니다.");
            Ui.Dim("      'teavel 고침' 을 실행하면 순서대로 하나씩 도와드립니다.");
        }

        return needsFix == 0 ? 0 : 1;
    }

    /// <summary>손봐야 할 것들을 고친다. id 를 주면 그 항목만.</summary>
    public async Task<int> RunFixAsync(string? id, CancellationToken ct)
    {
        var targets = id is null
            ? _setup.All.ToList()
            : _setup.Find(id) is { } one ? new List<ISetupTask> { one } : new List<ISetupTask>();

        if (id is not null && targets.Count == 0)
        {
            // 설정 항목이 아니면 앱 id 로 본다.
            if (_apps.Find(id) is { } app)
            {
                Ui.Title(app.Name);
                Ui.Fix(app.Name, await _installer.InstallAsync(app, ct).ConfigureAwait(false));
                return 0;
            }
            Ui.Error($"'{id}' 라는 항목이 없습니다. 'teavel 점검' 으로 항목 이름을 확인해 주세요.");
            return 2;
        }

        Ui.Title("손보기");
        foreach (var task in targets)
        {
            var check = await task.CheckAsync(ct).ConfigureAwait(false);
            if (check.State is CheckState.Ok or CheckState.NotApplicable)
            {
                Ui.Check(task.Title, check);
                continue;
            }

            Console.WriteLine();
            Ui.Warn($"{task.Title} — {check.Summary}");
            Ui.Dim($"      {task.Why}");

            if (!AssumeYes && !Ui.Confirm($"      지금 손볼까요?"))
            {
                Ui.Info("건너뜁니다.");
                continue;
            }

            Ui.Fix(task.Title, await task.FixAsync(ct).ConfigureAwait(false));
        }
        return 0;
    }

    // ─────────────────────────────── Microsoft 365 ───────────────────────────────

    /// <summary>
    /// 학교 그룹·Teams 를 살펴보고 정리하고 만든다.
    /// </summary>
    /// <remarks>
    /// 다른 명령과 달리 <b>상주 PowerShell</b> 을 쓴다. 호출마다 새로 띄우면
    /// 명령 하나마다 브라우저 로그인이 다시 뜨기 때문이다 —
    /// 자세한 사정은 <see cref="M365.M365Host"/> 에 적어 두었다.
    /// </remarks>
    public Task<int> RunM365Async(CancellationToken ct)
        => new M365Flow(_tools, AssumeYes).RunAsync(ct);

    /// <summary>
    /// 형태소 분석기를 갖춘다. 이미 있으면 아무것도 하지 않는다.
    /// </summary>
    /// <remarks>
    /// 말을 알아듣는 일은 두 층으로 되어 있다. 형태소 분석기가 <b>'합쳐줘' 와 '합치기' 가
    /// 같은 말</b>임을 알려 주고, 언어 모델이 <b>아예 다른 말투</b>를 알아본다.
    /// 앞엣것이 훨씬 싸다 — 96MB 대 1GB.
    /// </remarks>
    private async Task EnsureMorphemesAsync(CancellationToken ct)
    {
        Ui.Title("형태소 분석기");

        if (KiwiInstaller.Ready(_paths))
        {
            Ui.Ok("이미 갖춰져 있습니다.");
            return;
        }

        Ui.Dim("      말의 끝바꿈을 알아보는 부품입니다. 두 개를 잇달아 받습니다.");
        Ui.Dim("      받는 양은 약 96MB, 다 풀면 270MB 를 씁니다. 한 번만 받습니다.");
        Ui.Dim("      이것만 있어도 '합쳐줘' 와 '합치기' 를 같은 말로 알아봅니다.");
        Console.WriteLine();

        try
        {
            var last = -1;
            var step = "";
            await KiwiInstaller.InstallAsync(_paths,
                progress: (done, total) =>
                {
                    if (total <= 0) return;
                    var pct = (int)(done * 100 / total);
                    if (pct == last) return;
                    last = pct;
                    // 언어 모델 쪽과 같은 방식으로 한 줄을 고쳐 그린다.
                    // 줄마다 새로 찍으면 10%씩 열한 줄이 쌓여 화면이 지저분해진다.
                    Console.Write($"\r      {step}  {pct,3}%  ({done / 1024 / 1024}MB / {total / 1024 / 1024}MB)   ");
                },
                onStep: name => { step = name; last = -1; Console.WriteLine(); },
                ct: ct).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine();
            Ui.Ok("형태소 분석기를 갖췄습니다.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 없어도 Teavel 은 돈다 — 예전 방식으로 알아들을 뿐이다. 여기서 멈추지 않는다.
            Ui.Warn($"형태소 분석기를 갖추지 못했습니다: {ex.Message}");
            Ui.Dim("      낱말로는 그대로 알아듣습니다. 나중에 다시 '모델' 을 치시면 재시도합니다.");
        }
    }

    /// <summary>
    /// 학교 계정을 이 컴퓨터의 Microsoft 앱들에 잇는다.
    /// </summary>
    /// <remarks>
    /// 학교에서 컴퓨터를 처음 세팅할 때 <b>가장 먼저 할 일</b>이라 따로 명령을 두었다.
    /// '점검' 은 열 가지를 늘어놓지만, 실제로는 계정 하나를 잇는 것이 그중 대부분을 해결한다.
    /// </remarks>
    public Task<int> RunAccountAsync(CancellationToken ct)
    {
        var facts = new WindowsFacts(_registry, _paths);
        var flow = new AccountFlow(
            facts, new EdgeFacts(_paths), new OneDriveDetail(facts, _paths),
            _tools, _proc, AssumeYes);

        return flow.RunAsync(ct);
    }

    /// <summary>선생님을 이름으로 찾아 계정을 알려 준다.</summary>
    public Task<int> RunFindTeacherAsync(string? name, CancellationToken ct)
        => M365Flow.FindTeacherAsync(_tools, name, ct);

    /// <summary>
    /// 등록할 실행 파일을 정한다. 못 정하면 빈 문자열.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 이름을 짐작하지 않는다. 포털이 배포하는 파일에는 판 번호가 붙어
    /// <c>teavel-0.1.0.exe</c> 처럼 오는데, 예전에는 <c>teavel.exe</c> 를 찾다가
    /// 교사가 처음 하는 일에서 바로 실패했다.
    /// </para>
    /// <para>
    /// 하나로 못 정하면 <b>물어본다.</b> 넘겨짚어 엉뚱한 파일을 등록하면
    /// 나중에 그 파일을 지웠을 때 왜 안 되는지 알 길이 없다.
    /// </para>
    /// </remarks>
    private string ResolveExe()
    {
        var found = TeavelExe.Find(_paths);

        if (found.Path.Length > 0)
        {
            WarnIfRisky(found.Path);
            return found.Path;
        }

        Console.WriteLine();
        if (found.Candidates.Count == 0)
        {
            Ui.Warn("Teavel 실행 파일을 찾지 못했습니다.");
            Ui.Dim($"      {found.How}");
            Console.WriteLine();
            Ui.Dim("      그 파일을 이 창에 끌어다 놓으시면 경로가 적힙니다.");

            var typed = (Ui.Ask("      Teavel 실행 파일: ") ?? "").Trim().Trim('"');
            if (typed.Length == 0 || !File.Exists(typed))
            {
                Ui.Info("등록하지 않았습니다. 나중에 '설치' 라고 치시면 다시 여쭙겠습니다.");
                return "";
            }
            WarnIfRisky(typed);
            return typed;
        }

        Ui.Warn($"Teavel 실행 파일이 여러 개 보입니다. 어느 것을 등록할까요?");
        Ui.Dim($"      {found.How}");
        Console.WriteLine();

        for (var i = 0; i < Math.Min(found.Candidates.Count, 9); i++)
            Ui.Plain($"        [{i + 1}] {Path.GetFileName(found.Candidates[i])}");

        var pick = (Ui.Ask("      번호 (그냥 Enter 면 취소): ") ?? "").Trim();
        if (!int.TryParse(pick, out var n) || n < 1 || n > Math.Min(found.Candidates.Count, 9))
        {
            Ui.Info("등록하지 않았습니다.");
            return "";
        }

        WarnIfRisky(found.Candidates[n - 1]);
        return found.Candidates[n - 1];
    }

    /// <summary>받은 자리 그대로 두면 곤란한 경우 짚어 준다. 막지는 않는다.</summary>
    private static void WarnIfRisky(string exePath)
    {
        var why = TeavelExe.RiskyLocation(exePath);
        if (why.Length == 0) return;

        Console.WriteLine();
        Ui.Warn(why);
        Ui.Dim($"      지금 자리: {Path.GetDirectoryName(exePath)}");
        Ui.Dim("      그대로 등록해 드립니다. 나중에 옮기시면 '설치' 를 한 번 더 실행해 주세요.");
    }

    /// <summary>
    /// 첫 낱말이 <paramref name="names"/> 중 하나면 나머지를 돌려준다. 아니면 null.
    /// </summary>
    /// <remarks>
    /// '명단 C:\...\1학년.xlsx' 처럼 뒤에 값이 붙는 명령을 대화 모드에서도 받기 위한 것.
    /// 값이 없으면 빈 문자열을 돌려준다 — 부르는 쪽이 그때 물어본다.
    /// </remarks>
    private static string? Split(string line, params string[] names)
    {
        var space = line.IndexOf(' ');
        var head = space < 0 ? line : line[..space];
        if (!names.Contains(head, StringComparer.OrdinalIgnoreCase)) return null;
        return space < 0 ? "" : line[(space + 1)..].Trim().Trim('"');
    }

    /// <summary>대화 중에 도움말을 보여 준다.</summary>
    private static void ShowHelp()
    {
        Ui.Plain("""

              계정        학교 계정을 앱들에 연결합니다 (처음이면 여기서 시작)
              점검        지금 무엇이 안 돼 있는지 봅니다
              고침        하나씩 손봅니다
              목록        할 수 있는 일을 보여 줍니다
              모델        말을 더 잘 알아듣게 해 줍니다 (한 번만, 1GB쯤)
              설치        어느 폴더에서나 teavel 로 실행되게 등록합니다

              m365        학교 그룹·Teams 를 살펴보고 정리하고 만듭니다 (관리자용)
              명단 <파일>  명단 파일을 읽어 정리합니다
              선생님 <이름> 선생님 계정을 찾습니다

              자가점검     Teavel 자신이 온전한지 확인합니다
              삭제        Teavel 을 이 컴퓨터에서 지웁니다
              나가기       끝냅니다

              그 밖에는 하고 싶은 일을 그냥 적으시면 됩니다. 예: 엑셀 다 합쳐줘
        """);
    }

    /// <summary>도구가 아니라 흐름인 것을 실행한다.</summary>
    /// <summary>
    /// 도구를 부르는 말이 아닐 때 <b>말을 받아 준다.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 모델이 없으면 지어내지 않는다. 할 줄 아는 것을 보여 주고 받자고 권하는 편이
    /// 어설픈 잡담보다 낫다 — 교사가 원하는 것은 결국 일이 되는 것이다.
    /// </para>
    /// <para>
    /// 대화가 길어지면 대화만 하다 끝난다. 그래서 몇 마디에 한 번은
    /// <b>할 수 있는 일 쪽으로 돌려 세운다.</b>
    /// </para>
    /// </remarks>
    private async Task RunChatAsync(string utterance, CancellationToken ct)
    {
        Console.WriteLine();

        // 필요한 말만 한다. 잡담을 잘하는 것이 목적이 아니라,
        // 해 드릴 수 있는 것으로 데려가는 것이 목적이다.
        var kind = SmallTalk.Classify(utterance);

        switch (kind)
        {
            case SmallTalkKind.Greeting:
                Ui.Plain("  안녕하세요, 선생님. 하실 일을 그냥 적어 주시면 됩니다.");
                break;

            case SmallTalkKind.Thanks:
                Ui.Plain("  도움이 되었다니 다행입니다. 더 필요하시면 말씀해 주세요.");
                return;   // 여기서만은 목록을 안 깐다 — 인사에 안내문을 붙이면 지겹다

            case SmallTalkKind.Lost:
                Ui.Plain("  그러실 때는 '점검' 부터 해 보시면 좋습니다.");
                Ui.Dim("      지금 이 컴퓨터에 안 돼 있는 것을 짚어 드립니다.");
                Console.WriteLine();
                if (!AssumeYes && Ui.Confirm("      지금 해 볼까요?"))
                {
                    await RunCheckAsync(ct).ConfigureAwait(false);
                    return;
                }
                break;

            default:
                // 우리가 답할 수 없는 이야기다. 아는 척하지 않고 할 수 있는 것만 말한다.
                Ui.Plain("  그건 제가 답해 드릴 수 있는 일이 아닙니다.");
                break;
        }

        ShowWhatWeDo();
    }

    /// <summary>
    /// 해 드릴 수 있는 것을 보여 준다.
    /// </summary>
    /// <remarks>
    /// 카탈로그에서 뽑는다 — 손으로 적어 두면 도구를 늘렸을 때 여기만 옛말이 된다.
    /// 다만 카탈로그 순서 그대로 뽑으면 '등록하기 · 등록 풀기 · 모델 내려받기' 가 먼저
    /// 나온다. 그건 Teavel 을 쓰기 위한 잡일이지 선생님이 하려는 일이 아니다.
    /// </remarks>
    private static void ShowWhatWeDo()
    {
        var handy = ToolCatalog.All
            .Where(t => t.Category is ToolCategory.Excel or ToolCategory.Files
                                   or ToolCategory.Outlook or ToolCategory.Word)
            .Take(6)
            .ToList();

        Console.WriteLine();
        Ui.Plain("  제가 해 드릴 수 있는 것은 이런 것들입니다.");
        foreach (var t in handy)
            Ui.Dim($"      · {t.Title}");
        Ui.Dim("      학교 Teams 구성도 합니다. 전부 보시려면 '목록' 이라고 치세요.");
    }

    private async Task RunFlowAsync(ToolSpec tool, IntentMatch match, string utterance, CancellationToken ct)
    {
        switch (tool.Function)
        {
            case "m365":
                await RunM365Async(ct).ConfigureAwait(false);
                return;

            case "m365.archive":
                Console.WriteLine();
                Ui.Info("지난 학년도 팀을 정리하시려는 것 같습니다.");
                Ui.Dim("      ⑤ 정리 에서 하나씩 고르실 수 있습니다.");
                Ui.Dim("      '지난 학년도로 보관' 을 고르면 이름 앞에 연도를 붙이고 학생만 내보냅니다.");
                Ui.Dim("      팀과 파일·대화는 그대로 남습니다.");
                Console.WriteLine();
                if (!AssumeYes && !Ui.Confirm("      들어갈까요?")) { Ui.Info("취소했습니다."); return; }
                await RunM365Async(ct).ConfigureAwait(false);
                return;

            case "chat":
                await RunChatAsync(utterance, ct).ConfigureAwait(false);
                return;

            case "install":
                RunRegister();
                return;

            case "uninstall":
                RunUnregister();
                return;

            case "accounts":
                await RunAccountAsync(ct).ConfigureAwait(false);
                return;

            case "model":
                await RunModelAsync(ct).ConfigureAwait(false);
                return;

            case "m365.teacher":
                var name = match.Arguments.TryGetValue("Name", out var n) ? n?.ToString() : null;
                if (string.IsNullOrWhiteSpace(name)) name = Ui.Ask("      찾으실 선생님 성함: ")?.Trim();
                await RunFindTeacherAsync(name, ct).ConfigureAwait(false);
                return;

            default:
                Ui.Error($"'{tool.Function}' 은(는) 아직 만들어지지 않았습니다.");
                return;
        }
    }

    /// <summary>
    /// 학교 일이라는 것은 알았고, 그 안에서 <b>무엇을 하려는지</b>까지 가른다.
    /// </summary>
    /// <remarks>
    /// 전부 한 흐름으로 보내면 "작년 팀 백업해줘" 라고 해도 만들기부터 시작한다.
    /// 아무것도 안 하는 것보다 나쁘다 — 엉뚱한 일을 하기 때문이다.
    ///
    /// 하나로 못 정하면 <b>짐작하지 않고 물어본다.</b> 다만 그냥 묻는 것이 아니라
    /// 할 수 있는 일을 늘어놓고 고르게 한다 — 무엇을 물어야 할지 모르는 분들이다.
    /// </remarks>
    private async Task RunSchoolWorkAsync(string line, CancellationToken ct)
    {
        var t = line.Replace(" ", "").ToLowerInvariant();

        var tidyWords = new[] { "백업", "보관", "정리", "지난", "작년", "지난해", "묵은", "옛" };
        var makeWords = new[] { "만들", "생성", "구성", "새로", "올해", "새학기", "신학기" };

        var wantsTidy = tidyWords.Any(w => t.Contains(w, StringComparison.Ordinal));
        var wantsMake = makeWords.Any(w => t.Contains(w, StringComparison.Ordinal));

        if (wantsTidy && !wantsMake)
        {
            Console.WriteLine();
            Ui.Info("지난 학년도 팀을 정리하시려는 것 같습니다.");
            Ui.Dim("      학교 구성 화면으로 들어가면 ⑤ 정리 에서 하나씩 고르실 수 있습니다.");
            Ui.Dim("      '지난 학년도로 보관' 을 고르면 이름 앞에 연도를 붙이고 학생만 내보냅니다.");
            Ui.Dim("      팀과 파일·대화는 그대로 남습니다.");
            Console.WriteLine();
            if (!Ui.Confirm("      들어갈까요?")) { Ui.Info("취소했습니다."); return; }
        }

        await RunM365Async(ct).ConfigureAwait(false);
    }

    // ─────────────────────────────── 언어 모델 ───────────────────────────────

    /// <summary>언어 모델을 내려받는다. 이미 쓸 수 있으면 알려 주고 끝낸다.</summary>
    public async Task<int> RunModelAsync(CancellationToken ct)
    {
        // 형태소 분석기가 먼저다 — 훨씬 작고(약 96MB), 이것만 있어도 말귀가 눈에 띄게 좋아진다.
        // 언어 모델(1GB)을 안 받거나 못 받는 분도 여기까지는 얻어 간다.
        await EnsureMorphemesAsync(ct).ConfigureAwait(false);

        Ui.Title("언어 모델");

        if (_llm is not null)
        {
            var mismatch = LocalLlmIntentRouter.DescribeMismatch(_llm.ModelPath);

            if (mismatch is null)
            {
                Ui.Ok($"이미 쓸 수 있습니다: {_llm.ModelPath}");
                Ui.Dim($"      크기: {new FileInfo(_llm.ModelPath).Length / 1024 / 1024}MB");
                return 0;
            }

            // 남의 모델을 빌려 쓰는 중이다. 여기서 '이미 있다' 고 끝내면
            // 생기부 도우미를 쓰는 선생님은 제대로 된 모델을 영영 못 받는다 —
            // 말을 잘 못 알아듣는데 이유는 알 수 없는 상태가 이어진다.
            Ui.Warn($"지금은 다른 앱의 모델을 빌려 쓰고 있습니다: {Path.GetFileName(_llm.ModelPath)}");
            Ui.Dim($"      {mismatch}");
            Console.WriteLine();
        }

        if (!TeavelModelConfig.HasDownloadUrl)
        {
            Ui.Error("내려받을 주소가 정해져 있지 않습니다.");
            Ui.Details(new[]
            {
                "이 빌드에는 모델 주소가 아직 들어 있지 않습니다.",
                "",
                "쓸 수 있는 방법:",
                $"  · 모델 파일(.gguf)을 다음 폴더에 직접 넣기: {Path.Combine(_paths.DataDirectory, "models")}",
                "  · 또는 TEAVEL_GGUF_URL 환경변수에 주소를 지정하고 다시 실행",
                "",
                "모델이 없어도 메뉴로는 모든 기능을 쓸 수 있습니다.",
            });
            return 2;
        }

        var dest = ModelDownloader.DefaultModelPath(_paths);
        var mb = TeavelModelConfig.ModelApproxBytes / 1024 / 1024;

        Ui.Info($"약 {mb}MB 를 내려받습니다. 한 번만 받으면 됩니다.");
        Ui.Dim($"      저장 위치: {dest}");
        if (!AssumeYes && !Ui.Confirm("      지금 받을까요?")) { Ui.Info("취소했습니다."); return 0; }

        Console.WriteLine();
        var lastPercent = -1;
        try
        {
            await ModelDownloader.DownloadAsync(
                dest, TeavelModelConfig.ModelUrl, TeavelModelConfig.ModelApproxBytes,
                progress: (done, total) =>
                {
                    // 1%마다만 다시 그린다 — 매 버퍼마다 찍으면 화면이 깜빡인다.
                    var percent = total > 0 ? (int)(done * 100 / total) : 0;
                    if (percent == lastPercent) return;
                    lastPercent = percent;
                    Console.Write($"\r      {percent,3}%  ({done / 1024 / 1024}MB / {total / 1024 / 1024}MB)   ");
                },
                ct: ct).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine();
            Ui.Ok("언어 모델을 받았습니다. 이제 자유롭게 말로 시키실 수 있습니다.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Ui.Info("중단했습니다. 다음에 이어서 받습니다.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Ui.Error("모델을 받지 못했습니다.");
            Ui.Details(new[] { ex.Message });
            return 1;
        }
    }

    // ───────────────────────────── 목록·자가점검 ─────────────────────────────

    /// <summary>할 수 있는 일을 보여 준다. 세팅이 먼저다.</summary>
    public void RunList()
    {
        Ui.Title("PC 세팅 — 이것이 Teavel 의 본업입니다");
        foreach (var stage in _setup.ByStage())
        {
            Ui.Dim($"  {SetupCatalog.StageName(stage.Key)}");
            foreach (var t in stage) Ui.Plain($"      {t.Title}");
        }
        Console.WriteLine();
        Ui.Dim("  'teavel 점검' 으로 지금 상태를 보고, 'teavel 고침' 으로 손봅니다.");

        // 세팅에 딸린 것들(프린터)을 먼저 보이고, 업무 도구는 뒤로 둔다.
        foreach (var group in ToolCatalog.ByCategory().OrderBy(g => g.Key == ToolCategory.Setup ? 0 : 1))
        {
            Ui.Title(CategoryName(group.Key));
            foreach (var t in group)
            {
                Ui.Plain($"  {t.Title}");
                Ui.Dim($"      \"{t.Examples[0]}\"");
            }
        }

        Console.WriteLine();
        Ui.Dim("  그냥 하고 싶은 일을 말로 적으셔도 됩니다.");
    }

    // ─────────────────────────── PATH 등록 ───────────────────────────

    /// <summary>
    /// 아직 등록돼 있지 않으면 첫 실행 때 한 번만 물어본다.
    /// </summary>
    /// <remarks>
    /// 선생님이 '설치' 라는 명령의 존재를 알아야 하는 상태는 실패다.
    /// 포털 설치 프로그램이 등록해 주는 것이 정상 경로지만, 압축만 풀어 쓰거나
    /// 설치 단계가 관리자 권한으로 돌아 엉뚱한 계정에 등록된 경우를 여기서 건진다.
    /// 한 번 거절하면 다시 묻지 않는다 — 매번 물으면 그게 더 성가시다.
    /// </remarks>
    /// <summary>
    /// 언어 모델이 없으면 <b>그 자리에서 받자고 권한다.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// 예전에는 흐릿한 한 줄로 "'teavel 모델' 로 받으세요" 라고만 하고 넘어갔다.
    /// 그런데 <b>말을 알아듣는 것이 먼저다</b> — 그게 안 되면 나머지가 다 반쪽이 된다.
    /// 정확한 낱말을 알아야만 쓸 수 있는 도구는 그 낱말을 모르는 사람에게 없는 것이다.
    /// </para>
    /// <para>
    /// 그래서 등록을 권하듯 여기서도 권한다. 한 번 거절하면 다시 묻지 않는다 —
    /// 인터넷이 느린 학교도 있고, 나중에 하고 싶을 수도 있다.
    /// </para>
    /// </remarks>
    private async Task OfferModelOnceAsync(CancellationToken ct)
    {
        if (_llm is not null) return;

        var marker = Path.Combine(_paths.DataDirectory, "no-auto-model");
        if (File.Exists(marker))
        {
            Ui.Dim("  (언어 모델이 없어 낱말로 알아듣습니다. '모델' 이라고 치시면 받습니다)");
            return;
        }

        Console.WriteLine();
        Ui.Info("아직 말을 알아듣는 언어 모델이 없습니다.");
        Ui.Plain("""
              지금은 정해진 낱말만 알아듣습니다. 모델을 받으시면 말투가 달라도 알아듣습니다.

                지금        "엑셀 합치기" 는 되지만 "엑셀들 좀 묶어줘" 는 잘 못 알아듣습니다
                받은 뒤     둘 다 알아듣습니다

              1GB 쯤 되고 몇 분 걸립니다. 한 번만 받으면 됩니다.
              받은 뒤에는 인터넷 없이 동작합니다 — 학생 자료가 밖으로 나가지 않습니다.
        """);

        Console.WriteLine();
        if (!Ui.Confirm("      지금 받을까요?"))
        {
            try { File.WriteAllText(marker, "묻지 않음"); } catch { }
            Ui.Dim("      나중에 받으시려면 '모델' 이라고 치시면 됩니다.");
            return;
        }

        await RunModelAsync(ct).ConfigureAwait(false);
    }

    private void OfferRegistrationOnce()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (_path.IsRegistered()) return;

        var marker = Path.Combine(_paths.DataDirectory, "no-auto-register");
        if (File.Exists(marker)) return;

        Console.WriteLine();
        Ui.Info("아직 PowerShell 어디서나 실행되도록 등록돼 있지 않습니다.");
        Ui.Dim("      등록하면 다음부터 아무 폴더에서나 teavel 만 치면 됩니다.");

        if (!Ui.Confirm("      지금 등록할까요?"))
        {
            try { File.WriteAllText(marker, "묻지 않음"); } catch { }
            Ui.Dim("      나중에 하시려면 'teavel 설치' 를 실행하세요.");
            return;
        }

        var exe = ResolveExe();
        if (exe.Length == 0) return;

        Ui.Fix("등록", _path.Register(exe));
        Ui.Fix("탐색기 우클릭 메뉴", _explorer.Register(exe));
        Ui.Fix("Windows 설정 목록", _uninstall.Register(exe, Version));
    }

    /// <summary>PATH·탐색기 우클릭 메뉴·Windows 설정 목록에 등록한다.</summary>
    public int RunRegister()
    {
        Ui.Title("Teavel 등록");

        var exe = ResolveExe();
        if (exe.Length == 0) return 2;

        Ui.Fix("PowerShell 어디서나 실행", _path.Register(exe));
        Ui.Fix("탐색기 우클릭 메뉴", _explorer.Register(exe));

        // 설정 앱 목록에 올리는 것도 등록의 일부다. 이것이 빠져 있으면 나중에 지우려는
        // 선생님이 설정 > 앱 을 열었을 때 Teavel 이 아예 보이지 않는다.
        Ui.Fix("Windows 설정 목록", _uninstall.Register(exe, Version));
        return 0;
    }

    /// <summary>보여 줄 판 번호.</summary>
    private static string Version
        => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// 등록을 풀고 Teavel 이 남긴 설정도 치운다. 프로그램 파일 자체는 지우지 않는다.
    /// </summary>
    /// <remarks>
    /// 꼬였을 때 '지웠다 다시 설치' 로 되살아나야 한다. 그러려면 설정 흔적이 남으면 안 된다 —
    /// 예를 들어 '등록 안 함' 표식이 남아 있으면 다시 깔아도 등록을 다시 묻지 않는다.
    /// 언어 모델은 크니까 기본으로 남긴다(다시 받게 만들면 그게 더 손해다).
    /// </remarks>
    public int RunUnregister()
    {
        Ui.Title("등록 해제");
        Ui.Fix("PowerShell 등록 해제", _path.Unregister());
        Ui.Fix("탐색기 우클릭 메뉴 해제", _explorer.Unregister());
        Ui.Fix("Windows 설정 목록에서 내리기", _uninstall.Unregister());

        // 설정·표식 정리. models 폴더는 건드리지 않는다.
        var removed = 0;
        try
        {
            var dir = _paths.DataDirectory;
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    try { File.Delete(f); removed++; } catch { }
                }
            }
        }
        catch { }

        if (removed > 0) Ui.Ok($"남아 있던 설정 {removed}개를 치웠습니다.");

        Console.WriteLine();
        Ui.Info("등록만 풀었습니다. 프로그램과 내려받은 모델은 그대로 있습니다.");
        Ui.Dim("      파일까지 다 지우시려면 'teavel 삭제' 를 실행하세요.");

        return 0;
    }

    // ─────────────────────────────── 지우기 ───────────────────────────────

    /// <summary>
    /// Teavel 을 이 PC 에서 지운다 — 등록·설정·내려받은 것·프로그램 파일까지.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 설치 프로그램 없이 exe 하나로 받는 방식이라, 지우는 것도 우리가 해야 한다.
    /// Windows 설정 &gt; 앱 의 [제거] 도 결국 이 명령을 부른다
    /// (<see cref="UninstallRegistration"/> 이 그렇게 등록해 둔다).
    /// </para>
    /// <para>
    /// <b>무엇을 지울지 다 보여 주고 지운다.</b> 남의 PC 에서 파일을 지우는 일이다.
    /// 내려받은 모델(1GB 남짓)은 다시 받으려면 오래 걸리므로 <b>따로 여쭙는다</b> —
    /// 자리만 옮기려는 분에게 1GB 를 다시 받게 하는 것은 손해다.
    /// </para>
    /// </remarks>
    public int RunUninstall() => RunUninstall(out _);

    /// <param name="cancelled">
    /// 교사가 그만두었으면 true. 대화 모드에서 이것을 보고 <b>끝낼지 이어 갈지</b> 정한다 —
    /// 취소했는데도 창이 닫히면 그것대로 놀랄 일이다.
    /// </param>
    /// <inheritdoc cref="RunUninstall()"/>
    public int RunUninstall(out bool cancelled)
    {
        cancelled = false;
        Ui.Title("Teavel 지우기");

        var exe = TeavelExe.Find(_paths).Path;
        var settings = TeavelRemoval.Settings(_paths);
        var downloads = TeavelRemoval.Downloads(_paths);
        var program = TeavelRemoval.Program(exe);

        Ui.Dim("      지울 것을 먼저 보여 드립니다.");
        Console.WriteLine();

        Ui.Plain("      등록");
        Ui.Dim("        PowerShell 등록 · 탐색기 우클릭 메뉴 · Windows 설정 목록");

        if (settings.Count > 0)
        {
            Console.WriteLine();
            Ui.Plain($"      설정  ({_paths.DataDirectory})");
            foreach (var s in settings) Ui.Dim($"        {s.Title}");
        }

        if (program.Count > 0)
        {
            Console.WriteLine();
            Ui.Plain($"      프로그램  ({Path.GetDirectoryName(exe)})");
            foreach (var p in program) Ui.Dim($"        {p.Title}  {p.Size}");
        }

        if (downloads.Count > 0)
        {
            Console.WriteLine();
            Ui.Plain("      내려받아 둔 것 — 따로 여쭙겠습니다");
            foreach (var d in downloads) Ui.Dim($"        {d.Title}  {d.Size}");

            // 생기부 도우미가 받아 둔 모델을 빌려 쓰고 있을 수 있다. 그것은 남의 것이라
            // 우리가 지울 것이 아니다 — 여기 적힌 것은 Teavel 이 제 자리에 받은 것뿐이다.
            Ui.Dim("        (다른 앱이 받아 둔 것은 건드리지 않습니다)");
        }

        Console.WriteLine();

        // 되돌릴 수 없는 일이라 그냥 Enter 는 '아니오' 다. '제거' 라고 쳤는데
        // 무심코 누른 Enter 로 프로그램이 지워지면 안 된다.
        if (!AssumeYes && !Ui.Confirm("      정말 지울까요?", defaultYes: false))
        {
            cancelled = true;
            Ui.Info("그만두었습니다. 아무것도 지우지 않았습니다.");
            return 0;
        }

        // 모델은 따로 묻는다. --yes 로 들어왔으면(설정 앱의 조용한 제거) 함께 지운다.
        var alsoDownloads = downloads.Count > 0
            && (AssumeYes
                || Ui.Confirm(
                    $"      내려받은 것({downloads.Sum(d => d.Bytes) / 1024 / 1024:N0}MB)도 지울까요?",
                    defaultYes: false));

        Console.WriteLine();
        Ui.Fix("PowerShell 등록 해제", _path.Unregister());
        Ui.Fix("탐색기 우클릭 메뉴 해제", _explorer.Unregister());
        Ui.Fix("Windows 설정 목록에서 내리기", _uninstall.Unregister());

        // 언어 모델을 붙잡고 있으면 파일이 지워지지 않는다. 먼저 놓는다.
        _llm?.Dispose();

        var toDelete = new List<RemovalItem>(settings);
        if (alsoDownloads) toDelete.AddRange(downloads);

        var (removed, failed) = TeavelRemoval.Delete(toDelete);
        if (removed > 0) Ui.Ok($"{removed}개를 지웠습니다.");
        foreach (var f in failed) Ui.Warn($"지우지 못했습니다: {f}");

        if (!alsoDownloads && downloads.Count > 0)
        {
            Ui.Dim($"      내려받은 것은 남겨 두었습니다: {_paths.DataDirectory}");
            Ui.Dim("      다시 설치하시면 그대로 씁니다.");
        }

        // 프로그램 파일은 마지막이다 — 지금 그 파일로 돌고 있기 때문에 우리가 끝난
        // 뒤에 지워져야 한다.
        Console.WriteLine();
        if (program.Count == 0)
        {
            Ui.Info("프로그램 파일은 찾지 못했습니다. 받아 두신 teavel.exe 를 직접 지워 주세요.");
        }
        else
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(exe));

            // 비었을 때만 치울 폴더들. 프로그램 폴더에 선생님 파일이 섞여 있으면 그대로 남는다.
            var emptyFolders = new List<string>();
            if (folder is { Length: > 0 }) emptyFolders.Add(folder);
            if (alsoDownloads) emptyFolders.Add(_paths.DataDirectory);

            if (TeavelRemoval.ScheduleDelete(program, emptyFolders))
            {
                Ui.Ok("프로그램 파일은 이 창이 닫힌 뒤 몇 초 안에 지워집니다.");
                Ui.Dim($"      {folder}");
                Ui.Dim("      그 폴더에 다른 파일이 있으면 폴더는 남습니다 — 선생님 파일은 건드리지 않습니다.");
            }
            else
            {
                Ui.Warn("프로그램 파일은 자동으로 지우지 못했습니다.");
                Ui.Dim($"      이 폴더를 직접 지워 주세요: {folder}");
            }
        }

        Console.WriteLine();
        Ui.Info("그동안 고마웠습니다.");
        return 0;
    }

    /// <summary>도구 선언과 PowerShell 스크립트가 어긋나지 않았는지 확인한다.</summary>
    public async Task<int> RunSelfCheckAsync(CancellationToken ct)
    {
        Ui.Title("자가점검");

        var issues = ToolSelfCheck.Run(_tools.ScriptsDirectory);
        if (issues.Count == 0) Ui.Ok($"도구 {ToolCatalog.All.Count(t => !t.Module.StartsWith('@'))}개가 스크립트와 맞습니다.");
        else
        {
            Ui.Error($"{issues.Count}가지가 어긋났습니다.");
            foreach (var i in issues) Ui.Details(new[] { $"[{i.ToolId}] {i.Problem}" });
        }

        Console.WriteLine();
        if (_tools.FindPowerShell() is { } shell)
        {
            Ui.Ok($"PowerShell: {shell}");

            // 학교 그룹 정책이 우리 -ExecutionPolicy Bypass 를 이길 수 있다. 미리 짚어 준다.
            Ui.Check("실행 정책", await _tools.CheckExecutionPolicyAsync(ct).ConfigureAwait(false));
        }
        else
        {
            Ui.Error("PowerShell 을 찾지 못했습니다. Windows 에서 실행해 주세요.");
        }

        Console.WriteLine();
        if (_path.IsRegistered())
        {
            Ui.Ok("PowerShell 어디서나 teavel 로 실행됩니다.");
        }
        else
        {
            Ui.Warn("PATH 에 등록돼 있지 않습니다. 'teavel 설치' 로 등록하면 어디서나 teavel 만 쳐도 됩니다.");
        }

        // 지울 길이 있는지도 확인해 준다. 이것이 빠져 있으면 나중에 지우려는 선생님이
        // 설정 > 앱 을 열었을 때 Teavel 이 목록에 없어서 방법을 못 찾는다.
        if (_uninstall.IsRegistered())
        {
            Ui.Ok("Windows 설정 > 앱 목록에서 지울 수 있습니다.");
        }
        else
        {
            Ui.Warn("Windows 설정 > 앱 목록에 없습니다. 거기서는 지우지 못합니다.");
            Ui.Dim("      'teavel 설치' 로 등록하면 목록에 올라갑니다. ('teavel 삭제' 로는 지금도 지울 수 있습니다)");
        }

        // 말을 알아듣는 데 쓰는 것은 두 겹이다. 하나만 보고하면 교사가
        // '왜 어떤 말은 알아듣고 어떤 말은 못 알아듣나' 를 알 길이 없다.
        if (Morphemes.KiwiReady)
        {
            Ui.Ok($"형태소 분석기: Kiwi {KiwiNative.Version()} — '합쳐줘' 와 '합치기' 를 같은 말로 봅니다.");
        }
        else
        {
            Ui.Warn("형태소 분석기가 없습니다. 말끝이 다르면 놓칠 수 있습니다.");
            if (Morphemes.Why is { Length: > 0 } why) Ui.Dim($"      {why}");
            Ui.Dim("      'teavel 모델' 로 함께 내려받습니다.");
        }

        if (_llm is not null)
        {
            Ui.Ok($"언어 모델: {Path.GetFileName(_llm.ModelPath)}");
            Ui.Dim($"      {_llm.ModelPath}");
            if (LocalLlmIntentRouter.DescribeMismatch(_llm.ModelPath) is { } note)
            {
                Ui.Warn(note);
                Ui.Dim("      'teavel 모델' 로 Teavel 전용 모델을 받으면 나아집니다.");
            }
        }
        else
        {
            Ui.Warn("언어 모델이 없습니다. 낱말로만 알아듣습니다(메뉴는 그대로 동작합니다).");
            Ui.Dim("      'teavel 모델' 로 내려받을 수 있습니다.");
        }

        Ui.Ok($"앱 카탈로그: {_apps.Apps.Count}개 — {_apps.Source}");

        return issues.Count == 0 ? 0 : 1;
    }

    // ─────────────────────────────── 대화 ───────────────────────────────

    /// <summary>자연어로 명령을 받는 대화 모드.</summary>
    public async Task<int> RunInteractiveAsync(CancellationToken ct)
    {
        Brand.PrintBanner();
        if (StartFolder is not null)
            Ui.Dim($"  현재 폴더: {StartFolder}");

        OfferRegistrationOnce();
        await OfferModelOnceAsync(ct).ConfigureAwait(false);
        // 첫 화면에 명령을 늘어놓지 않는다 — 넷을 적으나 열을 적으나 못 읽는 것은 같다.
        // 대신 '모를 때 어디로 가면 되는지' 한 곳만 또렷하게 둔다.
        Ui.Dim("  하고 싶은 일을 그냥 적으셔도 됩니다.  예: 엑셀 다 합쳐줘 · 반 팀 만들어줘");

        await _mcp.ConnectAllAsync(ct).ConfigureAwait(false);
        if (_mcp.Connected.Count > 0)
        {
            Console.WriteLine();
            foreach (var c in _mcp.Connected)
                Ui.Ok($"{c.App.Name} 연결됨 — 기능 {c.Tools.Count}개");
        }
        foreach (var f in _mcp.Failures) Ui.Warn($"{f.App.Name} 에 연결하지 못했습니다: {f.Reason}");

        while (!ct.IsCancellationRequested)
        {
            Console.WriteLine();
            var line = Ui.Ask("> ")?.Trim();

            if (line is null) return 0;
            if (line.Length == 0) continue;
            // 명령줄에서 되는 것은 여기서도 다 돼야 한다.
            //
            // 포털에서 받으면 exe 파일 하나뿐이라 교사는 그것을 더블클릭한다 —
            // 그러면 곧장 이 대화 모드로 들어온다. 여기서 '자가점검' 이나 '모델' 을 못 치면
            // 그 명령들은 사실상 없는 것이나 마찬가지다.
            if (line is "나가기" or "종료" or "exit" or "quit") return 0;
            if (line is "목록" or "list") { RunList(); continue; }
            if (line is "계정" or "연결" or "account") { await RunAccountAsync(ct).ConfigureAwait(false); continue; }
            if (line is "점검" or "check") { await RunCheckAsync(ct).ConfigureAwait(false); continue; }
            if (line is "고침" or "손보기" or "fix") { await RunFixAsync(null, ct).ConfigureAwait(false); continue; }
            if (line is "자가점검" or "selfcheck") { await RunSelfCheckAsync(ct).ConfigureAwait(false); continue; }
            if (line is "모델" or "model") { await RunModelAsync(ct).ConfigureAwait(false); continue; }
            if (line is "설치" or "등록" or "install") { RunRegister(); continue; }
            if (line is "등록해제" or "unregister") { RunUnregister(); continue; }
            if (line is "삭제" or "지우기" or "제거" or "uninstall" or "remove")
            {
                // 정말 지웠으면 대화를 이어 갈 까닭이 없다 — 프로그램 파일이 곧
                // 사라지므로 여기서 나간다. 그만두었으면 하던 대로 이어 간다.
                RunUninstall(out var stopped);
                if (!stopped) return 0;
                continue;
            }
            if (line is "도움말" or "help" or "?") { ShowHelp(); continue; }

            // 뒤에 값이 붙는 것들.
            if (line is "m365" or "그룹" or "teams") { await RunM365Async(ct).ConfigureAwait(false); continue; }
            if (Split(line, "명단", "roster") is { } rosterArg)
            { RosterFlow.Run(rosterArg, AssumeYes); continue; }
            if (Split(line, "선생님", "교사", "teacher") is { } teacherArg)
            { await RunFindTeacherAsync(teacherArg, ct).ConfigureAwait(false); continue; }

            await HandleUtteranceAsync(line, ct).ConfigureAwait(false);
        }
        return 0;
    }

    /// <summary>말 한 마디만 처리하고 끝낸다(teavel "…" 형태로 부를 때).</summary>
    public async Task<int> HandleOneShotAsync(string utterance, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(utterance))
        {
            Ui.Error("무엇을 할지 적어 주세요.  예: teavel \"2반 엑셀 다 합쳐줘\"");
            return 2;
        }

        await _mcp.ConnectAllAsync(ct).ConfigureAwait(false);
        await HandleUtteranceAsync(utterance, ct).ConfigureAwait(false);
        return 0;
    }

    private async Task HandleUtteranceAsync(string utterance, CancellationToken ct)
    {
        var matches = await _router.RouteAsync(utterance, ct).ConfigureAwait(false);

        if (matches.Count == 0)
        {
            Ui.Warn("무슨 일인지 알아듣지 못했습니다.");
            Ui.Dim("      '목록' 을 입력하면 할 수 있는 일을 보여드립니다.");
            return;
        }

        var chosen = matches[0];

        // 말 걸기는 되묻지 않는다.
        //
        // 잡담에 "이 중 어떤 것일까요?" 를 내미는 것이 바로 없애려던 그 막다른 길이다.
        // 되묻는 까닭은 <b>엉뚱한 것을 실행할까 봐</b>인데, 이건 아무것도 실행하지 않고
        // 할 수 있는 일을 보여 줄 뿐이라 틀려도 손해가 없다.
        if (chosen.Tool.Id == "teavel.chat")
        {
            await RunToolAsync(chosen, utterance, ct).ConfigureAwait(false);
            return;
        }

        // 확신이 낮으면 넘겨짚지 않고 고르게 한다.
        if (chosen.Score < KeywordIntentRouter.ConfidentScore && matches.Count > 1)
        {
            Console.WriteLine();
            Ui.Info("이 중 어떤 것일까요?");
            for (var i = 0; i < Math.Min(matches.Count, 5); i++)
                Ui.Plain($"      {i + 1}. {matches[i].Tool.Title}");

            var pick = Ui.Ask("      번호 (그냥 Enter 면 취소): ")?.Trim();
            if (!int.TryParse(pick, out var n) || n < 1 || n > Math.Min(matches.Count, 5))
            {
                Ui.Info("취소했습니다.");
                return;
            }
            chosen = matches[n - 1];
        }

        await RunToolAsync(chosen, utterance, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 갈래를 고르게 한다. 안 고르셨으면 null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 번호로도, 말로도 받는다 — <see cref="Ui.Choose"/> 가 둘 다 알아듣는다.
    /// 알아듣지 못하면 넘겨짚지 않고 다시 묻되, <b>하던 일을 버리지 않는다.</b>
    /// 예전에는 여기서 틀리면 도구가 통째로 취소돼 처음부터 다시 해야 했다.
    /// </para>
    /// <para>
    /// 값이 아니라 <see cref="ToolChoice.Label"/> 을 보여 준다. PowerShell 매개변수 값은
    /// 영어라, 그것을 그대로 띄우면 우리말로 답한 선생님이 틀린 것이 된다.
    /// </para>
    /// </remarks>
    private static string? AskChoice(ToolParam p)
    {
        Ui.Dim($"      {p.Description}");

        var options = p.Options
            .Select((c, i) => new Ui.Choice((i + 1).ToString(), $"[{i + 1}] {c.Label}", c.Words))
            .ToList();

        // 필수가 아니면 기본값 갈래를 Enter 로 고를 수 있게 한다.
        var fallback = p.Required || p.Default is null
            ? ""
            : (p.Options.Select((c, i) => (c, i))
                        .FirstOrDefault(t => string.Equals(t.c.Value, p.Default, StringComparison.OrdinalIgnoreCase))
                        is { c: not null } hit ? (hit.i + 1).ToString() : "");

        var picked = Ui.Choose($"{p.Label}", options, fallback);

        return int.TryParse(picked, out var n) && n >= 1 && n <= p.Options.Count
            ? p.Options[n - 1].Value
            : null;
    }

    /// <summary>모자란 인자를 묻고, 검증하고, 실행한다.</summary>
    private async Task RunToolAsync(IntentMatch match, string utterance, CancellationToken ct)
    {
        var tool = match.Tool;

        // "@flow" 는 PowerShell 함수가 아니라 CLI 의 한 판이다.
        // 도구 목록에 함께 올려 둔 덕에 언어 모델과 낱말 라우터가 이것도 후보로 보고,
        // 관리자는 'teavel m365' 라는 말을 몰라도 하고 싶은 말로 닿는다.
        if (tool.Module == "@flow")
        {
            await RunFlowAsync(tool, match, utterance, ct).ConfigureAwait(false);
            return;
        }

        var args = new Dictionary<string, object>(match.Arguments, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine();
        Ui.Plain($"  {tool.Title}");
        Ui.Dim($"      {tool.Description}");
        Console.WriteLine();

        foreach (var p in tool.Parameters)
        {
            if (args.ContainsKey(p.Name)) continue;

            // 갈래는 번호로 고르게 한다.
            //
            // 예전에는 [school / personal / unknown] 을 띄우고 그대로 치기를 기다렸다.
            // 한국어로 물어 놓고 영어로 답하라는 것이라, 선생님이 "학교컴퓨터야" 라고 치면
            // 튕겨 나갔다 — 그것도 <b>하던 일이 통째로 날아간 채로</b>. 실제로 그렇게 막혔다.
            // 이제 우리말 보기를 번호와 함께 보여 주고, 번호로도 말로도 받는다.
            if (p.Kind == ToolParamKind.Choice && p.Options.Count > 0)
            {
                if (AskChoice(p) is { } picked) { args[p.Name] = picked; continue; }

                // 필수인데 안 고르셨다 — 넘겨짚지 않는다.
                if (p.Required) { Ui.Info("취소했습니다."); return; }
                continue;
            }

            // 탐색기에서 폴더를 열어 들어왔다면, 폴더를 묻는 자리의 기본값은 그 폴더다.
            // 긴 경로를 손으로 치게 하면 자연어로 만든 편의가 통째로 무의미해진다.
            var folderDefault = p.Kind == ToolParamKind.FolderPath ? StartFolder : null;

            string hint;
            if (folderDefault is not null)
                hint = $" (그냥 Enter 면 현재 폴더: {Path.GetFileName(folderDefault.TrimEnd(Path.DirectorySeparatorChar))})";
            else if (!p.Required)
                hint = $" (그냥 Enter 면 {(string.IsNullOrEmpty(p.Default) ? "생략" : p.Default)})";
            else
                hint = "";

            Ui.Dim($"      {p.Description}");
            var value = Ui.Ask($"      {p.Label}{hint}: ")?.Trim();

            if (string.IsNullOrWhiteSpace(value))
            {
                if (folderDefault is not null) { args[p.Name] = folderDefault; continue; }
                if (p.Required)
                {
                    Ui.Info("취소했습니다.");
                    return;
                }
                continue;   // 선택 인자는 비워 두면 기본값이 쓰인다
            }
            args[p.Name] = value;
        }

        var validation = _validator.Validate(tool, args);
        if (!validation.Ok)
        {
            Console.WriteLine();
            foreach (var e in validation.Errors) Ui.Error(e);
            return;
        }

        var call = new ToolInvocation(tool, validation.Normalized);

        // 파일을 바꾸는 일은 한 번 확인받는다. 학생 제출물을 잘못 건드리면 되돌릴 수 없다.
        if (tool.Mutating && !AssumeYes)
        {
            Console.WriteLine();
            Ui.Warn("이 작업은 파일을 만들거나 바꿉니다.");
            Ui.Dim($"      {call.Describe()}");
            if (!Ui.Confirm("      실행할까요?"))
            {
                Ui.Info("취소했습니다.");
                return;
            }
        }

        Console.WriteLine();
        Ui.Dim("      실행 중…");

        var result = await _tools.RunAsync(call, ct).ConfigureAwait(false);

        Console.WriteLine();
        if (result.Ok) Ui.Ok(result.Message);
        else Ui.Error(result.Message);
        Ui.Details(result.Details);
    }

    private static string CategoryName(ToolCategory c) => c switch
    {
        ToolCategory.Setup => "말로 물어보실 수 있는 것 — 계정·프린터",
        ToolCategory.Excel => "(보조) 엑셀 — 성적·명단",
        ToolCategory.Files => "(보조) 파일·폴더 정리",
        ToolCategory.Outlook => "(보조) 아웃룩 — 메일",
        ToolCategory.Word => "(보조) 워드 — 문서",
        _ => c.ToString(),
    };

    public async ValueTask DisposeAsync()
    {
        await _mcp.DisposeAsync().ConfigureAwait(false);
        _llm?.Dispose();
    }
}
