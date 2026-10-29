# Uploads the last Quest build (Builds\XRHouseExporter.apk) to the app's Meta Horizon release channel, so the headset
# updates itself like any app from the Store - no cable, no PC needed on the headset's side.
#
#   powershell -ExecutionPolicy Bypass -File Tools\UploadQuest.ps1 [-Channel ALPHA] [-Notes "text"]
#
# Needs (all outside the project, never in git):
#   %USERPROFILE%\.xrhouse\meta.json      {"appId": "<App ID>", "token": "<platform utility token>"}
#       App ID: Developer Dashboard > your app > Development > API.
#       Token:  Developer Dashboard > Account > Account settings > Platform utility token > Generate token.
#   ovr-platform-util.exe                 in %USERPROFILE%\.xrhouse\ (or on PATH) - Meta's Platform Utility CLI.
# The APK must be a full (non-development) build signed with the release key (MRUKEditorTools: build.txt says so).
param([string]$Channel = "ALPHA", [string]$Notes = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$cfgDir = Join-Path $env:USERPROFILE ".xrhouse"
$apk = Join-Path $root "Builds\XRHouseExporter.apk"
$info = Join-Path $root "Builds\XRHouseExporter.build.txt"

$util = Join-Path $cfgDir "ovr-platform-util.exe"
if (-not (Test-Path $util)) { $cmd = Get-Command ovr-platform-util.exe -ErrorAction SilentlyContinue; if ($cmd) { $util = $cmd.Source } }
if (-not (Test-Path $util)) { throw "ovr-platform-util.exe not found (put it into $cfgDir)." }
$metaFile = Join-Path $cfgDir "meta.json"
if (-not (Test-Path $metaFile)) { throw "$metaFile missing - {""appId"": ""..."", ""token"": ""...""}" }
$meta = Get-Content $metaFile -Raw | ConvertFrom-Json
if (-not $meta.appId -or -not $meta.token) { throw "$metaFile needs appId and token." }

if (-not (Test-Path $apk) -or -not (Test-Path $info)) { throw "No build - run the Quest build first." }
$b = @{}; Get-Content $info | ForEach-Object { $k, $v = $_ -split "=", 2; if ($k) { $b[$k] = $v } }
if ($b["development"] -ne "0") { throw "The last build is a development build - the Store takes only a full build (BuildQuest.Build)." }
if ($b["releaseSigned"] -ne "1") { throw "The last build isn't signed with the release key (%USERPROFILE%\.xrhouse\signing.json)." }
if ((Get-Item $apk).LastWriteTime -gt ([datetime]$b["built"]).AddMinutes(5)) { throw "The APK is newer than its build info - rebuild." }

if (-not $Notes) {
    Push-Location $root
    $Notes = "v$($b['version']) ($($b['versionCode'])) " + ((git log -1 --format=%s 2>$null) -join " ")
    Pop-Location
}
Write-Host "Uploading v$($b['version']) code $($b['versionCode']) to $Channel..."
& $util upload-quest-build --app-id $meta.appId --token $meta.token --apk $apk --channel $Channel --age-group TEENS_AND_ADULTS --notes $Notes --disable-progress-bar
if ($LASTEXITCODE -ne 0) { throw "Upload failed (exit $LASTEXITCODE)." }
Write-Host "Uploaded - the headset updates itself (or: Library > update)."
