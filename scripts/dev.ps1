# 一条命令启动 P3 开发全套：Vite（后台）+ 宿主开发态。
# 改一行 Vue 代码，1 秒内在 exe 窗口里看到效果（HMR）。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 1) 装依赖（首次）
if (-not (Test-Path 'src\Web.Client\node_modules')) {
  Push-Location src\Web.Client; pnpm install; Pop-Location
}

# 2) 起 Vite（后台）
Start-Process pwsh -ArgumentList @(
  '-NoExit', '-Command', 'cd src\Web.Client; pnpm dev'
)

# 3) 等端口
Write-Host '等待 Vite dev server…' -ForegroundColor Cyan
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline) {
  try {
    Invoke-WebRequest 'http://localhost:5173' -UseBasicParsing -TimeoutSec 1 | Out-Null
    break
  } catch { Start-Sleep -Milliseconds 500 }
}

# 4) 起宿主（开发态；Debug 构建允许 --dev）
dotnet run --project src\Host.Desktop -c Debug -- --dev
