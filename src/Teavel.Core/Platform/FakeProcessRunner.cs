namespace Teavel.Platform;

/// <summary>
/// 프로세스를 실제로 띄우지 않는 실행기 — 비Windows 개발·테스트용.
/// 무엇이 어떤 인자·stdin 으로 불렸는지 기록만 하고, 미리 정한 응답을 돌려준다.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    /// <summary>기록된 호출 한 건.</summary>
    public sealed record Invocation(string FileName, IReadOnlyList<string> Arguments, string? Stdin);

    private readonly List<Invocation> _calls = new();
    private readonly Dictionary<string, ProcessResult> _responses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>지금까지 기록된 호출들(순서대로).</summary>
    public IReadOnlyList<Invocation> Calls => _calls;

    /// <summary>실행 파일 이름별 응답. 지정하지 않으면 성공(빈 출력)으로 친다.</summary>
    public FakeProcessRunner Respond(string fileName, ProcessResult result)
    {
        _responses[fileName] = result;
        return this;
    }

    /// <summary><see cref="Exists"/> 가 true 를 돌려줄 실행 파일들.</summary>
    public HashSet<string> Available { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdin = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        _calls.Add(new Invocation(fileName, arguments.ToList(), stdin));
        var result = _responses.TryGetValue(fileName, out var r)
            ? r
            : new ProcessResult(0, "", "", false);
        return Task.FromResult(result);
    }

    public bool Exists(string fileName) => Available.Contains(fileName);

    public bool Launch(string fileName, IReadOnlyList<string>? arguments = null, bool useShellExecute = true)
    {
        _calls.Add(new Invocation(fileName, arguments?.ToList() ?? new List<string>(), null));
        return true;
    }
}
