# Teams 채팅 통제 — 학생끼리는 못 하게, 교사와는 되게

학교가 채팅을 통째로 끄면 아이가 선생님에게 조용히 물어볼 길까지 함께 닫힌다.
그렇다고 열어 두면 학생끼리 무엇을 주고받는지 아무도 모른다. **감독 채팅**은 그 사이를 연다.

이 문서는 **어느 학교에서든 그대로 따라 하면 같은 상태가 되도록** 쓴 것이다.
실기 근거는 늘푸른중(`nprm.goe.go.kr`) 2026-08-27 ~ 09-03 작업이고, 맨 아래에 기록을 남겼다.

---

## 무엇을 만드는가

| 누가 → 누구에게 | 새 채팅 | |
|---|---|---|
| 학생 → **교사** | **된다** | 아이가 조용히 물어볼 길 |
| 교사 → 학생 | 된다 | 선생님이 먼저 말 걸 수 있다 |
| 교사 → 교사 | 된다 | |
| **학생 → 학생** | **안 된다** | 이것을 막는 게 목적 |
| 교사가 연 방에 학생 여럿 | 된다 | 교사가 있으면 감독이 된다 |

교사는 자기가 감독하는 방을 **떠날 수 없고, 학생이 교사를 내보낼 수도 없다.**
감독이 빠진 방이 남지 않게 마이크로소프트가 막아 둔 것이다.

---

## 이 기능의 본체는 Graph 가 필요 없다

`docs/m365.md` ①번 원칙과 맞물리는 자리다. **막는 것과 지우는 것을 갈라야 한다.**

| | 필요한 것 |
|---|---|
| **학생끼리 채팅 막기** (이 기능의 본체) | `MicrosoftTeams` 모듈만. **Graph 도, 동의 화면도 없다** |
| 이미 만들어진 방 지우기 | Graph **앱 전용** 권한. 별도 결정이고 별도 작업이다 |

감독 채팅은 **켠 뒤에 새로 만들어지는 방에만** 걸린다. 이미 있던 방·회의 채팅·채널은
그대로 남는다(마이크로소프트 문서 명시). 그래서 두 작업이 갈린다.

---

## 구축 절차

### 0단계 — 알아야 하는 것은 하나뿐이다: 교사가 누구인가

**이 설정에서 사람이 정해 줘야 하는 것은 교사 명단 하나다.** 나머지는 전부 자동이다.
그리고 이게 유일하게 어려운 부분이다 — **교사를 자동으로 가려낼 방법이 없다.**

실기에서 확인한 것: `Department` · `Title` · `City` 가 **전원 비어 있었다.**
학교 테넌트에서 이 칸들은 보통 안 채워져 있다. 이름 규칙도 못 믿는다.

쓸 수 있는 것은 둘이다.

| 방법 | 신뢰도 | 비고 |
|---|---|---|
| **그룹 구성원** (교사 그룹) | 관리자가 관리하는 만큼 | 아래 '그룹별 적용' 참조 |
| **라이선스 종류** | 높다 | 교직원 SKU(`…_FACULTY`)와 학생 SKU(`…_STUUSEBNFT`)가 다르다. 다만 조회에 Graph 읽기가 필요하다 |

**둘을 대조하면 가장 안전하다.** 어느 쪽이든 **적용 전에 명단을 사람이 눈으로 본다.**

> **검토는 Full 을 받는 명단만 보면 된다.** 그게 이 설정의 위험 전부다.
> 교사가 명단에서 빠지면 → 그 선생님이 학생에게 말을 못 건다. 불편하지만 안전하다.
> 학생이 명단에 잘못 들어가면 → **그 학생이 전교생에게 말을 걸 수 있다.** 이것만 막으면 된다.

### 1단계 — 지금 상태를 먼저 본다 (읽기 전용)

```powershell
Connect-MicrosoftTeams

Get-CsTeamsMessagingPolicy -Identity Global |
    Select-Object AllowUserChat, ChatPermissionRole
Get-CsTeamsClientConfiguration -Identity Global |
    Select-Object AllowRoleBasedChatPermissions
Get-CsTeamsCallingPolicy -Identity Global |
    Select-Object AllowPrivateCalling
```

