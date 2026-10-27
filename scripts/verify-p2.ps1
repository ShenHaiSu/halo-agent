<#
.SYNOPSIS
  P2 引擎内核验收总入口（fail fast），证据落到 docs/evidence/P2/。
#>
[CmdletBinding()] param([switch]$SkipBuild, [string]$Only = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evid = Join-Path $root 'docs/evidence/P2'
Set-Location $root
New-Item -ItemType Directory -Force $evid | Out-Null
if (-not $env:HALO_DATA_DIR) { $env:HALO_DATA_DIR = Join-Path $env:TEMP 'haloagent-p2-verify' }
Remove-Item -Recurse -Force $env:HALO_DATA_DIR -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $env:HALO_DATA_DIR | Out-Null
$env:HALO_TEST_NO_NETWORK = '1'
function Step([string]$Id, [string]$Title, [scriptblock]$Body) {
  if ($Only -and $Id -notlike "*$Only*") { return }
  Write-Host "`n=== $Id $Title ===" -ForegroundColor Cyan
  & $Body
  if ($LASTEXITCODE -ne 0) { throw "FAILED: $Id" }
  Write-Host '  PASS' -ForegroundColor Green
}
function TestLog([string]$Project, [string]$Filter, [string]$Log) {
  $out = & dotnet test $Project -c Release --nologo --filter $Filter 2>&1
  $out | Out-File -Encoding utf8 (Join-Path $evid $Log)
  if ($LASTEXITCODE -ne 0) { $out | Select-Object -Last 30 | Write-Host; throw "dotnet test failed: $Project $Filter" }
}
if (-not $SkipBuild) {
  Step 'A-01a' 'Release 构建' { dotnet build HaloAgent.sln -c Release --nologo -warnaserror 2>&1 | Tee-Object (Join-Path $evid 'A-01-build.txt'); if ($LASTEXITCODE -ne 0) { throw 'build failed' } }
}
Step 'A-01b' '依赖纯净' { dotnet list package --include-transitive 2>&1 | Tee-Object (Join-Path $evid 'A-01-packages.txt'); if ($LASTEXITCODE -ne 0) { throw 'list failed' } }
Step 'A-02' '存储' { TestLog 'tests/Engine.Storage.Tests' 'FullyQualifiedName~Storage|FullyQualifiedName~Undo|FullyQualifiedName~Config|FullyQualifiedName~HistoryRepair|FullyQualifiedName~Paths' 'A-02-storage-tests.txt' }
Step 'A-03' '权限不漏判' { TestLog 'tests/Engine.Core.Tests' 'FullyQualifiedName~DangerousCommandInspector' 'A-03-permission-block.txt' }
Step 'A-05' '权限矩阵' { TestLog 'tests/Engine.Core.Tests' 'FullyQualifiedName~PermissionChecker' 'A-05-matrix.txt' }
Step 'A-07' '工具' { TestLog 'tests/Engine.Core.Tests' 'FullyQualifiedName~Tools|FullyQualifiedName~Glob|FullyQualifiedName~ToolArgument|FullyQualifiedName~TextUtil' 'A-07-tools.txt' }
Step 'A-08' 'agent循环' { TestLog 'tests/Engine.Core.Tests' 'FullyQualifiedName~Agent' 'A-08-agent-loop.txt' }
Step 'A-10' 'LLM容错' { TestLog 'tests/Engine.Core.Tests' 'FullyQualifiedName~Sse|FullyQualifiedName~ToolCallAccum|FullyQualifiedName~Retry|FullyQualifiedName~OpenAiCompatible' 'A-10-llm.txt' }
Step 'A-11' '压缩修复' { TestLog 'tests/Engine.Core.Tests' 'FullyQualifiedName~Compact|FullyQualifiedName~MessageSequence' 'A-11-compact.txt' }
Step 'A-16' 'P1无回归' { TestLog 'HaloAgent.sln' 'FullyQualifiedName~Contract|FullyQualifiedName~Channel|FullyQualifiedName~Frame' 'A-16-regression.txt' }
Write-Host "`n全部通过。证据见 docs/evidence/P2/" -ForegroundColor Green
