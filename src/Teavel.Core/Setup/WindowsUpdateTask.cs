using Teavel.Platform;
using Teavel.Tools;

namespace Teavel.Setup;

/// <summary>
/// Windows 자신이 최신인지 본다. <b>다른 무엇보다 먼저다.</b>
///
/// <para>
/// 학교는 컴퓨터 세팅을 업체에 맡기고, 업체는 만들어 둔 이미지를 하드디스크에 그대로
/// 복사한다. 그 이미지는 만든 날에 멈춰 있어서, 교사가 처음 켤 때 이미 두 해 넘게 묵은
/// Windows 인 일이 흔하다. 한 대만 그런 것이 아니라 <b>그 학교 전체가 같은 상태</b>다.
/// </para>
/// <para>
/// 그러면 보안 패치가 안 오는 것으로 끝나지 않는다. <b>학교 계정을 잇는 일부터 막힌다</b> —
/// 계정을 잇는 부품이 Windows 안에 있는데 그게 옛날 것이기 때문이다. 실기에서
/// 그렇게 막혔다(22H2, 2024년 2월에 멈춘 이미지, 지원 종료는 2024-10-08).
/// </para>
/// <para>
/// 그래서 이 항목이 계정보다 앞에 있다. 여기가 막혀 있으면 아래 것을 아무리 붙들어도
/// 안 되고, 선생님은 왜 안 되는지 알 길이 없다.
/// </para>
/// </summary>
public sealed class WindowsUpdateTask : ISetupTask
{
    private readonly WindowsFacts _facts;
    private readonly ToolRunner _runner;
    private readonly IProcessRunner _proc;

    /// <summary>오늘. 시험에서 날짜를 고정할 수 있게 받아 둔다.</summary>
    private readonly Func<DateOnly> _today;

    public WindowsUpdateTask(
        WindowsFacts facts, ToolRunner runner, IProcessRunner proc, Func<DateOnly>? today = null)
    {
        _facts = facts;
        _runner = runner;
        _proc = proc;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
    }

    public string Id => "windows.update";
    public string Title => "Windows 최신 상태";

    public string Why =>
        "여기가 오래되면 아래 것들이 다 막힙니다. 학교 계정을 잇는 부품도 Windows 안에 있어서, "
      + "묵은 Windows 에서는 계정 연결부터 실패합니다.";

    private static readonly Dictionary<string, object> NoArgs = new();

    public async Task<CheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return CheckResult.NotApplicable("Windows 에서만 확인할 수 있습니다.");

        var support = _facts.Support(_today());
        var lines = new List<string>();

        var name = _facts.WindowsBuild >= WindowsSupport.Windows11Build ? "Windows 11" : "Windows 10";
        lines.Add($"{name} {support.Version}  (빌드 {_facts.WindowsBuild})");

        // 지금 상태를 물어본다. 지원이 끝났든 아니든 <b>지난번에 왜 실패했는지</b>는 알아야 한다.
        var res = await _runner
            .InvokeAsync("Teavel.Setup", "Get-TeavelUpdateStatus", NoArgs, 300, "업데이트 확인", ct)
            .ConfigureAwait(false);

        var count = Count(res.Details);

        // ① 지원이 끝났는지 — 이게 가장 무겁다. 밀린 개수보다 먼저 말한다.
        if (support.State == SupportState.Ended)
        {
            var days = support.DaysPastEnd(_today()) ?? 0;
            lines.Add("");
            lines.Add($"이 판은 {support.EndsOn:yyyy-MM-dd} 에 지원이 끝났습니다 ({days / 30}개월 지났습니다).");
            lines.Add("보안 패치가 아예 내려오지 않습니다. 개별 업데이트를 쌓는 것으로는 해결되지 않고,");
            lines.Add("판 자체를 올려야 합니다.");
            lines.Add("");
            lines.Add("학교에서 업체가 복사해 둔 이미지를 그대로 쓰시는 경우 흔히 이렇습니다.");
            lines.AddRange(HistoryLines(res.Details));
            lines.AddRange(EstimateLines(res.Details));

            return CheckResult.NeedsFix($"지원이 끝난 판입니다 — {support.Version}", lines.ToArray());
        }

