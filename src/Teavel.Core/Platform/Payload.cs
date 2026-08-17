using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Teavel.Platform;

/// <summary>
/// exe 안에 넣어 둔 <c>scripts\</c> · <c>catalog\</c> 를 꺼내 놓는다.
///
/// <para>
/// 왜 넣어 두는가 — <b>포털은 exe 파일 하나만 배포하기 때문이다.</b>
/// 포털의 빌드 파이프라인은 <c>dotnet publish</c> 결과에서 <c>.exe</c> 하나만 집어
/// 내려받기로 올린다. 그 옆에 있던 scripts·catalog 는 그대로 버려진다.
/// 그러면 교사가 받은 Teavel 은 PowerShell 모듈이 없어 아무것도 하지 못한다.
/// </para>
/// <para>
/// 그래서 exe 안에 함께 묻고, 처음 쓸 때 꺼내 놓는다.
/// 꺼내 놓는 이유는 <b>PowerShell 이 파일을 필요로 하기 때문</b>이다 —
/// 모듈은 경로로 불러오는 것이라 메모리 안에서는 쓸 수 없다.
/// </para>
/// <para>
/// 개발 중이거나 직접 빌드한 판에는 exe 옆에 진짜 폴더가 있다. 그때는 그것을 쓴다 —
/// 스크립트를 고쳐 가며 시험하는데 묻어 둔 것이 덮어써 버리면 곤란하다.
/// </para>
/// </summary>
public static class Payload
{
    /// <summary>묻어 둔 것을 꺼내 놓을 곳(exe 옆에 진짜 폴더가 없을 때).</summary>
    public static string UnpackRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Teaveloper", "Teavel", "runtime");

    /// <summary>
    /// <paramref name="folder"/>(scripts · catalog)의 실제 경로를 준다.
    /// exe 옆에 있으면 그것을, 없으면 꺼내 놓고 그 자리를 돌려준다.
    /// </summary>
    public static string Ensure(string appDirectory, string folder)
    {
        var beside = Path.Combine(appDirectory, folder);

        // exe 옆에 진짜 폴더가 있으면 그대로 쓴다(개발·직접 빌드).
        if (Directory.Exists(beside) && Directory.EnumerateFiles(beside).Any()) return beside;

        var target = Path.Combine(UnpackRoot, folder);

        try
        {
            Unpack(folder, target);
        }
        catch (Exception)
        {
            // 꺼내지 못해도 옆 폴더를 가리켜 둔다 — 부르는 쪽이 '없다' 고 말할 수 있게.
            // 여기서 예외를 던지면 시작조차 못 한다.
            return Directory.Exists(target) ? target : beside;
        }

        return target;
    }

    /// <summary>
    /// 묻어 둔 파일들을 <paramref name="target"/> 에 쓴다.
    /// </summary>
    /// <remarks>
    /// 내용이 같으면 다시 쓰지 않는다. 매번 덮어쓰면 교사가 열어 둔 파일과 부딪히고,
    /// 무엇보다 <b>파일 시각이 계속 바뀌어</b> 백신·OneDrive 가 되풀이해 훑는다.
    ///
    /// 판을 올린 뒤에는 반드시 바뀌어야 하므로 내용 해시로 견준다 —
    /// 판 번호로 견주면 개발 중에 스크립트만 고쳤을 때 갱신되지 않는다.
    /// </remarks>
    private static void Unpack(string folder, string target)
    {
        var asm = Assembly.GetExecutingAssembly();
        var prefix = $"Teavel.Payload.{folder}.";

        var names = asm.GetManifestResourceNames()
                       .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                       .ToList();
        if (names.Count == 0) return;

        Directory.CreateDirectory(target);

        foreach (var name in names)
        {
            var fileName = name[prefix.Length..];
            var path = Path.Combine(target, fileName);

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;

            using var mem = new MemoryStream();
            stream.CopyTo(mem);
            var bytes = mem.ToArray();

            if (SameContent(path, bytes)) continue;

            File.WriteAllBytes(path, bytes);
        }
    }

    private static bool SameContent(string path, byte[] bytes)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var have = File.ReadAllBytes(path);
            return have.Length == bytes.Length
                && SHA256.HashData(have).AsSpan().SequenceEqual(SHA256.HashData(bytes));
        }
        catch (IOException) { return false; }
    }

    /// <summary>묻어 둔 것이 있는지. 자가점검에서 쓴다.</summary>
    public static bool HasEmbedded(string folder)
        => Assembly.GetExecutingAssembly()
                   .GetManifestResourceNames()
                   .Any(n => n.StartsWith($"Teavel.Payload.{folder}.", StringComparison.Ordinal));

    /// <summary>묻어 둔 파일 이름들. 자가점검에서 무엇이 들어갔는지 보여 준다.</summary>
    public static IReadOnlyList<string> EmbeddedNames(string folder)
    {
        var prefix = $"Teavel.Payload.{folder}.";
        return Assembly.GetExecutingAssembly()
                       .GetManifestResourceNames()
                       .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                       .Select(n => n[prefix.Length..])
                       .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                       .ToList();
    }
}
