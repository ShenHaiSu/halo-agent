<#
.SYNOPSIS
  Pxx 前缀门禁（B0）：对 src/tests/scripts/contract/build 执行 00 口径两正则，命中即非零退出。
.DESCRIPTION
  文件名正则（大小写不敏感）：p[0-9]{1,2}[-_.]
  内容正则（大小写敏感）：P[0-9]{1,2}[-_][\w + CJK]
  排除：bin/obj/node_modules/dist/artifacts/publish/wwwroot/.git
  放行（冻结归档引用 + 一版兼容）：
    1) `docs/Pxx…` / `docs\Pxx…` 历史归档路径引用（docs/P* 冻结不动，见 10-cleanup-plan §2/R7）；
    2) `HALO_P24_CANCEL_AUTH` 旧环境变量名，仅限 src/Engine.Core/Agent/AgentSession.cs 兼容回退行。
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-no-pxx-prefix.ps1
#>
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dirs = @('src','tests','scripts','contract','build')
$excludeDir = @('bin','obj','node_modules','dist','artifacts','publish','wwwroot','.git')
$fileNameRx = [regex]'p[0-9]{1,2}[-_.]'
$contentRx = [regex]'P[0-9]{1,2}[-_][\w\u4e00-\u9fa5]+'
$fail = 0

function Test-Excluded($full) {
  foreach ($d in $excludeDir) { if ($full -match "[\\/]$d([\\/]|$)") { return $true } }
  return $false
}

# 1) 文件名
foreach ($d in $dirs) {
  $base = Join-Path $root $d
  if (-not (Test-Path $base)) { continue }
  Get-ChildItem $base -Recurse -File -Force | ForEach-Object {
    if (Test-Excluded $_.FullName) { return }
    if ($fileNameRx.IsMatch($_.Name)) { Write-Host "FILENAME: $($_.FullName.Substring($root.Length+1))" -ForegroundColor Red; $script:fail = 1 }
  }
}

# 2) 内容
$exts = @('.cs','.ts','.vue','.ps1','.iss','.md','.json','.csproj','.html','.mjs','.js','.css','.xml','.props','.targets','.sln','.psm1')
foreach ($d in $dirs) {
  $base = Join-Path $root $d
  if (-not (Test-Path $base)) { continue }
  Get-ChildItem $base -Recurse -File -Force | ForEach-Object {
    if (Test-Excluded $_.FullName) { return }
    if ($_.Name -eq 'check-no-pxx-prefix.ps1') { return }
    if ($exts -notcontains $_.Extension.ToLower()) { return }
    $rel = $_.FullName.Substring($root.Length+1)
    $i = 0
    foreach ($line in [System.IO.File]::ReadLines($_.FullName)) {
      $i++
      if (-not $contentRx.IsMatch($line)) { continue }
      # 放行1：冻结归档路径引用
      if ($line -match 'docs[/\\]P[0-9]') { continue }
      # 放行2：旧 env 兼容回退（仅 AgentSession.cs）
      if ($rel -eq 'src\Engine.Core\Agent\AgentSession.cs' -and $line -match 'HALO_P24_CANCEL_AUTH') { continue }
      Write-Host "$rel`:$i`: $line" -ForegroundColor Red
      $script:fail = 1
    }
  }
}

if ($fail -eq 0) { Write-Host 'check-no-pxx-prefix: PASS (zero hits)' -ForegroundColor Green } else { Write-Host 'check-no-pxx-prefix: FAIL' -ForegroundColor Red }
exit $fail
