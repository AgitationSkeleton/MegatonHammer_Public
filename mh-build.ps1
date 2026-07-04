# Megaton Hammer build helper (Claude's build pipeline).
#   1. Always builds into Staging/.
#   2. If the DEPLOY editor is NOT currently running, also mirrors Staging -> Deploy so the user
#      doesn't have to run sync-deploy.bat. If it IS running, leaves Deploy alone (Staging only),
#      so a build never closes the editor mid-edit; the user syncs when ready.
# Detection is by the running process's executable path (must live under \Deploy\), so short-lived
# Staging CLI self-tests don't count as "the editor is running".
param([switch]$Quiet)
$ErrorActionPreference = 'Stop'
$root    = 'D:\Copilot_OOT\WorkFolders\MegatonHammer'
$staging = Join-Path $root 'Staging'
$deploy  = Join-Path $root 'Deploy'
$proj    = Join-Path $root 'src\MegatonHammer\MegatonHammer.csproj'

$out = & 'C:\Program Files\dotnet\dotnet.exe' build $proj -c Release -v q --output $staging 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host 'BUILD FAILED — Staging/Deploy left unchanged:'
    $out | Where-Object { $_ -match 'error|FAILED' } | Select-Object -First 20 | ForEach-Object { Write-Host $_ }
    exit 1
}

$deployExe = Join-Path $deploy 'MegatonHammer.exe'
$running = Get-Process -Name 'MegatonHammer' -ErrorAction SilentlyContinue |
           Where-Object { $_.Path -and ($_.Path -ieq $deployExe) }

if ($running) {
    Write-Host 'BUILT -> Staging. Deploy editor is RUNNING; Deploy left untouched. Run sync-deploy.bat when ready.'
} else {
    robocopy $staging $deploy /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NC /NS | Out-Null
    if ($LASTEXITCODE -ge 8) { Write-Host "BUILT -> Staging, but Deploy sync FAILED (robocopy $LASTEXITCODE)."; exit 1 }
    Write-Host 'BUILT -> Staging, and Deploy editor not running -> synced to Deploy directly.'
}

# Also mirror source changes into the public repo (best-effort; never fails the build).
try {
    $syncPub = Join-Path $root 'sync-public.ps1'
    if (Test-Path $syncPub) { & $syncPub -Quiet:$Quiet }
} catch { Write-Host "(public mirror sync skipped: $($_.Exception.Message))" }

exit 0
