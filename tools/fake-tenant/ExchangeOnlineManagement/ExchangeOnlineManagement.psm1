<#
    가짜 Exchange Online — 시험용.

    진짜와 같은 이름의 함수를 내어, PSModulePath 에 얹으면 Teavel 이 진짜 대신 이것을 부른다.
    테넌트도 로그인도 없이 M365 흐름 전체를 리눅스에서 돌려 볼 수 있다.

    ■ 왜 필요한가

    남의 학교 테넌트에 대고 시험할 수는 없다. 그렇다고 만들기·이름변경·삭제를 한 번도
    돌려 보지 않고 내보내면, 처음 쓰는 사람이 실제 학교에서 처음 겪게 된다.

    실제로 여기서 진짜 버그 둘을 잡았다.
      · 테넌트의 '3학년_4반' 을 두고 선언의 '3학년 4반' 을 새로 만들려 한 것
      · New-Team @args — 인자 하나 없이 호출되면서 조용히 성공한 것

    ■ 채워 넣은 것

    실제 학교 테넌트에서 본 목록 그대로다. 지어낸 이름으로는 저 둘이 안 나왔다 —
    한글 이름이 별칭에서 뭉개지는 것도, 동적 그룹이 0명으로 오는 것도 실물의 성질이다.

    ■ 쓰는 법

        PSModulePath=<이 폴더>  TEAVEL_FAKE_STORE=/tmp/store.json  teavel m365

    TEAVEL_FAKE_STORE 를 주면 상태가 파일에 남아, 여러 번 돌려도 안전한지 확인할 수 있다.
    주지 않으면 프로세스마다 처음 상태로 돌아간다.

    주의: Import-Module 은 판 번호가 아니라 PSModulePath 순서로 고른다.
    진짜 모듈이 앞에 있으면 그쪽이 이긴다.
#>

Set-StrictMode -Version Latest

# 실제 학교 테넌트에서 본 목록. 각 줄이 왜 여기 있는지는 docs/m365.md 에 적어 두었다.
$script:Store = @(
    # 테넌트를 만들면 자동으로 생긴다. 동적 그룹이라 0명으로 오는데 지우면 안 된다.
    @{ DisplayName = 'All Company';  Alias = 'AllCompany.5a2f.abc'; Team = $false; Members = 0;   Created = '2023-03-01'; Access = 'Public'  }

    # 한글 이름으로 만들면 별칭에서 뜻이 날아간다 — 대조를 이름으로 하는 이유.
    @{ DisplayName = '3학년_4반';    Alias = '3_4';                Team = $true;  Members = 30;  Created = '2024-03-02'; Access = 'Private' }
    @{ DisplayName = '3학년_과학';   Alias = '3_';                 Team = $true;  Members = 203; Created = '2024-03-02'; Access = 'Private' }
    @{ DisplayName = '늘푸른 학생회'; Alias = 'msteams_83a1ec';     Team = $true;  Members = 17;  Created = '2024-04-11'; Access = 'Private' }

    # 개인 용도로 만든 것. 사람이 적지만 시험용은 아니다.
    @{ DisplayName = '제주도 여행';   Alias = '949';                Team = $false; Members = 2;   Created = '2025-06-20'; Access = 'Private' }

    # 이름에 '테스트' 가 들어가지만 진짜 업무 그룹이다 — 이름만 보고 지우면 안 된다.
    @{ DisplayName = '테스트지 채점'; Alias = 'grading';            Team = $false; Members = 8;   Created = '2025-02-01'; Access = 'Private' }

    # 만들어 두고 잊은 것들.
    @{ DisplayName = 'Test';         Alias = 'test';               Team = $false; Members = 1;   Created = '2023-09-09'; Access = 'Private' }
    @{ DisplayName = '히히';          Alias = 'hihi';               Team = $true;  Members = 1;   Created = '2024-01-05'; Access = 'Private' }
)

if ($env:TEAVEL_FAKE_STORE -and (Test-Path $env:TEAVEL_FAKE_STORE)) {
    # ConvertFrom-Json 을 그대로 파이프에 물리면 안 된다.
    #
    # Windows PowerShell 5.1 은 배열을 <한 덩어리로> 내보낸다(7 부터 낱개로 바뀌었다).
    # 그래서 ForEach-Object 가 25번이 아니라 한 번 돌고, 값이 전부 배열인 해시테이블
    # 하나가 만들어진다. 증상은 엉뚱한 곳에서 터진다 — [datetime]$Row.Created 가
    # "Object[] 를 DateTime 으로 못 바꾼다" 고 한다.
    #
    # 변수에 그냥 받으면 다음 파이프가 낱개로 푼다.
    # @() 로 감싸는 것으로는 안 된다 — 그 한 덩어리를 원소 하나로 담을 뿐이다.
    $rows = Get-Content $env:TEAVEL_FAKE_STORE -Raw | ConvertFrom-Json
    $script:Store = @($rows | ForEach-Object {
        @{ DisplayName = $_.DisplayName; Alias = $_.Alias; Team = $_.Team
           Members = $_.Members; Created = $_.Created; Access = $_.Access }
    })
}

