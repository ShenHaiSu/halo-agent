# scripts/p5-verify.ps1
<#
.SYNOPSIS
  P5 一键验收。按依赖顺序执行，任何一步失败立即停止。
.OUTPUTS
  退出码 0 = 自动化项全过；非 0 = 第 N 步失败。
  全部输出写入 docs/evidence/P5/。
.NOTES
  不含"浏览器内"与"人工"项；那些见 docs/P5-前端交互/07-验收方法.md §3。
#>
param(
  [switch]$SkipCounterproof,
  [switch]$WithCoverage
)
$ErrorActionPreference = 'Stop'

$WebRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\src\Web.Client')).Path
$Evidence = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'docs\evidence\P5'
New-Item -ItemType Directory -Force $Evidence | Out-Null

$script:Steps = @()
function Step([string]$Name, [string]$File, [scriptblock]$Cmd) {
  $script:Steps += @{ Name = $Name; File = $File }
  $n = $script:Steps.Count
  Write-Host ''
  Write-Host "──── [$n] $Name ────" -ForegroundColor Cyan
  $sw = [Diagnostics.Stopwatch]::StartNew()
  Push-Location $WebRoot
  try {
    & $Cmd 2>&1 | Tee-Object (Join-Path $Evidence $File)
    $code = $LASTEXITCODE
  } finally { Pop-Location }
  $sw.Stop()
  Write-Host ("耗时 {0:N1}s  退出码 {1}" -f $sw.Elapsed.TotalSeconds, $code) -ForegroundColor Gray
  if ($code -ne 0) {
    Write-Host "✗ 第 $n 步失败：$Name （详见 $File）" -ForegroundColor Red
    exit $n
  }
}

if (-not $SkipCounterproof) {
  Step '反证：5 条红线 + 3 个检查脚本' 'A-0-counterproof-run.txt' {
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'p5-counterproof.ps1')
  }
}

Step '依赖与版本基线' 'A-0-baseline.txt' {
  pnpm install --frozen-lockfile
  pnpm list --depth 0
}

Step '类型检查（vue-tsc）' 'A-0-typecheck.txt' { pnpm typecheck }

Step 'Lint（eslint --max-warnings 0）' 'A-0-lint.txt' { pnpm lint }

Step '格式检查（prettier）' 'B-prettier.txt' {
  pnpm exec prettier --check "src/**/*.{ts,vue,css,json}" "scripts/*.mjs"
}

if ($WithCoverage) {
  Step '单测 + 覆盖率' 'B-test-coverage.txt' { pnpm test:coverage }
} else {
  Step '单测' 'B-test-output.txt' { pnpm test }
}

Step 'check:pins'     'A-0-check-pins.txt'     { pnpm check:pins }
Step 'check:colors'   'A-0-check-colors.txt'   { pnpm check:colors }
Step 'check:icons'    'A-0-check-icons.txt'    { pnpm check:icons }
Step 'check:tokens'   'A-0-check-tokens.txt'   { pnpm check:tokens }
Step 'check:layering' 'A-0-check-layering.txt' { pnpm check:layering }

Step 'R-1 零 v-html/innerHTML' 'A-1-no-vhtml.txt' {
  $hits = Select-String -Path (Join-Path $WebRoot 'src\**\*.vue'), (Join-Path $WebRoot 'src\**\*.ts') `
    -Pattern 'v-html','innerHTML','insertAdjacentHTML','outerHTML' -ErrorAction SilentlyContinue
  if ($hits) {
    $hits | Where-Object { $_.Line -notmatch '@allow-innerhtml' } |
      ForEach-Object { "命中 $($_.Path):$($_.LineNumber)  $($_.Line.Trim())" }
    if (($hits | Where-Object { $_.Line -notmatch '@allow-innerhtml' }).Count -gt 0) { exit 1 }
  }
  '✓ 零 v-html / innerHTML'
}

Step 'R-5 零 confirm/alert/prompt' 'A-0-no-native-dialog.txt' {
  $hits = Select-String -Path (Join-Path $WebRoot 'src\**\*.vue'), (Join-Path $WebRoot 'src\**\*.ts') `
    -Pattern '\bwindow\.confirm\s*\(','\bwindow\.alert\s*\(','\bwindow\.prompt\s*\(' -ErrorAction SilentlyContinue
  $real = $hits | Where-Object { $_.Line -notmatch '^\s*(//|\*)' -and $_.Line -notmatch '@allow-native-dialog' }
  if ($real) { $real | ForEach-Object { "命中 $($_.Path):$($_.LineNumber)  $($_.Line.Trim())" }; exit 1 }
  '✓ 零原生弹窗'
}

