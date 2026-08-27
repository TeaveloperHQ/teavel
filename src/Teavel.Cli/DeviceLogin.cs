using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Teavel.Cli;

/// <summary>
/// 코드를 적어 넣는 로그인 — 그 자리를 사람이 덜 하게 만든다.
///
/// <para>
/// 팀 로그인은 <b>창이 뜨지 않는다.</b> 상주 세션에는 창 손잡이가 없어서 창 방식이
/// 실패했고(<c>A window handle must be configured</c>), 그래서 코드 방식으로 갔다.
/// 그건 되는 길이지만, 관리자가 받아 드는 것은 이런 영어 한 줄이다.
/// </para>
/// <code>
/// To sign in, use a web browser to open the page https://login.microsoft.com/device
/// and enter the code ISGSV8ACB to authenticate.
/// </code>
/// <para>
/// 여기서 주소와 코드를 눈으로 골라내고 손으로 옮겨 적어야 한다. <c>I</c> 와 <c>1</c>,
/// <c>O</c> 와 <c>0</c> 이 섞이면 더 그렇다. 실기에서 <b>창이 뜨기를 기다리다 멈췄다</b>(2026-08-27).
/// </para>
/// <para>
/// 창은 우리가 못 띄운다 — 그건 마이크로소프트의 로그인 창이다. 그렇지만
/// <b>그 페이지를 대신 열어 주고 코드를 따로 크게 보여 줄 수는 있다.</b> 그러면 남는 일은
/// 붙여 넣기 하나다.
/// </para>
/// </summary>
public static class DeviceLogin
{
    /// <remarks>
    /// 문구에 기대지 않고 <b>주소와 코드의 생김새</b>로 찾는다. 이 줄은 마이크로소프트가
    /// 내는 것이라 판이나 테넌트 언어에 따라 말이 달라질 수 있다.
    /// </remarks>
    private static readonly Regex Address = new(@"https://\S*(?:device|deviceauth)\S*", RegexOptions.IgnoreCase);

    // 코드는 대문자·숫자 덩어리다. 짧은 낱말이 섞이지 않게 길이를 못 박는다.
    private static readonly Regex Code = new(@"\b([A-Z0-9]{7,12})\b");

    /// <summary>진행 한 줄에서 주소와 코드를 뽑는다. 없으면 <c>false</c>.</summary>
    public static bool TryRead(string line, out string url, out string code)
    {
        url = "";
        code = "";

        var a = Address.Match(line ?? "");
        if (!a.Success) return false;

        // 주소 뒤쪽에서 코드를 찾는다. 주소 안의 글자를 코드로 잘못 집지 않게.
        var tail = line![(a.Index + a.Length)..];
        var c = Code.Match(tail);
        if (!c.Success) return false;

        url = a.Value.TrimEnd('.', ',');
        code = c.Groups[1].Value;
        return true;
    }

    /// <summary>
    /// 그 페이지를 기본 브라우저로 연다.
    /// </summary>
    /// <remarks>
    /// 못 열어도 판이 끝나지는 않는다 — 주소는 화면에 그대로 있으니 손으로 여시면 된다.
    /// </remarks>
    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 주소는 화면에 남아 있다 */ }
    }
}
