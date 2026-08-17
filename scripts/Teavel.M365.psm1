<#
    Microsoft 365 — 학교 그룹·Teams 구성.

    ■ 왜 Graph 를 기본으로 쓰지 않는가

    Microsoft.Graph PowerShell 은 'Microsoft Graph Command Line Tools' 라는 앱으로 붙는데,
    Group.ReadWrite.All 같은 권한은 관리자 동의가 필요하다. 학교마다 전역 관리자가
    낯선 이름의 앱에 넓은 권한을 승인해야 한다는 뜻이다 — 잘 모르는 관리자에게 나쁜 첫 화면이다.

    반면 MicrosoftTeams · ExchangeOnlineManagement 는 마이크로소프트 자체 모듈이라
    모든 테넌트에 이미 동의돼 있다. 전역 관리자면 동의 화면 없이 그냥 붙는다.
    팀·M365 그룹·구성원은 전부 이 둘로 된다.

    Entra 보안 그룹만 Graph 가 필요해서 '고급' 으로 뺐다. 트리에 보안 그룹이 없으면
    Graph 는 아예 건드리지 않는다.

    ■ 이 모듈은 얇게 유지한다

    목록 가져오기와 하나 만들기만 한다. 무엇을 만들지 정하는 판단(선언 펼치기, 대조,
    이름 검증)은 전부 C# 에 있다 — 테넌트가 없어도 확인할 수 있어야 하기 때문이다.
#>

Set-StrictMode -Version Latest

# 팀·그룹 작업에 필요한 모듈. Graph 는 보안 그룹(고급)에서만 쓴다.
# 최소 버전은 낮게 잡는다 — 3.x 면 우리가 쓰는 것은 다 있다.
# (Connect-ExchangeOnline -Device 는 필요 없다. 교사 PC 에서는 브라우저 창이 그냥 뜬다)
$script:CoreModules = @(
    @{ Name = 'ExchangeOnlineManagement'; Min = [version]'3.0.0'; What = '그룹·메일' }
    @{ Name = 'MicrosoftTeams';           Min = [version]'4.0.0'; What = '팀'       }
)

<#
.SYNOPSIS
    PowerShell 이 실제로 모듈을 찾는 폴더 중, 내 계정 것이면서 OneDrive 밖인 곳.
.DESCRIPTION
    경로를 짐작하면 반드시 틀린다. 두 가지가 겹치기 때문이다.

      · OneDrive 폴더 백업이 켜져 있으면 문서 폴더가 OneDrive 아래로 옮겨진다.
        한국어 Windows 에서는 이름이 '문서' 라 영문 'Documents' 로 짐작하면 없다.
      · 그런데 OneDrive 아래에 모듈을 두면 파일 온디맨드로 DLL 이 자리표시자가 되어
        어셈블리 로드가 실패할 수 있다.

    그래서 $env:PSModulePath 를 읽되 OneDrive 아래는 피하고, 마땅한 곳이 없으면
    %LOCALAPPDATA% 에 우리 폴더를 만들어 쓴다.
