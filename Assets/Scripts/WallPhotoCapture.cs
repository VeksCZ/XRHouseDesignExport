using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
#if META_XR_SDK_INSTALLED
using Meta.XR;
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Edit mode "Photos" tool: documents the bare walls/floor/ceiling (cable chases, boxes, pipes) before they are
/// plastered over. Works over passthrough on the LIVE scan (the only one aligned with the real house): every photo
/// from the headset's passthrough camera (Passthrough Camera API, MRUK PassthroughCameraAccess) is saved with the
/// exact camera pose at the moment of exposure, stored relative to the MRUK room it was taken in (room UUID +
/// room-local pose, like HouseEdits) plus the camera's pinhole model - so on the PC every pixel of the photo can be
/// turned into a ray and hit against the scan: traced cable routes become 3D lines in the model.
///
/// Photos go to persistentDataPath/WallPhotos/Photos_yyyyMMdd/ (NNNN.jpg + NNNN.json), together with a snapshot of
/// the live scan (92_Data_Scan.scene.json) and room names, so the pulled folder is a complete scan source on the PC
/// ("Pull data from Quest" brings it into Exports/RoomData). Several sessions on one day append to the same folder.
///
/// Shooting: trigger = photo now. Auto (default on): a photo is taken by itself whenever the head is held still for
/// a moment looking at something not photographed yet. Every photo's footprint is outlined on the real surfaces
/// (coverage), so it's easy to see what is still missing.
///
/// Dense (for a Gaussian splat of the bare house): while on, a frame is taken every ~15 cm / 10 degrees of
/// head motion as you walk slowly (motion-blur gated), into the session's Dense/ subfolder - hundreds per room. Each
/// dense frame also carries a grid of seed points (rays hit against the scan, coloured from the photo) for the
/// splat's initial point cloud. Every photo additionally stores its pose in one fixed "house frame" (the room with the
/// smallest UUID), so photos from all rooms and days share one coordinate system: the menu "Wall photos > Export
/// COLMAP" (WallPhotoColmap) turns a pulled session into a COLMAP dataset for any splat trainer, and the trained splat
/// lands in that same frame - aligned with the scan.
/// </summary>
public class WallPhotoCapture : MonoBehaviour
{
    public XRMenu uiLog;
    public EditModeController editMode;
    public MRUKExporter exporter;
    public WalkThroughMode walk;

    public const string RootFolder = "WallPhotos";
    public const string SessionPrefix = "Photos_";
    const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";

    // Auto shot: head still for StillTime (below these speeds), at least MinInterval since the last shot, and the
    // view new: centre ray pointing >NewAngle away from every earlier shot standing nearby, or hitting >NewDistance
    // from every earlier shot's centre hit.
    const float StillTime = 0.25f, StillDegPerSec = 8f, StillMetersPerSec = 0.06f, MinInterval = 0.6f;
    const float NewAngle = 22f, NewDistance = 0.9f;
    // Cable photos the PC tools can use (RouteDetector rectifies up to 4.5 m / 72 deg - keep a margin).
    const float MaxShotDist = 3.5f, MaxShotAngle = 62f;
    // Dense frames sharper than this share of the recent median only (motion blur / focus): blurry ones are dropped.
    const float MinRelSharpness = 0.6f;

    public const string DenseFolder = "Dense";
    // Dense: a frame after DenseMove metres or DenseTurn degrees of camera motion, at most every DenseInterval, and
    // only while the head turns slowly enough for a sharp image. Same defaults as QuestRoomScan's KeyframeCollector
    // (0.15 m / 10 deg / 0.25 s / 120 deg/s), which its splat training is tuned on.
    const float DenseMove = 0.15f, DenseTurn = 10f, DenseInterval = 0.25f, DenseMaxDegPerSec = 120f, DenseMaxMetersPerSec = 1.0f;
    const int SeedCols = 8, SeedRows = 6, MaxPendingWrites = 3;

    static readonly Color CoverageColor = new Color(0.25f, 1f, 0.45f, 1f);
    static readonly Color LastColor = new Color(1f, 0.85f, 0.2f, 1f);

    bool wasActive, auto = true, showCoverage = true, dense;
    string sessionDir, houseRoomUuid;
    int nextIndex, denseCount, nextDenseIndex;
    Vector3 lastDensePos; Quaternion lastDenseRot; bool haveDense; float lastDenseTime = -10f;
    readonly List<WallPhotoMeta> shots = new List<WallPhotoMeta>();
    // One footprint per photo: a translucent fill on the surfaces it covers plus its outline.
    class Footprint { public GameObject go; public LineRenderer line; public Mesh mesh; }
    readonly List<Footprint> footprints = new List<Footprint>();
    Material lineMat, fillMat;
    const int FootGrid = 8; // rays per image side for the footprint fill
#if META_XR_SDK_INSTALLED
    // GetValidRooms is far too heavy to call per ray (it re-extracts every room's plan, and a room without a floor
    // makes the Meta SDK log an error with a stack trace each call) - refreshed only when MRUK's room list changes.
    static List<MRUKRoom> roomCache = new List<MRUKRoom>();
    static int roomCacheCount = -1;
    static List<MRUKRoom> Rooms
    {
        get
        {
            var m = MRUK.Instance;
            // keyed by the room count AND the scene version: another scan with as many rooms must not reuse the old list
            int n = (m != null ? m.Rooms.Count : 0) + 1000 * MRUKExporter.SceneVersion;
            if (n != roomCacheCount) { roomCache = m != null ? MRUKDataProcessor.GetValidRooms(m) : new List<MRUKRoom>(); roomCacheCount = n; }
            return roomCache;
        }
    }
#endif
    // 3D-scan coverage: the surface points every dense frame saw, binned into 10 cm cells and drawn as small patches on
    // the walls - orange = seen once, yellow = twice, green = 3+ views (what a splat needs). Shown while the 3D scan is on.
    class CovCell { public Vector3 p, n; public int count; }
    readonly Dictionary<Vector3Int, CovCell> coverage = new Dictionary<Vector3Int, CovCell>();
    GameObject covGo; Mesh covMesh; bool covDirty; float covBuiltAt;
    const float CovCellSize = 0.1f, CovPatch = 0.075f;

    // Pixel buffers reused between captures (5 MB each at 1280x960 - a fresh one per frame made the GC stall).
    readonly Stack<byte[]> pixelPool = new Stack<byte[]>();
    Transform head;

    // stillness tracking
    Vector3 lastHeadPos; Quaternion lastHeadRot; float stillFor, lastShotTime = -10f, headDegPerSec, headMetersPerSec;
    bool preparing, capturing;
    // sharpness of recent frames (for the relative blur test) and what the session logged
    readonly List<float> sharpDense = new List<float>(), sharpCable = new List<float>();
    int blurryDropped;
    Vector3 prevDensePos; Quaternion prevDenseRot; bool prevHaveDense; float prevDenseTime;
    static readonly object logLock = new object();
    int pendingWrites;

    // Panel: rides above the edit toolbar on the left controller (grip-drag it away and it stays put).
    Canvas panel;
    PanelDrag panelDrag;
    TMP_Text countText, roomText, statusText;
    UnityEngine.UI.Button coverageButton;
    const float PW = 560f, PH = 270f, PScale = 0.00045f, RowH = 62f, Gap = 10f;

    // HUD: one line of live guidance just below the centre of view (why it is or isn't shooting right now).
    Canvas hud;
    TMP_Text hudText;
    UnityEngine.UI.Image hudBar, hudFrame;
    float flashUntil, tipUntil;
    string flashText;
    const string TipPref = "WallPhotos.TipShown";

    // State the HUD explains.
    enum View { Unknown, Nothing, Covered, Moving, Settling, TooFar, TooOblique }
    View view;
    bool started;
    float retryAt;
    readonly Dictionary<string, int> cableByRoom = new Dictionary<string, int>(), denseByRoom = new Dictionary<string, int>();
    string currentRoomUuid, currentRoomName;

    AudioSource audioSrc;
    AudioClip shutterClip;

#if META_XR_SDK_INSTALLED
    PassthroughCameraAccess cam;
    // A camera frame is armed when it arrives (pose + timestamp taken in that same frame); its pixels are read back
    // one frame later - GetTexture() is filled on the render thread, so reading it in the arrival frame would give
    // the previous image (see PassthroughCameraAccess.GetTexture docs).
    bool shotRequested, requestDense, armed, cameraReady;
    Pose armedPose; DateTime armedTime;
#endif

    void Start()
    {
        if (editMode != null) editMode.TriggerPressed += OnTrigger;
    }