바꾸기 전 값을 적어 둔다. 되돌릴 때 쓴다.

### 2단계 — 교사 정책을 만든다

```powershell
New-CsTeamsMessagingPolicy -Identity EduFaculty
Set-CsTeamsMessagingPolicy -Identity EduFaculty -AllowUserChat $true -ChatPermissionRole Full
```

역할은 셋이다.

| 역할 | 누구에게 | 할 수 있는 것 |
|---|---|---|
| **Full** | 교사 | 누구에게나 먼저 말 걸 수 있다. 감독자가 된다 |
| Limited | 교사 아닌 교직원 | 교사·교직원에게만 먼저 말 걸 수 있다. 학생에겐 못 건다 |
| **Restricted** | 학생 | **Full 인 사람에게만** 먼저 말 걸 수 있다 |

행정실·급식실처럼 학생과 직접 채팅할 일이 없는 교직원은 **Limited** 가 맞다.
굳이 안 나눠도 되면 교사만 Full 로 하고 나머지는 전부 기본값에 둔다.

### 3단계 — 기본값을 잠근다

```powershell
Set-CsTeamsMessagingPolicy -Identity Global -ChatPermissionRole Restricted
```

**정책이 따로 없는 계정은 전부 Global 을 따른다.** 즉 이 한 줄로 학생 전원과
**앞으로 만들어질 계정 전부**가 Restricted 가 된다. 명단을 안 봐도 안전한 쪽에 떨어진다.

### 4단계 — 교사에게 정책을 준다

```powershell
$교사 = @('teacher1@school.kr','teacher2@school.kr')   # 0단계에서 정한 명단
foreach ($t in $교사) { Grant-CsTeamsMessagingPolicy -Identity $t -PolicyName EduFaculty }
```

수백 명이면 한 명씩 돌리지 말고 일괄 작업을 쓴다 — `New-CsBatchPolicyAssignmentOperation`.
교사는 보통 수십 명이라 반복문으로 충분하다.

### 5단계 — 감독 채팅을 켜고, 그 다음에 채팅을 연다

**순서가 중요하다.** 마이크로소프트 문서가 명시한다 — 역할을 다 정한 뒤에 켜고,
켠 뒤에 채팅을 열어야 무방비 구간이 안 생긴다.

```powershell
Set-CsTeamsClientConfiguration -Identity Global -AllowRoleBasedChatPermissions $true
Set-CsTeamsMessagingPolicy    -Identity Global -AllowUserChat $true
```

- `AllowRoleBasedChatPermissions` 는 **`CsTeamsClientConfiguration`** 에 있다
  (`CsTeamsMessagingPolicy` 가 아니다 — 여기서 자주 헤맨다).
- **테넌트 기본값은 꺼짐이고, 전체 아니면 전무다.** 일부 사용자에게만 켤 수 없다.

### 6단계 — 검증한다

```powershell
Get-CsTeamsMessagingPolicy -Identity Global | Select-Object AllowUserChat, ChatPermissionRole
Get-CsTeamsClientConfiguration -Identity Global | Select-Object AllowRoleBasedChatPermissions
Get-CsOnlineUser -Identity <교사UPN> | Select-Object UserPrincipalName, TeamsMessagingPolicy
Get-CsOnlineUser -Identity <학생UPN> | Select-Object UserPrincipalName, TeamsMessagingPolicy
```

기대값: Global 이 `AllowUserChat=True` · `ChatPermissionRole=Restricted`,
클라이언트 구성이 `AllowRoleBasedChatPermissions=True`,
교사는 `TeamsMessagingPolicy=EduFaculty`, 학생은 **비어 있음**(= Global 적용).

**정책 반영에는 시간이 걸린다.** 명령이 성공해도 사용자 화면에 반영되기까지 몇 시간
걸릴 수 있다. 바로 안 바뀐다고 다시 돌리지 않는다.

### 되돌리기

