<#
    가짜 Microsoft.Graph.Users — Update-MgUser 하나만 있으면 된다.

    ■ 진짜와 같은 모양이어야 한다

    Invoke-TeavelWrite 는 부르기 전에 cmdlet 의 매개변수를 들여다보고 -Confirm:$false 를
    붙일지 정한다. 그래서 가짜가 ErrorAction·Confirm 을 <b>직접 선언하면</b> 진짜에서는
    안 나는 오류가 여기서만 난다(실제로 그렇게 한 번 걸렸다).

    진짜 Update-MgUser 는 [CmdletBinding(SupportsShouldProcess)] 이고 ErrorAction 은
    공통 매개변수로 저절로 생긴다. 그 모양을 그대로 흉내 낸다.

    ■ 바꾼 비밀번호를 파일에 남기지 않는다

    가짜라도 비밀번호를 디스크에 적어 두는 버릇을 들이면 진짜에서도 그렇게 된다.
    누구를 언제 바꿨는지만 남긴다.

    ■ 실패를 재현할 수 있어야 한다

    진짜 테넌트에서는 자기 자신이나 상급 관리자의 비밀번호를 못 바꾼다. 그때 화면이
    한 사람만 건너뛰고 나머지를 마저 하는지가 이 기능에서 가장 중요한 갈래다.
    아이디에 'admin' 이 들어가면 거절한다.
#>

Set-StrictMode -Version Latest

$script:Log = @{}
$script:Removed = @{}

function Update-MgUser {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string] $UserId,
        [hashtable] $PasswordProfile
    )

    if ($UserId -like '*admin*') {
        throw "Insufficient privileges to complete the operation. ($UserId)"
    }

    if (-not $PasswordProfile -or -not $PasswordProfile['Password']) {
        throw 'PasswordProfile 이 비어 있습니다.'
    }

    if ([string]$PasswordProfile['Password'] -match '\s') {
        throw '비밀번호에 빈칸이 들어 있습니다.'
    }

    if ($PSCmdlet.ShouldProcess($UserId, '비밀번호 바꾸기')) {
        $script:Log[$UserId] = [pscustomobject]@{
            When       = (Get-Date).ToString('s')
            MustChange = [bool]$PasswordProfile['ForceChangePasswordNextSignIn']
        }
    }
}

function Get-TeavelFakePasswordLog { param() $script:Log }


<#
    계정을 지운다.

    진짜와 같은 모양이어야 한다 — [CmdletBinding(SupportsShouldProcess)] 이고
    ErrorAction 은 공통 매개변수로 저절로 생긴다. 직접 선언하면 Invoke-TeavelWrite 의
    -Confirm:$false 판단이 진짜와 달라진다.

    실패도 재현할 수 있어야 한다. 진짜에서는 자기 자신이나 상급 관리자를 못 지운다.
    한 사람이 막혀도 나머지를 마저 하는지가 이 기능에서 가장 중요한 갈래라,
    아이디에 'admin' 이 들어가면 거절한다 — 비밀번호 쪽과 같은 규칙이다.

    지운 사람은 Exchange 쪽에서도 사라져야 한다. 진짜는 한 곳에서 지우면 두 곳이
    함께 맞는데, 흉내가 얕으면 '지웠습니다' 라고 해 놓고 목록에 그대로 남아 있어
    관리자가 한 번 더 누른다.
#>
function Remove-MgUser {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string] $UserId
    )

    if ($UserId -like '*admin*') {
        throw "Insufficient privileges to complete the operation. ($UserId)"
    }

    if ($PSCmdlet.ShouldProcess($UserId, '계정 지우기')) {
        $script:Removed[$UserId] = (Get-Date).ToString('s')

        # 가짜 Exchange 가 떠 있으면 거기서도 지운다.
        $rm = Get-Command -Name 'Remove-TeavelFakeUser' -ErrorAction SilentlyContinue
        if ($rm) { & $rm -Identity $UserId }
    }
}

function Get-TeavelFakeRemovedLog { param() $script:Removed }

Export-ModuleMember -Function Update-MgUser, Get-TeavelFakePasswordLog, Remove-MgUser, Get-TeavelFakeRemovedLog
