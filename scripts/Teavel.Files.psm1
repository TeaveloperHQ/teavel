<#
    파일·폴더 정리.

    학생 제출물을 다루므로 원칙이 하나 있다: 무엇도 조용히 덮어쓰지 않는다.
    이름이 부딪히면 건너뛰고, 무엇을 건너뛰었는지 반드시 알린다.
#>

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    폴더 안 파일 이름에서 특정 글자를 찾아 바꾼다.
.DESCRIPTION
    Find 는 정규식이 아니라 글자 그대로 찾는다("과제(1)" 처럼 괄호가 들어가도 그대로 동작).
    바꾼 이름이 이미 있는 파일과 겹치면 그 파일은 건드리지 않고 넘어간다.
#>
function Rename-FileBatch {
    param(
        [Parameter(Mandatory)][string] $Folder,
        [Parameter(Mandatory)][string] $Find,
        [AllowEmptyString()][string] $ReplaceWith = '',
        [string] $Pattern = '*',
        [bool]   $Recurse = $false
    )

    if (-not (Test-Path -LiteralPath $Folder)) { throw "폴더를 찾지 못했습니다: $Folder" }
    if ([string]::IsNullOrEmpty($Find))        { throw '바꿀 글자를 알려주셔야 합니다.' }

    $files = @(Get-ChildItem -LiteralPath $Folder -Filter $Pattern -File -Recurse:$Recurse |
               Where-Object { $_.Name -notlike '~$*' })

    $renamed = New-Object System.Collections.Generic.List[string]
    $skipped = New-Object System.Collections.Generic.List[string]

    foreach ($f in $files) {
        if ($f.Name -notlike "*$Find*") { continue }

        $newName = $f.Name.Replace($Find, $ReplaceWith)
        if ([string]::IsNullOrWhiteSpace([IO.Path]::GetFileNameWithoutExtension($newName))) {
            $skipped.Add("$($f.Name) — 바꾸면 이름이 비어 버립니다")
            continue
        }
        if ($newName -eq $f.Name) { continue }

        $target = Join-Path $f.DirectoryName $newName
        if (Test-Path -LiteralPath $target) {
            $skipped.Add("$($f.Name) — '$newName' 이 이미 있습니다")
            continue
        }

        try {
            Rename-Item -LiteralPath $f.FullName -NewName $newName -ErrorAction Stop
            $renamed.Add("$($f.Name)  →  $newName")
        } catch {
            $skipped.Add("$($f.Name) — $($_.Exception.Message)")
        }
    }

    if ($renamed.Count -eq 0 -and $skipped.Count -eq 0) {
        return New-TeavelResult -Message "이름에 '$Find' 이(가) 들어간 파일이 없습니다. 바꾼 것이 없습니다."
    }

    $details = New-Object System.Collections.Generic.List[string]
    foreach ($r in $renamed) { $details.Add($r) }
    if ($skipped.Count -gt 0) {
        $details.Add('')
        $details.Add('건너뛴 파일:')
        foreach ($s in $skipped) { $details.Add("  $s") }
    }

    New-TeavelResult -Message "$($renamed.Count)개 파일 이름을 바꿨습니다." -Details $details
}

<#
.SYNOPSIS
    파일 이름에서 학번을 찾아 학번별 폴더로 옮긴다.
.DESCRIPTION
    학번을 못 찾은 파일은 '학번없음' 폴더로 모은다 — 그냥 두면 교사가 빠뜨린 걸 모른다.
