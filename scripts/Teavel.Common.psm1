<#
    Teavel 도구들이 함께 쓰는 도우미.

    표를 읽고 쓰는 일과 Office COM 을 다루는 일이 여기 모여 있다.
    도구 모듈(Teavel.Excel 등)은 업무 로직만 담고, 아래 함수들을 부른다.
#>

Set-StrictMode -Version Latest

# ─────────────────────────────── 결과 ───────────────────────────────

<#
.SYNOPSIS
    도구 함수가 돌려줄 결과 객체를 만든다.
.PARAMETER Message
    교사에게 보여줄 한 줄 요약.
.PARAMETER Details
    자세한 줄들(처리한 파일 목록, 통계 등).
#>
function New-TeavelResult {
    param(
        [Parameter(Mandatory)][string] $Message,
        [string[]] $Details = @()
    )
    [PSCustomObject]@{ Message = $Message; Details = @($Details) }
}

# ───────────────────────────── COM 수명 ─────────────────────────────

<#
.SYNOPSIS
    COM 개체 참조를 놓아 준다.
.DESCRIPTION
    이걸 빠뜨리면 교사 PC 작업 관리자에 EXCEL.EXE·WINWORD.EXE 가 보이지 않는 채로 쌓인다.
    도구 함수는 반드시 finally 에서 열었던 개체를 역순으로 돌려줘야 한다.
#>
function Remove-TeavelComObject {
    param([Parameter(ValueFromPipeline)] $InputObject)
    process {
        if ($null -ne $InputObject) {
            try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($InputObject) } catch { }
        }
    }
}

<#
.SYNOPSIS
    Office 응용 프로그램 COM 개체를 만든다(Excel/Word/Outlook).
.DESCRIPTION
    설치돼 있지 않으면 교사가 알아들을 수 있는 말로 실패시킨다.
    화면 갱신·경고창을 꺼서 배치 작업 중 대화상자로 멈추지 않게 한다.
#>
function New-TeavelOfficeApp {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Excel', 'Word', 'Outlook')]
        [string] $Kind
    )

    $progId = @{ Excel = 'Excel.Application'; Word = 'Word.Application'; Outlook = 'Outlook.Application' }[$Kind]
    $korean = @{ Excel = '엑셀';             Word = '워드';            Outlook = '아웃룩' }[$Kind]

    try {
        $app = New-Object -ComObject $progId
    } catch {
        throw "$korean 을(를) 열지 못했습니다. 이 컴퓨터에 $korean 이 설치돼 있는지 확인해 주세요."
    }

    try {
        if ($Kind -ne 'Outlook') {
            $app.Visible = $false
            $app.DisplayAlerts = $false
        }
        if ($Kind -eq 'Excel') {
            $app.ScreenUpdating = $false
            $app.AskToUpdateLinks = $false
            $app.EnableEvents = $false
        }
    } catch { }   # 버전에 따라 없는 속성이 있어도 진행

    $app
}

<#
.SYNOPSIS
    Office 응용 프로그램을 닫고 참조를 놓아 준다. 실패해도 예외를 내지 않는다.
#>
function Close-TeavelOfficeApp {
    param($App, [switch] $NoQuit)
    if ($null -eq $App) { return }
    if (-not $NoQuit) { try { $App.Quit() } catch { } }
    Remove-TeavelComObject $App
    # 놓아 준 참조가 실제로 회수되도록 한 번 밀어 준다.
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
}

# ──────────────────────────── 표 읽기·쓰기 ────────────────────────────

<#
.SYNOPSIS
    엑셀 또는 CSV 파일을 읽어 행 객체 배열로 돌려준다.
.DESCRIPTION
    머리글 행의 값이 각 행 객체의 속성 이름이 된다.
    엑셀은 셀을 하나씩 읽지 않고 UsedRange 를 통째로 가져온다 — 한 반 분량에서도
    셀 단위 접근은 수십 초가 걸리는 반면 이 방식은 한 번의 COM 호출로 끝난다.
.PARAMETER Path
    읽을 파일(.xlsx/.xls/.csv).
.PARAMETER Sheet
    시트 번호(1부터). CSV 는 무시한다.
.PARAMETER HeaderRow
    열 이름이 적힌 행 번호(1부터).
