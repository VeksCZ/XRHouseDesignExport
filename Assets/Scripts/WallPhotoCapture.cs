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
    const float StillTime = 0.45f, StillDegPerSec = 6f, StillMetersPerSec = 0.05f, MinInterval = 1.2f;
    const float NewAngle = 22f, NewDistance = 0.9f;
    const int OutlineSteps = 6; // rays per image edge for the coverage outline

    static readonly Color CoverageColor = new Color(0.25f, 1f, 0.45f, 1f);
    static readonly Color LastColor = new Color(1f, 0.85f, 0.2f, 1f);

    bool wasActive, auto = true, showCoverage = true;
    string sessionDir;
    int nextIndex;
    readonly List<WallPhotoMeta> shots = new List<WallPhotoMeta>();
    readonly List<LineRenderer> outlines = new List<LineRenderer>();
    Material lineMat;
    Transform head;

    // stillness tracking
    Vector3 lastHeadPos; Quaternion lastHeadRot; float stillFor, lastShotTime = -10f;
    bool preparing, capturing;
    int pendingWrites;

    Canvas panel;
    TMP_Text countText, statusText;
    UnityEngine.UI.Button autoButton, coverageButton;
    Vector3 panelPos; Quaternion panelRot; bool panelPlaced;
    const float PW = 600f, PH = 250f, PScale = 0.0006f, RowH = 62f, Gap = 8f;

#if META_XR_SDK_INSTALLED
    PassthroughCameraAccess cam;
    // A camera frame is armed when it arrives (pose + timestamp taken in that same frame); its pixels are read back
    // one frame later - GetTexture() is filled on the render thread, so reading it in the arrival frame would give
    // the previous image (see PassthroughCameraAccess.GetTexture docs).
    bool shotRequested, armed, cameraReady;
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
        ClearOutlines();
        if (lineMat) Destroy(lineMat);
    }

    bool Active => editMode != null && editMode.IsOn && editMode.CurrentTool == EditModeController.Tool.Photos;

    void Update()
    {
        if (!Active)
        {
            if (wasActive) { wasActive = false; StopCamera(); ClosePanel(); ClearOutlines(); }
            return;
        }
        if (!wasActive) { wasActive = true; Begin(); }
#if META_XR_SDK_INSTALLED
        if (cam == null || !cam.IsPlaying) return;
        if (!cameraReady) { cameraReady = true; RefreshPanel(); }
        TrackStillness();
        if (auto && !shotRequested && !capturing && stillFor >= StillTime && Time.time - lastShotTime >= MinInterval && IsNewView())
            shotRequested = true;
        PumpCapture();
#endif
    }

    // ---------- session ----------

    async void Begin()
    {
        if (walk != null && walk.IsOn)
        {
            uiLog?.AddLog("<color=orange>Photos: leave the walk-through first (A) - photos are taken over passthrough.</color>");
            return;
        }
        if (preparing) return;
        preparing = true;
        try
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            head = rig ? rig.centerEyeAnchor : Camera.main ? Camera.main.transform : null;
            if (exporter == null || !await exporter.EnsureLiveLoaded(uiLog))
            {
                uiLog?.AddLog("<color=red>Photos: the live scan could not be loaded - photos need it to line up with the real house.</color>");
                return;
            }
            if (!Active) return;
            OpenSession();
            BuildPanel();
            RebuildOutlines();
            if (!await EnsurePermission()) { SetStatus("<color=red>No camera permission</color>"); return; }
            if (!Active) return;
            StartCamera();
            uiLog?.AddLog($"Photos: trigger = photo{(auto ? ", auto shots when you hold your head still" : "")}. Saving to {Path.GetFileName(sessionDir)}.");
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

#if META_XR_SDK_INSTALLED
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
        cam.RequestedResolution = new Vector2Int(1280, 960); // the largest the API offers
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
#endif
    }

