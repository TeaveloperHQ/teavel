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

## 걸리는 곳 — 진짜 EXO 가 깔려 있으면 진다

**`PSModulePath` 를 앞세우는 것으로는 부족하다.** 가짜를 맨 앞에 두고 판 번호를 99.0.0 으로
올려도 졌다. 순서 문제가 아니라 이런 일이 벌어진다.

```
가짜가 이김 →  New-UnifiedGroup 같은 개별 명령
진짜가 이김 →  Connect-ExchangeOnline
                  └─ 실행되면서 /tmp/tmpEXO_*.psm1 을 만들어 넣는다
                     이게 나중에 들어와 가짜 명령들을 덮어쓴다
```

증상이 고약하다. 그룹을 만들려 하면 이렇게 나온다.

```
A server side error has occurred because of which the operation could not be completed.
```

가짜 모듈에는 없는 문구라 Teavel 버그로 보인다. **오류 details 의 경로가 `/tmp/tmpEXO_`
로 시작하면 이 경우다.** 진짜 폴더를 잠시 옮겼다가 되돌리는 것이 확실하다.

```bash
REAL=~/.local/share/powershell/Modules/ExchangeOnlineManagement
mv "$REAL" "$REAL.parked"
# ... 실험 ...
mv "$REAL.parked" "$REAL"      # 반드시 되돌린다
```

## 채워 넣은 것

실제 학교 테넌트에서 본 목록 그대로다. 지어낸 이름으로는 위의 둘이 안 나왔다 —
한글 이름이 별칭에서 뭉개지는 것도, `All Company` 가 0명으로 오는 것도 실물의 성질이다.
자세한 것은 [../../docs/m365.md](../../docs/m365.md).
