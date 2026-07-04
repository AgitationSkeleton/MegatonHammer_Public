<#
================================================================================
  Megaton Hammer - one-run build script (public edition)
================================================================================
  Builds the Megaton Hammer EDITOR (this repo) plus the three playtest engine
  forks (SoH / 2Ship / PJ64). The engines are fetched from their ORIGINAL
  upstreams (HarbourMasters / Project64) and Megaton Hammer's small changes are
  applied on top from forks\ -- so this package depends on NObody's private fork.

  HOW TO RUN
    1. Clone this repo (it already contains the editor + the fork delta).
    2. From a terminal in the repo root:
         powershell -ExecutionPolicy Bypass -File .\build-megaton.ps1
    3. When it finishes, double-click the "Megaton Hammer" shortcut it created.

  OPTIONS
    -EditorOnly   Build just the editor (skip all three forks) - fastest.
    -SkipForks    Same as -EditorOnly.
    -Clean        Delete previously fetched engine checkouts first.

  PREREQUISITES
    - Git, .NET 8 SDK            (editor)
    - Visual Studio 2022 + "Desktop development with C++", CMake   (forks)

  ROMs  (you must supply your own, legally-owned copies - none ship here)
    - The editor reads a ROM at RUNTIME (set the paths in Options on first run).
    - The SoH/2Ship forks extract assets from a ROM at BUILD time; put OoT / MM
      ROMs in a "roms" folder beside this script for those forks to finish.
================================================================================
#>
param([switch]$EditorOnly, [switch]$SkipForks, [switch]$Clean)
$ErrorActionPreference = 'Continue'
if ($SkipForks) { $EditorOnly = $true }

$Root = $PSScriptRoot; if (-not $Root) { $Root = (Get-Location).Path }
Set-Location $Root

# Engine forks come from UPSTREAM, pinned to the commit Megaton Hammer's patches were made against
# (see forks\README.md). PJ64 is overlaid from forks\pj64 rather than patched.
$Forks = @(
    @{ Name='SoH';   Dir='SoH';   Url='https://github.com/HarbourMasters/Shipwright.git';       Pin='948b84d8'; Rom='oot|ocarina|OOT' }
    @{ Name='2Ship'; Dir='2Ship'; Url='https://github.com/HarbourMasters/2ship2harkinian.git';  Pin='3545e62e'; Rom='mm|majora|MM'   }
)
$Pj64Url = 'https://github.com/project64/project64.git'

function Hdr($t)  { Write-Host ""; Write-Host "==== $t ====" -ForegroundColor Cyan }
function Info($t) { Write-Host "  $t" -ForegroundColor Gray }
function Ok($t)   { Write-Host "  $t" -ForegroundColor Green }
function Warn($t) { Write-Host "  $t" -ForegroundColor Yellow }
function Err($t)  { Write-Host "  $t" -ForegroundColor Red }
function Have($exe) { $null -ne (Get-Command $exe -ErrorAction SilentlyContinue) }
function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $mb = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null | Select-Object -First 1
        if ($mb -and (Test-Path $mb)) { return $mb }
    }
    if (Have 'msbuild') { return 'msbuild' }
    return $null
}

Hdr "Megaton Hammer build (public) - $Root"
if (-not (Have 'git'))    { Err "git not found - install Git for Windows, then re-run."; return }
if (-not (Have 'dotnet')) { Err ".NET 8 SDK not found - install it (the editor needs it), then re-run."; return }
$cmakeOk = Have 'cmake'; $msbuild = Find-MSBuild
Info ("git: ok   dotnet: {0}   cmake: {1}   msbuild: {2}" -f (dotnet --version 2>$null),
      $(if($cmakeOk){'ok'}else{'MISSING'}), $(if($msbuild){'ok'}else{'MISSING'}))

# 1. Build the EDITOR (this repo - src\ is local, nothing to clone)
Hdr "Building the editor (Megaton Hammer)"
$csproj = Join-Path $Root 'src\MegatonHammer\MegatonHammer.csproj'
$EditorExe = Join-Path $Root 'src\MegatonHammer\bin\Release\net8.0-windows\win-x64\MegatonHammer.exe'
Get-Process MegatonHammer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
& dotnet build $csproj -c Release -v m 2>&1 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
if (Test-Path $EditorExe) { Ok "editor built -> $EditorExe" } else { Err "editor build FAILED (see output above)" }

