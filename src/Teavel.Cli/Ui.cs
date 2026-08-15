using Teavel.Setup;

namespace Teavel.Cli;

/// <summary>
/// 콘솔 출력. 교사가 읽을 화면이므로 규칙이 하나 있다 — 무슨 일이 일어났는지,
/// 그리고 다음에 무엇을 하면 되는지가 항상 보여야 한다.
/// </summary>
public static class Ui
{
    private static void Write(string text, ConsoleColor color)
    {
        var before = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = before;
    }

    public static void Title(string text)
    {
        Console.WriteLine();
        Write(text, ConsoleColor.White);
        Write(new string('─', Math.Min(text.Length * 2, 60)), ConsoleColor.DarkGray);
    }

    public static void Ok(string text) => Write("  ✓ " + text, ConsoleColor.Green);
    public static void Warn(string text) => Write("  ! " + text, ConsoleColor.Yellow);
    public static void Error(string text) => Write("  ✗ " + text, ConsoleColor.Red);
    public static void Info(string text) => Write("  · " + text, ConsoleColor.Gray);
    public static void Plain(string text) => Console.WriteLine(text);
    public static void Dim(string text) => Write(text, ConsoleColor.DarkGray);

    /// <summary>들여쓴 자세한 줄들.</summary>
    public static void Details(IEnumerable<string> lines)
    {
        foreach (var l in lines)
            Write(string.IsNullOrWhiteSpace(l) ? "" : "      " + l, ConsoleColor.DarkGray);
    }

    /// <summary>진단 결과 한 항목을 상태 표시와 함께 찍는다.</summary>
    public static void Check(string title, CheckResult result)
    {
        switch (result.State)
        {
            case CheckState.Ok: Ok($"{title} — {result.Summary}"); break;
            case CheckState.NeedsFix: Warn($"{title} — {result.Summary}"); break;
            case CheckState.NotApplicable: Dim($"  - {title} — {result.Summary}"); break;
            default: Info($"{title} — {result.Summary}"); break;
        }
        Details(result.Lines);
    }

    /// <summary>수정 결과를 찍는다. 교사가 직접 해야 할 순서는 눈에 띄게.</summary>
    public static void Fix(string title, FixResult result)
    {
        switch (result.Outcome)
        {
            case FixOutcome.AlreadyOk: Ok($"{title} — {result.Summary}"); break;
            case FixOutcome.Fixed: Ok($"{title} — {result.Summary}"); break;
            case FixOutcome.ManualStepStarted: Warn($"{title} — {result.Summary}"); break;
            case FixOutcome.NotSupported: Dim($"  - {title} — {result.Summary}"); break;
            default: Error($"{title} — {result.Summary}"); break;
        }

        if (result.Steps.Count > 0)
        {
            Console.WriteLine();
            foreach (var s in result.Steps)
                Write(string.IsNullOrWhiteSpace(s) ? "" : "      " + s, ConsoleColor.Cyan);
            Console.WriteLine();
        }
    }

    /// <summary>한 줄 입력을 받는다. Ctrl+C·EOF 면 null.</summary>
    public static string? Ask(string prompt)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(prompt);
        Console.ForegroundColor = ConsoleColor.Gray;
        var line = Console.ReadLine();
        Console.ResetColor();
        return line;
    }

    /// <summary>예/아니오를 묻는다. 기본은 '예'.</summary>
    public static bool Confirm(string question)
    {
        while (true)
        {
            var answer = Ask($"{question} [Y/n] ")?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(answer) || answer is "y" or "yes" or "ㅇ" or "예" or "네") return true;
            if (answer is "n" or "no" or "ㄴ" or "아니오" or "아니요") return false;
        }
    }
}
