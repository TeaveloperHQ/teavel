using System.Text.RegularExpressions;

namespace Teavel.Roster;

/// <summary>
/// 학번이 학년·반·번호를 어떻게 이어 붙인 것인지.
/// </summary>
/// <remarks>
/// <b>학교마다 다르다.</b> 1학년 3반 1번이 <c>10301</c> 인 학교도 있고 <c>1301</c> 인 학교도 있다.
/// 반을 두 자리로 쓰느냐 한 자리로 쓰느냐의 차이인데, 여기를 박아 두면 절반의 학교에서 틀린다.
/// </remarks>
/// <param name="GradeDigits">학년이 몇 자리인지.</param>
/// <param name="ClassDigits">
/// 반이 몇 자리인지. <b>0 이면 자리를 채우지 않는다</b> — 3반은 '3', 10반은 '10'.
/// 실제 학교에서 이 꼴을 봤다: 1학년 3반 1번은 1301 인데 2학년 10반 5번은 21005 다.
/// 앞뒤 자릿수가 정해져 있으면 가운데는 남는 만큼으로 갈라낼 수 있어 문제가 없다.
/// </param>
/// <param name="NumberDigits">번호가 몇 자리인지.</param>
public sealed record StudentIdFormat(int GradeDigits, int ClassDigits, int NumberDigits)
{
    public int Length => GradeDigits + ClassDigits + NumberDigits;

    /// <summary>학년·반·번호를 학번으로.</summary>
    public string? Compose(string grade, string classNo, string number)
    {
        if (!int.TryParse(grade.Trim(), out var g)) return null;
        if (!int.TryParse(classNo.Trim(), out var c)) return null;
        if (!int.TryParse(number.Trim(), out var n)) return null;

        // 자릿수를 넘치면 이 형식이 아니다. 억지로 잘라 붙이면 엉뚱한 학번이 된다.
        if (g >= Pow10(GradeDigits) || n >= Pow10(NumberDigits)) return null;
        if (ClassDigits > 0 && c >= Pow10(ClassDigits)) return null;

        return g.ToString().PadLeft(GradeDigits, '0')
             + (ClassDigits > 0 ? c.ToString().PadLeft(ClassDigits, '0') : c.ToString())
             + n.ToString().PadLeft(NumberDigits, '0');
    }

    /// <summary>학번을 학년·반·번호로.</summary>
    public bool TryDecompose(string studentId, out string grade, out string classNo, out string number)
    {
        grade = classNo = number = "";

        var s = studentId.Trim();
        if (!Regex.IsMatch(s, @"^\d+$")) return false;

        // 반이 자리를 안 채우는 형식이면 길이가 학교·반마다 달라진다.
        // 앞(학년)과 뒤(번호)를 떼어 내고 남는 것이 반이다.
        if (ClassDigits == 0)
        {
            if (s.Length <= GradeDigits + NumberDigits) return false;
        }
        else if (s.Length != Length) return false;

        var mid = s.Length - GradeDigits - NumberDigits;

        grade   = int.Parse(s[..GradeDigits]).ToString();
        classNo = int.Parse(s.Substring(GradeDigits, mid)).ToString();
        number  = int.Parse(s.Substring(GradeDigits + mid, NumberDigits)).ToString();

        // 학년 0, 반 0 은 있을 수 없다. 그런 값이 나오면 이 형식이 아니다.
        return grade != "0" && classNo != "0" && number != "0";
    }

    /// <summary>사람에게 보여 줄 보기. "1학년 3반 1번 → 10301" 처럼.</summary>
    public string Example(int grade = 1, int classNo = 3, int number = 1)
        => $"{grade}학년 {classNo}반 {number}번 → {Compose(grade.ToString(), classNo.ToString(), number.ToString())}";

    public override string ToString()
        => ClassDigits > 0
            ? $"학년 {GradeDigits}자리 · 반 {ClassDigits}자리 · 번호 {NumberDigits}자리"
            : $"학년 {GradeDigits}자리 · 반은 그대로 · 번호 {NumberDigits}자리";

    private static int Pow10(int n) => (int)Math.Pow(10, n);
}

/// <summary>학번 형식을 알아낸 결과.</summary>
/// <param name="Format">알아낸 형식. 못 알아냈으면 null.</param>
/// <param name="Certain">
/// 자료로 확인한 것인지. <b>false 면 짐작이므로 사람에게 물어야 한다.</b>
/// </param>
/// <param name="Why">어떻게 알아냈는지 — 화면에 그대로 나간다.</param>
/// <param name="Alternatives">같은 자릿수에 들어맞는 다른 형식들. 물어볼 때 함께 보여 준다.</param>
public sealed record StudentIdGuess(
    StudentIdFormat? Format,
    bool Certain,
    string Why,
    IReadOnlyList<StudentIdFormat> Alternatives);

