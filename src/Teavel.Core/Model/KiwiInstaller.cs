using System.Formats.Tar;
using System.IO.Compression;
using Teavel.Intent;
using Teavel.Platform;

namespace Teavel.Model;

/// <summary>
/// 형태소 분석기(Kiwi)를 내려받아 놓는다.
///
/// <para>
/// 언어 모델과 같은 방식이다 — 핀 고정한 주소에서 받고, 이어받기가 되고, 데이터 폴더에 둔다.
/// 다만 훨씬 작다(84MB + 12MB). <b>이것만 있어도 말귀가 눈에 띄게 좋아진다</b> —
/// '합쳐줘' 와 '합치기' 를 같은 말로 알아보기 때문이다.
/// </para>
/// <para>
/// <b>모델과 네이티브의 판이 반드시 같아야 한다.</b> 다른 판을 섞으면
/// <c>Cannot open morphology file</c> 로 끝난다. 그래서 둘 다 같은 릴리스에서 받는다.
/// </para>
/// </summary>
public static class KiwiInstaller
{
    /// <summary>이미 쓸 수 있는지.</summary>
    public static bool Ready(ISystemPaths paths) => KiwiAssets.FindModel(paths) is not null;

    /// <summary>
    /// 받아서 푼다. 이미 있으면 아무것도 하지 않는다.
    /// </summary>
    /// <returns>모델이 놓인 폴더.</returns>
    public static async Task<string> InstallAsync(
        ISystemPaths paths,
        ModelDownloader.ProgressCallback? progress = null,
        CancellationToken ct = default)
    {
        if (KiwiAssets.FindModel(paths) is { } already) return already;

        var target = KiwiAssets.DefaultDirectory(paths);
        Directory.CreateDirectory(target);

        var temp = Path.Combine(paths.DataDirectory, "kiwi-download");
        Directory.CreateDirectory(temp);

        try
        {
            // ① 형태소 모델 — 크다. 여기서 대부분의 시간이 간다.
            var modelPack = Path.Combine(temp, "kiwi-model.tgz");
            await ModelDownloader.DownloadAsync(
                modelPack, TeavelModelConfig.KiwiModelUrl,
                TeavelModelConfig.KiwiModelApproxBytes, progress,
                expectGguf: false, ct: ct).ConfigureAwait(false);
            ExtractTgz(modelPack, temp);

            // 묶음 안에서 실제 모델 폴더를 찾아 옮긴다.
            // 판마다 안의 짜임이 달라(models/cong/base 등) 경로를 박아 두면 곧 틀린다.
            var found = FindModelFolder(temp)
                ?? throw new InvalidOperationException("받은 묶음 안에서 형태소 모델을 찾지 못했습니다.");

            foreach (var f in Directory.EnumerateFiles(found))
                File.Copy(f, Path.Combine(target, Path.GetFileName(f)), overwrite: true);

            // ② 네이티브 — 플랫폼마다 다르다.
            var nativePack = Path.Combine(temp, OperatingSystem.IsWindows() ? "kiwi-native.zip" : "kiwi-native.tgz");
            await ModelDownloader.DownloadAsync(
                nativePack, TeavelModelConfig.KiwiNativeUrl,
                TeavelModelConfig.KiwiNativeApproxBytes, progress,
                expectGguf: false, ct: ct).ConfigureAwait(false);

            var nativeDir = Path.Combine(temp, "native");
            Directory.CreateDirectory(nativeDir);
            if (OperatingSystem.IsWindows()) ZipFile.ExtractToDirectory(nativePack, nativeDir, overwriteFiles: true);
            else ExtractTgz(nativePack, nativeDir);

            // 공유 라이브러리만 챙긴다.
            //
            // 처음에는 이름에 'kiwi' 가 든 것을 다 가져왔는데, 묶음 안에 kiwi-cli ·
            // kiwi-model-builder · kiwi-test 같은 실행 파일이 들어 있어 380MB 가 딸려 왔다.
            // 교사 PC 에 쌓일 자리라 필요한 것만 남긴다.
            foreach (var f in Directory.EnumerateFiles(nativeDir, "*kiwi*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                if (!IsSharedLibrary(name)) continue;

                try { File.Copy(f, Path.Combine(target, name), overwrite: true); } catch (IOException) { }
            }

            Morphemes.Forget();          // 다음 번에 다시 찾아보게
            return target;
        }
        finally
        {
            // 받은 묶음은 남겨 둘 까닭이 없다. 100MB 가 넘는다.
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>공유 라이브러리인지. libkiwi.so · libkiwi.so.0.23.2 · kiwi.dll 등.</summary>
    private static bool IsSharedLibrary(string name)
        => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
        || name.Contains(".so", StringComparison.OrdinalIgnoreCase);

    /// <summary>푼 것 안에서 모델 파일들이 있는 폴더를 찾는다.</summary>
    private static string? FindModelFolder(string root)
    {
        foreach (var marker in new[] { "sj.morph", "default.dict" })
        {
            var hit = Directory.EnumerateFiles(root, marker, SearchOption.AllDirectories).FirstOrDefault();
            if (hit is not null) return Path.GetDirectoryName(hit);
        }
        return null;
    }

    /// <summary>.tgz 를 푼다. .NET 에 tar 가 있어 바깥 프로그램이 필요 없다.</summary>
    private static void ExtractTgz(string archive, string target)
    {
        using var file = File.OpenRead(archive);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gz, target, overwriteFiles: true);
    }
}
