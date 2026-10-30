using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
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
    /// <summary>MRUK holds the device's live scene (the live source, or a saved copy of it adopted without reloading),
    /// i.e. its rooms line up with the real house. A saved scan loaded from JSON comes back in some other frame.</summary>
    public bool WorldAligned { get; private set; }

    /// <summary>Live scan first, then cached scans, most recently saved first.</summary>
    public List<string> Sources => new[] { LiveSource }.Concat(MRUKSceneCache.ListCachedScans()).ToList();

    /// <summary>
    /// Makes a just-saved copy of the live scan the selected AND active source without reloading it - MRUK already
    /// holds exactly that data. Used when the first edit is made on the live scan: from then on the edits belong
    /// to this saved scan.
    /// </summary>
    /// <summary>Makes sure MRUK holds the selected scan lined up with the real house (the live scan, or a saved one
    /// aligned onto it) - for anything that works over passthrough, like wall photos and routes. Never switches the
    /// selected scan.</summary>
    int notAlignedLogged = -1;
    public async Task<bool> EnsureLiveLoaded(XRMenu ui = null)
    {
        if (WorldAligned && ActiveSource == SelectedSource) return true;
        // Never switches the source by itself: a saved scan stays selected and is lined up with the house; if it
        // can't be (no room in common - e.g. not in the house), it is the model only. Live scan = pick it with < >.
        // (loading a saved scan on the headset already tries to line it up - no reload loop when it can't)
        bool ok = await EnsureSelectedSourceLoaded(ui);
        if (ok && !WorldAligned && notAlignedLogged != SceneVersion && (notAlignedLogged = SceneVersion) >= 0)
            ui?.AddLog($"<color=orange>'{SelectedSource}' isn't lined up with the house here - walk through the model to work on it, or pick Live scan with < >.</color>");
        return ok && WorldAligned;
    }

    public void AdoptSavedLiveScan(string savedName)
    {
        SceneVersion++;
        if (string.IsNullOrEmpty(savedName)) return;
        SelectedSource = savedName;
        ActiveSource = savedName;
        BindScanData(savedName);
    }

    /// <summary>Points the edits and room names at the given saved scan (null = the live scan: no edits, default names).
    /// On the headset room names are one shared file anyway; in the Editor each scan folder has its own.</summary>
    static void BindScanData(string scanName)
    {
        HouseEditsStore.Bind(scanName);
        RoomNames.FilePath = MRUKSceneCache.RoomNamesPath(scanName);
    }

    /// <summary>Bumped whenever MRUK's rooms are replaced (another scan loaded) - room caches compare against it.</summary>
    public static int SceneVersion { get; private set; }

    /// <summary>How the loaded saved scan sits on the real house, e.g. "aligned to the house via 7 rooms (2 cm)".</summary>
    public string AlignInfo { get; private set; } = "";

    struct LiveRoom { public Vector3 pos; public Quaternion rot; public bool intact; }

    static Dictionary<string, LiveRoom> SnapshotRooms()
    {
        var d = new Dictionary<string, LiveRoom>();
        foreach (var r in MRUK.Instance.Rooms)
        {
            if (r == null) continue;
            d[r.Anchor.Uuid.ToString()] = new LiveRoom
            {
                pos = r.transform.position, rot = r.transform.rotation,
                intact = r.FloorAnchors.Count > 0 && r.WallAnchors.Count >= 3,
            };
        }
        return d;
    }

    /// <summary>
    /// Lines the just-loaded saved scan up with the real house: every room it shares with the live scene (same UUID -
    /// the saved scan is a copy of that scene) says where it really is now; the yaw + offset most of them agree on
    /// (within 2 deg / 15 cm) is applied to the tracking space, so the saved rooms land on the real walls. Rooms the
    /// device has since lost or re-scanned don't spoil it - they just disagree and are outvoted.
    /// </summary>
    void AlignToLive(Dictionary<string, LiveRoom> live, XRMenu ui)
    {
        var pairs = new List<(string id, LiveRoom l, Vector3 sp, Quaternion sr)>();
        foreach (var r in MRUK.Instance.Rooms)
        {
            if (r == null || !live.TryGetValue(r.Anchor.Uuid.ToString(), out var l)) continue;
            pairs.Add((r.Anchor.Uuid.ToString(), l, r.transform.position, r.transform.rotation));
        }
        if (pairs.Count == 0)
        {
            AlignInfo = "not aligned - no room in common with the live scan (another place)";
            ui?.AddLog($"<color=orange>Saved scan: {AlignInfo}. It is a model only here.</color>");
            Debug.Log("[Align] " + AlignInfo);
            return;
        }
        float Yaw(Quaternion q) { var f = q * Vector3.forward; return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg; }
        (Quaternion R, Vector3 t) Fit(int i)
        {
            var p = pairs[i];
            var R = Quaternion.Euler(0, Yaw(p.sr * Quaternion.Inverse(p.l.rot)), 0);
            return (R, p.sp - R * p.l.pos);
        }
        int best = -1, bestSupport = 0; float bestErr = float.MaxValue;
        for (int i = 0; i < pairs.Count; i++)
        {
            if (!pairs[i].l.intact && pairs.Any(p => p.l.intact)) continue; // prefer rooms the device still has whole
            var (R, t) = Fit(i);
            int support = 0; float err = 0;
            foreach (var p in pairs)
            {
                float d = Vector3.Distance(R * p.l.pos + t, p.sp);
                float a = Quaternion.Angle(R * p.l.rot, p.sr);
                if (d < 0.15f && a < 2f) { support++; err += d; }
            }
            err = support > 0 ? err / support : float.MaxValue;
            if (support > bestSupport || (support == bestSupport && err < bestErr)) { best = i; bestSupport = support; bestErr = err; }
        }
        if (best < 0) { AlignInfo = "not aligned"; return; }
        // refine over the rooms that agree
        var (R0, t0) = Fit(best);
        var agree = pairs.Where(p => Vector3.Distance(R0 * p.l.pos + t0, p.sp) < 0.15f && Quaternion.Angle(R0 * p.l.rot, p.sr) < 2f).ToList();
        float yawSum = 0;
        foreach (var p in agree) yawSum += Mathf.DeltaAngle(0, Yaw(p.sr * Quaternion.Inverse(p.l.rot)) - Yaw(R0)) ;
        var Rf = Quaternion.Euler(0, Yaw(R0) + yawSum / agree.Count, 0);
        Vector3 tf = Vector3.zero;
        foreach (var p in agree) tf += p.sp - Rf * p.l.pos;
        tf /= agree.Count;
        float res = agree.Average(p => Vector3.Distance(Rf * p.l.pos + tf, p.sp));
        SetTrackingOffset(Matrix4x4.TRS(tf, Rf, Vector3.one));
        WorldAligned = true;
        AlignInfo = $"aligned to the house via {agree.Count} of {pairs.Count} shared room(s), {res * 100f:0} cm";
        ui?.AddLog($"<color=green>Saved scan {AlignInfo}.</color>");
        Debug.Log($"[Align] {AlignInfo}; yaw {Yaw(Rf):0.0} deg, offset {tf}; disagreeing: {string.Join(", ", pairs.Except(agree).Select(p => p.id.Substring(0, 8)))}");
    }

    /// <summary>Shifts the camera rig's tracking space (identity = the device's own frame). With MRUK world locking on,
    /// that is MRUK's TrackingSpaceOffset (it rewrites the tracking space every frame); otherwise the transform itself.</summary>
    static void SetTrackingOffset(Matrix4x4 m)
    {
        var mruk = MRUK.Instance;
        if (mruk == null) return;
        if (mruk.EnableWorldLock) { mruk.TrackingSpaceOffset = m; return; }
        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig && rig.trackingSpace) rig.trackingSpace.SetLocalPositionAndRotation(m.GetPosition(), m.rotation);
    }

    /// <summary>Selects a saved scan by name (an explicit choice, e.g. the scan that replaces a deleted one).</summary>
    public void Select(string name)
    {
        if (!string.IsNullOrEmpty(name) && Sources.Contains(name)) SelectedSource = name;
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
            bool done = await RunExportPipeline(rooms, ui);
            if (done) PendingChanges.Exported();
            return done;
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
    // One load at a time: quick < > taps started overlapping loads (clear + load + align) that mixed scans and
    // left the app looking stuck.
    readonly System.Threading.SemaphoreSlim loadLock = new System.Threading.SemaphoreSlim(1, 1);

    /// <summary>True while a scan is being (re)loaded - MRUK may briefly hold the live scene (to line a saved scan up
    /// with the house) or nothing at all. Views never rebuild from what they see meanwhile; they follow SceneVersion.</summary>
    public static bool Loading { get; private set; }

    public async Task<bool> EnsureSelectedSourceLoaded(XRMenu ui, bool forceReload = false)
    {
        await loadLock.WaitAsync();
        Loading = true;
        try { return await LoadSelectedSource(ui, forceReload); }
        finally { Loading = false; loadLock.Release(); }
    }

    async Task<bool> LoadSelectedSource(XRMenu ui, bool forceReload)
    {
    #if META_XR_SDK_INSTALLED
        if (MRUK.Instance == null) {
            ui?.AddLog("<color=red>ERROR: MRUK Instance not found in scene!</color>");
            return false;
        }
        if (!forceReload && ActiveSource == SelectedSource) return true;

        if (SelectedSource == LiveSource) {
            SetTrackingOffset(Matrix4x4.identity);
            AlignInfo = "";
            if (!await EnsureLiveSceneLoaded(ui)) return false;
            WorldAligned = true;
            PendingChanges.CheckLive(MRUK.Instance.Rooms);
        } else {
            WorldAligned = false;
            AlignInfo = "";
            // Where the house's rooms really are right now (the live scene, no offset applied) - to line the saved
            // scan up with them below. In the house this works even when the live scene lost rooms or walls: any
            // room the two share is enough.
            Dictionary<string, LiveRoom> live = null;
            if (!Application.isEditor && !MRUKPathUtility.IsDesktop) {
                SetTrackingOffset(Matrix4x4.identity);
                await Task.Yield(); await Task.Yield();
                if (await EnsureLiveSceneLoaded(null)) { live = SnapshotRooms(); PendingChanges.CheckLive(MRUK.Instance.Rooms); }
            }
            ui?.AddLog($"Loading cached scan '{SelectedSource}'...");
            if (!await MRUKSceneCache.LoadCachedScene(MRUK.Instance, SelectedSource)) {
                ui?.AddLog("<color=red>Failed to load cached scan.</color>");
                return false;
            }
            if (live != null) AlignToLive(live, ui);
        }
        SceneVersion++;
        ActiveSource = SelectedSource;
        // Edits belong to a saved scan; the live scan has none until its first edit saves it (EditModeController).
        BindScanData(ActiveSource == LiveSource ? null : ActiveSource);
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
        await loadLock.WaitAsync();   // never alongside a scan load
        Loading = true;
        try {
            if (MRUK.Instance == null) {
                ui?.AddLog("<color=red>ERROR: MRUK Instance not found in scene!</color>");
                return null;
            }

            ui?.AddLog("<color=cyan>Saving current scan to cache...</color>");
            SetTrackingOffset(Matrix4x4.identity);
            AlignInfo = "";
            if (!await EnsureLiveSceneLoaded(ui)) return null;
            SceneVersion++;
            ActiveSource = LiveSource;
            WorldAligned = true;

            var rooms = MRUKDataProcessor.GetValidRooms(MRUK.Instance);
            if (rooms.Count == 0) {
                ui?.AddLog("<color=red>No valid rooms found - nothing to cache.</color>");
                return null;
            }

            name = string.IsNullOrEmpty(name) ? $"{DateTime.Now:yyyyMMdd_HHmm}_{rooms.Count}rooms" : name;
            string saved = MRUKSceneCache.SaveCurrentScene(MRUK.Instance, name);
            if (saved != null)
            {
                PendingChanges.LiveSaved();
                ui?.AddLog($"<color=green>Scan cached as '{saved}' ({rooms.Count} room(s)). You can export it later from anywhere.</color>");
            }
            else
                ui?.AddLog("<color=red>Failed to save scan to cache.</color>");
            // back to the scan you had picked - saving never switches it
            if (SelectedSource != LiveSource) await LoadSelectedSource(ui, true);
            return saved;
        } catch (Exception ex) {
            Debug.LogException(ex);
            ui?.AddLog("<color=red>ERROR: " + ex.Message + "</color>");
            return null;
        } finally {
            Loading = false;
            loadLock.Release();
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
        if (ok) HouseEditsStore.DeleteFor(name); // its edits mean nothing without the scan
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
        var loadTask = MRUK.Instance.LoadSceneFromDevice(false, true, MRUK.SceneModel.V2FallbackV1); // never starts Space Setup by itself

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
    /// <summary>
    /// Exports whatever MRUK currently holds (e.g. a scene JSON loaded by a test or tool via
    /// LoadSceneFromJsonString) under the given source name, into 'root' (default: the normal export root).
    /// Used by the batch export (ScanExportTests) that runs the real pipeline on a saved scan without a headset.
    /// Returns the session folder, or null on failure.
    /// </summary>
    public async Task<string> ExportLoadedScene(string sourceName, string root = null)
    {
        SelectedSource = ActiveSource = sourceName;
        WorldAligned = false;
        var rooms = MRUKDataProcessor.GetValidRooms(MRUK.Instance);
        if (rooms.Count == 0) { Debug.LogError("[BatchExport] No valid rooms in the loaded scene."); return null; }
        return await RunExportPipeline(rooms, null, root) ? Path.GetDirectoryName(LastReportPath) : null;
    }

    private async Task<bool> RunExportPipeline(List<MRUKRoom> rooms, XRMenu ui, string rootOverride = null)
    {
        string root = rootOverride ?? MRUKPathUtility.GetExportRoot();
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
        // The whole scan itself (MRUK scene JSON incl. global mesh), whether it came live from the device or
        // from a saved scan - lets the PC keep and re-load the exact scan this export was made from.
        try {
            string sceneJson = MRUK.Instance.SaveSceneToJsonString(true);
            if (!string.IsNullOrEmpty(sceneJson)) File.WriteAllText(Path.Combine(session, MRUKPathUtility.DATA_SCENE), sceneJson);
            else ui?.AddLog("<color=orange>Scan JSON was empty - not included in the export.</color>");
        } catch (Exception ex) {
            Debug.LogException(ex);
            ui?.AddLog("<color=orange>Could not include the scan JSON: " + ex.Message + "</color>");
        }
        // ...and what the scan itself doesn't hold: the user's edits and room names, so the export folder alone is
        // enough to reload, edit and re-export this scan on the PC (it is a scan source there, see MRUKSceneCache).
        try {
            if (HouseEditsStore.CurrentScan != null && HouseEditsStore.HasAny)
                File.WriteAllText(Path.Combine(session, MRUKPathUtility.DATA_EDITS), JsonUtility.ToJson(HouseEditsStore.Current, true));
            RoomNames.WriteTo(Path.Combine(session, MRUKPathUtility.DATA_ROOM_NAMES), rooms.Select(r => r.Anchor.Uuid.ToString()));
        } catch (Exception ex) {
            Debug.LogException(ex);
            ui?.AddLog("<color=orange>Could not include edits/room names: " + ex.Message + "</color>");
        }
        File.WriteAllText(Path.Combine(session, MRUKPathUtility.DATA_REPORT), MRUKReportBuilder.GenerateFullReport(outlines, angle, SelectedSource));
        // The scan's furniture spec: real (sloped) ceilings and the niches cut into the walls - as the walk-through shows them.
        Furniture.InstallCeilings(rooms, HouseEditsStore.CurrentScan);
        // Wall elevations + plans with the traced routes (switchable in the file).
        try { WallDrawings.Write(session, rooms, SelectedSource); }
        catch (Exception ex) { Debug.LogException(ex); ui?.AddLog("<color=orange>Wall drawings failed: " + ex.Message + "</color>"); }
        SetProgress(45, ui);

        ui?.AddLog("<color=cyan>[5/6] Exporting models...</color>");
        Save(XRModelFactory.CreateAnchorAnalytical(rooms, angle, LastHouseCenter), session, MRUKPathUtility.MODEL_CLEAN_OBJ, MRUKPathUtility.MODEL_CLEAN_GLB); SetProgress(55, ui); await Task.Yield();
        Save(XRModelFactory.CreateAnchorAnalyticalWithDimensions(rooms, angle, LastHouseCenter), session, MRUKPathUtility.MODEL_DIM_OBJ, MRUKPathUtility.MODEL_DIM_GLB); SetProgress(65, ui); await Task.Yield();
        Save(await XRModelFactory.CreateMeshAnalytical(rooms, angle, LastHouseCenter), session, MRUKPathUtility.MODEL_MESH_ANALYTICAL_OBJ, MRUKPathUtility.MODEL_MESH_ANALYTICAL_GLB); SetProgress(75, ui); await Task.Yield();
        Save(XRModelFactory.CreateReconstruction(rooms, angle, LastHouseCenter), session, MRUKPathUtility.MODEL_MESH_OBJ, MRUKPathUtility.MODEL_MESH_GLB); SetProgress(85, ui); await Task.Yield();
        Save(await XRModelFactory.CreateRawScan(rooms, angle, LastHouseCenter), session, MRUKPathUtility.MODEL_RAW_OBJ, MRUKPathUtility.MODEL_RAW_GLB); SetProgress(95, ui); await Task.Yield();

        File.WriteAllText(Path.Combine(session, MRUKPathUtility.MODEL_MTL), OBJWriter.GenerateMTL());

        ui?.AddLog("<color=cyan>[6/6] Per-room exports...</color>");
        // Everything the whole-house export has (all 4 model tiers, the HTML report), but scoped to just this
        // one room, in its own self-contained subfolder.
        foreach (var r in rooms) {
            string rDirName = $"{MRUKDataProcessor.GetSafeName(MRUKDataProcessor.GetRoomLabel(r))}_{r.Anchor.Uuid.ToString().Substring(0, 8)}";
            string rPath = Path.Combine(session, rDirName);
            Directory.CreateDirectory(rPath);
            var rList = new List<MRUKRoom> { r };
            await Task.Yield();   // a frame between rooms - the export used to freeze the headset for seconds

            Save(XRModelFactory.CreateAnchorAnalytical(rList, angle, LastHouseCenter), rPath, MRUKPathUtility.MODEL_CLEAN_OBJ, MRUKPathUtility.MODEL_CLEAN_GLB);
            Save(XRModelFactory.CreateAnchorAnalyticalWithDimensions(rList, angle, LastHouseCenter), rPath, MRUKPathUtility.MODEL_DIM_OBJ, MRUKPathUtility.MODEL_DIM_GLB);
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
        if (!Application.isBatchMode) UnityEditor.EditorUtility.RevealInFinder(session);
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
