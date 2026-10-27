<#
.SYNOPSIS
  P3 宿主与 CefSharp 验收总入口（fail fast），证据落到 docs/evidence/P3/。
  覆盖全部可自动化项；人工项（窗口/托盘/真机）按 06-验收方法.md 逐条执行。
#>
[CmdletBinding()] param([switch]$SkipBuild, [string]$Only = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evid = Join-Path $root 'docs/evidence/P3'
Set-Location $root
New-Item -ItemType Directory -Force $evid | Out-Null
if (-not $env:HALO_DATA_DIR) { $env:HALO_DATA_DIR = Join-Path $env:TEMP 'haloagent-p3-verify' }
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
  Step 'A-01a' 'Release 构建（零警告）' { dotnet build HaloAgent.sln -c Release --nologo -warnaserror 2>&1 | Tee-Object (Join-Path $evid 'A-01a-build.txt'); if ($LASTEXITCODE -ne 0) { throw 'build failed' } }
}
Step 'A-01b' '依赖纯净' { dotnet list package --include-transitive 2>&1 | Tee-Object (Join-Path $evid 'A-01b-packages.txt'); if ($LASTEXITCODE -ne 0) { throw 'list failed' } }
Step 'A-1.4' 'HostOptions 命令行' { TestLog 'tests/Host.Tests' 'FullyQualifiedName~HostOptions' 'A-1.4-cli.txt' }
Step 'A-2.1' 'GPU 判定矩阵' { TestLog 'tests/Host.Tests' 'FullyQualifiedName~GpuPolicy' 'A-2.1-gpu-matrix.txt' }
Step 'A-2.2' '驱动黑名单覆盖' {
  $src = Get-Content src\Host.Desktop\Gpu\GpuPolicy.cs -Raw
  $out = Join-Path $evid 'A-2.2-blacklist.txt'
  '' | Out-File $out
  $fail = $false
  foreach ($k in @('ig4icd', 'igd10um', 'nvwgf2um', 'nv4_disp', 'aticfx', 'vmware', 'virtualbox', 'qxl', 'cirrus')) {
    $hit = $src -match [regex]::Escape($k)
    "$($(if ($hit) { 'OK  ' } else { 'MISS' })) $k" | Tee-Object -FilePath $out -Append
    if (-not $hit) { $fail = $true }
  }
  if ($fail) { throw 'blacklist missing keywords' }
}
Step 'A-3.4' 'MIME 表' { TestLog 'tests/Host.Tests' 'FullyQualifiedName~Mime|FullyQualifiedName~Js_mime|FullyQualifiedName~Text_mimes' 'A-3.4-mime.txt' }
Step 'A-9.3' 'Router 与 schema 一致' {
  $out = Join-Path $evid 'A-9.3-router.txt'
  '' | Out-File $out
  $schema = Get-Content contract\agent.schema.json -Raw -Encoding utf8 | ConvertFrom-Json
  $schemaHost = $schema.methods.PSObject.Properties |
    Where-Object { $_.Value.implementedIn -eq 'host' } |
    Select-Object -ExpandProperty Name | Sort-Object
  "schema 中 implementedIn=host 的方法：$($schemaHost -join ', ')" | Tee-Object -FilePath $out -Append
  $code = Get-Content src\Host.Desktop\Bridge\HostMethodRouter.cs -Raw
  $codeHost = [regex]::Matches($code, '"([a-z]+\.[a-zA-Z0-9]+)"') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
  "代码中 HostMethodRouter 的方法：$($codeHost -join ', ')" | Tee-Object -FilePath $out -Append
  $missing = @($schemaHost | Where-Object { $codeHost -notcontains $_ })
  $extra = @($codeHost | Where-Object { $schemaHost -notcontains $_ })
  if ($missing) { "FAIL 代码缺少：$($missing -join ', ')" | Tee-Object -FilePath $out -Append; throw 'router mismatch' }
  if ($extra) { "FAIL 代码多余：$($extra -join ', ')" | Tee-Object -FilePath $out -Append; throw 'router mismatch' }
  'OK   两侧集合一致' | Tee-Object -FilePath $out -Append
  TestLog 'tests/Host.Tests' 'FullyQualifiedName~Router' 'A-9.3-router-tests.txt'
}
Step 'A-10.4' '无 file:// 生产入口' {
  $out = Join-Path $evid 'A-10.4-no-file-protocol.txt'
  '' | Out-File $out
  $fail = $false
  $hits = Select-String -Path src\Host.Desktop\**\*.cs -Pattern 'file://' -AllMatches -ErrorAction SilentlyContinue
  if (-not $hits) {
    'OK   C# 代码中无 file:// 引用' | Tee-Object -FilePath $out -Append
  } else {
    foreach ($h in $hits) {
      $isComment = $h.Line.TrimStart().StartsWith('//')
      $level = if ($isComment) { 'INFO' } else { 'FAIL' }
      if (-not $isComment) { $fail = $true }
      "$level $($h.Filename):$($h.LineNumber) $($h.Line.Trim())" | Tee-Object -FilePath $out -Append
    }
  }
  $prod = Select-String -Path src\Host.Desktop\Browser\BrowserBootstrap.cs -Pattern "app://local/index.html"
  if ($prod) { 'OK   生产态入口为 app://local/index.html' | Tee-Object -FilePath $out -Append }
  else { 'FAIL 生产态入口缺失' | Tee-Object -FilePath $out -Append; $fail = $true }
  if ($fail) { throw 'file:// found in prod code' }
}
Step 'A-2.4' 'rasterizer 默认不加' {
  $out = Join-Path $evid 'A-2.4-no-rasterizer.txt'
  $code = Get-Content src\Host.Desktop\Gpu\GpuPolicy.cs -Raw
  $active = [regex]::Matches($code, '^\s*AddIfAbsent\(settings,\s*"disable-software-rasterizer"\)', 'Multiline') |
    Where-Object { $_.Value.TrimStart() -notlike '//*' }
  # 注释行不算；正文出现即失败
  $lines = Get-Content src\Host.Desktop\Gpu\GpuPolicy.cs
  $bad = @($lines | Where-Object { $_ -match 'AddIfAbsent\(settings,\s*"disable-software-rasterizer"\)' -and $_.TrimStart() -notlike '//*' })
  if ($bad.Count -eq 0) { 'OK   默认不加 disable-software-rasterizer（仅注释占位）' | Tee-Object $out }
  else { "FAIL 发现生效行：$($bad -join '; ')" | Tee-Object $out; throw 'rasterizer enabled' }
}
Step 'B' 'B 级静态检查' {
  $out = Join-Path $evid 'B-checks.txt'
  '' | Out-File $out
  function Log($s) { $s | Tee-Object -FilePath $out -Append }
  $av = @(Select-String -Path src\Host.Desktop\**\*.cs -Pattern 'async\s+void' -ErrorAction SilentlyContinue)
  if ($av.Count -eq 0) { Log 'OK   B-02 无 async void' } else { $av | ForEach-Object { Log "FAIL B-02 $($_.Filename):$($_.LineNumber)" }; throw 'B-02' }
  $mf = (Get-Content src\Host.Desktop\Shell\MainForm.cs -ErrorAction SilentlyContinue)
  if ($null -eq $mf) { $mf = Get-Content src\Host.Desktop\MainForm.cs }
  Log "B-14 MainForm = $($mf.Count) 行（目标 ≤500，超限记技术债不阻断）"
  $cf = (Get-Content src\Host.Desktop\Shell\ChromeForm.cs).Count
  Log "B-15 ChromeForm = $cf 行（应 ≤300）"
  if ($cf -gt 300) { throw 'B-15' }
  $manifest = Get-Content src\Host.Desktop\app.manifest -Raw
  $osCount = ([regex]::Matches($manifest, 'supportedOS Id=')).Count
  Log "B-19 supportedOS 数 = $osCount（应 4）"
  if ($osCount -ne 4) { throw 'B-19' }
  $hard = @(Select-String -Path src\Host.Desktop\**\*.cs -Pattern 'C:\\[a-zA-Z]' -ErrorAction SilentlyContinue |
    Where-Object { -not $_.Filename.Contains('Tests') })
  # 测试与注释里的路径不算
  $hardReal = @($hard | Where-Object { $_.Line.TrimStart() -notlike '//*' })
  Log "B-18 硬编码绝对路径 $($hardReal.Count) 处（应 0）"
  if ($hardReal.Count -gt 0) { $hardReal | Select-Object -First 5 | ForEach-Object { Log "FAIL B-18 $($_.Filename):$($_.LineNumber)" }; throw 'B-18' }
  $pinv = @(Select-String -Path src\Host.Desktop\**\*.cs -Pattern '\[DllImport' -ErrorAction SilentlyContinue)
  $pinv | Group-Object Filename | ForEach-Object { Log "INFO B-05 $($_.Name)：$($_.Count) 个 DllImport" }
  Get-Content $out | Write-Host
}
Step 'A-16' '契约回归（Contract+Host）' { TestLog 'tests/Contract.Tests' 'FullyQualifiedName~Contract|FullyQualifiedName~Schema' 'A-16-contract.txt' }
Write-Host "`n全部通过。证据见 docs/evidence/P3/" -ForegroundColor Green