#>
function Get-TeavelModuleDirectory {
    param()

    # 구분자를 ';' 로 박아 두면 리눅스 pwsh 에서 한 덩어리가 되어 아무것도 못 찾는다.
    # 제품은 Windows 에서만 돌지만, 리눅스에서 돌려 볼 수 있어야 고칠 수 있다.
    $candidates = @($env:PSModulePath -split [IO.Path]::PathSeparator | Where-Object { $_ })

    # 내 계정 아래이면서 OneDrive 가 아닌 것
    $mine = $candidates | Where-Object {
        $_ -like "*\Users\$env:USERNAME\*" -and $_ -notmatch '\\OneDrive\\'
    } | Select-Object -First 1

    if ($mine) { return $mine }

    # 없으면 우리 폴더를 만들어 쓴다(OneDrive 가 문서 폴더를 가져간 경우가 여기 온다).
    # LOCALAPPDATA 가 비어 있으면(Windows 밖) Join-Path 가 터지므로 마지막 버팀목을 둔다 —
    # 여기서 예외가 나면 '준비 확인' 이라는 가장 앞 단계부터 막힌다.
    if ($env:LOCALAPPDATA) { return (Join-Path $env:LOCALAPPDATA 'Teaveloper\Modules') }
    return (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Teaveloper/Modules')
}

<#
.SYNOPSIS
    M365 작업에 필요한 것이 갖춰졌는지 본다. 아무것도 설치하거나 바꾸지 않는다.
.DESCRIPTION
    '있는지' 가 아니라 '쓸 만한 판이 있는지' 를 본다 — 너무 낮은 판이 깔려 있으면
    나중에 없는 매개변수에서 터진다.
#>
function Get-TeavelM365Readiness {
    param()

    $d = New-Object System.Collections.Generic.List[string]
    $need = New-Object System.Collections.Generic.List[string]

    foreach ($spec in $script:CoreModules) {
        $found = @(Get-Module -ListAvailable -Name $spec.Name -ErrorAction SilentlyContinue |
                   Sort-Object Version -Descending)

        if ($found.Count -eq 0) {
            $need.Add($spec.Name)
            $d.Add(("{0,-26} 없음        ({1})" -f $spec.Name, $spec.What))
            continue
        }

        $best = $found[0]
        if ($best.Version -lt $spec.Min) {
            $need.Add($spec.Name)
            $d.Add(("{0,-26} {1} — 낮음 ({2} 이상 필요)" -f $spec.Name, $best.Version, $spec.Min))
        } else {
            $d.Add(("{0,-26} {1}" -f $spec.Name, $best.Version))
        }

        # 판이 여럿이면 PowerShell 이 높은 것을 잡는데, 그게 깨져 있으면 낮은 것도 못 쓴다.
        if ($found.Count -gt 1) {
            $d.Add(("{0,-26} ! 판이 {1}개 깔려 있습니다: {2}" -f '', $found.Count, (($found.Version) -join ', ')))
        }
    }

    $d.Add('')
    $d.Add("PowerShell $($PSVersionTable.PSVersion)")
    $d.Add("설치할 곳    $(Get-TeavelModuleDirectory)")

    if ($need.Count -gt 0) {
        $d.Add('')
        $d.Add('Teavel 이 대신 설치해 드릴 수 있습니다.')
        $d.Add('(내 계정에만 설치되며 관리자 권한이 필요 없습니다)')
        return New-TeavelResult -Message "손봐야 할 모듈이 $($need.Count)개 있습니다." -Details $d
    }

    New-TeavelResult -Message '필요한 모듈이 모두 갖춰져 있습니다.' -Details $d
}

<#
.SYNOPSIS
    PowerShell 갤러리에서 모듈을 직접 받아 푼다. PowerShellGet 을 거치지 않는다.
.DESCRIPTION
    Install-Module 은 PS 5.1 에서 자주 막힌다 — PackageManagement 가 현재 세션에
    물려 있으면 갱신 자체가 안 되고, 그걸 푸는 데 프로세스를 새로 띄워야 한다.
    갤러리의 패키지는 그냥 zip 이므로 받아서 풀면 그 사슬을 통째로 건너뛴다.
#>
function Install-TeavelModuleFromGallery {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Version,
        [Parameter(Mandatory)][string] $Directory
    )

    $target = Join-Path $Directory "$Name\$Version"
    $tmp    = Join-Path $env:TEMP "teavel-$Name-$Version.zip"

    try {
        Invoke-WebRequest "https://www.powershellgallery.com/api/v2/package/$Name/$Version" `
            -OutFile $tmp -UseBasicParsing -ErrorAction Stop

        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        New-Item -ItemType Directory -Force $target | Out-Null

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($tmp, $target)

        # nupkg 부산물은 모듈 폴더에 있으면 지저분하다.
        foreach ($junk in '_rels', 'package', '[Content_Types].xml', "$Name.nuspec") {
            $p = Join-Path $target $junk
            if (Test-Path $p) { Remove-Item $p -Recurse -Force }
        }

        # 인터넷에서 받은 표시(MOTW)가 붙어 있으면 DLL 로드가 막힌다.
        Get-ChildItem $target -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
    }
    finally {
        if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
    }
}

<#
.SYNOPSIS
    M365 작업에 필요한 모듈을 내 계정에만 설치한다.
.DESCRIPTION
    -Scope CurrentUser 이므로 관리자 권한이 필요 없다.

    실제 교사 PC(Windows 11 · PowerShell 5.1)에서 이 일을 해 보면 관문이 여럿이다.
    하나씩 넘어간다:

      ① TLS 가 SystemDefault 면 갤러리에 연결조차 안 된다 → 1.2 로 올린다
      ② PSGallery 가 Untrusted 면 "정말 하시겠습니까?" 에서 멈춘다 → 그 세션에서만 신뢰로
      ③ PowerShellGet 1.0.0.1 은 낡아 -Force 가 안 먹는 구석이 있다
      ④ PackageManagement 가 물려 있으면 갱신 자체가 막힌다
      ③④ 는 풀기 번거로우므로, Install-Module 이 실패하면 갤러리에서 직접 받아 푼다.

    설치 위치는 짐작하지 않는다 — Get-TeavelModuleDirectory 를 보라.
#>
function Install-TeavelM365Module {
    param(
        [string] $Only
    )

    # ① 갤러리는 TLS 1.2 이상만 받는다.
    try {
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    } catch { }

    # ② 이 세션에서만 신뢰로 바꾼다. 원래 값은 끝나고 돌려놓는다 —
    #    교사 PC 의 설정을 우리가 말없이 바꿔 두면 안 된다.
    $policyBefore = $null
    try {
        $repo = Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue
        if ($repo -and $repo.InstallationPolicy -ne 'Trusted') {
            $policyBefore = $repo.InstallationPolicy
            Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue
        }
    } catch { }

    $dir = Get-TeavelModuleDirectory
    New-Item -ItemType Directory -Force $dir | Out-Null

    $done   = New-Object System.Collections.Generic.List[string]
    $failed = New-Object System.Collections.Generic.List[string]

    try {
        foreach ($spec in $script:CoreModules) {
            if ($Only -and $spec.Name -ne $Only) { continue }

            $have = @(Get-Module -ListAvailable -Name $spec.Name -ErrorAction SilentlyContinue |
                      Sort-Object Version -Descending)
            if ($have.Count -gt 0 -and $have[0].Version -ge $spec.Min) {
                $done.Add("$($spec.Name) $($have[0].Version) — 이미 쓸 만합니다")
                continue
            }

            # ③④ 를 피해 Install-Module 을 먼저 시도하고, 막히면 직접 받는다.
            $ok = $false
            try {
                Install-Module -Name $spec.Name -Scope CurrentUser -Force -AllowClobber `
                    -SkipPublisherCheck -ErrorAction Stop
                $ok = $true
                $done.Add("$($spec.Name) 설치 완료")
            } catch {
                $done.Add("$($spec.Name) — 갤러리에서 직접 받습니다")
            }

            if (-not $ok) {
                try {
                    $latest = (Find-Module -Name $spec.Name -ErrorAction Stop |
                               Sort-Object Version -Descending | Select-Object -First 1).Version.ToString()
                    Install-TeavelModuleFromGallery -Name $spec.Name -Version $latest -Directory $dir
                    if ($env:PSModulePath -notlike "*$dir*") { $env:PSModulePath = "$dir;$env:PSModulePath" }
                    $done.Add("$($spec.Name) $latest 설치 완료 ($dir)")
                } catch {
                    $failed.Add("$($spec.Name) — $($_.Exception.Message)")
                }
            }
        }
    }
    finally {
        if ($policyBefore) {
            try { Set-PSRepository -Name PSGallery -InstallationPolicy $policyBefore -ErrorAction SilentlyContinue } catch { }
        }
    }

    if ($failed.Count -gt 0) {
        $d = New-Object System.Collections.Generic.List[string]
        foreach ($x in $done)   { $d.Add($x) }
        foreach ($x in $failed) { $d.Add($x) }
        $d.Add('')
        $d.Add('학교 네트워크가 PowerShell 갤러리(www.powershellgallery.com)를 막고 있을 수 있습니다.')
        $d.Add('전산 담당 선생님께 위 메시지를 그대로 전해 주세요.')
        throw ("모듈을 설치하지 못했습니다: " + ($failed -join ' / '))
    }

    $d = New-Object System.Collections.Generic.List[string]
    foreach ($x in $done) { $d.Add($x) }
    $d.Add('')
    $d.Add('PowerShell 창을 새로 열면 확실히 반영됩니다.')

    New-TeavelResult -Message '모듈 준비를 마쳤습니다.' -Details $d
}

