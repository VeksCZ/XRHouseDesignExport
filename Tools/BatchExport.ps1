# Runs the real export pipeline on a saved scan (MRUK .scene.json) without a headset or an open Editor.
# Usage:  .\Tools\BatchExport.ps1 [-Scan <export folder, or a .scene.json>]
#         default: the newest scan in Exports\RoomData (any folder holding a 92_Data_Scan.scene.json)
# Output: Exports\RoomData\Export_<time>_<scan>\ next to the other exports, incl. the scan's edits and room names.
#         The Unity log is Logs\BatchExport_unity.log.
# The Unity Editor must be closed - batch mode can't open a project that is already open.
param([string]$Scan, [string]$Unity = "C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe", [int]$TimeoutSec = 600)

$project = Split-Path -Parent $PSScriptRoot
$exports = Join-Path $project "Exports\RoomData"
$logs = Join-Path $project "Logs"
New-Item -ItemType Directory -Force $exports, $logs | Out-Null
if (-not $Scan) {
    $Scan = (Get-ChildItem $exports -Directory | ForEach-Object { Get-Item (Join-Path $_.FullName "92_Data_Scan.scene.json") -ErrorAction SilentlyContinue } |
             Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
} elseif (Test-Path $Scan -PathType Container) {
    $Scan = Join-Path $Scan "92_Data_Scan.scene.json"
}
if (-not $Scan -or -not (Test-Path $Scan)) { Write-Error "Scan not found: $Scan"; exit 1 }

$running = Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" | Where-Object { $_.CommandLine -like "*$project*" -and -not (Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue).HasExited }
if ($running) { Write-Error "Unity already has this project open (PID $($running.ProcessId -join ', ')) - close it first."; exit 1 }

$env:XR_SCAN_JSON = (Resolve-Path $Scan).Path
$log = Join-Path $logs "BatchExport_unity.log"
Remove-Item $log -ErrorAction SilentlyContinue
Write-Host "Exporting $env:XR_SCAN_JSON ..."
$p = Start-Process -PassThru -FilePath $Unity -ArgumentList @(
    '-batchmode', '-nographics', '-projectPath', "`"$project`"", '-runTests', '-testPlatform', 'PlayMode',
    '-testFilter', 'ScanExportTests', '-testResults', "`"$logs\BatchExport_results.xml`"", '-logFile', "`"$log`"")

# Unity sometimes hangs on shutdown after the test run (XR plugins) - stop waiting once the run has completed.
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not $p.HasExited -and $sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
    Start-Sleep 2
    if ((Test-Path $log) -and (Select-String $log -Pattern 'Test run completed' -Quiet)) {
        if (-not $p.WaitForExit(15000)) { Stop-Process -Id $p.Id -Force }
        break
    }
}
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; Write-Host "Timed out after $TimeoutSec s" -ForegroundColor Red }

$done = Select-String $log -Pattern '\[BatchExport\] DONE (.+)$' | Select-Object -Last 1
if ($done) { Write-Host "OK: $($done.Matches[0].Groups[1].Value)" -ForegroundColor Green; exit 0 }
Select-String $log -Pattern 'error CS|Exception|Failed' | Select-Object -First 15 | ForEach-Object Line
Write-Host "Export FAILED - see $log" -ForegroundColor Red
exit 1
