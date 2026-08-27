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
# Connect-ExchangeOnline 의 -Device 는 '필요 없는' 것이 아니라 <b>쓸 수 없는</b> 것이다.
# 그것은 동적 매개변수라 $PSEdition -eq 'Core'(PowerShell 7)일 때만 등록된다.
# 학교 PC 기본값인 5.1 에는 아예 없어서 Get-Command 의 매개변수 목록에도 안 보인다.
#
#   PS 5.1 · 창 있는 콘솔        브라우저(WAM) — 됨
#   PS 5.1 · 파이프 상주 프로세스  실패(창 핸들) → -DisableWAM 필요
#   PS 7                        -Device 사용 가능
#
# MicrosoftTeams 는 5.1 에서도 -UseDeviceAuthentication 이 있다.
# 둘의 인증 능력이 다르므로 한 덩어리로 다루면 안 된다.
$script:CoreModules = @(
    @{ Name = 'ExchangeOnlineManagement'; Min = [version]'3.0.0'; What = '그룹·메일' }
    @{ Name = 'MicrosoftTeams';           Min = [version]'4.0.0'; What = '팀'       }
)

<#
.SYNOPSIS
    이 경로가 OneDrive 안인지.
.DESCRIPTION
    <b>업무용 OneDrive 는 폴더 이름이 다르다.</b> 개인용은 `OneDrive` 지만
    학교·회사 계정은 `OneDrive - 늘푸른중학교` 처럼 <b>조직명이 붙는다.</b>

    그래서 예전 판정(`-notmatch '\\OneDrive\\'`)은 업무용을 통째로 놓쳤다.
    이 함수가 있는 까닭이 바로 그 자리를 피하는 것인데, 정확히 그 자리를 골랐다:

        Get-TeavelModuleDirectory
        → C:\Users\user\OneDrive - 늘푸른중학교\문서\WindowsPowerShell\Modules

    그 결과 모듈이 OneDrive 아래 깔리고, 파일 온디맨드가 DLL 을 자리표시자로 만들어
    로드가 실패한다. 실기에서 MicrosoftTeams 가 그렇게 깔렸다(2026-08-17).

    이름으로 맞히는 대신 <b>환경 변수를 먼저 본다</b> — 조직명을 타지 않아 정확하다.
    없을 때만 이름 규칙으로 떨어진다.
#>
function Test-TeavelUnderOneDrive {
    param([string] $Path)

    if (-not $Path) { return $false }

    foreach ($root in @($env:OneDrive, $env:OneDriveCommercial, $env:OneDriveConsumer)) {
        if ($root -and $Path -like "$root*") { return $true }
    }

    # 환경 변수가 없을 때의 버팀목. 'OneDrive' 와 'OneDrive - 조직명' 을 모두 잡는다.
    return ($Path -match '\\OneDrive( - [^\\]+)?\\')
}

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
<#
    우리가 받아 둔 모듈 폴더를 이 세션에 알려 준다.

    PowerShell 은 이 폴더를 기본으로 보지 않는다. 예전에는 설치를 돌린 그 세션에서만
    붙여 줬는데, <b>모듈을 깐 뒤 세션을 새로 띄우면 방금 깐 것을 못 찾았다.</b>
    '설치했는데도 아직 모자랍니다' 가 그것이다(2026-08-27).

    그래서 켤 때마다 한 번 붙인다. 이 프로세스에만 걸리고 교사 PC 의 설정은 건드리지 않는다.

    <b>맨 뒤에 붙인다.</b> Import-Module 은 판 번호가 아니라 PSModulePath 차례로 고르므로,
    앞에 끼우면 누가 일부러 앞세워 둔 것을 밀어낸다. 가짜 테넌트가 그렇게 밀려나
    진짜 모듈이 올라오고 로그인에서 멈춘 적이 있다(2026-08-27).
#>
function Add-TeavelModulePath {
    param()

    $ours = $null
    try {
        if ($env:LOCALAPPDATA) { $ours = Join-Path $env:LOCALAPPDATA 'Teaveloper\Modules' }
    } catch { }

    if (-not $ours) { return }
    if ($env:PSModulePath -and (@($env:PSModulePath -split [IO.Path]::PathSeparator) -contains $ours)) { return }

    $env:PSModulePath = $env:PSModulePath + [IO.Path]::PathSeparator + $ours
}

