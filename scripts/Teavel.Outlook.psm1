<#
    아웃룩 — 개인별 메일 만들기, 첨부 파일 모으기.

    메일 발송은 되돌릴 수 없다. 그래서 New-BulkMailDraft 는 기본이 '임시 보관함에만 저장'이고,
    실제 발송은 교사가 Send 를 명시적으로 켰을 때만 한다.
#>

Set-StrictMode -Version Latest

# Outlook 상수 (COM 열거형을 그대로 쓰면 PowerShell 에서 다루기 번거로워 직접 적는다)
$script:olMailItem    = 0
$script:olFolderInbox = 6

<#
.SYNOPSIS
    명단의 각 행마다 메일을 하나씩 만든다.
.DESCRIPTION
    제목·본문의 {열이름} 자리가 그 행의 값으로 바뀐다.
    기본은 임시 보관함(Drafts)에 저장만 한다 — 교사가 눈으로 확인한 뒤 보낼 수 있게.
    Send 를 켜면 곧바로 발송한다.
.PARAMETER Send
    $true 면 즉시 발송. 되돌릴 수 없으니 기본은 $false.
#>
function New-BulkMailDraft {
    param(
        [Parameter(Mandatory)][string] $RosterFile,
        [Parameter(Mandatory)][string] $ToColumn,
        [Parameter(Mandatory)][string] $Subject,
        [Parameter(Mandatory)][AllowEmptyString()][string] $BodyTemplate,
        [string] $AttachmentColumn,
        [bool]   $Send      = $false,
        [int]    $Sheet     = 1,
        [int]    $HeaderRow = 1
    )

    $rows = @(Read-TeavelTable -Path $RosterFile -Sheet $Sheet -HeaderRow $HeaderRow)
    if ($rows.Count -eq 0) { throw '명단에 행이 없습니다. 시트 번호와 머리글 행을 확인해 주세요.' }

    [void](Get-TeavelColumnValue -Row $rows[0] -Column $ToColumn)
    if ($AttachmentColumn) { [void](Get-TeavelColumnValue -Row $rows[0] -Column $AttachmentColumn) }

    $outlook = $null
    $made    = 0
    $skipped = New-Object System.Collections.Generic.List[string]

    try {
        $outlook = New-TeavelOfficeApp -Kind Outlook

        foreach ($r in $rows) {
            $to = ([string](Get-TeavelColumnValue -Row $r -Column $ToColumn)).Trim()

            if ([string]::IsNullOrWhiteSpace($to)) {
                $skipped.Add('메일 주소가 비어 있는 행')
                continue
            }
            # 주소가 아닌 값(전화번호 등)이 들어오면 발송 시점에 오류가 나므로 미리 거른다.
            if ($to -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') {
                $skipped.Add("$to — 메일 주소 형태가 아닙니다")
                continue
            }

            $mail = $null
            try {
                $mail = $outlook.CreateItem($script:olMailItem)
                $mail.To      = $to
                $mail.Subject = Expand-TeavelTemplate -Template $Subject      -Row $r
                $mail.Body    = Expand-TeavelTemplate -Template $BodyTemplate -Row $r

                if ($AttachmentColumn) {
                    $att = ([string](Get-TeavelColumnValue -Row $r -Column $AttachmentColumn)).Trim()
                    if (-not [string]::IsNullOrWhiteSpace($att)) {
                        if (Test-Path -LiteralPath $att) {
                            [void]$mail.Attachments.Add([IO.Path]::GetFullPath($att))
                        } else {
                            $skipped.Add("$to — 첨부 파일을 찾지 못해 첨부 없이 만들었습니다: $att")
                        }
                    }
                }

                if ($Send) { $mail.Send() } else { $mail.Save() }
                $made++
            }
            catch {
                $skipped.Add("$to — $($_.Exception.Message)")
            }
            finally {
                Remove-TeavelComObject $mail
            }
        }
    }
    finally {
        # Outlook 은 교사가 쓰던 창일 수 있으니 Quit 하지 않는다.
        Close-TeavelOfficeApp $outlook -NoQuit
    }

    $where = if ($Send) { '발송했습니다' } else { '임시 보관함에 저장했습니다' }
    $details = New-Object System.Collections.Generic.List[string]
    if (-not $Send) { $details.Add('아웃룩의 [임시 보관함]에서 확인한 뒤 보내세요.') }
    if ($skipped.Count -gt 0) {
        $details.Add('')
        $details.Add('처리하지 못한 행:')
        foreach ($s in $skipped) { $details.Add("  $s") }
    }

    New-TeavelResult -Message "메일 $($made)통을 $where." -Details $details
}

