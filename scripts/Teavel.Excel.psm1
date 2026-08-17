<#
    엑셀 — 성적·명단 처리.

    모든 함수는 New-TeavelResult 로 결과를 돌려주고, 문제가 생기면 교사가 알아들을
    한국어로 throw 한다. 원본 파일은 어떤 함수도 덮어쓰지 않는다(Convert-Workbook 에서
    교사가 같은 경로를 직접 지정한 경우만 예외).
#>

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    엑셀 파일의 시트·열 이름·행 수를 알려준다.
.DESCRIPTION
    다른 작업을 하기 전에 열 이름을 확인하는 용도. 파일을 전혀 바꾸지 않는다.
#>
function Get-WorkbookInfo {
    param(
        [Parameter(Mandatory)][string] $File
    )

    if (-not (Test-Path -LiteralPath $File)) { throw "파일을 찾지 못했습니다: $File" }

    if ([IO.Path]::GetExtension($File) -ieq '.csv') {
        $rows = Read-TeavelTable -Path $File
        $cols = if ($rows.Count -gt 0) { @($rows[0].PSObject.Properties.Name) } else { @() }
        return New-TeavelResult -Message "CSV 파일입니다. $($rows.Count)행." -Details @(
            "열: $($cols -join ', ')"
        )
    }

    $excel = $null; $book = $null
    try {
        $excel = New-TeavelOfficeApp -Kind Excel
        $book  = $excel.Workbooks.Open([IO.Path]::GetFullPath($File), $false, $true)

        $details = New-Object System.Collections.Generic.List[string]
        $count = $book.Worksheets.Count

        for ($i = 1; $i -le $count; $i++) {
            $ws = $null; $range = $null
            try {
                $ws    = $book.Worksheets.Item($i)
                $range = $ws.UsedRange
                $rows  = $range.Rows.Count
                $cols  = $range.Columns.Count

                # 머리글(첫 행)만 한 번에 읽는다.
                $headers = @()
                $grid = $range.Value2
                if ($null -ne $grid -and $grid -is [array]) {
                    for ($c = 1; $c -le $cols; $c++) {
                        $h = $grid[1, $c]
                        if ($null -ne $h -and -not [string]::IsNullOrWhiteSpace([string]$h)) {
                            $headers += ([string]$h).Trim()
                        }
                    }
                }

                $details.Add("[$i] $($ws.Name) — $($rows)행 x $($cols)열")
                if ($headers.Count -gt 0) { $details.Add("     열: $($headers -join ', ')") }
                else { $details.Add('     (비어 있음)') }
            }
            finally {
                Remove-TeavelComObject $range
                Remove-TeavelComObject $ws
            }
        }

        New-TeavelResult -Message "시트 $($count)개를 찾았습니다." -Details $details
    }
    finally {
        if ($null -ne $book) { try { $book.Close($false) } catch { } ; Remove-TeavelComObject $book }
        Close-TeavelOfficeApp $excel
    }
}

<#
.SYNOPSIS
    폴더 안의 엑셀 파일들을 위아래로 이어붙여 하나로 만든다.
.DESCRIPTION
    열 구성이 조금씩 달라도 된다 — 모든 파일에서 본 열을 처음 나온 순서대로 모으고,
    어떤 파일에 없는 열은 빈칸으로 둔다. 어느 파일에서 온 행인지 '원본파일' 열에 적는다.
