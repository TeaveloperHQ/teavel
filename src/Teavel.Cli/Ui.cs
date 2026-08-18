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

    /// <summary>
    /// 한 줄 입력을 받는다. Ctrl+C·EOF 면 null.
    /// </summary>
    /// <remarks>
    /// <see cref="ConsoleInput"/> 로 읽는다 — <c>Console.ReadLine()</c> 을 그대로 쓰면
    /// 콘솔 코드 페이지에 따라 <b>선생님이 친 한글이 <c>?</c> 로 바뀌어</b> 도착한다.
    /// 그러면 '점검' 같은 글자 그대로 견주는 명령까지 안 맞아 아무 말도 통하지 않는다.
    /// </remarks>
    public static string? Ask(string prompt)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(prompt);
        Console.ForegroundColor = ConsoleColor.Gray;
        var line = ConsoleInput.ReadLine();
        Console.ResetColor();
        return line;
    }

    /// <summary>고를 수 있는 것 하나.</summary>
    /// <param name="Key">돌려줄 값. 보통 "1" · "2" 같은 번호.</param>
    /// <param name="Label">화면에 보여 줄 줄.</param>
    /// <param name="Words">이 갈래를 가리킬 때 쓸 법한 말들. 숫자 대신 이렇게 쳐도 알아듣는다.</param>
    public sealed record Choice(string Key, string Label, params string[] Words);

    /// <summary>
    /// 번호로도, <b>말로도</b> 고를 수 있게 묻는다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 선생님들은 중간에 번호 대신 하고 싶은 말을 그냥 친다 —
    /// "이건 작년 거야", "지워줘", "그냥 둬". 그때 숫자가 아니라고 기본값으로 넘겨 버리면
    /// <b>말한 것과 다른 일이 벌어진다.</b> 지우자고 했는데 그냥 두거나, 그 반대이거나.
    /// </para>
    /// <para>
    /// 그래서 말도 받는다. 알아듣지 못하면 넘겨짚지 않고 다시 묻되,
    /// 이렇게 말해도 된다는 것을 함께 보여 준다.
    /// </para>
    /// </remarks>
    public static string Choose(string prompt, IReadOnlyList<Choice> choices, string defaultKey)
    {
        foreach (var c in choices) Plain("        " + c.Label);

        while (true)
        {
            var line = Ask($"        {prompt} [{defaultKey}] ");
            if (line is null) return defaultKey;               // EOF — 물어볼 사람이 없다

            var t = line.Trim();
            if (t.Length == 0) return defaultKey;

            // 번호를 그대로 쳤을 때.
            var byKey = choices.FirstOrDefault(c => string.Equals(c.Key, t, StringComparison.OrdinalIgnoreCase));
            if (byKey is not null) return byKey.Key;

            // 말로 쳤을 때. 여러 갈래가 걸리면 넘겨짚지 않는다.
            var said = t.Replace(" ", "").ToLowerInvariant();
            var hits = choices
                .Where(c => c.Words.Any(w => said.Contains(w.Replace(" ", "").ToLowerInvariant(),
                                                          StringComparison.Ordinal)))
                .ToList();

            if (hits.Count == 1) return hits[0].Key;

            Console.WriteLine();
            Warn(hits.Count > 1
                ? "여러 가지로 들립니다. 하나만 골라 주세요."
                : "무슨 말씀인지 알아듣지 못했습니다.");
            Dim("        번호를 치셔도 되고, 이렇게 말씀하셔도 됩니다:");
            foreach (var c in choices.Where(c => c.Words.Length > 0))
                Dim($"          {c.Key} — {string.Join(" · ", c.Words.Take(3))}");
        }
    }

    /// <summary>
    /// 예/아니오를 묻는다. 그냥 Enter 는 기본값(<paramref name="defaultYes"/>).
    /// </summary>
    /// <param name="defaultYes">
    /// 그냥 Enter 를 눌렀을 때의 답. <b>되돌릴 수 없는 일에는 false 를 준다</b> —
    /// 파일을 지우는 물음에서 무심코 누른 Enter 가 '예' 가 되면 안 된다.
    /// </param>
    /// <remarks>
    /// 입력이 아예 끊긴 경우(파이프로 돌리거나 Ctrl+D)는 <b>'아니오'</b> 로 본다.
    /// 사람이 Enter 를 누른 것과 아무도 없는 것은 다르다 — 아무도 없는데 파일을 바꾸는
    /// 작업을 진행하면 안 된다. 자동으로 넘기려면 --yes 를 쓰면 된다.
    /// </remarks>
    public static bool Confirm(string question, bool defaultYes = true)
    {
        while (true)
        {
            var line = Ask($"{question} {(defaultYes ? "[Y/n]" : "[y/N]")} ");
            if (line is null) return false;          // EOF — 물어볼 사람이 없다

            var answer = line.Trim().ToLowerInvariant().Replace(" ", "");
            if (answer.Length == 0) return defaultYes;

            // 말로 답하는 경우도 받는다 — "응 해줘" · "아니 됐어" · "그만".
            if (answer is "y" or "yes" or "ㅇ" or "예" or "네" or "응" or "어" or "그래" or "좋아"
                or "해줘" or "해" or "맞아" or "맞습니다" or "진행") return true;
            if (answer is "n" or "no" or "ㄴ" or "아니오" or "아니요" or "아니" or "안돼" or "싫어"
                or "그만" or "됐어" or "취소" or "나중에" or "틀려" or "아닙니다") return false;

            if (answer.StartsWith("아니", StringComparison.Ordinal)) return false;
            if (answer.StartsWith("네", StringComparison.Ordinal)
                || answer.StartsWith("예", StringComparison.Ordinal)) return true;
        }
    }
}
