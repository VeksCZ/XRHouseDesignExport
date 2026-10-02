# Runs the real export pipeline on a saved scan (MRUK .scene.json) without a headset or an open Editor.
# Usage:  .\Tools\BatchExport.ps1 [-Scan <path to .scene.json>]   (default: newest scan in Exports\ScanCache by name)
# Output: Exports\Batch\Export_<time>_<scan>\  (the Unity log is Exports\Batch\unity.log)
# The Unity Editor must be closed - batch mode can't open a project that is already open.
param([string]$Scan, [string]$Unity = "C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe", [int]$TimeoutSec = 600)

$project = Split-Path -Parent $PSScriptRoot
$batch = Join-Path $project "Exports\Batch"
New-Item -ItemType Directory -Force $batch | Out-Null
if (-not $Scan) {
    # Scan names start with yyyyMMdd_HHmm, so the name sorts by capture time (file times change on every pull).
    $Scan = (Get-ChildItem (Join-Path $project "Exports\ScanCache") -Filter *.scene.json |
             Where-Object Name -match '^\d{8}_\d{4}' | Sort-Object Name -Descending | Select-Object -First 1).FullName
}
if (-not $Scan -or -not (Test-Path $Scan)) { Write-Error "Scan not found: $Scan"; exit 1 }

$running = Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" | Where-Object { $_.CommandLine -like "*$project*" -and -not (Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue).HasExited }
if ($running) { Write-Error "Unity already has this project open (PID $($running.ProcessId -join ', ')) - close it first."; exit 1 }

$env:XR_SCAN_JSON = (Resolve-Path $Scan).Path
$log = Join-Path $batch "unity.log"
Remove-Item $log -ErrorAction SilentlyContinue
Write-Host "Exporting $env:XR_SCAN_JSON ..."
$p = Start-Process -PassThru -FilePath $Unity -ArgumentList @(
    '-batchmode', '-nographics', '-projectPath', "`"$project`"", '-runTests', '-testPlatform', 'PlayMode',
    '-testFilter', 'ScanExportTests', '-testResults', "`"$batch\results.xml`"", '-logFile', "`"$log`"")

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
