@echo off
REM need22-P4 H4：后端 format 手动入口（与 pre-commit / CI 同源）。
REM 用法：在 develop\ 下执行 build\format.cmd [--check]
REM   无参：自动修（dotnet-csharpier --write），对齐 pre-commit 行为。
REM   --check：只检查不改文件，对齐 backend.yml CI 门禁。
setlocal
cd /d "%~dp0.."

dotnet tool restore
if errorlevel 1 exit /b 1

if "%~1"=="--check" (
  dotnet dotnet-csharpier ./src ./tests --check
) else (
  dotnet dotnet-csharpier ./src ./tests
)
endlocal
