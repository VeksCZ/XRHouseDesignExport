using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// 1:1 walk-through of the scanned house ("Walk"). The headset rig itself never moves - the MODEL is moved around
/// you instead (left stick = walk, right stick left/right = snap turn, right stick forward = teleport arc, floors
/// and steps followed up/down automatically). Moving the rig would drag MRUK's anchors out of line with the real
/// room for the scan overlay/minimap; moving the model leaves everything else exactly where it was.
///
/// Collisions use the model's own meshes on the built-in "Ignore Raycast" layer, queried explicitly with that
/// mask - so none of the existing default-mask grab raycasts (Dollhouse, Minimap, menu handle) can ever hit them.
/// Doors get no collider and a see-through material that fades out as you get close, so you just walk through.
/// Walking physically into a wall fades the view to black (you can't be stopped in the real world).
/// </summary>
public class WalkThroughMode : MonoBehaviour
{
    public XRMenu uiLog;
    public float moveSpeed = 1.4f;          // m/s at full left-stick deflection
    public float snapTurnDeg = 45f;
    public float seatedEyeHeight = 1.65f;   // virtual eye height while seated mode is on

    const int WalkLayer = 2;                // built-in "Ignore Raycast"
    const int WalkMask = 1 << WalkLayer;
    const float BodyRadius = 0.2f;
    const float Skin = 0.03f;
    const float StepUp = 0.35f;             // highest step/threshold you walk onto without teleporting
    const float StepDown = 0.6f;            // a bigger drop is treated as a gap (e.g. under a doorway between two
                                            // rooms' floor slabs, or the stairwell edge), not as a fall
    const float HeightFollow = 14f;         // 1/s - how fast the floor height catches up when stepping up/down
    const float DoorAlphaFar = 0.35f, DoorFadeNear = 0.5f, DoorFadeFar = 2.0f;
    /// <summary>The colour the model gives WINDOW parts (XRModelFactory) - shaded vertex colours are this times light.</summary>
    static readonly Color WindowPaletteColor = new Color(0.25f, 0.60f, 0.92f);
    const float TeleportSpeed = 7f;

    GameObject root;
    float yaw; Vector3 center;              // the model's own build frame, same as the Dollhouse (XRModelFactory)
    readonly List<(Renderer r, Material m, Color c)> doors = new List<(Renderer, Material, Color)>();
    Transform head, trackingSpace, rightHand;
    bool isOn, seated, turnArmed = true, aiming, teleportValid;
    float seatedLift;
    Vector3 teleportPoint;
    LineRenderer arc; GameObject reticle; Material arcMat, reticleMat; Mesh reticleMesh;
    GameObject fadeQuad; Material fadeMat; Mesh fadeMesh; float fade, blink;
    float prevNearClip = -1f;
    readonly List<Vector3> arcPoints = new List<Vector3>();

    // Stairs from the user's edits (walkable, with the stairwell cut out of the floor/ceiling above).
    readonly StairsInModel stairs = new StairsInModel();
    List<MRUKRoom> walkRooms;
    GameObject walkVisual;

    void OnEnable() { HouseEditsStore.Changed += OnEditsChanged; }
    void OnDisable() { HouseEditsStore.Changed -= OnEditsChanged; }
    string walledUp = "";
    void OnEditsChanged()
    {
        if (!isOn) return;
        // A doorway walled up / reopened changes the base model itself - rebuild it in place; anything else only
        // needs the edit-driven parts (stairs, openings, door leaves) re-applied.
        string sig = DoorCatalog.WalledUpSignature(HouseEditsStore.Current);
        if (sig != walledUp && !rebuilding) { walledUp = sig; _ = RebuildInPlace(); }
        else ApplyStairs();
    }

    async System.Threading.Tasks.Task RebuildInPlace()
    {
        rebuilding = true;
        try { await BuildVisual(); } finally { rebuilding = false; }
    }

    // Real door leaves for doors set up in edit mode (see WalkDoors).
    readonly WalkDoors walkDoors = new WalkDoors();
    // Furniture of the scan (95_Data_Furniture.json - bathroom fittings etc.).
    readonly FurnitureInModel furniture = new FurnitureInModel();
    readonly List<Renderer> windowPanels = new List<Renderer>(); // replaced by real sashes for windows set up in edit mode

    /// <summary>(Re)builds everything that comes from the user's edits: stairs and door leaves.</summary>
    void ApplyStairs()
    {
        if (!walkVisual || walkRooms == null) return;
        // The scanned-mesh models already contain the real stairs and doorways as scanned.
        if (walkModel != WalkModel.Anchor) return;
        stairs.Apply(walkVisual, yaw, center, walkRooms, HouseEditsStore.Current, true, WalkLayer, shaded: true);
        // Where you may fall: the drawn openings (model space XZ, a little beyond their edges).
        openingRects.Clear();
        Quaternion g = Quaternion.Euler(0, yaw, 0);
        foreach (var h in HouseEditsStore.Current.holes)
        {
            if (!StairGeometry.HoleWorld(h, walkRooms, out var a, out var b)) continue;
            Vector3 ma = g * (a - center), mb = g * (b - center);
            openingRects.Add(Rect.MinMaxRect(Mathf.Min(ma.x, mb.x) - 0.15f, Mathf.Min(ma.z, mb.z) - 0.15f,
                                             Mathf.Max(ma.x, mb.x) + 0.15f, Mathf.Max(ma.z, mb.z) + 0.15f));
        }
        walkDoors.Build(walkVisual, yaw, center, walkRooms, HouseEditsStore.Current, WalkLayer, doors.Select(d => d.r).Concat(windowPanels));
        Physics.SyncTransforms();
    }

