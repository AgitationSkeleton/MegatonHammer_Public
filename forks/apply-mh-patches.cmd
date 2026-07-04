@echo off
REM ============================================================================
REM Megaton Hammer: apply the editor's customizations to the playtest engine
REM submodules (SoH / 2Ship) after they are checked out at their pinned upstream
REM commit. Idempotent — safe to re-run. Installs the mh_playtest console command
REM patch and the MH build scripts into each fork's working tree.
REM
REM Usage (from anywhere):  forks\apply-mh-patches.cmd
REM Prereq:                 git submodule update --init --recursive
REM ============================================================================
setlocal
set "FORKS=%~dp0"
set "FORKS=%FORKS:~0,-1%"
for %%F in ("%FORKS%\..") do set "ROOT=%%~fF"

call :apply "%ROOT%\SoH"   soh   soh-mh_playtest.patch
call :apply "%ROOT%\2Ship" 2ship 2ship-mh_playtest.patch
REM SoH build fix: this toolchain's linked STL lib is older than its cl.exe, so SoH's util.cpp emits
REM char-search intrinsics (__std_find_not_ch_1 / __std_find_last_not_ch_pos_1) the older STL lib does
REM not export -> LNK2001. A scalar shim in util.cpp resolves the link. (2Ship links a newer STL.)
call :applysub "%ROOT%\SoH" soh-buildfix.patch
REM SoH's nested libultraship submodule: a Fast3D gfx-interpreter fix so a failed texture-load in
REM gfx_set_timg_otr_hash_handler_custom no longer over-advances the command pointer (which corrupted
REM the DL stream and crashed with a strlen on a misread opcode). Lets editor-exported geometry render.
call :applysub "%ROOT%\SoH\libultraship"   soh-libultraship.patch
REM 2Ship also patches its nested libultraship submodule (a GUI-texture null-guard that fixes the
REM instant boot crash). Applied separately since libultraship is its own submodule.
call :applysub "%ROOT%\2Ship\libultraship" 2ship-libultraship.patch
echo Done.
endlocal
exit /b 0

:apply
set "FORKDIR=%~1"
set "SCRIPTS=%~2"
set "PATCH=%FORKS%\patches\%~3"
echo === %FORKDIR% ===
if not exist "%FORKDIR%\.git" (
    echo   [skip] submodule not initialized. Run: git submodule update --init "%FORKDIR%"
    exit /b 0
)
REM Already applied?  (reverse-check succeeds when the change is present)
git -C "%FORKDIR%" apply --reverse --check "%PATCH%" >nul 2>&1
if not errorlevel 1 (
    echo   patch already applied.
) else (
    git -C "%FORKDIR%" apply "%PATCH%"
    if errorlevel 1 ( echo   [error] failed to apply %~3 & exit /b 1 )
    echo   patch applied.
)
copy /y "%FORKS%\build-scripts\%SCRIPTS%\mh_configure.cmd" "%FORKDIR%\mh_configure.cmd" >nul
copy /y "%FORKS%\build-scripts\%SCRIPTS%\mh_build.cmd"     "%FORKDIR%\mh_build.cmd"     >nul
echo   build scripts installed.
exit /b 0

REM Apply a patch to a nested submodule (no build scripts to install).
:applysub
set "SUBDIR=%~1"
set "SUBPATCH=%FORKS%\patches\%~2"
echo === %SUBDIR% ===
if not exist "%SUBDIR%\.git" ( echo   [skip] submodule not initialized. & exit /b 0 )
git -C "%SUBDIR%" apply --reverse --check "%SUBPATCH%" >nul 2>&1
if not errorlevel 1 (
    echo   patch already applied.
) else (
    git -C "%SUBDIR%" apply "%SUBPATCH%"
    if errorlevel 1 ( echo   [error] failed to apply %~2 & exit /b 1 )
    echo   patch applied.
)
exit /b 0
