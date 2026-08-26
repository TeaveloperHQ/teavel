<#
    가짜 Microsoft.Graph.Authentication.

    비밀번호 바꾸기는 Graph 가 필요한 유일한 자리다. 진짜 테넌트에 대고 남의 비밀번호를
    바꿔 보며 시험할 수는 없으므로, 여기서 연결과 권한 동의를 흉내 낸다.

    <b>권한이 모자란 경우를 재현할 수 있어야 한다.</b> 관리자가 동의 화면에서 [취소] 를
    누르는 일은 실제로 일어나고, 그때 화면이 무슨 말을 하는지가 이 기능의 절반이다.
    TEAVEL_FAKE_GRAPH_DENY 를 주면 동의를 거절한 것처럼 군다.
#>

Set-StrictMode -Version Latest

$script:Context = $null

function Connect-MgGraph {
    param(
        [string[]] $Scopes,
        [switch] $NoWelcome,
        [switch] $UseDeviceCode,
        $ErrorAction
    )

    Write-Host '  (가짜) 권한 동의 화면이 떴다고 칩니다.'

    if ($env:TEAVEL_FAKE_GRAPH_DENY) {
        Write-Host '  (가짜) 관리자가 [취소] 를 눌렀습니다.'
        $script:Context = [pscustomobject]@{
            Account     = 'admin@school.example.kr'
            Scopes      = @()
            TenantId    = 'fake-tenant'
        }
        return
    }

    $script:Context = [pscustomobject]@{
        Account  = 'admin@school.example.kr'
        Scopes   = @($Scopes)
        TenantId = 'fake-tenant'
    }

    Write-Host '  (가짜) Graph 에 연결됨'
}

function Get-MgContext {
    param($ErrorAction)
    $script:Context
}

function Disconnect-MgGraph { param($ErrorAction) $script:Context = $null }

Export-ModuleMember -Function Connect-MgGraph, Get-MgContext, Disconnect-MgGraph
