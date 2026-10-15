# Quest build + install + run (BuildQuest.BuildAndRun in the main project). Prints QUEST OK / QUEST FAILED.
$m = (Resolve-Path "$PSScriptRoot\..\..").Path
$u = 'C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe'
if (Get-Process Unity -ErrorAction SilentlyContinue) { "QUEST FAILED (Unity is running)"; exit 1 }
$log = "$m\Logs\quest_build.log"
Remove-Item $log -ErrorAction SilentlyContinue
$p = Start-Process $u -PassThru -WindowStyle Hidden -ArgumentList '-batchmode','-quit','-projectPath',"`"$m`"",'-buildTarget','Android','-executeMethod','BuildQuest.BuildAndRun','-logFile',"`"$log`""
$p.WaitForExit()
if (Select-String -Path $log -Pattern '\[BuildQuest\] Succeeded' -Quiet) { "QUEST OK $(Get-Date -Format HH:mm:ss)" }
else { "QUEST FAILED"; Select-String -Path $log -Pattern 'error CS|ADB Error|BuildQuest\]' | Select -Last 6 | % { $_.Line.Substring(0, [Math]::Min(240, $_.Line.Length)) } }
