# 构建前端，输出到 Host.Desktop\wwwroot（供宿主生产态 app:// 加载）。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Push-Location src\Web.Client
try {
  if (-not (Test-Path node_modules)) { pnpm install }
  pnpm build
} finally {
  Pop-Location
}

# 校验产物
$wwwroot = 'src\Host.Desktop\wwwroot'
if (-not (Test-Path "$wwwroot\index.html")) {
  throw "构建失败：$wwwroot\index.html 不存在"
}

# 关键校验 1：产物必须不带任何 file:// 引用
$bad = Select-String -Path "$wwwroot\**\*" -Pattern 'file:///' -List -ErrorAction SilentlyContinue
if ($bad) { throw "产物中包含 file:// 引用" }

Write-Host "前端已构建到 $wwwroot" -ForegroundColor Green
Get-ChildItem $wwwroot -Recurse -File |
  Measure-Object -Property Length -Sum |
  ForEach-Object { Write-Host ("总大小: {0:N1} MB / {1} 个文件" -f ($_.Sum / 1MB), $_.Count) }

# P3 验收工具：诊断页 + 契约快照拷入 wwwroot（发布前移除）
Copy-Item scripts\p3-diag.html "$wwwroot\__diag.html" -Force
Copy-Item contract\agent.schema.json "$wwwroot\__contract.json" -Force
Write-Host "已拷入 __diag.html + __contract.json（仅验收用）" -ForegroundColor Green
