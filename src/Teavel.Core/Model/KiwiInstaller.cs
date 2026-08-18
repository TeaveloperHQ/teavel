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
/// <para>
/// <b>둘 다 갖춰졌을 때만 자리에 놓는다.</b> 예전에는 받는 대로 바로 최종 폴더에 부었는데,
/// 모델(1/2)은 받고 네이티브(2/2)에서 인터넷이 끊기면 이런 상태로 남았다 —
/// 폴더에 모델 파일이 있으니 '이미 갖춰져 있습니다' 라고 보고하면서, 정작 쓰려고 하면
/// 네이티브가 없어 못 쓴다. 다시 받으라고 쳐도 이미 있다며 아무것도 하지 않으니
/// <b>스스로는 영영 못 낫는 상태</b>였다. 그래서 이제는 다 갖춰진 뒤에 한 번에 옮긴다.
/// </para>
/// </summary>
public static class KiwiInstaller
{
    /// <summary>이미 쓸 수 있는지.</summary>
    public static bool Ready(ISystemPaths paths) => KiwiAssets.FindUsable(paths) is not null;

    /// <summary>
    /// 받아서 푼다. 이미 있으면 아무것도 하지 않는다.
    /// </summary>
    /// <returns>모델이 놓인 폴더.</returns>
    /// <param name="onStep">
    /// 지금 무엇을 받는 중인지 알린다. 두 개를 잇달아 받으므로 이것이 없으면
    /// 진행률이 0%→100% 를 두 번 돌아 <b>다시 시작한 것처럼 보인다</b>(실제로 그렇게 보였다).
    /// </param>
    public static async Task<string> InstallAsync(
        ISystemPaths paths,
        ModelDownloader.ProgressCallback? progress = null,
        Action<string>? onStep = null,
        CancellationToken ct = default)
    {
        if (KiwiAssets.FindUsable(paths) is { } already) return already;

        var target = KiwiAssets.DefaultDirectory(paths);

        // 받는 자리와 푸는 자리. 다 갖춰지기 전에는 target 을 만들지도 않는다 —
        // 빈 폴더나 반만 든 폴더가 남으면 그것을 '설치됨' 으로 잘못 읽게 된다.
        var temp = Path.Combine(paths.DataDirectory, "kiwi-download");
        Directory.CreateDirectory(temp);

        var staging = Path.Combine(temp, "staging");
        Directory.CreateDirectory(staging);

        try
        {
            // ① 형태소 모델 — 크다. 여기서 대부분의 시간이 간다.
            onStep?.Invoke("말뭉치 (1/2)");
            var modelPack = Path.Combine(temp, "kiwi-model.tgz");
            await ModelDownloader.DownloadAsync(
                modelPack, TeavelModelConfig.KiwiModelUrl,
                TeavelModelConfig.KiwiModelApproxBytes, progress,
                expectGguf: false, ct: ct).ConfigureAwait(false);

            var modelOut = Path.Combine(temp, "model");
            Directory.CreateDirectory(modelOut);
            ExtractTgz(modelPack, modelOut);

            // 묶음 안에서 실제 모델 폴더를 찾아 옮긴다.
            // 판마다 안의 짜임이 달라(models/cong/base 등) 경로를 박아 두면 곧 틀린다.
            var found = FindModelFolder(modelOut)
                ?? throw new InvalidOperationException("받은 묶음 안에서 형태소 모델을 찾지 못했습니다.");

            foreach (var f in Directory.EnumerateFiles(found))
                File.Copy(f, Path.Combine(staging, Path.GetFileName(f)), overwrite: true);

            // ② 네이티브 — 플랫폼마다 다르다.
            onStep?.Invoke("분석기 (2/2)");
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
            var nativeCount = 0;
            foreach (var f in Directory.EnumerateFiles(nativeDir, "*kiwi*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f);
                if (!KiwiAssets.IsSharedLibrary(name)) continue;

                try { File.Copy(f, Path.Combine(staging, name), overwrite: true); nativeCount++; } catch (IOException) { }
            }

            // 여기서 막는다. 이 확인이 없으면 '모델만 있는 폴더' 가 그대로 자리를 차지하고,
            // 그 뒤로는 이미 깔린 것으로 보여 다시 받지도 못한다.
            if (nativeCount == 0)
                throw new InvalidOperationException(
                    "받은 묶음 안에서 분석기(공유 라이브러리)를 찾지 못했습니다.");

            if (!File.Exists(Path.Combine(staging, KiwiAssets.Marker)))
                throw new InvalidOperationException("받은 묶음에 형태소 모델 파일이 모자랍니다.");

            // 다 갖춰졌다. 이제서야 자리에 놓는다.
            Directory.CreateDirectory(target);
            foreach (var f in Directory.EnumerateFiles(staging))
                File.Copy(f, Path.Combine(target, Path.GetFileName(f)), overwrite: true);

            Morphemes.Forget();          // 다음 번에 다시 찾아보게
            return target;
        }
        finally
        {
            // 받은 묶음은 남겨 둘 까닭이 없다. 100MB 가 넘는다.
            // 지우지 못해도 설치 자체는 끝난 것이므로 여기서 예외를 내보내지 않는다 —
            // 임시 파일 청소 실패가 '설치 실패' 로 보고되면 원인을 엉뚱한 데서 찾게 된다.
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    /// <summary>푼 것 안에서 모델 파일들이 있는 폴더를 찾는다.</summary>
    private static string? FindModelFolder(string root)
    {
        foreach (var marker in new[] { KiwiAssets.Marker, "sj.morph" })
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
