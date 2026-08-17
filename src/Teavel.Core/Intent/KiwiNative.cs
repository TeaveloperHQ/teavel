using System.Runtime.InteropServices;

namespace Teavel.Intent;

/// <summary>
/// Kiwi 형태소 분석기(C-API)를 부른다.
///
/// <para>
/// <b>생기부 도우미가 쓰던 것을 그대로 가져왔다.</b>
/// 형태소 분석기는 우리가 손볼 것이 없는 부품이라 두 벌 만들 까닭이 없다 —
/// 같은 판(v0.23.2)·같은 모델이면 결과도 같다.
/// 원본: <c>LLLM/csharp/Core/KiwiNative.cs</c>
/// </para>
/// <para>
/// <b>쓰임은 다르다.</b> 생기부는 교사가 쓴 문장의 형태소를 색으로 보여 주지만,
/// Teavel 은 <b>어간만 뽑아 도구 낱말과 견주는</b> 데 쓴다. 그래서 필요한 것은
/// <see cref="Tokenize"/> 하나뿐이고, 나머지는 옮기지 않았다.
/// </para>
/// <para>
/// 왜 필요한가 — 지금 낱말 라우터는 <b>끝 한 글자를 떼는 것</b>이 전부다.
/// 그래서 '합쳐줘' 와 '합치기' 가 같은 말인 줄 모른다. 어간을 뽑으면 둘 다 '합치' 가 된다.
/// </para>
/// </summary>
public sealed class KiwiNative : IDisposable
{
    private const string Lib = "kiwi";

    // 배포(exe 옆 kiwi.dll)와 개발(LD_LIBRARY_PATH) 둘 다 되게, exe 폴더를 먼저 본다.
    // Teavel 은 내려받은 것을 데이터 폴더에 두므로 그 자리도 함께 본다.
    private static string? _extraDirectory;

    static KiwiNative() => NativeLibrary.SetDllImportResolver(typeof(KiwiNative).Assembly, ResolveNative);

    /// <summary>네이티브를 찾을 폴더를 하나 더 알려 준다(내려받아 둔 자리).</summary>
    public static void LookAlsoIn(string directory) => _extraDirectory = directory;

    private static IntPtr ResolveNative(string name, System.Reflection.Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;

        var dirs = new List<string>();
        if (_extraDirectory is { Length: > 0 }) dirs.Add(_extraDirectory);
        dirs.Add(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory);
        dirs.Add(AppContext.BaseDirectory);

        foreach (var dir in dirs)
        foreach (var f in new[] { "kiwi.dll", "libkiwi.so", "libkiwi.dylib", "libkiwi.so.0" })
        {
            try
            {
                var p = Path.Combine(dir, f);
                if (File.Exists(p) && NativeLibrary.TryLoad(p, out var h)) return h;
            }
            catch { }
        }
        return IntPtr.Zero;   // 못 찾으면 기본 검색(PATH·LD_LIBRARY_PATH)에 맡긴다
    }

    // kiwipiepy 기본값과 맞춘다: BUILD_DEFAULT(15) | MODEL_TYPE_CONG(0x400)
    // 판마다 값이 달라질 수 있어 확인용으로 바꿔 볼 수 있게 열어 둔다.
    private static readonly int InitOptions =
        int.TryParse(Environment.GetEnvironmentVariable("TEAVEL_KIWI_INIT"), out var v) ? v : 15 | 0x0400;
    private const int MatchOptions = 63 | (1 << 23);

    [StructLayout(LayoutKind.Sequential)]
    private struct AnalyzeOption
    {
        public int match_options;
        public IntPtr blocklist;
        public int open_ending;
        public int allowed_dialects;
        public float dialect_cost;
        public IntPtr typo_transformer;
        public float typo_threshold;
    }

    [DllImport(Lib)] private static extern IntPtr kiwi_init([MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath, int numThreads, int options, int enabledDialects);
    [DllImport(Lib)] private static extern IntPtr kiwi_analyze(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int topN, AnalyzeOption option, IntPtr pretokenized);
    [DllImport(Lib)] private static extern int kiwi_res_word_num(IntPtr res, int index);
    [DllImport(Lib)] private static extern IntPtr kiwi_res_form(IntPtr res, int index, int num);
    [DllImport(Lib)] private static extern IntPtr kiwi_res_tag(IntPtr res, int index, int num);
    [DllImport(Lib)] private static extern int kiwi_res_close(IntPtr res);
    [DllImport(Lib)] private static extern int kiwi_close(IntPtr handle);
    [DllImport(Lib)] private static extern IntPtr kiwi_version();
    [DllImport(Lib)] private static extern IntPtr kiwi_error();

    private IntPtr _h;

    public static string Version() => Marshal.PtrToStringUTF8(kiwi_version()) ?? "?";

    public KiwiNative(string modelPath, int numThreads = 1)
    {
        _h = kiwi_init(modelPath, numThreads, InitOptions, 0);
        if (_h == IntPtr.Zero)
            throw new InvalidOperationException("Kiwi 를 시작하지 못했습니다: "
                + (Marshal.PtrToStringUTF8(kiwi_error()) ?? "까닭을 알 수 없습니다"));
    }

    /// <summary>글을 형태소로 나눈다. (모양, 품사) 짝들.</summary>
    public IReadOnlyList<(string Form, string Tag)> Tokenize(string text)
    {
        var opt = new AnalyzeOption
        {
            match_options = MatchOptions,
            blocklist = IntPtr.Zero,
            open_ending = 0,
            allowed_dialects = 0,
            dialect_cost = 3.0f,
            typo_transformer = IntPtr.Zero,
            typo_threshold = 2.5f,
        };

        var res = kiwi_analyze(_h, text, 1, opt, IntPtr.Zero);
        if (res == IntPtr.Zero) return Array.Empty<(string, string)>();

        try
        {
            var n = kiwi_res_word_num(res, 0);          // 가장 그럴듯한 후보 하나만
            var outp = new List<(string, string)>(n);
            for (var i = 0; i < n; i++)
            {
                outp.Add((Marshal.PtrToStringUTF8(kiwi_res_form(res, 0, i)) ?? "",
                          Marshal.PtrToStringUTF8(kiwi_res_tag(res, 0, i)) ?? ""));
            }
            return outp;
        }
        finally { kiwi_res_close(res); }
    }

    public void Dispose()
    {
        if (_h != IntPtr.Zero) { kiwi_close(_h); _h = IntPtr.Zero; }
    }
}
