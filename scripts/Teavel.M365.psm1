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
    상태를 바꾸는 명령을 <b>확인 창 없이</b> 부른다.
.DESCRIPTION
    Set-User 같은 명령은 "이 작업을 수행하시겠습니까? [Y/A/N/...]" 를 묻는다.
    상주 세션에는 답할 사람이 없는데, 더 나쁜 것은 <b>멈추지도 않는다</b>는 점이다 —
    PowerShell 이 stdin 에서 답을 읽으려 하고, 거기 흘러오는 것은 우리가 보낸
    <b>다음 명령의 JSON 한 줄</b>이다. 그것을 답으로 먹고 명령 하나가 통째로 사라진다.

    $ConfirmPreference 를 모듈 범위에 두는 것으로는 막히지 않는다(실기 확인).
    호출마다 -Confirm:$false 를 명시해야 한다.

    그런데 모든 명령이 -Confirm 을 받는 것은 아니다. MicrosoftTeams 의 명령들은
    받지 않는 것이 있어, 무턱대고 붙이면 '그런 매개변수가 없다' 로 터진다.
    그래서 받을 수 있을 때만 붙인다.
.PARAMETER Command
    부를 명령 이름.
.PARAMETER Arguments
    이름 있는 인자들.
#>
function Invoke-TeavelWrite {
    param(
        [Parameter(Mandatory)][string] $Command,
        [hashtable] $Arguments = @{}
    )

    $cmd = Get-Command $Command -ErrorAction Stop

    $p = @{}
    foreach ($k in $Arguments.Keys) { $p[$k] = $Arguments[$k] }
    if ($cmd.Parameters.ContainsKey('Confirm')) { $p['Confirm'] = $false }

    & $cmd @p -ErrorAction Stop
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

            # Install-Module -Scope CurrentUser 는 우리가 고른 폴더가 아니라
            # PowerShell 이 정한 자리에 깐다. OneDrive 폴더 백업이 켜져 있으면 그 자리가
            # OneDrive 아래이고, 거기 놓인 DLL 은 자리표시자가 되어 로드에 실패할 수 있다.
            # 실기에서 MicrosoftTeams 가 OneDrive\문서 아래로 깔렸다(2026-08-17).
            #
            # 그래서 기본 자리가 OneDrive 아래면 Install-Module 을 쓰지 않고
            # 우리가 고른 폴더로 직접 받는다.
            $defaultsToOneDrive = $false
            try {
                $userScope = @($env:PSModulePath -split [IO.Path]::PathSeparator |
                               Where-Object { $_ -like "*\Users\$env:USERNAME\*" } | Select-Object -First 1)
                $defaultsToOneDrive = ($userScope -and $userScope[0] -match '\\OneDrive\\')
            } catch { }

            $ok = $false
            if ($defaultsToOneDrive) {
                $done.Add("$($spec.Name) — 기본 설치 자리가 OneDrive 아래라 다른 곳에 받습니다")
            }
            else {
                # ③④ 를 피해 Install-Module 을 먼저 시도하고, 막히면 직접 받는다.
                try {
                    Install-Module -Name $spec.Name -Scope CurrentUser -Force -AllowClobber `
                        -SkipPublisherCheck -ErrorAction Stop
                    $ok = $true
                    $done.Add("$($spec.Name) 설치 완료")
                } catch {
                    $done.Add("$($spec.Name) — 갤러리에서 직접 받습니다")
                }
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
            # 환경 변수로도 끄지만, 매개변수를 받는 판이면 그쪽이 확실하다.
            $p = @{ ShowBanner = $false }
            if ($Account) { $p['UserPrincipalName'] = $Account }
            if ((Get-Command Connect-ExchangeOnline).Parameters.ContainsKey('DisableWAM')) { $p['DisableWAM'] = $true }
            Connect-ExchangeOnline @p -ErrorAction Stop
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
            $p = @{}
            if ($Account) { $p['AccountId'] = $Account }
            if ((Get-Command Connect-MicrosoftTeams).Parameters.ContainsKey('DisableWAM')) { $p['DisableWAM'] = $true }
            Connect-MicrosoftTeams @p -ErrorAction Stop | Out-Null
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

        $team = Invoke-TeavelWrite -Command 'New-Team' -Arguments $params
        $d.Add("팀을 만들었습니다: $DisplayName")
        if ($tpl) { $d.Add("서식: $tpl") }
        # 부르는 쪽이 이 값으로 곧바로 채널을 붙인다. 형식을 바꾸면 저쪽도 바꿔야 한다.
        if ($team.GroupId) { $d.Add("GROUPID`t$($team.GroupId)") }

        # 소유자가 여럿이면 나머지를 붙인다.
        foreach ($o in ($Owners | Select-Object -Skip 1)) {
            try { Invoke-TeavelWrite -Command 'Add-TeamUser' -Arguments @{ GroupId = $team.GroupId; User = $o; Role = 'Owner' } | Out-Null; $d.Add("소유자 추가: $o") }
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

        $g = Invoke-TeavelWrite -Command 'New-UnifiedGroup' -Arguments $params
        $d.Add("그룹을 만들었습니다: $DisplayName")
        if ($g.PrimarySmtpAddress) { $d.Add("메일 주소: $($g.PrimarySmtpAddress)") }

        foreach ($o in ($Owners | Select-Object -Skip 1)) {
            try {
                Invoke-TeavelWrite -Command 'Add-UnifiedGroupLinks' -Arguments @{ Identity = $MailNickname; LinkType = 'Owners'; Links = $o }
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
    테넌트의 사람들을 훑는다. 누가 교사인지는 여기서 정하지 않는다.
.DESCRIPTION
    구성원을 배정하려면 먼저 누가 교사이고 누가 학생인지 알아야 한다.
    라이선스가 다르다는 것은 알지만, 그 라이선스를 어떻게 읽느냐가 문제였다.

      · Get-MsolUser 는 SKU 이름(STANDARDWOFFPACK_FACULTY)을 그대로 줬지만
        MSOnline 모듈이 2025년 5월에 퇴역했다.
      · Graph 는 SKU 를 주지만 관리자 동의 화면이 필요하다 — 우리가 피해 온 것이다.
      · Get-CsOnlineUser 는 SKU 대신 서비스 플랜 목록(AssignedPlan)을 준다.
        Teams 모듈이라 동의가 필요 없다.

    그래서 이렇게 한다. SKU 이름을 알아내려 들지 않고, **라이선스 꾸러미가 같은
    사람끼리 묶기만 한다.** 학교라면 큰 묶음 둘이 나온다 — 학생 수백 명과 교사 수십 명.
    어느 쪽이 교사인지는 관리자가 보면 안다. 이름 몇 개만 보여 주면 된다.

    이 방식은 SKU 이름이 무엇이든, 학교가 무슨 라이선스를 쓰든 똑같이 동작한다.
    마이크로소프트가 SKU 이름을 바꿔도 여기는 안 바뀐다.

    한 줄이 이렇게 나간다:
        USER<tab>UPN<tab>이름<tab>부서<tab>계정종류<tab>라이선스꾸러미
#>
function Get-TeavelTenantUser {
    param(
        [int] $Limit = 5000
    )

    Import-Module MicrosoftTeams -ErrorAction Stop

    $users = @(Get-CsOnlineUser -ResultSize $Limit -ErrorAction Stop)

    $d = New-Object System.Collections.Generic.List[string]
    foreach ($u in $users) {
        $upn = ''
        try { $upn = [string]$u.UserPrincipalName } catch { }
        if (-not $upn) { continue }

        $name = ''
        try { $name = [string]$u.DisplayName } catch { }

        $dept = ''
        try { if ($u.PSObject.Properties['Department']) { $dept = [string]$u.Department } } catch { }

        # 라이선스가 없는 계정은 IneligibleUser 로 온다 — 팀에 넣어도 못 쓴다.
        $kind = ''
        try { if ($u.PSObject.Properties['AccountType']) { $kind = [string]$u.AccountType } } catch { }

        # AssignedPlan 의 모양은 판마다 달라졌다(XML → JSON). 어느 쪽이든 이름만 뽑아 쓴다.
        # 못 뽑으면 빈 꾸러미가 되는데, 그러면 그 사람들끼리 한 묶음이 되어 눈에 띈다 —
        # 조용히 엉뚱한 묶음에 섞이는 것보다 낫다.
        $caps = New-Object System.Collections.Generic.List[string]
        try {
            foreach ($p in @($u.AssignedPlan)) {
                if (-not $p) { continue }
                $c = $null
                if ($p.PSObject.Properties['Capability'])        { $c = $p.Capability }
                elseif ($p.PSObject.Properties['ServicePlanId']) { $c = $p.ServicePlanId }
                else                                             { $c = [string]$p }

                # 꺼져 있는 플랜은 빼야 같은 라이선스끼리 같은 꾸러미가 된다.
                if ($p.PSObject.Properties['CapabilityStatus'] -and
                    $p.CapabilityStatus -and [string]$p.CapabilityStatus -ne 'Enabled') { continue }

                if ($c) { $caps.Add([string]$c) }
            }
        } catch { }

        $bundle = (($caps | Sort-Object -Unique) -join ',')

        $d.Add(("USER`t{0}`t{1}`t{2}`t{3}`t{4}" -f $upn, $name, $dept, $kind, $bundle))
    }

    New-TeavelResult -Message "사람 $($users.Count)명을 읽었습니다." -Details $d
}

<#
.SYNOPSIS
    사람들의 성·이름·표시이름을 읽는다. 고치지는 않는다.
.DESCRIPTION
    교육청 포털로 교사 계정을 만들면 성(LastName)과 이름(FirstName)이 나뉘어 들어간다.
    서양식 규격을 그대로 따른 것인데 한국에서는 못 쓴다 — 김·이·박이 학교마다 수십 명이라
    성만으로는 아무도 못 찾고, 화면에 '하늘 김' 처럼 뒤집혀 보이기도 한다.

    Get-CsOnlineUser 는 LastName 을 더 이상 주지 않는다. Exchange 의 Get-User 가 셋을 다 준다.

    한 줄이 이렇게 나간다:
        NAME<tab>UPN<tab>표시이름<tab>이름(First)<tab>성(Last)
#>
function Get-TeavelUserName {
    param(
        [int] $Limit = 5000
    )

    Import-Module ExchangeOnlineManagement -ErrorAction Stop

    $users = @(Get-User -ResultSize $Limit -ErrorAction Stop)

    $d = New-Object System.Collections.Generic.List[string]
    foreach ($u in $users) {
        $upn = ''
        try { $upn = [string]$u.UserPrincipalName } catch { }
        if (-not $upn) { continue }

        $disp = ''; $first = ''; $last = ''
        try { $disp  = [string]$u.DisplayName } catch { }
        try { $first = [string]$u.FirstName }  catch { }
        try { $last  = [string]$u.LastName }   catch { }

        $d.Add(("NAME`t{0}`t{1}`t{2}`t{3}" -f $upn, $disp, $first, $last))
    }

    New-TeavelResult -Message "사람 $($users.Count)명의 이름을 읽었습니다." -Details $d
}

<#
.SYNOPSIS
    표시 이름 하나를 고친다.
.DESCRIPTION
    Graph 없이 되는 길이다 — Exchange 의 Set-User 가 디렉터리의 표시 이름을 바꾼다.
    성·이름 칸은 건드리지 않는다. 그쪽은 다른 시스템이 쓰고 있을 수 있고,
    우리가 고치려는 것은 '화면에 보이는 이름' 하나뿐이다.

    Teams 앱에 반영되기까지 시간이 걸린다.
#>
function Set-TeavelDisplayName {
    param(
        [Parameter(Mandatory)][string] $Identity,
        [Parameter(Mandatory)][string] $DisplayName
    )

    Import-Module ExchangeOnlineManagement -ErrorAction Stop

    $before = ''
    try { $before = [string](Get-User -Identity $Identity -ErrorAction Stop).DisplayName } catch { }

    Invoke-TeavelWrite -Command 'Set-User' -Arguments @{ Identity = $Identity; DisplayName = $DisplayName }

    New-TeavelResult -Message "'$before' → '$DisplayName'" -Details @()
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
            Invoke-TeavelWrite -Command 'New-TeamChannel' -Arguments @{ GroupId = $GroupId; DisplayName = $name } | Out-Null
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
    팀에 이미 들어 있는 사람들을 읽는다.
.DESCRIPTION
    넣기 전에 이것부터 봐야 여러 번 돌려도 안전하다. 학기 중에 전학생이 한 명 왔을 때
    <b>그 한 명만</b> 넣을 수 있어야 하는데, 지금 누가 있는지 모르면 스물아홉 명을
    다시 넣으려 들게 된다.

    한 줄이 이렇게 나간다:
        MEMBER<tab>UPN<tab>역할
#>
function Get-TeavelTeamMember {
    param(
        [Parameter(Mandatory)][string] $GroupId
    )

    Import-Module MicrosoftTeams -ErrorAction Stop

    $users = @(Get-TeamUser -GroupId $GroupId -ErrorAction Stop)

    $d = New-Object System.Collections.Generic.List[string]
    foreach ($u in $users) {
        $upn = ''
        try { $upn = [string]$u.User } catch { }
        if (-not $upn) { continue }

        $role = ''
        try { $role = [string]$u.Role } catch { }

        $d.Add(("MEMBER`t{0}`t{1}" -f $upn, $role))
    }

    New-TeavelResult -Message "$($users.Count)명이 들어 있습니다." -Details $d
}

<#
.SYNOPSIS
    팀에 사람들을 넣는다. 이미 들어 있으면 건드리지 않는다.
.DESCRIPTION
    한 명씩 넣는다. 한 사람이 실패해도 나머지는 넣어야 하기 때문이다 —
    스물아홉 명이 들어갈 수 있는데 한 명 때문에 통째로 멈추면 안 된다.

    실패하는 까닭은 대개 둘이다. 계정이 아직 없거나(만들어야 한다),
    라이선스가 없어서(팀에 넣어도 못 들어온다). 둘 다 그대로 알려 준다.
.PARAMETER GroupId
    팀의 그룹 id.
.PARAMETER Users
    넣을 사람들의 로그인 아이디.
.PARAMETER Role
    Member(학생) 또는 Owner(교사).
#>
function Add-TeavelTeamMember {
    param(
        [Parameter(Mandatory)][string] $GroupId,
        [string[]] $Users = @(),
        [ValidateSet('Member', 'Owner')][string] $Role = 'Member'
    )

    Import-Module MicrosoftTeams -ErrorAction Stop

    $done   = New-Object System.Collections.Generic.List[string]
    $failed = New-Object System.Collections.Generic.List[string]

    foreach ($u in $Users) {
        if (-not $u) { continue }
        try {
            Invoke-TeavelWrite -Command 'Add-TeamUser' -Arguments @{
                GroupId = $GroupId; User = $u; Role = $Role
            } | Out-Null
            $done.Add($u)
        }
        catch {
            $failed.Add("$($u) — $($_.Exception.Message)")
        }
    }

    $d = New-Object System.Collections.Generic.List[string]
    foreach ($f in $failed) { $d.Add("실패: $f") }

    if ($failed.Count -gt 0 -and $done.Count -eq 0) {
        throw "$($failed.Count)명을 모두 넣지 못했습니다. $($failed[0])"
    }

    New-TeavelResult -Message "$($done.Count)명을 넣었습니다." -Details $d
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

    Invoke-TeavelWrite -Command 'Set-UnifiedGroup' -Arguments @{ Identity = $Identity; DisplayName = $NewDisplayName }
    $d.Add("이름: $before  →  $NewDisplayName")

    if ($NewAlias) {
        if ($NewAlias -notmatch '^[A-Za-z0-9._-]+$') {
            throw "별칭에는 영문자·숫자·붙임표·밑줄·점만 쓸 수 있습니다. (받은 값: $NewAlias)"
        }
        $oldSmtp = [string]$g.PrimarySmtpAddress
        Invoke-TeavelWrite -Command 'Set-UnifiedGroup' -Arguments @{ Identity = $Identity; Alias = $NewAlias }
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

    Invoke-TeavelWrite -Command 'Remove-UnifiedGroup' -Arguments @{ Identity = $Identity }

    $what.Add('')
    $what.Add('30일 안에는 관리 센터에서 복구할 수 있습니다. 그 뒤에는 되돌릴 수 없습니다.')

    New-TeavelResult -Message "'$($g.DisplayName)' 을(를) 지웠습니다." -Details $what
}

Export-ModuleMember -Function `
    Get-TeavelModuleDirectory, Get-TeavelM365Readiness, Install-TeavelM365Module, `
    Install-TeavelModuleFromGallery, Connect-TeavelM365, Invoke-TeavelWrite, `
    Get-TeavelM365Inventory, Get-TeavelTenantUser, `
    Get-TeavelUserName, Set-TeavelDisplayName, `
    New-TeavelM365Group, Sync-TeavelTeamChannel, `
    Get-TeavelTeamMember, Add-TeavelTeamMember, `
    Rename-TeavelM365Group, Remove-TeavelM365Group