    void OnDestroy()
    {
        if (editMode != null) editMode.TriggerPressed -= OnTrigger;
        StopCamera();
        ClosePanel();
        CloseHud();
        ClearOutlines();
        if (lineMat) Destroy(lineMat);
        if (fillMat) Destroy(fillMat);
        if (covGo) Destroy(covGo);
        if (covMesh) Destroy(covMesh);
        if (guideGo) Destroy(guideGo);
        if (guideMesh) Destroy(guideMesh);
        if (shutterClip) Destroy(shutterClip);
    }

    bool Active => editMode != null && editMode.IsOn && editMode.CurrentTool == EditModeController.Tool.Photos;

    void Update()
    {
        if (!Active)
        {
            if (wasActive) { wasActive = false; started = false; StopCamera(); ClosePanel(); CloseHud(); ClearOutlines(); if (covGo) covGo.SetActive(false); if (guideGo) guideGo.SetActive(false); guideRoom = "-"; WallOverlays.ForceScan = false; WallScan.SaveAll(); }
            return;
        }
        wasActive = true;
        // while the 3D scan records, the walls show what it has painted so far
        WallOverlays.ForceScan = dense;
        if (!started && !preparing && Time.time >= retryAt) Begin();
#if META_XR_SDK_INSTALLED
        if (cam == null || !cam.IsPlaying) { UpdateHud(); return; }
        if (!cameraReady) { cameraReady = true; RefreshPanel(); }
        TrackStillness();
        TrackRoom();
        view = View.Unknown;
        if (!shotRequested && !capturing)
        {
            if (dense) { if (pendingWrites < MaxPendingWrites && DenseDue()) shotRequested = requestDense = true; }
            else if (auto)
            {
                view = ClassifyView();
                if (view == View.Settling && stillFor >= StillTime && Time.time - lastShotTime >= MinInterval)
                { shotRequested = true; requestDense = false; }
            }
        }
        PumpCapture();
        UpdateCoverage();
        UpdateGuide();
#endif
        UpdateHud();
    }

    void LateUpdate()
    {
        FollowHand();
        PlaceHud();
    }

    // ---------- session ----------

    async void Begin()
    {
        var rig = FindFirstObjectByType<OVRCameraRig>();
        head = rig ? rig.centerEyeAnchor : Camera.main ? Camera.main.transform : null;
        BuildHud();
        // The mode it was opened for (menu: "3D scan" or "Cable photos") - never a 3D scan left on from last time,
        // which would record on its own while you walk to the next room.
        dense = StartDense; StartDense = false; haveDense = false;
        if (walk != null && walk.IsOn)
        {
            // Photos are taken of the real house, over passthrough - step out of the model first.
            uiLog?.AddLog("Photos: leaving the walk-through (photos are taken over passthrough).");
            uiLog?.OnToggleWalk();
            retryAt = Time.time + 1f;
            return;
        }
        preparing = true;
        try
        {
            SetHud("Lining the scan up with the house...");
            if (exporter == null || !await exporter.EnsureLiveLoaded(uiLog))
            {
                SetHud("<color=#ff6060>This scan isn't lined up with the house here - photos need that. Pick Live scan with < > if this isn't the house.</color>");
                retryAt = Time.time + 5f;
                return;
            }
            if (!Active) return;
            OpenSession();
            BuildPanel();
            RebuildOutlines();
            SetHud("Asking for camera access...");
            if (!await EnsurePermission())
            {
                SetStatus("<color=red>No camera permission - allow 'Headset cameras' for this app in Settings.</color>");
                SetHud("<color=#ff6060>Camera permission denied</color>");
                retryAt = Time.time + 10f;
                return;
            }
            if (!Active) return;
            StartCamera();
            started = true;
            if (PlayerPrefs.GetInt(TipPref, 0) == 0)
            {
                PlayerPrefs.SetInt(TipPref, 1);
                PlayerPrefs.Save();
                tipUntil = Time.time + 9f;
            }
            uiLog?.AddLog($"Photos: look at a bare wall and hold still - it shoots by itself; trigger = photo now. Saving to {Path.GetFileName(sessionDir)}.");
        }
        finally { preparing = false; }
    }

    void OpenSession()
    {
        string root = Path.Combine(Application.persistentDataPath, RootFolder);
        sessionDir = Path.Combine(root, SessionPrefix + DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(sessionDir);

        shots.Clear();
        foreach (var f in Directory.GetFiles(sessionDir, "*.json").Where(f => char.IsDigit(Path.GetFileName(f)[0])).OrderBy(f => f))
        {
            try { var m = JsonUtility.FromJson<WallPhotoMeta>(File.ReadAllText(f)); if (m != null) shots.Add(m); }
            catch (Exception e) { Debug.LogWarning($"[WallPhotos] skipping {f}: {e.Message}"); }
        }
        nextIndex = shots.Count == 0 ? 1 : shots.Max(s => s.index) + 1;
        cableByRoom.Clear(); denseByRoom.Clear();
        foreach (var sh in shots) Count(cableByRoom, sh.roomUuid);

        string denseDir = Path.Combine(sessionDir, DenseFolder);
        Directory.CreateDirectory(denseDir);
        var denseIdx = Directory.GetFiles(denseDir, "*.json")
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out int i) ? i : 0).ToList();
        denseCount = denseIdx.Count;
        var denseMetas = new List<WallPhotoMeta>();
        foreach (var f in Directory.GetFiles(denseDir, "*.json"))
        {
            try { var m = JsonUtility.FromJson<WallPhotoMeta>(File.ReadAllText(f)); if (m != null) { denseMetas.Add(m); Count(denseByRoom, m.roomUuid); } }
            catch { }
        }
        nextDenseIndex = denseIdx.Count == 0 ? 1 : denseIdx.Max() + 1;
        haveDense = false;

#if META_XR_SDK_INSTALLED
        // The house frame: the room with the smallest UUID - or the one this session already used.
        roomCacheCount = -1; // the live scan may have been (re)loaded
        var rooms = Rooms;
        houseRoomUuid = shots.Concat(denseMetas).Select(x => x.houseRoomUuid).FirstOrDefault(u => !string.IsNullOrEmpty(u) && rooms.Any(r => r.Anchor.Uuid.ToString() == u))
                        ?? rooms.Select(r => r.Anchor.Uuid.ToString()).OrderBy(u => u, StringComparer.Ordinal).FirstOrDefault();

        // Coverage of the 3D frames already taken today (seed points are stored in the house frame).
        coverage.Clear(); covDirty = true;
        var house = HouseRoom();
        if (house != null)
            foreach (var m in denseMetas.Where(m => m.houseRoomUuid == houseRoomUuid))
            {
                Vector3 camPos = house.transform.TransformPoint(m.housePosition);
                foreach (var sp in m.seedPoints)
                {
                    Vector3 p = house.transform.TransformPoint(sp);
                    AddCoverage(p, (camPos - p).normalized);
                }
            }

        // Snapshot of exactly the scan the poses refer to (room UUIDs stay the same across re-saves of one house,
        // but this makes the folder self-contained: on the PC it is a scan source of its own).
        try
        {
            string json = MRUK.Instance != null ? MRUK.Instance.SaveSceneToJsonString(true) : null;
            if (!string.IsNullOrEmpty(json)) File.WriteAllText(Path.Combine(sessionDir, MRUKPathUtility.DATA_SCENE), json);
            if (File.Exists(RoomNames.FilePath)) File.Copy(RoomNames.FilePath, Path.Combine(sessionDir, MRUKPathUtility.DATA_ROOM_NAMES), true);
        }
        catch (Exception e) { uiLog?.AddLog($"<color=orange>Photos: scan snapshot failed ({e.Message}).</color>"); }
#endif
    }

    // ---------- camera ----------

    async Task<bool> EnsurePermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(CameraPermission)) return true;
        var done = new TaskCompletionSource<bool>();
        var cb = new UnityEngine.Android.PermissionCallbacks();
        cb.PermissionGranted += _ => done.TrySetResult(true);
        cb.PermissionDenied += _ => done.TrySetResult(false);
        cb.PermissionDeniedAndDontAskAgain += _ => done.TrySetResult(false);
        UnityEngine.Android.Permission.RequestUserPermission(CameraPermission, cb);
        bool ok = await done.Task;
        if (!ok) uiLog?.AddLog("<color=red>Photos: camera permission denied (Settings > Apps > Permissions > Headset cameras).</color>");
        return ok;
#else
        await Task.CompletedTask;
        return true;
