<#
.SYNOPSIS
    M365 전용 상주 PowerShell. 한 번 붙고 계속 산다.

.DESCRIPTION
    보통 도구는 Invoke-TeavelTool.ps1 이 호출마다 PowerShell 을 새로 띄워 처리한다.
    그 편이 깨끗하고, 한 도구가 망가져도 다음 도구에 옮지 않는다.

    그런데 M365 는 그러면 안 된다. Connect-ExchangeOnline 은 그 프로세스 안에서만
    살아 있어서, 새로 띄울 때마다 브라우저 로그인을 다시 해야 한다.
    재고 보고 · 이름 바꾸고 · 만들고 하는 동안 로그인을 예닐곱 번 하게 된다는 뜻이다.
    로그인 창 하나도 어려워하는 분들에게 그건 사실상 못 쓰는 기능이다.

    그래서 이 스크립트는 한 번 떠서 계속 산다. Teavel 이 stdin 으로 요청을 한 줄씩
    흘려보내면 하나씩 처리하고 답한다. 연결은 그동안 유지된다.

    ■ 주고받는 방식

    요청(Teavel → 여기): 한 줄 JSON
        {"function":"Get-TeavelM365Inventory","args":{}}
        {"function":"__bye"}                              ← 끝내라

    답(여기 → Teavel): 표시 문구는 그냥 흘려보내고,
    결과 한 덩어리만 표시자를 붙여 한 줄로 낸다.

        연결 중입니다...                                   ← 그대로 사용자에게 보여 준다
        ##TEAVEL##{"ok":true,"message":"...","details":[]}  ← 이것이 결과

    Write-Host 로 나가는 안내(브라우저 로그인 설명 같은 것)가 결과와 섞이지 않게
    하려는 것이다. 표시자 없는 줄은 전부 진행 상황이라 곧바로 사용자 화면에 흐른다.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ScriptsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
    WAM(Windows 계정 관리자)을 쓰지 않는다.

    최근 판의 로그인은 WAM 을 거치는데, WAM 은 로그인 창을 띄울 '부모 창' 을 요구한다.
    이 프로세스는 창 없이 도는 상주 세션이라 부모 창이 없고, 그래서 이렇게 끝난다.

        A window handle must be configured.
        https://aka.ms/msal-net-wam#parent-window-handles

    실기에서 그랬다(2026-08-17). 같은 명령을 선생님이 직접 연 PowerShell 창에서
    돌리면 성공한다 — 거기엔 창이 있기 때문이다. 우리가 창을 만들 수도 있지만,
    그러면 까만 창이 하나 더 떠서 선생님이 어느 창을 봐야 할지 헷갈린다.

    WAM 을 끄면 예전 방식(브라우저 창)으로 돌아간다. 우리가 화면에 안내하는 것이
    바로 그 브라우저 창이므로 안내와도 맞는다.

    이 값은 이 프로세스에만 걸린다. 교사 PC 의 설정을 바꾸지 않는다.
#>
$env:MSAL_DISABLE_WAM = '1'

# 한글이 깨지지 않도록 입출력을 UTF-8(BOM 없음)로 고정한다.
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding = [Console]::OutputEncoding
} catch { }

$Marker = '##TEAVEL##'

<#
.SYNOPSIS
    오류에서 <b>쓸 만한 말</b>을 뽑아낸다.
.DESCRIPTION
    .NET 예외는 겹겹이 싸여 있어서 겉껍데기만 보면 아무것도 알 수 없다.
    실기에서 이렇게 나왔다:

        ✗ 하나 이상의 오류가 발생했습니다.

    이건 AggregateException 의 문구일 뿐이고 진짜 원인은 그 안에 있다.
    전원도 안 꽂고 AS 를 부르는 분들에게 이런 말은 아무 쓸모가 없다 —
    우리에게도 쓸모가 없다. 무엇이 잘못됐는지 알 수가 없기 때문이다.

    그래서 안쪽까지 파고들어 가장 구체적인 말을 앞에 세우고, 거쳐 온 것들을 함께 남긴다.
