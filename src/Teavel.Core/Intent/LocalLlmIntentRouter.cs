using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Native;
using Teavel.Model;
using Teavel.Platform;
using Teavel.Tools;

namespace Teavel.Intent;

/// <summary>
/// 로컬 GGUF 모델로 도구를 고르고 인자를 뽑는다. 인터넷을 쓰지 않는다.
///
/// 모델에게 코드를 짜게 하지 않는다는 점이 핵심이다. 시키는 일은 둘뿐이고 둘 다 출력이 짧다:
///   ① 도구 13개 중 하나의 id 고르기   (출력 ~10 토큰)
///   ② 그 도구의 인자를 JSON 으로 채우기 (출력 ~60 토큰)
///
/// 모델이 헛소리를 해도 카탈로그에 없는 id 는 버려지고, 인자는 <see cref="ToolArgumentValidator"/>
/// 가 다시 검사한다. 즉 모델은 '고르기' 만 할 뿐 무엇도 직접 실행하지 못한다.
///
/// 두 일 모두 <see cref="LlmSession"/> 을 쓴다 — 안 변하는 지시문을 한 번만 처리해 두어야
/// GPU 없는 교사 PC 에서 쓸 만한 속도가 나온다.
/// </summary>
public sealed class LocalLlmIntentRouter : IIntentRouter, IDisposable
{
    private readonly string _modelPath;
    private readonly int _contextSize;

    private LLamaWeights? _weights;
    private ModelParams? _params;
    private LlmSession? _picker;    // 도구 고르기 — 도구 목록이 캐시된다
    private LlmSession? _filler;    // 인자 뽑기 — 지시문이 캐시된다

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private bool _disposed;

    public LocalLlmIntentRouter(string modelPath, int contextSize = TeavelModelConfig.ContextSize)
    {
        _modelPath = modelPath;
        _contextSize = contextSize;
    }

    /// <summary>쓰고 있는 모델 파일 경로.</summary>
    public string ModelPath => _modelPath;

    /// <summary>
    /// 오래 걸리는 준비를 시작할 때 알린다. 화면에 한 줄 띄우는 데 쓴다.
    /// </summary>
    /// <remarks>
    /// GPU 없는 교사 PC 에서 1GB 모델을 처음 읽는 데 수십 초가 걸린다. 그동안 화면에
    /// 아무 변화가 없으면 <b>프로그램이 멈춘 것으로 보인다</b> — llama.cpp 의 진단문까지
    /// 껐으니 정말로 아무것도 움직이지 않는다. 기다리는 중이라고 말해 주는 것만으로
    /// '고장' 이 '기다림' 이 된다.
    /// </remarks>
    public Action<string>? OnLoading { get; set; }

    /// <summary>도구 고르기 프롬프트에서 캐시된 토큰 수. 적재 전에는 0.</summary>
    public int CachedTokens => _picker?.CachedTokens ?? 0;

    // ─────────────────────────── 모델 찾기 ───────────────────────────

