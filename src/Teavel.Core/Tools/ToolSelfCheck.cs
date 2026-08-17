using System.Text.RegularExpressions;

namespace Teavel.Tools;

/// <summary>자가점검에서 찾은 문제 하나.</summary>
/// <param name="ToolId">문제가 있는 도구.</param>
/// <param name="Problem">무엇이 어긋났는지.</param>
public sealed record SelfCheckIssue(string ToolId, string Problem);

/// <summary>
/// 도구 선언(C#)과 PowerShell 구현이 어긋나지 않았는지 대조한다.
///
/// 이 둘은 이름 문자열로만 이어져 있어서, 한쪽만 고치면 교사가 실행하는 순간에야 터진다.
/// 여기서는 PowerShell 을 실행하지 않고 .psm1 을 글자로 읽어 대조하므로
/// Windows 가 아닌 곳에서도(빌드 서버·개발 PC) 돌릴 수 있다.
/// </summary>
public static class ToolSelfCheck
{
    // function Merge-Workbook {  …  param( … )
    private static readonly Regex FunctionRegex = new(
        @"^\s*function\s+(?<name>[A-Za-z][\w-]*)\s*\{",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // param( 블록 안의 [string] $Folder, … 에서 변수 이름만 뽑는다.
    private static readonly Regex ParamRegex = new(
        @"\$(?<name>[A-Za-z_]\w*)\s*(?:=|,|\)|$)",
        RegexOptions.Compiled);

    /// <summary>
    /// 모든 도구를 대조한다. 문제가 없으면 빈 목록.
    /// </summary>
    /// <param name="scriptsDirectory">.psm1 들이 있는 폴더.</param>
    public static IReadOnlyList<SelfCheckIssue> Run(string scriptsDirectory)
    {
        var issues = new List<SelfCheckIssue>();
        var moduleCache = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);

        // 모듈별 Export-ModuleMember 목록. Export 문이 아예 없으면 null(그때는 전부 내보내진다).
        var moduleExports = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in ToolCatalog.All)
        {
            // "@flow" 는 PowerShell 함수가 아니라 CLI 의 한 판이다. 대조할 .psm1 이 없다.
            if (tool.Module.StartsWith('@')) continue;

            if (!moduleCache.TryGetValue(tool.Module, out var functions))
            {
                var path = Path.Combine(scriptsDirectory, tool.Module + ".psm1");
                if (!File.Exists(path))
                {
                    issues.Add(new SelfCheckIssue(tool.Id, $"도구 모음 파일이 없습니다: {path}"));
                    continue;
                }

                try
                {
                    functions = ParseModule(File.ReadAllText(path));
                }
                catch (IOException ex)
                {
                    issues.Add(new SelfCheckIssue(tool.Id, $"{tool.Module}.psm1 을 읽지 못했습니다: {ex.Message}"));
                    continue;
                }
                moduleCache[tool.Module] = functions;
            }

            if (!functions.TryGetValue(tool.Function, out var declared))
            {
                issues.Add(new SelfCheckIssue(tool.Id,
                    $"{tool.Module}.psm1 안에 '{tool.Function}' 함수가 없습니다."));
                continue;
            }

            // 정의만 하고 Export-ModuleMember 에 안 넣으면 모듈 밖에서 부를 수 없다.
            // 파일만 훑으면 함수가 '있으니' 통과해 버리는데, 교사 PC 에서는
            // "기능을 찾지 못했습니다" 로 끝난다 — 실제로 한 번 놓친 적이 있다.
            if (!moduleExports.TryGetValue(tool.Module, out var exported))
                moduleExports[tool.Module] = exported =
                    ParseExports(File.ReadAllText(Path.Combine(scriptsDirectory, tool.Module + ".psm1")));

            if (exported is not null && !exported.Contains(tool.Function))
                issues.Add(new SelfCheckIssue(tool.Id,
                    $"'{tool.Function}' 이(가) {tool.Module}.psm1 의 Export-ModuleMember 에 없습니다. "
                  + "정의는 돼 있지만 모듈 밖에서 부를 수 없습니다."));

            foreach (var p in tool.Parameters)
                if (!declared.Contains(p.Name))
                    issues.Add(new SelfCheckIssue(tool.Id,
                        $"'{tool.Function}' 에 '{p.Name}' 매개변수가 없습니다. "
                      + $"(스크립트가 받는 것: {string.Join(", ", declared.OrderBy(x => x))})"));
        }

