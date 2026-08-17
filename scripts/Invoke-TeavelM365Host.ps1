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

# 한글이 깨지지 않도록 입출력을 UTF-8(BOM 없음)로 고정한다.
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding = [Console]::OutputEncoding
} catch { }

$Marker = '##TEAVEL##'

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
        'Get-TeavelM365Inventory', 'New-TeavelM365Group', 'Sync-TeavelTeamChannel',
        'Rename-TeavelM365Group', 'Remove-TeavelM365Group'
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
        $detail = @()
        if ($_.InvocationInfo -and $_.InvocationInfo.ScriptLineNumber) {
            $detail += "위치: $($_.InvocationInfo.ScriptName):$($_.InvocationInfo.ScriptLineNumber)"
        }
        Write-TeavelReply -Ok $false -Message $_.Exception.Message -Details $detail
    }
}
