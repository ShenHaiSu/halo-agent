<#
.SYNOPSIS
  校验所有"铁律版本/平台"是否被有意或无意改动。

.DESCRIPTION
  本项目有一组**不可升级**的锁定项（CefSharp 109、.NET 6、chrome109 目标等）。
  这些锁定一旦漂移，产物在 Windows 7 上会静默崩坏，
  而开发机（Win10/11）上完全看不出问题。

  这个脚本把"锁定"变成可执行的检查，CI 与本地都必须跑。

.EXAMPLE
  pwsh -File scripts/verify-locks.ps1
  # 全部通过时退出码 0；任一失败退出码 1
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = ''
)

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    # 注意：PS 5.1 用 -File 启动时，param 默认值里 $PSScriptRoot 为空，只能在正文里推导
    $RepoRoot = if ($PSScriptRoot) { Join-Path $PSScriptRoot '..' } else { (Get-Location).Path }
}
$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path $RepoRoot).Path

$failures = New-Object System.Collections.Generic.List[string]
$passes   = New-Object System.Collections.Generic.List[string]

function Pass([string]$msg) {
    $passes.Add($msg)
    Write-Host "  [OK]   $msg" -ForegroundColor Green
}

function Fail([string]$msg) {
    $failures.Add($msg)
    Write-Host "  [FAIL] $msg" -ForegroundColor Red
}

function Read-Text([string]$relPath) {
    $p = Join-Path $RepoRoot $relPath
    if (-not (Test-Path $p)) { return $null }
    return Get-Content -Raw -Path $p
}

Write-Host ""
Write-Host "=== HaloAgent 版本/平台锁校验 ===" -ForegroundColor Cyan
Write-Host "仓库: $RepoRoot"
Write-Host ""