/// <summary>
/// 자료를 보고 학번 형식을 알아낸다. <b>박아 두지 않는다.</b>
///
/// <para>
/// 파일에 학번과 학년·반·번호가 함께 있으면 <b>맞춰 보면 안다</b> —
/// 어느 형식으로 이어 붙였을 때 실제 학번과 같아지는지 세어 보면 된다.
/// 이때는 짐작이 아니라 확인이다.
/// </para>
/// <para>
/// 학번만 있으면 자릿수로 후보를 좁힐 수는 있어도 하나로 못 정한다.
/// 네 자리 <c>1301</c> 은 1학년 3반 1번일 수도 있고 1학년 30반 1번일 수도 있다.
/// 그때는 <b>반드시 사람에게 묻는다.</b> 잘못 갈라 놓으면 아이가 없는 반에 배정된다.
/// </para>
/// </summary>
public static class StudentIdFormats
{
    /// <summary>흔히 쓰는 학번 형식. 학년은 한 자리로 본다(6학년까지라 두 자리가 될 일이 없다).</summary>
    public static readonly IReadOnlyList<StudentIdFormat> Known = new[]
    {
        new StudentIdFormat(1, 2, 2),   // 10301   — 반을 두 자리로 채운다. 가장 흔하다
        new StudentIdFormat(1, 0, 2),   // 1301 / 21005 — 반을 그대로 붙인다
        new StudentIdFormat(1, 1, 2),   // 1301    — 반이 늘 한 자리인 작은 학교
        new StudentIdFormat(1, 2, 3),   // 103001
        new StudentIdFormat(1, 0, 3),   // 13001 / 210005
        new StudentIdFormat(1, 3, 2),   // 100301
        new StudentIdFormat(1, 2, 1),   // 1031
        new StudentIdFormat(1, 0, 1),   // 131 / 2105
    };

    /// <summary>
    /// 자료에서 형식을 알아낸다.
    /// </summary>
    /// <param name="samples">(학번, 학년, 반, 번호). 비어 있는 값이 섞여 있어도 된다.</param>
    public static StudentIdGuess Detect(IEnumerable<(string Sid, string Grade, string ClassNo, string Number)> samples)
    {
        var all = samples.ToList();

        // ① 넷이 다 있는 줄이 있으면 맞춰 본다. 이게 가장 확실하다.
        var full = all.Where(s => s.Sid.Length > 0 && s.Grade.Length > 0
                               && s.ClassNo.Length > 0 && s.Number.Length > 0).ToList();

        if (full.Count > 0)
        {
            var fits = Known
                .Where(f => full.All(s => string.Equals(f.Compose(s.Grade, s.ClassNo, s.Number), s.Sid.Trim(),
                                                        StringComparison.Ordinal)))
                .ToList();

            if (fits.Count == 1)
                return new StudentIdGuess(fits[0], true,
                    $"파일 안의 학번 {full.Count}개와 학년·반·번호가 모두 이 형식으로 맞습니다.",
                    Array.Empty<StudentIdFormat>());

            if (fits.Count > 1)
                return new StudentIdGuess(fits[0], false,
                    $"학번 {full.Count}개가 형식 {fits.Count}가지에 모두 들어맞아 하나로 정하지 못했습니다.",
                    fits.Skip(1).ToList());

            return new StudentIdGuess(null, false,
                "학번이 학년·반·번호를 이어 붙인 모양이 아닙니다. 학교마다 다른 규칙을 쓰기도 합니다.",
                Array.Empty<StudentIdFormat>());
        }

        // ② 학번만 있다. 자릿수로 후보를 좁히는 것까지만 할 수 있다.
        var sids = all.Select(s => s.Sid.Trim())
                      .Where(s => s.Length > 0 && Regex.IsMatch(s, @"^\d+$"))
                      .ToList();
        if (sids.Count == 0)
            return new StudentIdGuess(null, false, "학번이 없습니다.", Array.Empty<StudentIdFormat>());

        // 갈라 봤을 때 학년·반·번호가 말이 되는 것만 남긴다.
        var plausible = Known
            .Where(f => sids.All(s => f.TryDecompose(s, out var g, out var c, out var n)
                                   && int.Parse(g) <= 6 && int.Parse(c) <= 30 && int.Parse(n) <= 60))
            .ToList();

        var lengths = string.Join(", ", sids.Select(s => s.Length).Distinct().OrderBy(x => x));

        if (plausible.Count == 0)
            return new StudentIdGuess(null, false,
                $"{lengths}자리 학번을 학년·반·번호로 가를 방법을 찾지 못했습니다.",
                Array.Empty<StudentIdFormat>());

        if (plausible.Count == 1)
            return new StudentIdGuess(plausible[0], false,
                "학번을 가를 수 있는 형식이 하나뿐입니다. 맞는지만 확인해 주세요.",
                Array.Empty<StudentIdFormat>());

        return new StudentIdGuess(plausible[0], false,
            $"학번을 가를 수 있는 형식이 {plausible.Count}가지입니다. 어느 것인지 골라 주세요.",
            plausible.Skip(1).ToList());
    }
}