#>
function Group-FileByStudentId {
    param(
        [Parameter(Mandatory)][string] $Folder,
        [string] $IdPattern = '\d{5}',
        [bool]   $Copy      = $false
    )

    if (-not (Test-Path -LiteralPath $Folder)) { throw "폴더를 찾지 못했습니다: $Folder" }

    try { $regex = [regex]::new($IdPattern) }
    catch { throw "학번 형태('$IdPattern')를 이해하지 못했습니다: $($_.Exception.Message)" }

    $files = @(Get-ChildItem -LiteralPath $Folder -File | Where-Object { $_.Name -notlike '~$*' })
    if ($files.Count -eq 0) { throw "'$Folder' 안에 파일이 없습니다. (하위 폴더는 보지 않습니다)" }

    $moved   = 0
    $noId    = 0
    $skipped = New-Object System.Collections.Generic.List[string]
    $perId   = @{}

    foreach ($f in $files) {
        $m  = $regex.Match($f.BaseName)
        $id = if ($m.Success) { $m.Value } else { $null }

        $folderName = if ($id) { $id } else { '학번없음' }
        $destDir    = Join-Path $Folder $folderName
        if (-not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }

        $dest = Join-Path $destDir $f.Name
        if (Test-Path -LiteralPath $dest) {
            $skipped.Add("$($f.Name) — $folderName 폴더에 같은 이름이 이미 있습니다")
            continue
        }

        try {
            if ($Copy) { Copy-Item -LiteralPath $f.FullName -Destination $dest -ErrorAction Stop }
            else       { Move-Item -LiteralPath $f.FullName -Destination $dest -ErrorAction Stop }

            if ($id) { $moved++; $perId[$id] = 1 + $(if ($perId.ContainsKey($id)) { $perId[$id] } else { 0 }) }
            else     { $noId++ }
        } catch {
            $skipped.Add("$($f.Name) — $($_.Exception.Message)")
        }
    }

    $verb = if ($Copy) { '복사' } else { '이동' }
    $details = New-Object System.Collections.Generic.List[string]
    $details.Add("학번 $($perId.Count)개 폴더로 $($moved)개 파일을 $verb 했습니다.")
    if ($noId -gt 0)          { $details.Add("학번을 못 찾은 파일 $($noId)개는 '학번없음' 폴더에 넣었습니다.") }
    if ($skipped.Count -gt 0) {
        $details.Add('')
        $details.Add('건너뛴 파일:')
        foreach ($s in $skipped) { $details.Add("  $s") }
    }

    New-TeavelResult -Message "$($moved + $noId)개 파일을 정리했습니다." -Details $details
}

<#
.SYNOPSIS
    명단과 제출물 폴더를 맞춰 보고 안 낸 학생을 알려준다.
.DESCRIPTION
    파일 이름 어딘가에 학번이 들어 있으면 낸 것으로 본다(하위 폴더까지 본다).
    파일을 전혀 건드리지 않는다.
#>
function Find-MissingSubmission {
    param(
        [Parameter(Mandatory)][string] $Folder,
        [Parameter(Mandatory)][string] $RosterFile,
        [Parameter(Mandatory)][string] $IdColumn,
        [string] $NameColumn,
        [int]    $Sheet     = 1,
        [int]    $HeaderRow = 1
    )

    if (-not (Test-Path -LiteralPath $Folder)) { throw "폴더를 찾지 못했습니다: $Folder" }

    $roster = @(Read-TeavelTable -Path $RosterFile -Sheet $Sheet -HeaderRow $HeaderRow)
    if ($roster.Count -eq 0) { throw '명단에 행이 없습니다. 시트 번호와 머리글 행을 확인해 주세요.' }

    [void](Get-TeavelColumnValue -Row $roster[0] -Column $IdColumn)
    if ($NameColumn) { [void](Get-TeavelColumnValue -Row $roster[0] -Column $NameColumn) }

    # 하위 폴더까지 포함해 이름을 한 덩어리로 모아 두고 학번이 들어 있는지만 본다.
    $names = @(Get-ChildItem -LiteralPath $Folder -File -Recurse |
               Where-Object { $_.Name -notlike '~$*' } |
               ForEach-Object { $_.Name })
    $haystack = ($names -join "`n")

    $missing  = New-Object System.Collections.Generic.List[string]
    $submitted = 0

    foreach ($r in $roster) {
        $rawId = [string](Get-TeavelColumnValue -Row $r -Column $IdColumn)
        if ([string]::IsNullOrWhiteSpace($rawId)) { continue }

        # 엑셀이 학번을 숫자로 읽어 "10203" 이 "10203.0" 이 되는 경우를 정리한다.
        $id = $rawId.Trim()
        if ($id -match '^\d+\.0+$') { $id = $id.Substring(0, $id.IndexOf('.')) }

        if ($haystack.Contains($id)) {
            $submitted++
        } else {
            $label = if ($NameColumn) {
                "$id  $([string](Get-TeavelColumnValue -Row $r -Column $NameColumn))"
            } else { $id }
            $missing.Add($label)
        }
    }

    $total = $submitted + $missing.Count
    if ($missing.Count -eq 0) {
        return New-TeavelResult -Message "$($total)명 모두 냈습니다." -Details @("확인한 파일 $($names.Count)개")
    }

    $details = New-Object System.Collections.Generic.List[string]
    $details.Add("낸 학생 $($submitted)명 / 전체 $($total)명")
    $details.Add('')
    $details.Add('안 낸 학생:')
    foreach ($m in $missing) { $details.Add("  $m") }

    New-TeavelResult -Message "$($missing.Count)명이 안 냈습니다." -Details $details
}

