namespace Teavel.Platform;

/// <summary>레지스트리 루트 — 우리가 건드리는 두 곳만.</summary>
public enum RegistryRoot
{
    /// <summary>HKEY_CURRENT_USER — 관리자 권한 불필요. 교사 PC에서 기본으로 쓰는 곳.</summary>
    CurrentUser,

    /// <summary>HKEY_LOCAL_MACHINE — 읽기는 자유, 쓰기는 관리자 권한 필요.</summary>
    LocalMachine,
}

/// <summary>문자열 값 하나 — 값과, 그것이 REG_EXPAND_SZ 인지.</summary>
/// <param name="Value">원본 문자열(%VAR% 가 펼쳐지지 않은 그대로).</param>
/// <param name="Expandable">REG_EXPAND_SZ 이면 true, REG_SZ 이면 false.</param>
public sealed record RegistryStringValue(string Value, bool Expandable);

/// <summary>
/// 레지스트리 접근을 좁게 감싼 인터페이스.
/// 실제 구현은 <see cref="WindowsRegistry"/>, 비Windows 개발·테스트에서는
/// <see cref="InMemoryRegistry"/> 를 끼워 로직만 검증한다.
/// </summary>
public interface IRegistry
{
    /// <summary>
    /// 값을 <b>펼치지 않고</b> 원본 그대로 읽는다. 종류(REG_SZ / REG_EXPAND_SZ)도 함께 준다.
    /// </summary>
    /// <remarks>
    /// 사용자 PATH 처럼 %VAR% 가 들어 있는 값은 반드시 이걸로 읽어야 한다.
    /// 보통의 읽기는 %USERPROFILE% 을 실제 경로로 펼쳐 주는데, 그 상태로 다시 쓰면
    /// 사용자의 PATH 에 박혀 있던 변수가 통째로 사라진다 — 되돌리기 어려운 손상이다.
    /// </remarks>
    RegistryStringValue? ReadStringValue(RegistryRoot root, string keyPath, string valueName);

    /// <summary>값을 종류까지 지정해 쓴다. 읽을 때의 종류를 그대로 돌려주어야 한다.</summary>
    bool WriteStringValue(RegistryRoot root, string keyPath, string valueName, string value, bool expandable);

    /// <summary>문자열 값을 읽는다. 키·값이 없으면 null.</summary>
    string? ReadString(RegistryRoot root, string keyPath, string valueName);

    /// <summary>DWORD 값을 읽는다. 키·값이 없으면 null.</summary>
    int? ReadDword(RegistryRoot root, string keyPath, string valueName);

    /// <summary>키가 존재하는지.</summary>
    bool KeyExists(RegistryRoot root, string keyPath);

    /// <summary>키 바로 아래 하위 키 이름들. 키가 없으면 빈 배열.</summary>
    IReadOnlyList<string> SubKeyNames(RegistryRoot root, string keyPath);

    /// <summary>문자열 값을 쓴다(키가 없으면 생성). 권한이 없으면 false.</summary>
    bool WriteString(RegistryRoot root, string keyPath, string valueName, string value);

    /// <summary>DWORD 값을 쓴다(키가 없으면 생성). 권한이 없으면 false.</summary>
    bool WriteDword(RegistryRoot root, string keyPath, string valueName, int value);

    /// <summary>키를 하위 키까지 통째로 지운다. 원래 없었으면 false.</summary>
    bool DeleteKey(RegistryRoot root, string keyPath);
}