# ------------------------------------------------------------------
Write-Host "[1/7] CefSharp 版本必须恰为 109.1.110" -ForegroundColor Cyan
# ------------------------------------------------------------------
$packagesProps = Read-Text 'Directory.Packages.props'
if (-not $packagesProps) {
    Fail 'Directory.Packages.props 不存在'
} else {
    $expected = '109.1.110'
    foreach ($pkg in @('CefSharp.Common.NETCore', 'CefSharp.WinForms.NETCore')) {
        $pattern = "Include=`"$([regex]::Escape($pkg))`"\s+Version=`"([^`"]+)`""
        $m = [regex]::Match($packagesProps, $pattern)
        if (-not $m.Success) {
            Fail "未在 Directory.Packages.props 中找到 $pkg 的版本声明"
        } elseif ($m.Groups[1].Value -ne $expected) {
            Fail "$pkg 版本为 $($m.Groups[1].Value)，必须是 $expected（Chromium 109 是支持 Win7 的末代）"
        } else {
            Pass "$pkg = $expected"
        }
    }
}

# 检查是否有项目偷偷覆盖了版本（绕过 CPM）
Get-ChildItem -Path (Join-Path $RepoRoot 'src') -Recurse -Filter '*.csproj' -ErrorAction SilentlyContinue |
    ForEach-Object {
        $content = Get-Content -Raw $_.FullName
        if ($content -match 'CefSharp[^>]*Version=') {
            Fail "$($_.Name) 内联指定了 CefSharp 版本，必须走 Directory.Packages.props"
        }
    }

# ------------------------------------------------------------------
Write-Host ""
Write-Host "[2/7] 所有 csproj 的 TFM 必须为 net6.0-windows" -ForegroundColor Cyan
# ------------------------------------------------------------------
$csprojs = Get-ChildItem -Path (Join-Path $RepoRoot 'src') -Recurse -Filter '*.csproj' -ErrorAction SilentlyContinue
if ($csprojs.Count -eq 0) {
    Fail 'src 下没有找到任何 csproj'
} else {
    foreach ($proj in $csprojs) {
        $content = Get-Content -Raw $proj.FullName
        # 允许 TargetFramework 定义在 Directory.Build.props（项目里不写）
        if ($content -match '<TargetFrameworks?>([^<]+)</TargetFrameworks?>') {
            $tfm = $Matches[1].Trim()
            if ($tfm -ne 'net6.0-windows') {
                Fail "$($proj.Name) 的 TFM 是 $tfm，必须是 net6.0-windows"
            } else {
                Pass "$($proj.Name) TFM = net6.0-windows"
            }
        }
    }
}
$buildProps = Read-Text 'Directory.Build.props'
if (-not $buildProps) {
    Fail 'Directory.Build.props 不存在'
} elseif ($buildProps -notmatch '<TargetFramework>net6\.0-windows</TargetFramework>') {
    Fail 'Directory.Build.props 未把 TargetFramework 锁为 net6.0-windows'
} else {
    Pass 'Directory.Build.props 锁 TFM = net6.0-windows'
}

# ------------------------------------------------------------------
Write-Host ""
Write-Host "[3/7] 禁止 AnyCPU，禁止 PublishSingleFile" -ForegroundColor Cyan
# ------------------------------------------------------------------
$allProps = @()
$allProps += Get-ChildItem -Path $RepoRoot -Recurse -Filter '*.props' -ErrorAction SilentlyContinue
$allProps += $csprojs
foreach ($f in $allProps) {
    $content = Get-Content -Raw $f.FullName
    if ($content -match '<PlatformTarget>\s*AnyCPU\s*</PlatformTarget>') {
        Fail "$($f.Name) 设置了 PlatformTarget=AnyCPU（CefSharp 的 libcef.dll 是架构相关的，AnyCPU 会加载失败）"
    }
    if ($content -match '<PublishSingleFile>\s*true\s*</PublishSingleFile>') {
        Fail "$($f.Name) 开启 PublishSingleFile（CefSharp 的非托管资源与子进程不支持单文件发布）"
    }
}
if ($failures.Count -eq 0) { Pass '未发现 AnyCPU / PublishSingleFile' }

# ------------------------------------------------------------------
Write-Host ""
Write-Host "[4/7] global.json 必须锁 .NET 6" -ForegroundColor Cyan
# ------------------------------------------------------------------
$globalJson = Read-Text 'global.json'
if (-not $globalJson) {
    Fail 'global.json 不存在（没有它就无法阻止误用 .NET 8/9 SDK）'
} else {
    try {
        $gj = $globalJson | ConvertFrom-Json
        $ver = $gj.sdk.version
        if ($ver -notmatch '^6\.') {
            Fail "global.json 的 sdk.version = $ver，必须以 6. 开头"
        } else {
            Pass "global.json sdk.version = $ver"
        }
        if ($gj.sdk.rollForward -and $gj.sdk.rollForward -notin @('latestFeature', 'latestPatch', 'disable')) {
            Fail "global.json rollForward = $($gj.sdk.rollForward)，可能跨主版本，建议 latestFeature"
        }
    } catch {
        Fail "global.json 不是合法 JSON: $_"
    }
}

# ------------------------------------------------------------------
Write-Host ""
Write-Host "[5/7] .browserslistrc 必须只含 chrome 109" -ForegroundColor Cyan
# ------------------------------------------------------------------
$browserslist = Read-Text '.browserslistrc'
if (-not $browserslist) {
    Fail '.browserslistrc 不存在'
} else {
    # 注意 @(...)：单行文件在 PS 5.1 下会被解包成标量 string，导致 $lines[0] 取到 Char 而报错
    $lines = @($browserslist -split "`r?`n" | Where-Object { $_.Trim() -ne '' -and -not $_.Trim().StartsWith('#') })
    if ($lines.Count -ne 1) {
        Fail ".browserslistrc 应只有一行 'chrome 109'，实际有 $($lines.Count) 行：$($lines -join ' | ')"
    } elseif ($lines[0].Trim() -ne 'chrome 109') {
        Fail ".browserslistrc 唯一一行是 '$($lines[0].Trim())'，必须是 'chrome 109'"
    } else {
        Pass '.browserslistrc = chrome 109'
    }
}

# ------------------------------------------------------------------
Write-Host ""
Write-Host "[6/7] vite / postcss 的构建目标必须是 chrome109" -ForegroundColor Cyan
# ------------------------------------------------------------------
$viteConfig = Read-Text 'src/Web.Client/vite.config.ts'
if (-not $viteConfig) {
    Fail 'src/Web.Client/vite.config.ts 不存在'
} else {
    if ($viteConfig -notmatch "target:\s*'chrome109'") {
        Fail "vite.config.ts 的 build.target 不是 'chrome109'"
    } else {
        Pass "vite.config.ts build.target = 'chrome109'"
    }
    if ($viteConfig -notmatch "cssTarget:\s*'chrome109'") {
        Fail "vite.config.ts 的 build.cssTarget 不是 'chrome109'（只设 target 不设 cssTarget，CSS 不会降级）"
    } else {
        Pass "vite.config.ts build.cssTarget = 'chrome109'"
    }
}

$postcssConfig = Read-Text 'src/Web.Client/postcss.config.ts'
if ($postcssConfig -and ($postcssConfig -notmatch "chrome 109")) {
    Fail "postcss.config.ts 未引用 'chrome 109' 作为目标浏览器"
} elseif ($postcssConfig) {
    Pass "postcss.config.ts 目标 = chrome 109"
}

# ------------------------------------------------------------------
Write-Host ""
Write-Host "[7/7] tailwindcss 必须是 3.4.x（禁止 4.x）" -ForegroundColor Cyan
# ------------------------------------------------------------------
$pkgJson = Read-Text 'src/Web.Client/package.json'
if (-not $pkgJson) {
    Fail 'src/Web.Client/package.json 不存在'
} else {
    try {
        $pkg = $pkgJson | ConvertFrom-Json
        $tw = $pkg.devDependencies.tailwindcss
        if (-not $tw) {
            Fail 'package.json 未声明 tailwindcss'
        } elseif ($tw -match '^\D*4\.') {
            Fail "tailwindcss = $tw。v4 的基线是 Chrome 111+，会产出 color-mix()/oklch() 导致 Chrome 109 白屏。必须用 3.4.x"
        } elseif ($tw -notmatch '3\.4\.') {
            Fail "tailwindcss = $tw，必须是 3.4.x"
        } else {
            Pass "tailwindcss = $tw"
        }
    } catch {
        Fail "package.json 解析失败: $_"
    }
}

# ------------------------------------------------------------------
Write-Host ""
Write-Host "=== 结果 ===" -ForegroundColor Cyan
Write-Host "通过: $($passes.Count)    失败: $($failures.Count)" -ForegroundColor $(if ($failures.Count -eq 0) { 'Green' } else { 'Red' })

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "失败项：" -ForegroundColor Red
    $i = 1
    foreach ($f in $failures) {
        Write-Host "  $i. $f" -ForegroundColor Red
        $i++
    }
    Write-Host ""
    Write-Host "这些锁定是为了保证在 Windows 7 SP1 上可用。修改前请阅读 ARCHITECTURE_STANDARD.md。" -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "全部锁定项校验通过。" -ForegroundColor Green
exit 0
