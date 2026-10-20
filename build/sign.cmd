@echo off
REM need22-P4 H6：发布物签名（signtool 时间戳）+ 签名状态输出（E5）。
REM 用法：build\sign.cmd [待签名目录，默认 artifacts\publish\win-x64]
REM   有证书：set SIGN_PFX=path\to\cert.pfx + SIGN_PASSWORD=*** 后调用，本脚本做带时间戳签名。
REM   无证书：自签占位通道（CI 输出 UNSIGNED-PLACEHOLDER 状态，不阻断）。
setlocal EnableDelayedExpansion
cd /d "%~dp0.."

set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=artifacts\publish\win-x64"
set "STATUS=UNSIGNED-PLACEHOLDER"

where signtool >nul 2>nul
if errorlevel 1 (
  echo [sign] signtool not found on PATH. STATUS=%STATUS% target=%TARGET%
  echo SIGN_STATUS=%STATUS% target=%TARGET%
  endlocal
  exit /b 0
)

if defined SIGN_PFX (
  if not exist "%TARGET%\Host.Desktop.exe" (
    echo [sign] target exe missing: %TARGET%\Host.Desktop.exe
    exit /b 1
  )
  echo [sign] signing with timestamp ...
  signtool sign /fd SHA256 /f "%SIGN_PFX%" /p "%SIGN_PASSWORD%" /tr http://timestamp.digicert.com /td SHA256 "%TARGET%\Host.Desktop.exe"
  if errorlevel 1 (
    echo [sign] sign FAILED. STATUS=SIGN-FAILED
    echo SIGN_STATUS=SIGN-FAILED target=%TARGET%
    exit /b 1
  )
  set "STATUS=SIGNED"
) else (
  echo [sign] SIGN_PFX not set: keeping self-sign placeholder, no blocking.
)

if "%STATUS%"=="SIGNED" (
  signtool verify /pa "%TARGET%\Host.Desktop.exe"
  if errorlevel 1 set "STATUS=SIGN-VERIFY-FAILED"
)

echo SIGN_STATUS=%STATUS% target=%TARGET%
endlocal
exit /b 0