```powershell
Set-CsTeamsMessagingPolicy -Identity Global -AllowUserChat $false        # 채팅 전면 차단
Set-CsTeamsClientConfiguration -Identity Global -AllowRoleBasedChatPermissions $false  # 감독 해제
Set-CsTeamsCallingPolicy -Identity Global -AllowPrivateCalling $true     # 통화 원복
```

### 통화도 같이 막는다

채팅만 막으면 **음성·영상 통화로 그대로 새 나간다.** 같은 모양으로 처리한다.

```powershell
Set-CsTeamsCallingPolicy -Identity Global -AllowPrivateCalling $false
New-CsTeamsCallingPolicy -Identity AllowCalling
Set-CsTeamsCallingPolicy -Identity AllowCalling -AllowPrivateCalling $true
foreach ($t in $교사) { Grant-CsTeamsCallingPolicy -Identity $t -PolicyName AllowCalling }
```

---

## 정한 것과 그 이유

### ① 그룹으로 막지 않는다 — 기본값을 잠그고 교사만 푼다

처음엔 "학생 그룹에 차단 정책"을 생각했다. **실기에서 깨졌다.**

| | 인원 |
|---|---|
| Students 보안그룹 | 182명 |
| 학생 라이선스 보유 | 349명 |
| **그룹에서 누락** | **167명** |

그룹이 명단과 안 맞으면 **그룹 기준 차단은 167명을 그대로 통과시킨다.**

> **학교 테넌트에서 "그룹 = 명단" 은 성립하지 않는다.**
> 그러므로 통제는 **허용 목록(교사)** 으로 관리하고 **차단 목록(학생)** 으로 관리하지 않는다.
> 교사는 수십 명이고 학생은 수백 명이며, **틀렸을 때 새는 방향이 반대다.**

### ② 그룹별 적용은 허용 쪽에만 쓴다

관리자가 팀·그룹·구성원을 다 정리한 다음이라면, 그룹을 골라 정책을 주는 게 가장 자연스럽다.
**단 방향이 정해져 있다.**

| | 그룹으로 하나 | 빠뜨렸을 때 |
|---|---|---|
| **교사에게 Full 주기** | **그렇게 한다** | 그 교사가 학생에게 말을 못 건다 — **안전** |
| 학생에게 Restricted 주기 | **하지 않는다** | 그 학생이 안 막힌다 — **위험** |

학생 쪽은 **3단계의 Global 기본값**이 맡는다. 그룹에 없든, 오늘 만들어졌든, 내년에
전학 오든 전부 Restricted 다. **그룹은 교사를 찾는 데만 쓴다.**

### ③ 내장 `Tag:EduStudent` 를 학생에게 주면 안 된다

이름은 학생용처럼 보이는데 **`ChatPermissionRole` 이 `Full` 이다.** 그대로 주면
학생이 전교생에게 말을 걸 수 있다. 게다가 **수정도 거부된다**(오류 `40006`).
teavel 이 내장 정책을 목록에 올린다면 이건 빼야 한다.

### ④ 신규 계정이 저절로 안전해야 한다

학교는 학기 중에도 계정이 생긴다. **관리자가 뭔가를 더 해야 안전해지는 설계는 언젠가 샌다.**
기본값을 Restricted 로 두는 이 방식은 새 계정이 자동으로 막힌 채 태어난다.
마이크로소프트 문서도 같은 말을 한다 — *"기본적으로 사용자는 restricted 역할을 받는다."*

---

## 기존 채팅방 지우기 — 여기부터는 Graph 앱 전용

**감독 채팅은 켠 뒤의 새 방에만 걸린다.** 이전에 만들어진 학생끼리의 방은 그대로 살아 있다.
지우는 것 말고 방법이 없다.

**이건 되돌릴 수 없는 작업이고(7일 뒤 영구), 본체 설정과 분리해야 한다.**
채팅을 막는 것만으로 충분한 학교도 많다.

### 왜 앱 전용인가

| | 위임 | 앱 전용 |
|---|---|---|
| 내 채팅방 목록 | 된다 | 된다 |
| **남의 채팅방 목록** | **안 된다** | 된다 (`Chat.ReadBasic.All`) |
| 채팅방 삭제 | 된다 (테넌트 관리자 한정) | 된다 (`Chat.ManageDeletion.All`) |

