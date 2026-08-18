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

                // 학교·회사 계정인지는 <b>테넌트 id 가 붙어 있는지</b>로 본다.
                //
                // edge_account_type 을 먼저 썼다가 틀렸다 — 그 값은 "aad" 같은 글자가 아니라
                // 숫자다. 글자로 읽으면 언제나 못 읽어서, 멀쩡히 학교 계정으로 로그인한
                // 컴퓨터도 '로그인 안 됨' 으로 나온다. 테넌트 id 는 개인 계정에는 아예 없어서
                // 이쪽이 훨씬 분명하다.
                var tenant = Text(v, "edge_account_tenant_id");
                var work = tenant is not null || Number(v, "edge_account_type") == AadAccount;

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

    /// <summary>edge_account_type 이 이 값이면 학교·회사(Entra ID) 계정이다.</summary>
    private const int AadAccount = 2;

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString())
            : null;

    private static int? Number(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : null;
}
