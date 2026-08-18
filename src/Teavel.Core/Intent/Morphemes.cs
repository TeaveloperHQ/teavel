using Teavel.Platform;

namespace Teavel.Intent;

/// <summary>
/// 한국어에서 <b>뜻을 지닌 조각만</b> 뽑는다. 있으면 Kiwi 를 쓰고, 없으면 예전 방식으로 돈다.
///
/// <para>
/// 지금까지 낱말 라우터는 <b>끝 한 글자를 떼는 것</b>이 전부였다. 그래서 이런 것이 샜다.
/// </para>
/// <code>
///   교사: "엑셀 좀 합쳐줘"   →  합쳐줘 · 합쳐
///   우리: "합치기"           →  합치기 · 합치
///                               ↑ 둘이 안 맞는다
/// </code>
/// <para>
/// 형태소로 나누면 둘 다 <c>합치</c> 가 되어 맞는다.
/// 조사·어미·기호를 버리고 <b>어간과 명사만</b> 남기는 것이 요점이다.
/// </para>
/// <para>
/// Kiwi 가 없어도 Teavel 은 그대로 돈다 — 예전 방식으로 내려갈 뿐이다.
/// 학교 인터넷이 느려 안 받은 분도 있을 것이고, 그때 아무것도 못 하게 되면 안 된다.
/// </para>
/// </summary>
public static class Morphemes
{
    private static readonly object Gate = new();
    private static KiwiNative? _kiwi;
    private static bool _tried;

    /// <summary>
    /// Kiwi 를 쓰고 있는지. 자가점검·평가에서 어느 쪽으로 잰 것인지 밝히는 데 쓴다.
    /// </summary>
    /// <remarks>
    /// <b>여기서 실제로 찾아본다.</b> 그냥 <c>_kiwi</c> 를 들여다보기만 하면, 아직
    /// 아무 말도 안 시킨 자가점검에서는 언제나 '없음' 이 나온다 — 한 번 그렇게 짰다가
    /// 멀쩡히 깔린 분석기를 없다고 보고했다.
    /// </remarks>
    public static bool KiwiReady => Get() is not null;

    /// <summary>못 쓰는 까닭. 쓰고 있거나 아직 안 찾아봤으면 빈 문자열.</summary>
    public static string Why { get; private set; } = "";

    /// <summary>
    /// 뜻을 지닌 조각만 남긴다.
    /// </summary>
    /// <remarks>
    /// <para>남기는 품사:</para>
    /// <list type="bullet">
    /// <item><c>NNG·NNP·NNB·NR</c> — 명사. '엑셀' · '학번' · '문서'</item>
    /// <item><c>VV·VA·VX·XR</c> — 용언의 어간. '합치' · '나누' · '바꾸'</item>
    /// <item><c>SL·SH·SN</c> — 로마자·한자·숫자. 'pdf' · 'csv' · '2'</item>
    /// <item><c>MAG</c> — 부사. '다시' · '전부'</item>
    /// </list>
    /// <para>
    /// 버리는 것: 조사(J*)·어미(E*)·기호(S*의 나머지)·접사.
    /// 그것들이 낱말 겹침을 흐리는 주범이다 — 어느 문장에나 나오기 때문이다.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Content(string text)
    {
        var kiwi = Get();
        if (kiwi is null) return Array.Empty<string>();

        try
        {
            var outp = new List<string>();
            foreach (var (form, tag) in kiwi.Tokenize(text))
            {
                if (form.Length == 0) continue;
                if (!Keep(tag)) continue;
                outp.Add(form.ToLowerInvariant());
            }
            return outp;
        }
        catch (Exception ex)
        {
            // 한 번 실패하면 그 뒤로는 쓰지 않는다. 낱말마다 예외를 내며 느려지는 것이 더 나쁘다.
            lock (Gate)
            {
                _kiwi?.Dispose();
                _kiwi = null;
                Why = ex.Message;
            }
            return Array.Empty<string>();
        }
    }

    private static bool Keep(string tag)
        => tag.StartsWith("NN", StringComparison.Ordinal)   // 명사
        || tag == "NR"                                      // 수사
        || tag.StartsWith("VV", StringComparison.Ordinal)   // 동사
        || tag.StartsWith("VA", StringComparison.Ordinal)   // 형용사
        || tag == "VX"                                      // 보조용언
        || tag == "XR"                                      // 어근
        || tag == "MAG"                                     // 부사
        || tag is "SL" or "SH" or "SN";                     // 로마자·한자·숫자

    /// <summary>
    /// 준비되면 Kiwi 를, 아니면 null. 한 번만 찾아본다.
    /// </summary>
    /// <remarks>
    /// 시작할 때 미리 올리지 않는다. 교사가 명령 한 줄도 안 치고 끝낼 수도 있는데
    /// 그때마다 모델을 읽으면 뜨는 데만 시간이 걸린다.
    /// </remarks>
    private static KiwiNative? Get()
    {
        lock (Gate)
        {
            if (_tried) return _kiwi;
            _tried = true;

            try
            {
                var model = KiwiAssets.FindModel();
                if (model is null) { Why = "형태소 분석기가 아직 없습니다."; return null; }

                // 모델은 있는데 분석기가 없는 경우를 따로 말한다. 그냥 두면
                // DllNotFoundException 의 영문 메시지가 그대로 교사 화면에 나가서
                // 무엇이 모자란 것인지 알 길이 없다.
                if (!KiwiAssets.HasNative(model))
                {
                    Why = "말뭉치는 있는데 분석기가 없습니다. '모델' 을 다시 실행하면 마저 받습니다.";
                    return null;
                }

                // FindModel 은 이미 '폴더' 를 준다. 여기서 또 GetDirectoryName 을 하면
                // 부모로 올라가 네이티브를 못 찾는다 — 한 번 그렇게 당했다.
                KiwiNative.LookAlsoIn(model);
                _kiwi = new KiwiNative(model);
                Why = "";
            }
            catch (Exception ex)
            {
                _kiwi = null;
                Why = ex.Message;
            }

            return _kiwi;
        }
    }