#endif
    }

    void StartCamera()
    {
#if META_XR_SDK_INSTALLED
        if (cam != null) { cam.enabled = true; return; }
        if (!PassthroughCameraAccess.IsSupported) { SetStatus("<color=red>Passthrough camera not supported</color>"); return; }
        var go = new GameObject("WallPhotoCamera");
        go.SetActive(false); // configure before OnEnable opens the camera
        go.transform.SetParent(transform, false);
        cam = go.AddComponent<PassthroughCameraAccess>();
        cam.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
        // the largest the headset offers (sharper chases for the PC tools); 1280x960 if the list isn't available
        var res = new Vector2Int(1280, 960);
        try
        {
            var sup = PassthroughCameraAccess.GetSupportedResolutions(PassthroughCameraAccess.CameraPositionType.Left);
            if (sup != null && sup.Length > 0)
            {
                res = sup.OrderByDescending(r => r.x * r.y).First();
                uiLog?.AddLog($"Photos: camera {res.x}x{res.y} (offered: {string.Join(", ", sup.Select(r => r.x + "x" + r.y))})");
            }
        }
        catch (Exception e) { Debug.LogWarning($"[WallPhotos] supported resolutions: {e.Message}"); }
        cam.RequestedResolution = res;
        go.SetActive(true);
        SetStatus("Camera starting...");
#endif
    }

    void StopCamera()
    {
#if META_XR_SDK_INSTALLED
        if (cam != null) cam.enabled = false;
        shotRequested = armed = cameraReady = false;
#endif
    }

    void OnTrigger()
    {
        if (!Active) return;
#if META_XR_SDK_INSTALLED
        if (cam == null || !cam.IsPlaying) { uiLog?.AddLog("Photos: camera not ready yet."); return; }
        shotRequested = true;
        requestDense = false; // the trigger always takes a cable photo
#endif
    }

#if META_XR_SDK_INSTALLED
    /// <summary>The camera pose in Unity's world. The API gives it in the device's own frame; when a saved scan is lined
    /// up with the house (MRUKExporter.AlignToLive shifts the tracking space), the rig's tracking space maps it into the
    /// world the rooms live in - with no shift that is (nearly) identity, as before.</summary>
    Pose CameraPose()
    {
        var p = cam.GetCameraPose();
        if (!rigSpace) { var rig = FindFirstObjectByType<OVRCameraRig>(); rigSpace = rig ? rig.trackingSpace : null; }
        return rigSpace ? new Pose(rigSpace.TransformPoint(p.position), rigSpace.rotation * p.rotation) : p;
    }
    Transform rigSpace;

    void PumpCapture()
    {
        if (!shotRequested || capturing) return;
        if (cam.IsUpdatedThisFrame)
        {
            armedPose = CameraPose();
            armedTime = cam.Timestamp;
            armed = true;
            return;
        }
        if (!armed) return;
        shotRequested = armed = false;
        Capture(armedPose, armedTime, requestDense);
    }

    void Capture(Pose pose, DateTime stamp, bool isDense)
    {
        var tex = cam.GetTexture();
        if (tex == null) return;
        capturing = true;
        if (isDense)
        {
            prevDensePos = lastDensePos; prevDenseRot = lastDenseRot; prevHaveDense = haveDense; prevDenseTime = lastDenseTime;
            lastDenseTime = Time.time; lastDensePos = pose.position; lastDenseRot = pose.rotation; haveDense = true;
        }
        else lastShotTime = Time.time;
        float reqAt = Time.realtimeSinceStartup, degAt = headDegPerSec, mpsAt = headMetersPerSec;
        var meta = BuildMeta(pose, stamp, tex.width, tex.height, isDense);
        if (meta == null)
        {
            capturing = false;
            if (!isDense) uiLog?.AddLog("<color=orange>Photos: you are outside every scanned room - not saved.</color>");
            return;
        }
        // Seed points for a splat: a grid of rays hit against the scan now; their colours come from the pixels below.
        var seedUv = new List<Vector2>();
        var seedHit = new List<Vector3>();
        var houseRoom = HouseRoom();
        if (isDense && houseRoom != null)
        {
            for (int y = 0; y < SeedRows; y++)
                for (int x = 0; x < SeedCols; x++)
                {
                    var uv = new Vector2((x + 0.5f) / SeedCols, (y + 0.5f) / SeedRows);
                    if (Hit(cam.ViewportPointToRay(uv, pose), out var p, out var nrm, out _))
                    {
                        seedUv.Add(uv); seedHit.Add(houseRoom.transform.InverseTransformPoint(p));
                        AddCoverage(p, nrm);
                    }
                }
        }

        AsyncGPUReadback.Request(tex, 0, TextureFormat.RGBA32, req =>
        {
            capturing = false;
            if (req.hasError) { if (!isDense) uiLog?.AddLog("<color=red>Photos: camera readback failed.</color>"); return; }
            var data = req.GetData<byte>();
            byte[] pixels;
            lock (pixelPool) pixels = pixelPool.Count > 0 && pixelPool.Peek().Length == data.Length ? pixelPool.Pop() : new byte[data.Length];
            data.CopyTo(pixels);
            int w = req.width, h = req.height;
            meta.imageWidth = w; meta.imageHeight = h;
            float readbackMs = (Time.realtimeSinceStartup - reqAt) * 1000f;
            meta.sharpness = Sharpness(pixels, w, h);
            var hist = isDense ? sharpDense : sharpCable;
            float median = Median(hist);
            meta.relativeSharpness = median > 0 ? meta.sharpness / median : 1f;
            hist.Add(meta.sharpness); if (hist.Count > 40) hist.RemoveAt(0);
            if (isDense && hist.Count > 8 && meta.relativeSharpness < MinRelSharpness)
            {
                // blurry: not saved; the next frame from about here is due right away
                blurryDropped++;
                lastDensePos = prevDensePos; lastDenseRot = prevDenseRot; haveDense = prevHaveDense; lastDenseTime = prevDenseTime;
                lock (pixelPool) if (pixelPool.Count < MaxPendingWrites + 1) pixelPool.Push(pixels);
                LogCapture(meta, false, "blurry", degAt, mpsAt, readbackMs, 0, 0);
                return;
            }
            meta.headDegPerSec = degAt; meta.headMetersPerSec = mpsAt;
            for (int i = 0; i < seedUv.Count; i++)
            {
                int px = Mathf.Clamp((int)(seedUv[i].x * w), 0, w - 1), py = Mathf.Clamp((int)(seedUv[i].y * h), 0, h - 1);
                int o = (py * w + px) * 4; // rows bottom-up, like viewport v
                meta.seedPoints.Add(seedHit[i]);
                meta.seedColors.Add(new Color32(pixels[o], pixels[o + 1], pixels[o + 2], 255));
            }
#if META_XR_SDK_INSTALLED
            if (isDense) PaintLive(pixels, w, h, meta);
#endif
            string jpg = Path.Combine(sessionDir, meta.file), json = Path.ChangeExtension(jpg, ".json");
            string metaJson = JsonUtility.ToJson(meta, !isDense);
            pendingWrites++;
            PendingChanges.Mark("photos");
            Task.Run(() =>
            {
                // Rows come bottom-up (Unity texture order), which is what the encoder expects: the JPG is upright,
                // with viewport (0,0) = its bottom-left pixel.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                byte[] bytes = ImageConversion.EncodeArrayToJPG(pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, 0, isDense ? 88 : 92);
                float encodeMs = (float)sw.Elapsed.TotalMilliseconds;
                lock (pixelPool) if (pixelPool.Count < MaxPendingWrites + 1) pixelPool.Push(pixels);
                File.WriteAllBytes(jpg, bytes);
                File.WriteAllText(json, metaJson); // the JSON last: a photo counts once both exist
                LogCapture(meta, true, "", degAt, mpsAt, readbackMs, encodeMs, bytes.Length / 1024);
            }).ContinueWith(t =>
            {
                pendingWrites--;
                if (t.Exception != null) Debug.LogError($"[WallPhotos] write failed: {t.Exception.GetBaseException()}");
                else if (!isDense) WallOverlays.PhotosChanged(); // "Photos on walls" picks it up
            });

            if (isDense)
            {
                denseCount++;
                nextDenseIndex++;
                Count(denseByRoom, meta.roomUuid);
                Click(0.12f);
                flashUntil = Time.time + 0.12f; flashText = null;
                if (denseCount % 5 == 0) RefreshPanel();
                if (denseCount % 25 == 0) Debug.Log($"[WallPhotos] 3D frames: {denseCount} saved, {blurryDropped} blurry dropped, coverage cells {coverage.Count}, pending writes {pendingWrites}");
                return;
            }
            shots.Add(meta);
            nextIndex++;
            Count(cableByRoom, meta.roomUuid);
            AddOutline(meta, true);
            GuideMark(meta);
            Debug.Log($"[WallPhotos] camera-head offset {(head ? Vector3.Distance(meta.worldPosition, head.position) : -1f):0.000} m (should be a few cm)");
            Debug.Log($"[WallPhotos] cable photo {meta.index} saved, sharp {meta.sharpness:0.0} (x{meta.relativeSharpness:0.00}), {meta.centerHitDistance:0.00} m {meta.centerHitLabel}{GuideSummary().Replace("\n", " - ")}");
            Haptic();
            Click(0.6f);
            flashUntil = Time.time + 0.9f; flashText = $"<color=#7dff9a>Photo {shots.Count} saved</color>";
            RefreshPanel();
        });
    }

