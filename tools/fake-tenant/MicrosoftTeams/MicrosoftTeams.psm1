<#
    가짜 MicrosoftTeams — 시험용. 짝은 ../ExchangeOnlineManagement 에 있다.

    진짜 New-Team 은 팀과 M365 그룹을 함께 만든다. 그래서 여기서도
    가짜 New-UnifiedGroup 을 부르고 팀 표시를 붙인다 — 그렇게 해야
    두 번째 실행에서 '이미 있음' 으로 제대로 갈린다.
#>

Set-StrictMode -Version Latest

function Connect-MicrosoftTeams {
    param($AccountId, $ErrorAction)
    Write-Host '  (가짜) 팀에 로그인됨'
}

function Disconnect-MicrosoftTeams { param([switch] $Confirm, $ErrorAction) }

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
        $script:Channels | ConvertTo-Json -Depth 5 | Set-Content -Path $script:ChannelStore
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

function Add-TeamUser { param($GroupId, $User, $Role, $ErrorAction) }
function Get-Team { param($ErrorAction) @() }
function Get-CsTenant { param($ErrorAction) [pscustomobject]@{ DisplayName = '가짜 학교' } }

Export-ModuleMember -Function Connect-MicrosoftTeams, Disconnect-MicrosoftTeams,
    New-Team, New-TeamChannel, Get-TeamChannel, Add-TeamUser, Get-Team, Get-CsTenant