Step 'R-3 无 keyCode（IME 坑 6）' 'A-3-no-keycode.txt' {
  $hits = Select-String -Path (Join-Path $WebRoot 'src\**\*.vue'), (Join-Path $WebRoot 'src\**\*.ts') `
    -Pattern '\.keyCode' -ErrorAction SilentlyContinue
  if ($hits) { $hits | ForEach-Object { "命中 $($_.Path):$($_.LineNumber)" }; exit 1 }
  '✓ 无 keyCode'
}

Step 'R-2：24 个事件 case' 'A-2-events-24.txt' {
  $f = Join-Path $WebRoot 'src\stores\eventBridge.ts'
  if (-not (Test-Path $f)) { "✗ 找不到 eventBridge.ts"; exit 1 }
  $txt = Get-Content -Raw $f

  $expected = @(
    'turn.started','turn.finished','text.delta','reasoning.delta',
    'tool.started','tool.finished','tool.outputDelta','tool.silentAlert',
    'round.started','llm.retrying','usage','notice',
    'ask.permission','ask.rounds',
    'session.info','session.historyLoaded','session.contextCleared',
    'session.compactionStarted','session.compacted','user.injected',
    'state.personaChanged','state.modeChanged','state.busyChanged',
    'resync.required'
  )
  "期望事件数 = $($expected.Count)"

  $missing = @()
  foreach ($e in $expected) {
    if ($txt -notmatch [regex]::Escape("case '$e'")) { $missing += $e }
  }
  if ($missing.Count -gt 0) {
    "✗ 缺少 case：`n$($missing -join "`n")"
    exit 1
  }
  "✓ 24 个 case 齐全"

  if ($txt -notmatch "default:[\s\S]{0,200}console\.debug") {
    "✗ default 分支没有 console.debug（违反 I-1 铁律）"
    exit 1
  }
  '✓ default 有 console.debug'

  $switchStart = $txt.IndexOf('switch (f.k)')
  $switchEnd = $txt.IndexOf('setLastSeq', $switchStart)
  if ($switchStart -lt 0 -or $switchEnd -lt 0) { "✗ 找不到 switch 或 setLastSeq"; exit 1 }
  $between = $txt.Substring($switchStart, $switchEnd - $switchStart)
  $open = ([regex]::Matches($between, '\{')).Count
  $close = ([regex]::Matches($between, '\}')).Count
  if ($open -ne $close) { "✗ setLastSeq 在 switch 块内（必须挪到外面）"; exit 1 }
  '✓ setLastSeq 在 switch 之外'
}

