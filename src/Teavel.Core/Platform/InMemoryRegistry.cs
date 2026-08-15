namespace Teavel.Platform;

/// <summary>
/// 메모리 레지스트리 — 비Windows 개발·테스트용.
/// 실제 교사 PC 상태를 흉내 내어 진단 로직만 검증한다(리눅스에서 빌드·실행 가능).
/// </summary>
public sealed class InMemoryRegistry : IRegistry
{
    // "root\keyPath" → (valueName → value)
    private readonly Dictionary<string, Dictionary<string, object>> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>HKLM 쓰기를 막아 '관리자 권한 없음' 상황을 재현한다.</summary>
    public bool DenyLocalMachineWrites { get; set; } = true;

    private static string Key(RegistryRoot root, string keyPath) => $"{root}\\{keyPath.Trim('\\')}";

    /// <summary>테스트 픽스처 구성용 — 값을 심는다(키도 함께 생김).</summary>
    public InMemoryRegistry Seed(RegistryRoot root, string keyPath, string valueName, object value)
    {
        var k = Key(root, keyPath);
        if (!_values.TryGetValue(k, out var bag))
            _values[k] = bag = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        bag[valueName] = value;
        return this;
    }

    /// <summary>값 없는 빈 키를 만든다(존재 여부만 보는 진단용).</summary>
    public InMemoryRegistry SeedKey(RegistryRoot root, string keyPath)
    {
        var k = Key(root, keyPath);
        if (!_values.ContainsKey(k))
            _values[k] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        return this;
    }

    // REG_EXPAND_SZ 로 심어 둔 값들("root\key\name" 집합).
    private readonly HashSet<string> _expandable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>REG_EXPAND_SZ 값을 심는다(PATH 처럼 %VAR% 가 들어간 값 재현용).</summary>
    public InMemoryRegistry SeedExpandable(RegistryRoot root, string keyPath, string valueName, string value)
    {
        Seed(root, keyPath, valueName, value);
        _expandable.Add($"{Key(root, keyPath)}\\{valueName}");
        return this;
    }

    public RegistryStringValue? ReadStringValue(RegistryRoot root, string keyPath, string valueName)
    {
        if (!_values.TryGetValue(Key(root, keyPath), out var bag)) return null;
        if (!bag.TryGetValue(valueName, out var v) || v is not string s) return null;
        return new RegistryStringValue(s, _expandable.Contains($"{Key(root, keyPath)}\\{valueName}"));
    }

    public bool WriteStringValue(RegistryRoot root, string keyPath, string valueName, string value, bool expandable)
    {
        if (root == RegistryRoot.LocalMachine && DenyLocalMachineWrites) return false;
        Seed(root, keyPath, valueName, value);
        var id = $"{Key(root, keyPath)}\\{valueName}";
        if (expandable) _expandable.Add(id); else _expandable.Remove(id);
        return true;
    }

    public string? ReadString(RegistryRoot root, string keyPath, string valueName)
        => _values.TryGetValue(Key(root, keyPath), out var bag) && bag.TryGetValue(valueName, out var v)
            ? v as string
            : null;

    public int? ReadDword(RegistryRoot root, string keyPath, string valueName)
        => _values.TryGetValue(Key(root, keyPath), out var bag) && bag.TryGetValue(valueName, out var v) && v is int i
            ? i
            : null;

    public bool KeyExists(RegistryRoot root, string keyPath)
    {
        var prefix = Key(root, keyPath);
        // 정확히 그 키가 있거나, 그 아래 하위 키가 있으면 존재로 본다(실제 레지스트리와 동일).
        return _values.ContainsKey(prefix)
            || _values.Keys.Any(k => k.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> SubKeyNames(RegistryRoot root, string keyPath)
    {
        var prefix = Key(root, keyPath) + "\\";
        return _values.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool WriteString(RegistryRoot root, string keyPath, string valueName, string value)
    {
        if (root == RegistryRoot.LocalMachine && DenyLocalMachineWrites) return false;
        Seed(root, keyPath, valueName, value);
        return true;
    }

    public bool WriteDword(RegistryRoot root, string keyPath, string valueName, int value)
    {
        if (root == RegistryRoot.LocalMachine && DenyLocalMachineWrites) return false;
        Seed(root, keyPath, valueName, value);
        return true;
    }

    public bool DeleteKey(RegistryRoot root, string keyPath)
    {
        if (root == RegistryRoot.LocalMachine && DenyLocalMachineWrites) return false;

        var prefix = Key(root, keyPath);
        var doomed = _values.Keys
            .Where(k => k.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var k in doomed)
        {
            _values.Remove(k);
            _expandable.RemoveWhere(e => e.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase));
        }
        return doomed.Count > 0;
    }
}
