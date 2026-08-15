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
    /// 아직 정해지지 않은 URL 을 나타내는 값.
    /// 배포 전에 <see cref="ModelUrl"/> 을 포털이 호스팅하는 주소로 바꿔야 한다.
    /// </summary>
    public const string UnsetUrl = "";

    /// <summary>
    /// 모델 내려받을 주소. 포털 배포 파이프라인에서 핀 고정 주소로 채운다
    /// (생기부 도우미처럼 읽기 전용 SAS 를 권장).
    /// 그전까지는 환경변수 TEAVEL_GGUF_URL 로 지정해 쓸 수 있다.
    /// </summary>
    public static readonly string ModelUrl =
        Environment.GetEnvironmentVariable("TEAVEL_GGUF_URL") ?? UnsetUrl;

    /// <summary>진행률 표시와 '덜 받았는지' 판단에 쓰는 근사 크기(Qwen2.5-1.5B-Instruct Q4_K_M 기준).</summary>
    public const long ModelApproxBytes = 1_117_320_704L;

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
    public const int PickerContextSize = 1024;

    /// <summary>
    /// 인자 뽑기에 쓸 문맥 크기. 지시문이 짧아 더 작아도 된다.
    /// </summary>
    public const int FillerContextSize = 768;

    /// <summary>기본 문맥 크기(따로 지정하지 않을 때).</summary>
    public const int ContextSize = PickerContextSize;
}
