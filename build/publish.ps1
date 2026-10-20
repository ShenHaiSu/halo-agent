param(
    [ValidateSet("win-x64", "win-x86")]
    [string]$Runtime = "win-x64",
    # need22-P4 H7：版本号（默认取 git tag，失败回退 0.6.0），与 Assembly VersionPrefix、installer.iss 对齐（E6）
    [string]$Version = "",
    # need22-P4 H6：置位后引入 Microsoft.Sbom.Targets 产出 sbom（默认关闭，不影响常规构建）
    [switch]$GenerateSbom
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    try { $tag = (git -C $RepoRoot describe --tags --abbrev=0 2>$null) } catch { $tag = "" }
    if ([string]::IsNullOrWhiteSpace($tag)) { $tag = "v0.6.0" }
    $Version = $tag.TrimStart("v")
}
Write-Host "==> Version: $Version (tag-aligned, E6)" -ForegroundColor Cyan

Write-Host "==> [1/4] Building Frontend (Web.Client -> target: chrome109)..." -ForegroundColor Cyan
Push-Location "$RepoRoot\src\Web.Client"
pnpm install --frozen-lockfile
pnpm build
Pop-Location

Write-Host "==> [2/4] Publishing Host.Desktop ($Runtime, Self-Contained, Multi-File)..." -ForegroundColor Cyan
$Platform = if ($Runtime -eq "win-x86") { "x86" } else { "x64" }
$OutDir = "$RepoRoot\artifacts\publish\$Runtime"

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }

$sbomArgs = @()
if ($GenerateSbom) { $sbomArgs += "-p:GenerateSbom=true" }

dotnet publish "$RepoRoot\src\Host.Desktop\Host.Desktop.csproj" `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:Platform=$Platform `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:VersionPrefix=$Version `
    @sbomArgs `
    -o $OutDir

# 铁律门禁：CefSharp 109 原生件要求多文件发布
if (-not (Test-Path "$OutDir\libcef.dll")) {
    throw "SINGLE-FILE SUSPECTED: libcef.dll missing in $OutDir (PublishSingleFile must stay false)."
}

Write-Host "==> [3/4] Sign status (H6)..." -ForegroundColor Cyan
& "$RepoRoot\build\sign.cmd" "artifacts\publish\$Runtime"

Write-Host "==> [4/4] Packaging Setup.exe via Inno Setup 6..." -ForegroundColor Cyan
$IsccPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (Test-Path $IsccPath) {
    & $IsccPath "/DMyAppArch=$Runtime" "/DSourceDir=$OutDir" "/DMyAppVersion=$Version" "$RepoRoot\build\installer.iss"
} else {
    Write-Warning "ISCC.exe not found at $IsccPath. Skipping installer generation."
}
