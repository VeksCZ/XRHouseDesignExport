param([string]$Out = 'WindowsTest')
# Windows app build in the shadow project (XRHouseDesignExport_WinBuild: junctions to Assets/Packages/ProjectSettings,
# its own Library), so the main project's Android Library is never reimported. -Out WindowsTest (scripted tests) or
# Windows (the app you use). Prints BUILD OK / BUILD FAILED.
$m = (Resolve-Path "$PSScriptRoot\..\..").Path
$s = "$m`_WinBuild"
$u = 'C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe'
if (Get-Process Unity -ErrorAction SilentlyContinue) { "BUILD FAILED (Unity is running)"; exit 1 }
if ($Out -eq 'Windows') {
    Get-Process XRHouseWalk -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$m\Builds\Windows\*" } | Stop-Process
    Start-Sleep 2
}
$log = "$m\Logs\desktop_build_$Out.log"
$p = Start-Process $u -PassThru -ArgumentList '-batchmode','-quit','-projectPath',"`"$s`"",'-buildTarget','StandaloneWindows64','-executeMethod','DesktopBuildTools.BuildWindowsBatch','-out',"`"$m\Builds\$Out\XRHouseWalk.exe`"",'-data',"`"$m\Exports\RoomData`"",'-logFile',"`"$log`""
$p.WaitForExit()
if ($p.ExitCode -eq 0) { "BUILD OK $(Get-Date -Format HH:mm:ss)" }
else { Select-String -Path $log -Pattern 'error CS' | Select -First 8 | % { $_.Line.Substring(0, [Math]::Min(240, $_.Line.Length)) }; "BUILD FAILED" }
