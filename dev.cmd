@echo off
REM ============================================================================
REM  Halo-Agent one-click DEVELOPMENT launcher (Windows cmd)
REM
REM  What it does:
REM    1) Spawns a NEW cmd window, cd into src\Web.Client, runs `pnpm run dev`
REM       (Vite dev server with HMR, port 5173 / strictPort).
REM    2) Waits until the dev server is actually listening on 5173.
REM    3) This window cd into dist, sets the dev env vars, then launches
REM       Host.Desktop.exe -> CEF loads the dev server and opens DevTools.
REM
REM  Dev-mode env vars (see src/Host.Desktop/MainForm.cs):
REM    HALO_ENV=Development       -> dev mode: load HALO_DEV_SERVER_URL + show DevTools
REM    HALO_DEV_SERVER_URL=...    -> dev server URL (default http://localhost:5173)
REM
REM  Optional (uncomment the two lines in section 3 if needed):
REM    HALO_BOOTSTRAP_PATH        -> isolate dev data from the production data root
REM    HALO_ALLOW_MULTIINSTANCE=1 -> let the dev instance run beside a production one
REM ============================================================================
setlocal EnableExtensions
cd /d "%~dp0"

set "DEV_PORT=5173"
set "DEV_URL=http://localhost:5173"
set "WEB_DIR=%~dp0src\Web.Client"
set "DIST_DIR=%~dp0dist"

REM ---- preflight -------------------------------------------------------------
if not exist "%WEB_DIR%\node_modules" (
  echo [ERROR] Frontend deps missing: %WEB_DIR%\node_modules
  echo          Run `pnpm install` inside src\Web.Client first.
  goto :fail
)
if not exist "%DIST_DIR%\Host.Desktop.exe" (
  echo [ERROR] Host binary missing: %DIST_DIR%\Host.Desktop.exe
  echo          Run build-dist.cmd first to generate .\dist.
  goto :fail
)

REM ---- 1) Vite dev server in a NEW window ------------------------------------
echo [dev] 1/3 Starting Vite dev server in a new window ...
start "HaloAgent Dev Server" /d "%WEB_DIR%" cmd /k "pnpm run dev"

REM ---- 2) wait until the dev server is really up (max 60s) -------------------
echo [dev] 2/3 Waiting for %DEV_URL% to be ready ...
set /a WAIT=0
:wait_loop
netstat -ano | findstr /c:"LISTENING" | findstr /c:":%DEV_PORT% " >nul 2>&1
if not errorlevel 1 goto :server_ready
set /a WAIT+=1
if %WAIT% GEQ 60 (
  echo [ERROR] Timed out waiting for the dev server after 60 seconds.
  echo          Check the "HaloAgent Dev Server" window for errors.
  goto :fail
)
timeout /t 1 /nobreak >nul
goto :wait_loop

:server_ready
echo [dev] Dev server is up.

REM ---- 3) enter dist, set dev env, launch Host -------------------------------
echo [dev] 3/3 Launching Host.Desktop.exe in DEVELOPMENT mode ...
cd /d "%DIST_DIR%"

set "HALO_ENV=Development"
set "HALO_DEV_SERVER_URL=%DEV_URL%"
REM set "HALO_BOOTSTRAP_PATH=%~dp0.tmp\dev-bootstrap\bootstrap.json"
REM set "HALO_ALLOW_MULTIINSTANCE=1"

start "HaloAgent Desktop Dev" "%DIST_DIR%\Host.Desktop.exe"

echo [dev] Done. Vite window = hot reload, Host window = the app.
echo [dev] Close either window to stop that part.
goto :done

:fail
echo [dev] Startup aborted.

:done
endlocal
pause
