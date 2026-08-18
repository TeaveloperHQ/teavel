using System.Runtime.InteropServices;

namespace Teavel.Cli;

/// <summary>
/// 콘솔에서 한 줄을 <b>글자 그대로</b> 읽는다.
///
/// <para>
/// 이것이 왜 따로 있는가 — <c>Console.ReadLine()</c> 은 콘솔이 준 <b>바이트</b>를
/// 입력 코드 페이지로 풀어서 준다. 그래서 코드 페이지가 한글을 담지 못하는 값이면
/// 선생님이 친 한글이 프로그램에 닿기 전에 <c>?</c> 로 바뀐다.
/// </para>
/// <para>
/// 실제로 이렇게 막혔다(2026-08-18). 화면에는 한글이 잘 나오는데 —
/// 그쪽은 출력 코드 페이지라 멀쩡했다 — 치는 말만 알아듣지 못했다.
/// </para>
/// <code>
///   &gt; 점검
///     ! 무슨 일인지 알아듣지 못했습니다.
///   &gt; 모델
///     ! 무슨 일인지 알아듣지 못했습니다.
/// </code>
/// <para>
/// <c>점검</c> 은 글자를 그대로 견주는 분기라 <b>틀릴 수가 없는 자리</b>다.
/// 그런데도 안 맞았다는 것은 프로그램에 닿은 글자가 <c>점검</c> 이 아니었다는 뜻이다.
/// 프로그램이 죽지도, 멈추지도 않았는데 <b>아무 말도 통하지 않으니</b>
/// 쓰는 쪽에서는 통째로 먹통이다 — 실제로 가장 나쁜 종류의 고장이었다.
/// </para>
/// <para>
/// 그래서 Windows 에서는 코드 페이지를 거치지 않는 <c>ReadConsoleW</c> 로 직접 읽는다.
/// 이 함수는 처음부터 UTF-16 을 주므로 어떤 코드 페이지에서도 한글이 상하지 않는다.
/// </para>
/// </summary>
internal static class ConsoleInput
{
    private const int StdInputHandle = -10;
    private const int ErrorOperationAborted = 995;   // Ctrl+C 로 읽기가 끊겼을 때

    /// <summary>한 번에 받을 수 있는 길이. 긴 경로를 끌어다 놓아도 남는 크기다.</summary>
    private const int BufferChars = 8192;

    /// <summary>한 줄을 읽는다. 입력이 끝났거나(Ctrl+Z·EOF) 끊겼으면(Ctrl+C) null.</summary>
    public static string? ReadLine()
    {
        // 파이프·파일로 들어오는 입력은 콘솔이 아니다. 그때는 평소 방식이 맞다.
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected)
            return Console.ReadLine();

        try
        {
            if (TryReadFromConsole(out var line)) return line;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            // 콘솔을 직접 읽지 못하는 환경이면 아래에서 평소 방식으로 내려간다.
        }

        return Console.ReadLine();
    }

    /// <summary>
    /// 콘솔에서 직접 읽는다.
    /// </summary>
    /// <param name="line">읽은 줄. 입력이 끝났거나 끊겼으면 null.</param>
    /// <returns>
    /// 콘솔에서 읽었으면 true. 이 자리가 콘솔이 아니어서 못 읽었으면 false —
    /// 그때만 평소 방식으로 물러선다.
    /// </returns>
    /// <remarks>
    /// '입력이 끝났다' 와 '여기서는 못 읽는다' 를 반드시 갈라야 한다. 둘을 뭉뚱그리면
    /// 콘솔이 아닌 환경에서 첫 줄부터 프로그램이 끝나거나, 반대로 Ctrl+C 를 눌렀는데
    /// 다시 읽으려 들어 빠져나가지 못한다.
    /// </remarks>
    private static bool TryReadFromConsole(out string? line)
    {
        line = null;

        var handle = GetStdHandle(StdInputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;

        var buffer = new char[BufferChars];

        if (!ReadConsoleW(handle, buffer, BufferChars, out var read, IntPtr.Zero))
        {
            // Ctrl+C 로 끊긴 것은 '못 읽는 자리' 가 아니라 '읽을 것이 없다' 이다.
            //
            // 여기서 빈 문자열을 주면 안 된다 — Ui.Confirm 은 빈 줄(그냥 Enter)을
            // '예' 로 읽으므로, "정말 지울까요?" 에서 Ctrl+C 를 누른 것이 <b>예</b> 가 된다.
            // null 이어야 '물어볼 사람이 없다' 로 읽혀 아니오가 된다.
            return Marshal.GetLastWin32Error() == ErrorOperationAborted;
        }

        // 0 글자는 Ctrl+Z(입력 끝)다. line 은 null 그대로 둔다.
        if (read == 0) return true;

        line = new string(buffer, 0, (int)read).TrimEnd('\r', '\n');
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsoleW(
        IntPtr hConsoleInput,
        [Out] char[] lpBuffer,
        uint nNumberOfCharsToRead,
        out uint lpNumberOfCharsRead,
        IntPtr pInputControl);
}
