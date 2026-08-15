using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Teavel.Platform;

/// <summary>실제 Windows 레지스트리. 비Windows 에서 생성하면 <see cref="PlatformNotSupportedException"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistry : IRegistry
{
    public WindowsRegistry()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WindowsRegistry 는 Windows 에서만 쓸 수 있습니다.");
    }

    private static RegistryKey Base(RegistryRoot root) => root switch
    {
        RegistryRoot.CurrentUser => Registry.CurrentUser,
        RegistryRoot.LocalMachine => Registry.LocalMachine,
        _ => throw new ArgumentOutOfRangeException(nameof(root)),
    };

    public RegistryStringValue? ReadStringValue(RegistryRoot root, string keyPath, string valueName)
    {
        try
        {
            using var k = Base(root).OpenSubKey(keyPath);
            if (k is null) return null;

            // DoNotExpandEnvironmentNames: %USERPROFILE% 같은 것을 펼치지 않고 원본 그대로 받는다.
            if (k.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string s)
                return null;

            var expandable = k.GetValueKind(valueName) == RegistryValueKind.ExpandString;
            return new RegistryStringValue(s, expandable);
        }
        catch { return null; }
    }

    public bool WriteStringValue(RegistryRoot root, string keyPath, string valueName, string value, bool expandable)
    {
        try
        {
            using var k = Base(root).CreateSubKey(keyPath, writable: true);
            if (k is null) return false;
            k.SetValue(valueName, value, expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
            return true;
        }
        catch { return false; }
    }

    public string? ReadString(RegistryRoot root, string keyPath, string valueName)
    {
        try
        {
            using var k = Base(root).OpenSubKey(keyPath);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    public int? ReadDword(RegistryRoot root, string keyPath, string valueName)
    {
        try
        {
            using var k = Base(root).OpenSubKey(keyPath);
            return k?.GetValue(valueName) is int i ? i : null;
        }
        catch { return null; }
    }

    public bool KeyExists(RegistryRoot root, string keyPath)
    {
        try
        {
            using var k = Base(root).OpenSubKey(keyPath);
            return k != null;
        }
        catch { return false; }
    }

    public IReadOnlyList<string> SubKeyNames(RegistryRoot root, string keyPath)
    {
        try
        {
            using var k = Base(root).OpenSubKey(keyPath);
            return k?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    public bool WriteString(RegistryRoot root, string keyPath, string valueName, string value)
    {
        try
        {
            using var k = Base(root).CreateSubKey(keyPath, writable: true);
            if (k == null) return false;
            k.SetValue(valueName, value, RegistryValueKind.String);
            return true;
        }
        catch { return false; }   // 권한 부족(HKLM 쓰기 등)은 예외 → false
    }

    public bool WriteDword(RegistryRoot root, string keyPath, string valueName, int value)
    {
        try
        {
            using var k = Base(root).CreateSubKey(keyPath, writable: true);
            if (k == null) return false;
            k.SetValue(valueName, value, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }

    public bool DeleteKey(RegistryRoot root, string keyPath)
    {
        try
        {
            if (!KeyExists(root, keyPath)) return false;
            Base(root).DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            return true;
        }
        catch { return false; }
    }
}
