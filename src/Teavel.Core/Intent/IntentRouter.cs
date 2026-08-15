using Teavel.Tools;

namespace Teavel.Intent;

/// <summary>무엇이 도구를 골랐는지.</summary>
public enum IntentSource
{
    /// <summary>낱말이 뚜렷하게 맞아떨어졌다.</summary>
    Keywords,

    /// <summary>언어 모델이 골랐다.</summary>
    Model,
}

/// <summary>교사의 말 한 마디에서 뽑아낸 도구 후보 하나.</summary>
/// <param name="Tool">고른 도구.</param>
/// <param name="Arguments">말에서 뽑아낸 인자(모자랄 수 있다 — CLI 가 나머지를 묻는다).</param>
/// <param name="Score">확신 정도(0~1).</param>
/// <param name="Source">무엇이 골랐는지.</param>
public sealed record IntentMatch(
    ToolSpec Tool,
    IReadOnlyDictionary<string, object> Arguments,
    double Score,
    IntentSource Source);

/// <summary>교사의 말에서 도구 후보를 뽑는다.</summary>
public interface IIntentRouter
{
    /// <summary>확신이 높은 순으로 후보를 돌려준다. 비어 있으면 못 알아들은 것.</summary>
    Task<IReadOnlyList<IntentMatch>> RouteAsync(string utterance, CancellationToken ct = default);
}

/// <summary>
/// 낱말로 도구를 고른다 — 모델 없이도 Teavel 이 동작하게 하는 바닥.
///
/// 도구가 13개뿐이고 각 도구의 예시 문장이 카탈로그에 있으므로, 낱말 겹침만으로도
/// 흔한 말("엑셀 합쳐줘", "누가 안 냈어")은 정확히 걸린다.
/// 모호할 때만 언어 모델을 부르면 되고, 그래서 대부분의 명령이 즉시 반응한다.
/// </summary>
public sealed class KeywordIntentRouter : IIntentRouter
{
    /// <summary>이 점수를 넘으면 모델에 물어볼 것 없이 바로 쓴다.</summary>
    public const double ConfidentScore = 0.55;

