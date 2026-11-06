 <#
 .SYNOPSIS
   P0 产物构建：一键产出可直接双击运行的发行目录 dist\<win-x86|win-x64>\（P22 行为页已纳入）。
 
 .DESCRIPTION
   顺序：
     [1/4] 生成契约类型（前端依赖它；P22：跑两遍验证幂等 + check-contract-settings 三方一致）
     [2/4] 构建前端到 Host.Desktop/wwwroot（含行为页 BehaviorPage）
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

     # 1) 契约类型生成（P22：跑两遍验证幂等 + 三方一致）
     Write-Host ""
     Write-Host "[1/4] 生成契约类型" -ForegroundColor Cyan
     if (Get-Command node -ErrorAction SilentlyContinue) {
         node scripts/gen-contract-types.mjs
         node scripts/gen-contract-types.mjs
         node scripts/check-contract-settings.mjs
         if ($LASTEXITCODE -ne 0) { throw "契约一致性校验失败（check-contract-settings）" }
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
     Write-Host "[3/5] 发布宿主与引擎（self-contained，$rid）" -ForegroundColor Cyan
     Write-Host "  注：CEF 体积大，这一步需要几分钟，请耐心等待..." -ForegroundColor DarkGray
     # Win7 安全参数（docs/P22-bundle/11 §6.4/§6.6）：
     #  PublishTrimmed=false（防裁剪 CEF 相关）、PublishReadyToRun=false（R2R 与 CEF 原生件组合在 Win7 易挂）、
     #  DebugType=embedded（pdb 内嵌，不再落地）、SatelliteResourceLanguages=zh-Hans（减英文卫星资源）。
     $publishCommon = @('-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false',
         '-p:DebugType=embedded', '-p:SatelliteResourceLanguages=zh-Hans')
     dotnet publish src/Host.Desktop/Host.Desktop.csproj `
         -c $Configuration -r $rid --self-contained true `
         -p:PlatformTarget=$Platform @publishCommon `
         -o $outDir --nologo
     if ($LASTEXITCODE -ne 0) { throw "宿主发布失败（退出码 $LASTEXITCODE）" }
 
     dotnet publish src/Engine.Server/Engine.Server.csproj `
         -c $Configuration -r $rid --self-contained true `
         -p:PlatformTarget=$Platform @publishCommon `
         -o $outDir --nologo
     if ($LASTEXITCODE -ne 0) { throw "引擎发布失败（退出码 $LASTEXITCODE）" }
 
     # 铁律门禁：CefSharp 109 原生件要求多文件发布（对齐 develop/build/publish.ps1）
     if (-not (Test-Path (Join-Path $outDir 'libcef.dll'))) {
         throw "SINGLE-FILE SUSPECTED: libcef.dll missing in $outDir (PublishSingleFile must stay false)."
     }
 
     # 瘦身：locales 只留 zh-CN/zh-TW/en-US（省 ~30MB，见 11 §6.4）
     $localesDir = Join-Path $outDir 'locales'
     if (Test-Path $localesDir) {
         $keep = @('zh-CN.pak', 'zh-TW.pak', 'en-US.pak')
         Get-ChildItem $localesDir -Filter *.pak -File -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -notin $keep } |
             Remove-Item -Force -ErrorAction SilentlyContinue
     }

    # 前端产物必须跟着宿主走（app://local/index.html 就靠它）
    $indexHtml = Join-Path $outDir 'wwwroot/index.html'
    if (-not (Test-Path $indexHtml)) {
        throw "发布目录缺少 wwwroot/index.html：请确认前端已构建（不要加 -SkipFrontend）"
    }

     # 4) 锁校验 + 产物断言
     Write-Host ""
     Write-Host "[4/5] 锁校验 + 产物断言" -ForegroundColor Cyan
     & (Join-Path $repoRoot 'scripts/verify-locks.ps1')
     if ($LASTEXITCODE -ne 0) { throw "锁校验失败" }
     & (Join-Path $repoRoot 'scripts/verify-artifacts.ps1') -Platform $Platform
     if ($LASTEXITCODE -ne 0) { throw "产物断言失败（verify-artifacts）" }
 
     # 5) 打包安装器（ISCC；OutputDir 必须绝对路径，见 11 §1.2）
     Write-Host ""
     Write-Host "[5/5] 打包安装器（ISCC）" -ForegroundColor Cyan
     $isccCandidates = @(
         'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
         'C:\Program Files\Inno Setup 6\ISCC.exe',
         (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
     ) | Where-Object { $_ -and (Test-Path $_) }
     if (-not $isccCandidates) {
         Write-Host "  跳过：未找到 ISCC.exe（先分发 dist 目录即可，装机再补装 Inno Setup 6 后重跑）" -ForegroundColor Yellow
     } else {
         $iscc = @($isccCandidates)[0]
         $verXml = [xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))
         # 注意：props 里有多个 PropertyGroup，直接 .Version 会拿到数组，必须取第一个非空并 Trim
         $appVersion = ($verXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version.Trim()
         if ([string]::IsNullOrWhiteSpace($appVersion)) { $appVersion = '0.0.1' }
         $srcAbs = (Resolve-Path $outDir).Path
         $destAbs = Join-Path $repoRoot (Join-Path 'artifacts' (Join-Path $appVersion "win-$Platform"))
         New-Item -ItemType Directory -Force $destAbs | Out-Null
         # KB 代装开关：x86 包需双架构 MSU 齐套，x64 包只需 x64 MSU（MSU 在 build/vcredist/）。
         $kbDir = Join-Path $repoRoot (Join-Path 'build' 'vcredist')
         $kbNeeded = if ($Platform -eq 'x86') { @('Windows6.1-KB2533623-x86.msu', 'Windows6.1-KB2533623-x64.msu') } else { @('Windows6.1-KB2533623-x64.msu') }
         $kbFlag = 1
         foreach ($f in $kbNeeded) { if (-not (Test-Path (Join-Path $kbDir $f))) { $kbFlag = 0 } }
         if ($kbFlag -eq 1) { Write-Host "  KB 代装：MSU 齐套，Win7 缺补丁时自动静默安装" -ForegroundColor DarkGray }
         else { Write-Host "  KB 代装：MSU 不全，Win7 缺补丁时仍硬拦（见 build/vcredist/README.md）" -ForegroundColor Yellow }
         & $iscc "/DAppVersion=$appVersion" "/DPlatform=$Platform" "/DSourceDir=$srcAbs" "/DOutDir=$destAbs" "/DKB_BUNDLED=$kbFlag" `
             (Join-Path $repoRoot 'build/installer.iss')
         if ($LASTEXITCODE -ne 0) { throw "ISCC 打包失败（退出码 $LASTEXITCODE）" }
         $setup = Join-Path $destAbs "HaloAgentSetup-$Platform-$appVersion.exe"
         if (-not (Test-Path $setup)) { throw "安装包未生成：$setup" }
         Write-Host "  OK：$setup" -ForegroundColor Green
     }
 
     $sw.Stop()
     $size = [math]::Round(((Get-ChildItem $outDir -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)
     Write-Host ""
     Write-Host "=== 完成，用时 $([math]::Round($sw.Elapsed.TotalSeconds, 1))s，产物约 ${size}MB ===" -ForegroundColor Green
     Write-Host "运行测试：" -ForegroundColor Cyan
     Write-Host "  1) 双击启动（看窗口 + 页面）："
     Write-Host "     $outDir\HaloAgent.exe"
     Write-Host "  2) 环境诊断："
     Write-Host "     $outDir\HaloAgent.exe --diagnostics"
     Write-Host "     $outDir\HaloAgent.Engine.exe --diagnostics"
     Write-Host "  3) 引擎直连（无 UI）："
     Write-Host "     echo {`"t`":`"req`",`"id`":1,`"m`":`"system.ping`"} | $outDir\HaloAgent.Engine.exe --stdio"
     Write-Host "  4) 页面点\"ping 引擎\"：1 秒内应显示 pong + 耗时"
 }
 finally {
    Pop-Location
}

Pause