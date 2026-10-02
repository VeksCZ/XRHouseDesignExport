using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Edit mode "Stairs" tool. Placing: point at the floor (walk-through or Dollhouse) and pull the trigger for the
/// bottom of the first step, the middle of every landing, and the top end of the last step, then Finish. Each new
/// segment snaps to the house's own wall directions, so turns come out as clean right angles. A live ghost shows
/// the walking line at its real heights. Afterwards a panel adjusts the width and the number of steps per flight
/// (the rise per step follows from the floor-to-floor height). Trigger on an existing stair selects it.
/// </summary>
public class StairEditTool : MonoBehaviour
{
    public XRMenu uiLog;
    public EditModeController editMode;
    public WalkThroughMode walk;
    public DollHouseVisualizer dollhouse;

    enum State { Idle, Placing, Adjusting }
    State state = State.Idle;
    bool wasActive;

    static readonly Color GhostColor = new Color(1f, 0.9f, 0.2f, 0.95f);
    static readonly Color SelectColor = new Color(1f, 0.55f, 0.1f, 1f);
    static readonly Color StairColor = new Color(0.3f, 0.95f, 0.45f, 0.95f);

    Transform frameRoot; float frameYaw; Vector3 frameCenter; bool frameIsWalk;
    List<MRUKRoom> rooms = new List<MRUKRoom>();
    Transform head, rightHand;

    readonly List<Vector3> placing = new List<Vector3>(); // scan world
    float placingWidth = 1.0f;
    Vector3? hoverPoint;                                   // snapped, scan world
    string selectedId;

    readonly List<LineRenderer> pool = new List<LineRenderer>();
    int used;
    Material lineMat;
    readonly List<Vector3> pts = new List<Vector3>();

    Canvas panel;
    Vector3 panelPos; Quaternion panelRot; bool panelPlaced;

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

    bool Active => editMode != null && editMode.IsOn && editMode.CurrentTool == EditModeController.Tool.Stairs;

    void Update()
    {
        used = 0;
        bool active = Active && ResolveFrame();
        if (!active)
        {
            if (wasActive) { ClosePanel(); panelPlaced = false; state = State.Idle; placing.Clear(); }
            wasActive = false;
            FinishLines();
            return;
        }
        if (!wasActive)
        {
            wasActive = true;
            var first = HouseEditsStore.Current.stairs.FirstOrDefault();
            if (first != null) { selectedId = first.id; state = State.Adjusting; }
            else { placing.Clear(); state = State.Placing; }
            RebuildPanel();
        }

        hoverPoint = PointedFloor();
        if (state == State.Placing && hoverPoint.HasValue && placing.Count > 0) hoverPoint = Snap(placing[placing.Count - 1], hoverPoint.Value);

        foreach (var s in HouseEditsStore.Current.stairs)
        {
            var r = StairGeometry.Resolve(s, rooms);
            if (r != null) DrawResolved(r, s.id == selectedId && state == State.Adjusting ? SelectColor : StairColor);
        }
        if (state == State.Placing) DrawGhost();
        FinishLines();
    }

    // ---------- frame & picking (same mapping as DoorEditTool) ----------

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

    /// <summary>Floor point under the right controller's ray, in the scan frame.</summary>
    Vector3? PointedFloor()
    {
        if (frameIsWalk)
        {
            if (walk.RaycastModel(new Ray(rightHand.position, rightHand.forward), out var hit) && hit.normal.y > 0.7f)
                return WorldToScan(hit.point);
            return null;
        }
        if (dollhouse.TryGetPointedFloor(out var modelPoint)) return Quaternion.Inverse(G) * modelPoint + frameCenter;
        return null;
    }

    /// <summary>New segment from 'prev' snapped to the house's wall directions (model axes); kept at prev's height.</summary>
    Vector3 Snap(Vector3 prev, Vector3 p)
    {
        Vector3 d = G * (p - prev);
        if (Mathf.Abs(d.x) >= Mathf.Abs(d.z)) d.z = 0f; else d.x = 0f;
        d.y = 0f;
        return prev + Quaternion.Inverse(G) * d;
    }

