param([string]$Test, [string]$Scan = 'Scan_20261002_0848_koupelna_wc', [string]$Extra = '')
# Runs one scripted test of the desktop app (Builds\WindowsTest, see DesktopWalkTests.cs); screenshots go to
# %TEMP%\xrh_tests\<test>, the log to %TEMP%\xrh_tests\<test>.log.
$m = (Resolve-Path "$PSScriptRoot\..\..").Path
$exe = "$m\Builds\WindowsTest\XRHouseWalk.exe"
$out = "$env:TEMP\xrh_tests\$Test"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
$log = "$env:TEMP\xrh_tests\$Test.log"
$p = Start-Process $exe -PassThru -WorkingDirectory (Split-Path $exe) -ArgumentList "-scan $Scan -test $Test -out `"$out`" -logFile `"$log`" -screen-fullscreen 0 -screen-width 1600 -screen-height 900 $Extra"
if (-not $p.WaitForExit(120000)) { $p.Kill(); "TIMEOUT" }
"exit $($p.ExitCode)"
Select-String -Path $log -Pattern '^(TEST|TRACE)|Exception' | % { $_.Line.Substring(0, [Math]::Min(240, $_.Line.Length)) }
Get-ChildItem $out -Filter *.png | % Name
