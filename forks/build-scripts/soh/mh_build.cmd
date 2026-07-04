@echo off
REM Megaton Hammer: build SoH (exe) + generate OTR/O2R assets from your staged OoT ROM.
REM Installed INTO the SoH working tree by apply-mh-patches.cmd, so %~dp0 is the fork dir.
REM Prereqs: same as mh_configure.cmd (VS 2022 C++, python3 + git on PATH, VCPKG_ROOT set).
setlocal
cd /d "%~dp0"
set "GIT_TERMINAL_PROMPT=0"
if "%VCPKG_ROOT%"=="" ( echo ERROR: set VCPKG_ROOT to your vcpkg folder first, then re-run. & exit /b 1 )
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if "%VSCMD_VER%"=="" (
    if exist "%VSWHERE%" (
        for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do call "%%i\VC\Auxiliary\Build\vcvars64.bat"
    ) else ( echo ERROR: Visual Studio not found. Run from a "Developer Command Prompt for VS 2022". & exit /b 1 )
)

echo === Building full project (this is the long step) ===
cmake --build build/x64
set BUILD_RC=%ERRORLEVEL%
echo MH_COMPILE_EXIT=%BUILD_RC%

if not %BUILD_RC%==0 goto :done

echo === Extracting game assets (oot.o2r) from ROM ===
cmake --build build/x64 --target ExtractAssets
echo MH_EXTRACT_EXIT=%ERRORLEVEL%

echo === Generating soh.o2r ===
cmake --build build/x64 --target GenerateSohOtr
echo MH_SOHOTR_EXIT=%ERRORLEVEL%

:done
echo MH_BUILD_DONE_EXIT=%BUILD_RC%
endlocal
