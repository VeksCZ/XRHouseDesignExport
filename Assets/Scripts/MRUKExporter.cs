using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
using Meta.XR;
#endif

/// <summary>
/// Scan source management and the export pipeline. A "source" is either the live device scan (only
/// available while inside the scanned space) or a scan previously saved to the local cache; whichever
/// is selected is what export, the dollhouse and the plan viewer all work from.
/// </summary>
public class MRUKExporter : MonoBehaviour
{
    public const string LiveSource = "Live scan";

    public XRMenu uiLog;
    public static string LastReportPath = "";
    public Vector3 LastHouseCenter { get; private set; } = Vector3.zero;

    /// <summary>The source the user picked in the menu.</summary>
    public string SelectedSource { get; private set; } = LiveSource;
    /// <summary>The source whose data MRUK currently holds (null until something was loaded).</summary>
    public string ActiveSource { get; private set; }

    /// <summary>Live scan first, then cached scans, most recently saved first.</summary>
    public List<string> Sources => new[] { LiveSource }.Concat(MRUKSceneCache.ListCachedScans()).ToList();

    public async void OnExportButton() { await ExportAllRooms(uiLog); }

    /// <summary>Moves the selection by +1/-1 through the available sources and returns the new one.</summary>
    public string CycleSource(int direction, XRMenu ui = null)
    {
        var sources = Sources;
        int i = Mathf.Max(0, sources.IndexOf(SelectedSource));
        SelectedSource = sources[((i + direction) % sources.Count + sources.Count) % sources.Count];
        ui?.AddLog($"Scan source: <b>{SelectedSource}</b> ({sources.IndexOf(SelectedSource) + 1}/{sources.Count})");
        return SelectedSource;
    }

    /// <summary>Exports whichever source is selected.</summary>
    public async Task<bool> ExportAllRooms(XRMenu ui = null)
    {
    #if META_XR_SDK_INSTALLED
        SetProgress(0, ui);
        try {
            ui?.AddLog($"<color=cyan>[1/6] Loading '{SelectedSource}'...</color>");
            if (!await EnsureSelectedSourceLoaded(ui, forceReload: true)) return false;

            SetProgress(15, ui);
            ui?.AddLog("<color=cyan>[2/6] Processing rooms...</color>");
            var rooms = MRUKDataProcessor.GetValidRooms(MRUK.Instance);
            ui?.AddLog($"Found {rooms.Count} valid rooms.");
            if (rooms.Count == 0) {
                ui?.AddLog("<color=red>No valid rooms found! Make sure you have completed 'Room Setup' in Quest settings.</color>");
                return false;
            }
            return await RunExportPipeline(rooms, ui);
        } catch (Exception ex) {
            Debug.LogException(ex);
            ui?.AddLog("<color=red>ERROR: " + ex.Message + "</color>");
            return false;
        }
    #else
        await Task.CompletedTask;
        return false;
    #endif
    }

    /// <summary>
    /// Makes MRUK hold the selected source: the live device scene (needs you to be inside the space) or
    /// a cached scan, which loads without any live tracking so it works from anywhere.
    /// </summary>
    public async Task<bool> EnsureSelectedSourceLoaded(XRMenu ui, bool forceReload = false)
    {
    #if META_XR_SDK_INSTALLED
        if (MRUK.Instance == null) {
            ui?.AddLog("<color=red>ERROR: MRUK Instance not found in scene!</color>");
            return false;
        }
        if (!forceReload && ActiveSource == SelectedSource) return true;

        if (SelectedSource == LiveSource) {
            if (!await EnsureLiveSceneLoaded(ui)) return false;
        } else {
            ui?.AddLog($"Loading cached scan '{SelectedSource}'...");
            if (!await MRUKSceneCache.LoadCachedScene(MRUK.Instance, SelectedSource)) {
                ui?.AddLog("<color=red>Failed to load cached scan.</color>");
                return false;
            }
        }
        ActiveSource = SelectedSource;
        return true;
    #else
        await Task.CompletedTask;
        return false;
    #endif
    }

