<#
    가짜 MicrosoftTeams — 시험용. 짝은 ../ExchangeOnlineManagement 에 있다.

    진짜 New-Team 은 팀과 M365 그룹을 함께 만든다. 그래서 여기서도
    가짜 New-UnifiedGroup 을 부르고 팀 표시를 붙인다 — 그렇게 해야
    두 번째 실행에서 '이미 있음' 으로 제대로 갈린다.
#>

Set-StrictMode -Version Latest

$script:TeamsOn = $false

function Connect-MicrosoftTeams {
    param($AccountId, $ErrorAction)
    $script:TeamsOn = $true
    Write-Host '  (가짜) 팀에 로그인됨'
}

function Disconnect-MicrosoftTeams { param([switch] $Confirm, $ErrorAction) $script:TeamsOn = $false }

function New-Team {
    param($DisplayName, $MailNickName, $Visibility, $Description, $Template, $Owner, $ErrorAction)

    Import-Module ExchangeOnlineManagement

    # 진짜도 팀을 만들면 그룹이 함께 생긴다.
    $null = New-UnifiedGroup -DisplayName $DisplayName -Alias $MailNickName -AccessType $Visibility
    Set-FakeTeamFlag -Alias $MailNickName

    # 재고가 주는 id 와 같아야 한다. 무작위로 주면 채널이 엉뚱한 id 아래 쌓인다.
    [pscustomobject]@{
        GroupId     = (Get-FakeGroupId -Alias $MailNickName)
        DisplayName = $DisplayName
    }
}

# 채널은 팀별로 따로 담아 둔다. 진짜도 팀을 만들면 '일반' 이 저절로 생긴다.
# 채널도 실행 사이에 남겨야 '여러 번 돌려도 안전한가' 를 제대로 볼 수 있다.
$script:ChannelStore = if ($env:TEAVEL_FAKE_STORE) { "$env:TEAVEL_FAKE_STORE.channels" } else { $null }
$script:Channels = @{}
if ($script:ChannelStore -and (Test-Path $script:ChannelStore)) {
    $loaded = Get-Content $script:ChannelStore -Raw | ConvertFrom-Json
    foreach ($p in $loaded.PSObject.Properties) { $script:Channels[$p.Name] = @($p.Value) }
}

function Save-FakeChannels {
    if ($script:ChannelStore) {
        $script:Channels | ConvertTo-Json -Depth 5 | Set-Content -Path $script:ChannelStore -Encoding utf8
    }
}

function Get-TeamChannel {
    param($GroupId, $ErrorAction)
    if (-not $script:Channels.ContainsKey($GroupId)) { $script:Channels[$GroupId] = @('일반') }
    @($script:Channels[$GroupId] | ForEach-Object { [pscustomobject]@{ DisplayName = $_ } })
}

function New-TeamChannel {
    param($GroupId, $DisplayName, $MembershipType, $ErrorAction)
    if (-not $script:Channels.ContainsKey($GroupId)) { $script:Channels[$GroupId] = @('일반') }
    if ($script:Channels[$GroupId] -contains $DisplayName) {
        throw "이미 있는 채널입니다: $DisplayName"
    }
    $script:Channels[$GroupId] += $DisplayName
    Save-FakeChannels
    [pscustomobject]@{ DisplayName = $DisplayName }
}

<#
    가짜 사람들. 실제 학교 비율에 맞춘다 — 학생이 교사보다 훨씬 많다.
    라이선스 꾸러미(AssignedPlan)는 교사·학생이 다르되 SKU 이름은 드러내지 않는다.
    진짜 Get-CsOnlineUser 도 SKU 이름을 주지 않기 때문이다.
#>
$script:FacultyPlan = @('EXCHANGE_S_STANDARD', 'MCOSTANDARD', 'SHAREPOINTSTANDARD_EDU',
                        'TEAMS1', 'SCHOOL_DATA_SYNC_P1', 'INTUNE_EDU')
$script:StudentPlan = @('EXCHANGE_S_STANDARD', 'MCOSTANDARD', 'SHAREPOINTSTANDARD_EDU', 'TEAMS1')