    void OnTrigger()
    {
        if (!Active) return;
        if (state == State.Placing)
        {
            if (!hoverPoint.HasValue) { uiLog?.AddLog("Stairs: point at the floor."); return; }
            Vector3 p = hoverPoint.Value;
            if (placing.Count > 0) p.y = placing[0].y;
            if (placing.Count > 0 && Vector3.Distance(p, placing[placing.Count - 1]) < 0.2f) return;
            placing.Add(p);
            RebuildPanel();
            return;
        }
        // Idle/Adjusting: pick the stair whose footprint is under the ray.
        if (!hoverPoint.HasValue) return;
        foreach (var s in HouseEditsStore.Current.stairs)
        {
            var r = StairGeometry.Resolve(s, rooms);
            if (r != null && r.footprint.Any(rect => Inside(rect, hoverPoint.Value)))
            {
                selectedId = s.id; state = State.Adjusting; RebuildPanel();
                return;
            }
        }
    }

    static bool Inside(Vector3[] rect, Vector3 p)
    {
        bool inside = false;
        for (int i = 0, j = rect.Length - 1; i < rect.Length; j = i++)
        {
            Vector3 a = rect[i], b = rect[j];
            if ((a.z > p.z) != (b.z > p.z) && p.x < (b.x - a.x) * (p.z - a.z) / (b.z - a.z) + a.x) inside = !inside;
        }
        return inside;
    }

    void OnEditsChanged()
    {
        if (!wasActive) return;
        if (state == State.Adjusting && HouseEditsStore.Current.stairs.All(s => s.id != selectedId))
        {
            // Undone/deleted - fall back to the first remaining stair, or to placing a new one.
            var first = HouseEditsStore.Current.stairs.FirstOrDefault();
            if (first != null) selectedId = first.id; else { state = State.Placing; placing.Clear(); }
        }
        RebuildPanel();
    }

    // ---------- drawing ----------

    void DrawResolved(StairGeometry.Resolved r, Color c)
    {
        foreach (var (start, end, dir, risers, y0) in r.flights)
        {
            float y1 = y0 + risers * r.rise;
            Vector3 side = Vector3.Cross(Vector3.up, dir) * (r.width / 2f);
            pts.Clear();
            pts.Add(At(start - side, y0)); pts.Add(At(end - side, y1)); pts.Add(At(end + side, y1)); pts.Add(At(start + side, y0));
            Emit(c, true);
        }
        foreach (var (center, dir, y) in r.landings)
        {
            Vector3 f = dir * (r.width / 2f), side = Vector3.Cross(Vector3.up, dir) * (r.width / 2f);
            pts.Clear();
            pts.Add(At(center - f - side, y)); pts.Add(At(center + f - side, y)); pts.Add(At(center + f + side, y)); pts.Add(At(center - f + side, y));
            Emit(c, true);
        }
    }

    static Vector3 At(Vector3 p, float y) => new Vector3(p.x, y + 0.02f, p.z);

    void DrawGhost()
    {
        var all = new List<Vector3>(placing);
        if (hoverPoint.HasValue)
        {
            var h = hoverPoint.Value;
            if (all.Count > 0) h.y = all[0].y;
            all.Add(h);
        }
        if (all.Count == 1)
        {
            // Just the start: a small cross on the floor.
            Vector3 p = all[0];
            pts.Clear(); pts.Add(At(p - Vector3.right * 0.15f, p.y)); pts.Add(At(p + Vector3.right * 0.15f, p.y)); Emit(GhostColor, false);
            pts.Clear(); pts.Add(At(p - Vector3.forward * 0.15f, p.y)); pts.Add(At(p + Vector3.forward * 0.15f, p.y)); Emit(GhostColor, false);
            return;
        }
        if (all.Count < 2) return;
        var temp = new StairEdit { roomUuid = "", width = placingWidth, localPoints = all.Select(p => new Vector3Data(p)).ToList() };
        var r = StairGeometry.Resolve(temp, rooms);
        if (r != null) DrawResolved(r, GhostColor);
    }

    void Emit(Color c, bool loop)
    {
        if (used >= pool.Count) pool.Add(NewLine());
        var lr = pool[used++];
        lr.gameObject.SetActive(true);
        lr.loop = loop;
        lr.positionCount = pts.Count;
        for (int i = 0; i < pts.Count; i++) lr.SetPosition(i, ScanToWorld(pts[i]));
        lr.startColor = lr.endColor = c;
        lr.widthMultiplier = Mathf.Max(0.0025f, 0.015f * frameRoot.lossyScale.x);
    }

