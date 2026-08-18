namespace Teavel.Tools;

/// <summary>
/// Teavel 이 할 줄 아는 일 전부. 여기 없는 일은 하지 않는다 — 그게 이 설계의 요점이다.
///
/// 로컬 모델은 코드를 짜지 않고 이 목록에서 <b>하나를 고르고 인자만 채운다</b>.
/// 실제 동작은 scripts/&lt;Module&gt;.psm1 안의, 사람이 검증해 둔 PowerShell 함수가 한다.
/// 도구를 늘리려면 여기 선언 하나 + PowerShell 함수 하나를 짝으로 추가하면 된다.
///
/// 인자 <see cref="ToolParam.Name"/> 은 PowerShell 함수의 매개변수 이름과 반드시 같아야 한다
/// (어긋나면 `teavel 자가점검` 이 잡아낸다).
/// </summary>
public static class ToolCatalog
{
    /// <summary>선언된 모든 도구.</summary>
    public static IReadOnlyList<ToolSpec> All { get; } = Build();

    /// <summary>id 로 도구를 찾는다. 없으면 null.</summary>
    public static ToolSpec? Find(string id)
        => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>영역별로 묶어서 돌려준다(메뉴 모드용).</summary>
    public static IEnumerable<IGrouping<ToolCategory, ToolSpec>> ByCategory()
        => All.GroupBy(t => t.Category);

    // 여러 도구가 공유하는 인자 — 엑셀 표를 읽는 방식은 어디서나 같다.
    private static ToolParam Sheet() => new(
        "Sheet", ToolParamKind.Number, "시트 번호",
        "몇 번째 시트를 읽을지. 보통 첫 번째 시트입니다.", Required: false, Default: "1");

    private static ToolParam HeaderRow() => new(
        "HeaderRow", ToolParamKind.Number, "머리글 행",
        "열 이름(학번·이름 등)이 적힌 행 번호. 보통 1행입니다.", Required: false, Default: "1");

