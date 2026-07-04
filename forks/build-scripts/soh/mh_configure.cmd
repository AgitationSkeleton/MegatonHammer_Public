@echo off
REM Megaton Hammer: configure SoH with MSVC + native VS CMake/Ninja.
REM git (from msys2) is appended LAST so vcvars' MSVC link.exe still wins over msys link.exe.
set "PATH=C:\Windows\System32;C:\Windows;C:\Windows\System32\WindowsPowerShell\v1.0;C:\Program Files\Python313;C:\Program Files\Python313\Scripts;C:\devkitPro\msys2\usr\bin"
set "GIT_TERMINAL_PROMPT=0"
call "C:\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
REM vcvars sets VCPKG_ROOT to its own (empty) copy; override it AFTER vcvars with our pre-cloned vcpkg.
set "VCPKG_ROOT=D:\Copilot_OOT\WorkFolders\vcpkg"
set "CMAKE=C:\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
set "NINJA=C:\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
cd /d "D:\Copilot_OOT\WorkFolders\MegatonHammer\SoH"
"%CMAKE%" -S . -B build/x64 -G Ninja -DCMAKE_MAKE_PROGRAM="%NINJA%" -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl
echo MH_CONFIGURE_DONE_EXIT=%ERRORLEVEL%