function Save-FakeStore {
    if ($env:TEAVEL_FAKE_STORE) {
        $script:Store | ConvertTo-Json -Depth 5 | Set-Content -Path $env:TEAVEL_FAKE_STORE -Encoding utf8
    }
}

<#
    별칭에서 그룹 id 를 만든다.

    진짜 테넌트는 만들 때 id 를 정해 주지만, 가짜에서는 무작위로 주면
    Get-UnifiedGroup 이 주는 id 와 New-Team 이 주는 id 가 달라진다.
    그러면 채널이 엉뚱한 id 아래 쌓여, 실은 안 되는 것이 되는 것처럼 보인다.
    실제로 그렇게 한 번 속았다.
#>
function Get-FakeGroupId {
    param([string] $Alias)
    ([guid]::new([System.Security.Cryptography.MD5]::Create().ComputeHash(
        [Text.Encoding]::UTF8.GetBytes($Alias)))).ToString()
}

<#
    진짜 Get-UnifiedGroup 이 내주는 모양 그대로 만든다.
    Teavel 이 읽는 속성만 담는다 — 실제 테넌트에서 이것들이 온다는 것은 확인했다.
#>
function New-FakeRow {
    param($Row)
    [pscustomobject]@{
        DisplayName                 = $Row.DisplayName
        Alias                       = $Row.Alias
        PrimarySmtpAddress          = "$($Row.Alias)@school.example.kr"
        ResourceProvisioningOptions = @(if ($Row.Team) { 'Team' })
        GroupMemberCount            = $Row.Members
        WhenCreated                 = [datetime]$Row.Created
        AccessType                  = $Row.Access
        # 채널을 손대려면 이 id 가 있어야 한다.
        ExternalDirectoryObjectId   = (Get-FakeGroupId -Alias ([string]$Row.Alias))
    }
}

function Connect-ExchangeOnline {
    param([switch] $ShowBanner, [switch] $Device, $UserPrincipalName, $ErrorAction)
    Write-Host '  (가짜) 메일·그룹에 로그인됨'
}

function Disconnect-ExchangeOnline { param([switch] $Confirm, $ErrorAction) }

function Get-UnifiedGroup {
    param($Identity, $ResultSize, $ErrorAction)

    if ($Identity) {
        $row = $script:Store |
               Where-Object { $_.Alias -eq $Identity -or $_.DisplayName -eq $Identity } |
               Select-Object -First 1
        if (-not $row) { throw "그런 그룹이 없습니다: $Identity" }
        return (New-FakeRow $row)
    }

    return @($script:Store | ForEach-Object { New-FakeRow $_ })
}

function New-UnifiedGroup {
    param($DisplayName, $Alias, $AccessType, $Notes, $Owner, $Members, $ErrorAction)

    # 진짜도 여기서 막는다. 이 검사가 없었으면 @args 버그를 못 잡았다 —
    # 빈 별칭으로 계속 만들어지며 조용히 성공했을 것이다.
    if ($script:Store | Where-Object { $_.Alias -eq $Alias }) {
        throw "별칭이 이미 있습니다: $Alias"
    }

    $row = @{
        DisplayName = $DisplayName
        Alias       = $Alias
        Team        = $false
        Members     = 1
        Created     = (Get-Date).ToString('yyyy-MM-dd')
        Access      = $(if ($AccessType) { $AccessType } else { 'Private' })
    }
    $script:Store += $row
    Save-FakeStore
    New-FakeRow $row
}

function Set-UnifiedGroup {
    param($Identity, $DisplayName, $Alias, $PrimarySmtpAddress, $ErrorAction)

    $row = $script:Store |
           Where-Object { $_.Alias -eq $Identity -or $_.DisplayName -eq $Identity } |
           Select-Object -First 1
    if (-not $row) { throw "그런 그룹이 없습니다: $Identity" }

    if ($DisplayName) { $row.DisplayName = $DisplayName }
    if ($Alias)       { $row.Alias = $Alias }
    Save-FakeStore
}

function Remove-UnifiedGroup {
    param($Identity, $Confirm, $ErrorAction)
    $script:Store = @($script:Store |
        Where-Object { $_.Alias -ne $Identity -and $_.DisplayName -ne $Identity })
    Save-FakeStore
}

<#
    가짜 Get-User / Set-User. 교육청 포털이 만든 계정처럼 성과 이름이 나뉘어 있다.
