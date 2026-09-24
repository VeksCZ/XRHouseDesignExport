using System.Collections.Generic;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Highlights the scanned room geometry directly over passthrough as glowing edge lines - the rectangle of
/// every wall, door and window, and the real (possibly L-shaped) perimeter of every floor and ceiling - similar
/// to Quest's own Room Setup preview. Applied to every room MRUKDataProcessor.GetValidRooms() considers real
/// (the same set the dollhouse and export use), so walking from an already-scanned room into another
/// already-scanned one keeps both visible rather than only "the current room".
///
/// An earlier version used MRUK's EffectMesh to fill each surface with a translucent solid colour instead of
/// drawing its edges. Standing inside a room means being surrounded by walls, floor and ceiling on every side,
/// so even a faint fill over all of them at once reads as a uniform haze rather than a highlight - lines drawn
/// only along the real edges avoid that entirely, since there's no fill left to haze anything.
/// </summary>
public class LiveScanOverlay : MonoBehaviour
{
    public XRMenu uiLog;
    bool isOn;
    public bool IsOn => isOn;

#if META_XR_SDK_INSTALLED
    const float LineWidth = 0.012f;
    readonly List<GameObject> lines = new List<GameObject>();
    // Same colours as the 3D model tiers (XRModelFactory.GetColorForMaterial) and the floor plan (door/window
    // stroke colours), so a door or window reads the same way in every view this app has.
    static readonly Color WallColor = new Color(0.35f, 0.9f, 1f, 1f);
    static readonly Color DoorColor = new Color(0.85f, 0.55f, 0.25f, 1f);
    static readonly Color WindowColor = new Color(0.25f, 0.60f, 0.92f, 1f);
    readonly Dictionary<Color, Material> lineMats = new Dictionary<Color, Material>();

    Material GetLineMaterial(Color color)
    {
        if (lineMats.TryGetValue(color, out var mat)) return mat;
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        mat = new Material(shader) { name = "LiveScanOverlayLine", color = color };
        if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        lineMats[color] = mat;
        return mat;
    }

    void AddLine(Vector3 a, Vector3 b, Color color)
    {
        var go = new GameObject("ScanOverlayEdge");
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.material = GetLineMaterial(color);
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.SetPosition(0, a);
        lr.SetPosition(1, b);
        lr.startWidth = lr.endWidth = LineWidth;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lines.Add(go);
    }

    /// <summary>Traces a closed polygon (a room's real floor/ceiling footprint, which can be L-shaped) given in
    /// the anchor's own local XY plane.</summary>
    void AddPolygonEdges(Vector3 origin, Quaternion rot, IReadOnlyList<Vector2> boundary, Color color)
    {
        if (boundary == null || boundary.Count < 2) return;
        for (int i = 0; i < boundary.Count; i++)
        {
            Vector2 p0 = boundary[i], p1 = boundary[(i + 1) % boundary.Count];
            AddLine(origin + rot * new Vector3(p0.x, p0.y, 0), origin + rot * new Vector3(p1.x, p1.y, 0), color);
        }
    }

    /// <summary>The 4 edges of a wall/door/window's own rectangle (MRUKAnchor.PlaneRect), centred on the anchor.</summary>
    void AddRectEdges(Vector3 center, Quaternion rot, float w, float h, Color color)
    {
        Vector3 p00 = center + rot * new Vector3(-w / 2f, -h / 2f, 0);
        Vector3 p10 = center + rot * new Vector3(w / 2f, -h / 2f, 0);
        Vector3 p11 = center + rot * new Vector3(w / 2f, h / 2f, 0);
        Vector3 p01 = center + rot * new Vector3(-w / 2f, h / 2f, 0);
        AddLine(p00, p10, color); AddLine(p10, p11, color); AddLine(p11, p01, color); AddLine(p01, p00, color);
    }

    public bool ToggleOnOff()
    {
        isOn = !isOn;
        if (isOn) Show(); else Hide();
        return isOn;
    }

    /// <summary>Rebuilds against whatever MRUK currently holds (e.g. after the active scan changed) - the
    /// overlay's own anchors no longer exist once the scan underneath it changes, so there's nothing sensible
    /// to keep showing the way the Dollhouse does; this either shows the new scan or leaves itself off.</summary>
    public bool RebuildInPlace()
    {
        if (!isOn) return false;
        Hide();
        Show();
        return isOn;
    }

    /// <summary>Turns the overlay off without flipping isOn like ToggleOnOff would if it were already off.</summary>
    public void ForceOff()
    {
        if (!isOn) return;
        isOn = false;
        Hide();
    }

    void Show()
    {
        Hide();
        if (MRUK.Instance == null) { uiLog?.AddLog("<color=red>Scan overlay: no MRUK instance.</color>"); isOn = false; return; }
        var rooms = MRUKDataProcessor.GetValidRooms(MRUK.Instance);
        if (rooms.Count == 0) { uiLog?.AddLog("<color=red>Scan overlay: no valid rooms.</color>"); isOn = false; return; }

        foreach (var room in rooms)
        {
            foreach (var a in room.Anchors)
            {
                if (a == null) continue;
                if (MRUKDataProcessor.IsStructuralWall(a) && a.PlaneRect.HasValue)
                    AddRectEdges(a.transform.position, a.transform.rotation, a.PlaneRect.Value.width, a.PlaneRect.Value.height, WallColor);
                else if (MRUKDataProcessor.IsDoor(a) && a.PlaneRect.HasValue)
                    AddRectEdges(a.transform.position, a.transform.rotation, a.PlaneRect.Value.width, a.PlaneRect.Value.height, DoorColor);
                else if (MRUKDataProcessor.IsWindow(a) && a.PlaneRect.HasValue)
                    AddRectEdges(a.transform.position, a.transform.rotation, a.PlaneRect.Value.width, a.PlaneRect.Value.height, WindowColor);
                else if ((a.Label == MRUKAnchor.SceneLabels.FLOOR || a.Label == MRUKAnchor.SceneLabels.CEILING) && a.PlaneBoundary2D != null)
                    AddPolygonEdges(a.transform.position, a.transform.rotation, a.PlaneBoundary2D, WallColor);
            }
        }
        uiLog?.AddLog($"<color=green>Scan overlay ON ({lines.Count} edges)</color>");
    }

    void Hide()
    {
        foreach (var go in lines) if (go) Destroy(go);
        lines.Clear();
    }
#else
    public bool ToggleOnOff() { isOn = !isOn; return isOn; }
    public bool RebuildInPlace() => false;
    public void ForceOff() { isOn = false; }
#endif
}
