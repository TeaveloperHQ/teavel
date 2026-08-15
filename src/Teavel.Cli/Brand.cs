using System.Runtime.InteropServices;
using System.Text;

namespace Teavel.Cli;

/// <summary>
/// teaveloper 브랜드 — 죽방(竹防) 엠블럼과 공식 그라데이션.
///
/// 색과 형상은 teaveloper 공용 자산에서 가져왔다(teaveloper-runner/assets/icon.svg,
/// seat-shuffler/branding). 형제 앱들과 같아 보여야 하므로 여기서 임의로 바꾸지 않는다.
/// </summary>
public static class Brand
{
    /// <summary>공식 그라데이션 — 인디고 → 바이올렛 → 시안.</summary>
    private static readonly (byte R, byte G, byte B)[] Gradient =
    {
        (0x63, 0x66, 0xf1),   // #6366f1
        (0x8b, 0x5c, 0xf6),   // #8b5cf6
        (0x06, 0xb6, 0xd4),   // #06b6d4
    };

    /// <summary>
    /// 죽방 엠블럼 — 원(머리) 아래로 삼각(그물)이 이어진다.
    /// </summary>
    /// <remarks>
    /// 공용 SVG 의 두 도형을 터미널 칸으로 옮긴 것이다:
    /// <code>
    ///   &lt;circle cx="64" cy="40" r="24"/&gt;          원: 지름 48
    ///   &lt;path d="M64 53 L43 112 L85 112 Z"/&gt;      삼각: 꼭짓점이 원 안(y=53)에서 시작
    /// </code>
    /// 삼각형 꼭짓점이 원과 겹쳐 시작하므로 둘 사이에 기둥이나 틈이 없다.
    /// 밑변(42)이 원 지름(48)과 비슷해, 아래로 갈수록 넓어지되 원보다 넓어지지는 않는다.
    /// </remarks>
    private static readonly string[] Emblem =
    {
        "  ▄███▄  ",
        " ███████ ",
        "  ▀███▀  ",
        "   ▄█▄   ",
        "  ▄███▄  ",
        " ▄█████▄ ",
    };

    /// <summary>엠블럼 오른쪽에 붙는 글. 줄 수는 엠블럼과 맞춘다.</summary>
    private static readonly string[] Wordmark =
    {
        "",
        "T E A V E L",
        "선생님 컴퓨터를 대신 세팅해 드립니다",
        "",
        "처음이시면  점검  을 쳐 보세요.",
        "목록 · 점검 · 고침 · 나가기",
    };

    private static bool? _colorSupported;

    /// <summary>24비트 색을 쓸 수 있는지. 한 번만 판단하고 기억한다.</summary>
    private static bool ColorSupported => _colorSupported ??= DetectColorSupport();

    private static bool DetectColorSupport()
    {
        // 파일이나 파이프로 넘길 때는 색 코드가 쓰레기 문자로 남는다.
        if (Console.IsOutputRedirected) return false;

        // https://no-color.org — 값과 상관없이 존재하면 색을 끈다.
        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null) return false;

        if (!OperatingSystem.IsWindows()) return true;

        // Windows 콘솔은 ANSI 처리가 꺼져 있을 수 있다. 켜지지 않으면 색을 포기한다
        // (억지로 쓰면 화면에 ←[38;2;… 같은 글자가 그대로 찍힌다).
        return TryEnableVirtualTerminal();
    }

    // ── Windows 콘솔 ANSI 활성화 ──
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private static bool TryEnableVirtualTerminal()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            if (!GetConsoleMode(handle, out var mode)) return false;
            if ((mode & EnableVirtualTerminalProcessing) != 0) return true;
            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch { return false; }
    }

    /// <summary>0~1 위치의 그라데이션 색을 낸다.</summary>
    private static (byte R, byte G, byte B) ColorAt(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var span = 1.0 / (Gradient.Length - 1);
        var i = Math.Min((int)(t / span), Gradient.Length - 2);
        var local = (t - i * span) / span;

        var (r1, g1, b1) = Gradient[i];
        var (r2, g2, b2) = Gradient[i + 1];

        return (
            (byte)(r1 + (r2 - r1) * local),
            (byte)(g1 + (g2 - g1) * local),
            (byte)(b1 + (b2 - b1) * local));
    }

    /// <summary>시작 배너를 찍는다.</summary>
    public static void PrintBanner()
    {
        var rows = Math.Max(Emblem.Length, Wordmark.Length);

        Console.WriteLine();
        for (var i = 0; i < rows; i++)
        {
            var art = i < Emblem.Length ? Emblem[i] : new string(' ', Emblem[0].Length);
            var text = i < Wordmark.Length ? Wordmark[i] : "";

            // 엠블럼은 위에서 아래로 그라데이션 — SVG 의 대각선 그라데이션을 세로로 옮긴 것.
            var (r, g, b) = ColorAt(rows <= 1 ? 0 : (double)i / (rows - 1));

            var line = new StringBuilder();
            line.Append("  ");
            if (ColorSupported) line.Append($"[38;2;{r};{g};{b}m");
            line.Append(art);
            if (ColorSupported) line.Append("[0m");

            if (text.Length > 0)
            {
                line.Append("   ");
                // 첫 글자줄(T E A V E L)만 밝게, 나머지는 흐리게.
                if (ColorSupported)
                    line.Append(i == 1 ? "[1;97m" : "[90m");
                line.Append(text);
                if (ColorSupported) line.Append("[0m");
            }

            Console.WriteLine(line.ToString());
        }
        Console.WriteLine();
    }

    /// <summary>한 줄짜리 짧은 표식(대화가 아닌 명령에서 쓴다).</summary>
    public static void PrintMark(string subtitle)
    {
        var (r, g, b) = ColorAt(0.35);
        var line = new StringBuilder("  ");
        if (ColorSupported) line.Append($"[38;2;{r};{g};{b}m");
        line.Append("▲ Teavel");
        if (ColorSupported) line.Append("[0m");
        if (subtitle.Length > 0)
        {
            if (ColorSupported) line.Append("[90m");
            line.Append("  ").Append(subtitle);
            if (ColorSupported) line.Append("[0m");
        }
        Console.WriteLine();
        Console.WriteLine(line.ToString());
    }
}