    /// <summary>
    /// Snapshots the live device scan to local storage so it can be exported/viewed later from
    /// anywhere. Always reads the live scene, whatever source is selected.
    /// </summary>
    public async Task<string> SaveScanToCache(XRMenu ui = null, string name = null)
    {
    #if META_XR_SDK_INSTALLED
        try {
            if (MRUK.Instance == null) {
                ui?.AddLog("<color=red>ERROR: MRUK Instance not found in scene!</color>");
                return null;
            }

            ui?.AddLog("<color=cyan>Saving current scan to cache...</color>");
            if (!await EnsureLiveSceneLoaded(ui)) return null;
            ActiveSource = LiveSource;

            var rooms = MRUKDataProcessor.GetValidRooms(MRUK.Instance);
            if (rooms.Count == 0) {
                ui?.AddLog("<color=red>No valid rooms found - nothing to cache.</color>");
                return null;
            }

            name = string.IsNullOrEmpty(name) ? $"{DateTime.Now:yyyyMMdd_HHmm}_{rooms.Count}rooms" : name;
            string saved = MRUKSceneCache.SaveCurrentScene(MRUK.Instance, name);
            if (saved != null)
                ui?.AddLog($"<color=green>Scan cached as '{saved}' ({rooms.Count} room(s)). You can export it later from anywhere.</color>");
            else
                ui?.AddLog("<color=red>Failed to save scan to cache.</color>");
            return saved;
        } catch (Exception ex) {
            Debug.LogException(ex);
            ui?.AddLog("<color=red>ERROR: " + ex.Message + "</color>");
            return null;
        }
    #else
        await Task.CompletedTask;
        return null;
    #endif
    }

    /// <summary>Deletes the currently selected cached scan (the live source isn't a saved file, so there's
    /// nothing to delete for it). Falls back to the live source afterwards since the deleted one no longer exists.</summary>
    public bool DeleteSelectedScan(XRMenu ui = null)
    {
        if (SelectedSource == LiveSource)
        {
            ui?.AddLog("<color=orange>The live scan isn't a saved file - nothing to delete.</color>");
            return false;
        }
        string name = SelectedSource;
        bool ok = MRUKSceneCache.DeleteCachedScan(name);
        ui?.AddLog(ok ? $"<color=green>Deleted cached scan '{name}'.</color>" : $"<color=red>Could not delete '{name}'.</color>");
        if (ok)
        {
            if (ActiveSource == name) ActiveSource = null;
            SelectedSource = LiveSource;
        }
        return ok;
    }

    /// <summary>True if whatever MRUK currently holds contains at least one exportable room.</summary>
    public bool HasValidRooms()
    {
    #if META_XR_SDK_INSTALLED
        return MRUK.Instance != null && MRUKDataProcessor.GetValidRooms(MRUK.Instance).Count > 0;
    #else
        return false;
    #endif
    }

