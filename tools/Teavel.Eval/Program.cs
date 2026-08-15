using System.Diagnostics;
using Teavel.Intent;
using Teavel.Tools;

// ────────────────────────────────────────────────────────────────────────────
// 라우터 측정 — **교사가 실제로 겪는 것**을 센다.
//
// 부품(낱말 라우터·언어 모델)을 따로 재면 교사 경험과 어긋난다. 실제 경로는 이렇다:
//
//   LayeredIntentRouter: 낱말 라우터 먼저 →  점수 >= 0.55 면 **모델을 아예 안 부른다**
//                        아니면 모델에 묻고, 모델 결과를 앞에 둔다
//   TeavelSession:       1순위 점수 < 0.55 이고 후보가 2개 이상이면 교사에게 고르게 하고,
//                        아니면 **그대로 실행한다**
//
// 여기서 중요한 것 — 언어 모델은 자기 답에 늘 0.8 을 매긴다. 0.55 를 넘으므로
// **모델이 고르면 교사는 고를 기회 없이 그 도구를 만난다.** 그래서 모델 모드에서
// '3순위 안에 있었다' 는 위로가 되지 않는다. 2·3순위는 화면에 뜨지 않는다.
//
// 그래서 결과를 정확도 대신 **교사가 보는 다섯 가지**로 센다.
//
// 쓰는 법:
//   dotnet run --project tools/Teavel.Eval -- keyword          (모델 없는 PC 재현)
//   dotnet run --project tools/Teavel.Eval -- model <모델.gguf>
//
// 모델 모드는 llama.cpp 네이티브가 필요하다. **교사 PC 와 같은 Windows 에서 재는 것을
// 전제로 한다** — 리눅스 개발 머신에서는 LLamaSharp 가 네이티브를 못 올리는 경우가 있다.
// ────────────────────────────────────────────────────────────────────────────

// 카탈로그의 Examples 와 겹치지 않게 지은 문장들이다. 예시를 그대로 쓰면 낱말 라우터가
// 당연히 맞히므로 측정의 뜻이 없다. 앞쪽 13개는 비교적 평이하고, 뒤쪽 11개는 도구 이름·
// 예시에 나오는 낱말을 일부러 피했다.
//
// 문장은 사람이 지은 것이라 엄밀한 평가가 아니다. **절대 점수보다 설정 사이의 차이**
// 를 보는 용도다(같은 세트를 모델 있는 경우/없는 경우에 돌려 견준다).
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
    Console.WriteLine("  keyword                모델이 없는 교사 PC 를 재현");
    Console.WriteLine("  model <모델.gguf>      모델이 있는 교사 PC 를 재현");
    Console.WriteLine("  guard <모델.gguf>      위와 같되, 모델과 낱말의 1순위가 다르면");
    Console.WriteLine("                         자동 실행 대신 교사에게 고르게 한다");
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

LocalLlmIntentRouter? llm = null;
string label;
var guard = false;

if (args[0] == "keyword") label = "모델 없음 (낱말 라우터만)";
else if (args[0] is "model" or "guard" && args.Length >= 2)
{
    if (!File.Exists(args[1])) { Console.Error.WriteLine($"모델 파일이 없습니다: {args[1]}"); return 2; }
    llm = new LocalLlmIntentRouter(args[1]);
    guard = args[0] == "guard";
    label = Path.GetFileNameWithoutExtension(args[1]) + (guard ? " + 가드" : "");
}
else { Console.Error.WriteLine("인자가 올바르지 않습니다. --help 를 보세요."); return 2; }

var keywords = new KeywordIntentRouter();

// TeavelSession 이 만드는 것과 같은 라우터. 가드 모드에서는 아래에서 직접 합성한다.
var router = new LayeredIntentRouter(keywords, llm);