    /// <summary>
    /// Jumps to the next story (cyclic, upwards): straight above/below where you stand if that story has floor
    /// there, else the middle of its largest room. Stories = floor anchor heights more than 1.5 m apart.
    /// </summary>
    public string NextFloor()
    {
        if (!isOn || walkRooms == null || !TryGetScanWorldPose(out var headScan, out _)) return null;
        var floors = walkRooms.SelectMany(r => r.FloorAnchors).Where(f => f != null && f.PlaneRect.HasValue).ToList();
        var levels = new List<float>();
        foreach (var y in floors.Select(f => f.transform.position.y).OrderBy(y => y))
            if (levels.Count == 0 || y - levels[levels.Count - 1] > 1.5f) levels.Add(y);
        if (levels.Count < 2) return "Only one floor in this scan";

        float feetY = headScan.y - (head.position.y - TargetFloorY);
        int cur = 0;
        for (int i = 1; i < levels.Count; i++) if (Mathf.Abs(levels[i] - feetY) < Mathf.Abs(levels[cur] - feetY)) cur = i;
        int next = (cur + 1) % levels.Count;
        float level = levels[next];

        Vector3 target;
        var here = DoorCatalog.FindRoomAt(walkRooms, new Vector3(headScan.x, level + 0.05f, headScan.z));
        var hereFloor = here?.FloorAnchors.FirstOrDefault(f => f != null && Mathf.Abs(f.transform.position.y - level) < 0.5f);
        if (hereFloor != null) target = new Vector3(headScan.x, hereFloor.transform.position.y, headScan.z);
        else
        {
            var biggest = floors.Where(f => Mathf.Abs(f.transform.position.y - level) < 0.5f)
                .OrderByDescending(f => f.PlaneRect.Value.width * f.PlaneRect.Value.height).First();
            target = biggest.transform.position;
        }
        // Straight above can be the stairwell opening drawn in the ceiling - you'd land there and fall straight back
        // down. Step out of it to its nearest edge that still has floor of that story (else the largest room).
        bool moved = OutOfOpening(ref target, floors, level);
        Place(Quaternion.Euler(0, yaw, 0) * (target - center), root.transform.rotation);
        blink = 1f;
        Debug.Log($"[Walk] Next floor: feet {feetY:0.00} (level {cur + 1}) -> level {next + 1} at {level:0.00}, target {target}, " +
                  $"{(hereFloor != null ? "straight above/below" : "largest room")}{(moved ? ", moved out of the stair opening" : "")}");
        return $"Floor {next + 1}/{levels.Count}";
    }

    /// <summary>If a scan-world floor point lies in a drawn floor opening (where Fall would drop you), moves it just
    /// past the opening's nearest edge where that story has floor - or to its largest room. True if it moved.</summary>
    bool OutOfOpening(ref Vector3 target, List<MRUKAnchor> floors, float level)
    {
        Quaternion g = Quaternion.Euler(0, yaw, 0), gi = Quaternion.Inverse(g);
        Vector3 m = g * (target - center);
        var hole = openingRects.FirstOrDefault(r => r.Contains(new Vector2(m.x, m.z)));
        if (hole.width <= 0f) return false;
        const float margin = 0.4f;
        var options = new[]
        {
            new Vector2(hole.xMin - margin, m.z), new Vector2(hole.xMax + margin, m.z),
            new Vector2(m.x, hole.yMin - margin), new Vector2(m.x, hole.yMax + margin),
        }.OrderBy(p => (p - new Vector2(m.x, m.z)).sqrMagnitude);
        foreach (var p in options)
        {
            if (openingRects.Any(r => r.Contains(p))) continue;
            Vector3 w = gi * new Vector3(p.x, m.y, p.y) + center;
            var room = DoorCatalog.FindRoomAt(walkRooms, new Vector3(w.x, level + 0.05f, w.z));
            var f = room?.FloorAnchors.FirstOrDefault(a => a != null && Mathf.Abs(a.transform.position.y - level) < 0.5f);
            if (f == null) continue;
            target = new Vector3(w.x, f.transform.position.y, w.z);
            return true;
        }
        var biggest = floors.Where(f => Mathf.Abs(f.transform.position.y - level) < 0.5f)
            .OrderByDescending(f => f.PlaneRect.Value.width * f.PlaneRect.Value.height).FirstOrDefault();
        if (biggest == null) return false;
        target = biggest.transform.position;
        return true;
    }

    /// <summary>Right trigger while walking: opens/closes the door leaf under the ray. False if none.</summary>
    public bool TryToggleDoor()
    {
        if (!isOn || !rightHand) return false;
        return Physics.Raycast(rightHand.position, rightHand.forward, out var hit, 6f, WalkMask, QueryTriggerInteraction.Collide)
               && walkDoors.Toggle(hit.collider);
    }

    // ---------- moving furniture: grab what the ray points at, drag it over the floor, turn it 90 degrees ----------

    Transform moving; string movingKey; int movingIndex;
    Vector3 grabPoint, movingStart;   // model space: where the ray first hit it, and the item's start offset
    float grabY;                      // model-space height of the drag plane (the grab point's)

    public bool IsMovingFurniture => moving;

    /// <summary>Picks the piece of furniture the ray hits (world ray). False if it hits something else first.</summary>
    public bool TryGrabFurniture(Ray ray)
    {
        if (!isOn || !walkVisual || moving) return false;
        if (!Physics.Raycast(ray, out var hit, 8f, WalkMask, QueryTriggerInteraction.Ignore)) return false;
        var item = furniture.ItemOf(hit.collider, out movingKey, out movingIndex);
        if (!item) return false;
        moving = item;
        grabPoint = walkVisual.transform.InverseTransformPoint(hit.point);
        grabY = grabPoint.y;
        movingStart = moving.localPosition;
        return true;
    }