    /// <summary>다시 찾아보게 한다(내려받은 직후).</summary>
    public static void Forget()
    {
        lock (Gate)
        {
            _kiwi?.Dispose();
            _kiwi = null;
            _tried = false;
            Why = "";
        }
    }
}

/// <summary>Kiwi 모델·네이티브가 어디 있는지.</summary>
/// <remarks>
/// 생기부 도우미가 이미 받아 두었으면 그것을 빌려 쓴다 — 모델 파일과 같은 방식이다.
/// 같은 판이면 결과가 같으므로 두 번 받을 까닭이 없다.
/// </remarks>
public static class KiwiAssets
{
    /// <summary>모델 폴더 이름. 생기부 도우미와 같게 맞춘다.</summary>
    public const string ModelDirName = "kiwi_model";

    /// <summary>
    /// 이 파일이 있어야 모델 폴더로 본다.
    /// </summary>
    /// <remarks>
    /// 판마다 들어 있는 파일이 다르다 — 0.23 의 CoNg 모델은 <c>cong.mdl</c> 을 쓰고
    /// 옛 판은 <c>sj.morph</c> 를 쓴다. <c>default.dict</c> 는 어느 판에나 있어 이것으로 알아본다.
    /// </remarks>
    public const string Marker = "default.dict";

    /// <summary>Teavel 이 받아 둘 자리.</summary>
    public static string DefaultDirectory(ISystemPaths paths)
        => Path.Combine(paths.DataDirectory, ModelDirName);

    /// <summary>공유 라이브러리인지. libkiwi.so · libkiwi.so.0.23.2 · kiwi.dll 등.</summary>
    public static bool IsSharedLibrary(string name)
        => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
        || name.Contains(".so", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <b>정말 쓸 수 있는</b> 모델 폴더를 찾는다. 없으면 null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FindModel"/> 과 달리 <b>분석기(네이티브)까지 있는지</b> 본다.
    /// 모델만 있고 네이티브가 없으면 쓸 수 없는데, 그것을 '갖춰짐' 으로 세면
    /// 다시 받으라고 해도 이미 있다며 아무것도 하지 않는다 — 스스로는 못 낫는 상태가 된다.
    /// 실제로 네이티브 내려받기가 끊겼을 때 그렇게 됐다.
    /// </para>
    /// <para>
    /// 네이티브는 모델 폴더 옆에 있을 수도 있고, 배포본처럼 exe 옆에 있을 수도 있다.
    /// <see cref="KiwiNative"/> 가 두 곳을 다 보므로 여기서도 두 곳을 다 본다.
    /// </para>
    /// </remarks>
    public static string? FindUsable(ISystemPaths? paths = null)
    {
        var model = FindModel(paths);
        return model is not null && HasNative(model) ? model : null;
    }

    /// <summary>분석기(공유 라이브러리)가 이 폴더나 exe 폴더에 있는지.</summary>
    public static bool HasNative(string modelDirectory)
    {
        foreach (var dir in new[]
                 {
                     modelDirectory,
                     Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
                     AppContext.BaseDirectory,
                 })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                if (Directory.EnumerateFiles(dir, "*kiwi*").Any(f => IsSharedLibrary(Path.GetFileName(f))))
                    return true;
            }
            catch (IOException) { }
        }
        return false;
    }

    /// <summary>모델 폴더를 찾는다. 없으면 null.</summary>
    public static string? FindModel(ISystemPaths? paths = null)
    {
        foreach (var dir in Candidates(paths))
        {
            try
            {
                if (!Directory.Exists(dir)) continue;

                // 파일이 바로 있는 경우와 한 겹 더 들어간 경우를 모두 본다
                // (압축을 풀면 kiwi_model/kiwi_model/… 이 되는 일이 있다).
                if (File.Exists(Path.Combine(dir, Marker))) return dir;

                foreach (var sub in Directory.EnumerateDirectories(dir))
                    if (File.Exists(Path.Combine(sub, Marker))) return sub;
            }
            catch (IOException) { }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(ISystemPaths? paths)
    {
        var env = Environment.GetEnvironmentVariable("TEAVEL_KIWI_MODEL");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;

        yield return Path.Combine(AppContext.BaseDirectory, ModelDirName);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Teaveloper", "Teavel", ModelDirName);

        // 생기부 도우미가 받아 둔 것.
        yield return Path.Combine(local, "SaenggibuHelper", ModelDirName);
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "saenggibu-helper", ModelDirName);

        if (paths is not null) yield return DefaultDirectory(paths);
    }
}
