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

        IRegistry registry = OperatingSystem.IsWindows()
            ? new WindowsRegistry()
            : new InMemoryRegistry();   // 개발용 — Windows 가 아니면 아무것도 못 찾는 상태로 동작한다

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

        var modelPath = LocalLlmIntentRouter.FindModel(_paths);
        _llm = modelPath is null ? null : new LocalLlmIntentRouter(modelPath);
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

    // ─────────────────────────────── 언어 모델 ───────────────────────────────

    /// <summary>언어 모델을 내려받는다. 이미 쓸 수 있으면 알려 주고 끝낸다.</summary>
    public async Task<int> RunModelAsync(CancellationToken ct)
    {
        Ui.Title("언어 모델");

        if (_llm is not null)
        {
            Ui.Ok($"이미 쓸 수 있습니다: {_llm.ModelPath}");
            Ui.Dim($"      크기: {new FileInfo(_llm.ModelPath).Length / 1024 / 1024}MB");
            return 0;
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

        Ui.Fix("등록", _path.Register());
        Ui.Fix("탐색기 우클릭 메뉴", _explorer.Register());
    }

    /// <summary>PATH 와 탐색기 우클릭 메뉴에 등록한다.</summary>
    public int RunRegister()
    {
        Ui.Title("Teavel 등록");
        Ui.Fix("PowerShell 어디서나 실행", _path.Register());
        Ui.Fix("탐색기 우클릭 메뉴", _explorer.Register());
        return 0;
    }

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
        Ui.Info("이제 프로그램 폴더를 지우시면 완전히 제거됩니다.");

        var models = Path.Combine(_paths.DataDirectory, "models");
        if (Directory.Exists(models))
        {
            Ui.Dim($"      언어 모델은 남겨 두었습니다: {models}");
            Ui.Dim("      다시 설치하면 그대로 씁니다. 지우시려면 이 폴더를 직접 지우세요.");
        }

        return 0;
    }

    /// <summary>도구 선언과 PowerShell 스크립트가 어긋나지 않았는지 확인한다.</summary>
    public async Task<int> RunSelfCheckAsync(CancellationToken ct)
    {
        Ui.Title("자가점검");

        var issues = ToolSelfCheck.Run(_tools.ScriptsDirectory);
        if (issues.Count == 0) Ui.Ok($"도구 {ToolCatalog.All.Count}개가 스크립트와 맞습니다.");
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
        if (_llm is null) Ui.Dim("  (언어 모델이 없어 낱말로 알아듣습니다. 'teavel 모델' 로 받으세요)");

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
            if (line is "나가기" or "종료" or "exit" or "quit") return 0;
            if (line is "목록" or "list") { RunList(); continue; }
            if (line is "점검" or "check") { await RunCheckAsync(ct).ConfigureAwait(false); continue; }
            if (line is "고침" or "손보기" or "fix") { await RunFixAsync(null, ct).ConfigureAwait(false); continue; }

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

        await RunToolAsync(chosen, ct).ConfigureAwait(false);
    }

    /// <summary>모자란 인자를 묻고, 검증하고, 실행한다.</summary>
    private async Task RunToolAsync(IntentMatch match, CancellationToken ct)
    {
        var tool = match.Tool;
        var args = new Dictionary<string, object>(match.Arguments, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine();
        Ui.Plain($"  {tool.Title}");
        Ui.Dim($"      {tool.Description}");
        Console.WriteLine();

        foreach (var p in tool.Parameters)
        {
            if (args.ContainsKey(p.Name)) continue;

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

            if (p.Kind == ToolParamKind.Choice) hint += $" [{string.Join(" / ", p.Choices ?? Array.Empty<string>())}]";

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