**삭제는 위임으로도 되는데 목록을 못 얻는다.** 지우려면 id 를 알아야 하므로 앱 등록이 강제된다.

### 앱은 짧게 쓰고 지운다

관리자 동의까지 **전부 명령이다.** 포털에서 단추를 누를 필요가 없다.

```
POST /applications                                # 앱
POST /servicePrincipals                           # 서비스 주체
POST /servicePrincipals/{id}/appRoleAssignments   # 이것이 관리자 동의 그 자체
POST /applications/{id}/addPassword               # 시크릿 (만료를 7일로 박는다)
```

역할 id 는 **하드코딩하지 말고** Graph 서비스 주체
(`appId eq '00000003-0000-0000-c000-000000000000'`)의 `appRoles` 에서 `value` 로 찾는다.
필요한 위임 범위는 `Application.ReadWrite.All` + `AppRoleAssignment.ReadWrite.All`.

**끝나면 앱 삭제 + 삭제된 항목에서 영구 제거 + 로컬 자격 증명 파일 삭제.**
남는 것은 감사 기록뿐이다. **만드는 함수와 지우는 함수는 반드시 쌍으로 낸다.**

### 절차

1. 앱 등록 · 권한 부여 (`Chat.ReadBasic.All` · `Chat.ManageDeletion.All` · `User.ReadBasic.All`)
2. 전 사용자 순회 → `GET /users/{id}/chats` → 중복 제거 → CSV **(읽기 전용)**
3. dry-run 으로 대상 확인
4. 삭제 → **재고를 다시 돌려 검증**
5. 앱·자격 증명 삭제

참고 구현: `C:\Users\user\scripts\teams-chat-0{0..4}-*.ps1`
(0=진단, 1=앱 등록, 2=재고, 3=삭제, 4=뒷정리)

---

## 실기 함정 — 다시 하면 또 만난다

### 1. WAM 창 핸들 오류 — teavel 은 이미 풀어 뒀다

콘솔 창 없는 프로세스에서 `Connect-MgGraph` 를 부르면 죽는다:

    InteractiveBrowserCredential authentication failed: A window handle must be configured

손으로 할 때는 **진짜 PowerShell 창에서 실행**하면 된다.

**teavel 에 새로 넣을 것은 없다.** `Teavel.M365.psm1` 이 이 오류를 이미 알고 있고
(`8fe596b` — `M365Host.cs` 가 부모 콘솔을 물려준다), 더 나아가 **창 방식과 코드 방식을
둘 다 해 본다.** 그 이유가 중요하다:

| 막히는 자리 | 언제 |
|---|---|
| 창 방식 | 상주 세션에 창이 없을 때 (`A window handle must be configured`, `AADSTS900561`) |
| **코드 방식** | **학교 테넌트가 조건부 액세스로 인증 흐름 자체를 막을 때** (2026-08-27 실측) |

즉 `-UseDeviceCode` 는 만능 우회로가 아니다. **어느 쪽이 막혔는지는 테넌트마다 다르고
미리 알 수 없으므로 짐작하지 말고 둘 다 해 본다.** 새로 만드는 Graph 앱 전용 경로도
이 기존 장치를 그대로 물려 써야 한다 — 같은 판단을 두 벌로 만들 이유가 없다.

### 2. 권한 전파 지연을 "권한 없음" 으로 오진하지 말 것

앱 권한 부여 직후 `403 Authorization_RequestDenied` 가 몇 분간 계속 났다.
**토큰에는 이미 역할이 실려 있었는데** Graph 인가 캐시가 늦게 따라온 것이었다.

**판별법 — 토큰의 `roles` 클레임을 직접 뜯어본다.** Graph 모듈을 안 거치므로
모듈 문제와 권한 문제를 한 번에 갈라낸다.

```powershell
$tok = Invoke-RestMethod -Method POST `
    -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
    -Body @{ client_id=$AppId; client_secret=$plain
             scope='https://graph.microsoft.com/.default'; grant_type='client_credentials' }
