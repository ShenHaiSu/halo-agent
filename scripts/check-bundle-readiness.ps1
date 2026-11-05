<#
.SYNOPSIS
  打包前置体检：检查 dist 完整性 + build/ 资源缺口 + 环境。只读，不改任何文件。

.DESCRIPTION
  对应 docs\P22-bundle\01-现状与基线.md。
  注意：本脚本按"完整方案"口径统计，比"最小可用"严格。
        最小可用只需 3~4 个阻塞项，见 docs\P22-bundle\10-完整方案-缺口总表.md 末节。
  在你补全资源的过程中反复跑，直到"阻塞项"全部消失。

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-bundle-readiness.ps1
#>

[CmdletBinding()]
param(
    [string] $Version = ''
)

$ErrorActionPreference = 'Continue'
$repoRoot = if ($PSScriptRoot) { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path } else { (Get-Location).Path }
Push-Location $repoRoot

$script:Blocking = 0
$script:Warn = 0

function Ok    { param($m) Write-Host "  [OK]   $m" -ForegroundColor Green }
function Miss  { param($m) Write-Host "  [MISS] $m" -ForegroundColor Red;    $script:Blocking++ }
function Warn  { param($m) Write-Host "  [WARN] $m" -ForegroundColor Yellow; $script:Warn++ }
function Head  { param($m) Write-Host "`n=== $m ===" -ForegroundColor Cyan }

function Get-PeMachine {
    param([string] $Path)
    try {
        $fs = [IO.File]::OpenRead($Path)
        try {
            $br = New-Object IO.BinaryReader $fs
            $fs.Position = 0x3C
            $peOff = $br.ReadInt32()
            $fs.Position = $peOff
            if ($br.ReadUInt32() -ne 0x00004550) { return 'not-pe' }
            return [int]$br.ReadUInt16()
        } finally { $fs.Dispose() }
    } catch { return 'error' }
}

