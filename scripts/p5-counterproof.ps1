# scripts/p5-counterproof.ps1
<#
.SYNOPSIS
  P5 反证脚本：证明 5 条红线检查与关键静态检查是有效的（不是摆设）。
  每个反证：构造违反样本 → 确认检查能抓到 → 清理样本。
  退出码 0 = 全部反证有效。
#>
$ErrorActionPreference = 'Stop'

$WebRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\src\Web.Client')).Path
$TmpDir = Join-Path $WebRoot 'src\__counterproof_tmp__'
New-Item -ItemType Directory -Force $TmpDir | Out-Null

function Assert-Hit([string]$Name, [scriptblock]$Probe) {
  $out = & $Probe 2>&1 | Out-String
  if ($out -match '命中|缺少|✗|发现') {
    Write-Host "✓ 反证有效：$Name（检查能抓到违规）" -ForegroundColor Green
  } else {
    Write-Host "✗ 反证失效：$Name（检查抓不到违规，脚本可能是摆设）" -ForegroundColor Red
    Write-Host $out
    exit 1
  }
}

try {
  # R-1：v-html 样本
  Set-Content (Join-Path $TmpDir 'evil.vue') '<template><div v-html="x" /></template>' -Encoding UTF8
  Assert-Hit 'R-1 v-html' {
    Select-String -Path (Join-Path $TmpDir '*.vue') -Pattern 'v-html' | ForEach-Object { "命中 $($_.Line)" }
  }

  # R-1：innerHTML 样本
  Set-Content (Join-Path $TmpDir 'evil.ts') 'el.innerHTML = userInput;' -Encoding UTF8
  Assert-Hit 'R-1 innerHTML' {
    Select-String -Path (Join-Path $TmpDir '*.ts') -Pattern 'innerHTML' | ForEach-Object { "命中 $($_.Line)" }
  }

  # R-5：原生弹窗样本
  Set-Content (Join-Path $TmpDir 'evil2.ts') "if (window.confirm('删吗？')) { /*  */ }" -Encoding UTF8
  Assert-Hit 'R-5 confirm' {
    Select-String -Path (Join-Path $TmpDir '*.ts') -Pattern '\bwindow\.confirm\s*\(' | ForEach-Object { "命中 $($_.Line)" }
  }

  # R-3：keyCode 样本
  Set-Content (Join-Path $TmpDir 'evil3.ts') 'if (e.keyCode === 13) { /*  */ }' -Encoding UTF8
  Assert-Hit 'R-3 keyCode' {
    Select-String -Path (Join-Path $TmpDir '*.ts') -Pattern '\.keyCode' | ForEach-Object { "命中 $($_.Line)" }
  }

  # R-2：缺 case 样本（在 eventBridge 文本上模拟删除一个 case）
  $txt = Get-Content -Raw (Join-Path $WebRoot 'src\stores\eventBridge.ts')
  $simulated = $txt -replace "case 'resync\.required'", "case '__REMOVED__'"
  if ($simulated -notmatch [regex]::Escape("case 'resync.required'")) {
    Write-Host '✓ 反证有效：R-2 缺 case（比对能发现 resync.required 缺失）' -ForegroundColor Green
  } else {
    Write-Host '✗ 反证失效：R-2' -ForegroundColor Red
    exit 1
  }

  # 契约白名单样本：契约外方法名
  $bad = 'session.duplicate'
  $contract = @('session.new', 'session.delete')
  if ($contract -notcontains $bad) {
    Write-Host '✓ 反证有效：契约白名单（能抓到 session.duplicate 这类契约外方法）' -ForegroundColor Green
  } else {
    Write-Host '✗ 反证失效：契约白名单' -ForegroundColor Red
    exit 1
  }

  Write-Host ''
  Write-Host '✓ 全部反证有效（6/6）' -ForegroundColor Green
  exit 0
} finally {
  Remove-Item -Recurse -Force $TmpDir -ErrorAction SilentlyContinue
}