        if (support.State == SupportState.EndingSoon)
        {
            lines.Add("");
            lines.Add($"이 판은 {support.EndsOn:yyyy-MM-dd} 에 지원이 끝납니다. 그 전에 올려 두시는 편이 좋습니다.");
            if (count > 0) lines.Add($"받아야 할 업데이트도 {count}개 있습니다.");

            return CheckResult.NeedsFix($"곧 지원이 끝납니다 — {support.Version}", lines.ToArray());
        }

        if (support.State == SupportState.Unknown)
        {
            lines.Add("");
            lines.Add("이 판이 언제까지 지원되는지 확인하지 못했습니다.");
            lines.Add("Windows 업데이트 화면에서 한 번 확인해 주세요.");
            return CheckResult.Unknown("판을 확인하지 못했습니다.", lines.ToArray());
        }

        if (count is null)
        {
            lines.Add("");
            lines.AddRange(res.Details.Where(d => !d.Contains('=')));
            return CheckResult.Unknown("업데이트를 확인하지 못했습니다.", lines.ToArray());
        }

        var upgrades = Titles(res.Details, "upgrade=").ToList();

        if (count > 0)
        {
            lines.Add("");
            foreach (var title in Titles(res.Details, "update=").Take(8)) lines.Add($"  · {title}");
            if (count > 8) lines.Add($"  … 그 밖에 {count - 8}개");

            if (upgrades.Count > 0)
            {
                lines.Add("");
                lines.Add($"판 올리기도 나와 있습니다: {upgrades[0]}");
                lines.Add("그건 오래 걸려서 따로 여쭙습니다 — 여기서는 나머지만 설치합니다.");
            }

            lines.AddRange(HistoryLines(res.Details));

            return CheckResult.NeedsFix($"받아야 할 업데이트가 {count}개 있습니다.", lines.ToArray());
        }

        // 보안 패치는 다 받았는데 판 올리기만 나와 있는 경우.
        // 아직 지원되는 판이라 급하지 않다 — 알리되 '손봐야 할 것' 으로 세지 않는다.
        if (upgrades.Count > 0)
        {
            lines.Add("");
            lines.Add($"판 올리기가 나와 있습니다: {upgrades[0]}");
            lines.Add("지금 판도 아직 지원되므로 급하지는 않습니다. 시간 있으실 때 하시면 됩니다.");
            return CheckResult.Ok("보안 패치는 최신입니다.", lines.ToArray());
        }

