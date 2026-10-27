<#
.SYNOPSIS
  P0 产物构建：一键产出可直接双击运行的发行目录 dist\<win-x86|win-x64>\。

.DESCRIPTION
  顺序：
    [1/4] 生成契约类型（前端依赖它）
    [2/4] 构建前端到 Host.Desktop/wwwroot
    [3/4] 自包含发布宿主 + 引擎到 dist\<rid>\
    [4/4] 版本锁校验 + 输出运行指引

  产物可直接拷到任意 Win7 SP1 / Win10 / Win11 机器运行（x86 包通吃 32/64 位）。

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File build-dist.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File build-dist.ps1 -Platform x64
  powershell -NoProfile -ExecutionPolicy Bypass -File build-dist.ps1 -SkipFrontend
#>

[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64')]
    [string]$Platform = 'x86',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipFrontend
)

$ErrorActionPreference = 'Stop'
# 注意：PS 5.1 用 -File 启动时 param 默认值里 $PSScriptRoot 为空，只能在正文里推导
$repoRoot = if ($PSScriptRoot) { (Resolve-Path (Join-Path $PSScriptRoot '.')).Path } else { (Get-Location).Path }
Push-Location $repoRoot
try {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $rid = if ($Platform -eq 'x86') { 'win-x86' } else { 'win-x64' }
    $outDir = Join-Path $repoRoot (Join-Path 'dist' $rid)

    Write-Host "=== HaloAgent 发行构建 ($Configuration / $Platform) ===" -ForegroundColor Cyan
    Write-Host "输出目录: $outDir"

    # 0) 清理旧产物（保证 dist 里是没有历史残留的干净包）
    if (Test-Path $outDir) {
        Write-Host ""
        Write-Host "[0/4] 清理旧产物" -ForegroundColor Cyan
        Remove-Item -Recurse -Force $outDir
    }
    New-Item -ItemType Directory -Force $outDir | Out-Null

    # 1) 契约类型生成
    Write-Host ""
    Write-Host "[1/4] 生成契约类型" -ForegroundColor Cyan
    if (Get-Command node -ErrorAction SilentlyContinue) {
        node scripts/gen-contract-types.mjs
    } else {
        Write-Host "  跳过：未安装 node" -ForegroundColor Yellow
    }

    # 2) 前端
    if (-not $SkipFrontend) {
        Write-Host ""
        Write-Host "[2/4] 构建前端" -ForegroundColor Cyan
        Push-Location src/Web.Client
        try {
            if (-not (Test-Path 'node_modules')) {
                if (Test-Path 'pnpm-lock.yaml') {
                    Write-Host "  pnpm install --frozen-lockfile..." -ForegroundColor DarkGray
                    pnpm install --frozen-lockfile
                } else {
                    Write-Host "  无 lockfile，首次安装并生成..." -ForegroundColor DarkGray
                    pnpm install
                }
            }
            pnpm build
        } finally {
            Pop-Location
        }
    } else {
        Write-Host ""
        Write-Host "[2/4] 跳过前端（-SkipFrontend，将复用已有的 wwwroot）" -ForegroundColor Yellow
    }

    # 3) 自包含发布（宿主 + 引擎进同一个目录）
    Write-Host ""
    Write-Host "[3/4] 发布宿主与引擎（self-contained，$rid）" -ForegroundColor Cyan
    Write-Host "  注：CEF 体积大，这一步需要几分钟，请耐心等待..." -ForegroundColor DarkGray
    dotnet publish src/Host.Desktop/Host.Desktop.csproj `
        -c $Configuration -r $rid --self-contained true `
        -p:PublishSingleFile=false -p:PlatformTarget=$Platform `
        -o $outDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "宿主发布失败（退出码 $LASTEXITCODE）" }

    dotnet publish src/Engine.Server/Engine.Server.csproj `
        -c $Configuration -r $rid --self-contained true `
        -p:PublishSingleFile=false -p:PlatformTarget=$Platform `
        -o $outDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "引擎发布失败（退出码 $LASTEXITCODE）" }

    # 前端产物必须跟着宿主走（app://local/index.html 就靠它）
    $indexHtml = Join-Path $outDir 'wwwroot/index.html'
    if (-not (Test-Path $indexHtml)) {
        throw "发布目录缺少 wwwroot/index.html：请确认前端已构建（不要加 -SkipFrontend）"
    }

    # 4) 锁校验
    Write-Host ""
    Write-Host "[4/4] 锁校验" -ForegroundColor Cyan
    & (Join-Path $repoRoot 'scripts/verify-locks.ps1')
    if ($LASTEXITCODE -ne 0) { throw "锁校验失败" }

    $sw.Stop()
    $size = [math]::Round(((Get-ChildItem $outDir -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)
    Write-Host ""
    Write-Host "=== 完成，用时 $([math]::Round($sw.Elapsed.TotalSeconds, 1))s，产物约 ${size}MB ===" -ForegroundColor Green
    Write-Host ""
    Write-Host "运行测试：" -ForegroundColor Cyan
    Write-Host "  1) 双击启动（看窗口 + 页面）："
    Write-Host "     $outDir\HaloAgent.exe"
    Write-Host "  2) 环境诊断："
    Write-Host "     $outDir\HaloAgent.exe --diagnostics"
    Write-Host "     $outDir\HaloAgent.Engine.exe --diagnostics"
    Write-Host "  3) 引擎直连（无 UI）："
    Write-Host "     echo {`"t`":`"req`",`"id`":1,`"m`":`"system.ping`"} | $outDir\HaloAgent.Engine.exe --stdio"
    Write-Host "  4) 页面点"ping 引擎"：1 秒内应显示 pong + 耗时"
}
finally {
    Pop-Location
}
