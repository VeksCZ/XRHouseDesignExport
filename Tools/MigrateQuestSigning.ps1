# One-time switch of the installed app from the debug key to the release key (see MRUKEditorTools release signing).
# Android won't update an app signed with another key, and uninstalling it deletes its data - so this backs the data
# up, uninstalls, installs the release-signed build and puts the data back.
#
#   powershell -ExecutionPolicy Bypass -File Tools\MigrateQuestSigning.ps1
#
# Copied back: ScanCache (saved scans, edits, furniture), Routes, WallPhotos. Not kept: settings (menu tab, panel
# positions...) and logs. The backup stays in Exports\QuestBackup_<time>_signing.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$adb = "C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
$pkg = "cz.veks.XRHouseDesignExport"
$files = "/sdcard/Android/data/$pkg/files"
$apk = Join-Path $root "Builds\XRHouseExporter.apk"
$info = Join-Path $root "Builds\XRHouseExporter.build.txt"
if (-not (Test-Path $info) -or -not ((Get-Content $info) -contains "releaseSigned=1")) { throw "Build with the release key first." }
if (-not ((& $adb devices) -match "\tdevice$")) { throw "No Quest connected over USB." }

$backup = Join-Path $root ("Exports\QuestBackup_" + (Get-Date -Format yyyyMMdd_HHmm) + "_signing")
New-Item -ItemType Directory $backup | Out-Null
foreach ($d in "ScanCache", "Routes", "WallPhotos") {
    & $adb shell "ls $files/$d" *> $null
    if ($LASTEXITCODE -eq 0) { & $adb pull "$files/$d" $backup | Out-Null; if ($LASTEXITCODE -ne 0) { throw "Backup of $d failed - nothing uninstalled." } }
}
$n = (Get-ChildItem $backup -Recurse -File | Measure-Object).Count
Write-Host "Backed up $n files to $backup"
if ($n -eq 0) { throw "Backup is empty - stopping before uninstall." }

& $adb uninstall $pkg
& $adb install $apk
if ($LASTEXITCODE -ne 0) { throw "Install failed - the data is in $backup" }
# The app's own folder exists only after its first start (the shell can't create it), and whole folders don't push
# into it - start it once, stop it, then make the folders and push file by file.
& $adb shell "monkey -p $pkg -c android.intent.category.LAUNCHER 1" | Out-Null
Start-Sleep 20
& $adb shell "am force-stop $pkg"
$dirs = Get-ChildItem $backup -Recurse -Directory | ForEach-Object { "'$files/" + $_.FullName.Substring($backup.Length + 1).Replace('\', '/') + "'" }
if ($dirs) { & $adb shell ("mkdir -p " + ($dirs -join ' ')) }
$failed = 0
foreach ($x in Get-ChildItem $backup -Recurse -File) {
    $rel = $x.FullName.Substring($backup.Length + 1).Replace('\', '/')
    & $adb push $x.FullName "$files/$rel" *> $null
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
if ($failed) { throw "$failed file(s) not restored - they are in $backup" }
# What adb pushed belongs to 'shell' (rwxrws---): the app could not even list those folders. Open them up to it.
& $adb shell "find '$files' -user shell -type d -exec chmod 777 {} + ; find '$files' -user shell -type f -exec chmod 666 {} +"
& $adb shell "ls $files"
Write-Host "Done - start the app (it asks for its permissions again)."
