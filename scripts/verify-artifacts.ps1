<#
.SYNOPSIS
  产物断言：发布目录 dist\<rid>\ 里的东西对不对（docs/P22-bundle/11 §9 精简版）。

.DESCRIPTION
  把“包里必须有 / 必须没有 / 位数一致”变成机器可判的断言。
  build-dist.ps1 [4/5] 自动调用；也可单独跑。

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-artifacts.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-artifacts.ps1 -Platform x64
#>
[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64')]
    [string]$Platform = 'x86'
)

$ErrorActionPreference = 'Stop'
$repoRoot = if ($PSScriptRoot) { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path } else { (Get-Location).Path }
$rid = if ($Platform -eq 'x86') { 'win-x86' } else { 'win-x64' }
$dir = Join-Path $repoRoot (Join-Path 'dist' $rid)
# 发布位数期望：x86=0x14C，x64=0x8664
$expectedMachine = if ($Platform -eq 'x86') { 0x14C } else { 0x8664 }

$failures = New-Object System.Collections.Generic.List[string]
$passes = New-Object System.Collections.Generic.List[string]

function Pass([string]$m) { $passes.Add($m); Write-Host "  [OK]   $m" -ForegroundColor Green }
function Fail([string]$m) { $failures.Add($m); Write-Host "  [FAIL] $m" -ForegroundColor Red }

