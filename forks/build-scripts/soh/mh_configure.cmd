@echo off
REM Megaton Hammer: configure SoH with MSVC + CMake/Ninja.
REM apply-mh-patches.cmd installs this INTO the SoH working tree, so %~dp0 is the fork dir.
REM Prereqs: Visual Studio 2022 (C++ workload, provides cmake/ninja); python3 + git on PATH;
REM          VCPKG_ROOT set to your vcpkg checkout.
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
cmake -S . -B build/x64 -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl
echo MH_CONFIGURE_DONE_EXIT=%ERRORLEVEL%
endlocal
