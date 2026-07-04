@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul 2>&1
echo INCLUDE_HAS_UCRT:
echo %INCLUDE% | findstr /i ucrt >nul && echo YES || echo NO
