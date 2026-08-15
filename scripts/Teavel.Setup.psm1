<#
    PC 세팅 — Windows 계정 연결, 프린터.

    Teavel 의 본업이 여기 있다. 선생님이 못 하는 것을 대신 하거나, 왜 안 되는지 설명한다.
#>

Set-StrictMode -Version Latest

# ═══════════════════════════ Windows 판과 계정 ═══════════════════════════

<#
.SYNOPSIS
    Windows 판(Home/Pro)과 학교 계정 연결 상태를 알아 온다.
.DESCRIPTION
    다른 함수들이 쓰는 원자료. 교사에게 보여줄 문장은 Get-TeavelWindowsInfo·Get-TeavelAccountGuide 가 만든다.
#>
function Get-TeavelSystemFacts {
    param()

    $cv = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $p  = Get-ItemProperty -Path $cv -ErrorAction SilentlyContinue

    $editionId   = if ($p -and $p.PSObject.Properties['EditionID'])      { [string]$p.EditionID }      else { '' }
    $productName = if ($p -and $p.PSObject.Properties['ProductName'])    { [string]$p.ProductName }    else { 'Windows' }
    $display     = if ($p -and $p.PSObject.Properties['DisplayVersion']) { [string]$p.DisplayVersion } else { '' }

    # EditionID 가 Core 로 시작하면 Home 이다(CoreN, CoreSingleLanguage 등 변종 포함).
    $isHome = $editionId -like 'Core*'

    # 장치를 조직에 연결(Entra ID 조인)하려면 Pro·Enterprise·Education 이어야 한다.
    # Home 은 기능 자체가 없어 설정 화면에 메뉴가 나오지도 않는다.
    $canJoinDevice = (-not $isHome) -and ($editionId -ne '')

    $edition = if ($isHome) { 'Home' }
               elseif ($editionId -like 'Professional*') { 'Pro' }
               elseif ($editionId -like 'Education*')    { 'Education' }
               elseif ($editionId -like 'Enterprise*')   { 'Enterprise' }
               else { $editionId }

    # ── 지금 계정이 어떻게 붙어 있는지 ──
    $azureJoined = $false; $domainJoined = $false; $workplaceJoined = $false
    $accounts = New-Object System.Collections.Generic.List[string]

    try {
        foreach ($line in @(dsregcmd /status 2>$null)) {
            if ($line -match '^\s*AzureAdJoined\s*:\s*(\S+)')   { $azureJoined     = ($Matches[1] -eq 'YES') }
            if ($line -match '^\s*DomainJoined\s*:\s*(\S+)')    { $domainJoined    = ($Matches[1] -eq 'YES') }
            if ($line -match '^\s*WorkplaceJoined\s*:\s*(\S+)') { $workplaceJoined = ($Matches[1] -eq 'YES') }
            if ($line -match '\s*(?:WorkplaceUPN|Executing Account Name|UPN)\s*:\s*(\S+@\S+)') {
                if (-not $accounts.Contains($Matches[1])) { $accounts.Add($Matches[1]) }
            }
        }
    } catch { }

    [PSCustomObject]@{
        EditionId       = $editionId
        Edition         = $edition
        ProductName     = $productName
        DisplayVersion  = $display
        IsHome          = $isHome
        CanJoinDevice   = $canJoinDevice
        AzureAdJoined   = $azureJoined       # 장치가 조직에 연결됨 — Windows 로그인 자체가 학교 계정
        DomainJoined    = $domainJoined      # 교내 도메인 가입
        WorkplaceJoined = $workplaceJoined   # 계정만 추가됨 — 앱만 연결(개인 PC 방식)
        Accounts        = @($accounts)
        AnyConnected    = ($azureJoined -or $domainJoined -or $workplaceJoined)
    }
}

<#
.SYNOPSIS
    이 컴퓨터가 Home 인지 Pro 인지, 그게 무슨 뜻인지 알려준다. 아무것도 바꾸지 않는다.