$p = $tok.access_token.Split('.')[1].Replace('-','+').Replace('_','/')
switch ($p.Length % 4) { 2 { $p += '==' } 3 { $p += '=' } }
([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p)) | ConvertFrom-Json).roles
```

### 3. 안 쓸 속성을 뽑으면 권한이 올라간다

전 사용자 순회에는 `User.ReadBasic.All` 이면 된다. 그런데 `accountEnabled` 를 `$select` 에
넣으면 상위 `User.Read.All` 이 필요해진다. **안 쓸 칸은 빼는 게 권한을 낮춘다.**

### 4. 삭제는 테넌트당 초당 1건

`DELETE /chats/{id}` 는 **1초에 1건**만 허용된다(문서 명시). 224건에 약 5분.
대량 작업은 **체크포인트 + 이어받기**가 필수다.

### 5. dry-run 을 완료로 착각하는 사고

실제로 일어났다. **로그 파일이 없다**는 사실로 잡아냈고, **재고를 다시 돌려** 확정했다.

> **"명령이 성공했다" 와 "상태가 바뀌었다" 는 다르다.**
> 파괴적 작업은 실행 후 **독립적인 재조회로 검증**하고, 그 검증을 사람이 아니라 도구가 하게 한다.
> 삭제 로그(요청 수락 기록)만으로 완료를 선언하면 안 된다.

---

## 안 되는 것 · 남는 구멍

| | 상태 | 비고 |
|---|---|---|
| **회의 중 채팅** | **막을 수 없었다** | `Set-CsTeamsMeetingPolicy` 가 `Forbidden 40301`. **전역 관리자인데도 거부됐다** — 메시징·통화 쓰기는 되는데 회의 정책만 막힌다. 교육청 위임 테넌트 제약으로 보이며 **원인 미확인**. 학교마다 다를 수 있다 |
| **채널** | 정책이 관여하지 않는다 | 팀 채널 게시글은 학생끼리 열려 있다. 아래 참조 |
| 이미 있던 채팅방 | 감독이 안 걸린다 | 지우는 수밖에 없다 |
| 게스트 | 역할 배정 불가 | 자동으로 Limited — Restricted 에게 말을 못 걸므로 **이 설계에선 안전한 쪽** |
| 감독 교사가 퇴직하면 | 그 방이 감독 없이 남는다 | 계정을 지우기 전에 다른 Full 사용자를 넣어야 한다 |

**채널에 대하여.** 수업용 팀(Class team)은 템플릿이 멤버의 채널 생성을 꺼둔 채로 만들어져
학생이 채널을 못 만든다. **일반 팀은 기본값이 반대다** — 멤버도 비공개 채널을 만들 수 있다.
확인: `Get-Team | Select DisplayName, AllowCreateUpdateChannels, AllowCreatePrivateChannels`
둘 다 `False` 여야 수업용 팀 설정이 걸린 것이다.

---

## teavel 반영 설계

### 어디에 붙나

**팀·그룹·구성원을 다 정리한 다음의 마무리 단계다.** 명단이 정리돼 있어야 교사를
고를 수 있으므로 순서가 그렇게 정해진다.

기존 화면 관례를 그대로 쓴다 — **왼쪽 나무에서 고르고 오른쪽 패널로 적용**하는
`[그룹에 넣기]` 와 같은 모양이다.

```
구성원 낱장
  왼쪽 나무에서 교사 그룹(또는 '교사' 가지)을 고른다
  [채팅 정책 적용]  →  오른쪽 패널
```

패널이 보여 줄 것:

```
채팅 통제를 켭니다

  학생끼리 새 채팅을 만들 수 없게 하고, 교사와는 주고받을 수 있게 합니다.
  이미 있는 채팅방과 회의 중 채팅, 채널에는 걸리지 않습니다.

  Full 권한을 받을 사람 (12명)          ← 이 명단만 확인하시면 됩니다
    김○○  kim@school.kr
    …
  나머지 349명은 자동으로 제한됩니다. 앞으로 만들어질 계정도 같습니다.

  [ ] 통화도 같이 막기 (권장)

                                   [취소]  [적용]