#>
function Read-TeavelTable {
    param(
        [Parameter(Mandatory)][string] $Path,
        [int] $Sheet = 1,
        [int] $HeaderRow = 1
    )

    if (-not (Test-Path -LiteralPath $Path)) { throw "파일을 찾지 못했습니다: $Path" }

    if ([IO.Path]::GetExtension($Path) -ieq '.csv') {
        # 엑셀에서 저장한 CSV 는 대개 시스템 기본 인코딩(한국어 Windows = 949)이다.
        # UTF-8 로 먼저 읽어 깨지면 949 로 다시 읽는다.
        $rows = @(Import-Csv -LiteralPath $Path -Encoding UTF8)
        if ($rows.Count -gt 0) {
            $firstHeader = @($rows[0].PSObject.Properties.Name)[0]
            if ($firstHeader -match '�') { $rows = @(Import-Csv -LiteralPath $Path -Encoding Default) }
        }
        return $rows
    }

    $excel = $null; $book = $null; $ws = $null; $range = $null
    try {
        $excel = New-TeavelOfficeApp -Kind Excel
        $book  = $excel.Workbooks.Open([IO.Path]::GetFullPath($Path), $false, $true)  # UpdateLinks=false, ReadOnly=true

        if ($Sheet -lt 1 -or $Sheet -gt $book.Worksheets.Count) {
            throw "$Sheet 번째 시트가 없습니다. 이 파일에는 시트가 $($book.Worksheets.Count)개 있습니다."
        }
        $ws    = $book.Worksheets.Item($Sheet)
        $range = $ws.UsedRange
        $grid  = $range.Value2

        if ($null -eq $grid) { return @() }

        # 셀이 하나뿐이면 배열이 아니라 값 하나가 온다.
        if ($grid -isnot [array]) { return @() }

        $rowCount = $grid.GetLength(0)
        $colCount = $grid.GetLength(1)

        # UsedRange 는 A1 이 아닌 곳에서 시작할 수 있다 — 시트 기준 행 번호를 배열 기준으로 옮긴다.
        $headerIndex = $HeaderRow - $range.Row + 1
        if ($headerIndex -lt 1 -or $headerIndex -gt $rowCount) {
            throw "$HeaderRow 행에서 열 이름을 찾지 못했습니다. 표가 비어 있거나 머리글 행 번호가 다릅니다."
        }

        $headers = @()
        for ($c = 1; $c -le $colCount; $c++) {
            $h = $grid[$headerIndex, $c]
            $headers += if ($null -eq $h -or [string]::IsNullOrWhiteSpace([string]$h)) { "열$c" } else { ([string]$h).Trim() }
        }

        $rows = New-Object System.Collections.Generic.List[object]
        for ($r = $headerIndex + 1; $r -le $rowCount; $r++) {
            $o = [ordered]@{}
            $hasValue = $false
            for ($c = 1; $c -le $colCount; $c++) {
                $v = $grid[$r, $c]
                if ($null -ne $v -and -not [string]::IsNullOrWhiteSpace([string]$v)) { $hasValue = $true }
                $o[$headers[$c - 1]] = $v
            }
            if ($hasValue) { $rows.Add([PSCustomObject]$o) }   # 완전히 빈 행은 버린다
        }

        # @($rows) 로 쓰면 안 된다.
        #
        # List[object] 를 @() 로 감싸면 PowerShell 이 '인수 형식이 일치하지 않습니다' 로 터진다.
        # Windows PowerShell 5.1 과 pwsh 7.4 에서 똑같이 그렇다. 목록이 비어 있어도 터진다.
        # List[string]·List[int]·List[psobject]·ArrayList 는 멀쩡하고 오직 List[object] 만 그렇다.
        #
        # 여기서 터지면 시트를 다 읽고 나서 마지막 줄에 실패한다 — 엑셀 도구가 통째로 못 쓰게 된다.
        # ToArray() 는 어느 판에서도 안전하다.
        return $rows.ToArray()
    }
    finally {
        Remove-TeavelComObject $range
        Remove-TeavelComObject $ws
        if ($null -ne $book) { try { $book.Close($false) } catch { } ; Remove-TeavelComObject $book }
        Close-TeavelOfficeApp $excel
    }
}

<#
.SYNOPSIS
    행 객체 배열을 엑셀 파일로 저장한다.
.DESCRIPTION
    셀을 하나씩 넣지 않고 2차원 배열을 만들어 Range 에 한 번에 넣는다(읽기와 같은 이유).
.PARAMETER Rows
    저장할 행 객체들.
.PARAMETER Path
    저장할 .xlsx 경로.