<#
.SYNOPSIS
    학교 M365 에 로그인한다. 전역 관리자 계정이어야 한다.
.DESCRIPTION
    MicrosoftTeams · ExchangeOnlineManagement 는 마이크로소프트 자체 모듈이라
    테넌트에 이미 동의돼 있다 — 낯선 앱에 권한을 주는 화면이 뜨지 않는다.

    이미 로그인돼 있으면 다시 묻지 않는다.
#>
function Connect-TeavelM365 {
    param(
        [string] $Account,
        [switch] $TeamsToo
    )

    Import-Module ExchangeOnlineManagement -ErrorAction Stop

    $d = New-Object System.Collections.Generic.List[string]

    # 이미 붙어 있으면 아무 말 없이 그대로 쓴다.
    $exoOk = $false
    try { $null = Get-OrganizationConfig -ErrorAction Stop; $exoOk = $true } catch { }

    if (-not $exoOk) {
        # ── 멈추기 전에 먼저 말한다 ──
        # 로그인 창이 뜬다는 걸 모르면 콘솔이 멈춘 줄 알고 닫아 버린다.
        # 결과로 돌려주면 늦다 — 기다리는 동안 화면에 아무것도 없기 때문이다.
        # 그래서 Write-Host 로 지금 찍는다(래퍼의 JSON 은 마지막 '{' 부터 읽으므로 안전하다).
        Write-Host ''
        Write-Host '  ┌─────────────────────────────────────────────────┐'
        Write-Host '  │  잠시 후 인터넷 창이 하나 저절로 열립니다        │'
        Write-Host '  └─────────────────────────────────────────────────┘'
        Write-Host ''
        Write-Host '  ① 열린 창에 학교 메일 주소를 넣고 [다음]'
        Write-Host '  ② 비밀번호를 넣습니다'
        Write-Host '  ③ "로그인 상태를 유지하시겠습니까?" 가 나오면 [예]'
        Write-Host ''
        Write-Host '  창이 안 보이면 — 다른 창 뒤에 숨어 있을 수 있습니다.'
        Write-Host '  화면 맨 아래 줄(작업 표시줄)에서 새로 생긴 인터넷 아이콘을 눌러 보세요.'
        Write-Host ''
        Write-Host '  로그인을 마치면 이 화면으로 저절로 돌아옵니다.'
        Write-Host '  그때까지 이 창을 닫지 마세요.'
        Write-Host ''
        Write-Host '  기다리는 중…'
        Write-Host ''

        try {
            if ($Account) { Connect-ExchangeOnline -UserPrincipalName $Account -ShowBanner:$false -ErrorAction Stop }
            else          { Connect-ExchangeOnline -ShowBanner:$false -ErrorAction Stop }
        } catch {
            throw ("로그인하지 못했습니다: " + $_.Exception.Message + "`n" +
                   "창을 닫으셨거나, 학교 계정이 아니거나, 전역 관리자가 아닐 수 있습니다.")
        }
    }

    # 팀을 만들 때만 필요하다. 재고를 보는 데는 Exchange 하나면 되므로
    # 로그인을 두 번 시키지 않는다 — 한 번도 버거운 분들이 대상이다.
    $teamsOk = $null
    if ($TeamsToo) {
        Import-Module MicrosoftTeams -ErrorAction Stop
        try { $null = Get-CsTenant -ErrorAction Stop; $teamsOk = $true } catch { $teamsOk = $false }
        if (-not $teamsOk) {
            Write-Host ''
            Write-Host '  팀 작업을 위해 한 번 더 로그인 창이 열립니다. 같은 계정으로 하시면 됩니다.'
            Write-Host ''
            if ($Account) { Connect-MicrosoftTeams -AccountId $Account -ErrorAction Stop | Out-Null }
            else          { Connect-MicrosoftTeams -ErrorAction Stop | Out-Null }
        }
    }

    $org = $null
    try { $org = Get-OrganizationConfig -ErrorAction SilentlyContinue } catch { }
    if ($org) { $d.Add("학교: $($org.DisplayName)") }
    $d.Add("메일·그룹: $(if ($exoOk) { '이미 연결돼 있었습니다' } else { '연결했습니다' })")
    if ($TeamsToo) { $d.Add("팀: $(if ($teamsOk) { '이미 연결돼 있었습니다' } else { '연결했습니다' })") }

    New-TeavelResult -Message '학교 M365 에 연결했습니다.' -Details $d
}

