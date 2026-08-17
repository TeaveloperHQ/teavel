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
$script:CoreModules = @('MicrosoftTeams', 'ExchangeOnlineManagement')

<#
.SYNOPSIS
    M365 작업에 필요한 것이 갖춰졌는지 본다. 아무것도 설치하거나 바꾸지 않는다.
#>
function Get-TeavelM365Readiness {
    param()

    $d = New-Object System.Collections.Generic.List[string]
    $missing = New-Object System.Collections.Generic.List[string]

    foreach ($m in $script:CoreModules) {
        $found = @(Get-Module -ListAvailable -Name $m -ErrorAction SilentlyContinue)
        if ($found.Count -gt 0) {
            $v = ($found | Sort-Object Version -Descending)[0].Version
            $d.Add("$m  $v")
        } else {
            $missing.Add($m)
            $d.Add("$m  — 없음")
        }
    }

    $d.Add('')
    $d.Add("PowerShell $($PSVersionTable.PSVersion)")

    if ($missing.Count -gt 0) {
        $d.Add('')
        $d.Add('설치가 필요합니다. Teavel 이 대신 설치해 드릴 수 있습니다.')
        $d.Add('(내 계정에만 설치되며 관리자 권한이 필요 없습니다)')
        return New-TeavelResult -Message "필요한 모듈 $($missing.Count)개가 없습니다." -Details $d
    }

    New-TeavelResult -Message '필요한 모듈이 모두 있습니다.' -Details $d
}

<#
.SYNOPSIS
    M365 작업에 필요한 모듈을 내 계정에만 설치한다.
.DESCRIPTION
    -Scope CurrentUser 이므로 관리자 권한이 필요 없다.
    학교 네트워크가 PowerShell 갤러리를 막고 있으면 여기서 실패한다 — 그때는
    전산 담당에게 알릴 수 있도록 이유를 그대로 전한다.
#>
function Install-TeavelM365Module {
    param()

    $installed = New-Object System.Collections.Generic.List[string]
    $failed    = New-Object System.Collections.Generic.List[string]

    foreach ($m in $script:CoreModules) {
        if (@(Get-Module -ListAvailable -Name $m -ErrorAction SilentlyContinue).Count -gt 0) { continue }
        try {
            Install-Module -Name $m -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop
            $installed.Add($m)
        } catch {
            $failed.Add("$m — $($_.Exception.Message)")
        }
    }

    if ($failed.Count -gt 0) {
        $d = New-Object System.Collections.Generic.List[string]
        foreach ($f in $failed) { $d.Add($f) }
        $d.Add('')
        $d.Add('학교 네트워크가 PowerShell 갤러리를 막고 있을 수 있습니다.')
        $d.Add('전산 담당 선생님께 위 메시지를 그대로 전해 주세요.')
        throw ("모듈을 설치하지 못했습니다: " + ($failed -join ' / '))
    }

    if ($installed.Count -eq 0) {
        return New-TeavelResult -Message '이미 다 설치돼 있습니다.'
    }
    New-TeavelResult -Message "모듈 $($installed.Count)개를 설치했습니다." -Details $installed
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
        [string] $Account
    )

    Import-Module MicrosoftTeams -ErrorAction Stop
    Import-Module ExchangeOnlineManagement -ErrorAction Stop

    $d = New-Object System.Collections.Generic.List[string]

    # 이미 붙어 있으면 그대로 쓴다.
    $teamsOk = $false
    try { $null = Get-CsTenant -ErrorAction Stop; $teamsOk = $true } catch { }

    if (-not $teamsOk) {
        if ($Account) { Connect-MicrosoftTeams -AccountId $Account -ErrorAction Stop | Out-Null }
        else          { Connect-MicrosoftTeams -ErrorAction Stop | Out-Null }
    }

    $exoOk = $false
    try { $null = Get-OrganizationConfig -ErrorAction Stop; $exoOk = $true } catch { }

    if (-not $exoOk) {
        if ($Account) { Connect-ExchangeOnline -UserPrincipalName $Account -ShowBanner:$false -ErrorAction Stop }
        else          { Connect-ExchangeOnline -ShowBanner:$false -ErrorAction Stop }
    }

    $org = $null
    try { $org = Get-OrganizationConfig -ErrorAction SilentlyContinue } catch { }
    if ($org) { $d.Add("테넌트: $($org.DisplayName)") }

    $d.Add("Teams: $(if ($teamsOk) { '이미 연결됨' } else { '새로 연결' })")
    $d.Add("Exchange: $(if ($exoOk) { '이미 연결됨' } else { '새로 연결' })")

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

        $d.Add(("GROUP`t{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}" -f `
            $g.DisplayName, $g.Alias, $g.PrimarySmtpAddress, $isTeam, $members, $created, $privacy))
    }

    New-TeavelResult -Message "그룹 $($groups.Count)개를 읽었습니다." -Details $d
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
    Get-TeavelM365Readiness, Install-TeavelM365Module, Connect-TeavelM365, `
    Get-TeavelM365Inventory, Rename-TeavelM365Group, Remove-TeavelM365Group
