@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat" x64
set SRC=D:\Copilot_OOT\WorkFolders\MegatonHammer\Project64\Source
msbuild "%SRC%\Project64-core\Project64-core.vcxproj" /p:Configuration=Release /p:Platform=x64 /v:m
if errorlevel 1 ( echo CORE_BUILD_FAILED & exit /b 1 )
msbuild "%SRC%\Project64\Project64.vcxproj" /p:Configuration=Release /p:Platform=x64 /v:m
echo PJ64_EXE_BUILD_EXIT=%errorlevel%
