@echo off
REM Halo-Agent one-click local build for manual testing (Windows cmd).
REM Usage:
REM   build-dist.cmd            (default win-x64)
REM   build-dist.cmd win-x86    (32-bit build for old Win7 machines)
setlocal EnableDelayedExpansion

cd /d "%~dp0"

set "RUNTIME=%~1"
if "%RUNTIME%"=="" set "RUNTIME=win-x64"
if not "%RUNTIME%"=="win-x64" if not "%RUNTIME%"=="win-x86" (
echo [ERROR] Usage: build-dist.cmd [win-x64 ^| win-x86]
exit /b 1
)

if "%RUNTIME%"=="win-x86" (
set "PLATFORM=x86"
) else (
set "PLATFORM=x64"
)

set "PUBLISH_DIR=artifacts\publish\%RUNTIME%"
set "DIST_DIR=dist"

where dotnet >nul 2>nul
if errorlevel 1 (
echo [ERROR] dotnet SDK not found in PATH. Install .NET 6 SDK first.
exit /b 1
)

echo ==^> [1/3] Building frontend ^(Web.Client -^> ../Host.Desktop/wwwroot^)...
REM Skip reinstall when node_modules already exists (local manual-test shortcut).
REM Fresh clones still get a full install; if pnpm blocks build scripts, run
REM "pnpm approve-builds" once inside src\Web.Client and re-run this script.
if not exist "src\Web.Client\node_modules" (
if exist "src\Web.Client\pnpm-lock.yaml" (
call pnpm --dir src\Web.Client install --frozen-lockfile
) else (
call pnpm --dir src\Web.Client install
)
if errorlevel 1 (
echo [ERROR] Frontend install failed. If pnpm reports ERR_PNPM_IGNORED_BUILDS,
echo         run "pnpm approve-builds" in src\Web.Client once, then re-run this script.
exit /b 1
)
) else (
echo     node_modules exists, skipping pnpm install.
)
call pnpm --dir src\Web.Client build
if errorlevel 1 (
echo [ERROR] Frontend build failed.
exit /b 1
)

echo ==^> [2/3] Publishing Host.Desktop ^(%RUNTIME%, Self-Contained, Multi-File^)...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
dotnet publish "src\Host.Desktop\Host.Desktop.csproj" -c Release -r %RUNTIME% --self-contained true -p:Platform=%PLATFORM% -p:PublishSingleFile=false -p:PublishReadyToRun=false -o "%PUBLISH_DIR%"
if errorlevel 1 (
echo [ERROR] dotnet publish failed.
exit /b 1
)

echo ==^> [3/3] Copying publish output to .\%DIST_DIR%\ ...
if not exist "%DIST_DIR%" mkdir "%DIST_DIR%"
robocopy "%PUBLISH_DIR%" "%DIST_DIR%" /E /NFL /NDL /NJH /NJS
set "COPY_CODE=%ERRORLEVEL%"
REM Robocopy exit 0-7 = success (copied / extra files), 8+ = failure.
if %COPY_CODE% GEQ 8 (
echo [ERROR] Copy to dist failed ^(robocopy exit %COPY_CODE%^).
exit /b 1
)

echo.
echo [OK] Done. Test binary is in .\%DIST_DIR%\ ^(%RUNTIME%^).
echo      Example: .\%DIST_DIR%\Host.Desktop.exe
endlocal
pause