    /// <summary>Follows the ray over the horizontal plane through the grab point.</summary>
    public void DragFurniture(Ray ray)
    {
        if (!moving || !walkVisual) return;
        var t = walkVisual.transform;
        Vector3 o = t.InverseTransformPoint(ray.origin), d = t.InverseTransformDirection(ray.direction);
        if (Mathf.Abs(d.y) < 1e-4f) return;
        float k = (grabY - o.y) / d.y;
        if (k <= 0f || k > 15f) return;   // pointing above the horizon
        Vector3 p = o + d * k;
        Vector3 delta = p - grabPoint; delta.y = 0f;
        moving.localPosition = movingStart + delta;
    }

    /// <summary>Puts it down where it is (saved) - or back where it was.</summary>
    public string DropFurniture(bool keep = true)
    {
        if (!moving) return null;
        Vector3 delta = moving.localPosition - movingStart;
        var spec = furniture.Spec;
        moving.localPosition = movingStart;
        moving = null;
        if (!keep || delta.sqrMagnitude < 1e-6f || spec == null) return null;
        // model space -> GLB axes (Z mirrored)
        if (!Furniture.MoveItem(spec, walkRooms, yaw, center, movingKey, movingIndex, delta.x, -delta.z)) return "Can't move this one";
        Furniture.Save(furniture.ScanName, spec);
        furniture.Reload();
        Physics.SyncTransforms();
        return $"Moved {delta.magnitude:0.00} m";
    }

    /// <summary>Turns the held item (or the one under the ray) 90 degrees; a held item stays held.</summary>
    public string RotateFurniture(Ray ray)
    {
        bool held = moving;
        if (!held && !TryGrabFurniture(ray)) return null;
        string key = movingKey; int index = movingIndex;
        Vector3 delta = moving.localPosition - movingStart;
        moving.localPosition = movingStart;
        moving = null;
        var spec = furniture.Spec;
        if (spec == null) return null;
        if (delta.sqrMagnitude > 1e-6f) Furniture.MoveItem(spec, walkRooms, yaw, center, key, index, delta.x, -delta.z);
        if (!Furniture.RotateItem(spec, walkRooms, yaw, center, key, index)) return "Can't turn this one";
        Furniture.Save(furniture.ScanName, spec);
        furniture.Reload();
        Physics.SyncTransforms();
        if (held) TryGrabFurniture(ray);   // keep holding the rebuilt item
        return "Turned 90°";
    }

    EditModeController editMode;

    bool FurnitureTool => editMode && editMode.IsOn && editMode.CurrentTool == EditModeController.Tool.Furniture;