function Get-PeMachine([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    return [BitConverter]::ToUInt16($b, $pe + 4)
}

Write-Host ""
Write-Host "=== HaloAgent 产物断言 ($rid) ===" -ForegroundColor Cyan
Write-Host "目录: $dir"
Write-Host ""

if (-not (Test-Path $dir)) { Fail "发布目录不存在：$dir（先跑 build-dist.ps1）" }
else {
    # ---- A 必须存在（11 项）----
    Write-Host "[A] 必须存在" -ForegroundColor Cyan
    foreach ($f in @('HaloAgent.exe', 'HaloAgent.Engine.exe', 'libcef.dll', 'icudtl.dat',
        'chrome_elf.dll', 'snapshot_blob.bin', 'CefSharp.Core.dll',
        'CefSharp.BrowserSubprocess.exe', 'CefSharp.WinForms.dll',
        'System.Private.CoreLib.dll', 'wwwroot/index.html')) {
        if (Test-Path (Join-Path $dir $f)) { Pass "A-exists $f" }
        else { Fail "A-exists 缺少 $f" }
    }

    # ---- B 必须不存在（6 项）----
    Write-Host ""
    Write-Host "[B] 必须不存在" -ForegroundColor Cyan
    $pdbs = @(Get-ChildItem $dir -Recurse -Include *.pdb -File -ErrorAction SilentlyContinue)
    if ($pdbs.Count -eq 0) { Pass "B-pdb 无 .pdb 落地（DebugType=embedded）" }
    else { Fail "B-pdb 发现 $($pdbs.Count) 个 .pdb（$($pdbs[0].Name)…），DebugType=embedded 未生效" }
    foreach ($f in @('dev.flag', 'disable-gpu.flag', 'force-software.flag')) {
        if (Test-Path (Join-Path $dir $f)) { Fail "B-flag 不应随包：$f" }
        else { Pass "B-flag 无 $f" }
    }
    $deps = Get-ChildItem $dir -Filter *.deps.json -File -ErrorAction SilentlyContinue
    $anycpu = $false
    foreach ($d in $deps) {
        $j = Get-Content -Raw $d.FullName | ConvertFrom-Json
        # runtimeTarget.name 不带 RID 后缀 = AnyCPU 产物（11 §9.2）
        if ($j.runtimeTarget.name -match '^\.NETCoreApp,Version=v6\.0$') { $anycpu = $true }
    }
    if (-not $anycpu) { Pass "B-anycpu deps.json 指明 RID（非 AnyCPU）" }
    else { Fail "B-anycpu 存在未指明 RID 的 deps.json（AnyCPU 嫌疑）" }
    $xunit = @(Get-ChildItem $dir -Filter 'xunit*' -File -ErrorAction SilentlyContinue)
    if ($xunit.Count -eq 0) { Pass "B-test 无 xunit 残留" }
    else { Fail "B-test 发现测试残留：$($xunit[0].Name)…" }

    # ---- C 位数一致（3 项）----
    Write-Host ""
    Write-Host "[C] 位数一致（期望 0x$($expectedMachine.ToString('X4'))）" -ForegroundColor Cyan
    foreach ($f in @('HaloAgent.exe', 'HaloAgent.Engine.exe', 'libcef.dll')) {
        $p = Join-Path $dir $f
        if (-not (Test-Path $p)) { Fail "C-arch $f 不存在，跳过" }
        else {
            $m = Get-PeMachine $p
            if ($m -eq $expectedMachine) { Pass "C-arch $f = 0x$($m.ToString('X4'))" }
            else { Fail "C-arch $f = 0x$($m.ToString('X4'))，与 -Platform $Platform 不符（x86/x64 libcef 混装）" }
        }
    }

    # ---- D 版本一致（2 项）----
     Write-Host "[D] 版本一致" -ForegroundColor Cyan
     $verXml = [xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))
     # 注意：props 里有多个 PropertyGroup，直接 .Version 会拿到数组（含空值），必须取第一个非空并 Trim
     $expectedVer = ($verXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version.Trim()
    foreach ($f in @('HaloAgent.exe', 'HaloAgent.Engine.exe')) {
        $p = Join-Path $dir $f
        if (-not (Test-Path $p)) { Fail "D-ver $f 不存在，跳过" }
        else {
            $pv = (Get-Item $p).VersionInfo.ProductVersion
            if ($pv -like "$expectedVer*") { Pass "D-ver $f ProductVersion=$pv" }
            else { Fail "D-ver $f ProductVersion=$pv，与 Directory.Build.props ($expectedVer) 不一致" }
        }
    }

    # ---- E wwwroot 内容（3 项）----
    Write-Host ""
    Write-Host "[E] wwwroot 内容" -ForegroundColor Cyan
    $wwwroot = Join-Path $dir 'wwwroot'
    $js = @(Get-ChildItem $wwwroot -Recurse -Include *.js -File -ErrorAction SilentlyContinue)
    $css = @(Get-ChildItem $wwwroot -Recurse -Include *.css -File -ErrorAction SilentlyContinue)
    if ($js.Count -ge 1) { Pass "E-wwwroot js x$($js.Count)" } else { Fail "E-wwwroot 无 js（前端未构建？）" }
    if ($css.Count -ge 1) { Pass "E-wwwroot css x$($css.Count)" } else { Fail "E-wwwroot 无 css" }
     $big = @($js | Where-Object { $_.Length -gt 1MB })
     # 上限 1MB：Win7 只要求能跑，包体积由 manualChunks 控制；超限说明该拆包了（11 §9 E组）。
     if ($big.Count -eq 0) { Pass "E-size 单个 js ≤ 1MB" }
     else { Fail "E-size 过大：$($big[0].Name) $([math]::Round($big[0].Length/1KB))KB" }

    # ---- F 依赖与安全（2 项）----
    Write-Host ""
    Write-Host "[F] 依赖版本" -ForegroundColor Cyan
    $core = Join-Path $dir 'CefSharp.Core.dll'
    if (Test-Path $core) {
        $fv = (Get-Item $core).VersionInfo.FileVersion
        if ($fv -like '109.*') { Pass "F-cefver CefSharp.Core=$fv" }
        else { Fail "F-cefver CefSharp.Core=$fv，必须是 109.x（Win7 末代）" }
    } else { Fail "F-cefver CefSharp.Core.dll 不存在" }
    $leak = @(Get-ChildItem $dir -Recurse -Include *.snk, *.pfx -File -ErrorAction SilentlyContinue)
    if ($leak.Count -eq 0) { Pass "F-keys 无密钥文件残留" }
    else { Fail "F-keys 发现密钥残留：$($leak[0].Name)" }
}

Write-Host ""
Write-Host "=== 结果：通过 $($passes.Count) / 失败 $($failures.Count) ===" -ForegroundColor $(if ($failures.Count -eq 0) { 'Green' } else { 'Red' })
if ($failures.Count -gt 0) {
    $i = 1
    foreach ($f in $failures) { Write-Host "  $i. $f" -ForegroundColor Red; $i++ }
    exit 1
}
Write-Host "产物断言全部通过。" -ForegroundColor Green
exit 0
