using System.Diagnostics;
using Teavel.Intent;
using Teavel.Tools;

// ────────────────────────────────────────────────────────────────────────────
// 라우터 정확도 측정 — "교사가 이렇게 말하면 이 도구가 나와야 한다" 를 세어 본다.
//
// 왜 저장소에 두는가: README 에 적힌 초기 수치(낱말 6/13, 7B 11/13)는 그때 쓴 문장이
// 남아 있지 않아 **재현할 수 없다.** 모델을 바꿀 때마다 새 문장을 지어 재면 이전 값과
// 견줄 수 없으므로, 문장 세트를 코드로 박아 둔다.
//
// 쓰는 법:
//   dotnet run --project tools/Teavel.Eval -- keyword
//   dotnet run --project tools/Teavel.Eval -- model <모델.gguf 경로>
//
// 모델 모드는 llama.cpp 네이티브가 필요하다. **교사 PC 와 같은 Windows 에서 재는 것을
// 전제로 한다** — 리눅스 개발 머신에서는 LLamaSharp 가 네이티브를 못 올리는 경우가 있다.
// ────────────────────────────────────────────────────────────────────────────

// 카탈로그의 Examples 와 겹치지 않게 지은 문장들이다. 예시를 그대로 쓰면 낱말 라우터가
// 당연히 맞히므로 측정의 뜻이 없다. 앞쪽 13개는 비교적 평이하고, 뒤쪽 11개는 도구 이름·
// 예시에 나오는 낱말을 일부러 피했다.
//
// 문장은 사람이 지은 것이라 엄밀한 평가가 아니다. **절대 점수보다 라우터 사이의 차이**
// 를 보는 용도다(같은 세트를 여러 라우터에 돌려 견준다).
var CASES = new Case[]
{
    new("반별로 흩어진 성적 파일을 한 장으로 모아줘",           "excel.merge_workbooks"),
    new("이 표에 어떤 항목들이 적혀 있는지 훑어봐줘",           "excel.list_sheets"),
    new("학급 기준으로 표를 갈라서 따로 저장해줘",              "excel.split_by_column"),
    new("우리 반 점수 평균이랑 최고점 좀 뽑아줘",               "excel.summarize"),
    new("이 표를 쉼표로 구분된 형식으로 저장해줘",              "excel.convert"),
    new("사진 파일들 앞에 붙은 날짜 지워줘",                    "files.rename_batch"),
    new("제출한 애들 학번 기준으로 나눠서 담아줘",              "files.organize_by_id"),
    new("제출 안 한 사람 명단 좀 만들어줘",                     "files.find_missing"),
    new("zip 파일들 한꺼번에 열어줘",                           "files.extract_archives"),
    new("학부모님께 개인별로 다른 내용 메일 보내게 준비해줘",   "outlook.draft_bulk"),
    new("메일에 온 첨부들 한 폴더에 내려받아줘",                "outlook.save_attachments"),
    new("명단 가지고 상장 하나씩 찍어줘",                       "word.mail_merge"),
    new("이 문서들 전부 PDF로 뽑아줘",                          "word.to_pdf"),

    // ── 더 어려운 쪽 ──
    new("애들이 낸 거 이름이 제각각인데 좀 통일해줘",           "files.rename_batch"),
    new("받은 것들 다 풀어놔줘",                                "files.extract_archives"),
    new("우리 반 애들 한 명 한 명한테 다른 글 써서 보내야 해",  "outlook.draft_bulk"),
    new("표 안에 뭐가 들었나 궁금해",                           "excel.list_sheets"),
    new("몇 점대가 많은지 궁금해",                              "excel.summarize"),
    new("상장을 애들 이름 넣어서 여러 장 만들어야 해",          "word.mail_merge"),
    new("학교 계정 어떻게 넣어요",                              "setup.account_guide"),
    new("내 컴퓨터 윈도우 뭐 쓰는지 알려줘",                    "setup.windows_info"),
    new("3층 복도 걸로 인쇄되게 해줘",                          "printer.set_default"),
    new("반마다 따로 파일이 되게 쪼개줘",                       "excel.split_by_column"),
    new("첨부로 온 것들 좀 챙겨줘",                             "outlook.save_attachments"),
};

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("사용법:");
    Console.WriteLine("  keyword                낱말 라우터만 (모델 없이)");
    Console.WriteLine("  model <모델.gguf>      로컬 언어 모델");
    return 2;
}

// 선언한 도구 id 가 실제로 카탈로그에 있는지 먼저 본다 — 도구 id 를 바꾸고 이 파일을
// 안 고치면 '영원히 틀리는 문장' 이 생기는데, 그건 모델 탓으로 오해되기 쉽다.
var known = ToolCatalog.All.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
var stale = CASES.Select(c => c.Want).Distinct().Where(w => !known.Contains(w)).ToList();
if (stale.Count > 0)
{
    Console.Error.WriteLine($"카탈로그에 없는 도구 id 가 문장에 들어 있습니다: {string.Join(", ", stale)}");
    return 2;
}

IIntentRouter router;
IDisposable? owned = null;
string label;

if (args[0] == "keyword")
{
    router = new KeywordIntentRouter();
    label = "낱말 라우터만";
}
else if (args[0] == "model" && args.Length >= 2)
{
    if (!File.Exists(args[1])) { Console.Error.WriteLine($"모델 파일이 없습니다: {args[1]}"); return 2; }
    var llm = new LocalLlmIntentRouter(args[1]);
    router = llm;
    owned = llm;
    label = Path.GetFileNameWithoutExtension(args[1]);
}
else { Console.Error.WriteLine("인자가 올바르지 않습니다. --help 를 보세요."); return 2; }

Console.WriteLine($"### {label}   (문장 {CASES.Length}개)");
Console.WriteLine();

int top1 = 0, top3 = 0;
var total = Stopwatch.StartNew();

foreach (var (say, want) in CASES)
{
    var t0 = total.Elapsed;
    var matches = await router.RouteAsync(say);
    var took = (total.Elapsed - t0).TotalSeconds;

    var got = matches.Count > 0 ? matches[0].Tool.Id : "(없음)";
    var hit1 = got == want;
    var hit3 = matches.Take(3).Any(m => m.Tool.Id == want);
    if (hit1) top1++;
    if (hit3) top3++;

    // O = 1순위 정답, ~ = 3순위 안에는 있음, X = 놓침
    Console.WriteLine($"{(hit1 ? "O" : hit3 ? "~" : "X")} {took,5:F1}s  {say}");
    if (!hit1) Console.WriteLine($"          원함 {want} / 받음 {got}");
}

Console.WriteLine();
Console.WriteLine($"{label}: 1순위 {top1}/{CASES.Length} · 3순위 안 {top3}/{CASES.Length} · 총 {total.Elapsed.TotalSeconds:F0}초");

owned?.Dispose();
return 0;

/// <summary>한 문장과 그 문장이 가리켜야 할 도구.</summary>
record Case(string Say, string Want);
