@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat" x64
set SRC=D:\Copilot_OOT\WorkFolders\MegatonHammer\Project64\Source
set OUT=D:\Copilot_OOT\WorkFolders\MegatonHammer\Project64
set RUN=D:\Copilot_OOT\WorkFolders\MegatonHammer\pj64run
msbuild "%SRC%\Project64-core\Project64-core.vcxproj" /p:Configuration=Release /p:Platform=x64 /m /v:m
if errorlevel 1 ( echo CORE_BUILD_FAILED & exit /b 1 )
msbuild "%SRC%\Project64\Project64.vcxproj" /p:Configuration=Release /p:Platform=x64 /m /v:m
if errorlevel 1 ( echo EXE_BUILD_FAILED & exit /b 1 )
msbuild "%SRC%\Project64-video\Project64-video.vcxproj" /p:Configuration=Release /p:Platform=x64 /m /v:m
if errorlevel 1 ( echo VIDEO_BUILD_FAILED & exit /b 1 )
copy /Y "%OUT%\bin\x64\Release\Project64.exe" "%RUN%\Project64.exe"
copy /Y "%OUT%\Plugin\x64\GFX\Project64-Video.dll" "%RUN%\Plugin\GFX\Project64-Video.dll"
echo PJ64_MM_BUILD_EXIT=%errorlevel%
