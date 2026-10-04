using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using TMPro;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
using Meta.XR;
#endif

public class DollHouseVisualizer : MonoBehaviour
{
    public float scale = 0.05f, spawnDistance = 1.0f;
    public XRMenu uiLog;
    private GameObject root;
    private Transform rightHand;
    // On/off and which of the 3 model tiers to show are separate now: one button used to cycle through all 4
    // states (Off/Anchor/Mesh/Raw) at once, so turning it back on after Raw needed 3 more presses to reach the
    // familiar Anchor view again, and a stray extra press silently swapped you into a different tier. A
    // dedicated on/off toggle and a separate mode-cycle keep "on" always showing whatever tier you last picked.
    private bool isOn = false;
    private DollhouseMode mode = DollhouseMode.AnchorAnalytical;
    private bool grabbed = false;
    private Vector3 off;
    private Quaternion rotOff;
    private readonly List<(Transform text, Vector3 localLineDir, TextMeshPro tmp, LineRenderer line)> dimensionLabels = new List<(Transform, Vector3, TextMeshPro, LineRenderer)>();
    private enum DollhouseMode { AnchorAnalytical, AnchorWithDimensions, MeshAnalytical, RawMesh }
    // Real-world target sizes for dimension lines/text, held constant regardless of the Dollhouse's own zoom -
    // see the per-frame rescale in Update(). A fixed LOCAL-space size (the old approach) instead grows or
    // shrinks along with the model itself as the user zooms, which is exactly why the lines kept looking
    // "hrozně tlusté" (thick) once the model was scaled up toward its real 1:1 size.
    const float DimLineWidthMeters = 0.0015f;
    const float DimTextWorldHeight = 0.07f;

    public bool IsOn => isOn;
    public bool ToggleOnOff() { isOn = !isOn; Refresh(); return isOn; }

    // Walk-through support: while walking the Dollhouse stays built but hidden, and pointing at its floor shows
    // a little figure where you'd enter (right trigger enters there - see XRMenu).
    private bool hidden;
    private GameObject enterMarker;
    private Mesh enterMarkerMesh;
    private List<(Transform t, Vector3[] v, int[] tri)> floorCache;
    const float MarkerHeight = 1.7f, MarkerWidth = 0.4f; // model metres - a person-sized figure at any zoom

    private float modelYaw; private Vector3 modelCenter;

    // Stairs from the user's edits (Anchor modes - the Mesh/Raw tiers show the scanned geometry as it is).
    private readonly StairsInModel stairs = new StairsInModel();
    private GameObject visualGo;
    private List<MRUKRoom> lastRooms;
    readonly FurnitureInModel furniture = new FurnitureInModel();
    void OnEnable() { HouseEditsStore.Changed += OnEditsChanged; }
    void OnDisable() { HouseEditsStore.Changed -= OnEditsChanged; }
    string walledUp = "";
    void OnEditsChanged() {
        // A doorway walled up / reopened changes the model itself (rebuilt in place); else just the stairs/openings.
        string sig = DoorCatalog.WalledUpSignature(HouseEditsStore.Current);
        if (sig != walledUp) { walledUp = sig; RebuildInPlace(); }
        else ApplyStairs();
    }
    void ApplyStairs() {
        if (!isOn || !root || !visualGo || lastRooms == null) return;
        if (mode != DollhouseMode.AnchorAnalytical && mode != DollhouseMode.AnchorWithDimensions) return;
        stairs.Apply(visualGo, modelYaw, modelCenter, lastRooms, HouseEditsStore.Current, false, 0);
        floorCache = null; // floor meshes may have just been re-cut
    }

    /// <summary>Root and build frame of the visible Dollhouse (see WalkThroughMode.TryGetModelFrame).</summary>
    public bool TryGetModelFrame(out Transform modelRoot, out float yaw, out Vector3 center) {
        modelRoot = root ? root.transform : null; yaw = modelYaw; center = modelCenter;
        return root && isOn && !hidden && root.activeSelf;
    }

    /// <summary>Hides/shows the built Dollhouse without rebuilding it (used while walking through the model).</summary>
    public void SetHidden(bool h) {
        hidden = h;
        if (root) root.SetActive(!h);
    }