        // 아래 두 검사는 scripts\ 안의 모든 스크립트를 본다.
        //
        // 도구 목록에 있는 것만 보면 안 된다 — M365 기능은 상주 세션으로 도는 터라
        // 도구 목록에 없고, 그래서 한동안 BOM 검사에서 통째로 빠져 있었다.
        // 여기 놓인 파일은 결국 다 교사 PC 에서 돌아간다.
        foreach (var path in EnumerateScripts(scriptsDirectory))
        {
            var name = Path.GetFileName(path);

            // ① BOM 없는 스크립트는 한국어 Windows 에서 글자가 깨진다.
            //    Windows PowerShell 5.1 은 BOM 이 없으면 UTF-8 이 아니라 시스템 코드 페이지(CP949)로
            //    읽는다 — 파일은 멀쩡한데 교사 화면의 안내문만 전부 깨져 나온다.
            //    실기에서 실제로 겪었다(2026-08-17, Teavel.M365.psm1).
            if (!HasUtf8Bom(path))
                issues.Add(new SelfCheckIssue(name,
                    "UTF-8 BOM 이 없습니다. 한국어 Windows 에서 안내문이 깨져 나옵니다."));

            // ② param() 을 선언한 함수 안에서 @args 로 splatting 하는 것.
            //    $args 는 '바인딩되지 않은 인자' 를 담는 자동 변수라, param() 이 있으면 늘 비어 있다.
            //    그런데 splatting 은 비어 있어도 조용히 성공한다 — 매개변수 하나 없이 호출된다.
            //    실기에서 겪었다(2026-08-17): 팀 만들기가 이름도 별칭도 없이 호출돼
            //    빈 그룹이 하나 생기고 그다음부터 전부 실패했다. 오류 메시지는 엉뚱한 곳을 가리켰다.
            var source = File.ReadAllText(path);

            foreach (var fn in FindEmptySplats(source))
                issues.Add(new SelfCheckIssue(name,
                    $"'{fn}' 안에서 @args 로 splatting 합니다. param() 이 있는 함수에서 $args 는 "
                  + "항상 비어 있어, 아무 인자도 넘기지 않은 채 성공한 것처럼 보입니다."));

            // ③ List[object] 를 @() 로 감싸는 것.
            //    실기에서 겪었다(2026-08-17): 엑셀 시트를 다 읽고 마지막 줄에서
            //    '인수 형식이 일치하지 않습니다' 로 터졌다. 목록이 비어 있어도 터진다.
            // ④ 테넌트를 바꾸는 명령을 확인 창 대비 없이 부르는 것.
            //    실기에서 겪었다(2026-08-17): Set-User 가 "이 작업을 수행하시겠습니까?" 를 묻는데,
            //    상주 세션에는 답할 사람이 없다. 멈추지도 않는다 — stdin 에서 답을 읽으려 하고
            //    거기 흘러오는 것은 우리가 보낸 다음 명령의 JSON 한 줄이라, 명령이 통째로 사라진다.
            foreach (var call in FindUnconfirmedWrites(source))
                issues.Add(new SelfCheckIssue(name,
                    $"'{call}' 을(를) 확인 창 대비 없이 부릅니다. "
                  + "Invoke-TeavelWrite 로 부르거나 -Confirm:$false 를 붙이세요. "
                  + "안 그러면 확인 창이 다음 명령을 답으로 먹습니다."));

            foreach (var v in FindObjectListWrapping(source))
                issues.Add(new SelfCheckIssue(name,
                    $"'${v}' 은(는) List[object] 인데 @() 로 감쌉니다. "
                  + "PowerShell 이 '인수 형식이 일치하지 않습니다' 로 터집니다. "
                  + $"${v}.ToArray() 를 쓰세요."));
        }

        // 같은 id 가 두 번 선언되면 Find 가 먼저 것만 돌려주므로 반드시 잡는다.
        foreach (var dup in ToolCatalog.All.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new SelfCheckIssue(dup.Key, $"같은 id 의 도구가 {dup.Count()}개 선언돼 있습니다."));

