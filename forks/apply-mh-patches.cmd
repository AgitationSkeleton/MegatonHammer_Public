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
REM SoH is now the Fierce Deity fork (soh_fd), which is self-contained and already carries the two fixes
REM the old HarbourMasters base needed as separate sub-patches:
REM   - soh-buildfix.patch (util.cpp STL-intrinsic shim) is folded INTO soh-mh_playtest.patch.
REM   - soh-libultraship.patch (Fast3D gfx-interpreter over-advance fix) is already VENDORED in soh_fd's
REM     libultraship, so there is no nested SoH\libultraship submodule to patch.
REM (Both retained in patches\ for reference / the deprecated HarbourMasters base.)
REM 2Ship still uses the HarbourMasters base + its nested libultraship submodule (a GUI-texture null-guard
REM that fixes the instant boot crash). Applied separately since libultraship is its own submodule.
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