    /// <summary>The Dollhouse's own heading, yaw only - entering the walk-through with it keeps the house turned
    /// the same way you were just looking at it.</summary>
    public Quaternion FacingYaw {
        get {
            if (!root) return Quaternion.identity;
            Vector3 f = Vector3.ProjectOnPlane(root.transform.forward, Vector3.up);
            if (f.sqrMagnitude < 1e-4f) f = Vector3.ProjectOnPlane(root.transform.up, Vector3.up);
            return f.sqrMagnitude < 1e-4f ? Quaternion.identity : Quaternion.LookRotation(f.normalized, Vector3.up);
        }
    }

    /// <summary>The floor point (model space) the right controller points at, if any.</summary>
    public bool TryGetPointedFloor(out Vector3 modelPoint) {
        modelPoint = default;
        if (!root || !isOn || hidden || !root.activeSelf || !rightHand) return false;
        return TryRaycastFloor(new Ray(rightHand.position, rightHand.forward), out modelPoint);
    }

    /// <summary>Exact ray-vs-triangle test against the FLOOR meshes only (the Dollhouse's own collider is just its
    /// bounding box, for grabbing), returned in root-local = model space.</summary>
    bool TryRaycastFloor(Ray ray, out Vector3 modelPoint) {
        modelPoint = default;
        if (floorCache == null) {
            floorCache = new List<(Transform, Vector3[], int[])>();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                if (mf.name == "FLOOR" && mf.sharedMesh) floorCache.Add((mf.transform, mf.sharedMesh.vertices, mf.sharedMesh.triangles));
        }
        float best = float.MaxValue; bool found = false; Vector3 bestWorld = default;
        foreach (var (t, v, tri) in floorCache) {
            if (!t) continue;
            // Mapping two points (not a direction) keeps the ray parameter identical to world distance along the ray.
            Vector3 o = t.InverseTransformPoint(ray.origin);
            Vector3 d = t.InverseTransformPoint(ray.origin + ray.direction) - o;
            for (int i = 0; i + 2 < tri.Length; i += 3) {
                if (RayTriangle(o, d, v[tri[i]], v[tri[i + 1]], v[tri[i + 2]], out float dist) && dist < best) {
                    best = dist; found = true; bestWorld = ray.origin + ray.direction * dist;
                }
            }
        }
        if (found) modelPoint = root.transform.InverseTransformPoint(bestWorld);
        return found;
    }