    LineRenderer NewLine()
    {
        if (!lineMat) lineMat = new Material(Shader.Find("Sprites/Default"));
        var go = new GameObject("StairEditLine");
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

    const float PW = 640f, PScale = 0.0006f, RowH = 62f, Gap = 8f;

    void ClosePanel()
    {
        if (panel) Destroy(panel.gameObject);
        panel = null;
    }

    void RebuildPanel()
    {
        if (!wasActive || !head) return;
        if (panel) { panelPos = panel.transform.position; panelRot = panel.transform.rotation; panelPlaced = true; }
        ClosePanel();
        if (state == State.Idle) return;

        StairEdit sel = state == State.Adjusting ? HouseEditsStore.Current.stairs.FirstOrDefault(s => s.id == selectedId) : null;
        if (state == State.Adjusting && sel == null) return;
        int flights = sel != null ? Mathf.Max(0, sel.localPoints.Count - 1) : 0;
        float ph = state == State.Placing ? 60 + 70 + (RowH + Gap) * 2 + 20 : 60 + 50 + (RowH + Gap) * (2 + flights) + 20;

        panel = XRUi.CreateWorldCanvas("StairEditPanel", new Vector2(PW, ph), PScale);
        var t = panel.transform;
        if (!panelPlaced)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            panelPos = head.position + fwd * 0.6f - Vector3.up * 0.18f + Quaternion.Euler(0, 90, 0) * fwd * 0.25f;
            panelRot = Quaternion.LookRotation(panelPos - head.position, Vector3.up);
            panelPlaced = true;
        }
        t.SetPositionAndRotation(panelPos, panelRot);
        XRUi.CreatePanel(t, "Background", XRUi.PanelColor, 0, 0, PW, ph);

        float y = 8, x0 = 20, fullW = PW - 40;
        if (state == State.Placing)
        {
            string next = placing.Count == 0 ? "bottom of the first step" : "middle of the next landing, or the top end";
            XRUi.CreateText(t, "Title", $"New stair - point {placing.Count + 1}: {next}", 22, TextAlignmentOptions.MidlineLeft, x0, y, fullW, 44, XRUi.TextColor, FontStyles.Bold);
            y += 52;
            XRUi.CreateText(t, "Hint", "Trigger on the floor under: first step's front edge -> each landing's middle -> last step's top edge. Points snap to right angles.",
                17, TextAlignmentOptions.TopLeft, x0, y, fullW, 64, XRUi.MutedText);
            y += 70;
            WidthRow(t, ref y, placingWidth, d => { placingWidth = Mathf.Clamp(placingWidth + d, 0.6f, 2.0f); RebuildPanel(); });
            float bw = (fullW - Gap * 2) / 3f;
            XRUi.CreateButton(t, "Remove last", x0, y, bw, RowH, () => { if (placing.Count > 0) placing.RemoveAt(placing.Count - 1); RebuildPanel(); }, 22);
            var finish = XRUi.CreateButton(t, "Finish", x0 + bw + Gap, y, bw, RowH, Finish, 22);
            XRUi.SetTint(finish, placing.Count >= 2 ? XRUi.ButtonOnColor : XRUi.ButtonDisabled);
            XRUi.CreateButton(t, "Cancel", x0 + (bw + Gap) * 2, y, bw, RowH, CancelPlacing, 22);
            return;
        }

        var r = StairGeometry.Resolve(sel, rooms);
        string info = r != null ? $"{sel.risers.Sum()} steps x {r.rise * 100f:0.0} cm = {r.topY - r.bottomY:0.00} m" : "";
        XRUi.CreateText(t, "Title", $"Stair  {info}", 22, TextAlignmentOptions.MidlineLeft, x0, y, fullW, 44, XRUi.TextColor, FontStyles.Bold);
        y += 52;
        WidthRow(t, ref y, sel.width, d => Change(s => s.width = Mathf.Clamp(s.width + d, 0.6f, 2.0f)));
        var lens = r != null ? StairGeometry.FlightLengths(r.points, r.width) : null;
        for (int i = 0; i < flights; i++)
        {
            int fi = i;
            int n = i < sel.risers.Count ? sel.risers[i] : 0;
            string tread = lens != null && n > 0 ? $"  tread {lens[i] / n * 100f:0} cm" : "";
            XRUi.CreateText(t, "Flight" + i, $"Flight {i + 1}: {n} steps{tread}", 20, TextAlignmentOptions.MidlineLeft, x0, y, 380, RowH, XRUi.TextColor);
            XRUi.CreateButton(t, "-", PW - 20 - 2 * 90 - Gap, y, 90, RowH, () => Change(s => SetRisers(s, fi, -1)), 30);
            XRUi.CreateButton(t, "+", PW - 20 - 90, y, 90, RowH, () => Change(s => SetRisers(s, fi, +1)), 30);
            y += RowH + Gap;
        }
        float w3 = (fullW - Gap * 2) / 3f;
        XRUi.CreateButton(t, "New stair", x0, y, w3, RowH, () => { state = State.Placing; placing.Clear(); RebuildPanel(); }, 22);
        var del = XRUi.CreateButton(t, "Delete", x0 + w3 + Gap, y, w3, RowH, Delete, 22);
        XRUi.SetTint(del, XRUi.ButtonDangerColor);
        XRUi.CreateButton(t, "Close", x0 + (w3 + Gap) * 2, y, w3, RowH, () => { state = State.Idle; ClosePanel(); }, 22);
    }

