<#
.SYNOPSIS
    Teavel 도구 실행 래퍼 — 표준 입력으로 받은 JSON 한 덩어리를 PowerShell 함수 호출로 바꾼다.

.DESCRIPTION
    Teavel(.NET) 이 이 스크립트를 띄우고 stdin 으로 다음 JSON 을 흘려보낸다:

        {
          "module":           "Teavel.Excel",
          "function":         "Merge-Workbook",
          "scriptsDirectory": "C:\\...\\scripts",
          "args":             { "Folder": "...", "Sheet": 1 }
        }

    인자를 명령줄이 아니라 stdin JSON 으로 받는 이유: 값 안의 따옴표·백틱·공백이
    명령을 갈라놓을 수 없게 하기 위해서다. 값은 splatting 으로 넘어가 끝까지 '값' 으로만 남는다.

    결과는 stdout 에 JSON 한 덩어리로 낸다:

        { "ok": true, "message": "...", "details": ["...", "..."] }

    도구 함수는 Message·Details 를 가진 객체를 돌려주거나(New-TeavelResult 사용),
    실패 시 throw 하면 된다. 나머지는 이 래퍼가 처리한다.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 한글이 깨지지 않도록 입출력을 UTF-8(BOM 없음)로 고정한다.
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding = [Console]::OutputEncoding
} catch { }

function Write-TeavelResponse {
    param(
        [bool]   $Ok,
        [string] $Message,
        [string[]] $Details = @()
    )
    $payload = [ordered]@{
        ok      = $Ok
        message = $Message
        details = @($Details)
    }
    # -Compress: 마지막 '{' 부터 읽는 .NET 쪽 파서와 맞추기 위해 한 줄로 낸다.
    Write-Output ($payload | ConvertTo-Json -Depth 6 -Compress)
}

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) {
        Write-TeavelResponse -Ok $false -Message '실행할 내용을 받지 못했습니다.'
        exit 1
    }

    $request = $raw | ConvertFrom-Json

    foreach ($field in 'module', 'function', 'scriptsDirectory') {
        if (-not $request.PSObject.Properties[$field] -or [string]::IsNullOrWhiteSpace($request.$field)) {
            Write-TeavelResponse -Ok $false -Message "요청에 '$field' 가 없습니다."
            exit 1
        }
    }

    # ── 공용 도우미 + 대상 모듈 적재 ──
    $common = Join-Path $request.scriptsDirectory 'Teavel.Common.psm1'
    if (Test-Path -LiteralPath $common) { Import-Module $common -Force -DisableNameChecking }

    $modulePath = Join-Path $request.scriptsDirectory ($request.module + '.psm1')
    if (-not (Test-Path -LiteralPath $modulePath)) {
        Write-TeavelResponse -Ok $false `
            -Message "도구 모음을 찾지 못했습니다: $($request.module)" `
            -Details @("있어야 할 위치: $modulePath")
        exit 1
    }
    Import-Module $modulePath -Force -DisableNameChecking

    $command = Get-Command -Name $request.function -CommandType Function -ErrorAction SilentlyContinue
    if (-not $command) {
        Write-TeavelResponse -Ok $false `
            -Message "'$($request.function)' 기능을 찾지 못했습니다." `
            -Details @("$($request.module) 안에 그 이름의 함수가 없습니다.")
        exit 1
    }

    # ── 인자를 해시테이블로 옮기며, 그 함수에 실제로 있는 매개변수인지 확인 ──
    # (선언과 구현이 어긋나면 조용히 무시하지 않고 여기서 잡는다)
    $splat = @{}
    $unknown = @()
    if ($request.PSObject.Properties['args'] -and $null -ne $request.args) {
        foreach ($p in $request.args.PSObject.Properties) {
            if ($command.Parameters.ContainsKey($p.Name)) {
                $splat[$p.Name] = $p.Value
            } else {
                $unknown += $p.Name
            }
        }
    }
    if ($unknown.Count -gt 0) {
        Write-TeavelResponse -Ok $false `
            -Message "'$($request.function)' 이(가) 받지 않는 입력이 있습니다: $($unknown -join ', ')" `
            -Details @('Teavel 의 도구 선언과 스크립트가 어긋났습니다. 자가점검을 실행해 주세요.')
        exit 1
    }

    $result = & $command @splat

    # 도구 함수는 Message·Details 를 가진 객체를 돌려주기로 약속돼 있다.
    $message = '완료했습니다.'
    $details = @()
    if ($null -ne $result) {
        if ($result.PSObject.Properties['Message'] -and $result.Message) { $message = [string]$result.Message }
        if ($result.PSObject.Properties['Details'] -and $result.Details) { $details = @($result.Details | ForEach-Object { [string]$_ }) }
    }

    Write-TeavelResponse -Ok $true -Message $message -Details $details
    exit 0
}
catch {
    $err = $_
    $detail = @()
    if ($err.InvocationInfo -and $err.InvocationInfo.PositionMessage) {
        $detail += ($err.InvocationInfo.PositionMessage -split "`n" | ForEach-Object { $_.TrimEnd() } | Where-Object { $_ })
    }
    Write-TeavelResponse -Ok $false -Message $err.Exception.Message -Details $detail
    exit 1
}