// 가드: 모델이 답했는데 낱말 라우터의 1순위와 다르면, 확신 점수를 문턱 아래로 낮춰
// 교사에게 고르게 한다(모델 후보를 맨 앞에 둔 목록). 둘이 같으면 그대로 실행.
//
// 왜 이런 걸 재는가: 모델이 틀린 4건은 모두 '모델 없을 때는 목록이 떴고 거기에 정답이
// 있던' 문장이었다. 모델이 상황을 개선한 게 아니라 안전한 선택지를 없앤 셈이다.
// 다만 이 가드가 맞은 것까지 목록으로 되돌리면 이득이 사라지므로, 재 봐야 안다.
async Task<IReadOnlyList<IntentMatch>> RouteAsync(string say)
{
    if (!guard) return await router.RouteAsync(say);

    var byKeyword = await keywords.RouteAsync(say);
    if (byKeyword.Count > 0 && byKeyword[0].Score >= KeywordIntentRouter.ConfidentScore)
        return byKeyword;
    if (llm is null) return byKeyword;

    var byModel = await llm.RouteAsync(say);
    if (byModel.Count == 0) return byKeyword;

    var agree = byKeyword.Count > 0 && byKeyword[0].Tool.Id == byModel[0].Tool.Id;
    var head = agree ? byModel[0] : byModel[0] with { Score = 0.5 };   // 0.55 미만 → 고르게 함

    var merged = new List<IntentMatch> { head };
    foreach (var k in byKeyword)
        if (!merged.Any(m => m.Tool.Id == k.Tool.Id)) merged.Add(k);
    return merged;
}

Console.WriteLine($"### {label}   (문장 {CASES.Length}개)");
Console.WriteLine();

int ranRight = 0, ranWrong = 0, askedHit = 0, askedMiss = 0, lost = 0;
var byModel = 0;
var total = Stopwatch.StartNew();

foreach (var (say, want) in CASES)
{
    var t0 = total.Elapsed;
    var matches = await RouteAsync(say);
    var took = (total.Elapsed - t0).TotalSeconds;

    // ── TeavelSession.HandleUtteranceAsync 의 판단을 그대로 흉내낸다 ──
    string mark, note;
    if (matches.Count == 0)
    {
        lost++;
        mark = "못알아들음"; note = "";
    }
    else
    {
        var chosen = matches[0];
        var asks = chosen.Score < KeywordIntentRouter.ConfidentScore && matches.Count > 1;
        if (chosen.Source == IntentSource.Model) byModel++;

        if (asks)
        {
            // 교사에게 최대 5개를 보여 주고 고르게 한다.
            var shown = matches.Take(5).ToList();
            if (shown.Any(m => m.Tool.Id == want)) { askedHit++; mark = "골라야함"; note = "정답이 목록에 있음"; }
            else { askedMiss++; mark = "골라야함"; note = $"목록에 정답 없음 (1순위 {chosen.Tool.Id})"; }
        }
        else if (chosen.Tool.Id == want)
        {
            ranRight++; mark = "바로실행"; note = "맞음";
        }
        else
        {
            ranWrong++; mark = "바로실행"; note = $"※ 틀린 도구가 실행됨 → {chosen.Tool.Id}";
        }
    }

    var who = matches.Count > 0 ? (matches[0].Source == IntentSource.Model ? "모델" : "낱말") : "  ";
    Console.WriteLine($"[{mark,-6}] {who} {took,5:F1}s  {say}");
    if (note.Length > 0 && !note.Equals("맞음")) Console.WriteLine($"             {note}");
}

var n = CASES.Length;
Console.WriteLine();
Console.WriteLine($"── {label} ──");
Console.WriteLine($"  바로 실행 · 맞음      {ranRight,2}/{n}   ← 교사가 가장 원하는 것");
Console.WriteLine($"  바로 실행 · 틀림      {ranWrong,2}/{n}   ← 가장 나쁨(고를 기회 없이 엉뚱한 도구)");
Console.WriteLine($"  골라야 함 · 정답 있음 {askedHit,2}/{n}");
Console.WriteLine($"  골라야 함 · 정답 없음 {askedMiss,2}/{n}");
Console.WriteLine($"  못 알아들음           {lost,2}/{n}");
Console.WriteLine($"  (모델이 답한 문장 {byModel}/{n} · 총 {total.Elapsed.TotalSeconds:F0}초)");

llm?.Dispose();
return 0;

/// <summary>한 문장과 그 문장이 가리켜야 할 도구.</summary>
record Case(string Say, string Want);