#>
function Get-TeavelErrorLines {
    param($ErrorRecord)

    $seen = New-Object System.Collections.Generic.List[string]

    function Add-One {
        param($Text)
        $t = ([string]$Text).Trim()
        if ($t -and -not $seen.Contains($t)) { $seen.Add($t) }
    }

    $ex = $ErrorRecord.Exception
    $depth = 0
    while ($ex -and $depth -lt 8) {
        Add-One $ex.Message

        # AggregateException 은 InnerException 하나가 아니라 여럿을 들고 있다.
        if ($ex -is [System.AggregateException]) {
            foreach ($ie in $ex.InnerExceptions) {
                $inner = $ie
                $d2 = 0
                while ($inner -and $d2 -lt 8) { Add-One $inner.Message; $inner = $inner.InnerException; $d2++ }
            }
        }

        $ex = $ex.InnerException
        $depth++
    }

    if ($seen.Count -eq 0) { Add-One $ErrorRecord.ToString() }
    ,$seen.ToArray()
}

function Write-TeavelReply {
    param(
        [bool] $Ok,
        [string] $Message,
        [string[]] $Details = @()
    )
    $payload = [ordered]@{ ok = $Ok; message = $Message; details = @($Details) }
    # 한 줄로 낸다 — 저쪽은 표시자로 시작하는 줄 하나를 결과로 읽는다.
    [Console]::Out.WriteLine($Marker + ($payload | ConvertTo-Json -Depth 6 -Compress))
    [Console]::Out.Flush()
}

try {
    Import-Module (Join-Path $ScriptsDirectory 'Teavel.Common.psm1') -Force -ErrorAction Stop
    Import-Module (Join-Path $ScriptsDirectory 'Teavel.M365.psm1')   -Force -ErrorAction Stop

    # 우리가 받아 둔 모듈 폴더를 이 세션에 알려 준다.
    #
    # PowerShell 은 이 폴더를 기본으로 보지 않는다. 예전에는 설치를 돌린 그 세션에서만
    # 붙여 줬는데, 모듈을 깐 뒤 세션을 새로 띄우면 방금 깐 것을 못 찾았다 —
    # '설치했는데도 아직 모자랍니다' 가 그것이다(2026-08-27).
    Add-TeavelModulePath
}
catch {
    Write-TeavelReply -Ok $false -Message "M365 기능을 불러오지 못했습니다: $($_.Exception.Message)"
    exit 1
}

Write-TeavelReply -Ok $true -Message '준비됐습니다.'

