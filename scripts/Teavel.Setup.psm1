<#
    PC 세팅 — Windows 업데이트, 계정 연결, 컴퓨터 이름.

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
    $display     = if ($p -and $p.PSObject.Properties['DisplayVersion']) { [string]$p.DisplayVersion } else { '' }
    $build       = 0
    if ($p -and $p.PSObject.Properties['CurrentBuild']) { [void][int]::TryParse([string]$p.CurrentBuild, [ref]$build) }

    # ProductName 을 믿으면 안 된다.
    # Windows 11 에서도 이 값이 "Windows 10 Pro" 로 남아 있다(마이크로소프트가 안 고쳤다).
    # 실기 확인: 빌드 26200 · 25H2 · Windows 11 인데 ProductName 은 "Windows 10 Pro".
    # 빌드 번호가 진실이다 — 22000 이상이면 Windows 11.
    $rawName = if ($p -and $p.PSObject.Properties['ProductName']) { [string]$p.ProductName } else { 'Windows' }
    $productName = if ($build -ge 22000 -and $rawName -match 'Windows 10') {
        $rawName -replace 'Windows 10', 'Windows 11'
    } else { $rawName }

    # EditionID 가 Core 로 시작하면 Home 이다(CoreN, CoreSingleLanguage 등 변종 포함).
    $isHome = $editionId -like 'Core*'

    # 판을 아예 읽지 못한 경우를 따로 둔다.
    # 이때 '어쨌든 Home 은 아니다' 로 넘기면 장치 연결(②)을 권하게 되는데,
    # 그건 학교가 그 컴퓨터를 관리하게 되는 길이다 — 모르는 채로 권할 방향이 아니다.
    $editionKnown = -not [string]::IsNullOrWhiteSpace($editionId)

    # 장치를 조직에 연결(Entra ID 조인)하려면 Pro·Enterprise·Education 이어야 한다.
    # Home 은 기능 자체가 없어 설정 화면에 메뉴가 나오지도 않는다.
    $canJoinDevice = $editionKnown -and (-not $isHome)

    $edition = if (-not $editionKnown) { '(확인 못 함)' }
               elseif ($isHome) { 'Home' }
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
        Build           = $build
        IsHome          = $isHome
        EditionKnown    = $editionKnown
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

    if (-not $f.EditionKnown) {
        $d.Add('이 컴퓨터의 Windows 판을 확인하지 못했습니다.')
        $d.Add('')
        $d.Add('학교 계정을 넣으실 때는 "계정 추가" 쪽으로 하세요 — 어느 판에서나 됩니다.')
    }
    elseif ($f.IsHome) {
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
        [string] $Ownership = 'unknown',

        [ValidateSet('school', 'personal', 'unknown')]
        [string] $Account = 'unknown'
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

    # 학교에서 M365 계정을 안 주는 경우가 있다(기간제·강사 등).
    # 그때는 '회사 또는 학교 액세스' 가 아니라 앱마다 개인 계정으로 로그인한다 —
    # Windows 판도 PC 주인도 따질 것 없이 답이 정해지므로 먼저 갈라낸다.
    if ($Account -eq 'personal') {
        $d.Add('학교 계정이 없으시면 개인 Microsoft 계정으로 앱을 쓰시면 됩니다.')
        $d.Add('Windows 로그인은 지금 쓰시는 그대로 두고, 앱만 연결합니다.')
        $d.Add('')
        $d.Add('  · 원드라이브 — 실행하고 개인 메일 주소로 로그인')
        $d.Add('  · 워드·엑셀  — 오른쪽 위 [로그인] 에 같은 주소로 로그인')
        $d.Add('  · 아웃룩     — 계정 추가에서 같은 주소 입력')
        $d.Add('')
        $d.Add('※ [회사 또는 학교 액세스] 는 쓰지 마세요. 그건 학교 계정용입니다.')
        $d.Add('※ 학생 개인정보가 든 자료는 되도록 개인 저장소에 두지 마세요.')
        $d.Add('   나중에 학교 계정이 생기면 그때 옮기시는 편이 좋습니다.')

        return New-TeavelResult -Message '개인 Microsoft 계정으로 앱을 연결하시면 됩니다.' -Details $d
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

    if (-not $f.EditionKnown) {
        # 판을 못 읽었다. 되돌리기 쉬운 ① 로 안내하고, 모른다는 사실을 숨기지 않는다.
        $d.Add('이 컴퓨터의 Windows 판을 확인하지 못했습니다.')
        $d.Add('그래서 ② 가 되는 컴퓨터인지 알 수 없습니다 — ① 로 하시는 편이 안전합니다.')
        $d.Add('① 은 어느 판에서나 되고, 마음에 안 들면 되돌리기도 쉽습니다.')
        $d.Add('')
        $d.Add('  [설정] → [계정] → [회사 또는 학교 액세스] → [연결]')
        $d.Add('  → 학교 메일 주소 입력 → 비밀번호 입력 → 끝')
        $msg = 'Windows 판을 확인하지 못했습니다. 계정 추가(①)로 하세요.'
    }
    elseif ($f.IsHome) {
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
        $d.Add('')
        $d.Add('학교에서 받은 메일 주소(M365 계정)가 없으시면 알려 주세요 —')
        $d.Add('개인 Microsoft 계정으로 쓰는 방법을 따로 안내해 드립니다.')
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
    학교 계정이 붙었는지만 짧게 알려준다. 아무것도 바꾸지 않는다.
.DESCRIPTION
    Get-TeavelAccountGuide 는 긴 안내문까지 만든다. 이 함수는 <b>지켜보기 위한 것</b>이라
    붙었는지 여부와 테넌트 id 만 준다.

    왜 필요한가 — 설정 창을 띄운 뒤 "다 하셨으면 Enter" 라고 하면, 선생님은 창 두 개를
    오가며 언제 Enter 를 눌러야 하는지 스스로 판단해야 한다. 몇 초마다 이걸 불러
    <b>붙는 순간 알아서 넘어가게</b> 하면 그 판단이 통째로 사라진다.

    테넌트 id 는 원드라이브 백업 폴더를 정책으로 켤 때 필요하다.
#>
function Get-TeavelAccountState {
    param()

    $connected = $false
    $tenant = ''
    $accounts = New-Object System.Collections.Generic.List[string]

    try {
        foreach ($line in @(dsregcmd /status 2>$null)) {
            if ($line -match '^\s*(?:AzureAdJoined|DomainJoined|WorkplaceJoined)\s*:\s*YES') { $connected = $true }

            # 장치 연결이면 TenantId, 계정 추가면 WorkplaceTenantId 로 나온다.
            if ($line -match '^\s*(?:Workplace)?TenantId\s*:\s*([0-9a-fA-F-]{36})') {
                if (-not $tenant) { $tenant = $Matches[1] }
            }
            if ($line -match '\s*(?:WorkplaceUPN|Executing Account Name|UPN)\s*:\s*(\S+@\S+)') {
                if (-not $accounts.Contains($Matches[1])) { $accounts.Add($Matches[1]) }
            }
        }
    } catch { }

    $d = New-Object System.Collections.Generic.List[string]
    $d.Add("connected=$connected")
    $d.Add("tenant=$tenant")
    foreach ($a in $accounts) { $d.Add("account=$a") }

    New-TeavelResult -Message $(if ($connected) { '연결됨' } else { '아직' }) -Details $d
}

# ═══════════════════════════ Windows 업데이트 ═══════════════════════════

<#
.SYNOPSIS
    이 업데이트가 판 올리기(기능 업데이트)인지.
.DESCRIPTION
    판 올리기는 우리가 조용히 시작하면 안 되는 것이라 반드시 갈라내야 한다.
    한 시간 넘게 걸리고 도중에 여러 번 다시 시작한다 — 수업 직전에 그것이 시작되면
    그 시간에 컴퓨터를 못 쓴다.

    분류(Categories)에 'Upgrades' 가 붙는다. 다만 판마다 분류가 비어 오는 일이 있어
    제목도 함께 본다("Windows 11, version 25H2" 꼴).
#>
function Test-TeavelFeatureUpdate {
    param([Parameter(Mandatory)] $Update)

    try {
        foreach ($c in @($Update.Categories)) {
            if ($c.Name -eq 'Upgrades') { return $true }
        }
    } catch { }

    return ($Update.Title -match 'version\s+\d{2}H\d')
}

<#
.SYNOPSIS
    받아야 할 Windows 업데이트가 몇 개인지 알아본다. 아무것도 설치하지 않는다.
.DESCRIPTION
    Windows Update 에이전트(COM)에게 직접 물어본다. 찾아보는 것만은 관리자 권한이 필요 없다.

    학교 컴퓨터는 업체가 만들어 둔 이미지를 복사해 온 것이라 만든 날에 멈춰 있다.
    그래서 처음 켜면 밀린 것이 수십 개인 일이 흔하다.
#>
function Get-TeavelUpdateStatus {
    param(
        # 드라이버까지 볼지. 기본은 보안·기능 업데이트만 본다.
        [switch] $IncludeDrivers
    )

    $d = New-Object System.Collections.Generic.List[string]

    try {
        $session = New-Object -ComObject Microsoft.Update.Session
        $searcher = $session.CreateUpdateSearcher()

        # IsInstalled=0 : 아직 안 깔린 것. IsHidden=0 : 숨겨 두지 않은 것.
        $query = "IsInstalled=0 and IsHidden=0"
        if (-not $IncludeDrivers) { $query += " and Type='Software'" }

        $result = $searcher.Search($query)
        $updates = @($result.Updates)

        # 판 올리기(기능 업데이트)와 나머지를 <b>갈라서</b> 센다.
        #
        # 둘은 성격이 전혀 다르다. 누적 업데이트는 몇 분이면 끝나고 조용히 깔아도 되지만,
        # 판 올리기는 한 시간 넘게 걸리고 여러 번 다시 시작한다. 한 덩어리로 세면
        # "1개 있습니다" 라고 해 놓고 한 시간짜리를 시작하게 된다.
        $normal = 0
        $needsReboot = $false

        foreach ($u in $updates) {
            if (Test-TeavelFeatureUpdate $u) {
                $d.Add("upgrade=$($u.Title)")
                continue
            }

            $normal++
            if ($u.InstallationBehavior.RebootBehavior -ne 0) { $needsReboot = $true }
            $d.Add("update=$($u.Title)")
        }

        $d.Add("count=$normal")
        $d.Add("reboot=$needsReboot")

        # 크기는 적지 않는다.
        #
        # MaxDownloadSize 는 기능 업데이트에서 못 믿는다 — 4GB 짜리를 96,907MB 로 알려 준다
        # (실기 확인: 25H2 를 101,614,132,073 바이트로 보고했다). 화면에 내보내면
        # "100GB 를 받아야 한다" 는 말이 되어 선생님이 겁먹고 그만둔다. 아예 읽지 않는다.

        # ── 얼마나 걸릴지 가늠할 재료 ──
        #
        # 판 올리기는 한나절이 갈 수도 있는 일이라, 시작하기 전에 얼마나 걸릴지 알아야
        # 언제 할지 정할 수 있다. 정확히는 못 맞히지만 <b>범위는 줄 수 있다</b> —
        # 설치 시간을 가장 크게 가르는 것이 디스크 종류다.
        try {
            $disk = Get-PhysicalDisk -ErrorAction Stop |
                    Where-Object { $_.DeviceId -eq 0 } | Select-Object -First 1
            if ($disk) { $d.Add("disk=$($disk.MediaType)") }
        } catch { }

        try {
            $free = (Get-PSDrive C -ErrorAction Stop).Free
            $d.Add("freegb=$([int]($free / 1GB))")
        } catch { }

        # 얼마나 밀렸는지. 이것이 시간을 가장 크게 가른다 —
        # 오래 밀릴수록 거쳐 갈 것이 많아져서, 판 올리기 한 번으로 끝나지 않는다.
        # InstallDate 는 이 Windows 가 이 디스크에 놓인 날이다(업체가 이미지를 뜬 날).
        try {
            $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
            if ($cv.PSObject.Properties['InstallDate']) {
                $laid = [DateTimeOffset]::FromUnixTimeSeconds([int64]$cv.InstallDate).LocalDateTime
                $d.Add("laid=$($laid.ToString('yyyy-MM-dd'))")
            }
        } catch { }

        # ── 왜 여태 안 됐는지 ──
        #
        # 지금 밀린 것보다 <b>지난번에 왜 실패했는지</b>가 훨씬 값진 단서다.
        # 이력에 기능 업데이트 실패가 있으면 '내려받기가 안 되는' 것이 아니라
        # '설치가 롤백되는' 상태다 — 손볼 곳이 전혀 다르다.
        try {
            $history = @($searcher.QueryHistory(0, 20))

            $lastOk = $history | Where-Object { $_.ResultCode -eq 2 } | Select-Object -First 1
            if ($lastOk) { $d.Add("lastok=$($lastOk.Date.ToString('yyyy-MM-dd'))") }

            # ResultCode 4 = 실패, 5 = 중단됨.
            $lastFail = $history | Where-Object { $_.ResultCode -in 4, 5 } | Select-Object -First 1
            if ($lastFail) {
                $hr = '0x{0:X8}' -f ($lastFail.HResult -band 0xFFFFFFFF)
                $d.Add("failtitle=$($lastFail.Title)")
                $d.Add("failcode=$hr")
                $d.Add("faildate=$($lastFail.Date.ToString('yyyy-MM-dd'))")
            }
        } catch { }

        $msg = if ($normal -eq 0) { '받을 업데이트가 없습니다.' }
               else { "받아야 할 업데이트가 $normal 개 있습니다." }

        return New-TeavelResult -Message $msg -Details $d
    }
    catch {
        # 업데이트 서비스가 꺼져 있거나 학교 정책이 막아 둔 경우가 있다.
        # 못 물어본 것과 '없다' 는 전혀 다르므로 그렇게 말한다.
        $d.Add("count=?")
        $d.Add($_.Exception.Message)
        return New-TeavelResult -Message 'Windows 업데이트를 확인하지 못했습니다.' -Details $d
    }
}

<#
.SYNOPSIS
    밀린 Windows 업데이트를 받아서 설치한다. 관리자 권한이 필요하다.
.DESCRIPTION
    받는 것과 설치하는 것을 Windows Update 에이전트에게 시킨다.

    ■ 판 올리기(22H2 → 25H2)는 여기서 하지 않는다

    그건 '기능 업데이트' 라 이 방법으로는 잘 되지 않고, 되더라도 한 시간 넘게 걸리며
    도중에 여러 번 다시 시작한다. 그런 일을 콘솔 창 뒤에서 조용히 시작하면 안 된다.
    판 올리기는 설정 화면을 열어 교사가 직접 누르게 한다.
#>
function Install-TeavelUpdates {
    param(
        [switch] $IncludeDrivers
    )

    $d = New-Object System.Collections.Generic.List[string]

    $admin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
             ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) {
        $d.Add('PowerShell 을 [관리자 권한으로 실행] 한 뒤 다시 해 주세요.')
        return New-TeavelResult -Message '관리자 권한이 필요합니다.' -Details $d
    }

    try {
        $session = New-Object -ComObject Microsoft.Update.Session
        $searcher = $session.CreateUpdateSearcher()

        $query = "IsInstalled=0 and IsHidden=0"
        if (-not $IncludeDrivers) { $query += " and Type='Software'" }

        $found = @($searcher.Search($query).Updates)

        # 판 올리기는 여기서 손대지 않는다. 이 한 줄이 없으면 "업데이트 설치" 라고 눌렀을 뿐인데
        # 한 시간짜리 판 올리기가 조용히 시작된다 — 그건 교사가 시킨 일이 아니다.
        $wanted = New-Object -ComObject Microsoft.Update.UpdateColl
        $skipped = 0

        foreach ($u in $found) {
            if (Test-TeavelFeatureUpdate $u) { $skipped++; continue }

            # 약관에 동의해야 하는 것이 섞여 있다. 동의하지 않으면 그 항목만 조용히 빠진다.
            if (-not $u.EulaAccepted) { try { $u.AcceptEula() } catch { } }
            [void]$wanted.Add($u)
        }

        if ($skipped -gt 0) { $d.Add("upgradeskipped=$skipped") }

        if ($wanted.Count -eq 0) {
            $d.Add('count=0')
            $msg = if ($skipped -gt 0) { '판 올리기 말고는 받을 것이 없습니다.' }
                   else { '받을 업데이트가 없습니다.' }
            return New-TeavelResult -Message $msg -Details $d
        }

        $downloader = $session.CreateUpdateDownloader()
        $downloader.Updates = $wanted
        [void]$downloader.Download()

        # 내려받기에 성공한 것만 설치한다.
        $ready = New-Object -ComObject Microsoft.Update.UpdateColl
        foreach ($u in $wanted) { if ($u.IsDownloaded) { [void]$ready.Add($u) } }

        if ($ready.Count -eq 0) {
            $d.Add('내려받지 못했습니다. 인터넷 연결을 확인해 주세요.')
            return New-TeavelResult -Message '업데이트를 내려받지 못했습니다.' -Details $d
        }

        $installer = $session.CreateUpdateInstaller()
        $installer.Updates = $ready

        # 창을 띄우지 않고 조용히 설치한다. 이걸 안 켜면 설치 중에 대화 상자가 떠서
        # 아무도 안 보는 화면에서 응답을 기다리며 멈춘다.
        try { $installer.ForceQuiet = $true } catch { }

        $result = $installer.Install()

        # ResultCode 2 = 성공, 3 = 일부 실패해도 설치는 됨.
        $ok = ($result.ResultCode -eq 2)
        $d.Add("installed=$($ready.Count)")
        $d.Add("reboot=$($result.RebootRequired)")
        $d.Add("code=$($result.ResultCode)")

        $msg = if ($result.RebootRequired) {
            "$($ready.Count)개를 설치했습니다. 다시 시작해야 마무리됩니다."
        } elseif ($ok) {
            "$($ready.Count)개를 설치했습니다."
        } else {
            "$($ready.Count)개 중 일부가 설치되지 않았습니다."
        }

        return New-TeavelResult -Message $msg -Details $d
    }
    catch {
        $d.Add($_.Exception.Message)
        return New-TeavelResult -Message '업데이트를 설치하지 못했습니다.' -Details $d
    }
}

<#
.SYNOPSIS
    Windows 업데이트 설정 화면을 연다.
.DESCRIPTION
    판 올리기(기능 업데이트)는 교사가 직접 눌러야 한다 — 오래 걸리고 여러 번 다시 시작한다.
#>
function Open-TeavelUpdateSetting {
    param()

    Start-Process 'ms-settings:windowsupdate'

    $d = New-Object System.Collections.Generic.List[string]
    $d.Add('[업데이트 확인] 을 누르시고, 나오는 것을 모두 설치해 주세요.')
    $d.Add('')
    $d.Add('판 올리기(예: 25H2)가 보이면 그것도 받으세요.')
    $d.Add('한 시간쯤 걸리고 여러 번 다시 시작합니다. 시간이 있을 때 하시는 편이 좋습니다.')

    New-TeavelResult -Message '업데이트 화면을 띄웠습니다.' -Details $d
}
# ═══════════════════════════ 컴퓨터 이름 ═══════════════════════════

<#
.SYNOPSIS
    이 컴퓨터의 이름과, 그것이 공장 기본값인지 알려준다. 아무것도 바꾸지 않는다.
.DESCRIPTION
    Windows 를 처음 켜면 DESKTOP-A1B2C3D 같은 이름이 자동으로 붙는다.
    그대로 두면 학교에서 어느 기계인지 분간이 안 되고, Teams·OneDrive·자산 목록에도
    그 이름이 그대로 뜬다. 선생님이 스스로 바꾸는 일은 거의 없다.
#>
function Get-TeavelComputerName {
    param()

    $key    = 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName'
    $active = ''
    $next   = ''
    try { $active = [string](Get-ItemProperty "$key\ActiveComputerName" -Name ComputerName -EA SilentlyContinue).ComputerName } catch { }
    try { $next   = [string](Get-ItemProperty "$key\ComputerName"       -Name ComputerName -EA SilentlyContinue).ComputerName } catch { }
    if (-not $active -and $env:COMPUTERNAME) { $active = [string]$env:COMPUTERNAME }

    # 이름을 아예 못 읽었으면 '기본값이 아니다' 로 넘기면 안 된다 — 모른다고 말해야 한다.
    if (-not $active) {
        return New-TeavelResult -Message '컴퓨터 이름을 확인하지 못했습니다.' -Details @(
            '레지스트리에서도 환경 변수에서도 이름을 읽지 못했습니다.',
            'Windows 가 아니거나, 읽을 권한이 없는 상태일 수 있습니다.'
        )
    }

    # 설치할 때 자동으로 붙는 꼴 — DESKTOP-XXXXXXX / WIN-XXXXXXXXXXX
    $looksDefault = $active -match '^(DESKTOP|WIN|PC)-[A-Z0-9]{6,}$'

    # 이름 바꾸기를 권하면 안 되는 상태인지(학교가 관리하는 PC)
    $f = Get-TeavelSystemFacts
    $managed = $f.AzureAdJoined -or $f.DomainJoined

    $d = New-Object System.Collections.Generic.List[string]
    $d.Add("지금 이름: $active")
    if ($next -and $next -ne $active) {
        $d.Add("바뀔 이름: $next  — 다시 시작하면 적용됩니다.")
    }

    if ($managed) {
        $d.Add('')
        $d.Add('이 컴퓨터는 학교가 관리하는 상태입니다(도메인·조직 연결).')
        $d.Add('이름을 함부로 바꾸면 학교 자원 접근이 끊길 수 있습니다 — 전산 담당 선생님께 문의하세요.')
    }
    elseif ($looksDefault) {
        $d.Add('')
        $d.Add('설치할 때 자동으로 붙은 이름 그대로입니다.')
        $d.Add('학교에서 어느 컴퓨터인지 알아보기 어렵고, Teams·원드라이브에도 이 이름이 뜹니다.')
    }

    $msg = if ($next -and $next -ne $active) { "이름이 '$next' 로 바뀔 예정입니다. 다시 시작해 주세요." }
           elseif ($managed)                 { "컴퓨터 이름은 '$active' 입니다. (학교 관리 PC)" }
           elseif ($looksDefault)            { "컴퓨터 이름이 '$active' — 아직 정하지 않은 이름입니다." }
           else                              { "컴퓨터 이름은 '$active' 입니다." }

    New-TeavelResult -Message $msg -Details $d
}

<#
.SYNOPSIS
    컴퓨터 이름을 바꾼다. 관리자 확인 창(UAC)이 한 번 뜬다.
.DESCRIPTION
    이름 바꾸기는 관리자 권한이 필요하다. Teavel 은 교사 계정으로 돌기 때문에,
    승격된 프로세스를 하나 띄워 거기서 바꾼다 — 선생님은 [예] 를 한 번 누르면 된다.

    바꾼 이름은 다시 시작해야 적용된다. 여기서 다시 시작시키지는 않는다.
.PARAMETER NewName
    새 이름. 영문자·숫자·붙임표(-)만 쓸 수 있고 15자 이내여야 한다.
    한글은 쓸 수 없다 — 네트워크에서 컴퓨터를 찾지 못하는 문제가 생긴다.
#>
function Set-TeavelComputerName {
    param(
        [Parameter(Mandatory)][string] $NewName
    )

    $name = $NewName.Trim()

    # ── 이름 규칙 ── 여기서 막지 않으면 네트워크 이름 해석이 조용히 망가진다.
    if ($name.Length -eq 0)  { throw '새 이름을 알려주셔야 합니다.' }
    if ($name.Length -gt 15) { throw "컴퓨터 이름은 15자를 넘을 수 없습니다. ('$name' 은 $($name.Length)자)" }
    if ($name -match '[가-힣]') {
        throw "컴퓨터 이름에 한글은 쓸 수 없습니다. 영문으로 지어 주세요. (예: 2-3-kimminsu)"
    }
    if ($name -notmatch '^[A-Za-z0-9-]+$') {
        throw "컴퓨터 이름에는 영문자·숫자·붙임표(-)만 쓸 수 있습니다. 빈칸이나 밑줄은 안 됩니다. (받은 값: $name)"
    }
    if ($name -match '^\d+$')      { throw '숫자만으로는 이름을 지을 수 없습니다.' }
    if ($name -match '^-|-$')      { throw '이름의 처음이나 끝에 붙임표(-)를 둘 수 없습니다.' }

    $current = [string]$env:COMPUTERNAME
    if ($name -ieq $current) {
        return New-TeavelResult -Message "이미 '$current' 입니다. 바꿀 것이 없습니다."
    }

    # ── 학교가 관리하는 PC 는 건드리지 않는다 ──
    $f = Get-TeavelSystemFacts
    if ($f.AzureAdJoined -or $f.DomainJoined) {
        throw ('이 컴퓨터는 학교가 관리하는 상태(도메인·조직 연결)입니다. ' +
               '이름을 바꾸면 학교 자원 접근이 끊길 수 있어 Teavel 이 바꾸지 않습니다. ' +
               '전산 담당 선생님께 문의해 주세요.')
    }

    # ── 관리자 권한으로 한 번만 승격 ──
    $quoted = $name -replace "'", "''"
    try {
        $p = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru -WindowStyle Hidden `
             -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
                           "Rename-Computer -NewName '$quoted' -Force -ErrorAction Stop"
    }
    catch {
        throw '관리자 확인 창에서 [아니오] 를 누르셨거나 권한을 얻지 못했습니다. 이름을 바꾸지 않았습니다.'
    }

    if ($null -eq $p -or $p.ExitCode -ne 0) {
        throw "이름을 바꾸지 못했습니다. 학교 정책으로 막혀 있을 수 있습니다. (종료 코드 $($p.ExitCode))"
    }

    # ── 실제로 예약됐는지 확인한다 ── 승격된 프로세스가 조용히 실패했을 수 있다.
    $pending = ''
    try {
        $pending = [string](Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName' `
                            -Name ComputerName -EA SilentlyContinue).ComputerName
    } catch { }

    if ($pending -ine $name) {
        throw "이름 바꾸기가 적용되지 않았습니다. 지금 예약된 이름은 '$pending' 입니다."
    }

    New-TeavelResult -Message "컴퓨터 이름을 '$name' 으로 바꿨습니다." -Details @(
        "지금 이름: $current  →  다시 시작 후: $name",
        '',
        '컴퓨터를 다시 시작해야 적용됩니다. 지금 하시지 않아도 됩니다.',
        '다시 시작하기 전까지는 예전 이름으로 보입니다.'
    )
}

Export-ModuleMember -Function `
    Get-TeavelSystemFacts, Get-TeavelWindowsInfo, Get-TeavelAccountGuide, Open-TeavelAccountSetting, `
    Get-TeavelAccountState, `
    Get-TeavelUpdateStatus, Install-TeavelUpdates, Open-TeavelUpdateSetting, `
    Get-TeavelComputerName, Set-TeavelComputerName
