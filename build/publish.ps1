param(
    [ValidateSet("win-x64", "win-x86")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

Write-Host "==> [1/3] Building Frontend (Web.Client -> target: chrome109)..." -ForegroundColor Cyan
Push-Location "$RepoRoot\src\Web.Client"
pnpm install --frozen-lockfile
pnpm build
Pop-Location

Write-Host "==> [2/3] Publishing Host.Desktop ($Runtime, Self-Contained, Multi-File)..." -ForegroundColor Cyan
$Platform = if ($Runtime -eq "win-x86") { "x86" } else { "x64" }
$OutDir = "$RepoRoot\artifacts\publish\$Runtime"

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }

dotnet publish "$RepoRoot\src\Host.Desktop\Host.Desktop.csproj" `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:Platform=$Platform `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -o $OutDir

Write-Host "==> [3/3] Packaging Setup.exe via Inno Setup 6..." -ForegroundColor Cyan
$IsccPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (Test-Path $IsccPath) {
    & $IsccPath "/DMyAppArch=$Runtime" "/DSourceDir=$OutDir" "$RepoRoot\build\installer.iss"
} else {
    Write-Warning "ISCC.exe not found at $IsccPath. Skipping installer generation."
}