<#
.SYNOPSIS
    최근 받은 메일의 첨부 파일을 한 폴더에 모아 저장한다.
.DESCRIPTION
    파일 이름 앞에 보낸 사람을 붙여 누가 낸 것인지 알 수 있게 한다.
    같은 이름이 겹치면 뒤에 번호를 붙인다 — 덮어쓰지 않는다.
#>
function Save-MailAttachment {
    param(
        [Parameter(Mandatory)][string] $OutputFolder,
        [int]    $Days = 7,
        [string] $SubjectContains,
        [string] $SenderContains
    )

    if ($Days -lt 1) { throw '며칠 치를 볼지는 1 이상이어야 합니다.' }

    $outDir = [IO.Path]::GetFullPath($OutputFolder)
    if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

    $outlook = $null; $ns = $null; $inbox = $null; $items = $null
    $saved   = 0
    $mails   = 0
    $details = New-Object System.Collections.Generic.List[string]

    try {
        $outlook = New-TeavelOfficeApp -Kind Outlook
        $ns      = $outlook.GetNamespace('MAPI')
        $inbox   = $ns.GetDefaultFolder($script:olFolderInbox)

        # Restrict 의 날짜는 미국식 표기만 받는다 — 지역 설정과 무관하게 고정한다.
        $since  = (Get-Date).AddDays(-$Days).ToString('MM/dd/yyyy hh:mm tt', [Globalization.CultureInfo]::InvariantCulture)
        $items  = $inbox.Items.Restrict("[ReceivedTime] >= '$since'")

        foreach ($mail in $items) {
            try {
                # 메일이 아닌 항목(회의 요청 등)은 건너뛴다.
                if (-not $mail.PSObject.Properties['Attachments']) { continue }

                if ($SubjectContains -and ([string]$mail.Subject) -notlike "*$SubjectContains*") { continue }
                if ($SenderContains) {
                    $sender = "$($mail.SenderName) $($mail.SenderEmailAddress)"
                    if ($sender -notlike "*$SenderContains*") { continue }
                }
                if ($mail.Attachments.Count -eq 0) { continue }

                $mails++
                $who = ConvertTo-TeavelSafeFileName -Name ([string]$mail.SenderName)

                for ($i = 1; $i -le $mail.Attachments.Count; $i++) {
                    $att = $null
                    try {
                        $att = $mail.Attachments.Item($i)

                        # 서명에 딸린 그림 등은 거른다.
                        if ([string]$att.FileName -match '^image\d+\.(png|jpg|jpeg|gif)$') { continue }

                        $name = "${who}_$(ConvertTo-TeavelSafeFileName -Name ([string]$att.FileName))"
                        $path = Join-Path $outDir $name

                        $n = 1
                        while (Test-Path -LiteralPath $path) {
                            $stem = [IO.Path]::GetFileNameWithoutExtension($name)
                            $ext  = [IO.Path]::GetExtension($name)
                            $path = Join-Path $outDir "$stem($n)$ext"
                            $n++
                        }

                        $att.SaveAsFile($path)
                        $saved++
                    }
                    catch { $details.Add("첨부 하나를 저장하지 못했습니다: $($_.Exception.Message)") }
                    finally { Remove-TeavelComObject $att }
                }
            }
            catch { }   # 개별 메일 문제로 전체를 멈추지 않는다
            finally { Remove-TeavelComObject $mail }
        }
    }
    finally {
        Remove-TeavelComObject $items
        Remove-TeavelComObject $inbox
        Remove-TeavelComObject $ns
        Close-TeavelOfficeApp $outlook -NoQuit
    }

    $details.Insert(0, "최근 ${Days}일, 첨부가 있는 메일 $($mails)통을 살펴봤습니다.")
    $details.Add("저장 폴더: $outDir")

    New-TeavelResult -Message "첨부 파일 $($saved)개를 저장했습니다." -Details $details
}

Export-ModuleMember -Function New-BulkMailDraft, Save-MailAttachment
