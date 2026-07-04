@echo off
rem ============================================================================
rem   Megaton Hammer - one-run build (double-click friendly wrapper)
rem ============================================================================
rem   This just runs build-megaton.ps1 for you with the right settings, so you
rem   don't have to deal with PowerShell's execution-policy prompts. It does NOT
rem   need to "Run as administrator" - keep it in the same (empty) folder as
rem   build-megaton.ps1 and double-click it.
rem
rem   Options (optional - only if you run it from a terminal):
rem     build-megaton.bat -EditorOnly    build just the editor (fastest)
rem     build-megaton.bat -Clean         delete cloned repos first (fresh build)
rem   Any flags you pass are forwarded straight to build-megaton.ps1.
rem
rem   Prerequisites are the same as the ps1: Git, .NET 8 SDK (editor), and for
rem   the playtest forks also Visual Studio 2022 (C++ workload) + CMake.
rem ============================================================================

setlocal
set "SCRIPT=%~dp0build-megaton.ps1"

if not exist "%SCRIPT%" (
    echo.
    echo   ERROR: build-megaton.ps1 was not found next to this batch file.
    echo   Keep build-megaton.bat and build-megaton.ps1 together in the same folder.
    echo.
    pause
    exit /b 1
)

rem Prefer PowerShell 7 (pwsh) if present, otherwise fall back to Windows PowerShell.
where pwsh >nul 2>&1
if %ERRORLEVEL%==0 (
    set "PS=pwsh"
) else (
    set "PS=powershell"
)

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "RC=%ERRORLEVEL%"

echo.
if not "%RC%"=="0" (
    echo   Build script exited with code %RC%.  See the messages above.
)
rem Pause so double-click users can read the summary before the window closes.
pause
exit /b %RC%