if (-not $EditorOnly) {
    # 2. Fetch the SoH/2Ship engines from UPSTREAM at the pinned commit, then apply MH's patches.
    foreach ($f in $Forks) {
        Hdr "Fetching $($f.Name) engine (upstream, pinned $($f.Pin))"
        $dir = Join-Path $Root $f.Dir
        if ($Clean -and (Test-Path $dir)) { Info "removing $($f.Dir) (-Clean)"; Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue }
        if (-not (Test-Path (Join-Path $dir '.git'))) {
            Info "cloning $($f.Url) ..."
            git clone $f.Url $dir 2>&1 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            Info "checking out pin $($f.Pin) ..."
            git -C $dir checkout $f.Pin 2>&1 | Out-Null
            git -C $dir submodule update --init --recursive 2>&1 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        } else { Info "$($f.Dir) already present - leaving as-is." }
    }
    # 3. Apply Megaton Hammer's fork delta (mh_playtest command + build-fix + libultraship guards).
    Hdr "Applying Megaton Hammer fork patches"
    $apply = Join-Path $Root 'forks\apply-mh-patches.cmd'
    if (Test-Path $apply) { & cmd /c "`"$apply`"" } else { Warn "forks\apply-mh-patches.cmd missing - skipping patch step." }

    # 4. Build SoH / 2Ship (best effort - needs CMake + VS C++ + a ROM for asset extraction).
    function Build-ShipFork($f) {
        Hdr "Building $($f.Name) fork"
        $dir = Join-Path $Root $f.Dir
        if (-not (Test-Path (Join-Path $dir 'CMakeLists.txt'))) { Warn "$($f.Name) not present - skipping."; return }
        if (-not $cmakeOk) { Warn "CMake not found - skipping $($f.Name)."; return }
        $mhc = Join-Path $dir 'mh_configure.cmd'; $mhb = Join-Path $dir 'mh_build.cmd'
        if ((Test-Path $mhc) -and (Test-Path $mhb)) {
            Info "using the fork's mh_configure/mh_build (edit the paths at their top for your machine)..."
            & cmd /c "`"$mhc`"" ; & cmd /c "`"$mhb`""
        } else {
            $build = Join-Path $dir 'build\x64'
            & cmake -S $dir -B $build -G 'Visual Studio 17 2022' -A x64 2>&1 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            $rom = Get-ChildItem (Join-Path $Root 'roms') -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $f.Rom } | Select-Object -First 1
            if ($rom) { & cmake --build $build --config Release --target ExtractAssets 2>&1 | Out-Null }
            else { Warn "no $($f.Rom) ROM in .\roms - the fork may need assets at first launch." }
            & cmake --build $build --config Release 2>&1 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        }
    }
    $Forks | ForEach-Object { Build-ShipFork $_ }

    # 5. PJ64 (optional): overlay forks\pj64 onto an upstream Project64 checkout, then build.
    Hdr "Project64 fork (optional N64 playtest path)"
    Info "PJ64 changes live in forks\pj64 (GPLv2). To build them:"
    Info "  1) git clone $Pj64Url Project64   (develop branch)"
    Info "  2) copy forks\pj64\*.cpp / .vcxproj over the matching Source\Project64-core\... files"
    Info "     and forks\pj64\Config over Project64's Config (see forks\pj64\README.md)."
    Info "  3) add MegatonHammer.cpp to Project64-core.vcxproj, build Release|x64 in VS 2022."
    Info "The editor + SoH/2Ship playtest work WITHOUT PJ64; it is only the vanilla-N64 path."
}

# 6. Shortcut + summary
Hdr "Creating shortcut"
if (Test-Path $EditorExe) {
    $lnk = Join-Path $Root 'Megaton Hammer.lnk'
    $sh = New-Object -ComObject WScript.Shell; $s = $sh.CreateShortcut($lnk)
    $s.TargetPath = $EditorExe; $s.WorkingDirectory = Split-Path $EditorExe
    $s.IconLocation = "$EditorExe,0"; $s.Description = 'Megaton Hammer - Zelda 64 level editor'; $s.Save()
    Ok "shortcut -> $lnk"
    Info "First run: set your OoT/MM ROM paths and (for playtest) the soh.exe / 2ship.exe / Project64.exe (Options)."
} else { Err "The editor did not build - check the messages above (usually a missing .NET 8 SDK)." }