#if META_XR_SDK_INSTALLED
    Texture2D liveTex;
    /// <summary>A 3D-scan frame painted onto the walls right away (WallScan), so you see what is covered.</summary>
    void PaintLive(byte[] pixels, int w, int h, WallPhotoMeta meta)
    {
        try
        {
            var mruk = MRUK.Instance;
            if (mruk == null) return;
            var rooms = WallScan.Rooms();
            WallScan.EnsurePlanes(rooms);
            if (!liveTex || liveTex.width != w || liveTex.height != h)
            {
                if (liveTex) Destroy(liveTex);
                liveTex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "WallScanLive" };
            }
            liveTex.SetPixelData(pixels, 0);
            liveTex.Apply(false);
            WallScan.Splat(liveTex, meta, rooms, WallScan.Key(Path.GetFileName(sessionDir), meta.index.ToString("0000")));
        }
        catch (Exception e) { Debug.LogWarning($"[WallPhotos] live wall scan: {e.Message}"); }
    }
#endif

    /// <summary>"  -  walls 63 %": how much of this room's walls the 3D scan has seen so far (empty until the room has planes).</summary>
    string CoverageText()
    {
#if META_XR_SDK_INSTALLED
        float c = currentRoomUuid != null ? WallScan.CoverageOf(currentRoomUuid) : -1f;
        return c >= 0f ? $"  -  walls {Mathf.RoundToInt(c * 100f)} %" : "";
#else
        return "";
#endif
    }

    /// <summary>Mean luminance gradient over a sparse grid (every 4th pixel, 2 px steps) - drops with motion blur and
    /// defocus. Absolute values depend on the surface (bare brick >> smooth plaster), so frames are compared with the
    /// recent median, not with a fixed limit.</summary>
    static float Sharpness(byte[] px, int w, int h)
    {
        double sum = 0; int n = 0;
        for (int y = 8; y < h - 8; y += 4)
            for (int x = 8; x < w - 8; x += 4)
            {
                int o = (y * w + x) * 4, ox = o + 8, oy = o + w * 8;
                int l = px[o] * 2 + px[o + 1] * 5 + px[o + 2];
                int lx = px[ox] * 2 + px[ox + 1] * 5 + px[ox + 2];
                int ly = px[oy] * 2 + px[oy + 1] * 5 + px[oy + 2];
                sum += Math.Abs(lx - l) + Math.Abs(ly - l);
                n++;
            }
        return n > 0 ? (float)(sum / n / 8.0) : 0f;
    }

    static float Median(List<float> v)
    {
        if (v.Count == 0) return 0f;
        var s = v.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }

    /// <summary>One line per capture in the session's capture_log.csv - for measuring how the capture behaves.</summary>
    void LogCapture(WallPhotoMeta m, bool saved, string reason, float deg, float mps, float readbackMs, float encodeMs, int kb)
    {
        string path = Path.Combine(sessionDir ?? "", "capture_log.csv");
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string line = string.Join(";", DateTime.Now.ToString("HH:mm:ss.fff"), m.dense ? "3d" : "cable", m.index.ToString(),
            saved ? "saved" : "dropped", reason, m.roomName ?? (m.roomUuid ?? "").Substring(0, Math.Min(8, (m.roomUuid ?? "").Length)),
            deg.ToString("0.0", ci), mps.ToString("0.00", ci), m.sharpness.ToString("0.0", ci), m.relativeSharpness.ToString("0.00", ci),
            readbackMs.ToString("0", ci), encodeMs.ToString("0", ci), kb.ToString(), m.centerHitDistance.ToString("0.00", ci),
            m.centerHitLabel ?? "", m.imageWidth + "x" + m.imageHeight);
        try
        {
            lock (logLock)
            {
                if (!File.Exists(path)) File.AppendAllText(path, "time;kind;index;result;reason;room;head_deg_s;head_m_s;sharpness;rel_sharpness;readback_ms;encode_ms;kb;center_dist_m;center_label;size\n");
                File.AppendAllText(path, line + "\n");
            }
        }
        catch { }
    }

    MRUKRoom HouseRoom() => MRUK.Instance == null || houseRoomUuid == null ? null
        : MRUK.Instance.Rooms.FirstOrDefault(r => r.Anchor.Uuid.ToString() == houseRoomUuid);

    /// <summary>Dense frame due: the camera moved/turned enough since the last one, slowly enough to be sharp.</summary>
    bool DenseDue()
    {
        if (Time.time - lastDenseTime < DenseInterval) return false;
        if (headDegPerSec > DenseMaxDegPerSec || headMetersPerSec > DenseMaxMetersPerSec) return false;
        if (!haveDense) return true;
        Pose p = CameraPose();
        if (Vector3.Distance(p.position, lastDensePos) < DenseMove && Quaternion.Angle(p.rotation, lastDenseRot) < DenseTurn) return false;
        // only a frame that adds something: enough of the view on surface not yet seen twice (no pile of near-copies)
        if (Time.time < noveltyCheckAt) return false;
        noveltyCheckAt = Time.time + 0.2f;
        return NewSurfaceShare(p) >= MinNewShare;
    }

    float noveltyCheckAt;
    const float MinNewShare = 0.15f;

    /// <summary>Share of the camera view (7 x 5 rays against the scan) landing on 10 cm cells seen by fewer than two
    /// 3D-scan frames so far.</summary>
    float NewSurfaceShare(Pose pose)
    {
        int hits = 0, fresh = 0;
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 7; x++)
            {
                var uv = new Vector2((x + 0.5f) / 7f, (y + 0.5f) / 5f);
                if (!Hit(cam.ViewportPointToRay(uv, pose), out var hp, out _, out _)) continue;
                hits++;
                if (!coverage.TryGetValue(Vector3Int.FloorToInt(hp / CovCellSize), out var c) || c.count < 2) fresh++;
            }
        return hits == 0 ? 0f : fresh / (float)hits;
    }

    WallPhotoMeta BuildMeta(Pose pose, DateTime stamp, int w, int h, bool isDense)
    {
        var rooms = Rooms;
        var room = rooms.FirstOrDefault(r => r.IsPositionInRoom(pose.position))
                   ?? rooms.OrderBy(r => Vector3.Distance(r.transform.position, pose.position)).FirstOrDefault();
        if (room == null) return null;

        // Pinhole model in viewport units, taken from the API itself (so any crop/scale between sensor and image is
        // already in it): camera-local direction of viewport (u,v) = ((u-cu)/fu, (v-cv)/fv, 1), (0,0) bottom-left.
        Quaternion inv = Quaternion.Inverse(pose.rotation);
        Vector3 d00 = inv * cam.ViewportPointToRay(new Vector2(0, 0), pose).direction;
        Vector3 d11 = inv * cam.ViewportPointToRay(new Vector2(1, 1), pose).direction;
        d00 /= d00.z; d11 /= d11.z;
        float fu = 1f / (d11.x - d00.x), fv = 1f / (d11.y - d00.y);

        var rt = room.transform;
        var intr = cam.Intrinsics;
        int index = isDense ? nextDenseIndex : nextIndex;
        var m = new WallPhotoMeta
        {
            index = index,
            dense = isDense,
            file = (isDense ? DenseFolder + "/" : "") + index.ToString("0000") + ".jpg",
            capturedAt = DateTime.Now.ToString("o"),
            cameraTimestamp = stamp.ToString("o"),
            camera = cam.CameraPosition.ToString(),
            roomUuid = room.Anchor.Uuid.ToString(),
            roomName = RoomNames.TryGet(room.Anchor.Uuid.ToString(), out var rn) ? rn : null,
            localPosition = rt.InverseTransformPoint(pose.position),
            localRotation = Quaternion.Inverse(rt.rotation) * pose.rotation,
            worldPosition = pose.position,
            worldRotation = pose.rotation,
            fu = fu, fv = fv, cu = -d00.x * fu, cv = -d00.y * fv,
            focalLength = intr.FocalLength,
            principalPoint = intr.PrincipalPoint,
            sensorResolution = new Vector2(intr.SensorResolution.x, intr.SensorResolution.y),
            imageWidth = w, imageHeight = h,
        };
        var house = HouseRoom();
        if (house != null)
        {
            m.houseRoomUuid = houseRoomUuid;
            m.housePosition = house.transform.InverseTransformPoint(pose.position);
            m.houseRotation = Quaternion.Inverse(house.transform.rotation) * pose.rotation;
        }
        if (CenterHit(pose, out float cd, out string cl)) { m.centerHitDistance = cd; m.centerHitLabel = cl; }
        return m;
    }

    /// <summary>Distance and label of what the photo's centre looks at, or false if it sees nothing scanned.</summary>
    bool CenterHit(Pose pose, out float dist, out string label)
    {
        var ray = cam.ViewportPointToRay(new Vector2(0.5f, 0.5f), pose);
        dist = 0;
        if (!Hit(ray, out var p, out label)) return false;
        dist = Vector3.Distance(ray.origin, p);
        return true;
    }

    static bool Hit(Ray ray, out Vector3 point, out string label) => Hit(ray, out point, out _, out label);

    static bool Hit(Ray ray, out Vector3 point, out Vector3 normal, out string label)
    {
        point = Vector3.zero; normal = Vector3.up; label = null;
        float best = float.MaxValue;
        if (MRUK.Instance == null) return false;
        foreach (var room in Rooms)
        {
            if (room.Raycast(ray, 15f, out RaycastHit hit, out MRUKAnchor anchor) && hit.distance < best)
            {
                best = hit.distance; point = hit.point; normal = hit.normal;
                label = anchor != null ? anchor.Label.ToString() : null;
            }
        }
        return best < float.MaxValue;
    }

    void TrackRoom()
    {
        if (!head || MRUK.Instance == null || Time.frameCount % 15 != 0) return;
        var room = Rooms.FirstOrDefault(r => r.IsPositionInRoom(head.position));
        string uuid = room != null ? room.Anchor.Uuid.ToString() : null;
        if (uuid == currentRoomUuid) return;
        currentRoomUuid = uuid;
        currentRoomName = room != null ? MRUKDataProcessor.GetRoomLabel(room) : null;
        RefreshPanel();
        Debug.Log($"[WallPhotos] room: {currentRoomName ?? "outside"} ({uuid}){(dense ? "" : GuideSummary().Replace("\n", " - "))}");
    }

    // ---------- photo guide: what of the current room isn't photographed yet ----------

    // The room's walls, floor and ceiling as a 25 cm grid; a cell counts as photographed once a cable photo saw it
    // (in the image, closer than 4.5 m, not too oblique). Cells still missing are red patches on the real surfaces
    // (with Outlines on, in cable mode) and the panel names the surfaces still missing.
    class GuideCell { public Vector3 p, n; public int surface; public bool seen; }
    readonly List<GuideCell> guide = new List<GuideCell>();
    readonly List<string> guideNames = new List<string>();
    string guideRoom;
    GameObject guideGo; Mesh guideMesh; bool guideDirty;
    const float GuideStep = 0.25f, GuidePatch = 0.17f, GuideMaxDist = 4.5f, GuideMaxAngle = 72f;

    void BuildGuide()
    {
        guide.Clear(); guideNames.Clear(); guideDirty = true;
        guideRoom = currentRoomUuid;
        var room = currentRoomUuid == null ? null : Rooms.FirstOrDefault(r => r.Anchor.Uuid.ToString() == currentRoomUuid);
        if (room == null) return;
        var openings = Rooms.SelectMany(r => r.Anchors).Where(a => a != null && a.PlaneRect.HasValue &&
            (a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW"))).ToList();
        var walls = room.Anchors.Where(a => a != null && a.PlaneRect.HasValue && MRUKDataProcessor.IsStructuralWall(a))
            .OrderBy(a => Mathf.Atan2(a.transform.forward.x, a.transform.forward.z)).ToList();
        void Add(MRUKAnchor a, string name, bool flat)
        {
            int idx = guideNames.Count; guideNames.Add(name);
            var r = a.PlaneRect.Value;
            for (float y = r.yMin + GuideStep / 2; y < r.yMax; y += GuideStep)
                for (float x = r.xMin + GuideStep / 2; x < r.xMax; x += GuideStep)
                {
                    if (flat && !InPlane(a, x, y)) continue;
                    Vector3 p = a.transform.TransformPoint(new Vector3(x, y, 0));
                    if (!flat && openings.Any(o => InOpening(o, p))) continue;
                    guide.Add(new GuideCell { p = p, n = a.transform.forward, surface = idx });
                }
        }
        for (int i = 0; i < walls.Count; i++) Add(walls[i], $"wall {i + 1}", false);
        foreach (var f in room.FloorAnchors) if (f != null && f.PlaneRect.HasValue) Add(f, "floor", true);
        foreach (var c in room.CeilingAnchors) if (c != null && c.PlaneRect.HasValue) Add(c, "ceiling", true);
        foreach (var sh in shots) GuideMark(sh, false);
    }

    static bool InOpening(MRUKAnchor o, Vector3 p)
    {
        Vector3 l = o.transform.InverseTransformPoint(p);
        var r = o.PlaneRect.Value;
        return Mathf.Abs(l.z) < 0.3f && l.x > r.xMin && l.x < r.xMax && l.y > r.yMin && l.y < r.yMax;
    }

    static bool InPlane(MRUKAnchor a, float x, float y)
    {
        var b = a.PlaneBoundary2D;
        if (b == null || b.Count < 3) return true;
        bool inside = false;
        for (int i = 0, j = b.Count - 1; i < b.Count; j = i++)
            if ((b[i].y > y) != (b[j].y > y) && x < (b[j].x - b[i].x) * (y - b[i].y) / (b[j].y - b[i].y) + b[i].x) inside = !inside;
        return inside;
    }

    void GuideMark(WallPhotoMeta s, bool dirty = true)
    {
        if (guide.Count == 0 || !ShotWorld(s, out var pos, out var rot)) return;
        var inv = Quaternion.Inverse(rot);
        foreach (var c in guide)
        {
            if (c.seen) continue;
            Vector3 d = c.p - pos;
            float dist = d.magnitude;
            if (dist > GuideMaxDist || Vector3.Angle(-d, c.n) > GuideMaxAngle) continue;
            Vector3 l = inv * d;
            if (l.z < 0.15f) continue;
            float vx = s.cu + s.fu * l.x / l.z, vy = s.cv + s.fv * l.y / l.z;
            if (vx > 0.03f && vx < 0.97f && vy > 0.03f && vy < 0.97f) c.seen = true;
        }
        if (dirty) { guideDirty = true; RefreshPanel(); }
    }

    /// <summary>" - 72 % photographed, missing: ceiling, wall 3" for the panel.</summary>
    string GuideSummary()
    {
        if (guideRoom != currentRoomUuid) BuildGuide();
        if (guide.Count == 0) return "";
        int pct = Mathf.RoundToInt(100f * guide.Count(c => c.seen) / guide.Count);
        var missing = new List<string>();
        for (int i = 0; i < guideNames.Count; i++)
        {
            var cells = guide.Where(c => c.surface == i).ToList();
            if (cells.Count > 0 && cells.Count(c => c.seen) < cells.Count * 0.8f && !missing.Contains(guideNames[i])) missing.Add(guideNames[i]);
        }
        return pct >= 97 ? $"\n<color=#7dff9a>Room photographed ({pct} %)</color>"
                         : $"\n{pct} % photographed - <color=#ffb347>missing: {string.Join(", ", missing)}</color>";
    }

    void UpdateGuide()
    {
        bool show = !dense && showCoverage && guide.Count > 0;
        if (guideRoom != currentRoomUuid) { BuildGuide(); RefreshPanel(); }
        if (guideGo) guideGo.SetActive(show);
        if (!show || !guideDirty) return;
        guideDirty = false;
        if (!guideGo)
        {
            guideGo = new GameObject("PhotoGuide"); // world space, no parent (see AddOutline)
            guideMesh = new Mesh { name = "PhotoGuide", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            guideGo.AddComponent<MeshFilter>().sharedMesh = guideMesh;
            var mr = guideGo.AddComponent<MeshRenderer>();
            if (!fillMat) fillMat = new Material(Shader.Find("Sprites/Default")) { name = "WallPhotoFill" };
            mr.sharedMaterial = fillMat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }
        var verts = new List<Vector3>(); var cols = new List<Color>(); var tris = new List<int>();
        var red = new Color(1f, 0.25f, 0.2f, 0.32f);
        foreach (var c in guide)
        {
            if (c.seen) continue;
            Vector3 t = Vector3.Cross(c.n, Mathf.Abs(c.n.y) > 0.9f ? Vector3.right : Vector3.up).normalized * (GuidePatch * 0.5f);
            Vector3 b = Vector3.Cross(c.n, t);
            Vector3 o = c.p + c.n * 0.012f;
            int k = verts.Count;
            verts.Add(o - t - b); verts.Add(o + t - b); verts.Add(o + t + b); verts.Add(o - t + b);
            cols.Add(red); cols.Add(red); cols.Add(red); cols.Add(red);
            tris.Add(k); tris.Add(k + 1); tris.Add(k + 2); tris.Add(k); tris.Add(k + 2); tris.Add(k + 3);
        }
        guideMesh.Clear();
        guideMesh.SetVertices(verts); guideMesh.SetColors(cols); guideMesh.SetTriangles(tris, 0);
        guideMesh.RecalculateBounds();
    }

    // ---------- 3D-scan coverage ----------

    void AddCoverage(Vector3 p, Vector3 n)
    {
        var key = Vector3Int.FloorToInt(p / CovCellSize);
        if (coverage.TryGetValue(key, out var c)) c.count++;
        else coverage[key] = new CovCell { p = p, n = n, count = 1 };
        covDirty = true;
    }

    void UpdateCoverage()
    {
        // the wall scan's blue veil (WallOverlays) shows coverage now - these patches only where there is no wall scan
        bool show = dense && showCoverage && coverage.Count > 0 && !WallScan.Planes.Any();
        if (covGo) covGo.SetActive(show);
        if (!show || !covDirty || Time.time - covBuiltAt < 0.3f) return;
        covDirty = false; covBuiltAt = Time.time;
        if (!covGo)
        {
            covGo = new GameObject("ScanCoverage"); // world space, no parent (see AddOutline)
            covMesh = new Mesh { name = "ScanCoverage", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            covGo.AddComponent<MeshFilter>().sharedMesh = covMesh;
            var mr = covGo.AddComponent<MeshRenderer>();
            if (!fillMat) fillMat = new Material(Shader.Find("Sprites/Default")) { name = "WallPhotoFill" };
            mr.sharedMaterial = fillMat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }
        int n = coverage.Count;
        var verts = new Vector3[n * 4]; var cols = new Color[n * 4]; var tris = new int[n * 6];
        int i = 0;
        foreach (var c in coverage.Values)
        {
            Vector3 nrm = c.n.sqrMagnitude > 1e-6f ? c.n.normalized : Vector3.up;
            Vector3 t = Vector3.Cross(nrm, Mathf.Abs(nrm.y) > 0.9f ? Vector3.right : Vector3.up).normalized * (CovPatch * 0.5f);
            Vector3 b = Vector3.Cross(nrm, t);
            Vector3 o = c.p + nrm * 0.015f;
            verts[i * 4] = o - t - b; verts[i * 4 + 1] = o + t - b; verts[i * 4 + 2] = o + t + b; verts[i * 4 + 3] = o - t + b;
            Color col = c.count >= 3 ? new Color(0.25f, 1f, 0.45f, 0.75f) : c.count == 2 ? new Color(1f, 0.9f, 0.2f, 0.75f) : new Color(1f, 0.5f, 0.1f, 0.75f);
            for (int k = 0; k < 4; k++) cols[i * 4 + k] = col;
            tris[i * 6] = i * 4; tris[i * 6 + 1] = i * 4 + 1; tris[i * 6 + 2] = i * 4 + 2;
            tris[i * 6 + 3] = i * 4; tris[i * 6 + 4] = i * 4 + 2; tris[i * 6 + 5] = i * 4 + 3;
            i++;
        }
        covMesh.Clear();
        covMesh.vertices = verts; covMesh.colors = cols; covMesh.triangles = tris;
        covMesh.RecalculateBounds();
    }

    // ---------- auto shot ----------

    void TrackStillness()
    {
        if (!head) return;
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        float deg = headDegPerSec = Quaternion.Angle(lastHeadRot, head.rotation) / dt;
        float mps = headMetersPerSec = Vector3.Distance(lastHeadPos, head.position) / dt;
        lastHeadPos = head.position; lastHeadRot = head.rotation;
        stillFor = deg < StillDegPerSec && mps < StillMetersPerSec ? stillFor + dt : 0f;
    }

    /// <summary>What the auto shot sees right now: nothing scanned, an already covered spot, a new spot while the head
    /// still moves, or a new spot it is settling on (shoots once stillFor reaches StillTime).</summary>
    View ClassifyView()
    {
        Pose pose = CameraPose();
        var ray = cam.ViewportPointToRay(new Vector2(0.5f, 0.5f), pose);
        if (!Hit(ray, out var hp, out var hn, out _)) return View.Nothing;
        // what the PC-side tools can use: the route detector skips surfaces seen from > 4.5 m or at > 72 deg
        if (Vector3.Distance(ray.origin, hp) > MaxShotDist) return View.TooFar;
        if (Vector3.Angle(-ray.direction, hn) > MaxShotAngle) return View.TooOblique;
        if (!IsNewView()) return View.Covered;
        return stillFor > 0 ? View.Settling : View.Moving;
    }

    bool IsNewView()
    {
        Pose pose = CameraPose();
        var ray = cam.ViewportPointToRay(new Vector2(0.5f, 0.5f), pose);
        if (!Hit(ray, out var p, out _)) return false; // looking at nothing scanned (out of a window...)
        foreach (var s in shots)
        {
            if (!ShotWorld(s, out var sp, out var sr)) continue;
            Vector3 sdir = sr * Vector3.forward;
            bool sameSpot = s.centerHitDistance > 0 && Vector3.Distance(sp + sdir * s.centerHitDistance, p) < NewDistance;
            bool sameDir = Vector3.Angle(sdir, ray.direction) < NewAngle && Vector3.Distance(sp, pose.position) < NewDistance;
            if (sameSpot || sameDir) return false;
        }
        return true;
    }

    /// <summary>A saved shot's camera pose in the current world (via its room).</summary>
    static bool ShotWorld(WallPhotoMeta s, out Vector3 pos, out Quaternion rot)
    {
        pos = s.worldPosition; rot = s.worldRotation;
        if (MRUK.Instance == null) return false;
        var room = MRUK.Instance.Rooms.FirstOrDefault(r => r.Anchor.Uuid.ToString() == s.roomUuid);
        if (room == null) return true; // other house / room gone: the stored world pose is the best we have
        pos = room.transform.TransformPoint(s.localPosition);
        rot = room.transform.rotation * s.localRotation;
        return true;
    }

    // ---------- coverage ----------

    void RebuildOutlines()
    {
        ClearOutlines();
        if (!showCoverage) return;
        for (int i = 0; i < shots.Count; i++) AddOutline(shots[i], i == shots.Count - 1);
    }

    /// <summary>The photo's footprint on the scanned surfaces: a grid of rays through the image hit against the scan
    /// (so it wraps around room corners), drawn as a translucent fill just in front of the surfaces plus its outline -
    /// easy to see at a glance what is already covered.</summary>
    void AddOutline(WallPhotoMeta s, bool last)
    {
        if (!showCoverage || !ShotWorld(s, out var pos, out var rot)) return;
        if (last && footprints.Count > 0) Recolor(footprints[footprints.Count - 1], CoverageColor);

        // Grid of surface points (null where the ray hits nothing scanned).
        var grid = new Vector3?[FootGrid, FootGrid];
        for (int j = 0; j < FootGrid; j++)
            for (int i = 0; i < FootGrid; i++)
            {
                var uv = new Vector2(i / (float)(FootGrid - 1), j / (float)(FootGrid - 1));
                Vector3 dir = rot * new Vector3((uv.x - s.cu) / s.fu, (uv.y - s.cv) / s.fv, 1f).normalized;
                if (Hit(new Ray(pos, dir), out var p, out _)) grid[i, j] = p - dir * 0.02f;
            }

        // Outline along the grid's border.
        var border = new List<Vector3>();
        void Add(int i, int j) { if (grid[i, j].HasValue) border.Add(grid[i, j].Value); }
        for (int i = 0; i < FootGrid; i++) Add(i, 0);
        for (int j = 1; j < FootGrid; j++) Add(FootGrid - 1, j);
        for (int i = FootGrid - 2; i >= 0; i--) Add(i, FootGrid - 1);
        for (int j = FootGrid - 2; j > 0; j--) Add(0, j);
        if (border.Count < 3) return;

        // Fill: every grid cell whose 4 corners hit and lie close together (not spanning a depth jump).
        var verts = new List<Vector3>();
        var tris = new List<int>();
        var idx = new int[FootGrid, FootGrid];
        for (int j = 0; j < FootGrid; j++) for (int i = 0; i < FootGrid; i++)
        {
            idx[i, j] = -1;
            if (grid[i, j].HasValue) { idx[i, j] = verts.Count; verts.Add(grid[i, j].Value); }
        }
        for (int j = 0; j < FootGrid - 1; j++) for (int i = 0; i < FootGrid - 1; i++)
        {
            int a = idx[i, j], b = idx[i + 1, j], c = idx[i + 1, j + 1], d = idx[i, j + 1];
            if (a < 0 || b < 0 || c < 0 || d < 0) continue;
            float span = Mathf.Max(Vector3.Distance(verts[a], verts[c]), Vector3.Distance(verts[b], verts[d]));
            if (span > 1.2f) continue;
            tris.AddRange(new[] { a, b, c, a, c, d });
        }

        if (!lineMat) lineMat = new Material(Shader.Find("Sprites/Default")) { name = "WallPhotoOutline" };
        if (!fillMat) fillMat = new Material(Shader.Find("Sprites/Default")) { name = "WallPhotoFill" };

        var fp = new Footprint { go = new GameObject("PhotoFootprint_" + s.index) };
        // No parent: the fill mesh is in world coordinates, and this component sits on the menu object, which isn't
        // at the origin - parented, the fill was drawn shifted away from the walls (the outline is world-space anyway).
        var lr = fp.line = fp.go.AddComponent<LineRenderer>();
        lr.sharedMaterial = lineMat;
        lr.useWorldSpace = true;
        lr.loop = true;
        lr.positionCount = border.Count;
        lr.SetPositions(border.ToArray());
        lr.startWidth = lr.endWidth = 0.01f;
        lr.shadowCastingMode = ShadowCastingMode.Off;
        lr.receiveShadows = false;
        if (tris.Count > 0)
        {
            var fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(fp.go.transform, false);
            fp.mesh = new Mesh { name = "PhotoFootprintFill" };
            fp.mesh.SetVertices(verts);
            fp.mesh.SetTriangles(tris, 0);
            fillGo.AddComponent<MeshFilter>().sharedMesh = fp.mesh;
            var mr = fillGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = fillMat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }
        Recolor(fp, last ? LastColor : CoverageColor);
        footprints.Add(fp);
    }

    /// <summary>Outline opaque, fill translucent - both via vertex colours (Sprites/Default), one shared material.</summary>
    static void Recolor(Footprint fp, Color c)
    {
        if (fp.line) fp.line.startColor = fp.line.endColor = c;
        if (fp.mesh)
        {
            var fill = new Color(c.r, c.g, c.b, 0.45f); // over passthrough a translucent pixel also lets the camera image through, so this reads much fainter than it looks in the editor
            var cols = new Color[fp.mesh.vertexCount];
            for (int i = 0; i < cols.Length; i++) cols[i] = fill;
            fp.mesh.colors = cols;
        }
    }
#endif

    void ClearOutlines()
    {
        foreach (var fp in footprints)
        {
            if (fp.go) Destroy(fp.go);
            if (fp.mesh) Destroy(fp.mesh);
        }
        footprints.Clear();
    }

    void UndoLast()
    {
        if (shots.Count == 0) return;
        if (pendingWrites > 0) { uiLog?.AddLog("Photos: still saving, try again in a moment."); return; }
        var s = shots[shots.Count - 1];
        shots.RemoveAt(shots.Count - 1);
        if (s.roomUuid != null && cableByRoom.TryGetValue(s.roomUuid, out int n)) cableByRoom[s.roomUuid] = Mathf.Max(0, n - 1);
        string jpg = Path.Combine(sessionDir, s.file);
        try { File.Delete(Path.ChangeExtension(jpg, ".json")); File.Delete(jpg); } catch (Exception e) { Debug.LogWarning(e); }
        WallOverlays.PhotosChanged();
#if META_XR_SDK_INSTALLED
        guideRoom = "-"; // rebuilt without the deleted photo
#endif
        nextIndex = shots.Count == 0 ? 1 : shots.Max(x => x.index) + 1;
#if META_XR_SDK_INSTALLED
        RebuildOutlines();
#endif
        RefreshPanel();
        uiLog?.AddLog($"Photos: deleted {s.file}.");
    }

    // ---------- panel ----------

    void BuildPanel()
    {
        ClosePanel();
        if (!head) return;
        panel = XRUi.CreateWorldCanvas("WallPhotoPanel", new Vector2(PW, PH), PScale);
        var t = panel.transform;
        panelDrag = PanelDrag.Attach(panel, PW, PH);
        panelPlaced = false;
        // back where you last put it (this session), if that is still near you
        if (savedPanel.HasValue && Vector3.Distance(savedPanel.Value.position, head.position) < 2.5f)
        { t.SetPositionAndRotation(savedPanel.Value.position, savedPanel.Value.rotation); panelPlaced = true; }
        FollowHand();
        XRUi.CreatePanel(t, "Background", XRUi.PanelColor, 0, 0, PW, PH);

        // which of the two (3D scan / photos) is picked in the menu's Tools tab - the title says which
        float x0 = 22, fullW = PW - 44, y = 14;
        countText = XRUi.CreateText(t, "Title", "", 24, TextAlignmentOptions.MidlineLeft, x0, y, fullW, 36, XRUi.TextColor, FontStyles.Bold);
        y += 38;
        roomText = XRUi.CreateText(t, "Room", "", 18, TextAlignmentOptions.TopLeft, x0, y, fullW, 50, XRUi.AccentText);
        y += 54;
        float w3 = (fullW - Gap * 2) / 3f;
        coverageButton = XRUi.CreateButton(t, "Outlines", x0, y, w3, RowH, () =>
        {
            showCoverage = !showCoverage;
#if META_XR_SDK_INSTALLED
            RebuildOutlines();
#endif
            RefreshPanel();
        }, 20);
        var undo = XRUi.CreateButton(t, "Undo photo", x0 + w3 + Gap, y, w3, RowH, UndoLast, 19);
        XRUi.SetTint(undo, XRUi.ButtonDangerColor);
        XRUi.CreateButton(t, "Done", x0 + (w3 + Gap) * 2, y, w3, RowH, () => editMode?.LeaveTool(), 22);
        y += RowH + 4;
        statusText = XRUi.CreateText(t, "Hint", "", 16, TextAlignmentOptions.TopLeft, x0, y, fullW, PH - y - 4, XRUi.MutedText);
        RefreshPanel();
    }

    /// <summary>Set by the menu before opening the tool: start in 3D-scan mode instead of cable photos.</summary>
    public static bool StartDense;
    /// <summary>True while the 3D scan (dense frames) is on.</summary>
    public bool Dense => dense;

    public void SetDense(bool on)
    {
        if (dense == on) return;
        Debug.Log($"[WallPhotos] mode: {(on ? "3D scan" : "cable photos")} (3D frames so far {denseCount}, blurry dropped {blurryDropped})");
        dense = on; haveDense = false; stillFor = 0;
        uiLog?.AddLog(on ? "3D scan: look at what is still blue - walls, floor, ceiling - from a couple of spots."
                         : "Photos: cable photos - look at a bare wall and hold still.");
        tipUntil = 0;
        RefreshPanel();
    }

    /// <summary>Just right of centre in front of you when it opens, then world-locked like the other panels; it comes
    /// back in front of you only when you've walked away from it (and haven't grip-dragged it somewhere).</summary>
    void FollowHand()
    {
        if (!panel || !head || (panelDrag && panelDrag.Moved)) return;
        if (panelPlaced && !XRUi.LeftBehind(panel.transform, head)) return;
        XRUi.PlaceInFront(panel.transform, head, 0.6f, 0.12f, 0.2f);
        panelPlaced = true;
    }
    bool panelPlaced;

    void RefreshPanel()
    {
        if (!panel) return;
        countText.text = dense ? $"3D scan  <color=#9aa6b8><size=80%>{denseCount} frames today</size></color>" : $"Photos  <color=#9aa6b8><size=80%>{shots.Count} today</size></color>";
        if (currentRoomUuid != null)
        {
            int c = cableByRoom.TryGetValue(currentRoomUuid, out int a) ? a : 0, d = denseByRoom.TryGetValue(currentRoomUuid, out int b) ? b : 0;
            string dHint = d == 0 ? "" : d < 120 ? " <color=#ffb347>(aim for 150+)</color>" : " <color=#7dff9a>ok</color>";
            roomText.text = $"{currentRoomName}:  {c} photos,  {d} 3D frames{dHint}";
#if META_XR_SDK_INSTALLED
            if (!dense) roomText.text += GuideSummary();
#endif
        }
        else roomText.text = "Outside the scanned rooms";
        if (!statusText.text.StartsWith("<color=red>"))
            statusText.text = dense
                ? "Look around slowly; it records by itself. Blue = not scanned yet, it fades as you look. Done when no blue is left. Trigger = a single photo."
                : "Look at a bare wall, floor or ceiling and hold still; it shoots by itself. Trigger = photo now. Green = covered, red = still missing.";

        XRUi.SetToggle(coverageButton, showCoverage);
    }

    void SetStatus(string s) { if (statusText) statusText.text = s; uiLog?.AddLog("Photos: " + s); }

    static Pose? savedPanel;

    void ClosePanel()
    {
        if (panel && panelDrag && panelDrag.Moved) savedPanel = new Pose(panel.transform.position, panel.transform.rotation);
        if (panel) Destroy(panel.gameObject);
        panel = null;
    }

    // ---------- HUD ----------

    const float HudW = 900f, HudH = 120f, HudScale = 0.0011f;

    void BuildHud()
    {
        if (hud) return;
        hud = XRUi.CreateWorldCanvas("WallPhotoHud", new Vector2(HudW, HudH), HudScale);
        var ray = hud.GetComponent<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>();
        if (ray) Destroy(ray); // display only - never catches the pointer
        var t = hud.transform;
        hudFrame = XRUi.CreatePanel(t, "Frame", new Color(1, 1, 1, 0f), 0, 0, HudW, HudH);
        XRUi.CreatePanel(t, "Back", new Color(0.03f, 0.04f, 0.06f, 0.72f), 6, 6, HudW - 12, HudH - 12);
        hudText = XRUi.CreateText(t, "Text", "", 34, TextAlignmentOptions.Center, 16, 10, HudW - 32, HudH - 34, Color.white, FontStyles.Bold);
        XRUi.CreatePanel(t, "BarBack", new Color(1, 1, 1, 0.12f), 40, HudH - 22, HudW - 80, 8);
        hudBar = XRUi.CreatePanel(t, "Bar", new Color(0.45f, 0.85f, 1f, 1f), 40, HudH - 22, 0, 8);
        foreach (var g in hud.GetComponentsInChildren<UnityEngine.UI.Graphic>()) g.raycastTarget = false;
    }

    void CloseHud()
    {
        if (hud) Destroy(hud.gameObject);
        hud = null;
    }

    void SetHud(string text)
    {
        if (hudText) hudText.text = text;
        SetBar(0);
    }

    void SetBar(float f)
    {
        if (!hudBar) return;
        var rt = hudBar.rectTransform;
        XRUi.Place(rt, 40, HudH - 22, (HudW - 80) * Mathf.Clamp01(f), 8);
    }

    /// <summary>One line telling why it is or isn't shooting right now.</summary>
    void UpdateHud()
    {
        if (!hud || !started) return;
        float now = Time.time;
        hudFrame.color = new Color(1, 1, 1, now < flashUntil ? 0.85f : 0f);
        if (now < tipUntil)
        {
            SetHud("<size=26>Look at a bare wall and hold still; it shoots by itself.\nGreen outline = covered.</size>");
            return;
        }
        if (now < flashUntil && flashText != null) { SetHud(flashText); return; }
#if META_XR_SDK_INSTALLED
        if (cam == null || !cam.IsPlaying) { SetHud("Camera starting..."); return; }
        if (dense)
        {
            bool tooFast = headDegPerSec > DenseMaxDegPerSec || headMetersPerSec > DenseMaxMetersPerSec;
            SetHud(tooFast ? "<color=#ffb347>Slower - the images get blurry</color>"
                           : (currentRoomUuid != null && denseByRoom.TryGetValue(currentRoomUuid, out int inRoom)
                               ? $"<color=#ff5050>\u25CF</color> 3D scan  -  {inRoom} frames in this room" + CoverageText() + (blurryDropped > 0 ? $"<size=24>  ({blurryDropped} blurry dropped)</size>" : "")
                               : $"<color=#ff5050>\u25CF</color> 3D scan  -  starting..."));
            return;
        }
        switch (view)
        {
            case View.Nothing: SetHud("<color=#aab4c4>Look at a wall, floor or ceiling</color>"); break;
            case View.Covered: SetHud("<color=#7dff9a>Already photographed</color> <size=26>- look somewhere else</size>"); break;
            case View.Moving: SetHud("Hold still to take a photo"); break;
            case View.TooFar: SetHud("<color=#ffb347>Too far</color> <size=26>- step closer (under 3.5 m)</size>"); break;
            case View.TooOblique: SetHud("<color=#ffb347>Too slanted</color> <size=26>- face the surface more</size>"); break;
            case View.Settling:
                hudText.text = "Hold still...";
                SetBar(stillFor / StillTime);
                break;
            default: SetHud(capturing || shotRequested ? "Taking photo..." : ""); break;
        }
#endif
    }

    /// <summary>A little below the centre of view, ~1.2 m away, gently following the head.</summary>
    void PlaceHud()
    {
        if (!hud || !head) return;
        Vector3 target = head.position + head.forward * 1.2f - head.up * 0.32f;
        var t = hud.transform;
        t.position = Vector3.Lerp(t.position, target, 1f - Mathf.Exp(-10f * Time.deltaTime));
        t.rotation = Quaternion.LookRotation(t.position - head.position, head.up);
    }

    static void Count(Dictionary<string, int> map, string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        map[key] = map.TryGetValue(key, out int n) ? n + 1 : 1;
    }

    /// <summary>A short shutter click (made in code - no audio asset needed).</summary>
    void Click(float volume)
    {
        if (!audioSrc)
        {
            audioSrc = gameObject.AddComponent<AudioSource>();
            audioSrc.playOnAwake = false;
            audioSrc.spatialBlend = 0f;
            const int rate = 44100, n = rate / 25;
            var data = new float[n];
            var rnd = new System.Random(7);
            for (int i = 0; i < n; i++)
            {
                float env = Mathf.Exp(-i / (rate * 0.006f));
                data[i] = env * (0.6f * (float)(rnd.NextDouble() * 2 - 1) + 0.4f * Mathf.Sin(i * 2f * Mathf.PI * 2200f / rate));
            }
            shutterClip = AudioClip.Create("Shutter", n, 1, rate, false);
            shutterClip.SetData(data, 0);
        }
        audioSrc.PlayOneShot(shutterClip, volume);
    }

    void Haptic()
    {
        OVRInput.SetControllerVibration(0.3f, 0.5f, OVRInput.Controller.RTouch);
        Invoke(nameof(StopHaptic), 0.06f);
    }
    void StopHaptic() => OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);
}

/// <summary>
/// One wall photo's sidecar (NNNN.json next to NNNN.jpg). Pixel (px, py) of the JPG (origin top-left, as image
/// tools count) is viewport u = (px + 0.5) / imageWidth, v = 1 - (py + 0.5) / imageHeight; its ray in the camera's
/// frame is ((u - cu) / fu, (v - cv) / fv, 1) (Unity axes: x right, y up, z forward), turned into the room's frame
/// by localRotation and starting at localPosition - then into any loaded copy of the scan via the room's transform.
/// </summary>
[Serializable]
public class WallPhotoMeta
{
    public string schemaVersion = "1";
    public int index;
    public string file;
    public string capturedAt;
    public string cameraTimestamp;
    public string camera;
    public string roomUuid;
    public string roomName;
    public Vector3 localPosition;
    public Quaternion localRotation;
    public Vector3 worldPosition;
    public Quaternion worldRotation;
    public float fu, fv, cu, cv;
    public Vector2 focalLength, principalPoint, sensorResolution;
    public int imageWidth, imageHeight;
    public float centerHitDistance;
    public string centerHitLabel;
    /// <summary>Dense frame (Dense/ subfolder, for a splat) rather than a cable photo.</summary>
    public bool dense;
    /// <summary>The camera pose in the house frame: local to the room houseRoomUuid (the same room for every photo
    /// of a session), so all rooms share one coordinate system - what the COLMAP export uses.</summary>
    public string houseRoomUuid;
    public Vector3 housePosition;
    public Quaternion houseRotation;
    /// <summary>Dense frames: scan surface points seen by the photo (house frame) and their colour in it.</summary>
    public List<Vector3> seedPoints = new List<Vector3>();
    public List<Color32> seedColors = new List<Color32>();
    /// <summary>Image sharpness (mean luminance gradient) and its ratio to the recent median of the same kind.</summary>
    public float sharpness, relativeSharpness;
    /// <summary>Head motion at the moment of the shot (deg/s, m/s).</summary>
    public float headDegPerSec, headMetersPerSec;
}