#>
function Get-TeavelWindowsInfo {
    param()

    $f = Get-TeavelSystemFacts
    $d = New-Object System.Collections.Generic.List[string]

    $d.Add("$($f.ProductName)  $($f.DisplayVersion)")
    $d.Add('')

    if ($f.IsHome) {
        $d.Add('이 컴퓨터는 Home 판입니다.')
        $d.Add('')
        $d.Add('  · 학교 계정을 "추가" 해서 원드라이브·아웃룩·팀즈를 쓰는 것 — 됩니다.')
        $d.Add('  · 컴퓨터 자체를 학교에 "연결" 하는 것 — 안 됩니다. Home 에는 그 기능이 없습니다.')
        $d.Add('')
        $d.Add('학교에서 "이 컴퓨터를 조직에 연결하세요" 라고 안내받으셨다면,')
        $d.Add('Home 판이라 그 메뉴가 없다고 전산 담당 선생님께 말씀하시면 됩니다.')
        $d.Add('계정 추가만으로도 수업에 필요한 것은 대부분 됩니다.')
    } else {
        $d.Add("이 컴퓨터는 $($f.Edition) 판입니다.")
        $d.Add('')
        $d.Add('  · 학교 계정 "추가" — 됩니다 (개인 컴퓨터에 알맞음)')
        $d.Add('  · 컴퓨터를 학교에 "연결" — 됩니다 (학교에서 지급한 컴퓨터에 알맞음)')
        $d.Add('')
        $d.Add('두 가지는 결과가 다릅니다. "계정 안내" 를 실행하면 어느 쪽이 맞는지 알려드립니다.')
    }

    New-TeavelResult -Message "Windows $($f.Edition) 판입니다." -Details $d
}

<#
.SYNOPSIS
    이 컴퓨터에서 학교 계정을 어떻게 넣어야 하는지 상황에 맞게 알려준다.
.DESCRIPTION
    Windows 판과 컴퓨터 주인에 따라 답이 다르다. 잘못 고르면 개인 컴퓨터를 학교가 관리하게 되거나,
    Home 에 없는 메뉴를 찾아 헤매게 된다.
.PARAMETER Ownership
    school = 학교에서 지급한 컴퓨터 · personal = 내 개인 컴퓨터 · unknown = 아직 모름
