namespace Teavel.M365;

/// <summary>테넌트에 있는 사람 하나.</summary>
/// <param name="Upn">로그인 아이디(메일 주소 꼴). 구성원을 넣을 때 이 값을 쓴다.</param>
/// <param name="DisplayName">화면에 보이는 이름.</param>
/// <param name="Department">부서. 교육청이 채워 뒀을 수도 있고 비어 있을 수도 있다.</param>
/// <param name="AccountType">Get-CsOnlineUser 가 주는 계정 종류. IneligibleUser 면 라이선스가 없다.</param>
/// <param name="LicenseBundle">
/// 켜져 있는 서비스 플랜을 정렬해 이어 붙인 것. 같은 라이선스를 받은 사람은 같은 값이 된다.
/// SKU 이름이 아니다 — 이름을 알아내려 들지 않는 것이 이 방식의 요점이다.
/// </param>
public sealed record TenantUser(
    string Upn,
    string DisplayName,
    string Department,
    string AccountType,
    string LicenseBundle);

/// <summary>라이선스 꾸러미가 같은 사람들의 묶음.</summary>
/// <param name="Bundle">그 꾸러미 값. 화면에 보여 주지는 않는다 — 사람이 읽을 것이 못 된다.</param>
/// <param name="People">그 묶음에 든 사람들.</param>
/// <param name="Departments">그 묶음에서 많이 보이는 부서 이름들. 비어 있을 수 있다.</param>
public sealed record LicenseCluster(
    string Bundle,
    IReadOnlyList<TenantUser> People,
    IReadOnlyList<string> Departments)
{
    public int Count => People.Count;

    /// <summary>라이선스가 아예 없는 묶음인지. 팀에 넣어도 쓰지 못한다.</summary>
    public bool Unlicensed => Bundle.Length == 0;

    /// <summary>관리자가 보고 알아볼 수 있게 이름 몇 개를 보여 준다.</summary>
    public string Sample(int count = 4)
        => string.Join(", ", People.Take(count).Select(p => p.DisplayName.Length > 0 ? p.DisplayName : p.Upn));
}

/// <summary>
/// 테넌트의 사람들을 <b>라이선스 꾸러미가 같은 것끼리</b> 묶는다.
///
/// <para>
/// 교사와 학생은 라이선스가 다르다. 문제는 그 라이선스를 <b>읽는</b> 방법이었다.
/// <c>Get-MsolUser</c> 는 SKU 이름(<c>STANDARDWOFFPACK_FACULTY</c>)을 그대로 줬지만
/// MSOnline 은 2025년에 서버 쪽이 닫혔고, Graph 는 관리자 동의 화면을 부른다.
/// 남은 <c>Get-CsOnlineUser</c> 는 SKU 대신 서비스 플랜 목록만 준다.
/// </para>
/// <para>
/// 그래서 <b>SKU 이름을 알아내는 것을 그만뒀다.</b> 같은 라이선스를 받은 사람끼리 묶기만 하면
/// 학교에서는 큰 묶음 둘이 나온다 — 학생 수백 명과 교사 수십 명. 어느 쪽이 교사인지는
/// 이름 몇 개만 보면 관리자가 안다. 아무것도 모르는 관리자도 그건 답할 수 있다.
/// </para>
/// <para>
/// 이 방식은 학교가 무슨 라이선스를 쓰든, 마이크로소프트가 SKU 이름을 바꾸든 그대로 동작한다.
/// 대신 <b>추측하지 않는다</b> — 어느 쪽이 교사인지는 반드시 사람에게 묻는다.
/// 여기서 잘못 짚으면 학생이 팀 소유자가 되어 반 전체를 지울 수 있다.
/// </para>
/// </summary>
public static class UserDirectory
{
    /// <summary>이보다 사람이 적은 묶음은 뒤로 보낸다 — 대개 관리자 계정이나 시험 계정이다.</summary>
    public const int SmallCluster = 3;