```

**Full 받는 명단만 보이면 된다.** 나머지는 기본값이 맡으므로 볼 것이 없고,
위험은 전부 이 명단 안에 있다.

**한눈에 낱장**에는 현재 상태 한 줄 — `채팅: 감독 중 (교사 12명 Full)` 또는 `채팅: 통제 없음`.

### 함수

`scripts/Teavel.M365.psm1` 기준. 기존 31개와 겹치지 않는 것만.

| 함수 | 하는 일 | 모듈 |
|---|---|---|
| `Get-TeavelChatPolicyState` | 현재 채팅·통화·감독 설정 상태 (읽기 전용) | Teams |
| `Set-TeavelChatSupervision` | 2~5단계를 순서대로. 교사 UPN 목록을 받는다 | Teams |
| `Remove-TeavelChatSupervision` | 되돌리기 | Teams |
| `Test-TeavelGraphToken` | 자격 증명의 실제 `roles` 클레임 표시 | 진단 |
| *(로그인)* | **새로 만들지 않는다.** 기존 창·코드 두 길 시도를 물려 쓴다 | — |
| `New-TeavelWorkApp` / `Remove-TeavelWorkApp` | 작업용 앱 등록·권한·삭제 — **쌍으로** | Graph |
| `Get-TeavelTenantChat` | 전 사용자 채팅방 재고 (앱 전용) | Graph |
| `Remove-TeavelTenantChat` | 초당 1건 · 체크포인트 · 이어받기 | Graph |

**앞의 셋만으로 이 기능의 본체가 완성된다.** Graph 쪽 넷은 '기존 방 지우기' 라는
별도 기능이고, 별도 확인을 받아야 한다.

### 넣지 말 것

- **회의 정책 조작** — `Forbidden 40301` 로 막힌다. 전역 관리자인데도 거부됐고 원인이
  미확인이다. 기능으로 내걸면 학교마다 실패한다.
- **상시 보유하는 Graph 앱** — "짧게 쓰고 지운다" 를 깨뜨린다.
- **학생 그룹 기준 차단** — ①의 이유.

### 지켜야 할 것

- **순서를 바꾸지 않는다** — 역할 부여 → 감독 켜기 → 채팅 열기. 거꾸로 하면 무방비 구간이 생긴다.
- **적용 전 미리보기**, 적용 후 **재조회 검증**. 정책 반영에 시간이 걸리므로
  "몇 시간 뒤 반영됩니다" 를 화면에 적는다.
- **한 사람이 막혀도 나머지는 마저 한다** — 기존 `Set-TeavelDisplayName` 계열과 같은 태도.

---

## 실기 기록 (늘푸른중, 2026-09-03)

| | |
|---|---|
| 교사 | 13명 (실교사 9 + 테스트 4) 에게 `EduFaculty` = Full |
| 학생 | 정책 없음 = Global = Restricted |
| 삭제한 채팅방 | **224개** (1:1 214 / 그룹 7 / 회의 3), 실패 0건 |
| 그중 교사가 낀 방 | 134 (관리자 지시로 함께 삭제) |
| 학생끼리만 | 90 |
| 사용자 수 | 367명 |
| 뒷정리 | 작업용 앱·자격 증명 삭제 및 영구 제거 확인 |

인계 문서: `C:\Users\user\M365Admin\teams-chat-handoff-2026-09-03.md`
감사 기록: `C:\Users\user\M365Admin\inventory\teams-chats-20260903-*.csv`

### 출처

- [Use supervised chats (Microsoft Learn)](https://learn.microsoft.com/en-us/microsoftteams/supervise-chats-edu)
- [Delete chat (Microsoft Graph)](https://learn.microsoft.com/en-us/graph/api/chat-delete)
- [List chats (Microsoft Graph)](https://learn.microsoft.com/en-us/graph/api/chat-list)
- [Private channels in Microsoft Teams](https://learn.microsoft.com/en-us/microsoftteams/private-channels)
