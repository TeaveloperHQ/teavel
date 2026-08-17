namespace Teavel.Model;

/// <summary>
/// 언어 모델 설정. 생기부 도우미(LLLM)의 Config 와 같은 방식이다 —
/// 핀 고정한 URL 을 상수로 두고, 환경변수로 갈아끼울 수 있게 한다.
///
/// 모델 선택에 대해:
/// Teavel 이 모델에게 시키는 일은 '도구 13개 중 하나 고르기 + 인자 뽑기' 뿐이고
/// 출력이 수십 토큰이다. 생기부 문장 생성처럼 7B 가 필요한 일이 아니다.
/// 그래서 1.5B 급을 기본으로 잡는다:
///   · GPU 없는 교사 PC 에서 한 번 판단하는 데 수십 초가 아니라 몇 초가 걸린다.
///   · RAM 8GB 에서 생기부 도우미(4.7GB 모델)와 부딪히지 않는다.
///   · 내려받는 양이 1/4 이하다.
/// 더 큰 모델을 쓰고 싶으면 TEAVEL_GGUF_URL 로 바꾸면 된다.
/// </summary>
public static class TeavelModelConfig
{
    public const string AppName = "Teavel";

    /// <summary>데이터 폴더에 저장될 모델 파일 이름.</summary>
    public const string ModelFilename = "qwen2.5-1.5b-instruct-q4_k_m.gguf";

    /// <summary>
    /// 모델 내려받을 주소 — 우리 Azure Blob(SAS, 읽기전용) 핀 고정.
    /// 생기부 도우미(LLLM)의 <c>Core/Config.cs</c> 와 같은 방식이다.
    /// </summary>
    /// <remarks>
    /// **URL 은 여기 리터럴로 박혀 있어야 한다.** 이 값은 교사 PC 에서 실행 시점에
    /// 읽히므로, 빌드 파이프라인에서 환경변수를 켜 두는 것으로는 아무 일도 일어나지 않는다.
    /// 환경변수 <c>TEAVEL_GGUF_URL</c> 은 어디까지나 교사·개발자가 다른 모델로 갈아끼우는
    /// 용도다(모델을 바꾸려면 이 리터럴을 고치고 다시 배포한다).
    ///
    /// SAS 는 읽기 전용(<c>sp=r</c>)이고 배포용 파일이라 공개 소스에 있어도 무방하다 —
    /// 생기부 도우미도 같은 선택을 했다.
    /// </remarks>
    public static readonly string ModelUrl =
        Environment.GetEnvironmentVariable("TEAVEL_GGUF_URL")
        ?? "https://sgb50013120.blob.core.windows.net/dist/qwen2.5-1.5b-instruct-q4_k_m.gguf?se=2035-12-31T23%3A59%3A59Z&sp=r&spr=https&sv=2026-04-06&sr=b&sig=F%2FKM%2FwECWAb3m5hkeSUNab%2BmEglN3o2%2FPgTPKiPFXb4%3D";

    /// <summary>진행률 표시와 '덜 받았는지' 판단에 쓰는 근사 크기(Qwen2.5-1.5B-Instruct Q4_K_M 기준).</summary>
    public const long ModelApproxBytes = 1_117_320_704L;

    /// <summary>
    /// 형태소 분석기(Kiwi) — 판을 못 박는다.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 생기부 도우미가 쓰던 것을 가져왔다. 형태소 분석기는 우리가 손볼 것이 없는 부품이라
    /// 두 벌 만들 까닭이 없다.
    /// </para>
    /// <para>
    /// <b>모델과 네이티브의 판이 반드시 같아야 한다.</b> 처음에는 생기부의 모델 주소를
    /// 그대로 썼는데(0.23.0) 네이티브(0.23.2)와 짝이 안 맞아 이렇게 끝났다 —
    /// <c>Cannot open morphology file 'sj.morph'</c>. 그 모델에는 그 파일이 아예 없었다.
    /// 그래서 둘 다 만든 곳의 같은 릴리스에서 받는다.
    /// </para>
    /// <para>
    /// 언어 모델(1GB)보다 훨씬 작아서(약 84MB) 먼저 받기에 알맞다.
    /// 이것만 있어도 '합쳐줘' 와 '합치기' 를 같은 말로 알아본다.
    /// </para>
    /// </remarks>
    public const string KiwiVersion = "0.23.2";