function Get-TeavelModuleDirectory {
    param()

    # 구분자를 ';' 로 박아 두면 리눅스 pwsh 에서 한 덩어리가 되어 아무것도 못 찾는다.
    # 제품은 Windows 에서만 돌지만, 리눅스에서 돌려 볼 수 있어야 고칠 수 있다.
    $candidates = @($env:PSModulePath -split [IO.Path]::PathSeparator | Where-Object { $_ })

    # 내 계정 아래이면서 OneDrive 가 아닌 것
    $mine = $candidates | Where-Object {
        $_ -like "*\Users\$env:USERNAME\*" -and -not (Test-TeavelUnderOneDrive $_)
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
        # 대괄호는 경로에서 와일드카드다. '[Content_Types].xml' 을 그냥 넘기면
        # 글자 묶음으로 읽혀 아무것도 안 지워진다 — -LiteralPath 라야 한다.
        foreach ($junk in '_rels', 'package', '[Content_Types].xml', "$Name.nuspec") {
            $p = Join-Path $target $junk
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
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
            #
            # 그 '기본 자리' 는 <b>'문서' 폴더 아래</b>다 — Install-Module -Scope CurrentUser 가
            # 거기 깔기 때문이다. 문서 폴더가 OneDrive 로 옮겨져 있으면 모듈도 거기로 간다.
            #
            # 예전에는 PSModulePath 에서 '내 계정' 으로 보이는 첫 항목을 보고 판단했는데,
            # 우리가 넣어 둔 Teaveloper\Modules 가 그 조건에 먼저 걸려 <b>이 검사가 늘 거짓</b>이
            # 됐다. 그래서 모듈이 OneDrive 아래로 깔렸고, 파일 온디맨드가 DLL 을 자리표시자로
            # 만들어 상주 세션이 통째로 죽었다. 실기에서 그랬다(2026-08-27).
            #
            # 차례에 기대지 않고 문서 폴더를 직접 묻는다.
            $defaultsToOneDrive = $false
            try {
                $docs = [Environment]::GetFolderPath('MyDocuments')
                if ($docs) {
                    $defaultsToOneDrive = Test-TeavelUnderOneDrive (Join-Path $docs 'WindowsPowerShell\Modules')
                }
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
                    Add-TeavelModulePath
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
        $teamsOk = Test-TeavelTeamsReady
        if (-not $teamsOk) {
            Write-Host ''
            Write-Host '  팀 작업을 위해 한 번 더 로그인 창이 열립니다. 같은 계정으로 하시면 됩니다.'
            Write-Host ''
            $p = @{}
            if ($Account) { $p['AccountId'] = $Account }
            if ((Get-Command Connect-MicrosoftTeams).Parameters.ContainsKey('DisableWAM')) { $p['DisableWAM'] = $true }

            # ── 두 길을 차례로 해 본다 ──
            #
            # 창 방식(브라우저)은 이 상주 세션에 창이 없어서 실패하는 판이 있다(2026-08-17):
            #   · 창이 아예 안 뜨고 'A window handle must be configured'
            #   · 창은 떠서 로그인까지 했는데 'AADSTS900561 — GET 요청을 받았습니다'
            #
            # 그래서 한동안 코드 방식만 썼다. 그런데 코드 방식도 반드시 되는 길이 아니었다.
            # 학교 테넌트가 조건부 액세스로 <b>코드 방식 자체를 막아</b> 둘 수 있다(2026-08-27):
            #
            #     로그인에 성공했지만 이 리소스에 액세스하기 위한 조건을 충족하지 않습니다.
            #     ... 관리자가 제한하는 브라우저, 앱, 위치 또는 <b>인증 흐름</b>에서 ...
            #
            # 어느 쪽이 막혔는지는 테넌트마다 다르고 우리가 미리 알 수 없다.
            # 그러니 짐작하지 말고 <b>둘 다 해 본다.</b> 창 방식이 먼저인 것은,
            # 되기만 하면 그쪽이 손이 덜 가기 때문이다.
            $tries = New-Object System.Collections.Generic.List[string]

            try {
                Connect-MicrosoftTeams @p -ErrorAction Stop | Out-Null
            }
            catch {
                $tries.Add('창 방식: ' + $_.Exception.Message)
            }

            if (-not (Test-TeavelTeamsReady)) {
                $byCode = $false
                foreach ($name in 'UseDeviceAuthentication', 'DeviceCode', 'Device') {
                    if ((Get-Command Connect-MicrosoftTeams).Parameters.ContainsKey($name)) { $byCode = $true; break }
                }

                if ($byCode) {
                    Write-Host ''
                    Write-Host '  창 방식으로는 안 됐습니다. 코드 방식으로 해 보겠습니다.'
                    Write-TeavelDeviceLoginNotice
                    try { Connect-TeavelTeamsByCode -Account $Account }
                    catch { $tries.Add('코드 방식: ' + $_.Exception.Message) }
                }
            }

            # 붙었는지 반드시 다시 본다.
            #
            # 연결 명령이 <b>조용히 돌아오는 판</b>이 있다. 그러면 여기서는 성공으로 보고하고,
            # 그 뒤의 팀 작업이 하나도 빠짐없이
            # 'You must call the Connect-MicrosoftTeams cmdlet before calling any other cmdlets'
            # 로 터진다. 관리자는 방금 '연결했습니다' 를 봤으므로 무엇이 잘못됐는지 알 수 없다.
            # 실기에서 그랬다(2026-08-27).
            if (Test-TeavelTeamsReady) { $teamsOk = $true }
            else {
                $why = @($tries) -join ' / '

                # 조건부 액세스에 막힌 것이면 그렇게 말해야 한다.
                # '로그인을 다시 해 보세요' 라고 하면 될 때까지 다시 하시다가 시간만 버린다.
                $blocked = $why -match 'AADSTS53003|AADSTS500011|Conditional Access|액세스 권한이 없습니다|인증 흐름'

                if ($blocked) {
                    throw ('학교 정책이 이 로그인을 막고 있습니다. 다시 시도하셔도 같습니다.' + "`n" +
                           '팀을 <b>만드는 것</b>만 이 로그인이 필요합니다 — 구성원 읽기·그룹에 넣기는 그대로 됩니다.' + "`n" +
                           '팀 만들기는 정식 관리 센터나 Teams 앱에서 하시고, 사람 넣기는 여기서 하시면 됩니다.' + "`n" +
                           '까닭: ' + $why)
                }

                throw ('팀에 붙지 못했습니다. ' + $why)
            }
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
    창이 안 뜰 때 쓰는 로그인 안내. 코드를 적어 넣는 방식이다.
#>
function Write-TeavelDeviceLoginNotice {
    Write-Host ''
    Write-Host '  팀 로그인은 코드를 적어 넣는 방식으로 합니다.'
    Write-Host ''
    Write-Host '  ┌─────────────────────────────────────────────────┐'
    Write-Host '  │  잠시 뒤 아래에 주소와 짧은 코드가 나옵니다      │'
    Write-Host '  └─────────────────────────────────────────────────┘'
    Write-Host ''
    Write-Host '  ① 인터넷 창을 직접 여세요 (엣지·크롬 아무거나)'
    Write-Host '  ② 아래에 나오는 주소를 주소창에 칩니다'
    Write-Host '  ③ 아래에 나오는 코드를 넣고 [다음]'
    Write-Host '  ④ 학교 계정으로 로그인합니다'
    Write-Host ''
    Write-Host '  휴대전화로 하셔도 됩니다. 같은 주소·같은 코드입니다.'
    Write-Host ''
}

<#
.SYNOPSIS
    코드를 적어 넣는 방식으로 팀에 연결한다.
.DESCRIPTION
    상주 세션에는 창이 없어 브라우저 로그인이 실패할 수 있다. 이 방식은 창이 필요 없다 —
    화면에 주소와 코드가 나오고, 사람이 아무 기기에서나 그것을 넣으면 된다.

    매개변수 이름이 판마다 다르므로 받을 수 있는 것을 찾아 쓴다.
#>
<#
    팀 cmdlet 을 지금 쓸 수 있는지 본다.

    <b>Get-CsTenant 로 보면 안 된다.</b> 그쪽은 Cs* 계열이라 Team* 계열이 못 쓰는
    상태에서도 성공한다. 실기에서 그래서 '팀: 연결했습니다' 라고 해 놓고, 그다음
    팀 작업이 하나도 빠짐없이
    'You must call the Connect-MicrosoftTeams cmdlet before calling any other cmdlets'
    로 터졌다(2026-08-27).

    그래서 <b>우리가 실제로 부르는 것</b>으로 본다. 없는 별칭을 물어보므로 테넌트가 커도 빠르고,
    '못 찾았다' 는 답은 곧 '붙어 있다' 는 뜻이다.
#>
function Test-TeavelTeamsReady {
    param()

    try {
        $null = Get-Team -MailNickName 'teavel-probe-none' -ErrorAction Stop
        return $true
    }
    catch {
        if ([string]$_.Exception.Message -match 'Connect-MicrosoftTeams') { return $false }

        # 다른 까닭이면 붙어 있는 것으로 본다. 진짜 문제라면 실제 작업에서
        # 그 자리의 말로 터지고, 그게 여기서 짐작한 말보다 낫다.
        return $true
    }
}

function Connect-TeavelTeamsByCode {
    param(
        [string] $Account
    )

    Import-Module MicrosoftTeams -ErrorAction Stop

    $cmd = Get-Command Connect-MicrosoftTeams
    $p = @{}
    if ($Account) { $p['AccountId'] = $Account }

    foreach ($name in 'UseDeviceAuthentication', 'DeviceCode', 'Device') {
        if ($cmd.Parameters.ContainsKey($name)) { $p[$name] = $true; break }
    }

    if ($p.Count -eq 0 -or -not ($p.Keys | Where-Object { $_ -ne 'AccountId' })) {
        throw '이 판의 Teams 모듈은 코드로 로그인하는 방법을 지원하지 않습니다. ' +
              'PowerShell 창에서 Connect-MicrosoftTeams 를 먼저 실행하신 뒤 다시 시도해 주세요.'
    }

    Connect-MicrosoftTeams @p -ErrorAction Stop | Out-Null
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
<#
    학교 사람 목록.

    ■ Exchange 로 읽는다

    예전에는 Get-CsOnlineUser(Teams)로 읽었다. 그런데 화면은 메일·그룹(Exchange)만 붙은 채
    뜨므로, 구성원을 한 번 보려고 <b>코드 방식 로그인을 한 번 더</b> 해야 했다.
    실기에서 그 자리에서 멈췄다 — 창이 뜨는 줄 알고 기다리셨다(2026-08-27).

    Get-User 가 필요한 것을 다 준다(실측): UserPrincipalName · DisplayName · Department ·
    WhenCreated · AccountDisabled. 그러면 두 번째 로그인 없이 명부가 채워진다.

    ■ 라이선스만 Teams 에 있다

    AssignedPlan 은 Exchange 에 없다. 그래서 <b>이미 붙어 있을 때만</b> 채우고, 아니면 비운다.
    비면 화면이 '모름' 이라고 말한다 — 없는 것으로 단정하지 않는다.
    교사·학생 가르기는 표시 이름의 학번으로도 되므로 명부는 그대로 쓸 만하다.
#>
function Get-TeavelTenantUser {
    param(
        [int] $Limit = 5000
    )

    Import-Module ExchangeOnlineManagement -ErrorAction Stop

    $rows = New-Object System.Collections.Generic.List[object]

    foreach ($e in @(Get-User -ResultSize Unlimited -ErrorAction Stop)) {
        $upn = ''
        try { $upn = [string]$e.UserPrincipalName } catch { }
        if (-not $upn) { continue }

        $name = ''
        try { $name = [string]$e.DisplayName } catch { }

        $dept = ''
        try { if ($e.PSObject.Properties['Department']) { $dept = [string]$e.Department } } catch { }

        $made = ''
        try { if ($e.WhenCreated) { $made = ([datetime]$e.WhenCreated).ToString('yyyy-MM-dd') } } catch { }

        # 모르면 빈 칸으로 둔다 — '아니다' 로 단정하면 이미 막아 둔 졸업생이 멀쩡해 보인다.
        $blocked = ''
        try {
            if ($e.PSObject.Properties['AccountDisabled'] -and $null -ne $e.AccountDisabled) {
                $blocked = $(if ($e.AccountDisabled) { '1' } else { '0' })
            }
        } catch { }

        $rows.Add([pscustomobject]@{
            Upn = $upn; Name = $name; Dept = $dept; Kind = ''
            Bundle = ''; Made = $made; Blocked = $blocked
        })
    }

    # 라이선스는 Teams 에만 있다. 붙어 있을 때만 얹는다 — 이것 때문에 로그인을 시키지 않는다.
    try {
        Import-Module MicrosoftTeams -ErrorAction Stop
        $null = Get-CsTenant -ErrorAction Stop

        $plan = @{}
        $kind = @{}
        foreach ($u in @(Get-CsOnlineUser -ResultSize $Limit -ErrorAction Stop)) {
            $id = ''
            try { $id = [string]$u.UserPrincipalName } catch { }
            if (-not $id) { continue }

            try { if ($u.PSObject.Properties['AccountType']) { $kind[$id] = [string]$u.AccountType } } catch { }

            # AssignedPlan 의 모양은 판마다 달라졌다(XML → JSON). 어느 쪽이든 이름만 뽑아 쓴다.
            $caps = New-Object System.Collections.Generic.List[string]
            try {
                foreach ($x in @($u.AssignedPlan)) {
                    if (-not $x) { continue }
                    $c = $null
                    if ($x.PSObject.Properties['Capability'])        { $c = $x.Capability }
                    elseif ($x.PSObject.Properties['ServicePlanId']) { $c = $x.ServicePlanId }
                    else                                              { $c = [string]$x }

                    # 꺼져 있는 플랜은 빼야 같은 라이선스끼리 같은 꾸러미가 된다.
                    if ($x.PSObject.Properties['CapabilityStatus'] -and
                        $x.CapabilityStatus -and [string]$x.CapabilityStatus -ne 'Enabled') { continue }

                    if ($c) { $caps.Add([string]$c) }
                }
            } catch { }

            $plan[$id] = (($caps | Sort-Object -Unique) -join ',')
        }

        foreach ($r in $rows) {
            if ($plan.ContainsKey($r.Upn)) { $r.Bundle = $plan[$r.Upn] }
            if ($kind.ContainsKey($r.Upn)) { $r.Kind   = $kind[$r.Upn] }
        }
    } catch {
        # 팀에 안 붙어 있다. 명부는 그대로 쓰고 라이선스 칸만 빈다.
    }

    $d = New-Object System.Collections.Generic.List[string]
    foreach ($r in $rows) {
        $d.Add(("USER`t{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}" -f $r.Upn, $r.Name, $r.Dept, $r.Kind, $r.Bundle, $r.Made, $r.Blocked))
    }

    New-TeavelResult -Message "사람 $($rows.Count)명을 읽었습니다." -Details $d
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
    $rows = New-Object System.Collections.Generic.List[object]

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
<#
    그룹에서 사람의 로그인 아이디를 꺼낸다.

    Get-UnifiedGroupLinks 가 주는 것은 받는 사람 개체라, 판마다 들고 있는 칸이 조금씩
    다르다. WindowsLiveID 가 로그인 아이디이지만 없는 판이 있고 그때는 대표 메일 주소가
    같은 값이다. 짐작하지 말고 있는 것부터 차례로 본다.
#>
function Get-TeavelLinkUpn {
    param($Recipient)

    foreach ($field in 'WindowsLiveID', 'UserPrincipalName', 'PrimarySmtpAddress') {
        $v = ''
        try { $v = [string]$Recipient.$field } catch { }
        if ($v -and $v.Contains('@')) { return $v }
    }
    ''
}

<#
.SYNOPSIS
    팀에 누가 들어 있는지 읽는다. <b>팀에 붙지 않고</b> 읽는다.
.DESCRIPTION
    팀 구성원은 그 팀을 받치는 M365 그룹의 구성원이다 — 같은 것을 Teams 가 자기 말로
    보여 줄 뿐이다. 그래서 이미 붙어 있는 Exchange 로 그대로 읽을 수 있다.

    전에는 Get-TeamUser 를 썼고, 그것 하나 때문에 로그인을 한 번 더 시켰다.
    그 두 번째 로그인은 창이 없어 코드 방식으로 갔는데, 학교 테넌트가 조건부 액세스로
    <b>코드 방식 자체를 막아</b> 두어 통째로 막혔다(2026-08-27).

        로그인에 성공했지만 이 리소스에 액세스하기 위한 조건을 충족하지 않습니다.
        ... 관리자가 제한하는 브라우저, 앱, 위치 또는 <b>인증 흐름</b>에서 ...

    Exchange 로 읽으면 그 문이 아예 필요 없다. 로그인은 한 번으로 끝난다 —
    원래 그래야 하는 것이었다.
#>
function Get-TeavelTeamMember {
    param(
        [Parameter(Mandatory)][string] $GroupId
    )

    # 소유자를 못 읽어도 구성원은 읽어야 한다. 역할이 덜 정확할 뿐 목록은 나온다.
    $owners = @()
    try { $owners = @(Get-UnifiedGroupLinks -Identity $GroupId -LinkType Owners -ResultSize Unlimited -ErrorAction Stop) }
    catch { }

    $members = @(Get-UnifiedGroupLinks -Identity $GroupId -LinkType Members -ResultSize Unlimited -ErrorAction Stop)

    $ownerSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($o in $owners) {
        $u = Get-TeavelLinkUpn $o
        if ($u) { $null = $ownerSet.Add($u) }
    }

    $d = New-Object System.Collections.Generic.List[string]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

    foreach ($m in $members) {
        $upn = Get-TeavelLinkUpn $m
        if (-not $upn) { continue }
        if (-not $seen.Add($upn)) { continue }
        $role = if ($ownerSet.Contains($upn)) { 'Owner' } else { 'Member' }
        $d.Add(("MEMBER`t{0}`t{1}" -f $upn, $role))
    }

    # 소유자인데 구성원 목록에는 없을 수 있다. 팀에서는 보이므로 함께 센다.
    foreach ($u in $ownerSet) {
        if ($seen.Add($u)) { $d.Add(("MEMBER`t{0}`tOwner" -f $u)) }
    }

    New-TeavelResult -Message "$($seen.Count)명이 들어 있습니다." -Details $d
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

    # 팀에 붙지 않고 넣는다. 팀 구성원은 그 팀을 받치는 M365 그룹의 구성원이라
    # 이미 붙어 있는 Exchange 로 그대로 넣을 수 있다. Get-TeavelTeamMember 와 같은 까닭이다.
    $link = if ($Role -eq 'Owner') { 'Owners' } else { 'Members' }

    $done   = New-Object System.Collections.Generic.List[string]
    $failed = New-Object System.Collections.Generic.List[string]

    foreach ($u in $Users) {
        if (-not $u) { continue }
        try {
            Invoke-TeavelWrite -Command 'Add-UnifiedGroupLinks' -Arguments @{
                Identity = $GroupId; LinkType = $link; Links = $u
            } | Out-Null

            # 소유자는 구성원이기도 해야 한다. 진짜 Teams 가 그렇게 만든다 —
            # 소유자로만 넣으면 팀 목록에 안 보이는 판이 있다.
            if ($Role -eq 'Owner') {
                try {
                    Invoke-TeavelWrite -Command 'Add-UnifiedGroupLinks' -Arguments @{
                        Identity = $GroupId; LinkType = 'Members'; Links = $u
                    } | Out-Null
                } catch { }
            }

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
    팀에서 학생들을 내보낸다. 소유자(교사)는 그대로 둔다.
.DESCRIPTION
    지난 학년도 팀을 보관할 때 쓴다. 팀과 그 안의 파일·대화는 그대로 두고
    구성원만 비우는 것이라, 지우는 것과 전혀 다르다 —
    나중에 자료를 찾아볼 일이 생겨도 남아 있다.

    소유자는 빼지 않는다. 소유자가 아무도 없는 팀은 관리 화면에서 손대기 까다로워지고,
    담당 선생님이 나중에 자료를 찾아볼 길도 막힌다.

    한 명씩 뺀다. 한 사람이 실패해도 나머지는 빼야 하기 때문이다.
#>
function Remove-TeavelTeamStudent {
    param(
        [Parameter(Mandatory)][string] $GroupId,
        [string[]] $Keep = @()
    )

    # 팀에 붙지 않고 뺀다 — Get-TeavelTeamMember 와 같은 까닭이다.
    $owners = @()
    try { $owners = @(Get-UnifiedGroupLinks -Identity $GroupId -LinkType Owners -ResultSize Unlimited -ErrorAction Stop |
                        ForEach-Object { Get-TeavelLinkUpn $_ } | Where-Object { $_ }) }
    catch { }

    $members = @(Get-UnifiedGroupLinks -Identity $GroupId -LinkType Members -ResultSize Unlimited -ErrorAction Stop |
                    ForEach-Object { Get-TeavelLinkUpn $_ } | Where-Object { $_ })

    $keepSet = @($owners + $Keep | Where-Object { $_ })
    $targets = @($members | Where-Object { $keepSet -notcontains $_ })

    $done   = New-Object System.Collections.Generic.List[string]
    $failed = New-Object System.Collections.Generic.List[string]

    foreach ($u in $targets) {
        try {
            Invoke-TeavelWrite -Command 'Remove-UnifiedGroupLinks' -Arguments @{
                Identity = $GroupId; LinkType = 'Members'; Links = $u
            } | Out-Null
            $done.Add($u)
        }
        catch { $failed.Add("$($u) — $($_.Exception.Message)") }
    }

    $d = New-Object System.Collections.Generic.List[string]
    $d.Add("소유자 $($owners.Count)명은 그대로 두었습니다.")
    foreach ($f in $failed) { $d.Add("실패: $f") }

    New-TeavelResult -Message "$($done.Count)명을 내보냈습니다." -Details $d
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

# ═══════════════════════════ 학번 읽기 · 묶음 ═══════════════════════════

<#
.SYNOPSIS
    표시 이름에서 학년·반·번호·이름을 뽑는다. 테넌트를 건드리지 않는다.
.DESCRIPTION
    <b>디렉터리 속성으로는 아무것도 가를 수 없다.</b> 실제 학교 테넌트 395명에서
    `Department`·`Title`·`City` 가 채워진 사람은 0명이었다(2026-08-19, nprm.goe.go.kr).

    대신 표시 이름에 학번이 박혀 있었다.

        10101강민서   ->  1학년 01반 01번 강민서
        30410박다움   ->  3학년 04반 10번 박다움

    정규식 하나로 210명이 풀렸고, 팀 구성원 수와 교차검증도 맞았다
    (`3학년_과학` 203명 = 학번 3학년 202 + 소유자 1).

    ■ 이 값은 '지금 학년' 이 아니다

    학번은 계정을 만들 때 찍힌 값이라, 매년 고쳐 주지 않으면 그대로 남는다.
    `30101` 이 올해 3학년이라는 뜻이 <b>아니다.</b> 그래서 이 함수는 학년을 읽어 줄 뿐
    <b>졸업생인지 판정하지 않는다.</b> 판정은 사람이 한다.
.PARAMETER Pattern
    학번 형식. 학교마다 다를 수 있어 밖에서 받는다. 기본은 학년1+반2+번호2.
.EXAMPLE
    Get-TeavelTenantUser | Get-TeavelStudentId
#>
function Get-TeavelStudentId {
    [CmdletBinding()]
    param(
        # 파이프라인으로 받는다. 이걸 빠뜨리면 조용히 빈 결과가 나온다(실기에서 당했다).
        [Parameter(Mandatory, ValueFromPipeline, ValueFromPipelineByPropertyName)]
        [AllowEmptyString()]
        [string] $DisplayName,

        [string] $Pattern = '^(?<g>[1-9])(?<k>\d{2})(?<n>\d{2})(?<p>.+)$'
    )

    process {
        $name = if ($null -eq $DisplayName) { '' } else { $DisplayName.Trim() }

        if ($name -match $Pattern) {
            [PSCustomObject]@{
                DisplayName = $DisplayName
                HasId       = $true
                StudentId   = $Matches['g'] + $Matches['k'] + $Matches['n']
                Grade       = [int] $Matches['g']
                Class       = [int] $Matches['k']
                Number      = [int] $Matches['n']
                PersonName  = $Matches['p'].Trim()
            }
        }
        else {
            [PSCustomObject]@{
                DisplayName = $DisplayName
                HasId       = $false
                StudentId   = ''
                Grade       = $null
                Class       = $null
                Number      = $null
                PersonName  = $name
            }
        }
    }
}

<#
.SYNOPSIS
    학번으로 학년·반 묶음을 만든다. 테넌트를 건드리지 않는다.
.DESCRIPTION
    졸업생 정리는 <b>학년 단위</b>로 벌어진다. 202명을 한 명씩 체크하는 화면은
    그 앞에서 무용지물이다. 그래서 묶어서 고를 수 있게 한다.

    ■ 묶음마다 계정 생성 연도를 함께 준다

    학번은 만든 때의 학년이라 그것만으로는 졸업 여부를 알 수 없다. 생성 연도가 있어야
    사람이 읽어 낼 수 있다 — 실기에서 '3학년 202명 중 199명이 2025년 생성' 이라는 사실이
    2025학년도 3학년 = 2026년 2월 졸업이라는 판단의 근거가 됐다.

    <b>이 함수는 졸업생을 고르지 않는다.</b> 근거를 늘어놓을 뿐이다.
    "졸업생 202명을 찾았습니다" 라고 말하는 순간 그 도구는 위험해진다.
.PARAMETER Users
    DisplayName 을 가진 개체들. WhenCreated 가 있으면 생성 연도로 쓴다.
#>
function Get-TeavelCohort {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Users,
        [string] $Pattern = '^(?<g>[1-9])(?<k>\d{2})(?<n>\d{2})(?<p>.+)$'
    )

    # 대소문자를 구분해 센다.
    #
    # PowerShell 의 @{} 는 기본이 대소문자 무시라, 'Test' 와 'TEST' 가 한 칸에 합쳐진다.
    # 실기에서 두 그룹이 한 줄로 합쳐져 구성원 수가 배로 보였다. 세는 곳에서는 반드시 구분한다.
    $buckets = New-Object 'System.Collections.Hashtable' ([StringComparer]::Ordinal)
    $order = New-Object System.Collections.Generic.List[string]

    foreach ($u in @($Users)) {
        $display = [string] $u.DisplayName
        $id = $display | Get-TeavelStudentId -Pattern $Pattern

        # 한글이 붙은 보간은 변수명으로 읽힌다 — "$g학년" 은 $g학년 이라는 변수다.
        # 반드시 중괄호로 감싼다.
        $key = if ($id.HasId) { "grade:$($id.Grade)|class:$($id.Class)" } else { 'none' }

        if (-not $buckets.ContainsKey($key)) {
            $buckets[$key] = [PSCustomObject]@{
                Kind    = if ($id.HasId) { 'class' } else { 'none' }
                Grade   = $id.Grade
                Class   = $id.Class
                Label   = if ($id.HasId) { "$($id.Grade)학년 $($id.Class)반" } else { '학번 없음' }
                Members = New-Object System.Collections.Generic.List[object]
                Years   = New-Object System.Collections.Generic.List[int]
            }
            $order.Add($key)
        }

        [void] $buckets[$key].Members.Add($id)

        $when = $u.WhenCreated
        if ($when -is [datetime]) { [void] $buckets[$key].Years.Add($when.Year) }
    }

    foreach ($key in $order) {
        $b = $buckets[$key]

        # @($list) 는 List[object] 를 펼치지 못하고 통째로 한 칸에 넣는다.
        # ToArray() 를 거쳐야 한다 — 실기에서 여기서 수가 1로 나왔다.
        $members = $b.Members.ToArray()
        $years = @($b.Years.ToArray() | Sort-Object -Unique)

        [PSCustomObject]@{
            Kind         = $b.Kind
            Grade        = $b.Grade
            Class        = $b.Class
            Label        = $b.Label
            Count        = $members.Count
            CreatedYears = $years
            Members      = $members
        }
    }
}

<#
    ── 비밀번호 ──────────────────────────────────────────────────────────

    이것 하나만 Microsoft Graph 가 필요하다.

    Exchange 에도 Teams 에도 비밀번호 cmdlet 이 없고(실측), MSOnline·AzureAD 는
    2025-05-30 에 은퇴했다. 남은 길이 Graph 하나뿐이다.

    그래서 여기만 별도로 붙는다. 그룹·팀·구성원은 지금까지처럼 자체 모듈로 하고,
    Graph 는 관리자가 비밀번호를 실제로 바꾸려 할 때 그때 연결한다.
    권한도 딱 하나만 받는다 — User-PasswordProfile.ReadWrite.All.
#>

function Get-TeavelGraphReadiness {
    param()

    $need = @('Microsoft.Graph.Authentication', 'Microsoft.Graph.Users')
    $miss = @()
    $have = New-Object System.Collections.Generic.List[string]

    foreach ($n in $need) {
        $m = Get-Module -ListAvailable -Name $n | Sort-Object Version -Descending | Select-Object -First 1
        if ($m) { $have.Add(('{0,-32} {1}' -f $n, $m.Version)) } else { $miss += $n }
    }

    if ($miss.Count -gt 0) {
        $have.Add('')
        $have.Add(('없는 것: ' + ($miss -join ', ')))
        return New-TeavelResult -Message '비밀번호를 바꾸려면 모듈을 더 갖춰야 합니다.' -Details $have.ToArray()
    }

    New-TeavelResult -Message '비밀번호를 바꿀 준비가 돼 있습니다.' -Details $have.ToArray()
}

function Install-TeavelGraphModule {
    param([string] $Version = '2.39.0')

    $dir = Get-TeavelModuleDirectory
    $d = New-Object System.Collections.Generic.List[string]

    foreach ($n in @('Microsoft.Graph.Authentication', 'Microsoft.Graph.Users')) {
        if (Get-Module -ListAvailable -Name $n) { $d.Add("$n — 이미 있습니다"); continue }
        Write-Host "  $n 내려받는 중…"
        Install-TeavelModuleFromGallery -Name $n -Version $Version -Directory $dir
        $d.Add("$n $Version — 받았습니다")
    }

    New-TeavelResult -Message '갖췄습니다.' -Details $d.ToArray()
}

function Connect-TeavelGraph {
    param([string[]] $Scopes = @('User-PasswordProfile.ReadWrite.All'))

    Import-Module Microsoft.Graph.Authentication -ErrorAction Stop

    # 이미 그 권한으로 붙어 있으면 아무 말 없이 그대로 쓴다.
    $ctx = $null
    try { $ctx = Get-MgContext -ErrorAction Stop } catch { }

    if ($ctx -and $ctx.Scopes) {
        $missing = @($Scopes | Where-Object { $_ -notin $ctx.Scopes })
        if ($missing.Count -eq 0) {
            return New-TeavelResult -Message '이미 연결돼 있습니다.' -Details @("계정: $($ctx.Account)")
        }
    }

    # ── 멈추기 전에 먼저 말한다 ──
    # 이 로그인은 앞의 둘과 다르다. 처음 보는 동의 화면이 뜨고, 거기서 관리자가
    # 겁을 먹고 [취소] 를 누르면 이 기능이 통째로 막힌다. 무엇에 동의하는지 미리 적는다.
    #
    # 그리고 무엇에 쓰는 권한이냐에 따라 적히는 말이 달라야 한다. 비밀번호를 바꿀 때와
    # 계정을 지울 때는 동의 화면에 뜨는 권한 이름부터 다르다. 안내가 실제 화면과
    # 다르면 관리자는 '내가 뭘 잘못 눌렀나' 하고 [취소] 를 누른다.
    $wide = $Scopes -contains 'User.ReadWrite.All'

    if ($wide) {
        $head  = '계정을 지우려면 권한을 한 번 허용해야 합니다'
        $shown = '사용자 읽기/쓰기 (User.ReadWrite.All)'
        $note  = @(
            '  이 허용은 비밀번호를 바꿀 때 쓰던 것보다 넓습니다 — 계정을 지울 수 있기 때문입니다.',
            '  지운 계정은 30일 안에는 관리 센터에서 되살릴 수 있고, 그 뒤에는 되살릴 수 없습니다.'
        )
    }
    else {
        $head  = '비밀번호를 바꾸려면 권한을 한 번 허용해야 합니다'
        $shown = '사용자 비밀번호 프로필 읽기/쓰기'
        $note  = @(
            '  이 허용은 비밀번호를 바꾸는 것 말고는 아무것도 못 합니다.'
        )
    }

    Write-Host ''
    Write-Host ('  ' + $head)
    Write-Host '  ─────────────────────────────────────────────'
    Write-Host ''
    Write-Host '  ① 인터넷 창이 열리고 학교 계정으로 로그인합니다'
    Write-Host '  ② "요청한 권한" 화면이 나옵니다'
    Write-Host ('  ③ 적혀 있는 것은 하나입니다 — ' + $shown)
    Write-Host '  ④ [수락] 을 누릅니다'
    Write-Host ''
    foreach ($n in $note) { Write-Host $n }
    Write-Host '  관리자 권한이 없는 선생님은 이 허용이 있어도 남의 계정을 건드리지 못합니다.'
    Write-Host ''
    Write-Host '  기다리는 중…'
    Write-Host ''

    Connect-MgGraph -Scopes $Scopes -NoWelcome -ErrorAction Stop

    $ctx = Get-MgContext -ErrorAction Stop
    if (-not $ctx) { throw '연결되지 않았습니다.' }

    $missing = @($Scopes | Where-Object { $_ -notin $ctx.Scopes })
    if ($missing.Count -gt 0) {
        throw ('권한을 받지 못했습니다: ' + ($missing -join ', ') + '. [수락] 을 누르셨는지 확인해 주세요.')
    }

    New-TeavelResult -Message '연결했습니다.' -Details @("계정: $($ctx.Account)")
}

<#
    계정을 지운다.

    <b>되돌릴 수 없는 일에 가장 가깝다.</b> 메일·과제·파일·원드라이브가 함께 사라진다.
    30일 안에는 관리 센터에서 되살릴 수 있지만, 그 뒤에는 아무도 못 되살린다.

    권한이 비밀번호보다 넓다 — User.ReadWrite.All 이다. 비밀번호만 바꿀 때 쓰던
    User-PasswordProfile.ReadWrite.All 로는 지울 수 없다. 넓어지는 만큼 화면이
    그것을 분명히 말해야 한다.
#>
function Remove-TeavelAccount {
    param(
        [Parameter(Mandatory)][string] $Identity
    )

    Import-Module Microsoft.Graph.Users -ErrorAction Stop

    # 자기 자신을 지우면 그 자리에서 관리 화면도, 되돌릴 사람도 함께 사라진다.
    $me = ''
    try {
        Import-Module ExchangeOnlineManagement -ErrorAction SilentlyContinue
        $conn = @(Get-ConnectionInformation -ErrorAction Stop)
        if ($conn.Count -gt 0) { $me = [string]$conn[0].UserPrincipalName }
    } catch { }

    if ($me -and $me -eq $Identity) {
        throw '지금 로그인하신 계정입니다. 자기 자신은 지울 수 없습니다.'
    }

    Invoke-TeavelWrite -Command 'Remove-MgUser' -Arguments @{ UserId = $Identity }

    New-TeavelResult -Message "$Identity — 지웠습니다." -Details @()
}

function Reset-TeavelPassword {
    param(
        [Parameter(Mandatory)][string] $Identity,
        [Parameter(Mandatory)][string] $Password,
        [bool] $MustChange = $true
    )

    Import-Module Microsoft.Graph.Users -ErrorAction Stop

    Invoke-TeavelWrite -Command 'Update-MgUser' -Arguments @{
        UserId          = $Identity
        PasswordProfile = @{
            Password                      = $Password
            ForceChangePasswordNextSignIn = $MustChange
        }
    }

    New-TeavelResult -Message "$Identity — 임시 비밀번호로 바꿨습니다." -Details @()
}

<#
    ── 계정 차단 ─────────────────────────────────────────────────────────

    졸업생 정리의 핵심 동작이다. <b>지우지 않고 막는다.</b>

    지우면 그 아이의 과제·파일·대화가 함께 사라지고 되돌릴 수 없다. 막아 두면 로그인만
    안 될 뿐 자료는 그대로 있고, 잘못 골랐어도 풀면 그만이다. 그리고 이것은 Exchange 로
    되므로 동의 화면이 없다 — Graph 가 필요한 삭제와 다르다.

    (라이선스를 돌려받으려면 언젠가 지워야 하지만, 그건 다른 결정이고 다른 날의 일이다.)
#>

function Set-TeavelAccountBlocked {
    param(
        [Parameter(Mandatory)][string] $Identity,
        [bool] $Blocked = $true
    )

    Import-Module ExchangeOnlineManagement -ErrorAction Stop

    # 자기 자신을 막으면 그 자리에서 관리 화면까지 함께 끝난다.
    # 그리고 풀어 줄 사람이 자기 자신이라 되돌릴 방법이 없다.
    if ($Blocked) {
        $me = ''
        try {
            $conn = @(Get-ConnectionInformation -ErrorAction Stop)
            if ($conn.Count -gt 0) { $me = [string]$conn[0].UserPrincipalName }
        } catch { }

        if ($me -and $me -eq $Identity) {
            throw '지금 로그인하신 계정입니다. 자기 자신은 차단할 수 없습니다.'
        }
    }

    Invoke-TeavelWrite -Command 'Set-User' -Arguments @{
        Identity = $Identity; AccountDisabled = $Blocked
    }

    $what = $(if ($Blocked) { '차단했습니다.' } else { '차단을 풀었습니다.' })
    New-TeavelResult -Message "$Identity — $what" -Details @()
}

Export-ModuleMember -Function `
    Test-TeavelUnderOneDrive, Get-TeavelStudentId, Get-TeavelCohort, `
    Get-TeavelModuleDirectory, Add-TeavelModulePath, Get-TeavelM365Readiness, Install-TeavelM365Module, `
    Install-TeavelModuleFromGallery, Connect-TeavelM365, Invoke-TeavelWrite, `
    Connect-TeavelTeamsByCode, Test-TeavelTeamsReady, `
    Get-TeavelM365Inventory, Get-TeavelTenantUser, `
    Get-TeavelUserName, Set-TeavelDisplayName, `
    New-TeavelM365Group, Sync-TeavelTeamChannel, `
    Get-TeavelTeamMember, Add-TeavelTeamMember, Remove-TeavelTeamStudent, `
    Rename-TeavelM365Group, Remove-TeavelM365Group, `
    Get-TeavelGraphReadiness, Install-TeavelGraphModule, Connect-TeavelGraph, Reset-TeavelPassword, `
    Remove-TeavelAccount, `
    Set-TeavelAccountBlocked
