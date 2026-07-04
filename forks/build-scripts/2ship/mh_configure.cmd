@echo off
REM Megaton Hammer: configure 2Ship with MSVC + native VS CMake/Ninja (mirrors SoH).
set "PATH=C:\Windows\System32;C:\Windows;C:\Windows\System32\WindowsPowerShell\v1.0;C:\Program Files\Python313;C:\Program Files\Python313\Scripts;C:\devkitPro\msys2\usr\bin"
set "GIT_TERMINAL_PROMPT=0"
call "C:\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
set "VCPKG_ROOT=D:\Copilot_OOT\WorkFolders\vcpkg"
set "CMAKE=C:\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
set "NINJA=C:\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
cd /d "D:\Copilot_OOT\WorkFolders\MegatonHammer\2Ship"
"%CMAKE%" -S . -B build/x64 -G Ninja -DCMAKE_MAKE_PROGRAM="%NINJA%" -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl
echo MH_CONFIGURE_DONE_EXIT=%ERRORLEVEL%
