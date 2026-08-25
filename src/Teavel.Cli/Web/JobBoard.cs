using System.Collections.Concurrent;

namespace Teavel.Cli.Web;

/// <summary>진행 한 줄. <paramref name="Kind"/> 는 화면에서 색으로 나타난다.</summary>
/// <param name="Kind">ok · warn · error · info · dim.</param>
public sealed record JobLine(string Kind, string Text);

/// <summary>
/// 오래 걸리는 일 하나.
/// </summary>
/// <remarks>
/// 팀 열일곱 개를 만들면 몇 분이 걸린다. 그동안 화면이 멈춰 있으면 관리자는 <b>끊긴 줄 알고
/// 창을 닫는다</b> — 그러면 절반만 만들어진 채로 끝난다. 그래서 시작만 하고 곧바로 답하고,
/// 화면이 진행을 받아 간다.
/// </remarks>
public sealed class Job
{
    private readonly List<JobLine> _lines = new();
    private readonly object _gate = new();

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Title { get; }
    public bool Done { get; private set; }
    public string Summary { get; private set; } = "";

    public Job(string title) => Title = title;

    public void Say(string kind, string text)
    {
        lock (_gate) _lines.Add(new JobLine(kind, text));
    }

    public void Ok(string text) => Say("ok", text);
    public void Warn(string text) => Say("warn", text);
    public void Error(string text) => Say("error", text);
    public void Info(string text) => Say("info", text);
    public void Dim(string text) => Say("dim", text);

    public void Details(IEnumerable<string> lines)
    {
        foreach (var l in lines)
            if (!string.IsNullOrWhiteSpace(l)) Dim(l);
    }

    public void Finish(string summary)
    {
        lock (_gate) { Summary = summary; Done = true; }
    }

    /// <summary><paramref name="from"/> 번째 줄부터. 화면은 받아 간 데까지를 기억한다.</summary>
    public IReadOnlyList<JobLine> Since(int from)
    {
        lock (_gate) return from >= _lines.Count ? Array.Empty<JobLine>() : _lines.Skip(from).ToList();
    }

    public int Count { get { lock (_gate) return _lines.Count; } }
}

/// <summary>
/// 돌고 있는 일들.
/// </summary>
/// <remarks>
/// <b>한 번에 하나만 돌린다.</b> 상주 PowerShell 이 하나뿐이라 두 개를 겹쳐 부르면
/// 답이 뒤섞인다. 화면에서 단추를 두 번 눌러도 두 번 도는 일이 없어야 한다.
/// </remarks>
public sealed class JobBoard
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly SemaphoreSlim _one = new(1, 1);

    public Job? Find(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    /// <summary>지금 돌고 있는 것이 있는지.</summary>
    public bool Busy => _one.CurrentCount == 0;

    /// <summary>
    /// 지금 돌고 있는 일. 없으면 <c>null</c>.
    /// </summary>
    /// <remarks>
    /// 상주 PowerShell 이 흘려보내는 진행 문구를 받을 곳이다. 그 문구는 콘솔로 바로 나가는데,
    /// 관리자는 브라우저를 보고 있다. <b>로그인 창 안내가 거기서만 나오면 아무도 못 본다.</b>
    /// </remarks>
    public Job? Current { get; private set; }

    /// <summary>일을 걸어 두고 곧바로 돌아온다.</summary>
    public Job Start(string title, Func<Job, CancellationToken, Task> work, CancellationToken ct)
    {
        var job = new Job(title);
        _jobs[job.Id] = job;

        _ = Task.Run(async () =>
        {
            await _one.WaitAsync(ct).ConfigureAwait(false);
            Current = job;
            try
            {
                await work(job, ct).ConfigureAwait(false);
                if (!job.Done) job.Finish("끝났습니다.");
            }
            catch (OperationCanceledException)
            {
                job.Warn("멈췄습니다.");
                job.Finish("멈췄습니다.");
            }
            catch (Exception ex)
            {
                job.Error(ex.Message);
                job.Finish("문제가 생겨 멈췄습니다.");
            }
            finally { Current = null; _one.Release(); }
        }, CancellationToken.None);

        return job;
    }

    /// <summary>오래된 것은 버린다. 한 판이 길어져도 메모리가 늘지 않게.</summary>
    public void Sweep()
    {
        if (_jobs.Count < 50) return;
        foreach (var kv in _jobs.Where(kv => kv.Value.Done).Take(_jobs.Count - 25))
            _jobs.TryRemove(kv.Key, out _);
    }
}
