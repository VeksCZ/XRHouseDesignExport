using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
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
    private DollhouseMode mode = DollhouseMode.Off;
    private bool grabbed = false;
    private Vector3 off;
    private Quaternion rotOff;
    private enum DollhouseMode { Off, AnchorAnalytical, MeshAnalytical, RawMesh }

    public bool Toggle() { mode = (DollhouseMode)(((int)mode+1)%4); Refresh(); return mode != DollhouseMode.Off; }
    public bool IsDragging => grabbed;

    private async void Refresh() {
        Cleanup();
        if (mode == DollhouseMode.Off) return;

        var camera = Camera.main;
        if (camera == null) { uiLog?.AddLog("<color=red>Dollhouse: no main camera</color>"); return; }

        uiLog?.AddLog($"Dollhouse Refresh: {mode}");
        camera.nearClipPlane = 0.001f;
        root = new GameObject("DollhouseRoot");
        var cam = camera.transform;
        root.transform.position = cam.position + cam.forward * spawnDistance;
        root.transform.rotation = Quaternion.Euler(0, cam.eulerAngles.y + 180, 0);
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
            if (mode == DollhouseMode.AnchorAnalytical) m = XRModelFactory.CreateAnchorAnalytical(rooms, yaw, c);
            else if (mode == DollhouseMode.MeshAnalytical) m = await XRModelFactory.CreateMeshAnalytical(rooms, yaw, c);
            else if (mode == DollhouseMode.RawMesh) m = await XRModelFactory.CreateRawScan(rooms, yaw, c);

            if (m != null) {
                var visual = UnityModelLoader.LoadToScene(m);
                if (visual != null) { 
                    visual.transform.SetParent(root.transform, false); 
                    AddCol(visual); 
                    uiLog?.AddLog("<color=green>Dollhouse: Model Loaded</color>");
                }
            }
        } catch (Exception ex) {
            uiLog?.AddLog($"<color=red>Dollhouse Error: {ex.Message}</color>");
            Debug.LogException(ex);
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
            foreach(var mr in root.GetComponentsInChildren<MeshRenderer>()) if(mr.sharedMaterial) Destroy(mr.sharedMaterial);
            Destroy(root);
        }
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
        if (!root || mode == DollhouseMode.Off) return;
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