#>
$script:People = @{}
$script:Blocked = @{}
function Get-User {
    param($Identity, $ResultSize, $ErrorAction)
    if ($script:People.Count -eq 0) {
        $names = @(
            @('teacher01', '김', '하늘'), @('teacher02', '이', '준서'),
            @('teacher03', '남궁', '민'), @('teacher04', '박', '서연')
        )
        foreach ($n in $names) {
            $upn = "$($n[0])@school.example.kr"
            # 포털이 넣은 그대로 — 표시 이름이 '하늘 김' 처럼 뒤집혀 있다.
            $script:People[$upn] = [pscustomobject]@{
                UserPrincipalName = $upn; DisplayName = "$($n[2]) $($n[1])"
                FirstName = $n[2]; LastName = $n[1]
            }
        }
        # 이미 제대로 된 계정도 하나 둔다 — 건드리지 말아야 한다.
        $script:People['teacher05@school.example.kr'] = [pscustomobject]@{
            UserPrincipalName = 'teacher05@school.example.kr'; DisplayName = '최민준'
            FirstName = '민준'; LastName = '최' }
    }
    if ($Identity) {
        # 진짜 테넌트에서는 Get-CsOnlineUser 가 준 사람이면 Exchange 에도 다 있다.
        # 여기 씨앗은 이름 붙이기 시험용 몇 명뿐이라, 나머지는 물어볼 때 만들어 준다 —
        # 그러지 않으면 학생을 막아 보는 시험이 '그런 사람이 없습니다' 로만 끝난다.
        if (-not $script:People.ContainsKey($Identity)) {
            if ($Identity -notlike '*@*') { throw "그런 사람이 없습니다: $Identity" }
            $script:People[$Identity] = [pscustomobject]@{
                UserPrincipalName = $Identity; DisplayName = $Identity
                FirstName = ''; LastName = ''
            }
        }
        return $script:People[$Identity]
    }
    @($script:People.Values)
}
<#
    진짜 Set-User 는 확인을 묻는다(실기에서 확인). 그것을 그대로 흉내 내야
    -Confirm:$false 를 빠뜨린 것이 시험에서 드러난다.
#>
function Set-User {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param($Identity, $DisplayName, $AccountDisabled)
    if (-not $script:People.ContainsKey($Identity)) { $null = Get-User -Identity $Identity }
    if ($PSCmdlet.ShouldProcess($Identity, '설정')) {
        if ($PSBoundParameters.ContainsKey('DisplayName'))     { $script:People[$Identity].DisplayName = $DisplayName }
        if ($PSBoundParameters.ContainsKey('AccountDisabled')) { $script:Blocked[$Identity] = [bool]$AccountDisabled }
    }
}

<#
    누가 로그인해 있는지. 진짜 EXO v3 가 이 이름으로 준다.
    자기 자신을 차단하려는 것을 여기서 막는다 — 막으면 되돌릴 사람이 없어진다.
#>
function Get-ConnectionInformation {
    param($ErrorAction)
    [pscustomobject]@{ UserPrincipalName = 'admin@school.example.kr'; State = 'Connected' }
}

function Get-TeavelFakeBlocked { param() $script:Blocked }

function Add-UnifiedGroupLinks { param($Identity, $LinkType, $Links, $ErrorAction) }

<#
    가짜 New-Team 이 부른다. 진짜 New-Team 이 만든 그룹에는
    ResourceProvisioningOptions 에 'Team' 이 붙는데, 그것을 흉내 내지 않으면
    두 번째 실행에서 "그룹은 있는데 팀이 안 붙었다" 로 잘못 갈린다.
#>
<#
    사람을 넣으면 그룹의 구성원 수도 늘어야 한다.
    진짜는 당연히 그런데, 흉내가 얕으면 새로 만든 팀이 계속 '구성원 1명뿐 → 정리 후보' 로
    올라와 시험이 거짓말을 한다. 실제로 그렇게 한 번 속았다.
#>
function Set-FakeMemberCount {
    param($GroupId, $Count)
    foreach ($row in $script:Store) {
        if ((Get-FakeGroupId -Alias ([string]$row.Alias)) -eq $GroupId) {
            $row.Members = $Count
            Save-FakeStore
            return
        }
    }
}

function Set-FakeTeamFlag {
    param($Alias)
    $row = $script:Store | Where-Object { $_.Alias -eq $Alias } | Select-Object -First 1
    if ($row) { $row.Team = $true; Save-FakeStore }
}

Export-ModuleMember -Function Connect-ExchangeOnline, Disconnect-ExchangeOnline,
    Get-UnifiedGroup, New-UnifiedGroup, Set-UnifiedGroup, Remove-UnifiedGroup,
    Add-UnifiedGroupLinks, Set-FakeTeamFlag, Set-FakeMemberCount, Get-FakeGroupId, Get-User, Set-User,
    Get-ConnectionInformation, Get-TeavelFakeBlocked