    /// <summary>
    /// 쓸 수 있는 모델 파일을 찾는다. 없으면 null (그러면 Teavel 은 낱말 라우터로만 동작한다).
    ///
    /// 생기부 도우미가 이미 받아 둔 모델을 함께 쓴다 — 4.7GB 를 두 번 받게 할 이유가 없다.
    /// </summary>
    public static string? FindModel(ISystemPaths paths)
    {
        var fromEnv = Environment.GetEnvironmentVariable("TEAVEL_MODEL");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var folders = new[]
        {
            Path.Combine(paths.AppDirectory, "models"),      // 포털이 동봉한 경우
            Path.Combine(paths.DataDirectory, "models"),     // Teavel 이 내려받는 곳
            SaenggibuModelsDirectory(paths),                 // 생기부 도우미와 공유
        };

        var candidates = new List<string>();
        foreach (var dir in folders)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                candidates.AddRange(Directory.GetFiles(dir, "*.gguf"));
            }
            catch { }
        }
        if (candidates.Count == 0) return null;

        // 생기부 도우미의 모델은 생기부 문장을 쓰도록 따로 학습된 것이라
        // '도구 고르기' 같은 지시 따르기에는 범용 instruct 모델보다 불리하다.
        // (1) Teavel 이 받은 모델 → (2) 범용 instruct → (3) 그 외 순, 같은 등급이면 작은 것.
        return candidates
            .OrderBy(Rank)
            .ThenBy(f => new FileInfo(f).Length)
            .First();

        static int Rank(string path)
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, TeavelModelConfig.ModelFilename, StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.Contains("instruct", StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
        }
    }

    /// <summary>쓰고 있는 모델이 이 일에 잘 맞는지. 잘 맞으면 null, 아니면 교사에게 알릴 한 줄.</summary>
    /// <remarks>
    /// 남의 모델을 빌려 쓰는 것은 '없는 것보다 낫다' 는 뜻이지 '제대로 된다' 는 뜻이 아니다.
    /// 도구를 잘못 고르는 일이 잦을 때 교사가 원인을 짚을 수 있어야 한다.
    /// </remarks>
    public static string? DescribeMismatch(string modelPath)
    {
        var name = Path.GetFileName(modelPath);

        if (string.Equals(name, TeavelModelConfig.ModelFilename, StringComparison.OrdinalIgnoreCase))
            return null;

        if (name.Contains("saenggibu", StringComparison.OrdinalIgnoreCase))
            return "생기부 도우미의 모델을 빌려 쓰고 있습니다. 생기부 문장 쓰기에 맞춰 학습된 모델이라 "
                 + "말을 알아듣는 정확도는 떨어질 수 있습니다.";

        if (!name.Contains("instruct", StringComparison.OrdinalIgnoreCase))
            return "지시 따르기용(instruct) 모델이 아닐 수 있습니다. 도구를 잘못 고르면 이것을 의심해 보세요.";

        return null;
    }

    /// <summary>
    /// 생기부 도우미가 모델을 받아 두는 폴더.
    /// 그쪽 MainWindow.DataDir() 규칙과 같아야 한다 — 어긋나면 공유가 조용히 안 될 뿐이라 눈치채기 어렵다.
    /// </summary>
    private static string SaenggibuModelsDirectory(ISystemPaths paths)
        => Path.Combine(
            paths.LocalAppData,
            OperatingSystem.IsWindows() ? "SaenggibuHelper" : "saenggibu-helper",
            "models");

    // ─────────────────────────── 라우팅 ───────────────────────────

    public async Task<IReadOnlyList<IntentMatch>> RouteAsync(string utterance, CancellationToken ct = default)
    {
        var tool = await PickToolAsync(utterance, ct).ConfigureAwait(false);
        if (tool is null) return Array.Empty<IntentMatch>();

        var args = await ExtractArgumentsAsync(utterance, tool, ct).ConfigureAwait(false);
        return new[] { new IntentMatch(tool, args, 0.8, IntentSource.Model) };
    }

    // ── ① 도구 고르기 ──

    /// <summary>
    /// 캐시될 지시문. 짧게 유지하는 것이 곧 속도다 — 여기 한 줄을 늘리면 매 요청이 느려지는 게 아니라
    /// 첫 요청만 느려지지만, 문맥을 많이 먹으면 작은 모델에서는 집중력도 떨어진다.
    /// </summary>
    private static string BuildPickerPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("교사가 한 말을 보고 아래 목록에서 알맞은 기능 하나를 고르는 일을 합니다.");
        sb.AppendLine("반드시 목록에 있는 id 하나만 답하세요. 설명은 쓰지 마세요.");
        sb.AppendLine("알맞은 것이 없으면 없음 이라고만 답하세요.");
        sb.AppendLine();
        foreach (var t in ToolCatalog.All)
        {
            sb.Append(t.Id).Append(" = ").Append(t.Title);
            if (t.Aliases.Count > 0)
                sb.Append(" (").Append(string.Join(", ", t.Aliases.Take(6))).Append(')');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private async Task<ToolSpec?> PickToolAsync(string utterance, CancellationToken ct)
    {
        var picker = await PickerAsync(ct).ConfigureAwait(false);

        var answer = await picker
            .AskAsync(utterance, maxTokens: 24, stopAt: new[] { "\n" }, ct)
            .ConfigureAwait(false);

        // 모델이 무슨 말을 덧붙였든, 카탈로그에 실제로 있는 id 만 인정한다.
        foreach (var t in ToolCatalog.All)
            if (answer.Contains(t.Id, StringComparison.OrdinalIgnoreCase))
                return t;

        return null;
    }

    // ── ② 인자 뽑기 ──

    private const string FillerPrompt =
        """
        교사가 한 말에서 요청한 항목의 값을 찾아 JSON 한 줄로만 답하는 일을 합니다.
        말 속에 분명히 나온 값만 넣으세요. 없으면 그 항목은 빼세요. 지어내지 마세요.
        JSON 외에 다른 말은 쓰지 마세요.
        """;

    private async Task<IReadOnlyDictionary<string, object>> ExtractArgumentsAsync(
        string utterance, ToolSpec tool, CancellationToken ct)
    {
        var empty = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // 말에서 뽑을 만한 인자가 없으면(전부 경로뿐이면) 모델을 부르지 않는다 — CLI 가 물어보면 된다.
        // 이 한 줄이 전체 응답 시간의 절반을 아낀다.
        var askable = tool.Parameters
            .Where(p => p.Kind is not (ToolParamKind.FilePath or ToolParamKind.FolderPath or ToolParamKind.OutputPath))
            .ToList();
        if (askable.Count == 0) return empty;

        var filler = await FillerAsync(ct).ConfigureAwait(false);

        var fields = new StringBuilder();
        foreach (var p in askable)
        {
            var kind = p.Kind switch
            {
                ToolParamKind.Number => "숫자",
                ToolParamKind.Bool => "true 또는 false",
                ToolParamKind.Choice => string.Join(" 또는 ", p.Values),
                _ => "글자",
            };
            fields.AppendLine($"- \"{p.Name}\" ({kind}): {p.Description}");
        }

        var question = $"항목:\n{fields}\n교사의 말: {utterance}";

        var answer = await filler
            .AskAsync(question, maxTokens: 128, stopAt: new[] { "\n\n" }, ct)
            .ConfigureAwait(false);

        return ParseJsonObject(answer, askable) ?? empty;
    }

    /// <summary>모델 출력에서 JSON 을 찾아, 선언된 인자만 골라 담는다.</summary>
    private static Dictionary<string, object>? ParseJsonObject(string text, IReadOnlyList<ToolParam> allowed)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // 선언에 없는 이름은 버린다 — 모델이 지어낸 인자가 실행까지 가지 못하게.
                if (!allowed.Any(p => string.Equals(p.Name, prop.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                object? value = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? i : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
                if (value is not null) result[prop.Name] = value;
            }
            return result;
        }
        catch (JsonException) { return null; }
    }

    // ─────────────────────────── 적재 ───────────────────────────

    /// <summary>문맥 하나만큼의 설정. 문맥 크기가 곧 KV 캐시 = RAM 이라 용도별로 다르게 잡는다.</summary>
    private ModelParams ContextParams(int contextSize) => new(_modelPath)
    {
        ContextSize = (uint)contextSize,
        GpuLayerCount = 0,                     // 교사 PC 에 GPU 가 있다고 가정하지 않는다
        Threads = TeavelModelConfig.Threads,   // 코어를 다 쓰면 교사가 하던 작업이 버벅인다

        // 모델 파일을 통째로 RAM 에 복사하지 않고 파일에서 매핑해 쓴다.
        // 기본값이 이미 이렇지만, RAM 8GB 교사 PC 에서는 바뀌면 안 되는 값이라 못 박아 둔다.
        UseMemorymap = true,

        // 페이지를 RAM 에 붙박아 두는 설정. 켜면 다른 프로그램이 쓸 메모리를 뺏는다.
        UseMemoryLock = false,
    };

    /// <summary>
    /// llama.cpp 이 화면에 쏟아내는 진단문을 <b>버린다.</b>
    /// </summary>
    /// <remarks>
    /// 안 막으면 교사 화면이 이런 것으로 뒤덮인다.
    /// <code>
    ///   sched_reserve: reserving full memory module
    ///   state_read_data: - reading memory module
    ///   graph_reserve: reserving a graph for ubatch with n_tokens = 512 …
    /// </code>
    /// 컴퓨터를 잘 모르는 분에게는 <b>고장 난 것으로 보인다.</b> 실제로 말을 걸 때마다
    /// state 를 되돌리므로 매번 나온다. 오류만 남기고 나머지는 버린다.
    /// </remarks>
    private static void SilenceNativeLogs()
    {
        if (_silenced) return;
        _silenced = true;

        try
        {
            NativeLogConfig.llama_log_set((level, message) =>
            {
                // 진짜 오류는 삼키지 않는다 — 안 그러면 왜 안 되는지 아무도 모른다.
                if (level is LLamaLogLevel.Error) Console.Error.Write(message);
            });
        }
        catch
        {
            // 로그를 못 끄는 것으로 Teavel 이 멈출 까닭은 없다.
        }
    }

    private static bool _silenced;

    private async Task EnsureModelAsync(CancellationToken ct)
    {
        if (_weights is not null) return;

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SilenceNativeLogs();
            if (_weights is not null) return;

            if (!File.Exists(_modelPath))
                throw new FileNotFoundException($"언어 모델 파일을 찾지 못했습니다: {_modelPath}");

            var mb = new FileInfo(_modelPath).Length / 1024 / 1024;
            OnLoading?.Invoke($"언어 모델을 읽는 중입니다({mb:N0}MB). 처음 한 번만 걸립니다…");

            _params = ContextParams(_contextSize);
            _weights = LLamaWeights.LoadFromFile(_params);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<LlmSession> PickerAsync(CancellationToken ct)
    {
        if (_picker is not null) return _picker;
        await EnsureModelAsync(ct).ConfigureAwait(false);

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_picker is not null) return _picker;

            // 도구 목록(수백 토큰)을 한 번 처리해 두는 자리다. 모델 읽기만큼은 아니어도
            // 몇 초가 걸리므로 여기서도 말없이 멈춰 있지 않게 한다.
            OnLoading?.Invoke("말귀를 준비하는 중입니다…");

            return _picker = new LlmSession(
                _weights!, ContextParams(TeavelModelConfig.PickerContextSize), BuildPickerPrompt());
        }
        finally { _loadGate.Release(); }
    }

    /// <summary>
    /// 인자 뽑기용 문맥은 <b>실제로 필요할 때만</b> 만든다.
    /// 인자가 전부 경로인 도구(프린터 목록, 파일 살펴보기 등)는 이 단계를 아예 건너뛰므로,
    /// 미리 만들어 두면 쓰지도 않을 KV 캐시를 계속 물고 있게 된다.
    /// </summary>
    private async Task<LlmSession> FillerAsync(CancellationToken ct)
    {
        if (_filler is not null) return _filler;
        await EnsureModelAsync(ct).ConfigureAwait(false);

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _filler ??= new LlmSession(
                _weights!, ContextParams(TeavelModelConfig.FillerContextSize), FillerPrompt);
        }
        finally { _loadGate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _picker?.Dispose();
        _filler?.Dispose();
        _weights?.Dispose();
        _weights = null;
        _loadGate.Dispose();
    }
}
