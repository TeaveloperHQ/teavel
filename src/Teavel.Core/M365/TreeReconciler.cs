namespace Teavel.M365;

/// <summary>테넌트에 지금 있는 그룹 하나.</summary>
/// <param name="DisplayName">
/// 화면에 보이는 이름. <b>대조는 이것을 기준으로 한다</b> —
/// 별칭은 한글 이름에서 뜻이 날아가거나 Teams 가 자동 생성해 믿을 수 없다.
/// </param>
/// <param name="MailNickname">별칭(Exchange 의 Alias).</param>
/// <param name="IsTeam">Teams 팀이 붙어 있는지.</param>
/// <param name="MemberCount">구성원 수. 모르면 -1.</param>
/// <param name="Created">만든 날(yyyy-MM-dd). 모르면 빈 문자열.</param>
/// <param name="Origin">어디서 왔는지 짐작할 단서. 비어 있을 수 있다 — 판단에 쓰지 않는다.</param>
public sealed record ExistingGroup(
    string DisplayName,
    string MailNickname,
    bool IsTeam,
    int MemberCount = -1,
    string Created = "",
    string Origin = "");

/// <summary>대조 결과 항목 하나가 어떻게 될지.</summary>
public enum PlanAction
{
    /// <summary>없으므로 만든다.</summary>
    Create,

    /// <summary>이미 있으므로 건드리지 않는다.</summary>
    Skip,

    /// <summary>이름은 있는데 모양이 다르다 — 사람이 봐야 한다.</summary>
    Conflict,
}

/// <summary>대조 결과 한 줄.</summary>
/// <param name="Declared">선언된 것.</param>
/// <param name="Action">어떻게 할지.</param>
/// <param name="Reason">왜 그렇게 정했는지 — 교사에게 그대로 보여 준다.</param>
/// <param name="Existing">이미 있는 것(있을 때만).</param>
public sealed record PlanItem(
    DeclaredGroup Declared,
    PlanAction Action,
    string Reason,
    ExistingGroup? Existing = null);

/// <summary>
/// 선언(트리)과 테넌트의 지금 상태를 대조해 <b>무엇을 만들지</b> 정한다.
///
/// 요점은 <b>재고부터 본다</b> 는 것이다. 무엇이 그것을 만들었는지(SDS·손·다른 도구)는
/// 따지지 않는다 — 이미 있으면 안 만들면 되고, 원인은 관리자가 미리보기를 보면 안다.
/// 만든 주체를 알아내려 들면 확장 특성을 뒤져야 하고, 그건 추가 권한이 필요한 데다
/// 마이크로소프트가 규격을 바꾸면 조용히 틀린다.
///
/// 여기서 아무것도 실행하지 않는다. 계획만 만들고, 보여 주고 승인받는 일은 CLI 가 한다.
/// 그래서 테넌트 없이도 이 판단을 전부 시험할 수 있다.
/// </summary>
public static class TreeReconciler
{
    /// <summary>선언과 재고를 맞춰 계획을 만든다.</summary>
    public static IReadOnlyList<PlanItem> Plan(
        IReadOnlyList<DeclaredGroup> declared,
        IReadOnlyList<ExistingGroup> existing)
    {
        // 이름과 별칭 둘 다로 찾는다 — 한쪽만 겹쳐도 만들다 실패하기 때문이다.
        var byName = new Dictionary<string, ExistingGroup>(StringComparer.OrdinalIgnoreCase);
        var byNick = new Dictionary<string, ExistingGroup>(StringComparer.OrdinalIgnoreCase);

        // 빈칸·밑줄만 다른 것을 잡기 위한 것. 첫 번째 것만 남긴다 —
        // 여기 걸리면 어차피 사람이 봐야 하고, 하나만 보여 줘도 무슨 일인지 알 수 있다.
        var byLoose = new Dictionary<string, ExistingGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in existing)
        {
            byName.TryAdd(e.DisplayName.Trim(), e);
            if (e.MailNickname.Length > 0) byNick.TryAdd(e.MailNickname.Trim(), e);

            var loose = Loosen(e.DisplayName);
            if (loose.Length > 0) byLoose.TryAdd(loose, e);
        }

        var plan = new List<PlanItem>();