#>
function Get-TeavelAccountGuide {
    param(
        [ValidateSet('school', 'personal', 'unknown')]
        [string] $Ownership = 'unknown'
    )

    $f = Get-TeavelSystemFacts
    $d = New-Object System.Collections.Generic.List[string]

    $d.Add("Windows $($f.Edition) 판")
    if     ($f.AzureAdJoined)   { $d.Add('지금 상태: 이 컴퓨터가 학교에 연결돼 있습니다.') }
    elseif ($f.DomainJoined)    { $d.Add('지금 상태: 교내 도메인에 가입돼 있습니다.') }
    elseif ($f.WorkplaceJoined) { $d.Add('지금 상태: 학교 계정이 추가돼 있습니다(앱 연결).') }
    else                        { $d.Add('지금 상태: 학교 계정이 연결돼 있지 않습니다.') }
    if ($f.Accounts.Count -gt 0) { $d.Add("연결된 계정: $($f.Accounts -join ', ')") }
    $d.Add('')

    if ($f.AnyConnected) {
        $d.Add('이미 연결돼 있어 더 하실 일이 없습니다.')
        $d.Add('원드라이브나 아웃룩이 여전히 로그인을 물으면 그 앱만 따로 점검하세요.')
        return New-TeavelResult -Message '학교 계정이 이미 연결돼 있습니다.' -Details $d
    }

    $d.Add('학교 계정을 넣는 방법은 두 가지이고, 결과가 다릅니다.')
    $d.Add('')
    $d.Add('  ① 계정 추가 — 원드라이브·아웃룩·팀즈만 학교 계정으로 이어집니다.')
    $d.Add('               Windows 로그인은 지금 쓰시는 그대로입니다.')
    $d.Add('               학교가 이 컴퓨터를 관리하지 않습니다.')
    $d.Add('')
    $d.Add('  ② 장치 연결 — Windows 로그인부터 학교 계정으로 바뀝니다.')
    $d.Add('               학교가 이 컴퓨터에 정책을 걸 수 있습니다(원격 초기화 포함).')
    $d.Add('               Pro·Education 판에서만 됩니다.')
    $d.Add('')

    if ($f.IsHome) {
        $d.Add('이 컴퓨터는 Home 판이라 ② 는 아예 불가능합니다. ① 로 하시면 됩니다.')
        $d.Add('')
        $d.Add('  [설정] → [계정] → [회사 또는 학교 액세스] → [연결]')
        $d.Add('  → 학교 메일 주소 입력 → 비밀번호 입력 → 끝')
        $d.Add('')
        $d.Add('※ 화면 아래에 "이 장치를 Microsoft Entra ID에 조인" 같은 파란 글씨가')
        $d.Add('   보이지 않는 것이 정상입니다. Home 에는 그 기능이 없습니다.')
        $msg = 'Home 판입니다. 계정 추가(①)로 하세요.'
    }
    elseif ($Ownership -eq 'school') {
        $d.Add('학교에서 지급한 컴퓨터라면 ② 장치 연결이 맞습니다.')
        $d.Add('학교가 관리하는 것이 정상이고, 그래야 학교 자원에 제대로 접근됩니다.')
        $d.Add('')
        $d.Add('  [설정] → [계정] → [회사 또는 학교 액세스] → [연결]')
        $d.Add('  → 화면 아래 [이 장치를 Microsoft Entra ID에 조인] 을 누르세요')
        $d.Add('  → 학교 메일 주소와 비밀번호 입력')
        $d.Add('')
        $d.Add('※ 위쪽 입력칸에 주소만 넣고 [다음] 을 누르면 ① 이 됩니다. 헷갈리기 쉬운 자리입니다.')
        $msg = '학교 지급 컴퓨터입니다. 장치 연결(②)로 하세요.'
    }
    elseif ($Ownership -eq 'personal') {
        $d.Add('개인 컴퓨터라면 ① 계정 추가가 맞습니다.')
        $d.Add('② 로 하면 학교가 선생님의 개인 컴퓨터를 관리하게 됩니다 — 권하지 않습니다.')
        $d.Add('')
        $d.Add('  [설정] → [계정] → [회사 또는 학교 액세스] → [연결]')
        $d.Add('  → 학교 메일 주소 입력 → 비밀번호 입력 → 끝')
        $d.Add('')
        $d.Add('※ 화면 아래 파란 글씨(조인·도메인 참가)는 누르지 마세요.')
        $msg = '개인 컴퓨터입니다. 계정 추가(①)로 하세요.'
    }
    else {
        $d.Add('어느 쪽이 맞는지는 이 컴퓨터가 누구 것이냐에 달렸습니다.')
        $d.Add('')
        $d.Add('  학교에서 받은 컴퓨터  →  ② 장치 연결')
        $d.Add('  내가 산 개인 컴퓨터   →  ① 계정 추가')
        $d.Add('')
        $d.Add('잘 모르시겠으면 ① 계정 추가로 하세요. 되돌리기 쉽고, 수업에 필요한 것은 다 됩니다.')
        $msg = '이 컴퓨터가 학교 것인지 개인 것인지 알려 주시면 정확히 안내해 드립니다.'
    }

    New-TeavelResult -Message $msg -Details $d
}

<#
.SYNOPSIS
    [회사 또는 학교 액세스] 설정 화면을 연다.
.DESCRIPTION
    비밀번호가 필요해 대신 해 드릴 수 없다. 대신 교사가 스스로 찾기 가장 어려운 화면을 정확히 열어 준다.
#>
function Open-TeavelAccountSetting {
    param()

    Start-Process 'ms-settings:workplace'

    $f = Get-TeavelSystemFacts
    $d = New-Object System.Collections.Generic.List[string]
    $d.Add('[연결] 을 누르고 학교 메일 주소와 비밀번호를 넣으세요.')
    if ($f.IsHome) {
        $d.Add('')
        $d.Add('Home 판이라 아래쪽 "조인" 파란 글씨는 없습니다. 그게 정상입니다.')
    }
    $d.Add('')
    $d.Add("마친 뒤 '점검' 을 다시 실행하면 확인됩니다.")

    New-TeavelResult -Message '설정 화면을 띄웠습니다.' -Details $d
}

