using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Edit mode "Hole" tool: draws an opening (the stairwell) into a floor/ceiling as a rectangle by two opposite
/// corners - from below on the ceiling, or from above on the upper floor. The rectangle is square to the house's
/// walls and its corners snap onto walls they come close to (a stairwell is usually bounded by walls). It cuts the
/// ceiling of the story below and the floor of the story above at once. Trigger on an existing hole selects it.
/// </summary>
public class HoleEditTool : MonoBehaviour
{
    public XRMenu uiLog;
    public EditModeController editMode;
    public WalkThroughMode walk;
    public DollHouseVisualizer dollhouse;

    enum State { Idle, CornerA, CornerB }
    State state = State.Idle;
    bool wasActive;

    static readonly Color GhostColor = new Color(1f, 0.9f, 0.2f, 0.95f);
    static readonly Color SelectColor = new Color(1f, 0.55f, 0.1f, 1f);
    static readonly Color HoleColor = new Color(0.3f, 0.95f, 0.45f, 0.95f);

    Transform frameRoot; float frameYaw; Vector3 frameCenter; bool frameIsWalk;
    List<MRUKRoom> rooms = new List<MRUKRoom>();
    WallSnap walls;
    Transform head, rightHand;

    Vector3 cornerA;
    float probe;            // +0.5 drawn on a floor, -0.5 on a ceiling (for wall snapping)
    Vector3? hover;
    string selectedId;

    readonly List<LineRenderer> pool = new List<LineRenderer>();
    int used;
    Material lineMat;
    readonly List<Vector3> line = new List<Vector3>();

    Canvas panel;
    PanelDrag panelDrag;
    Vector3 panelPos; Quaternion panelRot; bool panelPlaced, userPlaced;

    void Start()
    {
        if (editMode != null) editMode.TriggerPressed += OnTrigger;
        HouseEditsStore.Changed += OnEditsChanged;
    }

    void OnDestroy()
    {
        if (editMode != null) editMode.TriggerPressed -= OnTrigger;
        HouseEditsStore.Changed -= OnEditsChanged;
        ClosePanel();
        foreach (var lr in pool) if (lr) Destroy(lr.gameObject);
        if (lineMat) Destroy(lineMat);
    }

    bool Active => editMode != null && editMode.IsOn && editMode.CurrentTool == EditModeController.Tool.Holes;

    void Update()
    {
        used = 0;
        bool active = Active && ResolveFrame();
        if (!active)
        {
            if (wasActive) { ClosePanel(); if (!userPlaced) panelPlaced = false; state = State.Idle; }
            wasActive = false;
            FinishLines();
            return;
        }
        if (!wasActive)
        {
            wasActive = true;
            state = HouseEditsStore.Current.holes.Count == 0 ? State.CornerA : State.Idle;
            RebuildPanel();
        }

        hover = PointedSurface(out float hp);
        if (hover.HasValue)
        {
            if (state == State.CornerB) hp = probe;
            hover = walls.SnapPoint(hover.Value, 0.25f, hp);
            if (state == State.CornerA) probe = hp;
        }

        foreach (var h in HouseEditsStore.Current.holes)
            if (StairGeometry.HoleWorld(h, rooms, out var a, out var b)) DrawRect(a, b, h.id == selectedId ? SelectColor : HoleColor);
        if (state == State.CornerA && hover.HasValue) Cross(hover.Value);
        if (state == State.CornerB)
        {
            Cross(cornerA);
            if (hover.HasValue) DrawRect(cornerA, new Vector3(hover.Value.x, cornerA.y, hover.Value.z), GhostColor);
        }
        FinishLines();
    }

    // ---------- frame & picking ----------