    /// <summary>
    /// All plan sheets of whatever MRUK currently holds: per story an overview, then its rooms - in both the
    /// true measured shape ("Exact") and with small corner jitter cleaned up to real right angles ("Adjusted";
    /// see FloorPlanBuilder.Rectified). The viewer/report offer both rather than picking one, since a floor plan
    /// is meant to show the real shape but a cleaner-looking one is sometimes what you actually want.
    /// </summary>
    public (List<FloorPlanPage> exact, List<FloorPlanPage> adjusted) BuildPlanPages()
    {
    #if META_XR_SDK_INSTALLED
        var outlines = MRUKPlanExtractor.Extract(MRUKDataProcessor.GetValidRooms(MRUK.Instance));
        float yaw = FloorPlanBuilder.CorrectionYaw(outlines);
        var aligned = FloorPlanBuilder.Aligned(outlines, yaw);
        var exact = FloorPlanBuilder.Flatten(FloorPlanBuilder.BuildLevels(aligned));
        var adjusted = FloorPlanBuilder.Flatten(FloorPlanBuilder.BuildLevels(FloorPlanBuilder.Rectified(aligned)));
        return (exact, adjusted);
    #else
        return (new List<FloorPlanPage>(), new List<FloorPlanPage>());
    #endif
    }

#if META_XR_SDK_INSTALLED
    /// <summary>
    /// Requests Scene permission if needed and calls LoadSceneFromDevice, waiting (with a timeout)
    /// for MRUK to finish syncing. No-op in the Editor, where scene data is loaded by other means.
    /// </summary>
    private async Task<bool> EnsureLiveSceneLoaded(XRMenu ui)
    {
        if (Application.isEditor) {
            ui?.AddLog("Running in Editor - skipping device sync.");
            return true;
        }

        ui?.AddLog("Checking Scene permissions...");
        if (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene)) {
            ui?.AddLog("Requesting Scene permission - please respond to the system dialog...");
            bool granted = false;
            void OnGranted(string id) { if (id == OVRPermissionsRequester.ScenePermission) granted = true; }
            OVRPermissionsRequester.PermissionGranted += OnGranted;
            OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.Scene });

            int waitTicks = 0;
            while (!granted && !OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene) && waitTicks < 300) {
                await Task.Delay(100); waitTicks++;
            }
            OVRPermissionsRequester.PermissionGranted -= OnGranted;

            if (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene)) {
                ui?.AddLog("<color=red>ERROR: Scene permission was not granted.</color>");
                return false;
            }
            ui?.AddLog("Scene permission granted.");
        }

        ui?.AddLog("Calling LoadSceneFromDevice...");
        // V2FallbackV1: use the High Fidelity scene (multi-floor/ceiling, sloped ceilings) when the
        // device has it, but don't fail on rooms captured before HiFi scans existed.
        var loadTask = MRUK.Instance.LoadSceneFromDevice(true, true, MRUK.SceneModel.V2FallbackV1);

        // Real scans (several rooms, global meshes) can legitimately take well over 8s to stream from
        // the OS the first time - that used to abort the load outright even with a valid, complete scan.
        int loadTimeout = 0;
        while (!loadTask.IsCompleted && loadTimeout < 450) { // 45 seconds
            await Task.Delay(100);
            loadTimeout++;
            if (loadTimeout % 20 == 0) ui?.AddLog($"Still waiting for device sync... ({loadTimeout / 10}s)");
        }
        if (!loadTask.IsCompleted) {
            ui?.AddLog("<color=red>FATAL ERROR: LoadSceneFromDevice timed out after 45s!</color>");
            ui?.AddLog("Check if 'Room Setup' is done in Quest settings, or pick a cached scan instead.");
            return false;
        }
        if (loadTask.IsFaulted) {
            ui?.AddLog("<color=red>LoadSceneFromDevice failed: " + loadTask.Exception?.GetBaseException().Message + "</color>");
            return false;
        }
        var result = await loadTask;
        if (result != MRUK.LoadDeviceResult.Success) {
            ui?.AddLog($"<color=red>LoadSceneFromDevice: {result}</color>");
            return false;
        }

        int initTimeout = 0;
        while (!MRUK.Instance.IsInitialized && initTimeout < 50) {
            await Task.Delay(100); initTimeout++;
        }
        ui?.AddLog(MRUK.Instance.IsInitialized ? "Live scan loaded." : "<color=orange>MRUK init timeout - continuing anyway.</color>");
        return true;
    }

    /// <summary>Steps 3-6: layout, data dumps, report and all model tiers for an already resolved room list.</summary>
    private async Task<bool> RunExportPipeline(List<MRUKRoom> rooms, XRMenu ui)
    {
        string root = MRUKPathUtility.GetExportRoot();
        // Android 13+ storage protection fallback
        try { if (!Directory.Exists(root)) Directory.CreateDirectory(root); }
        catch {
            root = Application.persistentDataPath;
            ui?.AddLog("<color=orange>Using persistentDataPath fallback</color>");
        }

        ui?.AddLog("<color=cyan>[3/6] Calculating house layout...</color>");
        var outlines = MRUKPlanExtractor.Extract(rooms);
        LastHouseCenter = CalculateHouseCenter(rooms);
        float angle = FloorPlanBuilder.CorrectionYaw(outlines);
        string session = MRUKPathUtility.CreateSessionFolder(root, SelectedSource);
        ui?.AddLog($"Session created: {Path.GetFileName(session)} (wall alignment {angle:0.#} deg)");
        SetProgress(30, ui);

        ui?.AddLog("<color=cyan>[4/6] Data generation...</color>");
        File.WriteAllText(Path.Combine(session, MRUKPathUtility.DATA_JSON), MRUKDataProcessor.GenerateJson(rooms));
        File.WriteAllText(Path.Combine(session, MRUKPathUtility.DATA_DUMP), MRUKDataProcessor.GenerateSceneDump(rooms));
        File.WriteAllText(Path.Combine(session, MRUKPathUtility.DATA_REPORT), MRUKReportBuilder.GenerateFullReport(outlines, angle, SelectedSource));
        SetProgress(45, ui);

        ui?.AddLog("<color=cyan>[5/6] Exporting models...</color>");
        Save(XRModelFactory.CreateAnchorAnalytical(rooms, angle, LastHouseCenter), session, MRUKPathUtility.MODEL_CLEAN_OBJ, MRUKPathUtility.MODEL_CLEAN_GLB); SetProgress(60, ui);
        Save(await XRModelFactory.CreateMeshAnalytical(rooms, angle, LastHouseCenter), session, MRUKPathUtility.MODEL_MESH_ANALYTICAL_OBJ, MRUKPathUtility.MODEL_MESH_ANALYTICAL_GLB); SetProgress(75, ui);
        Save(XRModelFactory.CreateReconstruction(rooms, angle, LastHouseCenter), session, MRUKPathUtility.MODEL_MESH_OBJ, MRUKPathUtility.MODEL_MESH_GLB); SetProgress(85, ui);
        Save(await XRModelFactory.CreateRawScan(rooms, angle, LastHouseCenter), session, MRUKPathUtility.MODEL_RAW_OBJ, MRUKPathUtility.MODEL_RAW_GLB); SetProgress(95, ui);

        File.WriteAllText(Path.Combine(session, MRUKPathUtility.MODEL_MTL), OBJWriter.GenerateMTL());

        ui?.AddLog("<color=cyan>[6/6] Per-room exports...</color>");
        // Everything the whole-house export has (all 4 model tiers, the HTML report), but scoped to just this
        // one room, in its own self-contained subfolder.
        foreach (var r in rooms) {
            string rDirName = $"{MRUKDataProcessor.GetSafeName(MRUKDataProcessor.GetRoomLabel(r))}_{r.Anchor.Uuid.ToString().Substring(0, 8)}";
            string rPath = Path.Combine(session, rDirName);
            Directory.CreateDirectory(rPath);
            var rList = new List<MRUKRoom> { r };

            Save(XRModelFactory.CreateAnchorAnalytical(rList, angle, LastHouseCenter), rPath, MRUKPathUtility.MODEL_CLEAN_OBJ, MRUKPathUtility.MODEL_CLEAN_GLB);
            Save(await XRModelFactory.CreateMeshAnalytical(rList, angle, LastHouseCenter), rPath, MRUKPathUtility.MODEL_MESH_ANALYTICAL_OBJ, MRUKPathUtility.MODEL_MESH_ANALYTICAL_GLB);
            Save(XRModelFactory.CreateReconstruction(rList, angle, LastHouseCenter), rPath, MRUKPathUtility.MODEL_MESH_OBJ, MRUKPathUtility.MODEL_MESH_GLB);
            Save(await XRModelFactory.CreateRawScan(rList, angle, LastHouseCenter), rPath, MRUKPathUtility.MODEL_RAW_OBJ, MRUKPathUtility.MODEL_RAW_GLB);
            File.WriteAllText(Path.Combine(rPath, MRUKPathUtility.MODEL_MTL), OBJWriter.GenerateMTL());

            var roomOutline = MRUKPlanExtractor.Extract(rList);
            File.WriteAllText(Path.Combine(rPath, MRUKPathUtility.DATA_REPORT_ROOM), MRUKReportBuilder.GenerateFullReport(roomOutline, angle, SelectedSource));
        }

        ui?.AddLog("<color=green><b>EXPORT FINISHED!</b></color>");
        SetProgress(100, ui);
        LastReportPath = Path.Combine(session, MRUKPathUtility.DATA_REPORT);
        #if UNITY_EDITOR
        UnityEditor.EditorUtility.RevealInFinder(session);
        #endif
        return true;
    }

    private Vector3 CalculateHouseCenter(List<MRUKRoom> rs)
    {
        Vector3 c = Vector3.zero; int n = 0;
        foreach (var r in rs) {
            var f = r.FloorAnchors.FirstOrDefault(a => a != null);
            if (f != null) { c += f.transform.position; n++; }
        }
        return n > 0 ? c / n : rs[0].transform.position;
    }
#endif

#if UNITY_EDITOR
    /// <summary>Editor-only entry point, so tooling like the Unity MCP bridge can start the fast device build without a menu click.</summary>
    public void EditorFastBuildAndInstall() => MRUKEditorTools.BuildAPKFast();
#endif

    private void SetProgress(float v, XRMenu m) { if (m != null) m.SetProgress(v); }
    private void Save(XRHouseModel m, string f, string obj, string glb) {
        if (obj != null) File.WriteAllText(Path.Combine(f, obj), OBJWriter.WriteToString(m));
        if (glb != null) { byte[] b = GLBExporter.ExportToGLB(m); if (b != null) File.WriteAllBytes(Path.Combine(f, glb), b); }
    }
}
