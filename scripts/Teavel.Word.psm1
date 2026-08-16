<#
    워드 — 명단으로 문서 일괄 만들기, PDF 변환.

    Word COM 은 열어 둔 문서를 닫지 않으면 WINWORD.EXE 가 보이지 않는 채로 남는다.
    모든 함수가 finally 에서 문서를 먼저 닫고 응용 프로그램을 끝낸다.
#>

Set-StrictMode -Version Latest

# Word 상수
$script:wdFormatDocumentDefault = 16   # .docx
$script:wdFormatPDF             = 17
$script:wdReplaceAll            = 2
$script:wdFindContinue          = 1
$script:wdDoNotSaveChanges      = 0

<#
.SYNOPSIS
    한 문서 안의 모든 영역에서 글자를 바꾼다.
.DESCRIPTION
    본문뿐 아니라 머리글·바닥글까지 바꾼다. 상장·가정통신문 서식은 학교 이름이나
    날짜를 머리글에 두는 경우가 많아, 본문만 바꾸면 절반이 그대로 남는다.
#>
function Set-TeavelDocumentText {
    param(
        [Parameter(Mandatory)] $Document,
        [Parameter(Mandatory)][string] $Find,
        [Parameter(Mandatory)][AllowEmptyString()][string] $ReplaceWith
    )

    foreach ($story in $Document.StoryRanges) {
        $current = $story
        while ($null -ne $current) {
            $find = $null
            try {
                $find = $current.Find
                $find.ClearFormatting()
                $find.Replacement.ClearFormatting()
                $find.Text             = $Find
                $find.Replacement.Text = $ReplaceWith
                $find.Forward          = $true
                $find.Wrap             = $script:wdFindContinue
                $find.MatchCase        = $false
                $find.MatchWildcards   = $false
                [void]$find.Execute([ref]$Find, [ref]$false, [ref]$false, [ref]$false, [ref]$false, [ref]$false,
                                    [ref]$true, [ref]$script:wdFindContinue, [ref]$false,
                                    [ref]$ReplaceWith, [ref]$script:wdReplaceAll)
            }
            catch { }
            finally { Remove-TeavelComObject $find }

            $next = $null
            try { $next = $current.NextStoryRange } catch { }
            if ($current -ne $story) { Remove-TeavelComObject $current }
            $current = $next
        }
    }
}

<#
.SYNOPSIS
    워드 서식 파일과 명단 엑셀로 학생 수만큼 문서를 만든다.
.DESCRIPTION
    서식 안의 {이름}, {반} 같은 자리가 그 행의 값으로 바뀐다.
    한 학생에서 오류가 나도 나머지는 계속 만들고, 실패한 학생을 알려준다.
#>
function New-MergedDocument {
    param(
        [Parameter(Mandatory)][string] $TemplateFile,
        [Parameter(Mandatory)][string] $RosterFile,
        [Parameter(Mandatory)][string] $OutputFolder,
        [string] $NameColumn = '이름',
        [ValidateSet('docx', 'pdf')][string] $Format = 'docx',
        [int]    $Sheet     = 1,
        [int]    $HeaderRow = 1
    )

    if (-not (Test-Path -LiteralPath $TemplateFile)) { throw "서식 파일을 찾지 못했습니다: $TemplateFile" }

    $rows = @(Read-TeavelTable -Path $RosterFile -Sheet $Sheet -HeaderRow $HeaderRow)
    if ($rows.Count -eq 0) { throw '명단에 행이 없습니다. 시트 번호와 머리글 행을 확인해 주세요.' }

    $columns = @($rows[0].PSObject.Properties.Name)

    # 파일 이름에 쓸 열이 없으면 번호로 떨어뜨린다(멈추지 않는다).
    $useName = $columns -contains $NameColumn

    $outDir = [IO.Path]::GetFullPath($OutputFolder)
    if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

    $templateFull = [IO.Path]::GetFullPath($TemplateFile)
    $saveFormat   = if ($Format -eq 'pdf') { $script:wdFormatPDF } else { $script:wdFormatDocumentDefault }

    $word    = $null
    $made    = 0
    $failed  = New-Object System.Collections.Generic.List[string]

    try {
        $word = New-TeavelOfficeApp -Kind Word

        $index = 0
        foreach ($r in $rows) {
            $index++

            $label = if ($useName) { [string]$r.$NameColumn } else { "$index" }
            if ([string]::IsNullOrWhiteSpace($label)) { $label = "$index" }

            $doc = $null
            try {
                # 서식을 원본으로 삼아 새 문서를 연다(원본은 열리지 않으므로 안전하다).
                $doc = $word.Documents.Add($templateFull)

                foreach ($col in $columns) {
                    $value = [string]$r.$col
                    Set-TeavelDocumentText -Document $doc -Find "{$col}" -ReplaceWith $value
                }

                $safe = ConvertTo-TeavelSafeFileName -Name $label
                $path = Join-Path $outDir "$safe.$Format"

                $n = 1
                while (Test-Path -LiteralPath $path) {
                    $path = Join-Path $outDir "$safe($n).$Format"
                    $n++
                }

                $doc.SaveAs2($path, $saveFormat)
                $made++
            }
            catch {
                $failed.Add("$label — $($_.Exception.Message)")
            }
            finally {
                if ($null -ne $doc) {
                    try { $doc.Close($script:wdDoNotSaveChanges) } catch { }
                    Remove-TeavelComObject $doc
                }
            }
        }
    }
    finally {
        Close-TeavelOfficeApp $word
    }

    $details = New-Object System.Collections.Generic.List[string]
    if (-not $useName) {
        $details.Add("'$NameColumn' 열이 없어 파일 이름을 번호로 붙였습니다. (이 표의 열: $($columns -join ', '))")
    }
    if ($failed.Count -gt 0) {
        $details.Add('')
        $details.Add('만들지 못한 문서:')
        foreach ($f in $failed) { $details.Add("  $f") }
    }
    $details.Add('')
    $details.Add("저장 폴더: $outDir")

    New-TeavelResult -Message "문서 $($made)개를 만들었습니다. ($Format)" -Details $details
}