<#
.SYNOPSIS
    지금 테넌트에 있는 M365 그룹·팀을 전부 읽어 온다. 아무것도 바꾸지 않는다.
.DESCRIPTION
    정리를 시작하기 전에 무엇이 있는지 보는 것이 첫 일이다. 무엇이 그것을 만들었는지
    (SDS·손·다른 도구)는 따지지 않는다 — 이미 있으면 있는 것이고, 어떻게 할지는
    관리자가 보고 정한다.

    각 줄의 모양(C# 이 파싱한다):
        GROUP<TAB>이름<TAB>별칭<TAB>메일주소<TAB>팀인지<TAB>구성원수<TAB>만든날<TAB>비공개여부
#>
function Get-TeavelM365Inventory {
    param(
        [int] $Limit = 2000
    )

    Import-Module ExchangeOnlineManagement -ErrorAction Stop

    $groups = @(Get-UnifiedGroup -ResultSize $Limit -ErrorAction Stop)

    $d = New-Object System.Collections.Generic.List[string]
    foreach ($g in $groups) {
        # 팀이 붙어 있는 그룹은 ResourceProvisioningOptions 에 'Team' 이 들어 있다.
        $isTeam = $false
        try { $isTeam = @($g.ResourceProvisioningOptions) -contains 'Team' } catch { }

        $members = ''
        try { if ($null -ne $g.GroupMemberCount) { $members = [string]$g.GroupMemberCount } } catch { }

        # 날짜는 이미 DateTime 인 경우가 대부분이지만, 문자열로 오면 지역 형식이라
        # [datetime] 캐스트가 실패할 수 있다. 실패해도 그 그룹을 통째로 버리지는 않는다.
        $created = ''
        try {
            if ($g.WhenCreated -is [datetime]) {
                $created = $g.WhenCreated.ToString('yyyy-MM-dd')
            } elseif ($g.WhenCreated) {
                $parsed = [datetime]::MinValue
                if ([datetime]::TryParse([string]$g.WhenCreated, [ref]$parsed)) {
                    $created = $parsed.ToString('yyyy-MM-dd')
                }
            }
        } catch { }

        $privacy = ''
        try { $privacy = [string]$g.AccessType } catch { }

        # 채널을 손대려면 GroupId 가 있어야 한다. Get-UnifiedGroup 은 이 이름으로 준다.
        $gid = ''
        try { $gid = [string]$g.ExternalDirectoryObjectId } catch { }

        $d.Add(("GROUP`t{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}`t{7}" -f `
            $g.DisplayName, $g.Alias, $g.PrimarySmtpAddress, $isTeam, $members, $created, $privacy, $gid))
    }

    New-TeavelResult -Message "그룹 $($groups.Count)개를 읽었습니다." -Details $d
}

<#
.SYNOPSIS
    M365 그룹 또는 Teams 팀을 하나 만든다.
.DESCRIPTION
    선언(트리)과 재고를 대조해 '없다' 고 판단된 것만 여기로 온다.
    그래도 만들기 직전에 한 번 더 확인한다 — 대조와 실행 사이에 누가 만들었을 수 있고,
    같은 이름이 둘 생기면 나중에 정리하기가 훨씬 고약하다.

    Kind:
      m365 — 그룹만. 공유 사서함과 SharePoint 가 딸려 온다.
      team — 팀. 만들면 M365 그룹이 함께 생긴다. MicrosoftTeams 모듈이 필요하다.
.PARAMETER MailNickname
    메일 주소가 되는 별칭. 영문자·숫자·붙임표·밑줄·점만 쓸 수 있다.
    한글 이름으로 만들면 Windows 가 알아서 붙이는데 뜻이 날아가므로 반드시 지정한다.
#>
function New-TeavelM365Group {
    param(
        [Parameter(Mandatory)][string] $DisplayName,
        [Parameter(Mandatory)][string] $MailNickname,
        [string] $Description = '',
        [ValidateSet('m365', 'team')][string] $Kind = 'm365',
        [ValidateSet('standard', 'educationClass', 'educationStaff')][string] $Template = 'standard',
        [ValidateSet('private', 'public')][string] $Visibility = 'private',
        [string[]] $Owners = @()
    )

    Import-Module ExchangeOnlineManagement -ErrorAction Stop

    if ($MailNickname -notmatch '^[A-Za-z0-9._-]+$') {
        throw "별칭에는 영문자·숫자·붙임표·밑줄·점만 쓸 수 있습니다. (받은 값: $MailNickname)"
    }

    # 대조와 실행 사이에 생겼을 수 있다. 같은 이름이 둘이 되면 정리가 고약해진다.
    $dup = @(Get-UnifiedGroup -ResultSize Unlimited -ErrorAction SilentlyContinue |
             Where-Object { $_.DisplayName -eq $DisplayName -or $_.Alias -eq $MailNickname })
    if ($dup.Count -gt 0) {
        return New-TeavelResult -Message "'$DisplayName' 은(는) 이미 있습니다. 만들지 않았습니다." -Details @(
            "이미 있는 것: $($dup[0].DisplayName)  [$($dup[0].Alias)]"
        )
    }

    $d = New-Object System.Collections.Generic.List[string]

    if ($Kind -eq 'team') {
        Import-Module MicrosoftTeams -ErrorAction Stop

        $tpl = switch ($Template) {
            'educationClass' { 'EDU_Class' }
            'educationStaff' { 'EDU_Staff' }
            default          { $null }
        }

        $params = @{
            DisplayName  = $DisplayName
            MailNickName = $MailNickname
            Visibility   = $(if ($Visibility -eq 'public') { 'Public' } else { 'Private' })
        }
        if ($Description) { $params['Description'] = $Description }
        if ($tpl)         { $params['Template']    = $tpl }
        if ($Owners.Count -gt 0) { $params['Owner'] = $Owners[0] }

        $team = New-Team @params -ErrorAction Stop
        $d.Add("팀을 만들었습니다: $DisplayName")
        if ($tpl) { $d.Add("서식: $tpl") }
        # 부르는 쪽이 이 값으로 곧바로 채널을 붙인다. 형식을 바꾸면 저쪽도 바꿔야 한다.
        if ($team.GroupId) { $d.Add("GROUPID`t$($team.GroupId)") }

        # 소유자가 여럿이면 나머지를 붙인다.
        foreach ($o in ($Owners | Select-Object -Skip 1)) {
            try { Add-TeamUser -GroupId $team.GroupId -User $o -Role Owner -ErrorAction Stop; $d.Add("소유자 추가: $o") }
            catch { $d.Add("소유자 추가 실패($o): $($_.Exception.Message)") }
        }
    }
    else {
        $params = @{
            DisplayName = $DisplayName
            Alias       = $MailNickname
            AccessType  = $(if ($Visibility -eq 'public') { 'Public' } else { 'Private' })
        }
        if ($Description) { $params['Notes'] = $Description }
        if ($Owners.Count -gt 0) { $params['Owner'] = $Owners[0] ; $params['Members'] = $Owners[0] }

        $g = New-UnifiedGroup @params -ErrorAction Stop
        $d.Add("그룹을 만들었습니다: $DisplayName")
        if ($g.PrimarySmtpAddress) { $d.Add("메일 주소: $($g.PrimarySmtpAddress)") }

        foreach ($o in ($Owners | Select-Object -Skip 1)) {
            try {
                Add-UnifiedGroupLinks -Identity $MailNickname -LinkType Owners -Links $o -ErrorAction Stop
                $d.Add("소유자 추가: $o")
            } catch { $d.Add("소유자 추가 실패($o): $($_.Exception.Message)") }
        }
    }

    $d.Add('')
    $d.Add('만들어진 것이 Teams·아웃룩에 보이기까지 몇 분 걸릴 수 있습니다.')

    New-TeavelResult -Message "'$DisplayName' 을(를) 만들었습니다." -Details $d
}

<#
.SYNOPSIS
    팀에 선언된 채널을 맞춘다. 없는 것만 만들고, 이미 있으면 건드리지 않는다.
.DESCRIPTION
    여러 번 돌려도 안전해야 하므로 '만든다' 가 아니라 '맞춘다' 이다.
    팀을 만들다 중간에 끊겨도 다시 돌리면 모자란 채널만 채워진다.

    '일반'(General)은 팀을 만들면 저절로 생기므로 여기서 만들지 않는다.
    선언 쪽에서 이미 걸러 내지만, 손으로 부를 수도 있으니 여기서도 막는다.

    ■ 만든 채널은 기본이 '숨김' 이다

    PowerShell 로 만든 채널은 구성원 화면에서 기본으로 접혀 있다.
    선생님들이 "채널이 없다" 고 하는 원인이 대개 이것인데, Teams 앱에서
    [표시]를 누르면 보인다. 부르는 쪽이 이 말을 반드시 전해야 한다.
.PARAMETER GroupId
    팀의 그룹 id. 재고나 만들기 결과에서 온다.
.PARAMETER Channels
    있어야 할 채널 이름들.
#>
function Sync-TeavelTeamChannel {
    param(
        [Parameter(Mandatory)][string] $GroupId,
        [string[]] $Channels = @()
    )

    Import-Module MicrosoftTeams -ErrorAction Stop

    $wanted = @($Channels | Where-Object {
        $_ -and $_.Trim() -and $_.Trim() -notin @('일반', 'General', 'general')
    } | ForEach-Object { $_.Trim() })

    if ($wanted.Count -eq 0) {
        return New-TeavelResult -Message '만들 채널이 없습니다.' -Details @()
    }

    # 이미 있는 것을 세어야 여러 번 돌려도 안전하다.
    $existing = @()
    try {
        $existing = @(Get-TeamChannel -GroupId $GroupId -ErrorAction Stop |
                      ForEach-Object { [string]$_.DisplayName })
    } catch {
        # 팀이 방금 만들어졌으면 아직 조회가 안 될 수 있다. 그때는 빈 목록으로 보고 만든다 —
        # 이미 있는 것을 또 만들려 하면 아래에서 하나만 실패하고 나머지는 진행된다.
    }

    $made   = New-Object System.Collections.Generic.List[string]
    $kept   = New-Object System.Collections.Generic.List[string]
    $failed = New-Object System.Collections.Generic.List[string]

    foreach ($name in $wanted) {
        if ($existing -contains $name) { $kept.Add($name); continue }
        try {
            New-TeamChannel -GroupId $GroupId -DisplayName $name -ErrorAction Stop | Out-Null
            $made.Add($name)
        } catch {
            $failed.Add("$($name): $($_.Exception.Message)")
        }
    }

    $d = New-Object System.Collections.Generic.List[string]
    if ($made.Count -gt 0)   { $d.Add("만든 채널: $($made -join ', ')") }
    if ($kept.Count -gt 0)   { $d.Add("이미 있던 채널: $($kept -join ', ')") }
    foreach ($f in $failed)  { $d.Add("실패: $f") }

    if ($made.Count -gt 0) {
        $d.Add('')
        $d.Add('새로 만든 채널은 Teams 앱에서 접혀 있습니다. 채널 옆 [...] → [표시] 를 눌러야 보입니다.')
    }

    $msg = if ($made.Count -gt 0) { "채널 $($made.Count)개를 만들었습니다." } else { '채널이 이미 다 있습니다.' }
    if ($failed.Count -gt 0) { throw "채널 $($failed.Count)개를 만들지 못했습니다. $($failed -join ' / ')" }

    New-TeavelResult -Message $msg -Details $d
}

<#
.SYNOPSIS
    그룹·팀의 이름을 바꾼다. 내용은 그대로 남는다.
.DESCRIPTION
    지우고 다시 만들면 파일·대화·팀이 전부 날아간다. 이름만 바꾸면 그대로 둔 채
    새 체계에 편입시킬 수 있다 — 옛 그룹을 정리할 때 삭제보다 먼저 생각할 길이다.

    별칭(Alias)은 기본적으로 건드리지 않는다. 별칭을 바꾸면 메일 주소가 바뀌어
    기존에 공유된 주소·링크가 끊긴다. 정말 바꿔야 할 때만 -NewAlias 를 준다.
.PARAMETER Identity
    지금 이름 또는 별칭 또는 메일 주소.
.PARAMETER NewDisplayName
    새 이름.
.PARAMETER NewAlias
    새 별칭. 주면 메일 주소가 바뀐다 — 옛 주소로 오던 메일이 끊길 수 있다.
#>
function Rename-TeavelM365Group {
    param(
        [Parameter(Mandatory)][string] $Identity,
        [Parameter(Mandatory)][string] $NewDisplayName,
        [string] $NewAlias
    )

    Import-Module ExchangeOnlineManagement -ErrorAction Stop

    $g = Get-UnifiedGroup -Identity $Identity -ErrorAction Stop
    $before = $g.DisplayName
    $d = New-Object System.Collections.Generic.List[string]

    Set-UnifiedGroup -Identity $Identity -DisplayName $NewDisplayName -ErrorAction Stop
    $d.Add("이름: $before  →  $NewDisplayName")

    if ($NewAlias) {
        if ($NewAlias -notmatch '^[A-Za-z0-9._-]+$') {
            throw "별칭에는 영문자·숫자·붙임표·밑줄·점만 쓸 수 있습니다. (받은 값: $NewAlias)"
        }
        $oldSmtp = [string]$g.PrimarySmtpAddress
        Set-UnifiedGroup -Identity $Identity -Alias $NewAlias -ErrorAction Stop
        $d.Add("별칭: $($g.Alias)  →  $NewAlias")
        $d.Add('')
        $d.Add("옛 메일 주소($oldSmtp)로 오던 메일이 끊길 수 있습니다.")
    }

    $d.Add('')
    $d.Add('파일·대화·팀은 그대로 남아 있습니다.')
    $d.Add('Teams 앱에 반영되기까지 몇 분 걸릴 수 있습니다.')

    New-TeavelResult -Message "'$before' 의 이름을 바꿨습니다." -Details $d
}

<#
.SYNOPSIS
    그룹·팀을 지운다. 되돌릴 시간이 30일뿐이므로 조심해서 쓴다.
.DESCRIPTION
    그룹을 지우면 딸린 팀·파일·대화·SharePoint 사이트가 함께 사라진다.
    30일 안에는 복구할 수 있지만 그 뒤에는 되돌릴 수 없다.

    그래서 이 함수는 지우기 전에 무엇이 사라지는지 확인하고, 부르는 쪽이
    -Confirmed 를 명시적으로 켜야만 실제로 지운다. 켜지 않으면 무엇이 사라질지만 알려 준다.
.PARAMETER Identity
    지울 그룹의 이름·별칭·메일 주소.
.PARAMETER Confirmed
    켜야만 실제로 지운다. 끄면 미리보기만 한다.
#>
function Remove-TeavelM365Group {
    param(
        [Parameter(Mandatory)][string] $Identity,
        [bool] $Confirmed = $false
    )

    Import-Module ExchangeOnlineManagement -ErrorAction Stop

    $g = Get-UnifiedGroup -Identity $Identity -ErrorAction Stop

    $isTeam = $false
    try { $isTeam = @($g.ResourceProvisioningOptions) -contains 'Team' } catch { }
    $members = ''
    try { if ($null -ne $g.GroupMemberCount) { $members = [string]$g.GroupMemberCount } } catch { }

    $what = New-Object System.Collections.Generic.List[string]
    $what.Add("이름: $($g.DisplayName)")
    $what.Add("메일: $($g.PrimarySmtpAddress)")
    if ($members) { $what.Add("구성원: $($members)명") }
    $what.Add('')
    $what.Add('함께 사라지는 것:')
    $what.Add('  · 그룹 사서함과 주고받은 메일')
    $what.Add('  · SharePoint 사이트와 그 안의 모든 파일')
    if ($isTeam) { $what.Add('  · Teams 팀과 모든 채널·대화') }

    if (-not $Confirmed) {
        $what.Add('')
        $what.Add('아직 지우지 않았습니다.')
        return New-TeavelResult -Message "'$($g.DisplayName)' 을(를) 지우면 다음이 사라집니다." -Details $what
    }

    Remove-UnifiedGroup -Identity $Identity -Confirm:$false -ErrorAction Stop

    $what.Add('')
    $what.Add('30일 안에는 관리 센터에서 복구할 수 있습니다. 그 뒤에는 되돌릴 수 없습니다.')

    New-TeavelResult -Message "'$($g.DisplayName)' 을(를) 지웠습니다." -Details $what
}

Export-ModuleMember -Function `
    Get-TeavelModuleDirectory, Get-TeavelM365Readiness, Install-TeavelM365Module, `
    Install-TeavelModuleFromGallery, Connect-TeavelM365, `
    Get-TeavelM365Inventory, New-TeavelM365Group, Sync-TeavelTeamChannel, `
    Rename-TeavelM365Group, Remove-TeavelM365Group