<# 가짜 Exchange 가 막아 둔 사람인지. 모듈이 아직 안 올라왔으면 안 막힌 것으로 본다. #>
function Test-FakeBlocked {
    param([string] $Upn)
    try {
        $log = Get-TeavelFakeBlocked
        return ($log -and $log.ContainsKey($Upn) -and $log[$Upn])
    } catch { return $false }
}

function New-FakePerson {
    param($Upn, $Name, $Dept, $Kind, $Plans, $Made = '2024-03-02')
    [pscustomobject]@{
        UserPrincipalName = $Upn
        DisplayName       = $Name
        Department        = $Dept
        AccountType       = $Kind
        # 진짜 Get-CsOnlineUser 도 이 이름으로 준다. 졸업생을 가려내는 데 쓰인다.
        WhenCreated       = [datetime]$Made
        # 차단되면 $false 가 된다. 가짜 Exchange 의 Set-User 가 적어 둔 것을 본다.
        AccountEnabled    = -not (Test-FakeBlocked $Upn)
        AssignedPlan      = @($Plans | ForEach-Object {
            [pscustomobject]@{ Capability = $_; CapabilityStatus = 'Enabled' }
        })
    }
}

<#
    진짜는 Connect-MicrosoftTeams 전에는 아무것도 주지 않는다.

    가짜가 그것을 안 따졌더니, 화면이 팀에 붙기도 전에 사람 목록을 읽으려 하는 것을
    <b>여기서 못 잡고 실기에서 만났다</b> — 구성원 화면이 아무 말 없이 텅 비었다(2026-08-27).
    가짜는 진짜만큼 까다로워야 한다.
