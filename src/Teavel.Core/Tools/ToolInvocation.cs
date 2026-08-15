using Teavel.Platform;

namespace Teavel.Tools;

/// <summary>실행할 도구 하나 + 채워진 인자들.</summary>
/// <param name="Tool">고를 도구.</param>
/// <param name="Arguments">인자 이름 → 값. 값은 문자열/정수/불리언 중 하나.</param>
public sealed record ToolInvocation(ToolSpec Tool, IReadOnlyDictionary<string, object> Arguments)
{
    /// <summary>교사에게 "이걸 실행합니다" 하고 보여줄 한 줄.</summary>
    public string Describe()
    {
        if (Arguments.Count == 0) return Tool.Title;
        var parts = Arguments.Select(kv =>
        {
            var label = Tool.Param(kv.Key)?.Label ?? kv.Key;
            return $"{label}={kv.Value}";
        });
        return $"{Tool.Title} ({string.Join(", ", parts)})";
    }
}

/// <summary>인자 검증 결과.</summary>
/// <param name="Errors">교사에게 보여줄 오류 문장들. 비어 있으면 통과.</param>
/// <param name="Normalized">기본값이 채워지고 경로가 펼쳐진 최종 인자.</param>
public sealed record ValidationResult(IReadOnlyList<string> Errors, IReadOnlyDictionary<string, object> Normalized)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>도구 선언에 비추어 인자를 검사하고 정규화한다.</summary>
public sealed class ToolArgumentValidator
{
    private readonly ISystemPaths _paths;

    public ToolArgumentValidator(ISystemPaths paths) => _paths = paths;

    /// <summary>
    /// 인자를 검사한다. 경로는 환경변수를 펼치고, 빠진 선택 인자는 기본값으로 채운다.
    /// LLM 이 지어낸 인자 이름은 오류로 잡는다(조용히 버리면 교사가 엉뚱한 결과를 받는다).
    /// </summary>
    public ValidationResult Validate(ToolSpec tool, IReadOnlyDictionary<string, object> args)
    {
        var errors = new List<string>();
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in args.Keys)
            if (tool.Param(name) is null)
                errors.Add($"'{tool.Id}' 에는 '{name}' 이라는 입력이 없습니다.");

        foreach (var p in tool.Parameters)
        {
            var present = args.TryGetValue(p.Name, out var raw) && raw is not null
                          && !(raw is string s0 && string.IsNullOrWhiteSpace(s0));

            if (!present)
            {
                if (p.Required) errors.Add($"'{p.Label}' 을(를) 알려주셔야 합니다. ({p.Description})");
                else if (p.Default is not null) result[p.Name] = Coerce(p, p.Default, errors);
                continue;
            }

            result[p.Name] = Coerce(p, raw!, errors);
        }

        return new ValidationResult(errors, result);
    }

    private object Coerce(ToolParam p, object raw, List<string> errors)
    {
        var text = raw as string ?? raw.ToString() ?? "";

        switch (p.Kind)
        {
            case ToolParamKind.Number:
                if (raw is int i) return i;
                if (long.TryParse(text.Trim(), out var n)) return (int)n;
                errors.Add($"'{p.Label}' 은(는) 숫자여야 합니다. (받은 값: {text})");
                return 0;

            case ToolParamKind.Bool:
                if (raw is bool b) return b;
                var t = text.Trim().ToLowerInvariant();
                if (t is "true" or "yes" or "y" or "1" or "예" or "네") return true;
                if (t is "false" or "no" or "n" or "0" or "아니오" or "아니요") return false;
                errors.Add($"'{p.Label}' 은(는) 예/아니오 여야 합니다. (받은 값: {text})");
                return false;

            case ToolParamKind.Choice:
                var choice = (p.Choices ?? Array.Empty<string>())
                    .FirstOrDefault(c => string.Equals(c, text.Trim(), StringComparison.OrdinalIgnoreCase));
                if (choice is null)
                    errors.Add($"'{p.Label}' 은(는) {string.Join(" / ", p.Choices ?? Array.Empty<string>())} 중 하나여야 합니다. (받은 값: {text})");
                return choice ?? text.Trim();

            case ToolParamKind.FilePath:
            {
                var path = _paths.Expand(text.Trim().Trim('"'));
                if (!File.Exists(path)) errors.Add($"파일을 찾지 못했습니다: {path}");
                return path;
            }

            case ToolParamKind.FolderPath:
            {
                var path = _paths.Expand(text.Trim().Trim('"'));
                if (!Directory.Exists(path)) errors.Add($"폴더를 찾지 못했습니다: {path}");
                return path;
            }

            case ToolParamKind.OutputPath:
            {
                var path = _paths.Expand(text.Trim().Trim('"'));
                var parent = Path.GetDirectoryName(path);
                // 부모까지 없으면 교사가 경로를 잘못 말한 것 — 만들어주지 않고 알린다.
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    errors.Add($"저장할 위치의 상위 폴더가 없습니다: {parent}");
                return path;
            }

            default:
                return text.Trim();
        }
    }
}
