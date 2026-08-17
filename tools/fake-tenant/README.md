# 가짜 테넌트

테넌트도 로그인도 없이 `teavel m365` 흐름 전체를 리눅스에서 돌려 보는 장치.

진짜와 같은 이름의 함수를 내는 PowerShell 모듈 둘이다. `PSModulePath` 에 이 폴더를
얹으면 Teavel 이 진짜 대신 이것을 부른다.

## 왜

남의 학교 테넌트에 대고 시험할 수는 없다. 그렇다고 만들기·이름변경·삭제를 한 번도
돌려 보지 않고 내보내면, **처음 쓰는 사람이 실제 학교에서 처음 겪게 된다.**

여기서 진짜 버그 둘을 잡았다.

- 테넌트의 `3학년_4반`(30명)을 두고 선언의 `3학년 4반` 을 새로 만들려 한 것
- `New-Team @args` — 인자 하나 없이 호출되면서 조용히 성공한 것

## 쓰는 법

```bash
export PSModulePath=$PWD/tools/fake-tenant
export TEAVEL_FAKE_STORE=/tmp/teavel-fake-store.json   # 실행 사이에 상태를 남긴다
rm -f $TEAVEL_FAKE_STORE

# 정리는 전부 그냥 두기(3), 만들기는 승인(y)
printf '3\n3\n3\ny\n' | teavel m365
```

두 번째로 돌리면 아무것도 만들지 않아야 한다 — **여러 번 돌려도 안전**하다는 뜻이다.

`TEAVEL_FAKE_STORE` 를 주지 않으면 프로세스마다 처음 상태로 돌아간다.

## 걸리는 곳

`Import-Module` 은 **판 번호가 아니라 `PSModulePath` 순서로** 고른다.
진짜 `ExchangeOnlineManagement` 가 앞에 있으면 그쪽이 이긴다.
리눅스에서 진짜를 깔아 둔 적이 있으면 `~/.local/share/powershell/Modules` 를 먼저 치워야 한다.

## 채워 넣은 것

실제 학교 테넌트에서 본 목록 그대로다. 지어낸 이름으로는 위의 둘이 안 나왔다 —
한글 이름이 별칭에서 뭉개지는 것도, `All Company` 가 0명으로 오는 것도 실물의 성질이다.
자세한 것은 [../../docs/m365.md](../../docs/m365.md).