        foreach (var d in declared)
        {
            var hitName = byName.GetValueOrDefault(d.DisplayName.Trim());
            var hitNick = d.MailNickname.Length > 0 ? byNick.GetValueOrDefault(d.MailNickname.Trim()) : null;

            // ① 아무것도 안 걸린다 — 만들기 전에 '거의 같은 이름' 이 있는지 한 번 더 본다.
            if (hitName is null && hitNick is null)
            {
                // 실제 학교에서 이렇게 갈렸다: 테넌트에는 '3학년_4반'(30명) 이 있는데
                // 선언은 '3학년 4반' 이었다. 밑줄과 빈칸 하나 차이라 글자로는 다른 이름이지만,
                // 그대로 만들면 학교에 거의 같은 것이 둘 생기고 아이들이 어디로 들어갈지 모르게 된다.
                // 지우거나 이름을 맞추는 일은 사람이 정해야 하므로 여기서 세운다.
                var loose = byLoose.GetValueOrDefault(Loosen(d.DisplayName));
                if (loose is not null)
                {
                    plan.Add(new PlanItem(d, PlanAction.Conflict,
                        $"'{loose.DisplayName}' 과(와) 빈칸·기호만 다릅니다. "
                      + "그대로 만들면 거의 같은 것이 둘 생깁니다.",
                        loose));
                    continue;
                }

                plan.Add(new PlanItem(d, PlanAction.Create, "없으므로 만듭니다."));
                continue;
            }

            // ② 이름과 별칭이 서로 다른 그룹을 가리킨다 — 사람이 봐야 한다.
            if (hitName is not null && hitNick is not null && !ReferenceEquals(hitName, hitNick))
            {
                plan.Add(new PlanItem(d, PlanAction.Conflict,
                    $"이름은 '{hitName.DisplayName}' 에, 별칭은 '{hitNick.DisplayName}' 에 이미 쓰이고 있습니다.",
                    hitName));
                continue;
            }

            var hit = hitName ?? hitNick!;

            // ③ 별칭만 겹친다 — 다른 그룹이 그 메일 주소를 이미 쓰고 있다.
            if (hitName is null)
            {
                plan.Add(new PlanItem(d, PlanAction.Conflict,
                    $"별칭 '{d.MailNickname}' 을(를) '{hit.DisplayName}' 이(가) 이미 쓰고 있습니다.",
                    hit));
                continue;
            }

            // ④ 팀을 원하는데 그룹만 있다 — 그룹은 그대로 두고 팀만 붙이면 된다.
            if (d.Kind == GroupKind.Team && !hit.IsTeam)
            {
                plan.Add(new PlanItem(d, PlanAction.Conflict,
                    "같은 이름의 그룹은 있는데 팀이 붙어 있지 않습니다. "
                  + "새로 만들면 이름이 겹치므로, 기존 그룹에 팀을 붙이는 편이 낫습니다.",
                    hit));
                continue;
            }

            // ⑤ 이미 있다 — 건드리지 않는다.
            var what = hit.IsTeam ? "팀" : "그룹";
            var origin = hit.Origin.Length > 0 ? $" (표시: {hit.Origin})" : "";
            plan.Add(new PlanItem(d, PlanAction.Skip, $"이미 있는 {what}입니다{origin}.", hit));
        }

        return plan;
    }

    /// <summary>
    /// 이름에서 사람 눈에 잘 안 띄는 차이를 걷어낸다 — 빈칸 · 밑줄 · 붙임표 · 가운뎃점.
    /// </summary>
    /// <remarks>
    /// 학교 이름은 손으로 붙여 온 것이라 표기가 제각각이다.
    /// '3학년_4반' · '3학년 4반' · '3학년4반' 은 사람에게는 같은 반이지만 글자로는 다 다르다.
    /// 여기서 걷어내는 것은 <b>구분자뿐</b>이다. 글자 자체를 건드리면(예: 숫자 무시)
    /// 1반과 11반이 같아지는 식으로 엉뚱한 것이 겹친다.
    /// </remarks>
    public static string Loosen(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.Trim())
        {
            if (char.IsWhiteSpace(c)) continue;
            if (c is '_' or '-' or '·' or '.' or '‧' or '・') continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>계획을 한 줄로 요약한다.</summary>
    public static string Summarize(IReadOnlyList<PlanItem> plan)
    {
        var create = plan.Count(p => p.Action == PlanAction.Create);
        var skip = plan.Count(p => p.Action == PlanAction.Skip);
        var conflict = plan.Count(p => p.Action == PlanAction.Conflict);

        var parts = new List<string>();
        if (create > 0) parts.Add($"만들 것 {create}개");
        if (skip > 0) parts.Add($"이미 있어 건너뜀 {skip}개");
        if (conflict > 0) parts.Add($"확인 필요 {conflict}개");

        return parts.Count == 0 ? "선언된 것이 없습니다." : string.Join(" · ", parts);
    }
}
