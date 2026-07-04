@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat" x64
set "VCPKG_ROOT=D:\Copilot_OOT\WorkFolders\vcpkg"
set "PATH=%PATH%;C:\devkitPro\msys2\usr\bin"
set "CMAKE=C:\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
cd /d "D:\Copilot_OOT\WorkFolders\MegatonHammer\SoH"
"%CMAKE%" --build build/x64 --target soh
echo SOH_BUILD_EXIT=%errorlevel%