    private const string KiwiRelease = "https://github.com/bab2min/Kiwi/releases/download/v" + KiwiVersion;

    /// <summary>형태소 모델(모든 플랫폼 공통).</summary>
    public static readonly string KiwiModelUrl =
        Environment.GetEnvironmentVariable("TEAVEL_KIWI_MODEL_URL")
        ?? $"{KiwiRelease}/kiwi_model_v{KiwiVersion}_base.tgz";

    public const long KiwiModelApproxBytes = 88_069_580L;

    /// <summary>C-API 네이티브. 플랫폼마다 다르다.</summary>
    public static string KiwiNativeUrl =>
        Environment.GetEnvironmentVariable("TEAVEL_KIWI_NATIVE_URL")
        ?? (OperatingSystem.IsWindows()
            ? $"{KiwiRelease}/kiwi_win_x64_v{KiwiVersion}.zip"
            : $"{KiwiRelease}/kiwi_lnx_x86_64_v{KiwiVersion}.tgz");

    public static long KiwiNativeApproxBytes => OperatingSystem.IsWindows() ? 36_700_160L : 12_582_912L;

    /// <summary>내려받을 주소가 정해져 있는지.</summary>
    public static bool HasDownloadUrl => !string.IsNullOrWhiteSpace(ModelUrl);

    /// <summary>추론에 쓸 스레드 수. 교사가 쓰던 작업이 멈추지 않도록 코어를 다 쓰지 않는다.</summary>
    public static int Threads => Math.Max(2, Environment.ProcessorCount - 1);

    /// <summary>
    /// 도구 고르기에 쓸 문맥 크기.
    /// </summary>
    /// <remarks>
    /// 문맥은 곧 KV 캐시고, KV 캐시는 곧 RAM 이다. 필요한 만큼만 잡는다.
    /// 실측: 도구 목록 프리픽스 730 토큰 + 교사의 말·출력 100 안팎 → 1024 면 여유가 있다.
    /// 도구를 크게 늘리면 이 값도 함께 올려야 한다(자가점검이 알려 준다).
    /// </remarks>
    // 도구가 늘면 지시문도 길어진다. 1024 로는 도구 23개에서 넘쳤다(896/1024 에서 터짐).
    // 여유를 두되 무한정 키우지 않는다 — 문맥이 크면 첫 응답이 느려진다.
    public const int PickerContextSize = 2048;

    /// <summary>
    /// 인자 뽑기에 쓸 문맥 크기. 지시문이 짧아 더 작아도 된다.
    /// </summary>
    public const int FillerContextSize = 768;

    /// <summary>
    /// 말 상대용 문맥 크기. 앞뒤 몇 마디를 기억해야 해서 인자 뽑기보다 넉넉하다.
    /// </summary>
    /// <remarks>
    /// 오래 기억할수록 좋지만 그만큼 RAM 이고, 첫 응답도 느려진다.
    /// 교사와의 대화는 몇 마디 만에 "그래서 뭘 해 드릴까요" 로 가야 하므로 길 필요가 없다.
    /// </remarks>
    public const int ChatContextSize = 1536;

    /// <summary>말 상대의 답 길이 상한(토큰). 길면 교사가 읽지 않는다.</summary>
    public const int ChatMaxTokens = 160;

    /// <summary>기본 문맥 크기(따로 지정하지 않을 때).</summary>
    public const int ContextSize = PickerContextSize;
}
