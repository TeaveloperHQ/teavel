using System.Text.Json;
using Teavel.Platform;

namespace Teavel.Setup;

/// <summary>Edge 프로필 하나에 이어진 계정.</summary>
/// <param name="Profile">프로필 폴더 이름(Default · Profile 1 …).</param>
/// <param name="Display">화면에 보이는 이름.</param>
/// <param name="Email">이어진 계정 주소. 로그인 안 됐으면 null.</param>
/// <param name="IsWorkAccount">학교·회사 계정(Entra ID)인지.</param>
public sealed record EdgeProfile(string Profile, string Display, string? Email, bool IsWorkAccount);

/// <summary>
/// Edge 에 어떤 계정이 이어져 있는지 읽는다.
///
/// <para>
/// Edge 를 빼놓으면 안 되는 까닭이 있다. 학교 업무는 대부분 브라우저에서 일어난다 —
/// 나이스·업무포털·Teams 웹·SharePoint. Edge 에 학교 계정이 이어져 있으면 그 사이트들이
/// <b>로그인 없이 그냥 열린다.</b> 안 이어져 있으면 사이트마다 비밀번호를 다시 넣게 되고,
/// 그것이 "학교 컴퓨터는 원래 불편하다" 는 인상의 큰 몫이다.
/// </para>
/// <para>
/// 읽는 곳은 Edge 의 <c>Local State</c> 파일이다. 레지스트리에는 로그인한 계정이 남지 않는다.
/// </para>
/// </summary>
public sealed class EdgeFacts
{
    private readonly ISystemPaths _paths;

    public EdgeFacts(ISystemPaths paths) => _paths = paths;

    /// <summary>Edge 사용자 자료 폴더.</summary>
    private string UserData => Path.Combine(_paths.LocalAppData, "Microsoft", "Edge", "User Data");

    /// <summary>Edge 가 깔려 있는지.</summary>
    public bool Installed => ExePath is not null;

    /// <summary>msedge.exe 경로. 못 찾으면 null.</summary>
    public string? ExePath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                             "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             "Microsoft", "Edge", "Application", "msedge.exe"),
            };
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return null;
        }
    }

    /// <summary>
    /// 프로필들과 각각에 이어진 계정. 읽지 못하면 빈 목록.
    /// </summary>
    /// <remarks>
    /// Edge 가 켜져 있어도 읽을 수 있어야 하므로 공유 읽기로 연다 —
    /// 안 그러면 "Edge 를 닫고 다시 해 보세요" 라는, 아무도 하고 싶지 않은 안내를 하게 된다.
    /// </remarks>
    public IReadOnlyList<EdgeProfile> Profiles()
    {
        var file = Path.Combine(UserData, "Local State");

        try
        {
            if (!File.Exists(file)) return Array.Empty<EdgeProfile>();

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("profile", out var profile)
                || !profile.TryGetProperty("info_cache", out var cache))
                return Array.Empty<EdgeProfile>();

            var found = new List<EdgeProfile>();
            foreach (var entry in cache.EnumerateObject())
            {
                var v = entry.Value;

                var email = Text(v, "user_name");
                var display = Text(v, "name") ?? entry.Name;

                // 학교·회사 계정인지는 <b>테넌트 id 가 개인용 공용 테넌트가 아닌지</b>로 본다.
                //
                // 두 번 틀린 자리다. 처음에는 edge_account_type 을 "aad" 같은 글자로 읽었는데
                // 그 값은 숫자였다(개인 계정에서 5 였다. 짐작한 2 가 아니다).
                // 그다음에는 '테넌트 id 가 붙어 있으면 학교 계정' 으로 봤는데, 개인 계정에도
                // 붙어 있다 — 마이크로소프트가 개인 계정 전부에 쓰는 공용 테넌트가 하나 있다.
                // 그래서 hotmail 계정이 학교 계정으로 잡혀 '✓ 연결됨' 으로 나왔다.
                //
                // 학교 계정이면 그 학교의 테넌트 id 가 붙는다. 그것만 학교 것으로 센다.
                var tenant = Text(v, "edge_account_tenant_id");
                var work = tenant is not null
                        && !tenant.Equals(PersonalTenant, StringComparison.OrdinalIgnoreCase);

                found.Add(new EdgeProfile(entry.Name, display, email, work && email is not null));
            }
            return found;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<EdgeProfile>();
        }
    }

    /// <summary>학교 계정이 이어진 프로필. 없으면 null.</summary>
    public EdgeProfile? SchoolProfile() => Profiles().FirstOrDefault(p => p.IsWorkAccount);

    /// <summary>
    /// 개인 Microsoft 계정이 모두 함께 쓰는 테넌트 id.
    /// </summary>
    /// <remarks>
    /// 마이크로소프트가 정해 둔 값이라 계정마다 다르지 않다. hotmail·outlook 계정으로
    /// 로그인해도 이 id 가 붙기 때문에, '테넌트 id 가 있으니 학교 계정' 으로 보면 안 된다.
    /// </remarks>
    private const string PersonalTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString())
            : null;
}