    /// <summary>PowerShell 이 낸 사람 줄들을 읽는다.</summary>
    /// <remarks>
    /// 한 줄이 <c>USER\tUPN\t이름\t부서\t계정종류\t라이선스꾸러미</c> 꼴이다.
    /// 모양이 다른 줄은 버리지 않고 넘어간다 — 한 줄 때문에 명단 전체를 못 보면 아무것도 못 한다.
    /// </remarks>
    public static List<TenantUser> Parse(IEnumerable<string> lines)
    {
        var people = new List<TenantUser>();

        foreach (var line in lines)
        {
            var f = line.Split('\t');
            if (f.Length < 2 || !string.Equals(f[0], "USER", StringComparison.Ordinal)) continue;
            if (f[1].Length == 0) continue;

            people.Add(new TenantUser(
                Upn: f[1].Trim(),
                DisplayName: f.Length > 2 ? f[2].Trim() : "",
                Department: f.Length > 3 ? f[3].Trim() : "",
                AccountType: f.Length > 4 ? f[4].Trim() : "",
                LicenseBundle: f.Length > 5 ? f[5].Trim() : ""));
        }

        return people;
    }

    /// <summary>
    /// 라이선스 꾸러미별로 묶는다. 사람이 많은 묶음이 앞이다.
    /// </summary>
    /// <remarks>
    /// 손님(Guest)과 자원 계정은 뺀다. 사람이 아니거나 학교 밖 사람이라
    /// 교사·학생을 가르는 데 끼면 묶음만 지저분해진다.
    /// </remarks>
    public static IReadOnlyList<LicenseCluster> Cluster(IReadOnlyList<TenantUser> people)
    {
        var real = people.Where(p => !IsOutsider(p)).ToList();

        return real
            .GroupBy(p => p.LicenseBundle, StringComparer.Ordinal)
            .Select(g =>
            {
                var members = g.OrderBy(p => p.DisplayName, StringComparer.CurrentCulture).ToList();

                // 부서가 채워져 있으면 관리자가 알아보는 데 큰 도움이 된다.
                var depts = members
                    .Where(p => p.Department.Length > 0)
                    .GroupBy(p => p.Department, StringComparer.CurrentCultureIgnoreCase)
                    .OrderByDescending(d => d.Count())
                    .Take(3)
                    .Select(d => $"{d.Key}({d.Count()})")
                    .ToList();

                return new LicenseCluster(g.Key, members, depts);
            })
            // 라이선스 없는 묶음은 늘 뒤로 — 골라야 할 것이 아니다.
            .OrderBy(c => c.Unlicensed)
            .ThenByDescending(c => c.Count)
            .ToList();
    }

    /// <summary>교사·학생을 가르는 데 끼면 안 되는 계정인지.</summary>
    public static bool IsOutsider(TenantUser p)
        => p.AccountType.Equals("Guest", StringComparison.OrdinalIgnoreCase)
        || p.AccountType.Equals("ResourceAccount", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 어느 묶음이 교사인지 <b>짐작</b>한다. 그대로 쓰지 말고 관리자에게 확인받는 데 쓴다.
    /// </summary>
    /// <remarks>
    /// 학교는 학생이 교사보다 훨씬 많다. 그래서 사람이 많은 쪽이 학생, 그다음이 교사일
    /// 가능성이 높다 — 그저 물어볼 때 어느 쪽에 기본값을 둘지 정하는 용도다.
    /// 이 짐작을 근거로 실제 배정을 하지는 않는다. 틀리면 학생이 팀 소유자가 되기 때문이다.
    /// </remarks>
    public static LicenseCluster? GuessFaculty(IReadOnlyList<LicenseCluster> clusters)
    {
        var big = clusters.Where(c => !c.Unlicensed && c.Count >= SmallCluster).ToList();
        return big.Count >= 2 ? big[1] : null;
    }

    /// <summary>묶음 상황을 한 줄로.</summary>
    public static string Summarize(IReadOnlyList<LicenseCluster> clusters, IReadOnlyList<TenantUser> all)
    {
        var counted = clusters.Sum(c => c.Count);
        var outsiders = all.Count(IsOutsider);
        var noLicense = clusters.Where(c => c.Unlicensed).Sum(c => c.Count);

        var s = $"사람 {counted}명 · 라이선스 묶음 {clusters.Count(c => !c.Unlicensed)}가지";
        if (noLicense > 0) s += $" · 라이선스 없음 {noLicense}명";
        if (outsiders > 0) s += $" · 손님·자원계정 {outsiders}명(뺐습니다)";
        return s;
    }
}