#>
function Merge-Workbook {
    param(
        [Parameter(Mandatory)][string] $Folder,
        [Parameter(Mandatory)][string] $Output,
        [string] $Pattern   = '*.xlsx',
        [int]    $Sheet     = 1,
        [int]    $HeaderRow = 1
    )

    if (-not (Test-Path -LiteralPath $Folder)) { throw "폴더를 찾지 못했습니다: $Folder" }

    $outFull = [IO.Path]::GetFullPath($Output)

    $files = @(Get-ChildItem -LiteralPath $Folder -Filter $Pattern -File |
               Where-Object {
                   # 임시 파일(~$…)과, 지난번에 만든 결과 파일 자신은 제외한다.
                   $_.Name -notlike '~$*' -and $_.FullName -ne $outFull
               } |
               Sort-Object Name)

    if ($files.Count -eq 0) { throw "'$Folder' 안에서 '$Pattern' 에 맞는 파일을 찾지 못했습니다." }

    $columns = New-Object System.Collections.Generic.List[string]
    $columns.Add('원본파일')
    $merged  = New-Object System.Collections.Generic.List[object]
    $details = New-Object System.Collections.Generic.List[string]
    $skipped = New-Object System.Collections.Generic.List[string]

    foreach ($f in $files) {
        try {
            $rows = @(Read-TeavelTable -Path $f.FullName -Sheet $Sheet -HeaderRow $HeaderRow)
        } catch {
            # 한 파일이 이상해도 나머지는 살린다 — 무엇을 건너뛰었는지는 반드시 알린다.
            $skipped.Add("$($f.Name) — $($_.Exception.Message)")
            continue
        }

        foreach ($r in $rows) {
            $o = [ordered]@{ '원본파일' = $f.BaseName }
            foreach ($p in $r.PSObject.Properties) {
                if (-not $columns.Contains($p.Name)) { $columns.Add($p.Name) }
                $o[$p.Name] = $p.Value
            }
            $merged.Add([PSCustomObject]$o)
        }
        $details.Add("$($f.Name) — $($rows.Count)행")
    }

    if ($merged.Count -eq 0) { throw '합칠 행이 하나도 없습니다. 시트 번호와 머리글 행을 확인해 주세요.' }

    # @($merged) 로 감싸면 터진다 — List[object] 의 함정이다(Teavel.Common.psm1 의 주석 참고).
    Write-TeavelTable -Rows $merged.ToArray() -Path $outFull -Columns $columns.ToArray()

    if ($skipped.Count -gt 0) {
        $details.Add('')
        $details.Add('건너뛴 파일:')
        foreach ($s in $skipped) { $details.Add("  $s") }
    }
    $details.Add('')
    $details.Add("저장: $outFull")

    New-TeavelResult `
        -Message "파일 $($files.Count - $skipped.Count)개, 모두 $($merged.Count)행을 하나로 합쳤습니다." `
        -Details $details
}

<#
.SYNOPSIS
    표를 한 열의 값에 따라 여러 파일로 나눈다.
#>
function Split-WorkbookByColumn {
    param(
        [Parameter(Mandatory)][string] $File,
        [Parameter(Mandatory)][string] $Column,
        [Parameter(Mandatory)][string] $OutputFolder,
        [int] $Sheet     = 1,
        [int] $HeaderRow = 1
    )

    $rows = @(Read-TeavelTable -Path $File -Sheet $Sheet -HeaderRow $HeaderRow)
    if ($rows.Count -eq 0) { throw '표에 행이 없습니다. 시트 번호와 머리글 행을 확인해 주세요.' }

    # 열 이름이 틀렸으면 있는 열을 알려주며 멈춘다.
    [void](Get-TeavelColumnValue -Row $rows[0] -Column $Column)

    $outDir = [IO.Path]::GetFullPath($OutputFolder)
    if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

    $columns = @($rows[0].PSObject.Properties.Name)
    $groups  = $rows | Group-Object -Property $Column
    $base    = [IO.Path]::GetFileNameWithoutExtension($File)

    $details = New-Object System.Collections.Generic.List[string]
    foreach ($g in $groups) {
        $label = if ([string]::IsNullOrWhiteSpace($g.Name)) { '값없음' } else { $g.Name }
        $safe  = ConvertTo-TeavelSafeFileName -Name "${base}_${label}"
        $path  = Join-Path $outDir "$safe.xlsx"
        Write-TeavelTable -Rows @($g.Group) -Path $path -Columns $columns
        $details.Add("$label — $($g.Count)행 → $safe.xlsx")
    }

    $details.Add('')
    $details.Add("저장 폴더: $outDir")

    New-TeavelResult -Message "'$Column' 기준으로 $($groups.Count)개 파일로 나눴습니다." -Details $details
}

<#
.SYNOPSIS
    점수 열의 인원·평균·표준편차·최고·최저·중앙값을 낸다.
.DESCRIPTION
    파일을 바꾸지 않는다. GroupColumn 을 주면 그 값별로 나눠서도 계산한다.
    숫자가 아닌 칸(결시·미응시 등)은 세지 않고, 몇 개를 뺐는지 알려준다.