    /// <summary>Quest, edit mode, Furniture tool: trigger on a piece picks it up (it follows the ray over the floor),
    /// trigger again puts it down (saved); right stick click turns it 90 degrees. Leaving the tool puts it down.</summary>
    void HandleFurnitureGrab()
    {
        if (desktop || !rightHand) return;
        if (!editMode)
        {
            editMode = FindFirstObjectByType<EditModeController>();
            if (editMode) editMode.TriggerPressed += OnEditTrigger;
        }
        var ray = new Ray(rightHand.position, rightHand.forward);
        if (moving && !FurnitureTool) { uiLog?.AddLog(DropFurniture() ?? "Furniture: put down"); return; }
        if (!FurnitureTool) return;
        if (moving) DragFurniture(ray);
        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch))
            uiLog?.AddLog(RotateFurniture(ray) ?? "Furniture: point at a piece of furniture");
    }

    void OnEditTrigger()
    {
        if (!isOn || desktop || !rightHand || !FurnitureTool) return;
        if (moving) { uiLog?.AddLog(DropFurniture() ?? "Furniture: put down"); return; }
        if (TryGrabFurniture(new Ray(rightHand.position, rightHand.forward)))
        {
            OVRInput.SetControllerVibration(0.3f, 0.15f, OVRInput.Controller.RTouch);
            Invoke(nameof(StopFurnitureHaptic), 0.08f);
            uiLog?.AddLog("Furniture: moving - trigger puts it down, right stick click turns it");
        }
        else uiLog?.AddLog("Furniture: point at a piece of furniture");
    }

    void StopFurnitureHaptic() => OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);

    /// <summary>Opens/closes every door (desktop scripted tests).</summary>
    public void SetAllDoors(bool open) => walkDoors.SetAllDoors(open);

    /// <summary>The running walk-through, if any - lets the minimap show where you are in the model.</summary>
    public static WalkThroughMode Active { get; private set; }
    public bool IsOn => isOn;
    public bool IsSeated => seated;

    /// <summary>World Y the model's floor under you should sit at: the real floor (FloorLevel tracking origin),
    /// or lower by seatedLift in seated mode so your eyes end up at seatedEyeHeight above the model's floor.</summary>
    float TargetFloorY => trackingSpace.position.y - seatedLift;

    /// <summary>
    /// Starts walking. With modelFloorPoint (model space, e.g. picked on the Dollhouse floor) that point is placed
    /// under your feet and the model gets modelRotation (e.g. the Dollhouse's own yaw, so you keep facing the way
    /// you were looking at it). Without it the scan's real-world alignment is used if you stand inside the
    /// scanned floor area, otherwise the middle of the largest room.
    /// </summary>
    public async System.Threading.Tasks.Task<bool> Enter(Vector3? modelFloorPoint, Quaternion? modelRotation)
    {
        if (isOn) Exit();
        if (!ResolveRig()) { uiLog?.AddLog("<color=red>Walk: no camera rig.</color>"); return false; }
        if (MRUK.Instance == null) { uiLog?.AddLog("<color=red>Walk: no MRUK instance.</color>"); return false; }
        var rooms = MRUKDataProcessor.GetValidRooms(MRUK.Instance);
        if (rooms.Count == 0) { uiLog?.AddLog("<color=red>Walk: no valid rooms.</color>"); return false; }

        Furniture.InstallCeilings(rooms, HouseEditsStore.CurrentScan); // the real (sloped) ceilings from the spec
        center = DollHouseVisualizer.CalculateCenter(rooms);
        yaw = FloorPlanBuilder.CorrectionYaw(MRUKPlanExtractor.Extract(rooms));
        walkRooms = rooms;
        walledUp = DoorCatalog.WalledUpSignature(HouseEditsStore.Current);

        root = new GameObject("WalkRoot");
        // Kinematic body: the colliders get moved every frame, which PhysX handles far better for a kinematic
        // rigidbody's children than for "static" colliders.
        var rb = root.AddComponent<Rigidbody>();
        rb.isKinematic = true; rb.useGravity = false;
        if (!await BuildVisual())
        {
            Destroy(root); root = null; walkRooms = null;
            uiLog?.AddLog("<color=red>Walk: model build failed.</color>");
            return false;
        }

        isOn = true; Active = this;
        seated = false; seatedLift = 0f;

        Quaternion scanToModelInv = Quaternion.Inverse(Quaternion.Euler(0, yaw, 0));
        if (modelFloorPoint.HasValue) Place(modelFloorPoint.Value, modelRotation ?? scanToModelInv);
        else
        {
            // Model vertices are gRot * (world - center), so this puts every wall back where it was scanned.
            root.transform.SetPositionAndRotation(center, scanToModelInv);
            Physics.SyncTransforms();
            if (!SnapToFloorWide()) Place(LargestRoomPoint(), scanToModelInv);
        }

        BuildHelpers();
        var cam = Camera.main;
        if (cam != null) { prevNearClip = cam.nearClipPlane; cam.nearClipPlane = 0.05f; }
        if (!desktop) uiLog?.AddLog("<color=green>Walk ON</color> - L stick move, R stick turn / push forward to teleport, L stick click = seated, A = back");
        return true;
    }

    // ---------- which model you walk through ----------

    public enum WalkModel { Anchor, Mesh, Raw }
    WalkModel walkModel = WalkModel.Anchor;
    bool rebuilding;

    public string ModelLabel => walkModel switch
    {
        WalkModel.Anchor => "Walk: Anchor",
        WalkModel.Mesh => "Walk: Mesh",
        _ => "Walk: Raw",
    };

    /// <summary>Anchor (clean boxes, edits: doors/stairs) -> Mesh (scanned mesh + door/window frames) -> Raw
    /// (scanned mesh only). Rebuilt in place - you stay exactly where you are.</summary>
    public async System.Threading.Tasks.Task<string> CycleModel()
    {
        if (rebuilding) return ModelLabel;
        walkModel = (WalkModel)(((int)walkModel + 1) % 3);
        if (isOn)
        {
            rebuilding = true;
            try { await BuildVisual(); } finally { rebuilding = false; }
        }
        return ModelLabel;
    }

    /// <summary>(Re)builds the model under root for the current WalkModel, replacing any previous one.</summary>
    async System.Threading.Tasks.Task<bool> BuildVisual()
    {
        if (!root || walkRooms == null) return false;
        stairs.Dispose();
        walkDoors.Clear();
        furniture.Clear();
        if (walkVisual)
        {
            foreach (var mf in walkVisual.GetComponentsInChildren<MeshFilter>(true)) if (mf.sharedMesh) Destroy(mf.sharedMesh);
            foreach (var mr in walkVisual.GetComponentsInChildren<Renderer>(true)) if (mr.sharedMaterial) Destroy(mr.sharedMaterial);
            walkVisual.transform.SetParent(null, false); // Destroy is deferred - get it out of the model right now
            walkVisual.SetActive(false);
            Destroy(walkVisual);
            walkVisual = null;
        }
        doors.Clear();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        XRHouseModel model;
        if (walkModel == WalkModel.Anchor)
        {
            // rooms drawn from the furniture spec (not scanned): walls next to them are built thin, as against a
            // scanned neighbour
            XRModelFactory.ExtraFaces = Furniture.ExtraFaces(Furniture.Load(HouseEditsStore.CurrentScan), walkRooms, yaw, center);
            try { model = XRModelFactory.CreateAnchorAnalytical(walkRooms, yaw, center); }
            finally { XRModelFactory.ExtraFaces = null; }
            XRModelFactory.AddCeilings(model, walkRooms); // walking inside, a ceiling is what tells you you're indoors
        }
        else if (walkModel == WalkModel.Mesh) model = await XRModelFactory.CreateMeshAnalytical(walkRooms, yaw, center, forDollhouse: false);
        else model = await XRModelFactory.CreateRawScan(walkRooms, yaw, center, forDollhouse: false);
        if (!root) return false; // left while building
        long tModel = sw.ElapsedMilliseconds;

        var visual = UnityModelLoader.LoadToScene(model, shaded: true, classifyByNormal: walkModel != WalkModel.Anchor);
        if (visual == null) return false;
        long tMeshes = sw.ElapsedMilliseconds;
        visual.transform.SetParent(root.transform, false);
        walkVisual = visual;
        PrepareParts();
        long tColliders = sw.ElapsedMilliseconds;
        ApplyStairs();
        furniture.Apply(walkVisual, yaw, center, walkRooms, HouseEditsStore.CurrentScan, WalkLayer);
        Physics.SyncTransforms();
        Debug.Log($"TIMING walk {walkModel}: model {tModel} ms, meshes {tMeshes - tModel} ms, colliders {tColliders - tMeshes} ms, " +
                  $"stairs+doors {sw.ElapsedMilliseconds - tColliders} ms");
        return true;
    }

    public void Exit()
    {
        if (!isOn && !root) return;
        isOn = false; aiming = false;
        if (Active == this) Active = null;
        stairs.Dispose();
        walkDoors.Clear();
        furniture.Clear();
        walkVisual = null; walkRooms = null;
        if (root)
        {
            // Every mesh/material under root was generated by UnityModelLoader or here - never a shared asset.
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>()) if (mf.sharedMesh) Destroy(mf.sharedMesh);
            foreach (var mr in root.GetComponentsInChildren<Renderer>()) if (mr.sharedMaterial) Destroy(mr.sharedMaterial);
            Destroy(root);
        }
        root = null;
        doors.Clear();
        DestroyHelpers();
        var cam = Camera.main;
        if (cam != null && prevNearClip > 0f) cam.nearClipPlane = prevNearClip;
        prevNearClip = -1f;
        uiLog?.AddLog("Walk OFF");
    }

    /// <summary>Seated mode: virtual eye height fixed at seatedEyeHeight whatever your real head height is.</summary>
    public void ToggleSeated()
    {
        if (!isOn || !root) return;
        float oldTarget = TargetFloorY;
        seated = !seated;
        float eye = head.position.y - trackingSpace.position.y;
        seatedLift = seated ? Mathf.Max(0f, seatedEyeHeight - eye) : 0f;
        // Jump straight to the new height - the per-frame floor probe starts just above the target floor, so it
        // would otherwise begin inside/under the old floor slab and lose it.
        root.transform.position += Vector3.up * (TargetFloorY - oldTarget);
        Physics.SyncTransforms();
        uiLog?.AddLog(seated ? $"Walk: seated (eyes at {seatedEyeHeight:0.00} m)" : "Walk: standing");
    }

    /// <summary>The model root and its build frame: model point v = Euler(0,yaw,0) * (scanWorld - center),
    /// world = root.TransformPoint(v). Lets edit tools work in the scan's frame whatever the model is doing.</summary>
    public bool TryGetModelFrame(out Transform modelRoot, out float modelYaw, out Vector3 modelCenter)
    {
        modelRoot = root ? root.transform : null; modelYaw = yaw; modelCenter = center;
        return isOn && root;
    }

    /// <summary>Puts a floor point given in the scan's world frame under your feet, the model kept in its real-world
    /// orientation (desktop app: jump to a place; tests).</summary>
    public void PlaceAtScanPoint(Vector3 scanFloorPoint)
    {
        if (!isOn || !root) return;
        Place(Quaternion.Euler(0, yaw, 0) * (scanFloorPoint - center), Quaternion.Inverse(Quaternion.Euler(0, yaw, 0)));
        blink = 1f;
    }

    /// <summary>Physics hit against the walk-through's own walls/floors (for occlusion checks by edit tools).</summary>
    public bool RaycastModel(Ray ray, out RaycastHit hit, float maxDistance = 50f) =>
        Physics.Raycast(ray, out hit, maxDistance, WalkMask, QueryTriggerInteraction.Ignore);

    /// <summary>Your virtual head in the scan's own world frame (where MRUK's anchors are) - for the minimap.</summary>
    public bool TryGetScanWorldPose(out Vector3 pos, out Vector3 forward)
    {
        pos = forward = default;
        if (!isOn || !root || !head) return false;
        Quaternion gInv = Quaternion.Inverse(Quaternion.Euler(0, yaw, 0));
        pos = gInv * root.transform.InverseTransformPoint(head.position) + center;
        forward = gInv * root.transform.InverseTransformDirection(head.forward);
        return true;
    }

    /// <summary>Set when walking on the Windows desktop app: keyboard/mouse instead of controllers. The camera is the
    /// head, the rig root sits at floor level (tracking space), the "hand" ray is the view ray (doors are clicked).</summary>
    DesktopWalkRig desktop;
    public bool IsDesktop => desktop;

    bool ResolveRig()
    {
        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig)
        {
            desktop = null;
            head = rig.centerEyeAnchor; trackingSpace = rig.trackingSpace; rightHand = rig.rightHandAnchor;
            return head && trackingSpace && rightHand;
        }
        desktop = FindFirstObjectByType<DesktopWalkRig>();
        if (!desktop || !desktop.Head) return false;
        head = desktop.Head; trackingSpace = desktop.transform; rightHand = desktop.Head;
        return true;
    }

    /// <summary>Colliders for everything except doors; doors become see-through.</summary>
    void PrepareParts()
    {
        doors.Clear();
        windowPanels.Clear();
        var seeThrough = Shader.Find("Sprites/Default");
        foreach (var mf in walkVisual.GetComponentsInChildren<MeshFilter>())
        {
            mf.gameObject.layer = WalkLayer;
            var mr = mf.GetComponent<MeshRenderer>();
            bool isDoor = mr && mr.sharedMaterial && mr.sharedMaterial.name == "DOOR";
            if (mr && mr.sharedMaterial && mr.sharedMaterial.name == "WINDOW")
            {
                // Every window is glass from the start (only the opening ones need setting up - they then get real
                // sashes instead of this pane). Still solid to walk into.
                windowPanels.Add(mr);
                if (seeThrough != null && mf.sharedMesh)
                {
                    Destroy(mr.sharedMaterial);
                    // Same glass as an opening sash (WalkDoors): re-tint the shaded vertex colours from the window
                    // palette colour to the glass colour (keeps the per-face shading), white material, same alpha.
                    var cols = mf.sharedMesh.colors;
                    if (cols.Length == 0) cols = WhiteColors(mf.sharedMesh.vertexCount);
                    else
                    {
                        Color g = WalkDoors.GlassColor, b = WindowPaletteColor;
                        for (int i = 0; i < cols.Length; i++)
                            cols[i] = new Color(Mathf.Clamp01(cols[i].r * g.r / b.r), Mathf.Clamp01(cols[i].g * g.g / b.g), Mathf.Clamp01(cols[i].b * g.b / b.b), 1f);
                    }
                    mf.sharedMesh.colors = cols;
                    mr.sharedMaterial = new Material(seeThrough) { name = "WINDOW_GLASS", color = new Color(1f, 1f, 1f, WalkDoors.GlassColor.a) };
                }
            }
            if (isDoor)
            {
                if (seeThrough != null && mf.sharedMesh)
                {
                    var c = mr.sharedMaterial.color;
                    Destroy(mr.sharedMaterial);
                    // Sprites/Default multiplies by vertex colour - a shaded mesh already has its colour there,
                    // a plain one gets explicit white ones.
                    if (mf.sharedMesh.colors.Length == 0) mf.sharedMesh.colors = WhiteColors(mf.sharedMesh.vertexCount);
                    var m = new Material(seeThrough) { name = "DOOR_WALK", color = new Color(c.r, c.g, c.b, DoorAlphaFar) };
                    mr.sharedMaterial = m;
                    doors.Add((mr, m, c));
                }
                continue; // no collider: walk straight through
            }
            if (!mf.sharedMesh) continue;
            var col = mf.gameObject.AddComponent<MeshCollider>();
            col.sharedMesh = mf.sharedMesh;
        }
        Physics.SyncTransforms();
    }

    static Color[] WhiteColors(int n)
    {
        var a = new Color[n];
        for (int i = 0; i < n; i++) a[i] = Color.white;
        return a;
    }

    /// <summary>Puts a model-space floor point right under your feet, with the model rotated to rot.</summary>
    void Place(Vector3 modelPoint, Quaternion rot)
    {
        Vector3 feet = new Vector3(head.position.x, TargetFloorY, head.position.z);
        root.transform.SetPositionAndRotation(feet - rot * modelPoint, rot);
        Physics.SyncTransforms();
    }

    /// <summary>Real-world alignment: snaps the model's floor under you to the real floor, if there is one within
    /// reach (a small offset between scan and tracking floor is normal; above ~1.2 m it's likely another story).</summary>
    bool SnapToFloorWide()
    {
        Vector3 origin = new Vector3(head.position.x, TargetFloorY + 1.2f, head.position.z);
        if (!Physics.Raycast(origin, Vector3.down, out var hit, 50f, WalkMask, QueryTriggerInteraction.Ignore) || hit.normal.y < 0.5f)
            return false;
        root.transform.position += Vector3.up * (TargetFloorY - hit.point.y);
        Physics.SyncTransforms();
        return true;
    }

    /// <summary>Middle of the largest room's floor anchor, in model space - works for every WalkModel (the scanned
    /// meshes have no separate floor parts to look for).</summary>
    Vector3 LargestRoomPoint()
    {
        float best = -1f; Vector3 point = Vector3.zero;
        Quaternion g = Quaternion.Euler(0, yaw, 0);
        foreach (var r in walkRooms)
            foreach (var f in r.FloorAnchors)
            {
                if (f == null || !f.PlaneRect.HasValue) continue;
                float area = f.PlaneRect.Value.width * f.PlaneRect.Value.height;
                if (area <= best) continue;
                best = area;
                point = g * (f.transform.position - center);
            }
        return point;
    }

    void Update()
    {
        if (!isOn) return;
        if (!root || !head || !trackingSpace) { Exit(); return; }
        float dt = Time.deltaTime;
        HandleTeleport();
        HandleSnapTurn();
        HandleMove(dt);
        FollowFloor(dt);
        HandleFurnitureGrab();
        if (walkVisual && walkDoors.Tick(walkVisual.transform.InverseTransformPoint(head.position), dt, EditModeController.AnyOn)) Physics.SyncTransforms();
        UpdateDoors();
        UpdateFade(dt);
    }

    void HandleMove(float dt)
    {
        Vector2 s = desktop ? desktop.Move : OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        if (s.sqrMagnitude < 0.04f || (!desktop && StairEditTool.StickCapture)) return;
        float speed = moveSpeed * (desktop ? desktop.SpeedMultiplier : 1f);
        Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(head.up, Vector3.up); // looking straight down
        fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        Vector3 move = Vector3.ClampMagnitude(fwd * s.y + right * s.x, 1f) * speed * dt;
        move = Collide(move);
        if (move.sqrMagnitude < 1e-8f) return;
        root.transform.position -= move; // moving the world back == you moving forward
        Physics.SyncTransforms();
    }

    /// <summary>Capsule from just above step height up to your head - steps/thresholds below it never block,
    /// walls/windows do. Slides along whatever it hits instead of stopping dead.</summary>
    Vector3 Collide(Vector3 move)
    {
        Vector3 hp = head.position;
        Vector3 p1 = new Vector3(hp.x, TargetFloorY + StepUp + BodyRadius, hp.z);
        Vector3 p2 = new Vector3(hp.x, Mathf.Max(p1.y, hp.y - 0.15f), hp.z);
        for (int i = 0; i < 3; i++)
        {
            float dist = move.magnitude;
            if (dist < 1e-5f) return Vector3.zero;
            Vector3 dir = move / dist;
            if (!Physics.CapsuleCast(p1, p2, BodyRadius, dir, out var hit, dist + Skin, WalkMask, QueryTriggerInteraction.Ignore))
                return move;
            {
                Quaternion gInv = Quaternion.Inverse(Quaternion.Euler(0, yaw, 0));
                Vector3 S(Vector3 w) => gInv * root.transform.InverseTransformPoint(w) + center;
                var bnd = hit.collider.bounds;
                LastBlocker = $"{hit.collider.name} ({hit.collider.transform.parent?.name}) at {hit.point.y - TargetFloorY:0.00} m above feet, " +
                              $"point {S(hit.point)}, normal {hit.normal}, collider {S(bnd.min)}..{S(bnd.max)}, capsule {S(p1)}..{S(p2)} r {BodyRadius}, " +
                              $"iter {i} move {dist:0.000} hitDist {hit.distance:0.000} dir {dir}";
            }
            Vector3 n = hit.normal; n.y = 0f;
            if (i == 2 || n.sqrMagnitude < 1e-4f) return dir * Mathf.Max(0f, hit.distance - Skin);
            move = Vector3.ProjectOnPlane(move, n.normalized);
        }
        return move;
    }

    /// <summary>What stopped the last move (diagnostics / desktop tests).</summary>
    public string LastBlocker { get; private set; } = "";

    float fallSpeed;
    const float FallRing = 0.3f;     // a narrower gap than this all round you (doorway between slabs) is no hole

    void FollowFloor(float dt)
    {
        if (!ProbeFloor(out float floorY))
        {
            Fall(dt);
            return;
        }
        fallSpeed = 0f;
        float delta = TargetFloorY - floorY;
        if (Mathf.Abs(delta) < 0.0005f) return;
        root.transform.position += Vector3.up * (delta * (1f - Mathf.Exp(-HeightFollow * dt)));
        Physics.SyncTransforms();
    }

    /// <summary>No floor within a step under you, inside a drawn opening, and none anywhere within FallRing around
    /// you either (not just the gap under a doorway) - with a floor further down: fall onto it.</summary>
    readonly List<Rect> openingRects = new List<Rect>();

    void Fall(float dt)
    {
        Vector3 hp = head.position;
        // Only ever through an opening the user drew - never through a gap in the scan or out of a wall your head
        // went into (in the headset you walk for real, and a wall can't stop you).
        Vector3 m = walkVisual ? walkVisual.transform.InverseTransformPoint(hp) : Vector3.zero;
        if (!walkVisual || !openingRects.Any(r => r.Contains(new Vector2(m.x, m.z)))) { fallSpeed = 0f; return; }
        for (int i = 0; i < 8; i++)
        {
            Vector3 off = Quaternion.Euler(0, i * 45f, 0) * Vector3.forward * FallRing;
            if (ProbeFloorAt(hp + off, out _)) { fallSpeed = 0f; return; } // just a gap - stay at this height
        }
        Vector3 origin = new Vector3(hp.x, TargetFloorY, hp.z);
        if (!Physics.SphereCast(origin, 0.1f, Vector3.down, out var hit, 10f, WalkMask, QueryTriggerInteraction.Ignore) || hit.normal.y < 0.5f)
        { fallSpeed = 0f; return; } // nothing below (edge of the model) - stay
        fallSpeed = Mathf.Min(fallSpeed + 9.81f * dt, 12f);
        float drop = Mathf.Min(fallSpeed * dt, TargetFloorY - hit.point.y);
        if (drop <= 0f) return;
        root.transform.position += Vector3.up * drop; // the world rises == you fall
        Physics.SyncTransforms();
    }

    bool ProbeFloor(out float y) => ProbeFloorAt(head.position, out y);

    bool ProbeFloorAt(Vector3 hp, out float y)
    {
        y = 0f;
        Vector3 origin = new Vector3(hp.x, TargetFloorY + StepUp + 0.1f, hp.z);
        float dist = StepUp + 0.1f + StepDown;
        if (Physics.Raycast(origin, Vector3.down, out var hit, dist, WalkMask, QueryTriggerInteraction.Ignore) && hit.normal.y > 0.5f)
        { y = hit.point.y; return true; }
        // A thin ray can slip through the narrow gap under a doorway between two rooms' floor slabs - a small
        // sphere bridges it.
        if (Physics.SphereCast(origin, 0.12f, Vector3.down, out hit, dist, WalkMask, QueryTriggerInteraction.Ignore) && hit.normal.y > 0.5f)
        { y = hit.point.y; return true; }
        return false;
    }

    void HandleSnapTurn()
    {
        if (aiming || desktop) return; // desktop: the mouse turns the rig itself
        if (StairEditTool.StickCapture) { turnArmed = false; return; }
        float x = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).x;
        if (turnArmed && Mathf.Abs(x) > 0.7f)
        {
            // Rotating the world by -angle around your head == you turning by +angle.
            root.transform.RotateAround(head.position, Vector3.up, -Mathf.Sign(x) * snapTurnDeg);
            Physics.SyncTransforms();
            turnArmed = false;
        }
        else if (Mathf.Abs(x) < 0.3f) turnArmed = true;
    }

    void HandleTeleport()
    {
        if (desktop) return;
        Vector2 r = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        if (StairEditTool.StickCapture)
        {
            // The sticks adjust a stair - drop any aim without teleporting.
            if (aiming) { aiming = false; teleportValid = false; ShowArc(false); }
            return;
        }
        if (!aiming)
        {
            if (r.y > 0.7f && Mathf.Abs(r.x) < 0.5f) aiming = true;
            else return;
        }
        if (r.y < 0.25f)
        {
            aiming = false;
            ShowArc(false);
            if (teleportValid) DoTeleport();
            teleportValid = false;
            return;
        }
        ComputeArc();
    }

    void ComputeArc()
    {
        arcPoints.Clear();
        teleportValid = false;
        bool hitAny = false;
        Vector3 p = rightHand.position, v = rightHand.forward * TeleportSpeed;
        arcPoints.Add(p);
        const float stepT = 0.03f;
        for (int i = 0; i < 90; i++)
        {
            Vector3 next = p + v * stepT;
            v += Vector3.down * (9.81f * stepT);
            if (Physics.Linecast(p, next, out var hit, WalkMask, QueryTriggerInteraction.Ignore))
            {
                arcPoints.Add(hit.point);
                hitAny = true;
                teleportPoint = hit.point;
                teleportValid = hit.normal.y > 0.7f && HasHeadroom(hit.point);
                break;
            }
            arcPoints.Add(next);
            p = next;
        }
        ShowArc(true);
        arc.positionCount = arcPoints.Count;
        arc.SetPositions(arcPoints.ToArray());
        Color c = teleportValid ? new Color(0.3f, 0.95f, 0.45f, 0.9f) : new Color(1f, 0.35f, 0.3f, 0.9f);
        arc.startColor = arc.endColor = c;
        reticle.SetActive(hitAny);
        if (hitAny)
        {
            reticle.transform.position = teleportPoint + Vector3.up * 0.01f;
            reticleMat.color = new Color(c.r, c.g, c.b, 0.6f);
        }
    }

    static bool HasHeadroom(Vector3 floorPoint) =>
        !Physics.CheckCapsule(floorPoint + Vector3.up * 0.45f, floorPoint + Vector3.up * 1.6f, 0.12f, WalkMask, QueryTriggerInteraction.Ignore);

    void DoTeleport()
    {
        Vector3 feet = new Vector3(head.position.x, TargetFloorY, head.position.z);
        root.transform.position += feet - teleportPoint;
        Physics.SyncTransforms();
        blink = 1f; // short black blink - a hard cut without it is disorienting
    }

    void UpdateDoors()
    {
        Vector3 hp = head.position;
        foreach (var (r, m, c) in doors)
        {
            if (!r) continue;
            if (walkDoors.Suppressed.Contains(r)) { r.enabled = false; continue; } // replaced by a real leaf
            float d = Vector3.Distance(hp, r.bounds.ClosestPoint(hp));
            float a = DoorAlphaFar * Mathf.InverseLerp(DoorFadeNear, DoorFadeFar, d);
            m.color = new Color(c.r, c.g, c.b, a);
            r.enabled = a > 0.01f;
        }
    }

    void UpdateFade(float dt)
    {
        bool inWall = Physics.CheckSphere(head.position, 0.08f, WalkMask, QueryTriggerInteraction.Ignore);
        fade = Mathf.MoveTowards(fade, inWall ? 0.95f : 0f, dt * 4f);
        blink = Mathf.MoveTowards(blink, 0f, dt * 5f);
        float a = Mathf.Max(fade, blink);
        if (fadeQuad == null) return;
        fadeQuad.SetActive(a > 0.001f);
        fadeMat.color = new Color(0, 0, 0, a);
    }

    void ShowArc(bool on)
    {
        if (arc) arc.enabled = on;
        if (!on && reticle) reticle.SetActive(false);
    }

    void BuildHelpers()
    {
        var shader = Shader.Find("Sprites/Default");
        if (shader == null) { uiLog?.AddLog("<color=orange>Walk: Sprites/Default shader missing - no teleport arc/fade visuals.</color>"); return; }

        var arcGo = new GameObject("WalkTeleportArc");
        arc = arcGo.AddComponent<LineRenderer>();
        arcMat = new Material(shader);
        arc.sharedMaterial = arcMat;
        arc.widthMultiplier = 0.012f;
        arc.numCapVertices = 4;
        arc.useWorldSpace = true;
        arc.enabled = false;

        reticle = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        reticle.name = "WalkTeleportReticle";
        Destroy(reticle.GetComponent<Collider>());
        var rmf = reticle.GetComponent<MeshFilter>();
        reticleMesh = Instantiate(rmf.sharedMesh); // own copy - the primitive's mesh is a shared engine asset
        reticleMesh.colors = WhiteColors(reticleMesh.vertexCount);
        rmf.sharedMesh = reticleMesh;
        reticleMat = new Material(shader);
        reticle.GetComponent<MeshRenderer>().sharedMaterial = reticleMat;
        reticle.transform.localScale = new Vector3(0.45f, 0.004f, 0.45f);
        reticle.SetActive(false);

        fadeQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        fadeQuad.name = "WalkFade";
        Destroy(fadeQuad.GetComponent<Collider>());
        var fmf = fadeQuad.GetComponent<MeshFilter>();
        fadeMesh = Instantiate(fmf.sharedMesh);
        fadeMesh.colors = WhiteColors(fadeMesh.vertexCount);
        fmf.sharedMesh = fadeMesh;
        fadeMat = new Material(shader) { renderQueue = 4000, color = new Color(0, 0, 0, 0) };
        fadeQuad.GetComponent<MeshRenderer>().sharedMaterial = fadeMat;
        fadeQuad.transform.SetParent(head, false);
        fadeQuad.transform.localPosition = new Vector3(0, 0, 0.07f);
        fadeQuad.transform.localRotation = Quaternion.identity;
        fadeQuad.transform.localScale = Vector3.one * 0.6f;
        fadeQuad.SetActive(false);
        fade = blink = 0f;
    }

    void DestroyHelpers()
    {
        if (arc) Destroy(arc.gameObject);
        if (reticle) Destroy(reticle);
        if (fadeQuad) Destroy(fadeQuad);
        if (arcMat) Destroy(arcMat);
        if (reticleMat) Destroy(reticleMat);
        if (fadeMat) Destroy(fadeMat);
        if (reticleMesh) Destroy(reticleMesh);
        if (fadeMesh) Destroy(fadeMesh);
        arc = null; reticle = null; fadeQuad = null;
    }

    void OnDestroy() { if (isOn) Exit(); }
}