    // 어디에나 나오는 말은 변별력이 없어 점수에서 뺀다.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "해줘", "해", "좀", "이거", "그거", "저거", "파일", "폴더", "다", "전부", "모두",
        "만들어", "만들어줘", "줘", "하기", "에서", "으로", "로", "를", "을", "이", "가", "는", "은",
    };

    public Task<IReadOnlyList<IntentMatch>> RouteAsync(string utterance, CancellationToken ct = default)
    {
        var words = Tokenize(utterance);
        if (words.Count == 0)
            return Task.FromResult<IReadOnlyList<IntentMatch>>(Array.Empty<IntentMatch>());

        var scored = new List<IntentMatch>();

        foreach (var tool in ToolCatalog.All)
        {
            // 예시 문장과 유의어는 교사가 실제로 쓸 말이라 가장 무겁게 본다.
            // 제목·설명은 우리가 쓴 설명문이라 교사의 말투와 멀어 가볍게만 본다.
            var exampleScore = tool.Examples.Max(e => Overlap(words, Tokenize(e)));
            var aliasScore = tool.Aliases.Count == 0
                ? 0
                : tool.Aliases.Max(a => Overlap(words, Tokenize(a)));
            var titleScore = Overlap(words, Tokenize(tool.Title));
            var descScore = Overlap(words, Tokenize(tool.Description));

            var score = (Math.Max(exampleScore, aliasScore) * 0.6)
                      + (titleScore * 0.3)
                      + (descScore * 0.1);

            if (score > 0.05)
                scored.Add(new IntentMatch(tool, ExtractPaths(utterance, tool), score, IntentSource.Keywords));
        }

        IReadOnlyList<IntentMatch> result = scored
            .OrderByDescending(m => m.Score)
            .Take(5)
            .ToList();

        return Task.FromResult(result);
    }

    /// <summary>한국어를 형태소로 나누지 않고, 조사만 떼어 낸 낱말 뭉치로 만든다.</summary>
    private static List<string> Tokenize(string text)
    {
        var raw = text.Split(
            new[] { ' ', '\t', '\n', ',', '.', '!', '?', '"', '\'', '(', ')', '[', ']', '·' },
            StringSplitOptions.RemoveEmptyEntries);

        var words = new List<string>();
        foreach (var w in raw)
        {
            var t = w.Trim().ToLowerInvariant();
            if (t.Length < 2 || StopWords.Contains(t)) continue;

            words.Add(t);

            // "엑셀을" 처럼 조사가 붙은 꼴도 걸리도록 뒤 한 글자를 뗀 형태를 함께 넣는다.
            if (t.Length >= 3) words.Add(t[..^1]);
        }
        return words;
    }

    /// <summary>겹치는 낱말의 비율(0~1).</summary>
    private static double Overlap(IReadOnlyCollection<string> said, IReadOnlyCollection<string> tool)
    {
        if (said.Count == 0 || tool.Count == 0) return 0;

        // 어느 쪽이 길든 상관없이 본다. 한국어는 붙여 쓰는 합성어가 많아
        // 한쪽만 보면 절반을 놓친다 — 교사가 "압축파일" 이라 할 때 우리 낱말은 "압축" 이고,
        // "학급마다" 라 할 때 우리 낱말은 "학급" 이다.
        var hits = said.Count(s => tool.Any(t => Related(s, t)));

        return (double)hits / said.Count;
    }

    /// <summary>두 낱말이 같은 것을 가리키는지 — 한쪽이 다른 쪽을 품고 있으면 그렇다고 본다.</summary>
    private static bool Related(string a, string b)
    {
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;

        // 짧은 쪽이 두 글자는 돼야 한다. 한 글자를 허용하면 거의 모든 말이 서로 걸린다.
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= 2 && longer.Contains(shorter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 말 안에 그대로 적힌 경로(따옴표로 감쌌거나 C:\ 로 시작하는 것)를 인자로 미리 채운다.
    /// 나머지 인자는 CLI 가 교사에게 묻는다.
    /// </summary>
    private static Dictionary<string, object> ExtractPaths(string utterance, ToolSpec tool)
    {
        var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        var quoted = System.Text.RegularExpressions.Regex
            .Matches(utterance, @"""([^""]+)""|([A-Za-z]:\\[^\s""]+)")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .ToList();
        if (quoted.Count == 0) return args;

        // 첫 경로를 첫 번째 경로형 인자에 넣는다. 두 개 이상이면 순서대로.
        var pathParams = tool.Parameters
            .Where(p => p.Kind is ToolParamKind.FilePath or ToolParamKind.FolderPath or ToolParamKind.OutputPath)
            .ToList();

        for (var i = 0; i < Math.Min(quoted.Count, pathParams.Count); i++)
            args[pathParams[i].Name] = quoted[i];

        return args;
    }
}

/// <summary>
/// 낱말로 먼저 보고, 확신이 서지 않을 때만 언어 모델을 부른다.
///
/// 로컬 모델 추론은 교사 PC(GPU 없음)에서 수십 초가 걸릴 수 있다.
/// 흔한 명령을 낱말로 즉시 처리하면 모델을 부르는 일 자체가 드물어진다.
/// </summary>
public sealed class LayeredIntentRouter : IIntentRouter
{
    private readonly KeywordIntentRouter _keywords;
    private readonly IIntentRouter? _model;

    public LayeredIntentRouter(KeywordIntentRouter keywords, IIntentRouter? model)
    {
        _keywords = keywords;
        _model = model;
    }

    /// <summary>언어 모델을 쓸 수 있는지(모델 파일이 있는지).</summary>
    public bool ModelAvailable => _model is not null;

    public async Task<IReadOnlyList<IntentMatch>> RouteAsync(string utterance, CancellationToken ct = default)
    {
        var byKeyword = await _keywords.RouteAsync(utterance, ct).ConfigureAwait(false);

        if (byKeyword.Count > 0 && byKeyword[0].Score >= KeywordIntentRouter.ConfidentScore)
            return byKeyword;

        if (_model is null) return byKeyword;

        var byModel = await _model.RouteAsync(utterance, ct).ConfigureAwait(false);
        if (byModel.Count == 0) return byKeyword;

        // 모델이 고른 것을 앞에 두되, 낱말 후보도 남겨 교사가 바꿔 고를 수 있게 한다.
        var merged = new List<IntentMatch>(byModel);
        foreach (var k in byKeyword)
            if (!merged.Any(m => m.Tool.Id == k.Tool.Id))
                merged.Add(k);

        return merged;
    }
}