    static bool RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t) {
        t = 0;
        Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (Mathf.Abs(det) < 1e-9f) return false;
        float inv = 1f / det;
        Vector3 s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return false;
        Vector3 q = Vector3.Cross(s, e1);
        float w = Vector3.Dot(d, q) * inv;
        if (w < 0 || u + w > 1) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 0;
    }

    void UpdateEnterMarker() {
        Vector3 p = default;
        if (grabbed || !TryGetPointedFloor(out p)) { if (enterMarker) enterMarker.SetActive(false); return; }
        if (!enterMarker) {
            enterMarker = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            enterMarker.name = "WalkEnterMarker";
            Destroy(enterMarker.GetComponent<Collider>());
            var mf = enterMarker.GetComponent<MeshFilter>();
            enterMarkerMesh = Instantiate(mf.sharedMesh); // own copy: Cleanup() destroys every mesh under root
            mf.sharedMesh = enterMarkerMesh;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            enterMarker.GetComponent<MeshRenderer>().sharedMaterial = new Material(shader) { color = new Color(1f, 0.55f, 0.1f) };
            enterMarker.transform.SetParent(root.transform, false);
            enterMarker.transform.localScale = new Vector3(MarkerWidth, MarkerHeight / 2f, MarkerWidth); // capsule primitive is 2 units tall
        }
        enterMarker.SetActive(true);
        enterMarker.transform.localPosition = p + Vector3.up * (MarkerHeight / 2f);
        enterMarker.transform.localRotation = Quaternion.identity;
    }

    /// <summary>Cycles Anchor -&gt; Anchor+Dimensions -&gt; Mesh -&gt; Raw -&gt; Anchor. Rebuilds immediately if the dollhouse is on.</summary>
    public string CycleMode() {
        mode = (DollhouseMode)(((int)mode + 1) % 4);
        if (isOn) Refresh();
        return ModeLabel;
    }
    public bool IsDragging => grabbed;

    /// <summary>Short label for the status bar, so which of the 4 dollhouse modes is on screen is never a guess.</summary>
    public string ModeLabel => mode switch
    {
        DollhouseMode.AnchorAnalytical => "Anchor",
        DollhouseMode.AnchorWithDimensions => "Anchor+Dim",
        DollhouseMode.MeshAnalytical => "Mesh",
        DollhouseMode.RawMesh => "Raw",
        _ => "?",
    };

    /// <summary>Rebuilds the currently-shown model in place (e.g. after the active scan changed) - the
    /// dollhouse stays open and exactly where it was, per Refresh()'s placement-preserving behaviour.</summary>
    public void RebuildInPlace() { if (isOn) Refresh(); }

    private async void Refresh() {
        // If the dollhouse is already showing something (switching mode, or rebuilding after a scan change),
        // keep it exactly where the user left it instead of re-spawning in front of the camera - only a fresh
        // activation (root was null/destroyed, i.e. it was off) computes a new spawn position.
        Vector3? keepPos = null;
        Quaternion keepRot = Quaternion.identity;
        float keepScale = scale;
        if (root) { keepPos = root.transform.position; keepRot = root.transform.rotation; keepScale = root.transform.localScale.x; }

        Cleanup();
        if (!isOn) return;

        var camera = Camera.main;
        if (camera == null) { uiLog?.AddLog("<color=red>Dollhouse: no main camera</color>"); return; }

        uiLog?.AddLog($"Dollhouse Refresh: {mode}");
        camera.nearClipPlane = 0.001f;
        root = new GameObject("DollhouseRoot");
        if (keepPos.HasValue) {
            scale = keepScale;
            root.transform.SetPositionAndRotation(keepPos.Value, keepRot);
        } else {
            var cam = camera.transform;
            root.transform.SetPositionAndRotation(cam.position + cam.forward * spawnDistance, Quaternion.Euler(0, cam.eulerAngles.y + 180, 0));
        }
        root.transform.localScale = Vector3.one * scale;

        // Same filtering/dedup as the exporter, so the preview always matches what gets exported
        var rooms = MRUKDataProcessor.GetValidRooms(MRUK.Instance);

        if (rooms.Count == 0) {
            uiLog?.AddLog("<color=red>Dollhouse: No valid rooms found</color>");
            Cleanup();
            return;
        }

        uiLog?.AddLog($"Dollhouse: Processing {rooms.Count} rooms...");

        Vector3 c = CalculateCenter(rooms);
        // Same wall alignment as the export, so the preview is oriented like the exported model.
        float yaw = FloorPlanBuilder.CorrectionYaw(MRUKPlanExtractor.Extract(rooms));
        modelYaw = yaw; modelCenter = c;
        XRHouseModel m = null;

        try {
            // rooms drawn from the furniture spec (not scanned) make the walls next to them thin, as when walking
            XRModelFactory.ExtraFaces = Furniture.ExtraFaces(Furniture.Load(HouseEditsStore.CurrentScan), rooms, yaw, c);
            if (mode == DollhouseMode.AnchorAnalytical) m = XRModelFactory.CreateAnchorAnalytical(rooms, yaw, c);
            // Same baked dimension lines as the exported model (thin bars that scale with the model, so they
            // read as fine lines at any zoom); only the numbers are added separately below as billboarded text.
            else if (mode == DollhouseMode.AnchorWithDimensions) m = XRModelFactory.CreateAnchorAnalyticalWithDimensions(rooms, yaw, c, withText: false);
            else if (mode == DollhouseMode.MeshAnalytical) m = await XRModelFactory.CreateMeshAnalytical(rooms, yaw, c, forDollhouse: true);
            else if (mode == DollhouseMode.RawMesh) m = await XRModelFactory.CreateRawScan(rooms, yaw, c, forDollhouse: true);
            XRModelFactory.ExtraFaces = null;

            if (m != null) {
                var visual = UnityModelLoader.LoadToScene(m);
                if (visual != null) {
                    visual.transform.SetParent(root.transform, false);
                    AddCol(visual);
                    visualGo = visual; lastRooms = rooms;
                    ApplyStairs();
                    // furniture and the rooms the scan couldn't take (same spec as the walk-through)
                    furniture.Clear();
                    if (mode == DollhouseMode.AnchorAnalytical || mode == DollhouseMode.AnchorWithDimensions)
                        furniture.Apply(visual, yaw, c, rooms, HouseEditsStore.CurrentScan, visual.layer);
                    // Registers it with the XR Interaction Toolkit purely so the ray hovers it as a valid
                    // target (turns green and clips at its surface - see XRMenu.StyleRay); the actual grab is
                    // still the same custom grip+raycast logic in Update() below.
                    root.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
                    if (mode == DollhouseMode.AnchorWithDimensions) AddDimensionLabels(m.dimensions);
                    uiLog?.AddLog("<color=green>Dollhouse: Model Loaded</color>");
                    if (hidden) root.SetActive(false); // rebuilt while walking - stays out of the way
                }
            }
        } catch (Exception ex) {
            uiLog?.AddLog($"<color=red>Dollhouse Error: {ex.Message}</color>");
            Debug.LogException(ex);
        }
    }

    /// <summary>
    /// Instantiates one 3D world-space text plus an actual line for its measured span, per
    /// XRModelFactory.XRDimensionLabel - each already carries its own position/rotation/line endpoints in the
    /// model's local space (see XRModelFactory.CreateBoxPart's convention), so everything is parented directly
    /// under the Dollhouse root with no further transform, exactly like the rest of the model.
    /// </summary>
    private void AddDimensionLabels(List<XRDimensionLabel> dims) {
        dimensionLabels.Clear();
        // Same green/purple as a floor plan's own outer/inner dimension lines (DimensionColors) - two shared
        // materials, not one per label, so a rebuild never leaks more than these two unique instances.
        // The number itself stays a single neutral colour regardless of outer/inner, exactly like a floor
        // plan's own dimension text (dark on paper, white here for contrast against the model) - only the line
        // it belongs to is colour-coded.
        var textColor = Color.white;

        foreach (var d in dims) {
            LineRenderer lr = null; // the line itself is part of the model mesh (DimensionGeometry)

            var go = new GameObject("Dim_" + d.text);
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = d.position;
            go.transform.localRotation = d.rotation;
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = d.text;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.color = textColor;
            var mat = tmp.fontMaterial;
            if (mat.HasProperty("_CullMode")) mat.SetFloat("_CullMode", (float)UnityEngine.Rendering.CullMode.Off);
            else if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            // A Dollhouse can be freely picked up and rotated, so a single fixed orientation baked in at build
            // time can only ever be readable from one side. Rather than a free "always face the camera"
            // billboard (which let the text drift away from looking like it belongs to its own line, rotating
            // in every axis), Update() only ever flips it 180 degrees around the LINE's own axis - it stays
            // visually anchored to the line, and just picks whichever of its two possible readings faces you.
            Vector3 localLineDir = (d.lineEnd - d.lineStart).sqrMagnitude > 1e-6f ? (d.lineEnd - d.lineStart).normalized : Vector3.right;
            dimensionLabels.Add((go.transform, localLineDir, tmp, lr));
        }
    }

    /// <summary>The model's build origin - shared with WalkThroughMode, so a point picked on the Dollhouse is the
    /// same model-space point in the walk-through model.</summary>
    public static Vector3 CalculateCenter(List<MRUKRoom> rooms) {
        Vector3 c = Vector3.zero; int n = 0;
        foreach (var r in rooms) {
            var f = r.FloorAnchors.FirstOrDefault(a => a != null);
            if (f != null) { c += f.transform.position; n++; }
        }
        return n > 0 ? c / n : rooms[0].transform.position;
    }

    private void Cleanup() {
        stairs.Dispose();
        visualGo = null;
        if (root) {
            // includeInactive: the root itself is inactive while hidden for the walk-through, and so can be the
            // enter marker - skipping inactive children would leak all of their meshes/materials.
            foreach(var mf in root.GetComponentsInChildren<MeshFilter>(true)) if(mf.sharedMesh) Destroy(mf.sharedMesh);
            // Renderer (not just MeshRenderer) also catches the dimension lines' LineRenderers and the
            // dimension text's own TextMeshPro renderer, whose materials are every bit as uniquely created
            // here as a MeshRenderer's and would otherwise leak one set per Dollhouse rebuild.
            foreach(var mr in root.GetComponentsInChildren<Renderer>(true)) if(mr.sharedMaterial) Destroy(mr.sharedMaterial);
            Destroy(root);
        }
        dimensionLabels.Clear();
        floorCache = null; enterMarker = null; enterMarkerMesh = null;
        var camera = Camera.main;
        if (camera != null) camera.nearClipPlane = 0.1f;
    }

    private void AddCol(GameObject go) {
        Bounds b = new Bounds(go.transform.position, Vector3.zero);
        var rs = go.GetComponentsInChildren<Renderer>();
        foreach (var r in rs) b.Encapsulate(r.bounds);
        if (rs.Length > 0) {
            var col = root.AddComponent<BoxCollider>();
            col.center = root.transform.InverseTransformPoint(b.center);
            col.size = b.size / root.transform.lossyScale.x;
        }
    }

    void Update() {
        if (!root || !isOn || !root.activeSelf) return;

        if (dimensionLabels.Count > 0) {
            // Lines/text are children of root, so a fixed LOCAL width/font size would grow or shrink right
            // along with root.transform.localScale as the user zooms the Dollhouse - dividing by the current
            // scale here cancels that out, so their REAL-WORLD size (DimLineWidthMeters / DimTextWorldHeight)
            // stays constant whether the model is shrunk to a miniature or blown up to its real 1:1 size.
            float invScale = 1f / Mathf.Max(scale, 0.0001f);
            var cam = Camera.main;
            Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
            foreach (var (t, localDir, tmp, line) in dimensionLabels) {
                if (!t) continue;
                if (line) line.startWidth = line.endWidth = DimLineWidthMeters * invScale;
                if (tmp) tmp.fontSize = DimTextWorldHeight * invScale;
                if (cam == null) continue;
                // Stays visually attached to its own line: reading direction (local right) is locked to the
                // line's own axis, only the perpendicular-to-that-axis component of "towards the camera" is
                // free, so it only ever flips 180 degrees around the line rather than spinning freely.
                // TextMeshPro is readable from behind its transform (looking along its +Z), so forward must point
                // from the camera to the text - pointing it at the camera (as before) showed the text mirrored.
                // Then flip the reading direction if needed so the text is never upside down (horizontal
                // lines) and reads bottom-to-top on vertical lines.
                Vector3 right = root.transform.TransformDirection(localDir).normalized;
                Vector3 forward = Vector3.ProjectOnPlane(t.position - camPos, right);
                if (forward.sqrMagnitude < 1e-6f) continue; // camera exactly on the line's own axis - keep last frame's rotation
                forward.Normalize();
                Vector3 worldUp = root.transform.up;
                bool vertical = Mathf.Abs(Vector3.Dot(right, worldUp)) > 0.7f;
                if (vertical && Vector3.Dot(right, worldUp) < 0) right = -right;
                Vector3 up = Vector3.Cross(forward, right);
                if (!vertical && Vector3.Dot(up, worldUp) < 0) { right = -right; up = -up; }
                t.rotation = Quaternion.LookRotation(forward, up);
            }
        }

        if (!rightHand) {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            rightHand = rig ? rig.rightHandAnchor : null;
            if (!rightHand) return;
        }
        UpdateEnterMarker();
        var hand = rightHand;
        bool grip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        Vector2 s = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        if (StairEditTool.StickCapture) s = Vector2.zero;
        if (!grabbed) {
            if (GrabLock.GripPressed && Physics.Raycast(hand.position, hand.forward, out RaycastHit hit) && hit.collider.gameObject == root && GrabLock.TryTake(this)) {
                grabbed = true;
                OVRInput.SetControllerVibration(0.1f, 0.1f, OVRInput.Controller.RTouch); Invoke(nameof(StopVib), 0.05f);
                off = hand.InverseTransformPoint(root.transform.position);
                rotOff = Quaternion.Inverse(hand.rotation) * root.transform.rotation;
            }
        } else {
            if (!grip) { grabbed = false; GrabLock.Release(this); return; }
            root.transform.position = hand.TransformPoint(off);
            root.transform.rotation = hand.rotation * rotOff;
            if (Mathf.Abs(s.x) > 0.1f) { root.transform.Rotate(Vector3.up, -s.x * 120f * Time.deltaTime, Space.World); rotOff = Quaternion.Inverse(hand.rotation) * root.transform.rotation; }
            if (Mathf.Abs(s.y) > 0.1f) { scale = Mathf.Clamp(scale + s.y * 0.5f * Time.deltaTime, 0.005f, 1.0f); root.transform.localScale = Vector3.one * scale; }
        }
    }
    private void StopVib() => OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);
}