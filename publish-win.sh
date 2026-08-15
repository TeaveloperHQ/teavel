#!/usr/bin/env bash
# Teavel Windows 빌드 — teaveloper 포털의 빌드 파이프라인이 실행하는 스크립트.
#
# 배포용 exe 는 포털에서 빌드한다(앱 이름 통일성). 이 저장소는 소스만 제공하며
# GitHub 릴리스로 exe 를 배포하지 않는다 — teaveloper-runner 와 같은 방침이다.
#
# 묶는 것:
#   ① teavel.exe (self-contained) — 교사 PC 에 .NET 런타임 설치 불필요
#   ② scripts/  — 도구 PowerShell 모듈. 교사가 열어 무엇을 하는지 확인할 수 있게 그대로 둔다.
#   ③ catalog/  — teaveloper 앱 선언. 앱에 MCP 가 붙으면 이 파일만 갱신하면 되고 exe 는 그대로다.
#
# 언어 모델(GGUF)은 동봉하지 않는다. 생기부 도우미와 같은 방식으로
# 앱이 최초 실행 시 내려받는다(`teavel 모델`). 배포 파이프라인은 주소만 심으면 된다:
#   TEAVEL_GGUF_URL=<핀 고정 읽기전용 주소>
#
# 사용:  ./publish-win.sh [출력폴더]        (기본: publish/win-x64)
set -euo pipefail
cd "$(dirname "$0")"

OUT="${1:-publish/win-x64}"
DOTNET="${DOTNET:-dotnet}"

# 시스템 dotnet 에 SDK 가 없고 ~/.dotnet 에만 있는 환경을 배려한다.
if ! "$DOTNET" --list-sdks 2>/dev/null | grep -q .; then
  if [ -x "$HOME/.dotnet/dotnet" ]; then DOTNET="$HOME/.dotnet/dotnet"; fi
fi

echo "▶ teavel.exe publish → $OUT"
"$DOTNET" publish src/Teavel.Cli/Teavel.Cli.csproj \
  -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$OUT"

# csproj 가 이미 복사하지만, publish 출력에서도 확실히 자리 잡게 한 번 더 맞춘다.
echo "▶ scripts/ · catalog/ 동봉"
mkdir -p "$OUT/scripts" "$OUT/catalog"
cp -f scripts/*.ps1 scripts/*.psm1 "$OUT/scripts/"
cp -f catalog/*.json "$OUT/catalog/"

# 선언과 스크립트가 어긋난 채로 배포되는 일을 막는다. 어긋나면 빌드를 실패시킨다.
# 서명보다 먼저 해야 한다 — 이 뒤로 스크립트를 고치면 서명이 깨진다.
echo "▶ 자가점검 (도구 선언 ↔ PowerShell 대조)"
"$DOTNET" run --project src/Teavel.Cli -- 자가점검 >/dev/null 2>&1 || {
  echo "  ! 도구 선언과 스크립트가 어긋났습니다. 'teavel 자가점검' 으로 확인하세요."
  exit 1
}
echo "  · 통과"

# ── 코드 서명 ──
#
# exe 서명만으로는 부족하다. Teavel 의 실제 동작은 scripts/*.psm1 이 하고, 그건 별도 파일이라
# exe 에 서명해도 서명되지 않은 채 남는다.
#
# 왜 중요한가: 우리는 PowerShell 을 -ExecutionPolicy Bypass 로 부르지만 그건 Process 범위다.
# 학교가 그룹 정책(MachinePolicy/UserPolicy)으로 AllSigned·RemoteSigned 를 걸어 두면
# 그룹 정책이 우선하므로 Bypass 가 무시되고 스크립트가 아예 돌지 않는다.
# RemoteSigned 도 위험하다 — 압축을 받아 푼 파일에는 '다른 컴퓨터에서 온 파일' 표시가 붙어
# 원격으로 취급되기 때문이다.
#
# TEAVEL_SIGN_CMD 에 파일 하나를 받는 서명 명령을 주면 exe 와 스크립트를 모두 서명한다.
#   예(Linux CI): TEAVEL_SIGN_CMD='jsign --keystore … --alias … --tsaurl http://timestamp.digicert.com'
#   예(Windows):  TEAVEL_SIGN_CMD='signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256'
# 타임스탬프를 반드시 넣을 것 — 없으면 인증서가 만료되는 순간 기존 배포본의 서명이 죽는다.
if [ -n "${TEAVEL_SIGN_CMD:-}" ]; then
  echo "▶ 코드 서명 (exe + 스크립트)"
  for f in "$OUT/teavel.exe" "$OUT"/scripts/*.ps1 "$OUT"/scripts/*.psm1; do
    [ -f "$f" ] || continue
    # shellcheck disable=SC2086
    $TEAVEL_SIGN_CMD "$f" || { echo "  ! 서명 실패: $f"; exit 1; }
    echo "  · $(basename "$f")"
  done
else
  echo "▶ 코드 서명 건너뜀 (TEAVEL_SIGN_CMD 미설정)"
  echo "  ! 스크립트가 서명되지 않았습니다. 학교 그룹 정책이 AllSigned·RemoteSigned 면"
  echo "    교사 PC 에서 엑셀·워드·아웃룩 기능이 동작하지 않습니다."
fi

echo ""
echo "✅ 완료: $OUT"
ls -1 "$OUT" | sed 's/^/   /'
echo ""
echo "─────────────────────────────────────────────────────────────────"
echo "설치 프로그램에 넣을 것 — 마지막 단계에서 이 한 줄:"
echo ""
echo "    teavel.exe 설치"
echo ""
echo "  · PATH 와 탐색기 우클릭 메뉴를 등록한다. 묻지 않고 바로 끝난다."
echo "  · 반드시 '그 선생님 계정으로' 실행할 것. 관리자로 승격해 돌리면"
echo "    관리자 계정의 PATH 에 등록돼 정작 선생님에게는 잡히지 않는다."
echo "    (%LOCALAPPDATA% 에 설치하는 사용자별 설치라면 승격이 필요 없다)"
echo ""
echo "  그러면 선생님은 PowerShell 을 열고 'teavel' 만 치면 된다."
echo "─────────────────────────────────────────────────────────────────"
