#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The one Quest build path (menu MRUK, MRUKExporter.EditorFastBuildAndInstall, and BuildQuest for batch mode):
/// build -> adb install -> start the app on the headset.
/// </summary>
public static class MRUKEditorTools {
    /// <summary>The Android package id from Player Settings - nothing else hardcodes it.</summary>
    public static string PackageId => PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android);
    static string RemoteAppFiles => $"/sdcard/Android/data/{PackageId}/files";

    [MenuItem("MRUK/1. Full Build and Install APK", false, 10)]
    public static void BuildAndInstallFullMenu() => BuildAndInstallFull();

    public static bool BuildAndInstallFull() { 
        string apk = BuildInternal(BuildOptions.None); 
        if(apk == null) {
            Debug.LogError("<color=red>ERROR: Build failed, installation cancelled.</color>");
            return false;
        }
        if (InstallAPK(apk) && LaunchApp()) {
            Debug.Log("<color=green>ALL DONE: Build and Installation successful.</color>");
            return true;
        }
        Debug.LogWarning("<color=orange>PARTIAL SUCCESS: Build successful, but Installation failed (is Quest connected?).</color>");
        return false;
    }

    [MenuItem("MRUK/2. Fast Build and Install APK", false, 11)]
    public static void BuildAPKFastMenu() => BuildAPKFast();

    public static bool BuildAPKFast() { 
        string apk = BuildInternal(BuildOptions.Development, fast: true); 
        if(apk == null) return false;
        if (InstallAPK(apk) && LaunchApp()) {
            Debug.Log("<color=green>FAST BUILD AND INSTALL DONE.</color>");
            return true;
        }
        Debug.LogWarning("<color=orange>FAST BUILD DONE, but Installation failed.</color>");
        return false;
    }

    /// <summary>Starts the installed app on the headset (its launcher activity).</summary>
    public static bool LaunchApp() => RunAdb($"shell monkey -p {PackageId} -c android.intent.category.LAUNCHER 1");

    [MenuItem("MRUK/3. Install APK Only", false, 12)]
    public static void InstallOnly() { 
        string apk = "Builds/XRHouseExporter.apk";
        if (File.Exists(apk)) {
            var info = new FileInfo(apk);
            Debug.Log($"<color=cyan>Installing APK built on: {info.LastWriteTime:dd.MM. HH:mm:ss} (Size: {info.Length/1024/1024:F1} MB)</color>");
            if (InstallAPK(apk)) {
                Debug.Log("<color=green>INSTALLATION SUCCESSFUL.</color>");
            }
        } else {
            string msg = "APK file not found at Builds/XRHouseExporter.apk. Please run Build first.";
            Debug.LogError(msg);
            EditorUtility.DisplayDialog("Error", msg, "OK");
        }
    }

    [MenuItem("MRUK/4. Uninstall App", false, 13)]
    public static void UninstallAPK() { 
        Debug.Log("<color=orange>Uninstalling application...</color>");
        if (RunAdb($"uninstall {PackageId}")) {
            Debug.Log("<color=green>UNINSTALL COMPLETE.</color>");
        } else {
            Debug.LogError("<color=red>UNINSTALL FAILED.</color>");
        }
    }

    /// <summary>How many export sessions "Delete Old Exports" keeps - on the Quest and in Exports/RoomData.</summary>
    const int KeepExports = 5;   // keep in sync with "(keep 5)" in the menu item name below (attributes can't format an int)
    const string RemoteExportDir = "/sdcard/Download/XRHouseExports";

    /// <summary>Pulls every export session and the saved scans from the Quest into Exports/RoomData - the one local
    /// folder; every export there (it carries its scan JSON, edits and room names) is a scan source in the Editor.
    /// Saved scans (the app's own ScanCache) arrive as Scan_&lt;name&gt; folders in the same layout. Use "7. Delete Old
    /// Exports" first to trim what's there.</summary>
    [MenuItem("MRUK/5. Pull data from Quest", false, 30)]
    public static void PullFromQuest() {
        string local = Path.GetFullPath(MRUKPathUtility.GetExportRoot());
        Directory.CreateDirectory(local);
        Debug.Log($"<color=cyan>Pulling data from Quest to {local}...</color>");

        // Session logs used to be written into the export folder (and got pulled with it, hundreds of MB); builds
        // now keep them in the app's storage - remove what older builds left there before pulling.
        RunAdb($"shell rm -rf \"{RemoteExportDir}/Logs\" \"{RemoteExportDir}/session_debug_log.txt\"");
        PullSavedScans(local);
        PullWallPhotos(local);
        SyncRoutes(local);
        if (RunAdb($"pull \"{RemoteExportDir}/.\" \"{local}\"")) {
            var dirs = Directory.GetDirectories(local, "Export_*");
            if (dirs.Length > 0) {
                var latest = dirs.OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).First();
                Debug.Log($"<color=green>DOWNLOADED: Opening {Path.GetFileName(latest)}</color>");
                EditorUtility.RevealInFinder(latest);
            } else {
                Debug.LogWarning("No exports were found in the downloaded folder.");
                EditorUtility.RevealInFinder(local);
            }
        } else {
            Debug.LogError("<color=red>PULL FAILED: Could not download data from Quest.</color>");
        }
    }

    [MenuItem("MRUK/6. Run Export (in Play Mode)", false, 31)]
    public static async void ExportFromMenu() {
        var exp = UnityEngine.Object.FindAnyObjectByType<MRUKExporter>();
        if (exp != null) await exp.ExportAllRooms();
        else Debug.LogError("MRUKExporter not found in scene.");
    }

    /// <summary>
    /// Keeps only the newest KeepExports export sessions (by the timestamp in the folder name), both on the Quest
    /// and in Exports/RoomData; with KeepExports or fewer nothing is deleted. Deletes on the Quest over ADB: the
    /// in-headset "Delete exports" button can't remove sessions written by an earlier install of the APK (Android
    /// scoped storage only lets an app delete files it created itself, and every reinstall counts as a new app),
    /// so those used to stay in Download/XRHouseExports forever and came back on every pull.
    /// </summary>
    [MenuItem("MRUK/7. Delete Old Exports (keep 5)", false, 40)]
    public static void DeleteOldExportsMenu() {
        if (DeleteOldExportsOnQuest(KeepExports) == null)
            Debug.LogError("<color=red>Could not list exports on Quest (is it connected?) - nothing deleted there.</color>");
        int removedLocal = TrimLocalExports(Path.GetFullPath("Exports/RoomData"), KeepExports);
        Debug.Log($"<color=green>PC: removed {removedLocal} older export(s), kept the newest {KeepExports}.</color>");
    }

    /// <summary>
    /// Deletes every export session on the Quest except the newest 'keep' ones (by the timestamp in the folder name,
    /// Export_yyyyMMdd_HHmmss[_scan], which sorts correctly as plain text). Returns the kept folder names, newest
    /// first, or null if the Quest couldn't be listed.
    /// </summary>
    static System.Collections.Generic.List<string> DeleteOldExportsOnQuest(int keep) {
        if (!RunAdb($"shell ls -1 \"{RemoteExportDir}\"", out string listing)) return null;

        var rx = new System.Text.RegularExpressions.Regex(@"^Export_\d{8}_\d{6}");
        var folders = listing.Split('\n').Select(s => s.Trim()).Where(s => rx.IsMatch(s))
            .OrderByDescending(s => s, StringComparer.Ordinal).ToList();

        int deleted = 0, failed = 0;
        foreach (var old in folders.Skip(keep)) {
            if (RunAdb($"shell rm -rf \"{RemoteExportDir}/{old}\"")) deleted++; else failed++;
        }
        // Loose files left in the export root by very old builds.
        RunAdb($"shell rm -f \"{RemoteExportDir}/session_debug_log.txt\"");

        string msg = $"Quest: kept {Math.Min(keep, folders.Count)} newest export(s), deleted {deleted} older one(s)";
        if (failed > 0) Debug.LogWarning($"<color=orange>{msg}, {failed} could not be deleted.</color>");
        else Debug.Log($"<color=green>{msg}.</color>");
        return folders.Take(keep).ToList();
    }

    /// <summary>Deletes all but the newest 'keep' Export_* folders in the local export folder. Returns how many were removed.</summary>
    static int TrimLocalExports(string local, int keep) {
        int removed = 0;
        var dirs = Directory.GetDirectories(local, "Export_*")
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).Skip(keep);
        foreach (var d in dirs) {
            try { Directory.Delete(d, true); removed++; }
            catch (Exception ex) { Debug.LogWarning($"Could not delete local export '{d}': {ex.Message}"); }
        }
        return removed;
    }

    /// <summary>
    /// Copies the headset's saved scans (app storage: Name.scene.json, Name.edits.json, room_names.json) into
    /// 'exportRoot'/Scan_&lt;Name&gt;/ as 92_Data_Scan.scene.json, 93_Data_Edits.json and 94_Data_RoomNames.json - the
    /// same layout as an export, so the Editor treats them like any other scan source. Overwrites on every pull (the
    /// edits may have changed on the headset); goes through a temp folder that is removed afterwards.
    /// </summary>
    static void PullSavedScans(string exportRoot) {
        string tmp = Path.GetFullPath("Temp/QuestScanCache");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        try {
            if (!RunAdb($"pull \"{RemoteAppFiles}/ScanCache/.\" \"{tmp}\"")) return;
            string names = Path.Combine(tmp, "room_names.json");
            int n = 0;
            foreach (var scan in Directory.GetFiles(tmp, "*" + MRUKSceneCache.EXTENSION)) {
                string name = Path.GetFileName(scan).Substring(0, Path.GetFileName(scan).Length - MRUKSceneCache.EXTENSION.Length);
                string dir = Path.Combine(exportRoot, MRUKSceneCache.ScanFolderPrefix + name);
                Directory.CreateDirectory(dir);
                File.Copy(scan, Path.Combine(dir, MRUKPathUtility.DATA_SCENE), true);
                string edits = Path.Combine(tmp, name + HouseEditsStore.EXTENSION);
                if (File.Exists(edits)) File.Copy(edits, Path.Combine(dir, MRUKPathUtility.DATA_EDITS), true);
                if (File.Exists(names)) File.Copy(names, Path.Combine(dir, MRUKPathUtility.DATA_ROOM_NAMES), true);
                n++;
            }
            Debug.Log($"<color=green>Saved scans: {n} pulled into {MRUKSceneCache.ScanFolderPrefix}* folders.</color>");
        } finally {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>Wall photo sessions (WallPhotoCapture: persistentDataPath/WallPhotos/Photos_yyyyMMdd with JPGs, their
    /// pose sidecars and a scan snapshot) arrive as Photos_yyyyMMdd folders - each one a scan source of its own.</summary>
    static void PullWallPhotos(string exportRoot) {
        if (!RunAdb($"shell ls -1 \"{RemoteAppFiles}/{WallPhotoCapture.RootFolder}\"", out string listing)) return;
        int n = 0;
        foreach (var name in listing.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(WallPhotoCapture.SessionPrefix))) {
            string dir = Path.Combine(exportRoot, name);
            Directory.CreateDirectory(dir);
            if (RunAdb($"pull \"{RemoteAppFiles}/{WallPhotoCapture.RootFolder}/{name}/.\" \"{dir}\"")) n++;
            else Debug.LogError($"<color=red>Could not pull wall photos {name}.</color>");
        }
        if (n > 0) Debug.Log($"<color=green>Wall photos: {n} session folder(s) pulled ({WallPhotoCapture.SessionPrefix}*).</color>");
    }

    /// <summary>Traced routes (RouteStore, one file per house): the headset's and the PC's are merged by route id and the
    /// result written to both, so routes drawn on either side end up everywhere.</summary>
    static void SyncRoutes(string exportRoot) {
        string remote = $"{RemoteAppFiles}/{RouteStore.Folder}/{RouteStore.FileName}";
        string local = Path.Combine(exportRoot, RouteStore.Folder, RouteStore.FileName);
        string tmp = Path.Combine(Path.GetTempPath(), "quest_routes.json");
        if (File.Exists(tmp)) File.Delete(tmp);
        bool onQuest = RunAdb($"shell ls \"{remote}\"", out string ls) && !ls.Contains("No such file") && RunAdb($"pull \"{remote}\" \"{tmp}\"");
        var quest = onQuest ? RouteStore.Read(tmp) : null;
        var pc = RouteStore.Read(local);
        if (quest == null && pc == null) return;
        var merged = RouteStore.Merge(pc, quest);
        Directory.CreateDirectory(Path.GetDirectoryName(local));
        File.WriteAllText(local, JsonUtility.ToJson(merged, true));
        RunAdb($"shell mkdir -p \"{RemoteAppFiles}/{RouteStore.Folder}\"");
        RunAdb($"push \"{local}\" \"{remote}\"");
        Debug.Log($"<color=green>Routes: {merged.routes.Count} (Quest {quest?.routes.Count ?? 0}, PC {pc?.routes.Count ?? 0}) - merged into {local} and back to the Quest.</color>");
    }

    /// <summary>Session logs live in the app's own storage on the headset (not in the exports); fetches them into
    /// the project's Logs/Quest folder when they are actually needed.</summary>
    [MenuItem("MRUK/5b. Pull logs from Quest", false, 32)]
    public static void PullLogsFromQuest() {
        string local = Path.GetFullPath("Logs/Quest");
        Directory.CreateDirectory(local);
        if (RunAdb($"pull \"{RemoteAppFiles}/Logs/.\" \"{local}\"")) EditorUtility.RevealInFinder(local);
        else Debug.LogError("<color=red>Could not pull logs (is the Quest connected?).</color>");
    }

    [MenuItem("MRUK/8. Open Exports Folder", false, 50)]
    public static void OpenExportsFolder() {
        string path = Path.GetFullPath("Exports/RoomData");
        Directory.CreateDirectory(path);
        EditorUtility.RevealInFinder(path);
    }

    [MenuItem("MRUK/9. Backup Project (Zipped)", false, 100)]
    public static void BackupProject() {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        string zipPath = $"Backups/XRHouse_Backup_{timestamp}.zip";
        Directory.CreateDirectory("Backups");
        try {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) {
                AddFolderToZip(zip, "Assets");
                AddFolderToZip(zip, "Packages");
                AddFolderToZip(zip, "ProjectSettings");
            }
            Debug.Log($"<color=green>BACKUP DONE:</color> {zipPath}");
            EditorUtility.RevealInFinder(zipPath);
        } catch (Exception ex) { Debug.LogError("Backup failed: " + ex.Message); }
    }

    private static void AddFolderToZip(ZipArchive zip, string folder) {
        if (!Directory.Exists(folder)) return;
        foreach (string file in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)) {
            if (file.EndsWith(".lock") || file.Contains(".TMP")) continue;
            zip.CreateEntryFromFile(file, file.Replace("\\", "/"));
        }
    }

    /// <param name="fast">
    /// Iteration build: same optimised IL2CPP settings as the full build (an unoptimised "Debug" IL2CPP
    /// build runs the app several times slower on the headset, and switching configurations throws the
    /// whole incremental C++ cache away) - it only packages a symbol table instead of full debug symbols
    /// (Diagnostics Data needs at least a symbol table, so turning symbols off completely would warn on every build).
    /// </param>
    public static string BuildInternal(BuildOptions o, bool fast = false) {
        // Never alongside the Windows build (shared Assets - see DesktopBuildTools); a marker older than 30 min is
        // a leftover from a killed build and is ignored.
        var marker = new FileInfo(Path.Combine("Builds/Windows", MRUKPathUtility.DesktopBuildMarker));
        if (marker.Exists && (DateTime.Now - marker.LastWriteTime).TotalMinutes < 30) {
            Debug.LogError("<color=red>BUILD FAILED: a Windows desktop build is running - wait for it to finish.</color>");
            return null;
        }
        Debug.Log($"<color=cyan>Starting APK Build ({(fast ? "fast" : "full")})...</color>");

        SetDebugSymbols(fast ? "SymbolTable" : "Full");
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

        // SideQuest (and Android sideloading generally) only offers an update over an already-installed APK if
        // its version code is higher than the installed one - bump it on every build so a real release is
        // never accidentally shipped with a code that's already out there. PlayerSettings.bundleVersion (the
        // human-readable "1.1.0" string) is left alone; that's a deliberate call, not something to automate.
        PlayerSettings.Android.bundleVersionCode++;
        Debug.Log($"<color=cyan>Android version code: {PlayerSettings.Android.bundleVersionCode}</color>");

        // Build timestamp for VersionDisplay.BuildTime - a data file, so no script recompile is triggered.
        Directory.CreateDirectory("Assets/Resources");
        File.WriteAllText("Assets/Resources/BuildInfo.txt", DateTime.Now.ToString("dd.MM. HH:mm:ss"));
        AssetDatabase.Refresh();

        string apk = "Builds/XRHouseExporter.apk"; Directory.CreateDirectory("Builds");
        bool signed = ApplyReleaseSigning();
        UnityEditor.Build.Reporting.BuildReport report;
        try { report = BuildPipeline.BuildPlayer(new[] { "Assets/Scenes/MRUKExportScene.unity" }, apk, BuildTarget.Android, o); }
        finally { if (signed) ClearReleaseSigning(); }
        if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded) {
            // what was built - Tools/UploadQuest.ps1 only uploads a release-signed non-development build
            File.WriteAllText("Builds/XRHouseExporter.build.txt",
                $"versionCode={PlayerSettings.Android.bundleVersionCode}\nversion={PlayerSettings.bundleVersion}\n" +
                $"development={((o & BuildOptions.Development) != 0 ? 1 : 0)}\nreleaseSigned={(signed ? 1 : 0)}\nbuilt={DateTime.Now:s}\n");
            Debug.Log($"<color=green>BUILD COMPLETED SUCCESSFULLY</color> in {report.summary.totalTime.TotalSeconds:0} s.");
            return apk;
        }
        Debug.LogError("<color=red>BUILD FAILED!</color> Check Console for details.");
        return null;
    }

    // ---------- release signing ----------
    // Every build (also the fast development ones) is signed with the app's own release key, so a build installed over
    // USB and the one the Meta Horizon release channel installs are the same app and update each other - with the
    // debug key Android would refuse the update (different signature) and only an uninstall, which wipes the app's
    // data (saved scans, routes, photos), would help. The key and its password live outside the project:
    // %USERPROFILE%\.xrhouse\signing.json {"keystore","alias","password"} - never in ProjectSettings or git.
    // Losing the key means the store app can never be updated again: keep a backup of that folder.

    static string SigningFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xrhouse", "signing.json");

    [Serializable] class SigningInfo { public string keystore, alias, password; }

    static bool ApplyReleaseSigning() {
        try {
            if (!File.Exists(SigningFile)) { Debug.LogWarning("Release signing: " + SigningFile + " not found - signed with the debug key."); return false; }
            var s = JsonUtility.FromJson<SigningInfo>(File.ReadAllText(SigningFile));
            if (s == null || string.IsNullOrEmpty(s.keystore) || !File.Exists(s.keystore)) { Debug.LogWarning("Release signing: keystore missing - signed with the debug key."); return false; }
            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = s.keystore;
            PlayerSettings.Android.keystorePass = s.password;
            PlayerSettings.Android.keyaliasName = s.alias;
            PlayerSettings.Android.keyaliasPass = s.password;
            Debug.Log("<color=cyan>Release signing: " + Path.GetFileName(s.keystore) + " / " + s.alias + "</color>");
            return true;
        } catch (Exception e) { Debug.LogWarning("Release signing failed: " + e.Message + " - signed with the debug key."); return false; }
    }

    // back to the default after the build, so neither the key's path nor its use ends up in ProjectSettings.asset
    static void ClearReleaseSigning() {
        PlayerSettings.Android.useCustomKeystore = false;
        PlayerSettings.Android.keystoreName = "";
        PlayerSettings.Android.keystorePass = "";
        PlayerSettings.Android.keyaliasName = "";
        PlayerSettings.Android.keyaliasPass = "";
    }

    // Android debug symbol level lives in UnityEditor.Android.UserBuildSettings.DebugSymbols, a type that only exists
    // with the Android module installed - reflection keeps the editor tools compiling without it. The enum type is taken
    // from the property itself so the exact enum name does not matter.
    static void SetDebugSymbols(string level) {
        try {
            var userBuildSettings = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEditor.Android.UserBuildSettings", false))
                .FirstOrDefault(t => t != null);
            var levelProp = userBuildSettings?.GetNestedType("DebugSymbols")?.GetProperty("level");
            if (levelProp == null) throw new MissingMemberException("UserBuildSettings.DebugSymbols.level");
            levelProp.SetValue(null, Enum.Parse(levelProp.PropertyType, level));
            Debug.Log($"<color=green>Android Debug Symbols level set to {levelProp.GetValue(null)}.</color>");
        } catch (Exception ex) {
            Debug.LogWarning($"Could not set the Android debug symbol level via UserBuildSettings ({ex.Message}); using the legacy setting.");
    #pragma warning disable 0618
            EditorUserBuildSettings.androidCreateSymbols = level == "Full" ? AndroidCreateSymbols.Debugging : AndroidCreateSymbols.Public;
    #pragma warning restore 0618
        }
    }

    public static bool InstallAPK(string apk) { 
        Debug.Log("<color=cyan>Installing APK to Quest...</color>");
        return RunAdb("install -r \"" + Path.GetFullPath(apk) + "\""); 
    }

    public static bool RunAdb(string args) => RunAdb(args, out _);

    public static bool RunAdb(string args, out string output) {
        output = "";
        string sdk = EditorPrefs.GetString("AndroidSdkRoot");
        if(string.IsNullOrEmpty(sdk)) sdk = Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines/AndroidPlayer/SDK");
        string adb = Path.Combine(sdk, "platform-tools", "adb" + (Application.platform == RuntimePlatform.WindowsEditor ? ".exe" : ""));

        if (File.Exists(adb)) {
            // A headset reachable both by USB and by wireless ADB (adb tcpip) shows up as two devices for the
            // same physical unit - a plain "adb install"/"adb uninstall" with no explicit target then refuses
            // to guess ("more than one device/emulator") instead of picking one, failing the whole fast-build's
            // install step even though the build itself succeeded.
            args = GetDeviceArg(adb) + args;
            var info = new System.Diagnostics.ProcessStartInfo(adb, args) {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            var process = System.Diagnostics.Process.Start(info);
            output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0) {
                Debug.LogError($"ADB Error ({process.ExitCode}): {error}");
                return false;
            } else {
                Debug.Log($"ADB Success: {output}");
                return true;
            }
        } else {
            Debug.LogError("ADB executable not found. Please check Android SDK path in Preferences.");
            EditorUtility.DisplayDialog("ADB Not Found", "Could not find adb.exe. Please check Android SDK path.", "OK");
            return false;
        }
    }

    /// <summary>
    /// "" if zero or one device is connected (nothing to disambiguate); otherwise "-s &lt;serial&gt; " for a
    /// specific one - a USB serial (no ':') is preferred over a Wi-Fi one (host:port), since a wireless ADB
    /// link can be slower/less reliable for pushing a whole APK.
    /// </summary>
    static string GetDeviceArg(string adb) {
        try {
            var info = new System.Diagnostics.ProcessStartInfo(adb, "devices") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            };
            var process = System.Diagnostics.Process.Start(info);
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            var serials = output.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.EndsWith("\tdevice"))
                .Select(l => l.Split('\t')[0])
                .ToList();
            if (serials.Count <= 1) return "";

            string chosen = serials.FirstOrDefault(s => !s.Contains(':')) ?? serials[0];
            Debug.Log($"<color=cyan>Multiple ADB devices connected ({string.Join(", ", serials)}) - using {chosen}.</color>");
            return "-s " + chosen + " ";
        } catch (Exception ex) {
            Debug.LogWarning($"Could not list ADB devices to disambiguate ({ex.Message}); proceeding without -s.");
            return "";
        }
    }
}
#endif
