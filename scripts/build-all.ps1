<#
.SYNOPSIS
  一条命令完成全部构建：契约生成 → 前端 → 后端 → 拷贝 → 校验。

.DESCRIPTION
  顺序很重要：
    1. 生成契约类型（前端依赖它）
    2. 构建前端到 Host.Desktop/wwwroot
    3. 构建 C#（宿主会把 wwwroot 拷进输出目录）
    4. 跑锁校验

.EXAMPLE
  pwsh -File scripts/build-all.ps1
  pwsh -File scripts/build-all.ps1 -Platform x64 -Configuration Debug
#>

[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64')]
    [string]$Platform = 'x86',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipFrontend,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    Write-Host "=== HaloAgent 全量构建 ($Configuration / $Platform) ===" -ForegroundColor Cyan

    # 1) 契约类型生成
    Write-Host ""
    Write-Host "[1/5] 生成契约类型" -ForegroundColor Cyan
    if (Get-Command node -ErrorAction SilentlyContinue) {
        node scripts/gen-contract-types.mjs
    } else {
        Write-Host "  跳过：未安装 node" -ForegroundColor Yellow
    }

    # 2) 前端
    if (-not $SkipFrontend) {
        Write-Host ""
        Write-Host "[2/5] 构建前端" -ForegroundColor Cyan
        Push-Location src/Web.Client
        try {
            if (-not (Test-Path 'node_modules')) {
                if (Test-Path 'pnpm-lock.yaml') {
                    Write-Host "  pnpm install --frozen-lockfile..." -ForegroundColor DarkGray
                    pnpm install --frozen-lockfile
                } else {
                    # 首次克隆还没有 lockfile：生成一份（之后会入库，CI 走 frozen）
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
        Write-Host "[2/5] 跳过前端（-SkipFrontend）" -ForegroundColor Yellow
    }

    # 3) 后端 + 宿主
    Write-Host ""
    Write-Host "[3/5] 构建 C#" -ForegroundColor Cyan
    dotnet build HaloAgent.sln `
        -c $Configuration `
        -p:Platform=$Platform `
        -p:PlatformTarget=$Platform `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败（退出码 $LASTEXITCODE）" }

    # 4) 测试
    # 4) 测试（注意：故意不用 -p:Platform / --no-build）
    #   测试与产物架构解耦：测试跑在构建机的原生架构上（x64 机器上就是 x64
    #   testhost），不需要安装 x86 运行时；而产物架构由 [3/5] 的 Platform 决定。
    #   这与 05-验收方法.md §2.4 的命令（不带平台参数）一致。
    if (-not $SkipTests) {
        Write-Host ""
        Write-Host "[4/5] 运行测试" -ForegroundColor Cyan
        dotnet test HaloAgent.sln `
            -c $Configuration `
            --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet test 失败（退出码 $LASTEXITCODE）" }
    } else {
        Write-Host ""
        Write-Host "[4/5] 跳过测试（-SkipTests）" -ForegroundColor Yellow
    }

    # 5) 锁校验
    Write-Host ""
    Write-Host "[5/5] 锁校验" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'verify-locks.ps1') -RepoRoot $repoRoot
    if ($LASTEXITCODE -ne 0) { throw "锁校验失败" }

    $sw.Stop()
    Write-Host ""
    Write-Host "=== 完成，用时 $([math]::Round($sw.Elapsed.TotalSeconds, 1))s ===" -ForegroundColor Green
    Write-Host "宿主: src\Host.Desktop\bin\$Platform\$Configuration\net6.0-windows\HaloAgent.exe"
    Write-Host "引擎: src\Engine.Server\bin\$Platform\$Configuration\net6.0-windows\HaloAgent.Engine.exe"
}
finally {
    Pop-Location
}