while ($true) {
    $line = [Console]::In.ReadLine()

    # stdin 이 닫혔다 — Teavel 이 먼저 끝났다는 뜻이다. 조용히 나간다.
    if ($null -eq $line) { break }
    if ([string]::IsNullOrWhiteSpace($line)) { continue }

    try {
        $request = $line | ConvertFrom-Json
    }
    catch {
        Write-TeavelReply -Ok $false -Message '요청을 읽지 못했습니다.'
        continue
    }

    $fn = if ($request.PSObject.Properties['function']) { [string]$request.function } else { '' }
    if ($fn -eq '__bye') { break }

    if ([string]::IsNullOrWhiteSpace($fn)) {
        Write-TeavelReply -Ok $false -Message '무엇을 할지 받지 못했습니다.'
        continue
    }

    # 이 상주 세션에서 부를 수 있는 것은 여기 적힌 것뿐이다.
    # 이름을 그대로 실행하면 stdin 을 쥔 쪽이 무엇이든 부를 수 있게 된다.
    $allowed = @(
        'Get-TeavelM365Readiness', 'Install-TeavelM365Module', 'Connect-TeavelM365',
        'Get-TeavelM365Inventory', 'Get-TeavelTenantUser',
        'Get-TeavelUserName', 'Set-TeavelDisplayName',
        'New-TeavelM365Group', 'Sync-TeavelTeamChannel',
        'Get-TeavelTeamMember', 'Add-TeavelTeamMember', 'Remove-TeavelTeamStudent',
        'Rename-TeavelM365Group', 'Remove-TeavelM365Group',

        # 비밀번호만 Graph 를 쓴다. 다른 것과 섞이지 않게 줄을 갈라 둔다.
        'Get-TeavelGraphReadiness', 'Install-TeavelGraphModule',
        'Connect-TeavelGraph', 'Reset-TeavelPassword',
        'Set-TeavelAccountBlocked', 'Remove-TeavelAccount'
    )
    if ($allowed -notcontains $fn) {
        Write-TeavelReply -Ok $false -Message "'$fn' 은(는) 여기서 실행할 수 없습니다."
        continue
    }

    $callArgs = @{}
    if ($request.PSObject.Properties['args'] -and $request.args) {
        foreach ($p in $request.args.PSObject.Properties) {
            $callArgs[$p.Name] = $p.Value
        }
    }

    # 필수 매개변수가 빠졌으면 여기서 끊는다.
    #
    # 안 그러면 PowerShell 이 "Supply values for the following parameters:" 를 띄우고
    # <b>바로 이 stdin 에서</b> 값을 읽는다 — 그런데 이 stdin 은 우리가 JSON 명령을
    # 흘려보내는 통로다. 다음 명령이 통째로 답으로 먹히고, 그 뒤로는 모든 것이 한 칸씩
    # 밀린다. 확인 창(-Confirm)으로 똑같이 당한 적이 있어 이쪽도 함께 막는다.
    # 이 세션은 로그인 창이 떠야 해서 -NonInteractive 를 쓸 수 없다.
    $cmdInfo = Get-Command -Name $fn -CommandType Function -ErrorAction SilentlyContinue
    if ($cmdInfo) {
        $missing = @()
        foreach ($kv in $cmdInfo.Parameters.GetEnumerator()) {
            $isRequired = $kv.Value.Attributes |
                Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }
            if ($isRequired -and -not $callArgs.ContainsKey($kv.Key)) { $missing += $kv.Key }
        }
        if ($missing.Count -gt 0) {
            Write-TeavelReply -Ok $false `
                -Message "'$fn' 에 필요한 값이 빠졌습니다: $($missing -join ', ')" `
                -Details @('Teavel 의 도구 선언과 스크립트가 어긋났습니다. 자가점검을 실행해 주세요.')
            continue
        }
    }

    try {
        $result = & $fn @callArgs

        $msg = ''
        $det = @()
        if ($result) {
            if ($result.PSObject.Properties['Message']) { $msg = [string]$result.Message }
            if ($result.PSObject.Properties['Details']) { $det = @($result.Details) }
            if (-not $msg) { $msg = [string]$result }
        }
        Write-TeavelReply -Ok $true -Message $msg -Details $det
    }
    catch {
        # 한 도구가 실패해도 세션은 살아 있어야 한다.
        # 여기서 죽으면 애써 해 둔 로그인이 함께 날아간다.
        # 겉껍데기가 아니라 안쪽의 구체적인 말을 앞에 세운다.
        $lines = Get-TeavelErrorLines $_

        # 여러 겹이면 가장 안쪽(마지막)이 진짜 원인인 경우가 많다.
        # 다만 겉이 더 친절할 때도 있어, 뻔한 문구일 때만 안쪽으로 바꾼다.
        $vague = @('하나 이상의 오류가 발생했습니다.', 'One or more errors occurred.')
        $msg = $lines[0]
        if ($lines.Count -gt 1 -and ($vague -contains $msg)) { $msg = $lines[-1] }

        $detail = New-Object System.Collections.Generic.List[string]
        foreach ($l in $lines) { if ($l -ne $msg) { $detail.Add($l) } }
        if ($_.InvocationInfo -and $_.InvocationInfo.ScriptLineNumber) {
            $detail.Add("위치: $($_.InvocationInfo.ScriptName):$($_.InvocationInfo.ScriptLineNumber)")
        }

        Write-TeavelReply -Ok $false -Message $msg -Details $detail.ToArray()
    }
}
