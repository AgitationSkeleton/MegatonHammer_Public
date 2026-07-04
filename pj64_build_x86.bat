@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat" x86
set SRC=D:\Copilot_OOT\WorkFolders\MegatonHammer\Project64\Source
msbuild "%SRC%\Project64-core\Project64-core.vcxproj" /p:Configuration=Release /p:Platform=Win32 /m /v:m
if errorlevel 1 ( echo CORE_BUILD_FAILED & exit /b 1 )
msbuild "%SRC%\Project64\Project64.vcxproj" /p:Configuration=Release /p:Platform=Win32 /m /v:m
echo PJ64_X86_BUILD_EXIT=%errorlevel%