.PARAMETER Columns
    열 순서. 비우면 첫 행의 속성 순서를 쓴다.
#>
function Write-TeavelTable {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Rows,
        [Parameter(Mandatory)][string] $Path,
        [string[]] $Columns
    )

    if (-not $Columns -or $Columns.Count -eq 0) {
        if ($Rows.Count -eq 0) { throw '저장할 내용이 없습니다.' }
        $Columns = @($Rows[0].PSObject.Properties.Name)
    }

    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($Path))
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

    $excel = $null; $book = $null; $ws = $null; $target = $null
    try {
        $excel = New-TeavelOfficeApp -Kind Excel
        $book  = $excel.Workbooks.Add()
        $ws    = $book.Worksheets.Item(1)

        $rowCount = $Rows.Count + 1
        $colCount = $Columns.Count
        $grid = New-Object 'object[,]' $rowCount, $colCount

        for ($c = 0; $c -lt $colCount; $c++) { $grid[0, $c] = $Columns[$c] }
        for ($r = 0; $r -lt $Rows.Count; $r++) {
            for ($c = 0; $c -lt $colCount; $c++) {
                $prop = $Rows[$r].PSObject.Properties[$Columns[$c]]
                $grid[$r + 1, $c] = if ($prop) { $prop.Value } else { $null }
            }
        }

        $target = $ws.Range($ws.Cells.Item(1, 1), $ws.Cells.Item($rowCount, $colCount))
        $target.Value2 = $grid

        try {
            $ws.Rows.Item(1).Font.Bold = $true
            [void]$ws.Columns.AutoFit()
        } catch { }

        $full = [IO.Path]::GetFullPath($Path)
        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Force }
        $book.SaveAs($full, 51)   # 51 = xlOpenXMLWorkbook (.xlsx)
    }
    finally {
        Remove-TeavelComObject $target
        Remove-TeavelComObject $ws
        if ($null -ne $book) { try { $book.Close($false) } catch { } ; Remove-TeavelComObject $book }
        Close-TeavelOfficeApp $excel
    }
}

# ──────────────────────────────── 기타 ────────────────────────────────

<#
.SYNOPSIS
    "{이름} 학생" 같은 서식 문자열의 중괄호 자리를 행 값으로 채운다.
.DESCRIPTION
    행에 없는 열 이름은 그대로 둔다 — 조용히 빈칸으로 만들면 교사가 잘못을 눈치채지 못한다.
#>
function Expand-TeavelTemplate {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string] $Template,
        [Parameter(Mandatory)] $Row
    )
    [regex]::Replace($Template, '\{([^{}]+)\}', {
        param($m)
        $key  = $m.Groups[1].Value.Trim()
        $prop = $Row.PSObject.Properties[$key]
        if ($prop) { [string]$prop.Value } else { $m.Value }
    })
}

<#
.SYNOPSIS
    파일 이름에 쓸 수 없는 글자를 밑줄로 바꾼다.
#>
function ConvertTo-TeavelSafeFileName {
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Name)
    $invalid = [IO.Path]::GetInvalidFileNameChars()
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $Name.ToCharArray()) {
        [void]$sb.Append($(if ($invalid -contains $ch) { '_' } else { $ch }))
    }
    $out = $sb.ToString().Trim()
    if ([string]::IsNullOrWhiteSpace($out)) { '이름없음' } else { $out }
}

<#
.SYNOPSIS
    행에서 열을 찾는다. 없으면 있는 열 이름을 알려주며 실패시킨다.
.DESCRIPTION
    교사가 열 이름을 잘못 말했을 때 "그런 열 없음" 으로 끝내지 않고
    실제 열 목록을 보여 주면 바로 고쳐 말할 수 있다.
#>
function Get-TeavelColumnValue {
    param(
        [Parameter(Mandatory)] $Row,
        [Parameter(Mandatory)][string] $Column
    )
    $prop = $Row.PSObject.Properties[$Column]
    if (-not $prop) {
        $available = @($Row.PSObject.Properties.Name) -join ', '
        throw "'$Column' 열을 찾지 못했습니다. 이 표의 열: $available"
    }
    $prop.Value
}

Export-ModuleMember -Function `
    New-TeavelResult, Remove-TeavelComObject, New-TeavelOfficeApp, Close-TeavelOfficeApp, `
    Read-TeavelTable, Write-TeavelTable, Expand-TeavelTemplate, ConvertTo-TeavelSafeFileName, `
    Get-TeavelColumnValue
