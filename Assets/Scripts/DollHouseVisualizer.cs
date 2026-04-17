using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
using Meta.XR;
#endif

public class DollHouseVisualizer : MonoBehaviour
{
    [Header("Dollhouse Settings")]
    public float scale = 0.05f;
    public float spawnDistance = 1.0f;
    public float rotationSpeed = 120f;
    public float zoomSpeed = 0.5f;
    public XRMenu uiLog;

    private GameObject currentDollhouse;
    private DollhouseMode currentMode = DollhouseMode.Off;
    private bool isGrabbed = false;
    public bool IsDragging => isGrabbed;
    private Vector3 grabOffset;
    private Quaternion grabRotationOffset;
    private float originalNearClip = 0.1f;

    private enum DollhouseMode { Off, AnchorAnalytical, MeshAnalytical, RawMesh }

    public bool Toggle() {
        currentMode = (DollhouseMode)(((int)currentMode + 1) % 4);
        RefreshDollhouse();
        return currentMode != DollhouseMode.Off;
    }

    private async void RefreshDollhouse() {
        if (currentDollhouse != null) { Destroy(currentDollhouse); currentDollhouse = null; }
        if (originalNearClip > 0) Camera.main.nearClipPlane = originalNearClip;

        if (currentMode == DollhouseMode.Off) { uiLog?.AddLog("Dollhouse OFF."); return; }
        uiLog?.AddLog("Mode: " + currentMode);
        
        originalNearClip = Camera.main.nearClipPlane;
        Camera.main.nearClipPlane = 0.01f;

        currentDollhouse = new GameObject("Dollhouse_" + currentMode);
        Transform cam = Camera.main.transform;
        currentDollhouse.transform.position = cam.position + cam.forward * spawnDistance;
        currentDollhouse.transform.rotation = Quaternion.Euler(0, cam.eulerAngles.y + 180, 0);
        currentDollhouse.transform.localScale = Vector3.one * scale;

        Vector3 center = FindAnyObjectByType<MRUKExporter>()?.LastHouseCenter ?? Vector3.zero;
        if (currentMode == DollhouseMode.AnchorAnalytical) await BuildAnchorAnalytical(center);
        else if (currentMode == DollhouseMode.MeshAnalytical) await BuildMeshAnalytical(center);
        else if (currentMode == DollhouseMode.RawMesh) await BuildRawMesh(center);

        AddInteractionCollider();
        UpdateRayVisuals();
    }

    private async Task BuildAnchorAnalytical(Vector3 c) {
        await Task.Yield();
        string obj = MRUKModelExporter.GenerateAnchorAnalytical(MRUK.Instance.Rooms.ToList(), 0f, c);
        if (!string.IsNullOrEmpty(obj)) Load(obj);
    }
private async Task BuildMeshAnalytical(Vector3 c) {
        string obj = await MRUKModelExporter.GenerateCleanColoredAnalytical(0f, c);
        if (!string.IsNullOrEmpty(obj)) Load(obj);
    }
    private async Task BuildRawMesh(Vector3 c) {
        string obj = await MRUKModelExporter.GenerateRawHighFidelityMesh(0f, c);
        if (!string.IsNullOrEmpty(obj)) Load(obj);
    }

    private void Load(string data) {
        var go = OBJLoader.LoadFromString(data);
        if (go != null) go.transform.SetParent(currentDollhouse.transform, false);
    }

    private void AddInteractionCollider() {
        if (currentDollhouse == null) return;
        Bounds b = new Bounds(currentDollhouse.transform.position, Vector3.zero);
        var renders = currentDollhouse.GetComponentsInChildren<Renderer>();
        foreach (var r in renders) b.Encapsulate(r.bounds);
        if (renders.Length > 0) {
            var col = currentDollhouse.AddComponent<BoxCollider>();
            col.center = currentDollhouse.transform.InverseTransformPoint(b.center);
            col.size = b.size / currentDollhouse.transform.lossyScale.x;
        }
    }

    private void UpdateRayVisuals() {
        var line = FindObjectsByType<LineRenderer>(FindObjectsSortMode.None).FirstOrDefault(l => l.name.Contains("Ray"));
        if (line != null) { line.startWidth = 0.004f; line.endWidth = 0.001f; }
    }

    void Update() {
        if (currentDollhouse == null || currentMode == DollhouseMode.Off) return;
        Transform hand = GameObject.Find("RightHandAnchor")?.transform;
        if (hand == null) return;

        bool grip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        Vector2 stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);

        if (!isGrabbed) {
            if (grip && Physics.Raycast(hand.position, hand.forward, out RaycastHit hit)) {
                if (hit.collider.gameObject == currentDollhouse) {
                    isGrabbed = true;
                    grabOffset = hand.InverseTransformPoint(currentDollhouse.transform.position);
                    grabRotationOffset = Quaternion.Inverse(hand.rotation) * currentDollhouse.transform.rotation;
                    uiLog?.AddLog("Grabbed.");
                }
            }
        } else {
            if (!grip) { isGrabbed = false; uiLog?.AddLog("Released."); return; }
            currentDollhouse.transform.position = hand.TransformPoint(grabOffset);
            currentDollhouse.transform.rotation = hand.rotation * grabRotationOffset;

            if (Mathf.Abs(stick.x) > 0.1f) {
                currentDollhouse.transform.Rotate(Vector3.up, -stick.x * rotationSpeed * Time.deltaTime, Space.World);
                grabRotationOffset = Quaternion.Inverse(hand.rotation) * currentDollhouse.transform.rotation;
            }
            if (Mathf.Abs(stick.y) > 0.1f) {
                scale = Mathf.Clamp(scale + stick.y * zoomSpeed * Time.deltaTime, 0.005f, 1.0f);
                currentDollhouse.transform.localScale = Vector3.one * scale;
            }
        }
    }
}