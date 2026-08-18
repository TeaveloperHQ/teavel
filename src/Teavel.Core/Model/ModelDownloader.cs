using System.Net;
using System.Net.Http.Headers;
using Teavel.Platform;

namespace Teavel.Model;

/// <summary>
/// GGUF 모델 내려받기 — 생기부 도우미(LLLM)의 Downloader 와 같은 방식.
///
/// 학교 인터넷은 자주 끊기므로 네 가지를 지킨다:
///   · 이어받기 — .part 에 받아 두고 Range 요청으로 이어붙인다.
///   · 멎으면 끊는다 — 자료가 오다 말면 한없이 기다리지 않고 끊고 까닭을 말한다.
///   · 검증 — 앞 4바이트가 GGUF 인지 본다(로그인 페이지 HTML 을 받아 놓고 모델인 줄 아는 사고를 막는다).
///   · 원자적 완료 — 다 받고 검증까지 끝난 뒤에야 최종 이름으로 옮긴다.
/// </summary>
public static class ModelDownloader
{
    /// <summary>내려받기 진행 알림.</summary>
    public delegate void ProgressCallback(long downloaded, long total);

    /// <summary>
    /// 자료가 한 조각도 오지 않은 채 이만큼 지나면 끊는다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>HttpClient.Timeout 은 이 일에 쓸 수 없다.</b> 그것은 '내려받기 전체' 에 걸리는
    /// 시한이라, 1GB 를 느린 학교 인터넷으로 정상적으로 받는 중에도 걸려 버린다.
    /// 그래서 예전에는 60분으로 크게 잡아 두었는데, 그러면 반대쪽 사고가 난다 —
    /// 학교 방화벽이 연결만 붙잡고 아무것도 주지 않을 때 <b>화면이 60분 동안 멈춘다.</b>
    /// 진행률 줄이 한 자리에 선 채라 교사에게는 프로그램이 죽은 것으로 보인다.
    /// </para>
    /// <para>
    /// 재야 할 것은 전체 시간이 아니라 <b>자료가 끊긴 시간</b>이다.
    /// 느려도 오고 있으면 얼마든지 기다리고, 멎으면 곧 끊는다.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    /// <summary>연결과 응답 머리말까지 기다리는 시간.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

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
    /// <param name="approxBytes">
    /// 근사 크기. 진행률 표시에 쓰고, <b>서버가 길이를 알려 주지 않을 때만</b> 완결성 판단에 쓴다.
    /// </param>
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

        // 시한은 우리가 직접 잰다(StallTimeout). HttpClient 자신의 시한은 꺼 둔다 —
        // 켜 두면 정상적으로 느린 내려받기까지 도중에 끊는다.
        var http = client ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var ownsClient = client is null;

        try
        {
            try
            {
                return await AttemptAsync(mayResume: true).ConfigureAwait(false);
            }
            catch (StaleResumeException)
            {
                // 남아 있던 .part 가 쓸모없는 것이었다. 지우고 처음부터 받는다.
                TryDelete(part);
                return await AttemptAsync(mayResume: false).ConfigureAwait(false);
            }
        }
        finally
        {
            if (ownsClient) http.Dispose();
        }

        async Task<string> AttemptAsync(bool mayResume)
        {
            var existing = mayResume && File.Exists(part) ? new FileInfo(part).Length : 0;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(ConnectTimeout);

            HttpResponseMessage resp;
            try
            {
                resp = await http
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connect.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException(
                    $"서버가 {ConnectTimeout.TotalSeconds:0}초 안에 응답하지 않았습니다. "
                  + "학교 인터넷이나 방화벽 때문일 수 있습니다. 잠시 뒤 다시 시도해 주세요.");
            }

            // 서버가 알려 준 전체 길이. 안 알려 주면 null 이고, 그때만 근사값을 믿는다.
            long? declared;

            using (resp)
            {
                // 416 — 받아 둔 .part 가 서버 파일과 같거나 크다는 뜻이다.
                //
                // 여기서 그냥 EnsureSuccessStatusCode 로 넘기면 다시 받을 때마다 같은 자리에서
                // 같은 오류가 나서 **영영 낫지 않는다**. 한 번 잘못 받으면 그 뒤로는 무엇을
                // 해도 안 되는 상태가 된다. 지우고 처음부터 받게 한다.
                if (existing > 0 && resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    throw new StaleResumeException();

                resp.EnsureSuccessStatusCode();

                // 206(부분 응답)일 때만 이어붙인다. 200 이면 서버가 처음부터 다시 주는 것이므로 새로 쓴다.
                var resumed = existing > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                var already = resumed ? existing : 0;

                declared = resp.Content.Headers.ContentLength is { } len ? already + len : null;

                var total = declared ?? approxBytes;
                var downloaded = already;

                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = new FileStream(
                    part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[256 * 1024];
                    while (true)
                    {
                        // 읽기 하나하나에 시한을 건다. 이것이 없으면 연결이 멎었을 때
                        // 아무도 끊어 주지 않아 화면이 그대로 선다.
                        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        stall.CancelAfter(StallTimeout);

                        int read;
                        try
                        {
                            read = await src.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // 받던 데까지는 남겨 둔다 — 다음에 이어받으면 된다.
                            throw new IOException(
                                $"내려받는 중에 {StallTimeout.TotalSeconds:0}초 동안 아무것도 오지 않아 끊었습니다. "
                              + "받은 데까지는 남겨 두었으니, 다시 시도하시면 이어서 받습니다.");
                        }

                        if (read <= 0) break;

                        await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        downloaded += read;
                        progress?.Invoke(downloaded, total);
                    }
                }
            }

            // 받은 것이 정말 모델인지 확인한다.
            //
            // **여기서 걸리면 .part 를 반드시 지운다.** 남겨 두면 다음 실행이 그 뒤에
            // 이어붙이려 들고 서버는 416 을 준다. 한 번 어긋난 것이 영구 고장이 되는 자리다.
            if (expectGguf && !LooksLikeGguf(part))
            {
                TryDelete(part);
                throw new InvalidDataException(
                    "내려받은 파일이 올바른 모델(GGUF)이 아닙니다. "
                  + "학교 네트워크가 로그인 페이지로 돌려보냈을 수 있습니다.");
            }

            // 서버가 길이를 알려 줬으면 그 길이로, 아니면 근사값의 9할로 본다.
            //
            // 근사값은 어디까지나 우리 짐작이다. 그것만 믿으면 실제 파일이 짐작보다 조금
            // 작을 때 **멀쩡히 다 받아 놓고도 계속 실패한다** — 판이 바뀌어 파일 크기가
            // 달라지면 우리가 손대기 전까지 아무도 못 받는 상태가 된다.
            var got = new FileInfo(part).Length;
            var floor = declared ?? (long)(approxBytes * 0.9);

            if (got < floor)
            {
                TryDelete(part);
                throw new InvalidDataException(
                    $"끝까지 받지 못했습니다({got / 1024 / 1024}MB / {floor / 1024 / 1024}MB). "
                  + "받다 만 것은 지웠으니 다시 시도해 주세요.");
            }

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(part, destination);
            return destination;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>이어받으려던 .part 가 쓸모없다는 신호. 이 파일 밖으로 나가지 않는다.</summary>
    private sealed class StaleResumeException : Exception;
}