#>
function Get-ScoreSummary {
    param(
        [Parameter(Mandatory)][string] $File,
        [Parameter(Mandatory)][string] $ScoreColumn,
        [string] $GroupColumn,
        [int]    $Sheet     = 1,
        [int]    $HeaderRow = 1
    )

    $rows = @(Read-TeavelTable -Path $File -Sheet $Sheet -HeaderRow $HeaderRow)
    if ($rows.Count -eq 0) { throw '표에 행이 없습니다. 시트 번호와 머리글 행을 확인해 주세요.' }

    [void](Get-TeavelColumnValue -Row $rows[0] -Column $ScoreColumn)
    if ($GroupColumn) { [void](Get-TeavelColumnValue -Row $rows[0] -Column $GroupColumn) }

    # 한 묶음의 통계를 한 줄로.
    function Format-Stat {
        param([string] $Label, [object[]] $Values)

        $nums = New-Object System.Collections.Generic.List[double]
        $bad  = 0
        foreach ($v in $Values) {
            $d = 0.0
            if ($null -ne $v -and [double]::TryParse([string]$v, [ref]$d)) { $nums.Add($d) } else { $bad++ }
        }
        if ($nums.Count -eq 0) { return "$Label — 숫자로 된 점수가 없습니다." }

        $sorted = @($nums | Sort-Object)
        $n      = $sorted.Count
        $mean   = ($sorted | Measure-Object -Average).Average
        $median = if ($n % 2 -eq 1) { $sorted[[int]($n / 2)] } else { ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2 }
        $sd     = if ($n -gt 1) {
                      [Math]::Sqrt((($sorted | ForEach-Object { [Math]::Pow($_ - $mean, 2) } | Measure-Object -Sum).Sum) / ($n - 1))
                  } else { 0 }

        $line = "$Label — {0}명 · 평균 {1:N2} · 표준편차 {2:N2} · 최고 {3:N1} · 최저 {4:N1} · 중앙값 {5:N1}" -f `
                $n, $mean, $sd, $sorted[-1], $sorted[0], $median
        if ($bad -gt 0) { $line += "  (숫자가 아닌 칸 $($bad)개 제외)" }
        $line
    }

    $details = New-Object System.Collections.Generic.List[string]
    $details.Add((Format-Stat -Label '전체' -Values @($rows | ForEach-Object { $_.$ScoreColumn })))

    if ($GroupColumn) {
        $details.Add('')
        foreach ($g in ($rows | Group-Object -Property $GroupColumn | Sort-Object Name)) {
            $label = if ([string]::IsNullOrWhiteSpace($g.Name)) { '값없음' } else { $g.Name }
            $details.Add((Format-Stat -Label $label -Values @($g.Group | ForEach-Object { $_.$ScoreColumn })))
        }
    }

    New-TeavelResult -Message "'$ScoreColumn' 통계입니다. (파일은 바꾸지 않았습니다)" -Details $details
}

<#
.SYNOPSIS
    엑셀 파일을 CSV·XLSX·PDF 로 바꾼다.
.DESCRIPTION
    CSV 는 한국어 Windows 의 엑셀에서 바로 열리도록 UTF-8 BOM 을 붙여 저장한다
    (BOM 이 없으면 엑셀이 한글을 깨뜨린다).
#>
function Convert-Workbook {
    param(
        [Parameter(Mandatory)][string] $File,
        [Parameter(Mandatory)][ValidateSet('csv', 'xlsx', 'pdf')][string] $To,
        [string] $Output
    )

    if (-not (Test-Path -LiteralPath $File)) { throw "파일을 찾지 못했습니다: $File" }

    $srcFull = [IO.Path]::GetFullPath($File)
    $outFull = if ([string]::IsNullOrWhiteSpace($Output)) {
        [IO.Path]::ChangeExtension($srcFull, ".$To")
    } else {
        [IO.Path]::GetFullPath($Output)
    }

    if ($srcFull -ieq $outFull) { throw '원본과 저장할 파일이 같습니다. 다른 이름을 지정해 주세요.' }

    $parent = Split-Path -Parent $outFull
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

    $excel = $null; $book = $null
    try {
        $excel = New-TeavelOfficeApp -Kind Excel
        $book  = $excel.Workbooks.Open($srcFull, $false, $true)

        switch ($To) {
            'pdf' {
                # 0 = xlTypePDF
                $book.ExportAsFixedFormat(0, $outFull)
            }
            'csv' {
                # 62 = xlCSVUTF8 (Office 2016 이상). 없는 버전이면 6(xlCSV)로 떨어뜨린다.
                try   { $book.SaveAs($outFull, 62) }
                catch { $book.SaveAs($outFull, 6) }
            }
            'xlsx' {
                $book.SaveAs($outFull, 51)   # 51 = xlOpenXMLWorkbook
            }
        }
    }
    finally {
        if ($null -ne $book) { try { $book.Close($false) } catch { } ; Remove-TeavelComObject $book }
        Close-TeavelOfficeApp $excel
    }

    if (-not (Test-Path -LiteralPath $outFull)) { throw "변환은 끝났는데 결과 파일이 없습니다: $outFull" }

    $size = [Math]::Round((Get-Item -LiteralPath $outFull).Length / 1KB, 1)
    New-TeavelResult -Message "$To 파일로 바꿨습니다." -Details @("저장: $outFull  (${size} KB)")
}

Export-ModuleMember -Function Get-WorkbookInfo, Merge-Workbook, Split-WorkbookByColumn, Get-ScoreSummary, Convert-Workbook