try {
    Write-Host "HaloAgent 打包前置体检" -ForegroundColor White
    Write-Host "仓库：$repoRoot"

    # ============================================================
    Head '1. 环境'
    # ============================================================
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) { Ok "dotnet = $((& dotnet --version).Trim())" } else { Miss 'dotnet 不在 PATH' }

    $iscc = $null
    foreach ($c in @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))) {
        if ($c -and (Test-Path $c)) { $iscc = $c; break }
    }
    if ($iscc) {
        # ISCC.exe 无版本资源，FileVersion 恒为 0.0.0.0 —— 改读 whatsnew.htm
        $ver = (Get-Item $iscc).VersionInfo.FileVersion
        if (-not $ver -or $ver -match '^0\.0\.0\.0$') {
            $cht = Join-Path (Split-Path -Parent $iscc) 'whatsnew.htm'
            if (Test-Path $cht) {
                $m = Select-String -Path $cht -Pattern '<a name="(\d+\.\d+\.\d+)"></a>' | Select-Object -First 1
                if ($m) { $ver = $m.Matches[0].Groups[1].Value }
            }
        }
        if ($ver -and $ver -ne '0.0.0.0') {
            if ([version]$ver -ge [version]'6.2.0') { Ok "ISCC = $ver（>= 6.2，通过）" }
            else { Miss "ISCC = $ver，低于 6.2" }
        } else { Warn '无法判定 ISCC 版本，请在编译时人工确认' }
    } else { Miss '找不到 ISCC.exe' }

    # ============================================================
    Head '2. dist\win-x86（可直接当 SourceDir）'
    # ============================================================
    $d86 = Join-Path $repoRoot 'dist\win-x86'
    if (Test-Path $d86) {
        $all = Get-ChildItem $d86 -Recurse -File
        $mb = [math]::Round(($all | Measure-Object Length -Sum).Sum / 1MB, 1)
        Ok "存在：$($all.Count) 个文件 / $mb MB"

        foreach ($f in @('HaloAgent.exe','HaloAgent.Engine.exe','libcef.dll','icudtl.dat',
                         'chrome_elf.dll','snapshot_blob.bin','CefSharp.Core.dll',
                         'CefSharp.BrowserSubprocess.exe','CefSharp.WinForms.dll',
                         'System.Private.CoreLib.dll','wwwroot\index.html')) {
            if (Test-Path (Join-Path $d86 $f)) { Ok "  $f" } else { Miss "  缺 $f" }
        }

        # 例外：输出名以 HaloAgent.Engine.exe 为基准，兼容 Engine.Server.exe
        if (-not (Test-Path (Join-Path $d86 'HaloAgent.Engine.exe')) -and
            (Test-Path (Join-Path $d86 'Engine.Server.exe'))) {
            Warn '  引擎文件名是 Engine.Server.exe（方案文档里两个名字都提到过，请确认哪个是准的）'
        }

        foreach ($pair in @(@('HaloAgent.exe',0x014C), @('libcef.dll',0x014C))) {
            $m = Get-PeMachine (Join-Path $d86 $pair[0])
            if ($m -eq $pair[1]) { Ok ("  {0} PE=0x{1:X4}（x86）" -f $pair[0], $m) }
            else { Miss ("  {0} PE=0x{1:X4}，期望 0x{2:X4}" -f $pair[0], $m, $pair[1]) }
        }

        $pdbs = Get-ChildItem $d86 -Recurse -Filter *.pdb -File
        if ($pdbs) { Warn "  $($pdbs.Count) 个 .pdb（打包时 Excludes 排掉；根因见 §7.2）" }

        $paks = Get-ChildItem (Join-Path $d86 'locales') -Filter *.pak -File -ErrorAction SilentlyContinue
        if ($paks) {
            $paksMB = [math]::Round(($paks | Measure-Object Length -Sum).Sum / 1MB, 1)
            Warn "  locales 有 $($paks.Count) 个 pak / $paksMB MB（建议裁到 3 个，见 §9.2）"
        }
        foreach ($bad in @('dev.flag','disable-gpu.flag','force-software.flag')) {
            if (Test-Path (Join-Path $d86 $bad)) { Miss "  存在 $bad（应排除）" }
        }
        # WPF 冗余程序集（排除 System.Windows.Forms* 等真正在用的 WinForms）
        $wpf = Get-ChildItem $d86 -File | Where-Object {
            $_.Name -match '^(PresentationFramework|PresentationCore|PresentationNative|PresentationUI|ReachFramework|WindowsBase|System\.Xaml|wpfgfx|PenImc)' -or
            $_.Name -in @('System.Windows.Presentation.dll','System.Windows.Input.Manipulations.dll')
        }
        if ($wpf) {
            $wpfMB = [math]::Round(($wpf | Measure-Object Length -Sum).Sum / 1MB, 1)
            Warn "  WPF 程序集 $($wpf.Count) 个 / $wpfMB MB（§9.1：建议保留，写入已知限制）"
        }
    } else { Miss 'dist\win-x86 不存在，先跑 build-dist.ps1' }

    # ============================================================
    Head '3. dist\win-x64（双版本的第二条腿）'
    # ============================================================
    $d64 = Join-Path $repoRoot 'dist\win-x64'
    if (Test-Path $d64) {
        $m = Get-PeMachine (Join-Path $d64 'libcef.dll')
        if ($m -eq 0x8664) { Ok 'dist\win-x64 存在，libcef.dll PE=0x8664（x64）' }
        else { Miss ("dist\win-x64 的 libcef.dll PE=0x{0:X4}，期望 0x8664" -f $m) }
    } else {
        Miss 'dist\win-x64 不存在 —— P0 阻塞：跑 build-dist.ps1 -Platform x64（见 §7.1）'
    }

    # ============================================================
    Head '4. build\ 资源（P0 阻塞项）'
    # ============================================================
    $need = [ordered]@{
        'installer.iss'                    = '安装器主脚本'
        'license.zh-CN.txt'                = 'LicenseFile'
        'THIRD-PARTY-NOTICES.txt'          = 'CEF/Chromium 合规'
        'logos\app.ico'                    = 'SetupIconFile'
        'msgs\ChineseSimplified.isl'       = '[Languages]'
        'vcredist\vcredist_x86.exe'        = 'x86 VC++ 运行库'
        'vcredist\vcredist_x64.exe'        = 'x64 VC++ 运行库'
        'code\Prereq.pas'                  = '先决条件检测'
        'code\Startup.pas'                 = '安装前关闭实例'
        'code\Uninstall.pas'               = '卸载清理'
    }
    foreach ($k in $need.Keys) {
        $p = Join-Path $repoRoot (Join-Path 'build' $k)
        if (Test-Path $p) { Ok "$k" } else { Miss "$k  ($($need[$k]))" }
    }

    $wz = @('logos\wizard-small.bmp','logos\wizard-large.bmp')
    foreach ($k in $wz) {
        $p = Join-Path $repoRoot (Join-Path 'build' $k)
        if (-not (Test-Path $p)) { Warn "$k 不存在（可选；只要 [Setup] 里没启用 WizardImage* 就无妨）" }
    }

    # installer.iss 内容体检
    $iss = Join-Path $repoRoot 'build\installer.iss'
    if (Test-Path $iss) {
        $txt = Get-Content $iss -Raw
        $lines = ($txt -split "`n").Count
        Ok "installer.iss 存在，$lines 行"
        if ($lines -lt 60) { Miss '  installer.iss 仍是占位桩（< 60 行），需按 §4 重写' }
        if ($txt -notmatch '(?m)^\[Files\]')            { Miss '  缺 [Files] 段' }
        if ($txt -notmatch 'AppId=\{')                  { Miss '  缺 AppId（发布后不可改）' }
        if ($txt -notmatch 'OutputBaseFilename')        { Miss '  缺 OutputBaseFilename' }
        if ($txt -notmatch '#define\s+AppVersion')      { Miss '  缺 #define AppVersion 兜底' }
        if ($txt -notmatch '#if\s+Platform')            { Warn '  未见 #if Platform 架构分支（确认架构条件化）' }
        if ($txt -match 'ArchitecturesAllowed=x86 x64') { Miss '  ArchitecturesAllowed=x86 x64 是语义错误写法（见 §4.1.3）' }
        if ($txt -match 'Program Files')                { Miss '  出现 Program Files 字面量（违反 R-10）' }
        if ($txt -match 'Compression=lzma2\s*$')        { Warn '  Compression=lzma2 缺 /max' }
    }

    # ============================================================
    Head '5. scripts\ 驱动脚本'
    # ============================================================
    foreach ($k in @('build-release.ps1','verify-artifacts.ps1')) {
        $p = Join-Path $repoRoot (Join-Path 'scripts' $k)
        if (Test-Path $p) { Ok $k } else { Miss "$k 不存在（双版本驱动，见 §6）" }
    }
    if (Test-Path (Join-Path $repoRoot 'build-dist.ps1')) {
        Ok 'build-dist.ps1 存在'
        $bd = Get-Content (Join-Path $repoRoot 'build-dist.ps1') -Raw
        if ($bd -notmatch 'DebugType=embedded') { Warn '  build-dist.ps1 缺 -p:DebugType=embedded（会落 5 个 pdb，见 §7.2）' }
        if ($bd -notmatch 'SatelliteResourceLanguages') { Warn '  build-dist.ps1 未指定 SatelliteResourceLanguages' }
    }

    # ============================================================
    Head '6. 宿主侧前置改动（M-1 / M-5）'
    # ============================================================
    $hostOpts = Join-Path $repoRoot 'src\Host.Desktop\Shell\HostOptions.cs'
    if (Test-Path $hostOpts) {
        $ho = Get-Content $hostOpts -Raw
        if ($ho -match '--shutdown') { Ok 'M-5 --shutdown 已实现' }
        else { Warn 'M-5 未实现：--shutdown 缺失（卸载时只能 taskkill 兜底，见 §8.2）' }
        if ($ho -match 'datadir\.txt') { Ok 'M-1 datadir.txt 已实现' }
        else { Warn 'M-1 未实现：datadir.txt 不读取（一期可接受，见 §8.1）' }
    } else { Warn '找不到 HostOptions.cs，路径变了？' }

    $csproj = Join-Path $repoRoot 'src\Host.Desktop\Host.Desktop.csproj'
    if (Test-Path $csproj) {
        $cs = Get-Content $csproj -Raw
        if ($cs -match 'ApplicationIcon')   { Ok 'csproj 有 ApplicationIcon' }   else { Warn 'csproj 缺 ApplicationIcon（exe 无图标，见 §8.3）' }
        if ($cs -match 'EmbeddedResource')  { Ok 'csproj 有 EmbeddedResource' }  else { Warn 'csproj 缺 EmbeddedResource（AppIcons.cs 永远回退系统图标，见 §8.3）' }
    }

    # ============================================================
    Head '7. 遗留产物'
    # ============================================================
    $stale = Join-Path $repoRoot 'publish\mysetup.exe'
    if (Test-Path $stale) { Warn 'publish\mysetup.exe 存在（占位空壳包，建议删除防误发）' } else { Ok '无 publish\mysetup.exe' }

    # ============================================================
    Write-Host ("`n{0}" -f ('=' * 60)) -ForegroundColor White
    if ($script:Blocking -eq 0) {
        Write-Host "阻塞项 0 个，警告 $script:Warn 个 —— 可以开始出包。" -ForegroundColor Green
    } else {
        Write-Host "阻塞项 $script:Blocking 个，警告 $script:Warn 个 —— 先清阻塞项。" -ForegroundColor Red
        Write-Host "对照 docs\P22-bundle\ 目录："
        Write-Host "  只想出包分发  -> 02-最小可用-施工清单.md（4 件事，约 40 分钟）"
        Write-Host "  正式对外发布  -> 10-完整方案-缺口总表.md"
    }
} finally {
    Pop-Location
}

if ($script:Blocking -gt 0) { exit 1 }
exit 0
