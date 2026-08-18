<#
.SYNOPSIS
    assets/icon.svg · icon-small.svg 로 assets/icon.ico 를 다시 만든다.

.DESCRIPTION
    왜 이 스크립트가 있는가 — 아이콘이 어디서 봐도 흐릿했다.

    까닭은 .ico 안에 든 크기가 16·32·48·64·128·256 여섯 개뿐이었다는 것이다.
    Windows 는 화면 배율에 따라 다른 크기를 달라고 한다. 100% 면 작업 표시줄이 24px,
    125% 면 30px, 150% 면 36px, 175% 면 42px 을 찾는다. 그중 어느 것도 들어 있지 않아서
    Windows 가 매번 가까운 것을 늘리거나 줄여 썼고, 그 늘리고 줄인 결과가 흐릿함이었다.

    그래서 실제로 요구되는 크기를 미리 다 넣어 둔다 — 16·20·24·32·40·48·64·96·128·256.

    작은 크기는 icon-small.svg(꺾쇠 없는 판)를 쓴다. 16px 은 가로 16칸이 전부라
    꺾쇠와 죽방을 같이 넣으면 둘 다 뭉개진다.

    큰 그림을 줄여서 만들지 않고 크기마다 새로 그린다. 1024px 그림 하나를 16px 로 줄이면
    가는 획이 뭉개지는데, 그것이 바로 없애려는 흐릿함이다. 그리는 일은 Edge(Chromium)에
    맡긴다 — Windows 에 이미 있어 따로 받을 것이 없다.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/build-icon.ps1
#>

