# Headless SoH/2Ship playtest harness: export a test level -> launch the engine (the MH boot hook
# auto-warps into it) -> read the boot log + detect any new crash -> report a verdict. Lets engine work
# iterate unattended. Usage: soh_harness.ps1 [-EngineDir <dir>] [-WaitSec 35] [-Build]
param(
    [string]$EngineDir = "D:\Copilot_OOT\WorkFolders\MegatonHammer\SoH\x64\Release",
    [int]$WaitSec = 35,
    [switch]$Build,
    [string]$Preset = "Default"
)
$ErrorActionPreference = "Continue"
$mhProj  = "D:\Copilot_OOT\WorkFolders\MegatonHammer\src\MegatonHammer\MegatonHammer.csproj"
$mhExe   = "D:\Copilot_OOT\WorkFolders\MegatonHammer\src\MegatonHammer\bin\Release\net8.0-windows\win-x64\MegatonHammer.exe"
$exe     = Join-Path $EngineDir "soh.exe"
$bootLog = Join-Path $EngineDir "mh_playtest_boot.log"
$crashLog = Join-Path $EngineDir "logs\Ship of Harkinian.log"

if ($Build) {
    Write-Output "[harness] building editor (Release)..."
    & "C:\Program Files\dotnet\dotnet.exe" build $mhProj -c Release -v q -nologo 2>&1 | Select-String -Pattern "error|Build succeeded" | Select-Object -First 3
}

Write-Output "[harness] exporting Test Temple -> mods\mh_playtest.o2r (append mode)"
& $mhExe --packplaytest $EngineDir $Preset append 2>&1 | Select-String -Pattern "packed|EXCEPTION" | ForEach-Object { "  $_" }

# SoH writes BOTH normal logs and crash dumps to this same file, so detect crashes by content
# (a CrashHandler "Exception:" line), not by size growth. Rotate the old log so any crash this run
# is unambiguous.
if (Test-Path $crashLog) { Move-Item $crashLog "$crashLog.prev" -Force -ErrorAction SilentlyContinue }
Remove-Item $bootLog -ErrorAction SilentlyContinue

Write-Output "[harness] launching SoH for $WaitSec sec..."
$p = Start-Process -FilePath $exe -WorkingDirectory $EngineDir -PassThru
$exited = $p.WaitForExit($WaitSec * 1000)
if (-not $exited) { Stop-Process -Id $p.Id -Force; $how = "killed" } else { $how = "self-exited code=$($p.ExitCode)" }
Start-Sleep -Seconds 1

Write-Output ""
Write-Output "=== BOOT LOG ($how) ==="
if (Test-Path $bootLog) { Get-Content $bootLog } else { Write-Output "(no boot log - hook never ran)" }

$crashed  = (Test-Path $crashLog) -and (Select-String -Path $crashLog -Pattern "Exception:|CrashHandler" -Quiet)
$warped   = (Test-Path $bootLog)  -and (Select-String -Path $bootLog  -Pattern "OK: warp triggered" -Quiet)
$rendered = (Test-Path $bootLog)  -and (Select-String -Path $bootLog  -Pattern "RENDER OK" -Quiet)
$sceneInit = (Test-Path $crashLog) -and (Select-String -Path $crashLog -Pattern "Scene Init - sceneNum" -Quiet)
Write-Output ""
Write-Output "=== VERDICT ==="
if ($crashed) {
    Write-Output "CRASH:"
    Get-Content $crashLog | Select-Object -Last 60 | Select-String -Pattern "Exception:|Line:|Scene:|Category:|RIP:|gfx_|Unhandled OP" | Select-Object -Last 24 | ForEach-Object { "  $_" }
} elseif ($rendered) {
    (Get-Content $bootLog | Select-String -Pattern "RENDER OK" | Select-Object -First 1) | ForEach-Object { "  $_" }
    Write-Output "PASS - level loaded AND rendering frames in SCENE_MH_APPEND, no crash."
} elseif ($warped -and $sceneInit) {
    Write-Output "PASS - warp triggered, scene init reached, no crash. Level loaded."
} elseif ($warped) {
    Write-Output "PASS (partial) - warp triggered, no crash, but no render confirmation yet."
} else {
    Write-Output "INCONCLUSIVE - no crash, but no 'warp triggered' in boot log."
}