Step 'A-10.2：契约方法名白名单' 'A-10-contract.txt' {
  $contract = @(
    'system.ping','system.info','system.protocol','system.shutdown','system.diagnostics',
    'agent.prompt','agent.queue','agent.unqueue','agent.cancel','agent.answer','agent.drain','agent.resync',
    'session.new','session.resume','session.list','session.rename','session.delete',
    'session.clear','session.compact','session.setCwd',
    'state.snapshot','state.setMode','state.setPersona','state.setProvider',
    'state.setMaxTurns','state.setRetry',
    'settings.get','settings.saveModel','settings.saveNetwork','settings.probeModels',
    'settings.saveExtraHeaders','settings.mcp.get','settings.mcp.save',
    'asset.skills.list','asset.skills.get','asset.skills.save','asset.skills.delete','asset.skills.run',
    'asset.personas.list','asset.personas.get','asset.personas.save',
    'asset.memory.list','asset.memory.get','asset.memory.save','asset.memory.delete',
    'tool.list','tool.undoList','tool.undoRollback','tool.mcpStatus',
    'fs.listDir','fs.completePaths','fs.validatePath',
    'update.check','update.install'
  )
  "白名单 = $($contract.Count) 个方法"

  $files = Get-ChildItem -Recurse -File (Join-Path $WebRoot 'src') -Include *.ts,*.vue
  $used = @{}
  foreach ($f in $files) {
    $t = Get-Content -Raw $f.FullName
    foreach ($mm in [regex]::Matches($t, "transport\.request(?:<[^>]*>)?\(\s*'([a-zA-Z]+\.[a-zA-Z]+)'")) {
      $used[$mm.Groups[1].Value] = $true
    }
    foreach ($mm in [regex]::Matches($t, 'transport\.request(?:<[^>]*>)?\(\s*"([a-zA-Z]+\.[a-zA-Z]+)"')) {
      $used[$mm.Groups[1].Value] = $true
    }
  }

  "实际使用 = $($used.Count) 个"
  $bad = @()
  foreach ($k in $used.Keys) { if ($contract -notcontains $k) { $bad += $k } }
  if ($bad.Count -gt 0) {
    "✗ 使用了契约外方法：`n$($bad -join "`n")"
    exit 1
  }
  "✓ 全部方法名在契约内"
  "使用的：`n$(($used.Keys | Sort-Object) -join "`n")"
}

Step 'A-10.1 产物体积' 'A-10-size.txt' {
  pnpm build:only
  $out = Join-Path $WebRoot '..\Host.Desktop\wwwroot\assets'
  if (-not (Test-Path $out)) { "✗ wwwroot\assets 不存在"; exit 1 }
  $js = Get-ChildItem "$out\*.js" -ErrorAction SilentlyContinue
  $css = Get-ChildItem "$out\*.css" -ErrorAction SilentlyContinue
  $jsSum = if ($js) { ($js | Measure-Object Length -Sum).Sum } else { 0 }
  $cssSum = if ($css) { ($css | Measure-Object Length -Sum).Sum } else { 0 }

  "JS:  $([math]::Round($jsSum/1KB,1)) KB（$($js.Count) 个文件）"
  "CSS: $([math]::Round($cssSum/1KB,1)) KB（$($css.Count) 个文件）"

  $limitKB = 800
  $jsKB = [math]::Round($jsSum/1KB,1)
  if ($jsKB -gt $limitKB) { "✗ JS 超过 $limitKB KB（实际 $jsKB KB）"; exit 1 }
  "✓ JS 体积 $jsKB KB ≤ $limitKB KB"
}

Step 'A-10.3 无禁用大库' 'A-10-no-heavy-libs.txt' {
  $pkg = Get-Content -Raw (Join-Path $WebRoot 'package.json') | ConvertFrom-Json
  $all = @{}
  if ($pkg.dependencies) { $pkg.dependencies.PSObject.Properties | ForEach-Object { $all[$_.Name] = $_.Value } }
  if ($pkg.devDependencies) { $pkg.devDependencies.PSObject.Properties | ForEach-Object { $all[$_.Name] = $_.Value } }

  $forbid = @('highlight.js','shiki','prismjs','moment','dayjs','lodash','marked','markdown-it','dompurify','katex','monaco-editor')
  $bad = @()
  foreach ($ff in $forbid) { if ($all.ContainsKey($ff)) { $bad += "$ff = $($all[$ff])" } }
  if ($bad.Count -gt 0) {
    "✗ 引入了被禁用的库：`n$($bad -join "`n")"
    exit 1
  }
  '✓ 无禁用大库'
}

Step 'A-1.1 Markdown 用例' 'A-1-markdown-test.txt' {
  pnpm test -- MarkdownRenderer --reporter=verbose
}
Step 'A-1.4 高亮测试' 'A-1-highlight-test.txt' {
  pnpm test -- highlight --reporter=verbose
}
Step 'A-5.1 虚拟滚动测试' 'A-5-virtualize-test.txt' {
  pnpm test -- virtualize --reporter=verbose
}
Step 'A-8.1 检索测试' 'A-8-fuzzy-test.txt' {
  pnpm test -- fuzzy --reporter=verbose
}
Step 'A-3.1 IME 组合态测试' 'A-3-ime-test.txt' {
  pnpm test -- useComposition --reporter=verbose
}
Step 'A-6.2 校验测试' 'A-6-validators-test.txt' {
  pnpm test -- validators --reporter=verbose
}

Write-Host ''
Write-Host '════════════════════════════════════' -ForegroundColor Green
Write-Host '✓ P5 自动化项全部通过' -ForegroundColor Green
Write-Host "证据目录：$Evidence" -ForegroundColor Gray
Write-Host '════════════════════════════════════' -ForegroundColor Green
Write-Host ''
Write-Host '下一步（必须人工执行，见 07-验收方法 §3 的浏览器内部分）：' -ForegroundColor Yellow
Write-Host '  1) pnpm dev  → 浏览器打开 http://localhost:5173/?diag=1' -ForegroundColor Yellow
Write-Host '  2) Console 执行 __p5diag.runAll()' -ForegroundColor Yellow
Write-Host '  3) 把输出粘到 docs/evidence/P5/A-browser-diag.txt' -ForegroundColor Yellow
Write-Host '  4) 按 §3 的 38 个人工项逐项勾选' -ForegroundColor Yellow
exit 0
