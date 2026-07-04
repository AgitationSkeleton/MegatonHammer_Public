param([string]$Rom, [int]$MaxFrames = 2000, [int]$TimeoutSec = 70, [string]$Params = "")
$dir = "$env:TEMP\MegatonHammer"; $log = "$dir\mh_n64_playtest.log"
Remove-Item $log -ErrorAction SilentlyContinue
$ptxt = "$dir\mh_n64_playtest.txt"
if ($Params -ne "") { Set-Content $ptxt $Params -Encoding ascii }
elseif (Test-Path $ptxt) { Rename-Item $ptxt "params.held" -Force }
$env:MH_HEADLESS = "1"; $env:MH_MAXFRAMES = "$MaxFrames"
$exe = "D:\Copilot_OOT\WorkFolders\MegatonHammer\pj64run\Project64.exe"
$p = Start-Process -FilePath $exe -ArgumentList "`"$Rom`"" -PassThru
$ok = $p.WaitForExit($TimeoutSec * 1000)
if (-not $ok) { Stop-Process -Id $p.Id -Force; $verdict = "BACKSTOP-KILLED@${TimeoutSec}s" } else { $verdict = "self-exited" }
Start-Sleep 1
$env:MH_MAXFRAMES = ""
if ($Params -eq "" -and (Test-Path "$dir\params.held")) { Rename-Item "$dir\params.held" "mh_n64_playtest.txt" -Force }
Write-Output "ROM: $(Split-Path $Rom -Leaf)  [$verdict]"
# Report PC/gameMode transitions only
$pc = ""; $gm = ""
Get-Content $log | ForEach-Object {
  if ($_ -match 'alive frame=(\d+) pc=(0x[0-9A-Fa-f]+)') { if ($matches[2] -ne $pc) { Write-Output ("  f{0,-5} PC -> {1}" -f $matches[1], $matches[2]); $pc = $matches[2] } }
  elseif ($_ -match 'hb frame=(\d+) gameMode=(\S+) ?.*entrance=(\S+)') { $key = "$($matches[2])|$($matches[3])"; if ($key -ne $gm) { Write-Output ("  f{0,-5} gameMode={1} entrance={2}" -f $matches[1], $matches[2], $matches[3]); $gm = $key } }
  elseif ($_ -match 'WARP|PlayState|sceneId|MH_MAXFRAMES') { Write-Output "  $_" }
}
$last = (Get-Content $log -Tail 2)[0]
Write-Output "  LAST: $last"
