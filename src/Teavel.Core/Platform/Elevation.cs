using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;

namespace Teavel.Platform;

/// <summary>지금 이 프로그램이 어떤 권한으로 돌고 있는지.</summary>
public enum ElevationKind
{
    /// <summary>관리자 권한으로 돌고 있다. 할 수 있다.</summary>
    Elevated,

    /// <summary>
    /// 관리자인데 UAC 가 권한을 걸러 놓았다 — <b>승인 창 한 번이면 된다.</b>
    /// </summary>
    Filtered,

    /// <summary>이 계정은 이 컴퓨터의 관리자가 아니다. 다른 사람의 계정이 필요하다.</summary>
    NotAdministrator,

    /// <summary>Windows 가 아니거나 알아보지 못했다.</summary>
    Unknown,
}

/// <summary>권한을 올려 다시 띄운 결과.</summary>
public enum ElevationLaunch
{
    /// <summary>새 창이 떴다. 이 창은 하던 일을 접어야 한다.</summary>
    Started,

    /// <summary>교사가 승인 창에서 [아니오] 를 눌렀다.</summary>
    Declined,

    /// <summary>띄우지 못했다.</summary>
    Failed,

    /// <summary>이 환경에서는 할 수 없다.</summary>
    NotSupported,
}

/// <summary>
/// 관리자 권한이 필요할 때 <b>스스로 올린다.</b>
///
/// <para>
/// 왜 필요한가 — 학교 컴퓨터에서 선생님 계정은 대개 <b>이미 관리자 그룹에 들어 있다.</b>
/// 그런데 UAC 가 평소에는 그 권한을 걸러 두기 때문에, 프로그램이 보기에는 관리자가 아니다.
/// 그래서 예전에는 이렇게 말했다.
/// </para>
/// <code>
///   PowerShell 을 [관리자 권한으로 실행] 한 뒤 다시 해 주세요.
/// </code>
/// <para>
/// 이건 선생님에게 <b>또 하나의 벽</b>이다. 어디를 눌러야 관리자로 실행되는지 아는 분이라면
/// 애초에 Teavel 이 필요하지 않았을 것이다. 승인 창 한 번이면 되는 일을 그렇게 미룰 까닭이 없다.
/// </para>
/// <para>
/// <b>관리자 그룹에 있는지</b>를 알아내는 것이 요점이다. 걸러진 토큰에서는
/// <c>IsInRole(Administrator)</c> 도 false 이고 <c>WindowsIdentity.Groups</c> 에도
/// Administrators 가 아예 없다(실기 확인). 토큰의 <b>승격 유형</b>을 물어야 갈린다 —
/// Limited 면 '관리자인데 걸러진 것' 이다.
/// </para>
/// </summary>
public static class Elevation
{
    // GetTokenInformation 의 TokenElevationType.
    private const int TokenElevationType = 18;

    private const int TokenElevationTypeDefault = 1;   // UAC 꺼짐, 또는 일반 사용자
    private const int TokenElevationTypeFull = 2;      // 승격됨
    private const int TokenElevationTypeLimited = 3;   // 관리자인데 걸러짐

    /// <summary>교사가 승인 창에서 [아니오] 를 눌렀을 때의 오류 번호.</summary>
    private const int ErrorCancelled = 1223;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass,
        out int tokenInformation, int tokenInformationLength, out int returnLength);

    /// <summary>지금 권한 상태.</summary>
    public static ElevationKind Current
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return ElevationKind.Unknown;

            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var isAdminNow = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

                if (GetTokenInformation(identity.Token, TokenElevationType, out var type, sizeof(int), out _))
                {
                    return type switch
                    {
                        TokenElevationTypeFull => ElevationKind.Elevated,
                        TokenElevationTypeLimited => ElevationKind.Filtered,

                        // UAC 를 꺼 둔 컴퓨터다. 그러면 지금 권한이 곧 최종 권한이다.
                        TokenElevationTypeDefault => isAdminNow
                            ? ElevationKind.Elevated
                            : ElevationKind.NotAdministrator,

                        _ => isAdminNow ? ElevationKind.Elevated : ElevationKind.Unknown,
                    };
                }

                // 토큰을 못 물어봤다. 아는 것까지만 말한다 —
                // '관리자가 아니다' 와 '모르겠다' 를 뭉뚱그리면 될 일을 안 된다고 하게 된다.
                return isAdminNow ? ElevationKind.Elevated : ElevationKind.Unknown;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SecurityException)
            {
                return ElevationKind.Unknown;
            }
        }
    }

    /// <summary>관리자 권한으로 돌고 있는지.</summary>
    public static bool IsElevated => Current == ElevationKind.Elevated;

    /// <summary>승인 창 한 번으로 올릴 수 있는지.</summary>
    public static bool CanElevate => Current == ElevationKind.Filtered;

    /// <summary>
    /// 같은 명령을 관리자 권한으로 다시 띄운다.
    /// </summary>
    /// <param name="exePath">우리 실행 파일. <c>dotnet run</c> 으로 돌고 있으면 쓸 수 없다.</param>
    /// <param name="args">넘겨줄 인자 — 하던 일을 그대로 이어서 하도록.</param>
    /// <param name="workingDirectory">
    /// 시작할 폴더. 이걸 안 넘기면 승격된 창은 <c>C:\Windows\System32</c> 에서 시작한다 —
    /// 탐색기에서 폴더를 열어 들어온 경우 그 폴더를 잃는다.
    /// </param>
    public static ElevationLaunch Relaunch(
        string exePath, IEnumerable<string> args, string? workingDirectory = null)
    {
        if (!OperatingSystem.IsWindows()) return ElevationLaunch.NotSupported;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return ElevationLaunch.NotSupported;

        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                // runas 로 띄우려면 셸을 거쳐야 한다. 이걸 false 로 두면 승격이 안 된다.
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            };

            foreach (var a in args) psi.ArgumentList.Add(a);

            return Process.Start(psi) is not null ? ElevationLaunch.Started : ElevationLaunch.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return ElevationLaunch.Declined;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return ElevationLaunch.Failed;
        }
    }
}
