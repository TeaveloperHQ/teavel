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

    [pscustomobject]@{
        GroupId     = [guid]::NewGuid().ToString()
        DisplayName = $DisplayName
    }
}

function Add-TeamUser { param($GroupId, $User, $Role, $ErrorAction) }
function Get-Team { param($ErrorAction) @() }
function Get-CsTenant { param($ErrorAction) [pscustomobject]@{ DisplayName = '가짜 학교' } }

Export-ModuleMember -Function Connect-MicrosoftTeams, Disconnect-MicrosoftTeams,
    New-Team, Add-TeamUser, Get-Team, Get-CsTenant