    void WidthRow(Transform t, ref float y, float width, Action<float> change)
    {
        XRUi.CreateText(t, "Width", $"Width {width:0.00} m", 20, TextAlignmentOptions.MidlineLeft, 20, y, 380, RowH, XRUi.TextColor);
        XRUi.CreateButton(t, "-", PW - 20 - 2 * 90 - Gap, y, 90, RowH, () => change(-0.05f), 30);
        XRUi.CreateButton(t, "+", PW - 20 - 90, y, 90, RowH, () => change(+0.05f), 30);
        y += RowH + Gap;
    }

    static void SetRisers(StairEdit s, int flight, int delta)
    {
        while (s.risers.Count < s.localPoints.Count - 1) s.risers.Add(0);
        s.risers[flight] = Mathf.Max(0, s.risers[flight] + delta);
        if (s.risers.Sum() == 0) s.risers[flight] = 1;
    }

    void CancelPlacing()
    {
        placing.Clear();
        var first = HouseEditsStore.Current.stairs.FirstOrDefault();
        if (first != null) { selectedId = first.id; state = State.Adjusting; }
        else state = State.Idle;
        RebuildPanel();
    }

    async void Finish()
    {
        if (placing.Count < 2) { uiLog?.AddLog("Stairs: place at least the bottom and the top end."); return; }
        var pts3 = new List<Vector3>(placing);
        var room = DoorCatalog.FindRoomAt(rooms, pts3[0] + Vector3.up * 0.05f);
        float rise = StairGeometry.TopFloorY(rooms, pts3[0].y) - pts3[0].y;
        var s = new StairEdit
        {
            id = Guid.NewGuid().ToString("N"),
            roomUuid = DoorCatalog.RoomId(room),
            width = placingWidth,
            localPoints = pts3.Select(p => new Vector3Data(room != null ? room.transform.InverseTransformPoint(p) : p)).ToList(),
            risers = StairGeometry.DefaultRisers(pts3, placingWidth, rise),
        };
        if (await editMode.Commit(e => e.stairs.Add(s)))
        {
            placing.Clear();
            selectedId = s.id; state = State.Adjusting;
            uiLog?.AddLog($"Stair added: {s.risers.Sum()} steps over {rise:0.00} m. Adjust steps per flight on the panel.");
            RebuildPanel();
        }
    }

    async void Change(Action<StairEdit> mutate)
    {
        string id = selectedId;
        await editMode.Commit(e =>
        {
            var s = e.stairs.FirstOrDefault(x => x.id == id);
            if (s != null) mutate(s);
        });
    }

    async void Delete()
    {
        string id = selectedId;
        await editMode.Commit(e => e.stairs.RemoveAll(x => x.id == id));
    }
}