    bool ResolveFrame()
    {
        Transform r; float y; Vector3 c; bool isWalk = false;
        if (walk != null && walk.TryGetModelFrame(out r, out y, out c)) isWalk = true;
        else if (dollhouse == null || !dollhouse.TryGetModelFrame(out r, out y, out c)) return false;
        if (r != frameRoot)
        {
            frameRoot = r;
#if META_XR_SDK_INSTALLED
            rooms = MRUK.Instance != null ? MRUKDataProcessor.GetValidRooms(MRUK.Instance) : new List<MRUKRoom>();
#endif
            walls = new WallSnap(rooms);
        }
        frameYaw = y; frameCenter = c; frameIsWalk = isWalk;
        if (!head || !rightHand)
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            head = rig ? rig.centerEyeAnchor : null;
            rightHand = rig ? rig.rightHandAnchor : null;
        }
        return frameRoot && head && rightHand;
    }

    Quaternion G => Quaternion.Euler(0, frameYaw, 0);
    Vector3 ScanToWorld(Vector3 p) => frameRoot.TransformPoint(G * (p - frameCenter));
    Vector3 WorldToScan(Vector3 p) => Quaternion.Inverse(G) * frameRoot.InverseTransformPoint(p) + frameCenter;

    /// <summary>A floor (from above) or ceiling (from below) point under the ray, scan frame. probeHeight tells the
    /// wall snapping which way the walls go from it.</summary>
    Vector3? PointedSurface(out float probeHeight)
    {
        probeHeight = 0.5f;
        if (frameIsWalk)
        {
            if (walk.RaycastModel(new Ray(rightHand.position, rightHand.forward), out var hit) && Mathf.Abs(hit.normal.y) > 0.7f)
            {
                probeHeight = hit.normal.y > 0 ? 0.5f : -0.5f;
                return WorldToScan(hit.point);
            }
            return null;
        }
        if (dollhouse.TryGetPointedFloor(out var modelPoint)) return Quaternion.Inverse(G) * modelPoint + frameCenter;
        return null;
    }

    void OnTrigger()
    {
        if (!Active) return;
        if (!wasActive) { uiLog?.AddLog("Hole: open the Dollhouse or Walk first."); return; }
        if (state == State.CornerA)
        {
            if (!hover.HasValue) { uiLog?.AddLog("Hole: point at the ceiling (from below) or the floor (from above)."); return; }
            cornerA = hover.Value;
            state = State.CornerB;
            RebuildPanel();
            return;
        }
        if (state == State.CornerB)
        {
            if (!hover.HasValue) return;
            Vector3 b = new Vector3(hover.Value.x, cornerA.y, hover.Value.z);
            Vector3 ma = G * (cornerA - frameCenter), mb = G * (b - frameCenter);
            if (Mathf.Abs(ma.x - mb.x) < 0.3f || Mathf.Abs(ma.z - mb.z) < 0.3f) { uiLog?.AddLog("Hole: too narrow."); return; }
            Commit(cornerA, b);
            return;
        }
        // Idle: select the hole under the ray.
        if (!hover.HasValue) return;
        foreach (var h in HouseEditsStore.Current.holes)
        {
            if (!StairGeometry.HoleWorld(h, rooms, out var a, out var bb)) continue;
            if (Mathf.Abs(hover.Value.y - a.y) > 0.8f) continue;
            Vector3 m = G * (hover.Value - frameCenter), ma = G * (a - frameCenter), mb = G * (bb - frameCenter);
            if (m.x >= Mathf.Min(ma.x, mb.x) && m.x <= Mathf.Max(ma.x, mb.x) && m.z >= Mathf.Min(ma.z, mb.z) && m.z <= Mathf.Max(ma.z, mb.z))
            {
                selectedId = h.id;
                RebuildPanel();
                return;
            }
        }
    }

    async void Commit(Vector3 a, Vector3 b)
    {
        // The room the corner lies in (a ceiling point is ~2.5 m above its room's floor - FindRoomAt allows that).
        var room = DoorCatalog.FindRoomAt(rooms, a - Vector3.up * 0.05f) ?? DoorCatalog.FindRoomAt(rooms, a + Vector3.up * 0.05f);
        var h = new HoleEdit
        {
            id = Guid.NewGuid().ToString("N"),
            roomUuid = DoorCatalog.RoomId(room),
            localA = new Vector3Data(room != null ? room.transform.InverseTransformPoint(a) : a),
            localB = new Vector3Data(room != null ? room.transform.InverseTransformPoint(b) : b),
        };
        if (await editMode.Commit(e => e.holes.Add(h)))
        {
            selectedId = h.id;
            state = State.Idle;
            uiLog?.AddLog("Hole added.");
            RebuildPanel();
        }
    }

    void OnEditsChanged()
    {
        if (!wasActive) return;
        if (selectedId != null && HouseEditsStore.Current.holes.All(h => h.id != selectedId)) selectedId = null;
        RebuildPanel();
    }

    // ---------- drawing ----------

    /// <summary>The rectangle spanned by a and b, square to the house's walls (model axes), at a's height.</summary>
    void DrawRect(Vector3 a, Vector3 b, Color c)
    {
        Vector3 ma = G * (a - frameCenter), mb = G * (b - frameCenter);
        Quaternion gi = Quaternion.Inverse(G);
        Vector3 P(float x, float z) => gi * new Vector3(x, ma.y, z) + frameCenter;
        line.Clear();
        line.Add(P(ma.x, ma.z)); line.Add(P(mb.x, ma.z)); line.Add(P(mb.x, mb.z)); line.Add(P(ma.x, mb.z));
        Emit(c, true);
    }

    void Cross(Vector3 p)
    {
        line.Clear(); line.Add(p - Vector3.right * 0.15f); line.Add(p + Vector3.right * 0.15f); Emit(GhostColor, false);
        line.Clear(); line.Add(p - Vector3.forward * 0.15f); line.Add(p + Vector3.forward * 0.15f); Emit(GhostColor, false);
    }

    void Emit(Color c, bool loop)
    {
        if (used >= pool.Count) pool.Add(NewLine());
        var lr = pool[used++];
        lr.gameObject.SetActive(true);
        lr.loop = loop;
        lr.positionCount = line.Count;
        // Lifted a little off the surface towards you, so it's never hidden inside the floor/ceiling.
        Vector3 toEye = (head.position - ScanToWorld(line[0])).normalized * 0.02f;
        for (int i = 0; i < line.Count; i++) lr.SetPosition(i, ScanToWorld(line[i]) + toEye);
        lr.startColor = lr.endColor = c;
        lr.widthMultiplier = Mathf.Max(0.0025f, 0.015f * frameRoot.lossyScale.x);
    }

    LineRenderer NewLine()
    {
        if (!lineMat) lineMat = new Material(Shader.Find("Sprites/Default"));
        var go = new GameObject("HoleEditLine");
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = lineMat;
        lr.useWorldSpace = true;
        lr.numCapVertices = 2;
        return lr;
    }

    void FinishLines()
    {
        for (int i = used; i < pool.Count; i++) if (pool[i] && pool[i].gameObject.activeSelf) pool[i].gameObject.SetActive(false);
    }

    // ---------- panel ----------

    const float PW = 600f, PH = 260f, PScale = 0.0006f, RowH = 62f, Gap = 8f;

    void ClosePanel()
    {
        if (panel && panelDrag && panelDrag.Moved) userPlaced = true;
        if (panel) { panelPos = panel.transform.position; panelRot = panel.transform.rotation; panelPlaced = true; }
        if (panel) Destroy(panel.gameObject);
        panel = null;
    }

    void RebuildPanel()
    {
        if (!wasActive || !head) return;
        ClosePanel();
        panel = XRUi.CreateWorldCanvas("HoleEditPanel", new Vector2(PW, PH), PScale);
        var t = panel.transform;
        if (!panelPlaced)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            panelPos = head.position + fwd * 0.6f - Vector3.up * 0.18f + Quaternion.Euler(0, 90, 0) * fwd * 0.25f;
            panelRot = Quaternion.LookRotation(panelPos - head.position, Vector3.up);
            panelPlaced = true;
        }
        t.SetPositionAndRotation(panelPos, panelRot);
        panelDrag = PanelDrag.Attach(panel, PW, PH);
        XRUi.CreatePanel(t, "Background", XRUi.PanelColor, 0, 0, PW, PH);

        float x0 = 20, fullW = PW - 40, y = 8;
        string title = state switch
        {
            State.CornerA => "New opening - first corner",
            State.CornerB => "New opening - opposite corner",
            _ => $"Openings: {HouseEditsStore.Current.holes.Count}" + (selectedId != null ? "  (one selected)" : ""),
        };
        string hint = state switch
        {
            State.CornerA => "Trigger on the ceiling from below, or on the upper floor from above. Corners snap onto nearby walls.",
            State.CornerB => "Trigger at the opposite corner. The rectangle is square to the walls and cuts ceiling and floor.",
            _ => "Trigger on an opening to select it.",
        };
        XRUi.CreateText(t, "Title", title, 22, TextAlignmentOptions.MidlineLeft, x0, y, fullW, 44, XRUi.TextColor, FontStyles.Bold);
        y += 50;
        XRUi.CreateText(t, "Hint", hint, 18, TextAlignmentOptions.TopLeft, x0, y, fullW, 64, XRUi.MutedText);
        y += 70 + Gap;
        float w3 = (fullW - Gap * 2) / 3f;
        if (state == State.Idle)
        {
            XRUi.CreateButton(t, "New opening", x0, y, w3, RowH, () => { state = State.CornerA; RebuildPanel(); }, 20);
            var del = XRUi.CreateButton(t, "Delete", x0 + w3 + Gap, y, w3, RowH, Delete, 20);
            XRUi.SetTint(del, selectedId != null ? XRUi.ButtonDangerColor : XRUi.ButtonDisabled);
        }
        else XRUi.CreateButton(t, "Cancel", x0, y, w3 * 2 + Gap, RowH, () => { state = State.Idle; RebuildPanel(); }, 20);
        XRUi.CreateButton(t, "Close", x0 + (w3 + Gap) * 2, y, w3, RowH, () => { state = State.Idle; ClosePanel(); }, 20);
    }

    async void Delete()
    {
        if (selectedId == null) return;
        string id = selectedId;
        await editMode.Commit(e => e.holes.RemoveAll(h => h.id == id));
    }
}
