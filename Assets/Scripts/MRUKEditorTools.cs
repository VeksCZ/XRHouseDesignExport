#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using UnityEditor;
using UnityEngine;

public static class MRUKEditorTools {
    [MenuItem("MRUK/1. Full Build and Install APK", false, 10)]
    public static void BuildAndInstallFull() { 
        string apk = BuildInternal(BuildOptions.None); 
        if(apk != null) {
            if (InstallAPK(apk)) {
                Debug.Log("<color=green>ALL DONE: Build and Installation successful.</color>");
            } else {
                Debug.LogWarning("<color=orange>PARTIAL SUCCESS: Build successful, but Installation failed (is Quest connected?).</color>");
            }
        } else {
            Debug.LogError("<color=red>ERROR: Build failed, installation cancelled.</color>");
        }
    }

    [MenuItem("MRUK/2. Fast Build and Install APK", false, 11)]
    public static void BuildAPKFast() { 
        string apk = BuildInternal(BuildOptions.Development, fast: true); 
        if(apk != null) {
            if (InstallAPK(apk)) {
                Debug.Log("<color=green>FAST BUILD AND INSTALL DONE.</color>");
            } else {
                Debug.LogWarning("<color=orange>FAST BUILD DONE, but Installation failed.</color>");
            }
        }
    }

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
        if (RunAdb("uninstall com.veks.XRHouseDesignExport")) {
            Debug.Log("<color=green>UNINSTALL COMPLETE.</color>");
        } else {
            Debug.LogError("<color=red>UNINSTALL FAILED.</color>");
        }
    }

    /// <summary>How many export sessions "Delete Old Exports" keeps - on the Quest and in Exports/RoomData.</summary>
    const int KeepExports = 5;   // keep in sync with "(keep 5)" in the menu item name below (attributes can't format an int)
    const string RemoteExportDir = "/sdcard/Download/XRHouseExports";

    /// <summary>Pulls every export session (and the saved scans) from the Quest. Use "7. Delete Old Exports" first
    /// to trim what's there. Each pulled export's scan JSON is also copied into Exports/ScanCache so it shows up
    /// as a scan source in the Editor.</summary>
    [MenuItem("MRUK/5. Pull data from Quest", false, 30)]
    public static void PullFromQuest() {
        string local = Path.GetFullPath("Exports/RoomData");
        Directory.CreateDirectory(local);
        Debug.Log($"<color=cyan>Pulling data from Quest to {local}...</color>");

        // Saved scans live in the app's own storage, not in Downloads.
        string scanLocal = Path.GetFullPath("Exports/ScanCache");
        Directory.CreateDirectory(scanLocal);
        RunAdb($"pull \"/sdcard/Android/data/com.veks.XRHouseDesignExport/files/ScanCache/.\" \"{scanLocal}\"");
        if (RunAdb($"pull \"{RemoteExportDir}/.\" \"{local}\"")) {
            var dirs = Directory.GetDirectories(local, "Export_*");
            foreach (var d in dirs) CopyExportScanToCache(d, scanLocal);
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

    /// <summary>Copies an export's scan JSON into the Editor's ScanCache (named after the export), so the exact
    /// scan behind that export can be picked as a source and re-exported or inspected on the PC.</summary>
    static void CopyExportScanToCache(string exportDir, string scanCache) {
        string src = Path.Combine(exportDir, MRUKPathUtility.DATA_SCENE);
        if (!File.Exists(src)) return;
        string dst = Path.Combine(scanCache, Path.GetFileName(exportDir) + MRUKSceneCache.EXTENSION);
        if (!File.Exists(dst)) File.Copy(src, dst);
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
        var report = BuildPipeline.BuildPlayer(new[] { "Assets/Scenes/MRUKExportScene.unity" }, apk, BuildTarget.Android, o);
        if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded) {
            Debug.Log($"<color=green>BUILD COMPLETED SUCCESSFULLY</color> in {report.summary.totalTime.TotalSeconds:0} s.");
            return apk;
        }
        Debug.LogError("<color=red>BUILD FAILED!</color> Check Console for details.");
        return null;
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
