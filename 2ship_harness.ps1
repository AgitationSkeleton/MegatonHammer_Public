# Headless 2Ship (MM) playtest harness — MM counterpart of soh_harness.ps1. Exports an MM-format
# Test Temple O2R, launches 2ship.exe (the MH boot hook auto-warps into SCENE_MH_APPEND), then reports
# PASS/CRASH from the engine log (content-based). Usage: 2ship_harness.ps1 [-EngineDir <dir>] [-WaitSec 35] [-Build]
param(
    [string]$EngineDir = "D:\Copilot_OOT\WorkFolders\MegatonHammer\2Ship\x64\Release",
    [int]$WaitSec = 35,
    [switch]$Build,
    [string]$Preset = "Default"
)
$ErrorActionPreference = "Continue"
$mhProj  = "D:\Copilot_OOT\WorkFolders\MegatonHammer\src\MegatonHammer\MegatonHammer.csproj"
$mhExe   = "D:\Copilot_OOT\WorkFolders\MegatonHammer\src\MegatonHammer\bin\Release\net8.0-windows\win-x64\MegatonHammer.exe"
$exe     = Join-Path $EngineDir "2ship.exe"
$bootLog = Join-Path $EngineDir "mh_playtest_boot.log"
$crashLog = Join-Path $EngineDir "logs\2 Ship 2 Harkinian.log"

if ($Build) {
    Write-Output "[harness] building editor (Release)..."
    & "C:\Program Files\dotnet\dotnet.exe" build $mhProj -c Release -v q -nologo 2>&1 | Select-String -Pattern "error|Build succeeded" | Select-Object -First 3
}

Write-Output "[harness] exporting MM Test Temple -> mods\mh_playtest.o2r (mm, append mode)"
& $mhExe --packplaytest $EngineDir $Preset mm append 2>&1 | Select-String -Pattern "packed|EXCEPTION" | ForEach-Object { "  $_" }

# 2Ship writes normal logs AND crash dumps to the same file; detect crashes by content, not size.
if (Test-Path $crashLog) { Move-Item $crashLog "$crashLog.prev" -Force -ErrorAction SilentlyContinue }
Remove-Item $bootLog -ErrorAction SilentlyContinue

Write-Output "[harness] launching 2Ship for $WaitSec sec..."
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
Write-Output ""
Write-Output "=== VERDICT ==="
if ($crashed) {
    Write-Output "CRASH:"
    Get-Content $crashLog | Select-Object -Last 60 | Select-String -Pattern "Exception:|Line:|Scene:|Category:|RIP:|gfx_|Unhandled OP" | Select-Object -Last 24 | ForEach-Object { "  $_" }
} elseif ($rendered) {
    (Get-Content $bootLog | Select-String -Pattern "RENDER OK" | Select-Object -First 1) | ForEach-Object { "  $_" }
    Write-Output "PASS - level loaded AND rendering frames in SCENE_MH_APPEND, no crash."
} elseif ($warped) {
    Write-Output "PASS (partial) - warp triggered, no crash, but no render confirmation yet."
} else {
    Write-Output "INCONCLUSIVE - no crash, but no 'warp triggered' in boot log."
}
