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

Export-ModuleMember -Function Update-MgUser, Get-TeavelFakePasswordLog
