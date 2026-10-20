@echo off
REM need22-P4 H5：清理本地构建堆积（E4）。CI 首步调用。
REM 只清 .NET 构建产物与发布中间目录，不碰 src\Web.Client\node_modules。
REM 用法：在 develop\ 下执行 build\clean.cmd
setlocal
cd /d "%~dp0.."

echo [clean] removing bin/obj/artifacts/dist/TestResults ...
for %%p in (
  "src\Host.Desktop\bin" "src\Host.Desktop\obj"
  "tests\HaloAgent.Tests\bin" "tests\HaloAgent.Tests\obj"
) do (
  if exist %%p rmdir /s /q %%p
)
if exist "artifacts" rmdir /s /q "artifacts"
if exist "dist" rmdir /s /q "dist"
if exist "TestResults" rmdir /s /q "TestResults"

echo [clean] verifying ...
set "LEFT=0"
for %%p in (
  "src\Host.Desktop\bin" "src\Host.Desktop\obj"
  "tests\HaloAgent.Tests\bin" "tests\HaloAgent.Tests\obj"
  "artifacts" "dist"
) do (
  if exist %%p set /a LEFT+=1
)
if %LEFT% NEQ 0 (
  echo [clean] WARN: %LEFT% leftover dirs remain.
  exit /b 1
)
echo [clean] OK: bin/obj/artifacts/dist empty.
endlocal