<#
.SYNOPSIS
    폴더 안의 zip 파일을 모두 푼다.
.DESCRIPTION
    각 압축 파일은 같은 이름의 폴더에 푼다. 이미 그 폴더가 있으면 건너뛴다.
    DeleteAfter 는 압축 해제가 성공한 파일만 지운다.
#>
function Expand-ArchiveBatch {
    param(
        [Parameter(Mandatory)][string] $Folder,
        [string] $OutputFolder,
        [bool]   $DeleteAfter = $false
    )

    if (-not (Test-Path -LiteralPath $Folder)) { throw "폴더를 찾지 못했습니다: $Folder" }

    $baseDir = if ([string]::IsNullOrWhiteSpace($OutputFolder)) { $Folder } else { [IO.Path]::GetFullPath($OutputFolder) }
    if (-not (Test-Path -LiteralPath $baseDir)) { New-Item -ItemType Directory -Path $baseDir -Force | Out-Null }

    $archives = @(Get-ChildItem -LiteralPath $Folder -Filter '*.zip' -File)
    if ($archives.Count -eq 0) { throw "'$Folder' 안에 zip 파일이 없습니다." }

    $done    = New-Object System.Collections.Generic.List[string]
    $skipped = New-Object System.Collections.Generic.List[string]
    $deleted = 0

    foreach ($a in $archives) {
        $dest = Join-Path $baseDir $a.BaseName
        if (Test-Path -LiteralPath $dest) {
            $skipped.Add("$($a.Name) — '$($a.BaseName)' 폴더가 이미 있습니다")
            continue
        }

        try {
            Expand-Archive -LiteralPath $a.FullName -DestinationPath $dest -Force -ErrorAction Stop
            $count = @(Get-ChildItem -LiteralPath $dest -Recurse -File).Count
            $done.Add("$($a.Name) → $($a.BaseName)\  ($($count)개)")

            if ($DeleteAfter) {
                Remove-Item -LiteralPath $a.FullName -Force -ErrorAction Stop
                $deleted++
            }
        } catch {
            $skipped.Add("$($a.Name) — $($_.Exception.Message)")
        }
    }

    $details = New-Object System.Collections.Generic.List[string]
    foreach ($d in $done) { $details.Add($d) }
    if ($deleted -gt 0) {
        $details.Add('')
        $details.Add("압축 파일 $($deleted)개를 지웠습니다.")
    }
    if ($skipped.Count -gt 0) {
        $details.Add('')
        $details.Add('건너뛴 파일:')
        foreach ($s in $skipped) { $details.Add("  $s") }
    }

    New-TeavelResult -Message "압축 $($done.Count)개를 풀었습니다." -Details $details
}

Export-ModuleMember -Function Rename-FileBatch, Group-FileByStudentId, Find-MissingSubmission, Expand-ArchiveBatch
