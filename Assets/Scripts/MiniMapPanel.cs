using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// A small preview of the scanned house with an avatar for your current position and facing - similar to the
/// little preview Quest's own Room Setup shows while you fix up a room scan. Like the Dollhouse, it spawns once
/// in front of you and then just sits there in world space until you grab and drag it aside - an earlier version
/// instead rigidly followed your head every frame, which read as "attached to your view" rather than a real
/// object you could glance at or move out of the way.
/// </summary>
public class MiniMapPanel : MonoBehaviour
{
    public XRMenu uiLog;
    const float Scale = 0.02f;         // metres per model unit - much smaller than the Dollhouse's own default 0.05
    const float SpawnDistance = 0.7f;
    const float SpawnAngleLeft = 30f;   // degrees left of where you're looking
    const float TiltDeg = 50f;          // tips the model's top towards you, like a tabletop display you look down onto
    // The avatar is built in the SAME model space as the house (real metres, pre-Scale) so it shrinks exactly
    // proportionally with it, like a little figure standing in a little house - no separate "real-world size"
    // compensation needed (an earlier version sized the marker to stay a fixed real-world 12cm regardless of
    // map scale, which on a shrunk map made it look like a boulder crushing the house).
    const float AvatarHeight = 1.7f, AvatarWidth = 0.4f;

    GameObject root;
    GameObject avatarBody;
    Transform rightHand;
    bool grabbed;
    Vector3 grabOff;
    Quaternion grabRotOff;
    Vector3 modelCenter;
    float modelYaw;
    bool isOn;

    public bool IsOn => isOn;
    public bool IsDragging => grabbed;

    public bool ToggleOnOff()
    {
        isOn = !isOn;
        if (isOn) Build(); else Cleanup();
        return isOn;
    }

    /// <summary>Rebuilds the model against whatever MRUK currently holds (e.g. after the active scan changed) -
    /// the old model's anchors are gone once the scan underneath it changes, so this either shows the new scan
    /// or leaves the minimap off (Build() itself turns isOn off if the new scan has no valid rooms). Keeps
    /// wherever it was placed, same as the Dollhouse.</summary>
    public bool RebuildInPlace()
    {
        if (!isOn) return false;
        Build();
        return isOn;
    }

    /// <summary>Turns the minimap off without flipping isOn like ToggleOnOff would if it were already off.</summary>
    public void ForceOff()
    {
        if (!isOn) return;
        isOn = false;
        Cleanup();
    }

    void Build()
    {
        // Keep wherever it was placed (e.g. after a scan change) instead of re-spawning in front of the camera
        // every time - only a fresh activation (root was null/destroyed) picks a new spot.
        Vector3? keepPos = null; Quaternion keepRot = Quaternion.identity;
        if (root) { keepPos = root.transform.position; keepRot = root.transform.rotation; }
        Cleanup();
#if META_XR_SDK_INSTALLED
        if (MRUK.Instance == null) { uiLog?.AddLog("<color=red>Minimap: no MRUK instance.</color>"); isOn = false; return; }
        var rooms = MRUKDataProcessor.GetValidRooms(MRUK.Instance);
        if (rooms.Count == 0) { uiLog?.AddLog("<color=red>Minimap: no valid rooms.</color>"); isOn = false; return; }

        var floorAnchors = rooms.SelectMany(r => r.FloorAnchors).Where(f => f != null).ToList();
        modelCenter = floorAnchors.Count > 0
            ? floorAnchors.Aggregate(Vector3.zero, (s, f) => s + f.transform.position) / floorAnchors.Count
            : rooms[0].transform.position;
        modelYaw = FloorPlanBuilder.CorrectionYaw(MRUKPlanExtractor.Extract(rooms));

        var model = XRModelFactory.CreateAnchorAnalytical(rooms, modelYaw, modelCenter);
        var visual = UnityModelLoader.LoadToScene(model);

        root = new GameObject("MiniMapRoot");
        var cam = Camera.main;
        if (keepPos.HasValue) root.transform.SetPositionAndRotation(keepPos.Value, keepRot);
        else if (cam != null)
        {
            // At eye level, 30 degrees to the left of wherever you're looking, tilted so you look down onto
            // its top like a tabletop model instead of straight at a vertical wall of it.
            Quaternion camYaw = Quaternion.Euler(0, cam.transform.eulerAngles.y, 0);
            Vector3 dirToSpawn = camYaw * Quaternion.Euler(0, -SpawnAngleLeft, 0) * Vector3.forward;
            Vector3 spawnPos = cam.transform.position + dirToSpawn * SpawnDistance;
            // Rotating "up" by +TiltDeg around the (now-yawed) right axis tips the model's top towards
            // "forward", which LookRotation just pointed at the viewer - a negative angle here was tipping the
            // open top away from the viewer instead, showing its underside.
            Quaternion faceViewer = Quaternion.LookRotation(-dirToSpawn, Vector3.up);
            Quaternion tilt = Quaternion.AngleAxis(TiltDeg, faceViewer * Vector3.right);
            root.transform.SetPositionAndRotation(spawnPos, tilt * faceViewer);
        }
        root.transform.localScale = Vector3.one * Scale;

        if (visual != null) visual.transform.SetParent(root.transform, false);
        AddCol(visual);
        // Registers it with the XR Interaction Toolkit purely so the ray hovers it as a valid target (turns
        // green and clips at its surface - see XRMenu.StyleRay); the actual grab is still the same custom
        // grip+raycast logic in Update() below.
        root.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();

        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        var avatarMat = new Material(shader) { color = new Color(1f, 0.25f, 0.25f, 1f) };

        // A simple hint of a standing person - a capsule body plus a small "nose" so which way you're facing
        // is visible too, not just where you are.
        avatarBody = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        avatarBody.name = "MiniMapAvatarBody";
        Destroy(avatarBody.GetComponent<Collider>());
        avatarBody.transform.SetParent(root.transform, false);
        avatarBody.transform.localScale = new Vector3(AvatarWidth, AvatarHeight / 2f, AvatarWidth); // capsule primitive is 2 units tall
        avatarBody.GetComponent<MeshRenderer>().material = avatarMat;

        // Unity has no built-in cone primitive - a small flattened cube stands in for a "nose" just as well.
        var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
        nose.name = "MiniMapAvatarNose";
        Destroy(nose.GetComponent<Collider>());
        nose.transform.SetParent(avatarBody.transform, false);
        // In the body's own local space (stretched by AvatarWidth/AvatarHeight above) - counter-scale so the
        // nose stays a small, undistorted nub regardless of the body's own scale.
        nose.transform.localPosition = new Vector3(0, 0.3f, 0.55f);
        nose.transform.localScale = new Vector3(0.35f, 0.35f, 0.7f);
        nose.GetComponent<MeshRenderer>().material = avatarMat;

        uiLog?.AddLog("<color=green>Minimap ON</color>");
#else
        isOn = false;
#endif
    }