        return issues;
    }

    /// <summary>
    /// 테넌트를 바꾸는 명령들. 이것들은 확인 창을 띄울 수 있다.
    /// </summary>
    /// <remarks>
    /// 낱말 규칙(Set-*·New-*)으로 잡으면 New-Object·Set-Content 까지 걸려 잔소리가 된다.
    /// 남의 테넌트를 실제로 바꾸는 것만 적어 둔다.
    /// </remarks>
    private static readonly string[] TenantWrites =
    {
        "Set-User", "Set-Mailbox", "Set-UnifiedGroup", "New-UnifiedGroup", "Remove-UnifiedGroup",
        "Add-UnifiedGroupLinks", "Remove-UnifiedGroupLinks",
        "New-Team", "Set-Team", "Remove-Team", "New-TeamChannel", "Remove-TeamChannel",
        "Add-TeamUser", "Remove-TeamUser",
    };

    /// <summary>확인 창 대비 없이 부르는 테넌트 변경 명령들.</summary>
    /// <remarks>
    /// 설명글(<c>&lt;# … #&gt;</c>)에도 명령 이름이 나오므로 그것부터 걷어낸다.
    /// 안 그러면 '이 명령은 확인을 묻는다' 라고 적어 둔 주석이 그대로 걸려 잔소리가 된다.
    /// </remarks>
    internal static IEnumerable<string> FindUnconfirmedWrites(string source)
    {
        var code = Regex.Replace(source, "<#.*?#>", "", RegexOptions.Singleline);
        code = Regex.Replace(code, "(?m)#.*$", "");

        foreach (var cmd in TenantWrites)
        {
            // 이름 뒤에 글자가 더 붙으면 다른 명령이다(Set-UserPhoto 는 Set-User 가 아니다).
            var pattern = "(?<![\\w'\\-])" + Regex.Escape(cmd) + "(?![\\w\\-])";

            foreach (Match m in Regex.Matches(code, pattern))
            {
                var line = LineAround(code, m.Index);
                if (line.Contains("-Confirm:$false", StringComparison.OrdinalIgnoreCase)) continue;

                // Invoke-TeavelWrite 가 대신 붙여 준다.
                if (line.Contains("Invoke-TeavelWrite", StringComparison.Ordinal)) continue;

                yield return cmd;
                break;
            }
        }
    }

    /// <summary>주어진 자리가 든 논리적 한 줄(백틱으로 이어진 것 포함).</summary>
    private static string LineAround(string source, int at)
    {
        var start = source.LastIndexOf('\n', Math.Min(at, source.Length - 1));
        start = start < 0 ? 0 : start + 1;

        var end = start;
        while (true)
        {
            var nl = source.IndexOf('\n', end);
            if (nl < 0) return source[start..];

            // 백틱으로 끝나면 다음 줄까지가 한 줄이다. 여기서 볼 것은 '그 줄' 이지
            // 파일 처음부터가 아니다 — 그걸 헷갈리면 이어짐을 영영 못 찾는다.
            if (source[end..nl].TrimEnd().EndsWith('`')) { end = nl + 1; continue; }

            return source[start..nl];
        }
    }

    /// <summary>scripts\ 안의 PowerShell 파일들.</summary>
    private static IEnumerable<string> EnumerateScripts(string scriptsDirectory)
    {
        if (!Directory.Exists(scriptsDirectory)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(scriptsDirectory, "*.ps*1", SearchOption.AllDirectories)
                            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }
        catch (IOException) { return Array.Empty<string>(); }
    }

    /// <summary>
    /// param() 을 선언해 놓고 <c>@args</c> 로 splatting 하는 함수 이름들.
    /// </summary>
    /// <remarks>
    /// <c>$args</c> 는 param() 으로 받지 <b>못한</b> 인자만 담는 자동 변수다.
    /// param() 이 있고 인자가 전부 거기로 들어가면 <c>$args</c> 는 빈 배열이고,
    /// <c>@args</c> splatting 은 <b>조용히 아무것도 넘기지 않는다.</b>
    /// 실수의 모양이 늘 같다 — 해시테이블을 정성껏 채워 놓고 이름만 틀리게 부른다.
    /// </remarks>
    internal static IEnumerable<string> FindEmptySplats(string source)
    {
        foreach (Match fn in FunctionRegex.Matches(source))
        {
            var name = fn.Groups["name"].Value;
            var block = FunctionBody(source, fn.Index);
            if (block.Length == 0) continue;

            // param() 이 없으면 $args 로 받는 것이 정상이다.
            if (!Regex.IsMatch(block, @"\bparam\s*\(", RegexOptions.IgnoreCase)) continue;

            // 주석 안의 @args 까지 잡으면 잔소리가 된다.
            var code = Regex.Replace(block, @"(?m)#.*$", "");
            if (Regex.IsMatch(code, @"@args\b")) yield return name;
        }
    }

    /// <summary>
    /// <c>List[object]</c> 로 만든 변수를 <c>@()</c> 로 감싸는 곳의 변수 이름들.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>@($list)</c> 는 <c>List[object]</c> 일 때만 터진다 —
    /// <c>인수 형식이 일치하지 않습니다(Argument types do not match)</c>.
    /// Windows PowerShell 5.1 과 pwsh 7.4 에서 똑같고, <b>목록이 비어 있어도</b> 터진다.
    /// <c>List[string]</c>·<c>List[int]</c>·<c>List[psobject]</c>·<c>ArrayList</c> 는 멀쩡하다.
    /// </para>
    /// <para>
    /// 이것이 고약한 이유는 <b>일을 다 하고 나서</b> 터지기 때문이다.
    /// 엑셀 시트를 전부 읽은 뒤 돌려주는 마지막 줄에서 실패해, 화면에는
    /// 엉뚱한 오류만 남고 원인은 어디에도 보이지 않는다.
    /// </para>
    /// </remarks>
    internal static IEnumerable<string> FindObjectListWrapping(string source)
    {
        // 주석 안의 예시까지 잡으면 잔소리가 된다.
        var code = Regex.Replace(source, @"(?m)#.*$", "");

        var declared = Regex.Matches(code,
                @"\$(?<name>\w+)\s*=\s*(?:New-Object\s+(?:System\.Collections\.Generic\.)?List\[object\]"
              + @"|\[System\.Collections\.Generic\.List\[object\]\]::new\(\))",
                RegexOptions.IgnoreCase)
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in declared)
        {
            if (Regex.IsMatch(code, $@"@\(\s*\${Regex.Escape(name)}\s*\)"))
                yield return name;
        }
    }

    /// <summary>
    /// <paramref name="start"/> 의 함수 선언에서 중괄호 짝을 세어 본문을 잘라 낸다.
    /// 짝이 안 맞으면 빈 문자열 — 그건 구문 오류라 다른 곳에서 걸린다.
    /// </summary>
    private static string FunctionBody(string source, int start)
    {
        var open = source.IndexOf('{', start);
        if (open < 0) return "";

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        return "";
    }

    /// <summary>파일이 UTF-8 BOM 으로 시작하는지.</summary>
    private static bool HasUtf8Bom(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[3];
            return f.Read(head) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// Export-ModuleMember -Function 에 적힌 이름들을 뽑는다.
    /// Export 문이 아예 없으면 null — 그때는 PowerShell 이 모든 함수를 내보내므로 따질 것이 없다.
    /// </summary>
    private static HashSet<string>? ParseExports(string source)
    {
        var at = source.IndexOf("Export-ModuleMember", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        // 줄 끝 백틱으로 이어지는 여러 줄 형태를 그대로 받는다.
        var tail = source[at..];
        var end = 0;
        while (end < tail.Length)
        {
            var nl = tail.IndexOf('\n', end);
            if (nl < 0) { end = tail.Length; break; }

            var line = tail[end..nl].TrimEnd('\r', ' ', '\t');
            end = nl + 1;
            if (!line.EndsWith('`')) break;   // 이어지지 않으면 여기까지
        }

        var names = Regex.Matches(tail[..end], @"[A-Za-z][\w-]*-[A-Za-z][\w-]*")
            .Select(m => m.Value)
            .Where(n => !n.Equals("Export-ModuleMember", StringComparison.OrdinalIgnoreCase));

        return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>모듈 원문에서 (함수 이름 → 매개변수 이름들) 을 뽑는다.</summary>
    private static Dictionary<string, HashSet<string>> ParseModule(string source)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (Match fn in FunctionRegex.Matches(source))
        {
            var name = fn.Groups["name"].Value;
            var body = source[fn.Index..];

            var paramStart = body.IndexOf("param", StringComparison.OrdinalIgnoreCase);
            if (paramStart < 0) { result[name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase); continue; }

            var open = body.IndexOf('(', paramStart);
            if (open < 0) { result[name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase); continue; }

            // param( … ) 의 짝이 맞는 닫는 괄호를 찾는다(안에 [ValidateSet(...)] 같은 중첩이 있다).
            var depth = 0;
            var close = -1;
            for (var i = open; i < body.Length; i++)
            {
                if (body[i] == '(') depth++;
                else if (body[i] == ')' && --depth == 0) { close = i; break; }
            }
            if (close < 0) { result[name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase); continue; }

            var block = body[(open + 1)..close];
            var names = ParamRegex.Matches(block).Select(m => m.Groups["name"].Value);
            result[name] = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }
}