#if META_XR_SDK_INSTALLED
    void PumpCapture()
    {
        if (!shotRequested || capturing) return;
        if (cam.IsUpdatedThisFrame)
        {
            armedPose = cam.GetCameraPose();
            armedTime = cam.Timestamp;
            armed = true;
            return;
        }
        if (!armed) return;
        shotRequested = armed = false;
        Capture(armedPose, armedTime);
    }

    void Capture(Pose pose, DateTime stamp)
    {
        var tex = cam.GetTexture();
        if (tex == null) return;
        capturing = true;
        lastShotTime = Time.time;
        var meta = BuildMeta(pose, stamp, tex.width, tex.height);
        if (meta == null) { capturing = false; uiLog?.AddLog("<color=orange>Photos: you are outside every scanned room - not saved.</color>"); return; }

        AsyncGPUReadback.Request(tex, 0, TextureFormat.RGBA32, req =>
        {
            capturing = false;
            if (req.hasError) { uiLog?.AddLog("<color=red>Photos: camera readback failed.</color>"); return; }
            byte[] pixels = req.GetData<byte>().ToArray();
            int w = req.width, h = req.height;
            meta.imageWidth = w; meta.imageHeight = h;
            string jpg = Path.Combine(sessionDir, meta.file), json = Path.ChangeExtension(jpg, ".json");
            string metaJson = JsonUtility.ToJson(meta, true);
            pendingWrites++;
            Task.Run(() =>
            {
                // Rows come bottom-up (Unity texture order), which is what the encoder expects: the JPG is upright,
                // with viewport (0,0) = its bottom-left pixel.
                byte[] bytes = ImageConversion.EncodeArrayToJPG(pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, 0, 92);
                File.WriteAllBytes(jpg, bytes);
                File.WriteAllText(json, metaJson); // the JSON last: a photo counts once both exist
            }).ContinueWith(t =>
            {
                pendingWrites--;
                if (t.Exception != null) Debug.LogError($"[WallPhotos] write failed: {t.Exception.GetBaseException()}");
            });

            shots.Add(meta);
            nextIndex++;
            AddOutline(meta, true);
            Haptic();
            RefreshPanel();
        });
    }

    WallPhotoMeta BuildMeta(Pose pose, DateTime stamp, int w, int h)
    {
        var rooms = MRUK.Instance != null ? MRUKDataProcessor.GetValidRooms(MRUK.Instance) : new List<MRUKRoom>();
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
        var m = new WallPhotoMeta
        {
            index = nextIndex,
            file = nextIndex.ToString("0000") + ".jpg",
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

    static bool Hit(Ray ray, out Vector3 point, out string label)
    {
        point = Vector3.zero; label = null;
        float best = float.MaxValue;
        if (MRUK.Instance == null) return false;
        foreach (var room in MRUKDataProcessor.GetValidRooms(MRUK.Instance))
        {
            if (room.Raycast(ray, 15f, out RaycastHit hit, out MRUKAnchor anchor) && hit.distance < best)
            {
                best = hit.distance; point = hit.point;
                label = anchor != null ? anchor.Label.ToString() : null;
            }
        }
        return best < float.MaxValue;
    }

    // ---------- auto shot ----------

    void TrackStillness()
    {
        if (!head) return;
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        float deg = Quaternion.Angle(lastHeadRot, head.rotation) / dt;
        float mps = Vector3.Distance(lastHeadPos, head.position) / dt;
        lastHeadPos = head.position; lastHeadRot = head.rotation;
        stillFor = deg < StillDegPerSec && mps < StillMetersPerSec ? stillFor + dt : 0f;
    }

    bool IsNewView()
    {
        Pose pose = cam.GetCameraPose();
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

    /// <summary>The photo's footprint on the scanned surfaces: rays along the image border, hit against the scan
    /// (so it wraps around room corners), drawn as one closed line just in front of the surfaces.</summary>
    void AddOutline(WallPhotoMeta s, bool last)
    {
        if (!showCoverage || !ShotWorld(s, out var pos, out var rot)) return;
        if (last && outlines.Count > 0) SetColor(outlines[outlines.Count - 1], CoverageColor);
        var pts = new List<Vector3>();
        OutlineEdge(s, pos, rot, new Vector2(0, 0), new Vector2(1, 0), pts);
        OutlineEdge(s, pos, rot, new Vector2(1, 0), new Vector2(1, 1), pts);
        OutlineEdge(s, pos, rot, new Vector2(1, 1), new Vector2(0, 1), pts);
        OutlineEdge(s, pos, rot, new Vector2(0, 1), new Vector2(0, 0), pts);
        if (pts.Count < 3) return;

        if (!lineMat)
        {
            lineMat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "WallPhotoOutline" };
            if (lineMat.HasProperty("_Cull")) lineMat.SetFloat("_Cull", (float)CullMode.Off);
        }
        var go = new GameObject("PhotoFootprint_" + s.index);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = lineMat;
        lr.useWorldSpace = true;
        lr.loop = true;
        lr.positionCount = pts.Count;
        lr.SetPositions(pts.ToArray());
        lr.startWidth = lr.endWidth = 0.012f;
        lr.shadowCastingMode = ShadowCastingMode.Off;
        lr.receiveShadows = false;
        SetColor(lr, last ? LastColor : CoverageColor);
        outlines.Add(lr);
    }
#endif

#if META_XR_SDK_INSTALLED
    static void OutlineEdge(WallPhotoMeta s, Vector3 pos, Quaternion rot, Vector2 a, Vector2 b, List<Vector3> pts)
    {
        for (int i = 0; i < OutlineSteps; i++)
        {
            Vector2 uv = Vector2.Lerp(a, b, i / (float)OutlineSteps);
            Vector3 dir = rot * new Vector3((uv.x - s.cu) / s.fu, (uv.y - s.cv) / s.fv, 1f).normalized;
            if (Hit(new Ray(pos, dir), out var p, out _)) pts.Add(p - dir * 0.02f);
        }
    }
#endif

    static void SetColor(LineRenderer lr, Color c) { if (lr) lr.startColor = lr.endColor = c; }

    void ClearOutlines()
    {
        foreach (var lr in outlines) if (lr) Destroy(lr.gameObject);
        outlines.Clear();
    }

    void UndoLast()
    {
        if (shots.Count == 0) return;
        if (pendingWrites > 0) { uiLog?.AddLog("Photos: still saving, try again in a moment."); return; }
        var s = shots[shots.Count - 1];
        shots.RemoveAt(shots.Count - 1);
        string jpg = Path.Combine(sessionDir, s.file);
        try { File.Delete(Path.ChangeExtension(jpg, ".json")); File.Delete(jpg); } catch (Exception e) { Debug.LogWarning(e); }
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
        if (!panelPlaced)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            panelPos = head.position + fwd * 0.6f - Vector3.up * 0.25f + Quaternion.Euler(0, -90, 0) * fwd * 0.3f;
            panelRot = Quaternion.LookRotation(panelPos - head.position, Vector3.up);
            panelPlaced = true;
        }
        t.SetPositionAndRotation(panelPos, panelRot);
        PanelDrag.Attach(panel, PW, PH);
        XRUi.CreatePanel(t, "Background", XRUi.PanelColor, 0, 0, PW, PH);

        float x0 = 20, fullW = PW - 40, y = 8;
        countText = XRUi.CreateText(t, "Title", "", 22, TextAlignmentOptions.MidlineLeft, x0, y, fullW, 44, XRUi.TextColor, FontStyles.Bold);
        y += 46;
        statusText = XRUi.CreateText(t, "Hint", "", 18, TextAlignmentOptions.TopLeft, x0, y, fullW, 60, XRUi.MutedText);
        y += 64 + Gap;
        float w4 = (fullW - Gap * 3) / 4f;
        autoButton = XRUi.CreateButton(t, "Auto", x0, y, w4, RowH, () => { auto = !auto; stillFor = 0; RefreshPanel(); }, 20);
        coverageButton = XRUi.CreateButton(t, "Coverage", x0 + (w4 + Gap), y, w4, RowH, () =>
        {
            showCoverage = !showCoverage;
#if META_XR_SDK_INSTALLED
            RebuildOutlines();
#endif
            RefreshPanel();
        }, 18);
        var undo = XRUi.CreateButton(t, "Delete last", x0 + (w4 + Gap) * 2, y, w4, RowH, UndoLast, 17);
        XRUi.SetTint(undo, XRUi.ButtonDangerColor);
        XRUi.CreateButton(t, "Close", x0 + (w4 + Gap) * 3, y, w4, RowH, ClosePanel, 20);
        RefreshPanel();
    }

    void RefreshPanel()
    {
        if (!panel) return;
        countText.text = $"Wall photos: {shots.Count}   ({Path.GetFileName(sessionDir)})";
        if (statusText.text.StartsWith("<color=red>")) return;
        statusText.text = auto
            ? "Look at a wall and hold still - it shoots by itself. Trigger = shoot now. Green = already covered."
            : "Trigger = take a photo. Green outlines = already covered.";
        XRUi.SetTint(autoButton, auto ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        XRUi.SetTint(coverageButton, showCoverage ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
    }

    void SetStatus(string s) { if (statusText) statusText.text = s; uiLog?.AddLog("Photos: " + s); }

    void ClosePanel()
    {
        if (panel) Destroy(panel.gameObject);
        panel = null;
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
}