[CmdletBinding()]
param(
    # 결과를 쓸 자리. 비우면 assets/icon.ico.
    [string] $Output,

    # 중간 PNG 를 남겨 눈으로 확인하고 싶을 때.
    [switch] $KeepPng
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# $PSScriptRoot 는 param 기본값 자리에서 비어 있을 수 있다. 본문에서 잡는다.
$root = Split-Path -Parent $PSCommandPath
$assets = Resolve-Path (Join-Path $root '..\assets')

if (-not $Output) { $Output = Join-Path $assets 'icon.ico' }

# 크기와, 그 크기를 어느 SVG 로 그릴지.
#
# 배열로 둔다. [ordered] 해시테이블을 쓰면 안 된다 — 정수로 색인하면 <키>가 아니라
# <몇 번째>로 읽어서 $plan[16] 이 조용히 빈 값이 된다. 그러면 src 가 빈 <img> 를
# 그리게 되어, 크기는 멀쩡한데 속이 텅 빈 아이콘이 나온다(실제로 한 번 그렇게 만들었다).
$plan = @(
    @{ Size = 16;  Svg = 'icon-small.svg' }
    @{ Size = 20;  Svg = 'icon-small.svg' }
    @{ Size = 24;  Svg = 'icon-small.svg' }
    @{ Size = 32;  Svg = 'icon-small.svg' }
    @{ Size = 40;  Svg = 'icon-small.svg' }
    @{ Size = 48;  Svg = 'icon-small.svg' }
    @{ Size = 64;  Svg = 'icon.svg' }
    @{ Size = 96;  Svg = 'icon.svg' }
    @{ Size = 128; Svg = 'icon.svg' }
    @{ Size = 256; Svg = 'icon.svg' }
)

# ── Edge 찾기 ──

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $edge) { throw "Edge 를 찾지 못했습니다. 아이콘을 그리려면 Edge 가 필요합니다." }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("teavel-icon-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

Copy-Item (Join-Path $assets 'icon.svg') $work
Copy-Item (Join-Path $assets 'icon-small.svg') $work

# ── ① SVG 하나당 한 번만 그린다 ──
#
# 크기마다 Edge 를 새로 띄우면 그 시작 비용이 그림 그리는 시간보다 훨씬 크다
# (이 컴퓨터에서 한 번에 1~2분이 걸렸다). 한 장에 나란히 늘어놓고 한 번에 찍은 뒤
# 잘라 쓴다 — 열 번이 두 번이 된다.

function Invoke-Edge([string] $htmlPath, [int] $w, [int] $h, [string] $pngPath) {
    $edgeArgs = @(
        '--headless', '--disable-gpu', '--hide-scrollbars', '--no-first-run'
        '--force-device-scale-factor=1'
        '--default-background-color=00000000'
        "--window-size=$w,$h"
        "--screenshot=$pngPath"
        $htmlPath
    )

    # Start-Process 로 부른다. `& $edge` 로 부르면 Edge 가 stderr 로 쏟는 진단문을
    # PowerShell 이 ErrorRecord 로 감싸고, ErrorActionPreference='Stop' 아래에서는
    # 그것이 곧 중단이 된다 — 그림은 멀쩡히 그려졌는데도 실패로 끝난다.
    $log = "$pngPath.log"
    Start-Process -FilePath $edge -ArgumentList $edgeArgs -Wait -NoNewWindow `
                  -RedirectStandardError $log -RedirectStandardOutput "$log.out" | Out-Null

    if (-not (Test-Path $pngPath)) { throw "$htmlPath 를 그리지 못했습니다." }
}

# SVG 별로 묶어서 한 장씩 그린다. 결과: 크기 → 잘라 낸 Bitmap
$rendered = @{}

foreach ($svg in ($plan | ForEach-Object { $_.Svg } | Select-Object -Unique)) {
    $group = @($plan | Where-Object { $_.Svg -eq $svg })

    # 가로로 나란히. x 자리를 미리 정해 두고 그 자리에서 잘라 낸다.
    $x = 0
    $tags = foreach ($g in $group) {
        $tag = "<img src='$svg' width='$($g.Size)' height='$($g.Size)' style='position:absolute;left:${x}px;top:0'>"
        $g.X = $x
        $x += $g.Size
        $tag
    }

    $stripW = $x
    # 해시테이블은 Measure-Object -Property 로 못 읽는다. 값을 먼저 꺼내서 잰다.
    $stripH = ($group | ForEach-Object { $_.Size } | Measure-Object -Maximum).Maximum

    $html = Join-Path $work ("strip-" + [System.IO.Path]::GetFileNameWithoutExtension($svg) + ".html")
    $png  = [System.IO.Path]::ChangeExtension($html, '.png')

    @"
<!doctype html><meta charset="utf-8">
<style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}</style>
$($tags -join "`n")
"@ | Set-Content -Path $html -Encoding utf8

    Write-Host ("  {0} 를 한 장으로 그립니다 ({1}x{2})" -f $svg, $stripW, $stripH)
    Invoke-Edge -htmlPath $html -w $stripW -h $stripH -pngPath $png

    # 잘라 낸다.
    $strip = [System.Drawing.Image]::FromFile($png)
    try {
        foreach ($g in $group) {
            $cut = New-Object System.Drawing.Bitmap($g.Size, $g.Size,
                       [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $gfx = [System.Drawing.Graphics]::FromImage($cut)
            try {
                $gfx.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $src = New-Object System.Drawing.Rectangle $g.X, 0, $g.Size, $g.Size
                $dst = New-Object System.Drawing.Rectangle 0, 0, $g.Size, $g.Size
                $gfx.DrawImage($strip, $dst, $src, [System.Drawing.GraphicsUnit]::Pixel)
            } finally { $gfx.Dispose() }

            $rendered[$g.Size] = $cut
            if ($KeepPng) { $cut.Save((Join-Path $work "icon-$($g.Size).png"),
                                      [System.Drawing.Imaging.ImageFormat]::Png) }
        }
    } finally { $strip.Dispose() }
}

# ── ② 비트맵(DIB) 으로 바꾼다 ──

function Get-DibBytes([System.Drawing.Bitmap] $bmp) {
    $size = $bmp.Width

    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixels = New-Object byte[] ($stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $out = New-Object System.IO.BinaryWriter($ms)

    # BITMAPINFOHEADER. 높이를 두 배로 적는 것이 .ico 규칙이다
    # (색 그림과 마스크가 위아래로 붙어 있다고 보기 때문).
    $out.Write([int] 40)
    $out.Write([int] $size)
    $out.Write([int] ($size * 2))
    $out.Write([int16] 1)
    $out.Write([int16] 32)
    $out.Write([int] 0)
    $out.Write([int] ($size * $size * 4))
    $out.Write([int] 0); $out.Write([int] 0); $out.Write([int] 0); $out.Write([int] 0)

    # 색 그림 — 아래에서 위로 쓴다.
    for ($y = $size - 1; $y -ge 0; $y--) { $out.Write($pixels, $y * $stride, $size * 4) }

    # 마스크 — 32비트 그림은 투명도를 알파로 다루므로 전부 0(불투명)으로 둔다.
    # 자리는 반드시 있어야 한다. 빼면 Windows 가 그림을 반만 읽는다.
    $maskStride = [Math]::Floor(($size + 31) / 32) * 4
    $out.Write((New-Object byte[] ($maskStride * $size)), 0, $maskStride * $size)

    $out.Flush()
    return $ms.ToArray()
}

function Get-PngBytes([System.Drawing.Bitmap] $bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

# ── ③ .ico 로 묶는다 ──

$frames = @()
foreach ($p in $plan) {
    $bmp = $rendered[$p.Size]

    # 전부 비트맵으로 넣는다.
    #
    # 256 만 PNG 로 넣어 봤다 — 파일이 250KB 작아지지만, PNG 로 담긴 항목을 못 읽는
    # 프로그램이 있다(.NET 의 System.Drawing.Icon 이 그렇다. 256 을 달라고 하면 128 을 준다).
    # 아이콘은 우리가 아니라 남이 읽는 파일이라, 크기를 아끼자고 읽는 쪽을 가릴 일이 아니다.
    $bytes = Get-DibBytes $bmp

    $frames += [pscustomobject]@{ Size = $p.Size; Bytes = $bytes }
    Write-Host ("  {0,4}px  {1,8:N0} bytes  ({2})" -f $p.Size, $bytes.Length, $p.Svg)
}

# 바이트를 직접 쌓는다.
#
# 처음에는 BinaryWriter 로 썼는데, 같은 코드가 단독으로 돌릴 때는 맞고 이 스크립트
# 안에서는 몇몇 필드를 통째로 빼먹었다. 어느 오버로드가 잡히느냐가 문맥을 타는 것이라
# 짐작하기 어렵다. 여기서는 몇 바이트짜리인지가 곧 파일 규격이므로, 짐작할 여지를 없앤다.
$bytes = New-Object System.Collections.Generic.List[byte]

function Add-U16([System.Collections.Generic.List[byte]] $list, [int] $value) {
    $list.AddRange([byte[]] [System.BitConverter]::GetBytes([uint16] $value))
}
function Add-U32([System.Collections.Generic.List[byte]] $list, [long] $value) {
    $list.AddRange([byte[]] [System.BitConverter]::GetBytes([uint32] $value))
}

# ICONDIR
Add-U16 $bytes 0                  # 예약
Add-U16 $bytes 1                  # 1 = 아이콘
Add-U16 $bytes $frames.Count

# ICONDIRENTRY — 256 은 크기 칸에 0 으로 적는다(한 바이트라 256 이 안 들어간다).
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bytes.Add([byte] $dim)
    $bytes.Add([byte] $dim)
    $bytes.Add([byte] 0)          # 팔레트 색 수 — 32비트라 0
    $bytes.Add([byte] 0)          # 예약
    Add-U16 $bytes 1              # 평면 수
    Add-U16 $bytes 32             # 비트 수
    Add-U32 $bytes $f.Bytes.Length
    Add-U32 $bytes $offset
    $offset += $f.Bytes.Length
}

foreach ($f in $frames) { $bytes.AddRange([byte[]] $f.Bytes) }

$target = [System.IO.Path]::GetFullPath($Output)
[System.IO.File]::WriteAllBytes($target, $bytes.ToArray())

foreach ($bmp in $rendered.Values) { $bmp.Dispose() }

# ── ④ 쓴 것을 도로 읽어 확인한다 ──
#
# 이 확인이 없어서 <b>Windows 가 아예 못 읽는 아이콘</b>을 만들어 놓고도 몰랐다.
# 크기 목록은 그럴듯하게 찍혔고 파일도 생겼는데, 정작 열어 보면 "매개 변수가 잘못되었습니다"
# 였다. 아이콘은 눈으로 확인하기 전까지 깨진 티가 안 나므로 여기서 반드시 막는다.
Write-Host ""
Write-Host "  확인"

$check = [System.IO.File]::ReadAllBytes($target)

$count = [System.BitConverter]::ToUInt16($check, 4)
if ($count -ne $frames.Count) { throw "목록이 깨졌습니다: $count 개로 적혔습니다(있어야 할 것은 $($frames.Count) 개)." }

# 목록의 각 칸이 제 자리를 가리키는지.
for ($i = 0; $i -lt $count; $i++) {
    $o = 6 + 16 * $i
    $dim = if ($check[$o] -eq 0) { 256 } else { [int] $check[$o] }
    $len = [System.BitConverter]::ToUInt32($check, $o + 8)
    $at  = [System.BitConverter]::ToUInt32($check, $o + 12)

    if ($dim -ne $frames[$i].Size)      { throw "$i 번째 칸의 크기가 $dim 로 적혔습니다(있어야 할 것은 $($frames[$i].Size))." }
    if ($len -ne $frames[$i].Bytes.Length) { throw "$dim px 의 길이가 어긋납니다." }
    if ($at + $len -gt $check.Length)   { throw "$dim px 가 파일 밖을 가리킵니다." }

    Write-Host ("    {0,3}px  {1,8:N0} bytes  @ {2:N0}" -f $dim, $len, $at)
}

# 실제로 열어 본다.
#
# 256 은 빼고 잰다. System.Drawing.Icon 은 256px 항목을 다루지 못해서, 있는데도
# 128 을 돌려준다 — 파일이 아니라 그 클래스의 한계다(탐색기는 제대로 읽는다).
foreach ($f in $frames) {
    if ($f.Size -ge 256) { continue }

    $icon = New-Object System.Drawing.Icon($target, $f.Size, $f.Size)
    try {
        if ($icon.Width -ne $f.Size) {
            throw "$($f.Size)px 를 달라고 했는데 $($icon.Width)px 가 나왔습니다 — 그 크기가 안 들어갔습니다."
        }
    } finally { $icon.Dispose() }
}
Write-Host ("    {0}개 크기가 제대로 들어갔습니다." -f $frames.Count)

Write-Host ""
Write-Host ("  → {0}  ({1:N0} bytes, {2}개 크기)" -f $target, (Get-Item $target).Length, $frames.Count)

if ($KeepPng) { Write-Host "  중간 PNG: $work" }
else { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