<#
.SYNOPSIS
    이 컴퓨터의 프린터 상태를 알려준다. 아무것도 바꾸지 않는다.
#>
function Get-PrinterStatus {
    param()

    $printers = @(Get-Printer -ErrorAction Stop)
    if ($printers.Count -eq 0) {
        return New-TeavelResult -Message '이 컴퓨터에 프린터가 하나도 없습니다.' -Details @(
            '학교에서 쓰는 프린터 주소(예: \\print-server\3층복도)를 알아 오시면 등록해 드립니다.'
        )
    }

    # 기본 프린터는 WMI 로 봐야 확실하다(Get-Printer 에는 기본 여부가 없다).
    $default = $null
    try { $default = (Get-CimInstance -ClassName Win32_Printer -Filter 'Default = TRUE').Name } catch { }

    # "마지막에 쓴 프린터를 기본으로" 설정 — 켜져 있으면 기본 프린터가 계속 바뀐다.
    $legacyKey = 'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows'
    $managedByWindows = $false
    try {
        $v = Get-ItemProperty -Path $legacyKey -Name 'LegacyDefaultPrinterMode' -ErrorAction SilentlyContinue
        # 0(또는 값 없음) = Windows 가 기본 프린터를 관리함
        $managedByWindows = ($null -eq $v) -or ($v.LegacyDefaultPrinterMode -eq 0)
    } catch { }

    $details = New-Object System.Collections.Generic.List[string]
    foreach ($p in ($printers | Sort-Object Name)) {
        $mark = if ($p.Name -eq $default) { '★ 기본' } else { '      ' }
        $where = if ($p.Type -eq 'Connection') { $p.ComputerName } else { $p.PortName }
        $details.Add("$mark  $($p.Name)   [$where]")
    }

    if ($managedByWindows) {
        $details.Add('')
        $details.Add('! Windows 가 "마지막에 쓴 프린터"를 기본으로 바꾸고 있습니다.')
        $details.Add('  기본 프린터를 정해도 계속 바뀝니다. 기본 프린터를 지정하면 이 설정도 꺼 드립니다.')
    }

    $msg = if ($default) { "프린터 $($printers.Count)대. 기본은 '$default' 입니다." }
           else          { "프린터 $($printers.Count)대. 기본 프린터가 정해져 있지 않습니다." }

    New-TeavelResult -Message $msg -Details $details
}

<#
.SYNOPSIS
    기본 프린터를 정한다.
.DESCRIPTION
    Windows 가 기본 프린터를 제멋대로 바꾸지 않도록 '마지막에 쓴 프린터' 기능도 함께 끈다.
    이걸 안 끄면 지정해도 다음 인쇄 뒤에 되돌아간다.
#>
function Set-TeavelDefaultPrinter {
    param(
        [Parameter(Mandatory)][string] $Name
    )

    $printers = @(Get-Printer -ErrorAction Stop)
    $match = $printers | Where-Object { $_.Name -eq $Name }

    if (-not $match) {
        # 정확히 같은 이름이 없으면 비슷한 것을 찾아 알려 준다(교사가 이름을 대충 말한다).
        $similar = @($printers | Where-Object { $_.Name -like "*$Name*" })
        if ($similar.Count -eq 1) {
            $match = $similar[0]
        } else {
            $all = ($printers | ForEach-Object { $_.Name }) -join ', '
            throw "'$Name' 이라는 프린터가 없습니다. 이 컴퓨터의 프린터: $all"
        }
    }

    $printer = Get-CimInstance -ClassName Win32_Printer -Filter "Name='$($match.Name -replace "'","''")'"
    if (-not $printer) { throw "'$($match.Name)' 을(를) 설정하지 못했습니다." }
    [void](Invoke-CimMethod -InputObject $printer -MethodName SetDefaultPrinter)

    $details = New-Object System.Collections.Generic.List[string]

    # 1 = 사용자가 정한 기본 프린터를 유지 (0/없음 = Windows 가 마음대로 바꿈)
    try {
        $key = 'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows'
        if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
        Set-ItemProperty -Path $key -Name 'LegacyDefaultPrinterMode' -Value 1 -Type DWord
        $details.Add('"마지막에 쓴 프린터를 기본으로" 설정을 껐습니다 — 이제 바뀌지 않습니다.')
    } catch {
        $details.Add('! 기본 프린터가 다시 바뀔 수 있습니다(설정을 끄지 못했습니다).')
    }

    New-TeavelResult -Message "기본 프린터를 '$($match.Name)' 으로 정했습니다." -Details $details
}