        return CheckResult.Ok("최신입니다.", lines.ToArray());
    }

    public async Task<FixResult> FixAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return FixResult.NotSupported("Windows 에서만 할 수 있습니다.");

        var support = _facts.Support(_today());

        // 판 올리기는 설정 화면으로 보낸다.
        //
        // 우리가 대신 눌러 줄 수도 있지만 그러지 않는다. 한 시간 넘게 걸리고 도중에 여러 번
        // 다시 시작하는 일이라, <b>진행률이 보이는 쪽</b>이 훨씬 낫다. 콘솔 뒤에서 조용히
        // 돌리면 되고 있는지 멈춘 것인지 알 길이 없고, 수업 직전에 시작되면 그 시간에
        // 컴퓨터를 못 쓴다. 언제 할지는 선생님이 정하시는 편이 맞다.
        if (support.State is SupportState.Ended or SupportState.EndingSoon)
        {
            await _runner
                .InvokeAsync("Teavel.Setup", "Open-TeavelUpdateSetting", NoArgs, 60, "업데이트 화면", ct)
                .ConfigureAwait(false);

            return FixResult.Manual(
                "업데이트 화면을 열었습니다. 판 올리기는 직접 눌러 주세요.",
                "[업데이트 확인] → 나오는 것을 모두 설치 → 판 올리기가 보이면 그것도.",
                "",
                "이건 설정 화면에서 하시는 편이 낫습니다:",
                "  · 어디까지 됐는지 진행률이 보입니다",
                "  · 중간에 멈추거나 미룰 수 있습니다",
                "  · 한 시간쯤 걸리고 여러 번 다시 시작합니다 — 수업 없는 시간에 하세요",
                "",
                "다 하신 뒤 'teavel 점검' 을 다시 실행하시면 이어서 도와드립니다.");
        }

        // 밀린 누적 업데이트는 우리가 받아서 설치할 수 있다 — 관리자 권한이 있으면.
        //
        // 권한이 없으면 여기서 <b>두 길을 나란히 보여 주고 고르시게 한다.</b>
        // 어느 한쪽이 늘 낫지 않다 — 자리를 비울 거면 대신 하는 쪽이, 지켜보고 싶으면
        // 설정 화면 쪽이 낫다. 우리가 정할 일이 아니다.
        if (!Elevation.IsElevated)
            return FixResult.NeedsAdmin(
                "업데이트를 설치하려면 관리자 권한이 필요합니다.",
                "두 가지 방법이 있습니다.",
                "",
                "  Teavel 이 대신 설치   승인 창 한 번이면 됩니다. 자리를 비우셔도 됩니다.",
                "                        대신 진행률은 안 보입니다.",
                "",
                "  설정 화면에서 직접    어디까지 됐는지 보이고 중간에 멈출 수 있습니다.",
                "                        설정 > Windows 업데이트 에서 하시면 됩니다.",
                "",
                "대신 하길 원하시면 아래 물음에 [예] 를,",
                "직접 하시려면 [아니오] 를 누르고 설정 > Windows 업데이트 를 여세요.");

        var res = await _runner
            .InvokeAsync("Teavel.Setup", "Install-TeavelUpdates", NoArgs, 3600, "업데이트 설치", ct)
            .ConfigureAwait(false);

        if (!res.Ok) return FixResult.Failed(res.Message, res.Details.ToArray());

        var reboot = res.Details.Any(d => d.Equals("reboot=True", StringComparison.OrdinalIgnoreCase));

        return reboot
            ? FixResult.Fixed(res.Message) with
              {
                  NextSteps = new[]
                  {
                      "다시 시작해야 마무리됩니다.",
                      "다시 시작한 뒤 'teavel 점검' 을 실행하시면 이어서 도와드립니다.",
                  },
              }
            : FixResult.Fixed(res.Message);
    }

    /// <summary>
    /// 지난번에 무엇이 왜 실패했는지 — 있으면 그 줄들.
    /// </summary>
    /// <summary>
    /// 판 올리기가 얼마나 걸릴지 — <b>짐작이라고 밝히고</b> 범위로 말한다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 이게 없으면 대응이 안 된다. 실제로 한나절을 잡아먹은 일이 있었다 —
    /// 얼마나 걸릴지 몰라서 그냥 시작했고, 그동안 그 컴퓨터를 못 썼다.
    /// </para>
    /// <para>
    /// 정확히는 못 맞힌다. 그러니 <b>맞히려 하지 않고 무엇에 달렸는지를 말한다.</b>
    /// 설치는 디스크가 가르고, 받기는 학교 인터넷이 가른다. 그리고 하루가 가는 진짜 까닭은
    /// 대개 한 번에 안 끝나서다 — 되돌려지면 그만큼 다시다. 그 말을 반드시 적는다.
    /// </para>
    /// </remarks>
    private IEnumerable<string> EstimateLines(IReadOnlyList<string> details)
    {
        var ssd = Value(details, "disk=") is { } disk
               && disk.Contains("SSD", StringComparison.OrdinalIgnoreCase);

        var install = ssd ? "30분 ~ 1시간" : "1 ~ 3시간";
        var note = ssd ? "이 컴퓨터는 SSD 라 빠른 편입니다" : "이 컴퓨터는 하드디스크라 오래 걸립니다";

        yield return "";
        yield return "얼마나 걸리는지 (짐작입니다)";
        yield return "  받기      20분 ~ 2시간   4GB 안팎입니다. 학교 인터넷 속도에 달렸습니다";
        yield return $"  설치      {install,-13}{note}";
        yield return "  다시 시작  2 ~ 4번        그동안 이 컴퓨터를 못 씁니다";

        // 얼마나 밀렸느냐가 시간을 가장 크게 가른다.
        //
        // 처음에는 이걸 안 보고 "두세 시간" 이라고만 적었는데, 2년 밀린 컴퓨터에서
        // 세 시간을 넘겼다는 말을 듣고 고쳤다. 밀린 만큼 거쳐 갈 것이 늘어나서
        // 판 올리기 한 번으로 끝나지 않는다.
        var stale = StaleYears(details);

        yield return "";
        if (stale is >= 2)
        {
            yield return $"이 컴퓨터는 {stale}년 가까이 밀려 있습니다. 그만큼 더 걸립니다 —";
            yield return "실제로 2년 밀린 컴퓨터에서 세 시간을 넘긴 적이 있습니다.";
            yield return "반나절은 비워 두시는 편이 안전합니다.";
        }
        else if (stale is >= 1)
        {
            yield return $"이 컴퓨터는 {stale}년쯤 밀려 있습니다. 서너 시간은 잡으세요.";
        }
        else
        {
            yield return "한 번에 끝나면 두세 시간입니다.";
        }

        yield return "도중에 되돌려지면 그만큼 다시 걸립니다 — 하루가 가는 것은 대개 이 때문입니다.";
        yield return "수업 없는 시간에 시작하시고, 전원은 꽂아 두세요.";

        if (Value(details, "freegb=") is { } free && int.TryParse(free, out var gb) && gb < 25)
            yield return $"※ C: 여유 공간이 {gb}GB 뿐입니다. 판 올리기에는 25GB 쯤 필요합니다.";
    }

    /// <summary>몇 해나 밀렸는지. 못 재면 null.</summary>
    /// <remarks>
    /// 이 Windows 가 디스크에 놓인 날(업체가 이미지를 뜬 날)부터 센다.
    /// 그 뒤로 판 올리기를 했으면 그 날짜가 갱신되므로, 밀린 기간과 대체로 맞는다.
    /// </remarks>
    private int? StaleYears(IReadOnlyList<string> details)
        => Value(details, "laid=") is { } laid && DateOnly.TryParse(laid, out var when)
            ? (_today().DayNumber - when.DayNumber) / 365
            : null;

    /// <remarks>
    /// <para>
    /// <b>본 것만 적고 까닭은 말하지 않는다.</b> 실패 코드 하나가 여러 사연을 가리킨다 —
    /// 0xC1900101 은 드라이버가 안 맞아서일 수도, 설치 중 전원이 꺼져서일 수도,
    /// 사람이 멈춰서일 수도 있다. 우리가 하나로 정해 말하면 엉뚱한 데를 고치게 된다.
    /// </para>
    /// <para>
    /// 그래서 한 줄이다. 이 줄의 쓸모는 <b>같은 자리에서 또 걸렸을 때 알아보는 것</b>이지,
    /// 원인을 짚어 주는 것이 아니다.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> HistoryLines(IReadOnlyList<string> details)
    {
        var title = Value(details, "failtitle=");
        if (title is null) yield break;

        var code = Value(details, "failcode=");
        var date = Value(details, "faildate=");

        yield return "";
        yield return $"지난번({date})에 '{title}' 이(가) {code} 로 끝났습니다.";
    }

    private static string? Value(IEnumerable<string> details, string prefix)
        => details.FirstOrDefault(d => d.StartsWith(prefix, StringComparison.Ordinal)) is { } hit
           && hit.Length > prefix.Length
            ? hit[prefix.Length..]
            : null;

    /// <summary>"count=12" 를 읽는다. 못 읽었으면 null.</summary>
    private static int? Count(IEnumerable<string> details)
        => details.FirstOrDefault(d => d.StartsWith("count=", StringComparison.Ordinal)) is { } hit
           && int.TryParse(hit["count=".Length..], out var n)
            ? n
            : null;

    private static IEnumerable<string> Titles(IEnumerable<string> details, string prefix)
        => details.Where(d => d.StartsWith(prefix, StringComparison.Ordinal))
                  .Select(d => d[prefix.Length..]);
}
