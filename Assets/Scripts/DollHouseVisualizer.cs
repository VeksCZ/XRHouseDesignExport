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
    private readonly List<(Transform text, Vector3 localLineDir)> dimensionLabels = new List<(Transform, Vector3)>();
    private enum DollhouseMode { AnchorAnalytical, AnchorWithDimensions, MeshAnalytical, RawMesh }

    public bool IsOn => isOn;
    public bool ToggleOnOff() { isOn = !isOn; Refresh(); return isOn; }

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
        XRHouseModel m = null;

        try {
            if (mode == DollhouseMode.AnchorAnalytical || mode == DollhouseMode.AnchorWithDimensions) m = XRModelFactory.CreateAnchorAnalytical(rooms, yaw, c);
            else if (mode == DollhouseMode.MeshAnalytical) m = await XRModelFactory.CreateMeshAnalytical(rooms, yaw, c, forDollhouse: true);
            else if (mode == DollhouseMode.RawMesh) m = await XRModelFactory.CreateRawScan(rooms, yaw, c, forDollhouse: true);

            if (m != null) {
                var visual = UnityModelLoader.LoadToScene(m);
                if (visual != null) {
                    visual.transform.SetParent(root.transform, false);
                    AddCol(visual);
                    // Registers it with the XR Interaction Toolkit purely so the ray hovers it as a valid
                    // target (turns green and clips at its surface - see XRMenu.StyleRay); the actual grab is
                    // still the same custom grip+raycast logic in Update() below.
                    root.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
                    if (mode == DollhouseMode.AnchorWithDimensions) AddDimensionLabels(m.dimensions);
                    uiLog?.AddLog("<color=green>Dollhouse: Model Loaded</color>");
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
        // Same pale yellow as the floor plan's own dimension numbers/lines, for a consistent look between the two.
        var lineColor = new Color(1f, 0.92f, 0.55f, 0.9f);
        var lineMat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = lineColor };
        if (lineMat.HasProperty("_Cull")) lineMat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);

        foreach (var d in dims) {
            // The line sits on its own identity-transform child, separate from the label's own positioned one
            // below, so its two endpoints (already in the model's local space) map directly with no further
            // transform applied on top.
            var lineGo = new GameObject("DimLine_" + d.text);
            lineGo.transform.SetParent(root.transform, false);
            var lr = lineGo.AddComponent<LineRenderer>();
            lr.material = lineMat;
            lr.useWorldSpace = false;
            lr.positionCount = 2;
            lr.SetPosition(0, d.lineStart);
            lr.SetPosition(1, d.lineEnd);
            // Thin - the model-space width used to be 0.02 (2cm), which at the Dollhouse's zoomable scale
            // range (0.005-1.0) could render anywhere from hairline to a genuinely thick 2cm bar; 0.006 stays
            // thin across that whole range and reads as a real line, not a slab.
            lr.startWidth = lr.endWidth = 0.006f;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var go = new GameObject("Dim_" + d.text);
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = d.position;
            go.transform.localRotation = d.rotation;
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = d.text;
            tmp.fontSize = 1.6f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.color = lineColor;
            var mat = tmp.fontMaterial;
            if (mat.HasProperty("_CullMode")) mat.SetFloat("_CullMode", (float)UnityEngine.Rendering.CullMode.Off);
            else if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            // A Dollhouse can be freely picked up and rotated, so a single fixed orientation baked in at build
            // time can only ever be readable from one side. Rather than a free "always face the camera"
            // billboard (which let the text drift away from looking like it belongs to its own line, rotating
            // in every axis), Update() only ever flips it 180 degrees around the LINE's own axis - it stays
            // visually anchored to the line, and just picks whichever of its two possible readings faces you.
            Vector3 localLineDir = (d.lineEnd - d.lineStart).sqrMagnitude > 1e-6f ? (d.lineEnd - d.lineStart).normalized : Vector3.right;
            dimensionLabels.Add((go.transform, localLineDir));
        }
    }

    private Vector3 CalculateCenter(List<MRUKRoom> rooms) {
        Vector3 c = Vector3.zero; int n = 0;
        foreach (var r in rooms) {
            var f = r.FloorAnchors.FirstOrDefault(a => a != null);
            if (f != null) { c += f.transform.position; n++; }
        }
        return n > 0 ? c / n : rooms[0].transform.position;
    }

    private void Cleanup() {
        if (root) {
            foreach(var mf in root.GetComponentsInChildren<MeshFilter>()) if(mf.sharedMesh) Destroy(mf.sharedMesh);
            // Renderer (not just MeshRenderer) also catches the dimension lines' LineRenderers and the
            // dimension text's own TextMeshPro renderer, whose materials are every bit as uniquely created
            // here as a MeshRenderer's and would otherwise leak one set per Dollhouse rebuild.
            foreach(var mr in root.GetComponentsInChildren<Renderer>()) if(mr.sharedMaterial) Destroy(mr.sharedMaterial);
            Destroy(root);
        }
        dimensionLabels.Clear();
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
        if (!root || !isOn) return;

        if (dimensionLabels.Count > 0) {
            var cam = Camera.main;
            if (cam != null) {
                Vector3 camPos = cam.transform.position;
                foreach (var (t, localDir) in dimensionLabels) {
                    if (!t) continue;
                    // Stays visually attached to its own line: reading direction (local right) is locked to the
                    // line's own axis, only the perpendicular-to-that-axis component of "towards the camera" is
                    // free, so it only ever flips 180 degrees around the line rather than spinning freely.
                    Vector3 right = root.transform.TransformDirection(localDir).normalized;
                    Vector3 forward = Vector3.ProjectOnPlane(camPos - t.position, right);
                    if (forward.sqrMagnitude < 1e-6f) forward = Vector3.ProjectOnPlane(Vector3.up, right);
                    if (forward.sqrMagnitude < 1e-6f) continue; // camera exactly on the line's own axis - keep last frame's rotation
                    forward.Normalize();
                    Vector3 up = Vector3.Cross(forward, right);
                    t.rotation = Quaternion.LookRotation(forward, up);
                }
            }
        }

        if (!rightHand) {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            rightHand = rig ? rig.rightHandAnchor : null;
            if (!rightHand) return;
        }
        var hand = rightHand;
        bool grip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        Vector2 s = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        if (!grabbed) {
            if (grip && Physics.Raycast(hand.position, hand.forward, out RaycastHit hit) && hit.collider.gameObject == root) {
                grabbed = true;
                OVRInput.SetControllerVibration(0.1f, 0.1f, OVRInput.Controller.RTouch); Invoke(nameof(StopVib), 0.05f);
                off = hand.InverseTransformPoint(root.transform.position);
                rotOff = Quaternion.Inverse(hand.rotation) * root.transform.rotation;
            }
        } else {
            if (!grip) { grabbed = false; return; }
            root.transform.position = hand.TransformPoint(off);
            root.transform.rotation = hand.rotation * rotOff;
            if (Mathf.Abs(s.x) > 0.1f) { root.transform.Rotate(Vector3.up, -s.x * 120f * Time.deltaTime, Space.World); rotOff = Quaternion.Inverse(hand.rotation) * root.transform.rotation; }
            if (Mathf.Abs(s.y) > 0.1f) { scale = Mathf.Clamp(scale + s.y * 0.5f * Time.deltaTime, 0.005f, 1.0f); root.transform.localScale = Vector3.one * scale; }
        }
    }
    private void StopVib() => OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);
}