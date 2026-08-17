using System.Net;
using System.Net.Http.Headers;
using Teavel.Platform;

namespace Teavel.Model;

/// <summary>
/// GGUF 모델 내려받기 — 생기부 도우미(LLLM)의 Downloader 와 같은 방식.
///
/// 학교 인터넷은 자주 끊기므로 세 가지를 지킨다:
///   · 이어받기 — .part 에 받아 두고 Range 요청으로 이어붙인다.
///   · 검증 — 앞 4바이트가 GGUF 인지 본다(로그인 페이지 HTML 을 받아 놓고 모델인 줄 아는 사고를 막는다).
///   · 원자적 완료 — 다 받고 검증까지 끝난 뒤에야 최종 이름으로 옮긴다.
/// </summary>
public static class ModelDownloader
{
    /// <summary>내려받기 진행 알림.</summary>
    public delegate void ProgressCallback(long downloaded, long total);

    /// <summary>모델이 놓일 자리(데이터 폴더의 models\).</summary>
    public static string DefaultModelPath(ISystemPaths paths)
        => Path.Combine(paths.DataDirectory, "models", TeavelModelConfig.ModelFilename);

    /// <summary>쓸 만한 모델 파일이 이미 있는지.</summary>
    public static bool Exists(string? path)
        => path is not null && File.Exists(path) && new FileInfo(path).Length > 1_000_000;

    /// <summary>파일 앞 4바이트가 GGUF 매직인지.</summary>
    public static bool LooksLikeGguf(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> buf = stackalloc byte[4];
            return f.Read(buf) == 4 && buf[0] == (byte)'G' && buf[1] == (byte)'G'
                                    && buf[2] == (byte)'U' && buf[3] == (byte)'F';
        }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// 모델을 내려받는다. 이미 있으면 그대로 돌려준다.
    /// </summary>
    /// <param name="destination">저장할 경로.</param>
    /// <param name="url">내려받을 주소. 비어 있으면 <see cref="InvalidOperationException"/>.</param>
    /// <param name="approxBytes">근사 크기(진행률·완결성 판단용).</param>
    public static async Task<string> DownloadAsync(
        string destination,
        string url,
        long approxBytes,
        ProgressCallback? progress = null,
        HttpClient? client = null,
        bool expectGguf = true,
        CancellationToken ct = default)
    {
        if (Exists(destination)) return destination;

        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                "언어 모델을 내려받을 주소가 정해져 있지 않습니다. "
              + "TEAVEL_GGUF_URL 환경변수로 지정하거나, 모델 파일을 models 폴더에 직접 넣어 주세요.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var part = destination + ".part";
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        var http = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        var ownsClient = client is null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

            using var resp = await http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            // 206(부분 응답)일 때만 이어붙인다. 200 이면 서버가 처음부터 다시 주는 것이므로 새로 쓴다.
            var resumed = existing > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
            var already = resumed ? existing : 0;

            var total = resp.Content.Headers.ContentLength is { } len ? already + len : approxBytes;
            var downloaded = already;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(
                part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    downloaded += read;
                    progress?.Invoke(downloaded, total);
                }
            }

            // 받은 것이 정말 모델인지 확인한다. 아니면 지운다 —
            // 깨진 .part 를 남겨 두면 다음 실행에서 그 뒤에 이어붙여 영영 낫지 않는다.
            if (expectGguf && !LooksLikeGguf(part))
            {
                TryDelete(part);
                throw new InvalidDataException(
                    "내려받은 파일이 올바른 모델(GGUF)이 아닙니다. "
                  + "학교 네트워크가 로그인 페이지로 돌려보냈을 수 있습니다.");
            }

            if (new FileInfo(part).Length < (long)(approxBytes * 0.9))
                throw new InvalidDataException("모델을 끝까지 받지 못했습니다. 다시 시도해 주세요.");

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(part, destination);
            return destination;
        }
        catch (OperationCanceledException)
        {
            // 취소는 .part 를 남겨 둔다 — 다음에 이어받기 위해서다.
            throw;
        }
        finally
        {
            if (ownsClient) http.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
