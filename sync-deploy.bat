@echo off
rem ============================================================================
rem   Sync Megaton Hammer:  Staging (newest build)  ->  Deploy (where you run it)
rem ============================================================================
rem   Claude builds into the Staging folder and NEVER touches Deploy, so a build
rem   can never close the editor you're working in. When YOU are ready to pick up
rem   the latest changes: SAVE your project, then run this. It closes the editor,
rem   copies the new build over Deploy, and relaunches it.
rem ============================================================================
setlocal
set "STAGING=%~dp0Staging"
set "DEPLOY=%~dp0Deploy"

if not exist "%STAGING%\MegatonHammer.exe" (
    echo.
    echo   ERROR: no build found in "%STAGING%".
    echo   Nothing to sync yet.
    echo.
    pause
    exit /b 1
)

echo.
echo   Staging : %STAGING%
echo   Deploy  : %DEPLOY%
echo.
echo   This CLOSES Megaton Hammer and updates Deploy from the latest build.
echo   ** SAVE YOUR PROJECT FIRST. **
echo.
choice /C YN /N /M "   Proceed?  [Y/N]  "
if errorlevel 2 (
    echo   Cancelled.
    exit /b 0
)

echo.
echo   Closing Megaton Hammer...
taskkill /F /IM MegatonHammer.exe >nul 2>&1
rem let the OS release the file locks before copying
ping -n 2 127.0.0.1 >nul

echo   Syncing Staging -^> Deploy...
robocopy "%STAGING%" "%DEPLOY%" /MIR /R:3 /W:1 /NFL /NDL /NJH /NJS /NC /NS >nul
rem robocopy: exit codes 0-7 are success (files copied / nothing to do); 8+ are real errors
if %ERRORLEVEL% GEQ 8 (
    echo.
    echo   Sync FAILED (robocopy code %ERRORLEVEL%^). Is a file still locked?
    echo   Close everything and try again.
    echo.
    pause
    exit /b 1
)

echo   Done. Relaunching...
start "" "%DEPLOY%\MegatonHammer.exe"
echo.
echo   Deploy is up to date and Megaton Hammer is running again.
exit /b 0