    void AddCol(GameObject visual)
    {
        if (visual == null) return;
        Bounds b = new Bounds(visual.transform.position, Vector3.zero);
        var rs = visual.GetComponentsInChildren<Renderer>();
        foreach (var r in rs) b.Encapsulate(r.bounds);
        if (rs.Length == 0) return;
        var col = root.AddComponent<BoxCollider>();
        col.center = root.transform.InverseTransformPoint(b.center);
        col.size = b.size / root.transform.lossyScale.x;
    }

    void Cleanup()
    {
        if (root)
        {
            // GameObject.CreatePrimitive's mesh is one of Unity's built-in SHARED engine assets (the same
            // "Capsule"/"Cube" mesh used by every such primitive anywhere in the app) - destroying it here, as
            // an earlier version did indiscriminately for every mesh under root, broke the avatar on every
            // toggle after the first (it silently lost its mesh). Only the model's own generated meshes, and
            // every material here (all uniquely created, never shared), are safe to destroy.
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
                if (mf.sharedMesh && mf.gameObject != avatarBody && (avatarBody == null || mf.transform.parent != avatarBody.transform))
                    Destroy(mf.sharedMesh);
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>()) if (mr.sharedMaterial) Destroy(mr.sharedMaterial);
            Destroy(root);
        }
        root = null; avatarBody = null;
    }

    void Update()
    {
        if (!isOn || !root) return;
        var cam = Camera.main;
        if (!cam) return;

        if (!rightHand)
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            rightHand = rig ? rig.rightHandAnchor : null;
        }
        if (rightHand)
        {
            bool grip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            if (!grabbed)
            {
                if (grip && Physics.Raycast(rightHand.position, rightHand.forward, out RaycastHit hit) && hit.collider.gameObject == root)
                {
                    grabbed = true;
                    OVRInput.SetControllerVibration(0.1f, 0.1f, OVRInput.Controller.RTouch); Invoke(nameof(StopVib), 0.05f);
                    grabOff = rightHand.InverseTransformPoint(root.transform.position);
                    grabRotOff = Quaternion.Inverse(rightHand.rotation) * root.transform.rotation;
                }
            }
            else if (!grip) grabbed = false;
            else
            {
                root.transform.position = rightHand.TransformPoint(grabOff);
                root.transform.rotation = rightHand.rotation * grabRotOff;
            }
        }

        if (avatarBody)
        {
            // Same center/yaw the model's own vertices were built with (XRModelFactory), so the avatar's X/Z
            // line up with the shrunk house exactly as the real headset position relates to the real house -
            // independent of wherever root itself has been dragged to or how it's currently oriented. Its
            // height is grounded to the model's own floor (modelCenter.y, the average of the real floor
            // anchors' own height - unaffected by gRot, a yaw-only rotation) rather than derived from the
            // camera's height, so its feet land on the floor instead of hanging down from head height.
            Quaternion gRot = Quaternion.Euler(0, modelYaw, 0);
            Vector3 localCam = gRot * (cam.transform.position - modelCenter);
            avatarBody.transform.localPosition = new Vector3(localCam.x, AvatarHeight / 2f, localCam.z);
            Vector3 facing = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (facing.sqrMagnitude > 0.0001f) avatarBody.transform.localRotation = Quaternion.LookRotation(gRot * facing.normalized, Vector3.up);
        }
    }

    void StopVib() => OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);
}