<#
.SYNOPSIS
    폴더 안의 워드 문서를 모두 PDF 로 바꾼다. 원본은 그대로 둔다.
#>
function Convert-DocumentToPdf {
    param(
        [Parameter(Mandatory)][string] $Folder,
        [string] $OutputFolder,
        [string] $Pattern = '*.doc*',
        [bool]   $Recurse = $false
    )

    if (-not (Test-Path -LiteralPath $Folder)) { throw "폴더를 찾지 못했습니다: $Folder" }

    $files = @(Get-ChildItem -LiteralPath $Folder -Filter $Pattern -File -Recurse:$Recurse |
               Where-Object { $_.Name -notlike '~$*' -and $_.Extension -imatch '^\.docx?$' })

    if ($files.Count -eq 0) { throw "'$Folder' 안에서 '$Pattern' 에 맞는 워드 문서를 찾지 못했습니다." }

    $outDir = if ([string]::IsNullOrWhiteSpace($OutputFolder)) { $null } else { [IO.Path]::GetFullPath($OutputFolder) }
    if ($outDir -and -not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

    $word    = $null
    $done    = 0
    $skipped = New-Object System.Collections.Generic.List[string]

    try {
        $word = New-TeavelOfficeApp -Kind Word

        foreach ($f in $files) {
            $target = if ($outDir) {
                Join-Path $outDir ([IO.Path]::GetFileNameWithoutExtension($f.Name) + '.pdf')
            } else {
                [IO.Path]::ChangeExtension($f.FullName, '.pdf')
            }

            if (Test-Path -LiteralPath $target) {
                $skipped.Add("$($f.Name) — 같은 이름의 PDF 가 이미 있습니다")
                continue
            }

            $doc = $null
            try {
                $doc = $word.Documents.Open($f.FullName, $false, $true)   # ConfirmConversions=false, ReadOnly=true
                $doc.SaveAs2($target, $script:wdFormatPDF)
                $done++
            }
            catch {
                $skipped.Add("$($f.Name) — $($_.Exception.Message)")
            }
            finally {
                if ($null -ne $doc) {
                    try { $doc.Close($script:wdDoNotSaveChanges) } catch { }
                    Remove-TeavelComObject $doc
                }
            }
        }
    }
    finally {
        Close-TeavelOfficeApp $word
    }

    $details = New-Object System.Collections.Generic.List[string]
    if ($outDir) { $details.Add("저장 폴더: $outDir") }
    else         { $details.Add('원본 파일 옆에 저장했습니다.') }
    if ($skipped.Count -gt 0) {
        $details.Add('')
        $details.Add('건너뛴 파일:')
        foreach ($s in $skipped) { $details.Add("  $s") }
    }

    New-TeavelResult -Message "$($done)개를 PDF 로 바꿨습니다." -Details $details
}

Export-ModuleMember -Function New-MergedDocument, Convert-DocumentToPdf