#>
function Get-CsOnlineUser {
    param($Identity, $ResultSize, $Filter, $Properties, $AccountType, $ErrorAction)

    if (-not $script:TeamsOn) {
        throw 'Run Connect-MicrosoftTeams before running cmdlets.'
    }

    $people = New-Object System.Collections.Generic.List[object]

    # 교사 24명
    $teachers = @('김하늘','이준서','박서연','최민준','정예린','강도윤','조유진','윤시우',
                  '장서윤','임건우','한지호','오채원','서동현','신아름','권태양','황보람',
                  '안세진','송민서','류가온','전소율','고은채','문지훈','배수아','남기범')
    $subjects = @('국어과','수학과','영어과','과학과','사회과','체육과')
    for ($i = 0; $i -lt $teachers.Count; $i++) {
        $people.Add((New-FakePerson -Upn ("teacher{0:d2}@school.example.kr" -f ($i+1)) `
            -Name $teachers[$i] -Dept $subjects[$i % $subjects.Count] -Kind 'User' -Plans $script:FacultyPlan))
    }

    # 학생 180명 (3학년 x 6반 x 10명)
    foreach ($grade in 1..3) {
        foreach ($room in 1..6) {
            foreach ($no in 1..10) {
                $sid = '{0}{1:d2}{2:d2}' -f $grade, $room, $no
                # 실제 학교의 학생 표시 이름은 학번+이름이다(10101홍길동).
                $family = @('김','이','박','최','정','강','조','윤','장','임')[($no - 1) % 10]
                $given  = @('민준','서연','도윤','하은','시우','지아','예준','수아','건우','채원')[($room - 1) % 10]
                # 학년마다 들어온 해가 다르다. 3학년이 가장 오래됐다 —
                # 만든 날로 줄을 세우면 졸업이 가까운 아이들이 위로 올라온다.
                $year = 2026 - (4 - $grade)
                $people.Add((New-FakePerson -Upn "s$sid@school.example.kr" `
                    -Name "$sid$family$given" -Dept '' -Kind 'User' -Plans $script:StudentPlan `
                    -Made "$year-03-02"))
            }
        }
    }

    # 라이선스 없는 계정 · 손님 · 자원 계정 — 실제 테넌트에 늘 섞여 있다.
    $people.Add((New-FakePerson -Upn 'old.teacher@school.example.kr' -Name '퇴직교사' -Dept '' -Kind 'IneligibleUser' -Plans @() -Made '2019-03-04'))
    $people.Add((New-FakePerson -Upn 'guest@other.example.com' -Name '외부 강사' -Dept '' -Kind 'Guest' -Plans @() -Made '2025-09-01'))
    $people.Add((New-FakePerson -Upn 'room1@school.example.kr' -Name '회의실1' -Dept '' -Kind 'ResourceAccount' -Plans @() -Made '2023-03-01'))

    if ($Identity) { return @($people | Where-Object { $_.UserPrincipalName -eq $Identity }) }
    $people.ToArray()
}

# 구성원도 실행 사이에 남긴다 — 여러 번 돌려도 안전한지 보려면 필요하다.
$script:MemberStore = if ($env:TEAVEL_FAKE_STORE) { "$env:TEAVEL_FAKE_STORE.members" } else { $null }
$script:Members = @{}
if ($script:MemberStore -and (Test-Path $script:MemberStore)) {
    $loaded = Get-Content $script:MemberStore -Raw | ConvertFrom-Json
    foreach ($p in $loaded.PSObject.Properties) { $script:Members[$p.Name] = @($p.Value) }
}
function Save-FakeMembers {
    if ($script:MemberStore) { $script:Members | ConvertTo-Json -Depth 5 | Set-Content -Path $script:MemberStore -Encoding utf8 }
}

function Get-TeamUser {
    param($GroupId, $Role, $ErrorAction)
    if (-not $script:Members.ContainsKey($GroupId)) { return @() }
    @($script:Members[$GroupId] | ForEach-Object {
        $bits = $_ -split '\|'
        [pscustomobject]@{ User = $bits[0]; Role = $bits[1] }
    })
}

function Add-TeamUser {
    param($GroupId, $User, $Role)
    if (-not $script:Members.ContainsKey($GroupId)) { $script:Members[$GroupId] = @() }
    if ($script:Members[$GroupId] -match "^$([regex]::Escape($User))\|") {
        throw "이미 들어 있습니다: $User"
    }
    $script:Members[$GroupId] += "$User|$(if($Role){$Role}else{'Member'})"
    Save-FakeMembers

    # 그룹의 구성원 수도 따라 늘어야 한다 — 진짜가 그렇다.
    Import-Module ExchangeOnlineManagement
    Set-FakeMemberCount -GroupId $GroupId -Count $script:Members[$GroupId].Count
}
function Remove-TeamUser {
    param($GroupId, $User, $Role)
    if (-not $script:Members.ContainsKey($GroupId)) { return }
    $script:Members[$GroupId] = @($script:Members[$GroupId] | Where-Object { $_ -notmatch "^$([regex]::Escape($User))\|" })
    Save-FakeMembers
    Import-Module ExchangeOnlineManagement
    Set-FakeMemberCount -GroupId $GroupId -Count $script:Members[$GroupId].Count
}

function Get-Team { param($ErrorAction) @() }
<#
    붙었는지 가르는 자리다. Connect-TeavelM365 가 이것으로 '이미 붙어 있나' 를 본다.

    가짜가 늘 성공했더니 <b>붙지도 않았는데 붙은 줄 알고</b> Connect-MicrosoftTeams 를
    건너뛰었다. 그다음 Get-CsOnlineUser 가 거절해 구성원 목록이 비었다.
    진짜는 붙기 전에는 이것도 안 준다.
#>
function Get-CsTenant {
    param($ErrorAction)
    if (-not $script:TeamsOn) { throw 'Run Connect-MicrosoftTeams before running cmdlets.' }
    [pscustomobject]@{ DisplayName = '가짜 학교' }
}

Export-ModuleMember -Function Connect-MicrosoftTeams, Disconnect-MicrosoftTeams,
    New-Team, New-TeamChannel, Get-TeamChannel, Add-TeamUser, Get-TeamUser, Remove-TeamUser, Get-Team, Get-CsTenant,
    Get-CsOnlineUser