    private static List<ToolSpec> Build() => new()
    {
        // ─────────────────────────── 엑셀 ───────────────────────────

        // ── Teavel 자신 (도구가 아니라 흐름) ──
        //
        // 이것들은 '설치' · '모델' 이라고 정확히 쳐야만 닿았다. 실기에서 이렇게 막혔다.
        //
        //   > 실행파일 찾아서 등록해줘   → 엉뚱한 도구 다섯 개를 늘어놓았다
        //   > 언어모델 다운로드 하자     → 무슨 일인지 알아듣지 못했습니다
        //
        // 정확한 낱말을 알아야만 닿는다면, 그 낱말을 모르는 사람에게는 없는 기능이다.
        // 학교에서 컴퓨터를 처음 세팅할 때 가장 먼저 할 일이다. 선생님이 이 일을 부를 때
        // 쓰는 말이 워낙 여러 가지라(계정 연결·로그인·원드라이브 설정·메일 설정…)
        // 유의어를 넉넉히 적어 둔다 — 여기 닿지 못하면 세팅이 시작조차 되지 않는다.
        new ToolSpec(
            Id: "setup.connect_accounts",
            Title: "학교 계정을 이 컴퓨터에 연결하기",
            Category: ToolCategory.Setup,
            Description: "학교에서 받은 계정을 Windows 에 잇고, Edge·원드라이브·오피스·아웃룩·팀즈가 "
                       + "그 계정을 쓰도록 차례로 안내합니다. 원드라이브는 무엇이 동기화되고 있는지 "
                       + "보여 주고 폴더를 고를 수 있게 합니다.",
            Examples: new[]
            {
                "학교 계정 연결해줘",
                "원드라이브 설정 좀 해줘",
                "엣지에 학교 계정 넣어줘",
                "처음 세팅하는데 뭐부터 해야 돼",
                "오피스 로그인 어떻게 해",
            },
            Parameters: Array.Empty<ToolParam>(),
            Module: "@flow",
            Function: "accounts",
            Mutating: false)
        {
            Aliases = new[]
            {
                "계정 연결", "계정", "로그인", "학교 계정", "계정 잇기", "처음 세팅", "초기 세팅",
                "원드라이브", "onedrive", "동기화", "백업 폴더", "엣지", "edge", "브라우저",
                "오피스 로그인", "메일 설정", "아웃룩 설정",
            },
        },

        new ToolSpec(
            Id: "teavel.install",
            Title: "어디서나 teavel 로 실행되게 등록하기",
            Category: ToolCategory.Setup,
            Description: "아무 폴더에서나 teavel 만 쳐도 되게 등록하고, 폴더 우클릭 메뉴에 넣습니다. "
                       + "관리자 권한이 필요 없고 되돌릴 수 있습니다.",
            Examples: new[]
            {
                "실행파일 찾아서 등록해줘",
                "어디서나 쓸 수 있게 해줘",
                "teavel 등록해줘",
                "우클릭 메뉴에 넣어줘",
            },
            Parameters: Array.Empty<ToolParam>(),
            Module: "@flow",
            Function: "install",
            Mutating: true)
        {
            Aliases = new[] { "설치", "등록", "path 등록", "우클릭 메뉴", "바로 실행" },
        },

        new ToolSpec(
            Id: "teavel.uninstall",
            Title: "등록 풀기",
            Category: ToolCategory.Setup,
            Description: "어디서나 실행되게 해 둔 등록과 우클릭 메뉴를 풉니다. "
                       + "프로그램 파일은 지우지 않습니다.",
            Examples: new[]
            {
                "등록 풀어줘",
                "우클릭 메뉴 없애줘",
            },
            Parameters: Array.Empty<ToolParam>(),
            Module: "@flow",
            Function: "uninstall",
            Mutating: true)
        {
            // '제거' 는 여기 두지 않는다 — 그 말로 부르면 아래 teavel.remove(정말 지우기)로 가야 한다.
            // 같은 말이 두 곳에 있으면 말로 부를 때와 명령으로 칠 때가 서로 다른 일을 한다.
            Aliases = new[] { "등록 해제", "등록해제", "되돌리기", "path 에서 빼기" },
        },

        new ToolSpec(
            Id: "teavel.remove",
            Title: "Teavel 지우기",
            Category: ToolCategory.Setup,
            Description: "Teavel 을 이 컴퓨터에서 지웁니다 — 등록·설정·프로그램 파일까지. "
                       + "내려받아 둔 언어 모델과 형태소 분석기는 지울지 따로 물어봅니다.",
            Examples: new[]
            {
                "teavel 지워줘",
                "이거 삭제하고 싶어",
                "프로그램 없애줘",
            },
            Parameters: Array.Empty<ToolParam>(),
            Module: "@flow",
            Function: "remove",
            Mutating: true)
        {
            Aliases = new[] { "삭제", "제거", "지우기", "완전 삭제", "없애기" },
        },

        new ToolSpec(
            Id: "teavel.model",
            Title: "말을 알아듣는 언어 모델 내려받기",
            Category: ToolCategory.Setup,
            Description: "한 번만 받으면 말투가 달라도 알아듣습니다. 1GB 쯤 되고 몇 분 걸립니다. "
                       + "받은 뒤에는 인터넷 없이 동작합니다.",
            Examples: new[]
            {
                "언어모델 다운로드 하자",
                "말 잘 알아듣게 해줘",
                "모델 받아줘",
                "인공지능 켜줘",
            },
            Parameters: Array.Empty<ToolParam>(),
            Module: "@flow",
            Function: "model",
            Mutating: true)
        {
            Aliases = new[] { "모델", "언어 모델", "llm", "다운로드", "내려받기" },
        },

        // ── 말 걸기 ──
        //
        // 도구를 부르는 말이 아닐 때 갈 곳이다.
        //
        // 이것이 없을 때 모델은 <b>반드시 도구 하나를 골라야만 했다.</b> 그래서
        // "안녕 대화좀 해보자" 에도 도구 목록이 떴고, 교사는 고를 것이 없어 취소하고,
        // 다시 말을 걸면 또 같은 목록을 봤다. 말을 알아들으라고 1GB 를 받게 해 놓고
        // 정작 말을 안 받아 준 셈이다.
        //
        // 도구로 등록해 두면 라우터·목록·인자 검증이 전부 그대로 쓰인다.
        new ToolSpec(
            Id: "teavel.chat",
            Title: "그냥 말 걸기",
            Category: ToolCategory.Setup,
            Description: "도구를 부르는 말이 아니라 인사·질문·잡담일 때 이것을 고릅니다. "
                       + "무엇을 해야 할지 모르겠다는 말도 여기입니다.",
            Examples: new[]
            {
                "안녕",
                "대화 좀 해보자",
                "너 뭐 할 줄 알아?",
                "뭘 해야 할지 모르겠어",
                "고마워",
            },
            Parameters: Array.Empty<ToolParam>(),
            Module: "@flow",
            Function: "chat",
            Mutating: false)
        {
            Aliases = new[] { "대화", "인사", "잡담", "이야기", "물어보기", "도와줘" },
        },

        // ── 학교 전체 (도구가 아니라 흐름) ──
        //
        // Module 이 "@flow" 인 것은 PowerShell 함수가 아니라 CLI 의 한 판을 가리킨다.
        // 자가점검은 이것들을 건너뛴다(대조할 .psm1 이 없다).
        new ToolSpec(
            Id: "school.compose",
            Title: "학교 그룹·Teams 구성하기",
            Category: ToolCategory.School,
            Description: "학교에 지금 있는 그룹과 팀을 살펴보고, 정리하고, 모자란 반 팀을 만들고, "
                       + "명단으로 학생을 반에 넣습니다. 전역 관리자만 할 수 있습니다.",
            Examples: new[]
            {
                "반 팀 만들어줘",
                "올해 학급 팀 구성해야 해",
                "학생들 반에 넣어줘",
                "teams 구성하고 싶어",
                "학교 그룹 정리해줘",
            },
            Parameters: Array.Empty<ToolParam>(),
            Module: "@flow",
            Function: "m365",
            Mutating: true)
        {
            Aliases = new[] { "학급 팀", "수업 팀", "반 만들기", "구성원 배정", "m365 그룹", "팀 구성" },
        },

        new ToolSpec(
            Id: "school.archive",
            Title: "지난 학년도 팀 보관하기",
            Category: ToolCategory.School,
            Description: "작년에 쓰던 팀을 이름 앞에 연도를 붙여 보관하고 학생들을 내보냅니다. "
                       + "팀과 그 안의 파일·대화는 그대로 남아, 나중에 찾아볼 수 있습니다.",
            Examples: new[]
            {
                "작년 팀 백업해줘",
                "지난 학년도 팀 정리하고 싶어",
                "묵은 팀에서 학생들 빼줘",
                "재작년 반 보관해줘",
            },
            Parameters: Array.Empty<ToolParam>(),
            Module: "@flow",
            Function: "m365.archive",
            Mutating: true)
        {
            Aliases = new[] { "팀 백업", "팀 보관", "학년도 정리", "지난해 팀", "학생 내보내기" },
        },

        new ToolSpec(
            Id: "school.find_teacher",
            Title: "선생님 계정 찾기",
            Category: ToolCategory.School,
            Description: "이름으로 선생님의 학교 계정(로그인 아이디)을 찾아 줍니다. "
                       + "학생 계정은 걸러 냅니다.",
            Examples: new[]
            {
                "김하늘 선생님 계정 뭐야",
                "이 선생님 아이디 찾아줘",
                "선생님 계정 검색",
            },
            Parameters: new[]
            {
                new ToolParam("Name", ToolParamKind.Text, "성함", "찾으실 선생님 성함. 성만 넣으셔도 됩니다."),
            },
            Module: "@flow",
            Function: "m365.teacher",
            Mutating: false)
        {
            Aliases = new[] { "선생님 아이디", "교사 계정", "계정 찾기" },
        },

        new ToolSpec(
            Id: "excel.list_sheets",
            Title: "엑셀 파일 구조 살펴보기",
            Category: ToolCategory.Excel,
            Description: "엑셀 파일에 어떤 시트가 있고 열 이름이 무엇인지, 몇 행인지 알려줍니다. "
                       + "다른 엑셀 작업을 하기 전에 열 이름을 확인할 때 먼저 씁니다.",
            Examples: new[]
            {
                "이 엑셀 파일에 뭐가 들어있는지 보여줘",
                "성적표 열 이름이 뭐야",
                "시트 목록 알려줘",
            },
            Parameters: new[]
            {
                new ToolParam("File", ToolParamKind.FilePath, "엑셀 파일", "살펴볼 엑셀 파일의 경로."),
            },
            Module: "Teavel.Excel", Function: "Get-WorkbookInfo", Mutating: false, TimeoutSeconds: 120)
            { Aliases = new[] { "살펴보기", "확인", "구조", "열 이름", "항목", "미리보기", "뭐가 있는지", "들어있는지" } },

        new ToolSpec(
            Id: "excel.merge_workbooks",
            Title: "엑셀 파일 여러 개를 하나로 합치기",
            Category: ToolCategory.Excel,
            Description: "한 폴더 안의 엑셀 파일들을 위아래로 이어붙여 하나의 파일로 만듭니다. "
                       + "열 구성이 같은 파일들(반별 성적표, 분담해 받은 명단 등)에 씁니다. "
                       + "원본 파일은 건드리지 않습니다.",
            Examples: new[]
            {
                "2반 수행평가 엑셀 다 합쳐줘",
                "이 폴더 엑셀 파일들 하나로 만들어줘",
                "반별 성적표 하나로 모아줘",
            },
            Parameters: new[]
            {
                new ToolParam("Folder", ToolParamKind.FolderPath, "폴더", "합칠 엑셀 파일들이 들어 있는 폴더."),
                new ToolParam("Output", ToolParamKind.OutputPath, "저장할 파일",
                    "합친 결과를 저장할 엑셀 파일 경로."),
                new ToolParam("Pattern", ToolParamKind.Text, "파일 이름 조건",
                    "합칠 파일 이름 패턴. 예: *.xlsx, 2반*.xlsx", Required: false, Default: "*.xlsx"),
                Sheet(),
                HeaderRow(),
            },
            Module: "Teavel.Excel", Function: "Merge-Workbook", Mutating: true)
            { Aliases = new[] { "합치기", "묶기", "모으기", "이어붙이기", "통합", "하나로", "한 장으로", "한 파일로", "취합" } },

        new ToolSpec(
            Id: "excel.split_by_column",
            Title: "엑셀을 특정 열 값으로 나누기",
            Category: ToolCategory.Excel,
            Description: "표를 한 열의 값(반, 학년, 동아리 등)에 따라 여러 파일로 나눕니다. "
                       + "전교생 명단을 반별 파일로 쪼갤 때 씁니다. 원본은 그대로 둡니다.",
            Examples: new[]
            {
                "전교생 명단을 반별로 나눠줘",
                "학년별로 파일 쪼개줘",
                "동아리별 엑셀로 분리해줘",
            },
            Parameters: new[]
            {
                new ToolParam("File", ToolParamKind.FilePath, "엑셀 파일", "나눌 원본 엑셀 파일."),
                new ToolParam("Column", ToolParamKind.Text, "기준 열 이름",
                    "이 열의 값이 같은 행끼리 한 파일로 묶입니다. 예: 반, 학년"),
                new ToolParam("OutputFolder", ToolParamKind.OutputPath, "저장할 폴더",
                    "나눈 파일들을 넣을 폴더. 없으면 만듭니다."),
                Sheet(),
                HeaderRow(),
            },
            Module: "Teavel.Excel", Function: "Split-WorkbookByColumn", Mutating: true)
            { Aliases = new[] { "나누기", "쪼개기", "분리", "분할", "따로", "단위로", "별로 만들기" } },

        new ToolSpec(
            Id: "excel.summarize",
            Title: "점수 통계 내기",
            Category: ToolCategory.Excel,
            Description: "점수 열의 인원·평균·표준편차·최고·최저·중앙값을 계산합니다. "
                       + "반 등으로 묶어서 비교할 수도 있습니다. 파일을 바꾸지 않고 결과만 보여줍니다.",
            Examples: new[]
            {
                "이 성적표 평균 내줘",
                "반별 평균이랑 최고점 알려줘",
                "중간고사 점수 통계 보여줘",
            },
            Parameters: new[]
            {
                new ToolParam("File", ToolParamKind.FilePath, "엑셀 파일", "점수가 들어 있는 엑셀 파일."),
                new ToolParam("ScoreColumn", ToolParamKind.Text, "점수 열 이름",
                    "통계를 낼 숫자 열의 이름. 예: 총점, 중간고사"),
                new ToolParam("GroupColumn", ToolParamKind.Text, "묶을 열 이름",
                    "이 열의 값별로 나눠서 통계를 냅니다. 예: 반. 필요 없으면 비워 둡니다.",
                    Required: false),
                Sheet(),
                HeaderRow(),
            },
            Module: "Teavel.Excel", Function: "Get-ScoreSummary", Mutating: false)
            { Aliases = new[] { "평균", "통계", "최고점", "최저점", "석차", "표준편차", "몇 점", "분포", "집계" } },

        new ToolSpec(
            Id: "excel.convert",
            Title: "엑셀 파일 형식 바꾸기",
            Category: ToolCategory.Excel,
            Description: "엑셀 파일을 CSV·XLSX·PDF 로 바꿉니다. "
                       + "나이스(NEIS)에 올릴 CSV 를 만들거나, 표를 PDF 로 뽑을 때 씁니다.",
            Examples: new[]
            {
                "이 엑셀 csv로 바꿔줘",
                "성적표 PDF로 뽑아줘",
                "csv 파일을 엑셀로 만들어줘",
            },
            Parameters: new[]
            {
                new ToolParam("File", ToolParamKind.FilePath, "원본 파일", "바꿀 엑셀 또는 CSV 파일."),
                new ToolParam("To", ToolParamKind.Choice, "바꿀 형식", "어떤 형식으로 바꿀지.",
                    Choices: new[]
                    {
                        new ToolChoice("csv", "CSV — 나이스에 올릴 쉼표 구분 파일", "csv", "쉼표", "나이스", "neis"),
                        new ToolChoice("xlsx", "엑셀 (xlsx)", "엑셀", "xlsx", "excel"),
                        new ToolChoice("pdf", "PDF", "pdf", "피디에프"),
                    }),
                new ToolParam("Output", ToolParamKind.OutputPath, "저장할 파일",
                    "결과 파일 경로. 비우면 원본 옆에 같은 이름으로 만듭니다.", Required: false),
            },
            Module: "Teavel.Excel", Function: "Convert-Workbook", Mutating: true)
            { Aliases = new[] { "변환", "바꾸기", "csv", "쉼표 구분", "pdf", "내보내기", "저장 형식", "나이스" } },

        // ────────────────────────── 파일 정리 ──────────────────────────

        new ToolSpec(
            Id: "files.rename_batch",
            Title: "파일 이름 일괄 바꾸기",
            Category: ToolCategory.Files,
            Description: "폴더 안 파일 이름에서 특정 글자를 찾아 다른 글자로 바꿉니다. "
                       + "학생들이 제각각 붙여 놓은 제출물 이름을 정리할 때 씁니다.",
            Examples: new[]
            {
                "파일 이름에서 '최종' 빼줘",
                "제출물 이름 앞에 붙은 공백 정리해줘",
                "'과제1'을 '수행평가1'로 바꿔줘",
            },
            Parameters: new[]
            {
                new ToolParam("Folder", ToolParamKind.FolderPath, "폴더", "이름을 바꿀 파일들이 있는 폴더."),
                new ToolParam("Find", ToolParamKind.Text, "바꿀 글자", "파일 이름에서 찾을 글자."),
                new ToolParam("ReplaceWith", ToolParamKind.Text, "바뀔 글자",
                    "찾은 글자를 무엇으로 바꿀지. 지울 거면 비워 둡니다.", Required: false, Default: ""),
                new ToolParam("Pattern", ToolParamKind.Text, "파일 이름 조건",
                    "대상 파일 패턴. 예: *.hwp", Required: false, Default: "*"),
                new ToolParam("Recurse", ToolParamKind.Bool, "하위 폴더까지",
                    "하위 폴더 안의 파일도 바꿀지.", Required: false, Default: "false"),
            },
            Module: "Teavel.Files", Function: "Rename-FileBatch", Mutating: true, TimeoutSeconds: 300)
            { Aliases = new[] { "이름 바꾸기", "떼기", "빼기", "지우기", "붙은 것", "일괄 변경", "제목 정리" } },

        new ToolSpec(
            Id: "files.organize_by_id",
            Title: "학번별 폴더로 분류하기",
            Category: ToolCategory.Files,
            Description: "파일 이름에서 학번을 찾아, 학번마다 폴더를 만들어 파일을 옮겨 넣습니다. "
                       + "한 폴더에 뒤섞여 들어온 제출물을 학생별로 정리할 때 씁니다.",
            Examples: new[]
            {
                "제출물 학번별 폴더로 정리해줘",
                "학번마다 폴더 만들어서 넣어줘",
            },
            Parameters: new[]
            {
                new ToolParam("Folder", ToolParamKind.FolderPath, "폴더", "정리할 파일들이 있는 폴더."),
                new ToolParam("IdPattern", ToolParamKind.Text, "학번 형태",
                    "학번을 찾을 정규식. 기본은 연속된 숫자 5자리입니다.",
                    Required: false, Default: @"\d{5}"),
                new ToolParam("Copy", ToolParamKind.Bool, "옮기지 않고 복사",
                    "원본을 남기고 복사할지. 기본은 옮깁니다.", Required: false, Default: "false"),
            },
            Module: "Teavel.Files", Function: "Group-FileByStudentId", Mutating: true, TimeoutSeconds: 300)
            { Aliases = new[] { "학번", "분류", "학생별", "개인별", "담기", "폴더로 나누기", "정리" } },

        new ToolSpec(
            Id: "files.find_missing",
            Title: "미제출자 찾기",
            Category: ToolCategory.Files,
            Description: "명단 엑셀과 제출물 폴더를 맞춰 보고, 파일을 내지 않은 학생을 알려줍니다. "
                       + "파일을 전혀 건드리지 않습니다.",
            Examples: new[]
            {
                "누가 안 냈는지 알려줘",
                "미제출자 확인해줘",
                "명단이랑 대조해서 빠진 사람 찾아줘",
            },
            Parameters: new[]
            {
                new ToolParam("Folder", ToolParamKind.FolderPath, "제출물 폴더", "학생들이 낸 파일이 있는 폴더."),
                new ToolParam("RosterFile", ToolParamKind.FilePath, "명단 엑셀", "전체 학생 명단 엑셀 파일."),
                new ToolParam("IdColumn", ToolParamKind.Text, "학번 열 이름",
                    "명단에서 학번이 적힌 열 이름. 예: 학번"),
                new ToolParam("NameColumn", ToolParamKind.Text, "이름 열 이름",
                    "미제출자를 이름으로도 보여줍니다. 없으면 비워 둡니다.", Required: false),
                Sheet(),
                HeaderRow(),
            },
            Module: "Teavel.Files", Function: "Find-MissingSubmission", Mutating: false)
            { Aliases = new[] { "미제출", "안 낸", "안 냈는지", "빠진", "누락", "대조", "명단 확인", "누가" } },

        new ToolSpec(
            Id: "files.extract_archives",
            Title: "압축 파일 일괄 풀기",
            Category: ToolCategory.Files,
            Description: "폴더 안의 zip 파일을 모두 풀어 각각 같은 이름의 폴더에 넣습니다.",
            Examples: new[]
            {
                "zip 파일들 다 풀어줘",
                "압축 일괄 해제해줘",
            },
            Parameters: new[]
            {
                new ToolParam("Folder", ToolParamKind.FolderPath, "폴더", "압축 파일들이 있는 폴더."),
                new ToolParam("OutputFolder", ToolParamKind.OutputPath, "풀어 놓을 폴더",
                    "비우면 압축 파일이 있는 자리에 풉니다.", Required: false),
                new ToolParam("DeleteAfter", ToolParamKind.Bool, "풀고 나서 원본 삭제",
                    "압축 파일을 지울지. 기본은 남겨 둡니다.", Required: false, Default: "false"),
            },
            Module: "Teavel.Files", Function: "Expand-ArchiveBatch", Mutating: true, TimeoutSeconds: 600)
            { Aliases = new[] { "압축", "풀기", "zip", "해제", "열기" } },

        // ─────────────────────────── 아웃룩 ───────────────────────────

        new ToolSpec(
            Id: "outlook.draft_bulk",
            Title: "명단으로 개인별 메일 만들기",
            Category: ToolCategory.Outlook,
            Description: "명단 엑셀의 각 행마다 메일을 하나씩 만듭니다. 제목·본문에 {이름} 처럼 "
                       + "열 이름을 중괄호로 넣으면 학생마다 그 값으로 바뀝니다. "
                       + "기본은 '보내지 않고 임시 보관함에만' 만듭니다.",
            Examples: new[]
            {
                "학부모님께 개인별로 메일 만들어줘",
                "명단 보고 각자한테 성적 안내 메일 초안 만들어줘",
            },
            Parameters: new[]
            {
                new ToolParam("RosterFile", ToolParamKind.FilePath, "명단 엑셀", "받는 사람 주소가 들어 있는 엑셀."),
                new ToolParam("ToColumn", ToolParamKind.Text, "메일 주소 열 이름",
                    "받는 사람 주소가 적힌 열 이름. 예: 학부모메일"),
                new ToolParam("Subject", ToolParamKind.Text, "제목",
                    "메일 제목. {이름} 처럼 열 이름을 넣으면 학생마다 바뀝니다."),
                new ToolParam("BodyTemplate", ToolParamKind.Text, "본문",
                    "메일 본문. {이름}, {총점} 처럼 열 이름을 넣을 수 있습니다."),
                new ToolParam("AttachmentColumn", ToolParamKind.Text, "첨부 파일 열 이름",
                    "학생별 첨부 파일 경로가 적힌 열. 없으면 비워 둡니다.", Required: false),
                new ToolParam("Send", ToolParamKind.Bool, "바로 보내기",
                    "예로 하면 곧바로 발송합니다. 기본은 임시 보관함에만 만듭니다.",
                    Required: false, Default: "false"),
                Sheet(),
                HeaderRow(),
            },
            Module: "Teavel.Outlook", Function: "New-BulkMailDraft", Mutating: true)
            { Aliases = new[] { "메일", "보내기", "발송", "개인별", "학부모", "안내 메일", "초안", "각자에게" } },

        new ToolSpec(
            Id: "outlook.save_attachments",
            Title: "받은 메일 첨부 파일 모아 저장하기",
            Category: ToolCategory.Outlook,
            Description: "최근 받은 메일의 첨부 파일을 한 폴더에 모아 저장합니다. "
                       + "파일 이름 앞에 보낸 사람을 붙여 누가 낸 건지 알 수 있게 합니다.",
            Examples: new[]
            {
                "메일로 온 과제 첨부파일 다 저장해줘",
                "지난주 받은 첨부 모아줘",
            },
            Parameters: new[]
            {
                new ToolParam("OutputFolder", ToolParamKind.OutputPath, "저장할 폴더", "첨부 파일을 모을 폴더."),
                new ToolParam("Days", ToolParamKind.Number, "며칠 치",
                    "최근 며칠 안에 받은 메일을 볼지.", Required: false, Default: "7"),
                new ToolParam("SubjectContains", ToolParamKind.Text, "제목에 들어간 말",
                    "제목에 이 말이 들어간 메일만 봅니다. 비우면 전부.", Required: false),
                new ToolParam("SenderContains", ToolParamKind.Text, "보낸 사람에 들어간 말",
                    "보낸 사람 이름·주소에 이 말이 들어간 메일만 봅니다. 비우면 전부.", Required: false),
            },
            Module: "Teavel.Outlook", Function: "Save-MailAttachment", Mutating: true)
            { Aliases = new[] { "첨부", "받은 메일", "내려받기", "수신함", "받은편지함", "모아 저장" } },

        // ──────────────────────────── 워드 ────────────────────────────

        new ToolSpec(
            Id: "word.mail_merge",
            Title: "명단으로 문서 일괄 만들기",
            Category: ToolCategory.Word,
            Description: "워드 서식 파일의 {이름} 같은 자리를 명단 엑셀의 값으로 바꿔, "
                       + "학생 수만큼 문서를 만듭니다. 상장·가정통신문·개인별 안내문에 씁니다.",
            Examples: new[]
            {
                "명단으로 상장 다 만들어줘",
                "가정통신문 학생별로 뽑아줘",
                "이 서식에 명단 넣어서 문서 만들어줘",
            },
            Parameters: new[]
            {
                new ToolParam("TemplateFile", ToolParamKind.FilePath, "서식 워드 파일",
                    "{이름} 처럼 바뀔 자리가 들어 있는 워드 파일."),
                new ToolParam("RosterFile", ToolParamKind.FilePath, "명단 엑셀", "채워 넣을 값이 든 엑셀."),
                new ToolParam("OutputFolder", ToolParamKind.OutputPath, "저장할 폴더",
                    "만들어진 문서를 넣을 폴더. 없으면 만듭니다."),
                new ToolParam("NameColumn", ToolParamKind.Text, "파일 이름에 쓸 열",
                    "만들어지는 파일 이름에 쓸 열. 예: 이름", Required: false, Default: "이름"),
                new ToolParam("Format", ToolParamKind.Choice, "저장 형식", "어떤 형식으로 저장할지.",
                    Required: false, Default: "docx",
                    Choices: new[]
                    {
                        new ToolChoice("docx", "워드 문서 (docx) — 나중에 고칠 수 있습니다", "워드", "docx", "doc", "고칠"),
                        new ToolChoice("pdf", "PDF — 그대로 인쇄하거나 보낼 때", "pdf", "피디에프", "인쇄"),
                    }),
                Sheet(),
                HeaderRow(),
            },
            Module: "Teavel.Word", Function: "New-MergedDocument", Mutating: true, TimeoutSeconds: 900)
            { Aliases = new[] { "상장", "표창장", "가정통신문", "안내문", "서식", "채우기", "학생별 문서", "이름 넣어" } },

        new ToolSpec(
            Id: "word.to_pdf",
            Title: "문서 일괄 PDF 변환",
            Category: ToolCategory.Word,
            Description: "폴더 안의 워드 문서를 모두 PDF 로 바꿉니다. 원본은 그대로 둡니다.",
            Examples: new[]
            {
                "이 폴더 워드 파일들 다 PDF로 바꿔줘",
                "문서 전부 pdf로 변환해줘",
            },
            Parameters: new[]
            {
                new ToolParam("Folder", ToolParamKind.FolderPath, "폴더", "변환할 문서가 있는 폴더."),
                new ToolParam("OutputFolder", ToolParamKind.OutputPath, "저장할 폴더",
                    "비우면 원본 옆에 만듭니다.", Required: false),
                new ToolParam("Pattern", ToolParamKind.Text, "파일 이름 조건",
                    "대상 파일 패턴.", Required: false, Default: "*.doc*"),
                new ToolParam("Recurse", ToolParamKind.Bool, "하위 폴더까지",
                    "하위 폴더 문서도 변환할지.", Required: false, Default: "false"),
            },
            Module: "Teavel.Word", Function: "Convert-DocumentToPdf", Mutating: true, TimeoutSeconds: 900)
            { Aliases = new[] { "pdf", "변환", "바꾸기", "인쇄용" } },

        // ────────────────────── 계정 — 설명하고 답하기 ──────────────────────
        // 세팅에서 가장 많이 막히는 자리이고, '해 주는' 것보다 '설명하는' 일이 큰 자리다.
        // 안내문은 사람이 쓴 것을 그대로 읽어 준다 — 계정·라이선스 이야기는 모델이 지어내면 안 된다.

        new ToolSpec(
            Id: "setup.windows_info",
            Title: "내 컴퓨터가 Home 인지 Pro 인지 알려주기",
            Category: ToolCategory.Setup,
            Description: "이 컴퓨터의 Windows 판을 확인하고, 그 판에서 학교 계정을 어떻게 넣을 수 있는지 "
                       + "무엇이 안 되는지 설명합니다. 아무것도 바꾸지 않습니다.",
            Examples: new[]
            {
                "내 컴퓨터 프로야 홈이야",
                "윈도우 버전이 뭐야",
                "내 윈도우가 무슨 판인지 알려줘",
            },
            Parameters: Array.Empty<ToolParam>(),
            Module: "Teavel.Setup", Function: "Get-TeavelWindowsInfo", Mutating: false, TimeoutSeconds: 60)
            { Aliases = new[] { "홈", "프로", "윈도우 판", "에디션", "버전" } },

        new ToolSpec(
            Id: "setup.account_guide",
            Title: "학교 계정을 어떻게 넣어야 하는지 안내",
            Category: ToolCategory.Setup,
            Description: "이 컴퓨터에서 학교 계정을 넣는 두 가지 방법(계정 추가 / 장치 연결)의 차이를 설명하고, "
                       + "Windows 판과 컴퓨터 주인에 맞는 쪽을 알려줍니다. 클릭 순서까지 알려줍니다.",
            Examples: new[]
            {
                "학교 계정 어떻게 넣어요",
                "회사 또는 학교 액세스가 뭐예요",
                "조인이랑 계정 추가랑 뭐가 달라요",
            },
            Parameters: new[]
            {
                new ToolParam("Ownership", ToolParamKind.Choice, "이 컴퓨터는",
                    "학교에서 지급한 컴퓨터인지 개인 컴퓨터인지에 따라 해야 할 일이 다릅니다.",
                    Required: false, Default: "unknown",
                    Choices: new[]
                    {
                        new ToolChoice("school", "학교에서 준 컴퓨터입니다",
                            "학교", "지급", "관용", "업무", "회사", "기관"),
                        new ToolChoice("personal", "제 개인 컴퓨터입니다",
                            "개인", "제것", "내것", "사비", "집"),
                        new ToolChoice("unknown", "잘 모르겠습니다", "모르", "글쎄", "몰라"),
                    }),
                new ToolParam("Account", ToolParamKind.Choice, "쓰실 계정은",
                    "학교에서 받은 M365 계정이 있으면 학교 계정, 없어서 개인 Microsoft 계정을 "
                  + "쓰실 거면 개인 계정입니다. 개인 계정이면 안내가 완전히 달라집니다.",
                    Required: false, Default: "unknown",
                    Choices: new[]
                    {
                        new ToolChoice("school", "학교에서 받은 계정을 씁니다  (@___.sen.go.kr 같은 것)",
                            "학교", "받은", "업무", "기관", "sen", "go.kr"),
                        new ToolChoice("personal", "개인 Microsoft 계정을 씁니다  (hotmail·outlook 등)",
                            "개인", "내", "제", "hotmail", "outlook", "gmail", "네이버"),
                        new ToolChoice("unknown", "잘 모르겠습니다", "모르", "글쎄", "몰라"),
                    }),
            },
            Module: "Teavel.Setup", Function: "Get-TeavelAccountGuide", Mutating: false, TimeoutSeconds: 60)
            { Aliases = new[] { "학교 계정", "계정 추가", "장치 연결", "조인", "회사 또는 학교", "로그인" } },

        // ─────────────────────────── 컴퓨터 이름 ───────────────────────────

        new ToolSpec(
            Id: "setup.computer_name",
            Title: "컴퓨터 이름 확인",
            Category: ToolCategory.Setup,
            Description: "이 컴퓨터의 이름과, 그것이 아직 공장 기본값인지 알려줍니다. 아무것도 바꾸지 않습니다.",
            Examples: new[]
            {
                "내 컴퓨터 이름이 뭐야",
                "컴퓨터 이름 확인해줘",
                "이 컴퓨터 이름 알려줘",
            },
            Parameters: Array.Empty<ToolParam>(),
            Module: "Teavel.Setup", Function: "Get-TeavelComputerName", Mutating: false, TimeoutSeconds: 60)
            { Aliases = new[] { "컴퓨터 이름", "장치 이름", "PC 이름", "확인" } },

        new ToolSpec(
            Id: "setup.rename_computer",
            Title: "컴퓨터 이름 바꾸기",
            Category: ToolCategory.Setup,
            Description: "컴퓨터 이름을 새로 정합니다. 관리자 확인 창이 한 번 뜨고, "
                       + "다시 시작해야 적용됩니다. 학교가 관리하는 컴퓨터는 바꾸지 않습니다.",
            Examples: new[]
            {
                "컴퓨터 이름 바꿔줘",
                "PC 이름을 2-3-kimminsu 로 바꿔줘",
                "장치 이름 새로 정하고 싶어",
            },
            Parameters: new[]
            {
                new ToolParam("NewName", ToolParamKind.Text, "새 컴퓨터 이름",
                    "영문자·숫자·붙임표(-) 만, 15자 이내. 한글은 쓸 수 없습니다. "
                  + "예: 2-3-kimminsu, sci-lab-01"),
            },
            Module: "Teavel.Setup", Function: "Set-TeavelComputerName", Mutating: true, TimeoutSeconds: 180)
            { Aliases = new[] { "컴퓨터 이름", "이름 바꾸기", "장치 이름", "PC 이름" } },

        // ──────────────────────────── 프린터 ────────────────────────────
        // 세팅 항목이면서 말로 시킬 수 있는 것들. 선생님들이 가장 많이 막히는 자리다.

        new ToolSpec(
            Id: "printer.list",
            Title: "프린터 목록 보기",
            Category: ToolCategory.Setup,
            Description: "이 컴퓨터에 등록된 프린터와 기본 프린터를 알려줍니다. 아무것도 바꾸지 않습니다.",
            Examples: new[]
            {
                "프린터 뭐 있는지 보여줘",
                "기본 프린터가 뭐야",
                "프린터 목록 알려줘",
            },
            Parameters: Array.Empty<ToolParam>(),
            Module: "Teavel.Setup", Function: "Get-PrinterStatus", Mutating: false, TimeoutSeconds: 60)
            { Aliases = new[] { "프린터", "인쇄", "기본 프린터", "목록" } },

        new ToolSpec(
            Id: "printer.set_default",
            Title: "기본 프린터 정하기",
            Category: ToolCategory.Setup,
            Description: "인쇄 단추를 눌렀을 때 나갈 프린터를 정합니다. "
                       + "Windows 가 '마지막에 쓴 프린터'로 자꾸 바꾸는 것도 함께 꺼 줍니다.",
            Examples: new[]
            {
                "기본 프린터를 3층복도로 해줘",
                "인쇄가 자꾸 딴 데로 나가",
                "기본 프린터 바꿔줘",
            },
            Parameters: new[]
            {
                new ToolParam("Name", ToolParamKind.Text, "프린터 이름",
                    "기본으로 쓸 프린터 이름. 정확하지 않아도 비슷하면 찾아 줍니다."),
            },
            Module: "Teavel.Setup", Function: "Set-TeavelDefaultPrinter", Mutating: true, TimeoutSeconds: 60)
            { Aliases = new[] { "기본 프린터", "프린터 바꾸기", "딴 데로 나감", "인쇄 설정" } },

        new ToolSpec(
            Id: "printer.add",
            Title: "프린터 추가하기",
            Category: ToolCategory.Setup,
            Description: "학교 공유 프린터나 IP 프린터를 등록합니다. "
                       + "공유 프린터는 드라이버가 서버에서 따라오므로 따로 구하지 않아도 됩니다.",
            Examples: new[]
            {
                "프린터 추가해줘",
                "교무실 프린터 연결해줘",
                "프린터 등록하고 싶어",
            },
            Parameters: new[]
            {
                new ToolParam("Path", ToolParamKind.Text, "공유 프린터 경로",
                    @"예: \\print-server\3층복도 — IP 로 붙일 거면 비워 둡니다.", Required: false),
                new ToolParam("Address", ToolParamKind.Text, "IP 주소",
                    "IP 프린터일 때만. 예: 192.168.0.50", Required: false),
                new ToolParam("Name", ToolParamKind.Text, "붙일 이름",
                    "IP 프린터일 때 이 컴퓨터에서 부를 이름.", Required: false),
                new ToolParam("DriverName", ToolParamKind.Text, "드라이버 이름",
                    "IP 프린터일 때만. 모르면 비워 두면 쓸 수 있는 목록을 알려줍니다.", Required: false),
            },
            Module: "Teavel.Setup", Function: "Add-TeavelPrinter", Mutating: true, TimeoutSeconds: 120)
            { Aliases = new[] { "프린터 추가", "프린터 연결", "프린터 등록", "새 프린터" } },
    };
}
