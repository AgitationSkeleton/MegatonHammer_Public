@echo off
REM Megaton Hammer: build SoH (exe) + generate OTR/O2R assets from the staged ROM.
set "PATH=C:\Windows\System32;C:\Windows;C:\Windows\System32\WindowsPowerShell\v1.0;C:\Program Files\Python313;C:\Program Files\Python313\Scripts;C:\devkitPro\msys2\usr\bin"
set "GIT_TERMINAL_PROMPT=0"
call "C:\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
set "VCPKG_ROOT=D:\Copilot_OOT\WorkFolders\vcpkg"
set "CMAKE=C:\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
cd /d "D:\Copilot_OOT\WorkFolders\MegatonHammer\SoH"

echo === Building full project (this is the long step) ===
"%CMAKE%" --build build/x64
set BUILD_RC=%ERRORLEVEL%
echo MH_COMPILE_EXIT=%BUILD_RC%

if not %BUILD_RC%==0 goto :done

echo === Extracting game assets (oot.o2r) from ROM ===
"%CMAKE%" --build build/x64 --target ExtractAssets
echo MH_EXTRACT_EXIT=%ERRORLEVEL%

echo === Generating soh.o2r ===
"%CMAKE%" --build build/x64 --target GenerateSohOtr
echo MH_SOHOTR_EXIT=%ERRORLEVEL%

:done
echo MH_BUILD_DONE_EXIT=%BUILD_RC%