<#
.SYNOPSIS
    프린터를 추가한다. 공유 프린터 또는 IP 프린터.
.DESCRIPTION
    학교는 대개 공유 프린터(\\서버\이름)를 쓴다. 이 경우 드라이버가 서버에서 따라오므로
    교사가 드라이버를 따로 구할 필요가 없다 — 그래서 이 길을 먼저 권한다.
    IP 로 직접 붙일 때는 드라이버가 이미 이 컴퓨터에 있어야 한다.
.PARAMETER Path
    공유 프린터 경로. 예: \\print-server\3층복도
.PARAMETER Address
    IP 프린터 주소. 예: 192.168.0.50
.PARAMETER Name
    IP 프린터일 때 붙일 이름.
.PARAMETER DriverName
    IP 프린터일 때 쓸 드라이버 이름. 비우면 이 컴퓨터에 있는 드라이버 목록을 알려준다.
#>
function Add-TeavelPrinter {
    param(
        [string] $Path,
        [string] $Address,
        [string] $Name,
        [string] $DriverName
    )

    if ($Path) {
        if ($Path -notmatch '^\\\\[^\\]+\\.+') {
            throw "공유 프린터 경로는 \\서버이름\프린터이름 형태여야 합니다. (받은 값: $Path)"
        }
        if (@(Get-Printer -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq $Path }).Count -gt 0) {
            return New-TeavelResult -Message '이미 등록된 프린터입니다.' -Details @($Path)
        }

        Add-Printer -ConnectionName $Path -ErrorAction Stop
        return New-TeavelResult -Message '공유 프린터를 등록했습니다.' -Details @(
            $Path,
            '드라이버는 서버에서 자동으로 받아 왔습니다.',
            "기본 프린터로 쓰시려면 '기본 프린터 설정' 을 이어서 하세요."
        )
    }

    if (-not $Address) { throw '공유 프린터 경로(\\서버\이름) 또는 IP 주소 중 하나는 알려 주셔야 합니다.' }
    if (-not $Name)    { throw 'IP 프린터는 붙일 이름이 필요합니다.' }

    if (-not $DriverName) {
        $drivers = @(Get-PrinterDriver -ErrorAction SilentlyContinue | ForEach-Object { $_.Name } | Sort-Object)
        throw ("IP 프린터는 드라이버 이름이 필요합니다. 이 컴퓨터에 있는 드라이버: " + ($drivers -join ', '))
    }

    $portName = "IP_$Address"
    if (@(Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue).Count -eq 0) {
        Add-PrinterPort -Name $portName -PrinterHostAddress $Address -ErrorAction Stop
    }

    Add-Printer -Name $Name -DriverName $DriverName -PortName $portName -ErrorAction Stop

    New-TeavelResult -Message "IP 프린터 '$Name' 을(를) 등록했습니다." -Details @(
        "주소: $Address",
        "드라이버: $DriverName"
    )
}

Export-ModuleMember -Function `
    Get-TeavelSystemFacts, Get-TeavelWindowsInfo, Get-TeavelAccountGuide, Open-TeavelAccountSetting, `
    Get-PrinterStatus, Set-TeavelDefaultPrinter, Add-TeavelPrinter
