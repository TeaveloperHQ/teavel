using Teavel.M365;
using Teavel.Roster;

namespace Teavel.Cli;

/// <summary>
/// <b>관리자가 정한 것</b>을 담는 그릇들.
///
/// <para>
/// 콘솔이든 관리 화면이든 정하는 방식은 다르지만 <b>정해진 것의 모양은 같아야 한다.</b>
/// 그래야 실행하는 코드가 하나로 남는다 — 화면이 늘 때마다 지우고 넣는 코드를 다시 쓰면
/// 안전장치도 그만큼 늘어나고, 늘어난 만큼 하나씩 빠진다.
/// </para>
/// <para>
/// 그래서 화면은 여기까지만 만든다. 실제로 부르는 것은 <see cref="M365Flow"/> 와
/// 관리 화면의 API 가 같은 함수로 한다.
/// </para>
/// </summary>
public enum TidyAction
{
    /// <summary>손대지 않는다. 잘 모르겠으면 이것이다.</summary>
    Keep,

    /// <summary>이름만 바꾼다. 안의 파일·대화는 그대로 남는다.</summary>
    Rename,

    /// <summary>이름 앞에 연도를 붙이고 학생만 내보낸다. 팀과 자료는 그대로 둔다.</summary>
    Archive,

    /// <summary>지운다. 되돌릴 수 없다.</summary>
    Delete,
}

/// <summary>정리 후보 하나를 어떻게 할지 정한 것.</summary>
public sealed record TidyDecision(ExistingGroup Group, TidyAction Action, string NewName);

/// <summary>한 반에 실제로 넣기로 한 사람들.</summary>
public sealed record MemberPick(string ClassKey, ExistingGroup Team, IReadOnlyList<RosterRow> People);

/// <summary>어느 반의 담임을 누구로 할지 정한 것.</summary>
public sealed record OwnerPick(string ClassKey, string GroupId, TenantUser Teacher);
