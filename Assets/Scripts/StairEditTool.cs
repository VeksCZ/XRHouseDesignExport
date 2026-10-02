using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Edit mode "Stairs" tool, step by step:
///  1. trigger on the floor at one corner of the bottom step, 2. at the other corner - that line is the front edge
///     of the first step and sets the width; the stairs run away from you, square to it (snapped to the house's
///     wall directions);
///  3. point along the stairs to where this flight ends (works through walls - the point is the closest one on the
///     flight's own axis to your ray) and pull the trigger;
///  4. on the panel: landing + turn left / turn right (then step 3 again for the next flight), or Finish.
/// A live 3D ghost shows the stair at its real heights all the way. Afterwards the panel adjusts the width and the
/// steps per flight (the rise per step follows from the floor-to-floor height). Trigger on a stair selects it.
/// </summary>
public class StairEditTool : MonoBehaviour
{
    public XRMenu uiLog;
    public EditModeController editMode;
    public WalkThroughMode walk;
    public DollHouseVisualizer dollhouse;

    enum State { Idle, Corner1, Corner2, FlightEnd, Turn, Adjusting }
    State state = State.Idle;
    bool wasActive;

    static readonly Color GhostColor = new Color(1f, 0.9f, 0.2f, 0.95f);
    static readonly Color SelectColor = new Color(1f, 0.55f, 0.1f, 1f);
    static readonly Color StairColor = new Color(0.3f, 0.95f, 0.45f, 0.95f);

    Transform frameRoot; float frameYaw; Vector3 frameCenter; bool frameIsWalk;
    List<MRUKRoom> rooms = new List<MRUKRoom>();
    Transform head, rightHand;

    // Placing (scan world): pts = bottom-middle, then landing centres; dirs[i] = run direction of flight i.
    Vector3 corner1;
    float width = 1.0f;
    readonly List<Vector3> pts = new List<Vector3>();
    readonly List<Vector3> dirs = new List<Vector3>();
    Vector3 axisStart;          // where the current flight starts (on the walking line)
    Vector3 pendingEnd;         // confirmed end of the current flight (Turn state)
    Vector3? hover;             // floor point / flight end under the ray, scan world
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

    bool Active => editMode != null && editMode.IsOn && editMode.CurrentTool == EditModeController.Tool.Stairs;
    bool Placing => state == State.Corner1 || state == State.Corner2 || state == State.FlightEnd || state == State.Turn;

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
            var first = HouseEditsStore.Current.stairs.FirstOrDefault();
            if (first != null) { selectedId = first.id; state = State.Adjusting; }
            else StartNew();
            RebuildPanel();
        }

        hover = state == State.FlightEnd ? AxisPoint() : PointedFloor();
        hoverWallN = null;
        if (state == State.Corner1 && hover.HasValue && walls != null && walls.Nearest(hover.Value, 0.35f, 0.5f, out var w, out float wd))
        {
            // Near a wall: the corner goes onto its face and the stairs will run along that wall.
            hover = hover.Value - w.n * wd;
            hoverWallN = w.n;
        }
        if (state == State.Corner2 && hover.HasValue) hover = SnapCorner2(hover.Value);

        foreach (var s in HouseEditsStore.Current.stairs)
        {
            var r = StairGeometry.Resolve(s, rooms);
            if (r != null) DrawResolved(r, s.id == selectedId && state == State.Adjusting ? SelectColor : StairColor);
        }
        DrawGhost();
        FinishLines();
    }

    void StartNew()
    {
        state = State.Corner1;
        pts.Clear(); dirs.Clear();
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

    /// <summary>The point on the current flight's axis closest to the controller ray - so the flight's end can be
    /// set by pointing roughly at it, even through walls or up into the stairwell.</summary>
    Vector3? AxisPoint()
    {
        if (dirs.Count == 0) return null;
        Vector3 d = dirs[dirs.Count - 1];
        Vector3 o = WorldToScan(rightHand.position);
        Vector3 u = WorldToScan(rightHand.position + rightHand.forward) - o;
        Vector3 w0 = o - axisStart;
        float a = Vector3.Dot(u, u), b = Vector3.Dot(u, d), dd = Vector3.Dot(u, w0), e = Vector3.Dot(d, w0);
        float den = a - b * b; // c = d.d = 1
        if (den < 1e-6f) return null;
        float sRay = (b * e - dd) / den;
        if (sRay < 0f) return null;
        float t = Mathf.Clamp((a * e - b * dd) / den, 0.2f, 8f);
        return axisStart + d * t;
    }

    WallSnap walls;
    Vector3? hoverWallN, corner1WallN;

    /// <summary>Second corner: square off the wall the first corner touched (the bottom step's edge runs straight
    /// out of that wall, so the stairs follow it) and stop on the opposite wall if it's close; otherwise square to
    /// the house's wall directions.</summary>
    Vector3 SnapCorner2(Vector3 raw)
    {
        if (!corner1WallN.HasValue) return Snap(corner1, raw);
        Vector3 n = corner1WallN.Value;
        float t = Mathf.Max(0.4f, Vector3.Dot(raw - corner1, n));
        float opposite = walls != null ? walls.Ahead(corner1, n, 2.5f, 0.5f) : -1f;
        if (opposite > 0f && Mathf.Abs(opposite - t) < 0.35f) t = opposite;
        return corner1 + n * t;
    }

    /// <summary>p moved so the line prev->p runs along one of the house's wall directions (model axes).</summary>
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
        if (!wasActive) { uiLog?.AddLog("Stairs: open the Dollhouse or Walk first."); return; }
        switch (state)
        {
            case State.Corner1:
                if (!hover.HasValue) { uiLog?.AddLog("Stairs: point at the floor."); return; }
                corner1 = hover.Value;
                corner1WallN = hoverWallN;
                state = State.Corner2;
                break;
            case State.Corner2:
            {
                if (!hover.HasValue) { uiLog?.AddLog("Stairs: point at the floor."); return; }
                Vector3 c2 = hover.Value; c2.y = corner1.y;
                Vector3 edge = c2 - corner1; edge.y = 0f;
                if (edge.magnitude < 0.4f) { uiLog?.AddLog("Stairs: the two corners are too close."); return; }
                width = Mathf.Clamp(edge.magnitude, 0.6f, 2.0f);
                Vector3 p0 = (corner1 + c2) / 2f;
                Vector3 d = Vector3.Cross(Vector3.up, edge).normalized;
                // The stairs run away from you (you stand at the bottom looking up).
                Vector3 eye = WorldToScan(head.position);
                if (Vector3.Dot(d, p0 - new Vector3(eye.x, p0.y, eye.z)) < 0f) d = -d;
                pts.Clear(); dirs.Clear();
                pts.Add(p0); dirs.Add(d);
                axisStart = p0;
                state = State.FlightEnd;
                break;
            }
            case State.FlightEnd:
                if (!hover.HasValue) { uiLog?.AddLog("Stairs: point along the stairs."); return; }
                pendingEnd = hover.Value;
                state = State.Turn;
                break;
            case State.Turn:
                return; // choose on the panel
            default:
                // Idle/Adjusting: pick the stair whose footprint is under the ray.
                var floor = PointedFloor();
                if (!floor.HasValue) return;
                foreach (var s in HouseEditsStore.Current.stairs)
                {
                    var r = StairGeometry.Resolve(s, rooms);
                    if (r != null && r.footprint.Any(rect => Inside(rect, floor.Value)))
                    {
                        selectedId = s.id; state = State.Adjusting; RebuildPanel();
                        return;
                    }
                }
                return;
        }
        RebuildPanel();
    }

    /// <summary>Landing at the end of the current flight, then the next flight turning left (-1) or right (+1).</summary>
    void TurnLanding(int side)
    {
        Vector3 d = dirs[dirs.Count - 1];
        // A wall just beyond the landing: push/pull the landing so its far edge sits right against it.
        if (walls != null)
        {
            float s = walls.Ahead(new Vector3(pendingEnd.x, pts[0].y, pendingEnd.z), d, width + 0.6f, 0.5f);
            if (s > 0f)
            {
                Vector3 snapped = pendingEnd + d * (s - width);
                if (Vector3.Dot(snapped - axisStart, d) >= 0.2f) pendingEnd = snapped;
            }
        }
        Vector3 c = pendingEnd + d * (width / 2f);
        Vector3 nd = Quaternion.Euler(0, 90f * side, 0) * d;
        pts.Add(c); dirs.Add(nd);
        axisStart = c + nd * (width / 2f);
        state = State.FlightEnd;
        RebuildPanel();
    }

    /// <summary>One step back through the placing states.</summary>
    void Back()
    {
        switch (state)
        {
            case State.Corner2: state = State.Corner1; break;
            case State.FlightEnd:
                if (pts.Count <= 1) { state = State.Corner2; break; }
                // Undo the last landing: back to choosing what follows the previous flight.
                Vector3 c = pts[pts.Count - 1];
                pts.RemoveAt(pts.Count - 1); dirs.RemoveAt(dirs.Count - 1);
                pendingEnd = c - dirs[dirs.Count - 1] * (width / 2f);
                axisStart = pts.Count == 1 ? pts[0] : pts[pts.Count - 1] + dirs[dirs.Count - 1] * (width / 2f);
                state = State.Turn;
                break;
            case State.Turn: state = State.FlightEnd; break;
        }
        RebuildPanel();
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
            if (first != null) selectedId = first.id; else StartNew();
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
            line.Clear();
            line.Add(At(start - side, y0)); line.Add(At(end - side, y1)); line.Add(At(end + side, y1)); line.Add(At(start + side, y0));
            Emit(c, true);
        }
        foreach (var (center, dir, y) in r.landings)
        {
            Vector3 f = dir * (r.width / 2f), side = Vector3.Cross(Vector3.up, dir) * (r.width / 2f);
            line.Clear();
            line.Add(At(center - f - side, y)); line.Add(At(center + f - side, y)); line.Add(At(center + f + side, y)); line.Add(At(center - f + side, y));
            Emit(c, true);
        }
    }

    static Vector3 At(Vector3 p, float y) => new Vector3(p.x, y + 0.02f, p.z);

    void Cross(Vector3 p)
    {
        line.Clear(); line.Add(At(p - Vector3.right * 0.15f, p.y)); line.Add(At(p + Vector3.right * 0.15f, p.y)); Emit(GhostColor, false);
        line.Clear(); line.Add(At(p - Vector3.forward * 0.15f, p.y)); line.Add(At(p + Vector3.forward * 0.15f, p.y)); Emit(GhostColor, false);
    }

    void DrawGhost()
    {
        switch (state)
        {
            case State.Corner1:
                if (hover.HasValue) Cross(hover.Value);
                break;
            case State.Corner2:
                Cross(corner1);
                if (hover.HasValue)
                {
                    Vector3 c2 = hover.Value; c2.y = corner1.y;
                    line.Clear(); line.Add(At(corner1, corner1.y)); line.Add(At(c2, corner1.y)); Emit(GhostColor, false);
                }
                break;
            case State.FlightEnd:
            case State.Turn:
            {
                Vector3? end = state == State.Turn ? pendingEnd : hover;
                if (!end.HasValue) { DrawPartial(); break; }
                var all = new List<Vector3>(pts) { end.Value };
                var temp = new StairEdit { roomUuid = "", width = width, localPoints = all.Select(p => new Vector3Data(p)).ToList() };
                var r = StairGeometry.Resolve(temp, rooms);
                if (r != null) DrawResolved(r, GhostColor);
                break;
            }
        }
    }

    /// <summary>While the ray isn't on the flight's axis: just the walking line so far.</summary>
    void DrawPartial()
    {
        if (pts.Count == 0) return;
        line.Clear();
        foreach (var p in pts) line.Add(At(p, pts[0].y));
        line.Add(At(axisStart, pts[0].y));
        Emit(GhostColor, false);
    }

    void Emit(Color c, bool loop)
    {
        if (used >= pool.Count) pool.Add(NewLine());
        var lr = pool[used++];
        lr.gameObject.SetActive(true);
        lr.loop = loop;
        lr.positionCount = line.Count;
        for (int i = 0; i < line.Count; i++) lr.SetPosition(i, ScanToWorld(line[i]));
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
        if (panel && panelDrag && panelDrag.Moved) userPlaced = true;
        if (panel) { panelPos = panel.transform.position; panelRot = panel.transform.rotation; }
        if (panel) Destroy(panel.gameObject);
        panel = null;
    }

    void RebuildPanel()
    {
        if (!wasActive || !head) return;
        if (panel) panelPlaced = true;
        ClosePanel();
        if (state == State.Idle) return;

        StairEdit sel = state == State.Adjusting ? HouseEditsStore.Current.stairs.FirstOrDefault(s => s.id == selectedId) : null;
        if (state == State.Adjusting && sel == null) return;
        int flights = sel != null ? Mathf.Max(0, sel.localPoints.Count - 1) : 0;
        float ph = Placing ? 60 + 76 + (RowH + Gap) * 2 + 20 : 60 + 50 + (RowH + Gap) * (2 + flights) + 20;

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
        panelDrag = PanelDrag.Attach(panel, PW, ph);
        XRUi.CreatePanel(t, "Background", XRUi.PanelColor, 0, 0, PW, ph);

        float y = 8, x0 = 20, fullW = PW - 40;
        if (Placing)
        {
            int flight = dirs.Count;
            string title = state switch
            {
                State.Corner1 => "New stair - step 1/3",
                State.Corner2 => "New stair - step 2/3",
                State.FlightEnd => $"Flight {flight} - where does it end?",
                _ => $"Flight {flight} ends here - then?",
            };
            string hint = state switch
            {
                State.Corner1 => "Trigger on the floor at ONE corner of the bottom step. Next to a wall it snaps onto the wall and the stairs will run along it.",
                State.Corner2 => "Trigger at the OTHER corner - sets the width (stops on the opposite wall if there is one). The stairs run away from you.",
                State.FlightEnd => "Point along the stairs to the top of this flight (works through walls) and pull the trigger.",
                _ => "Add a landing turning left or right (it snaps against a wall right behind it), or finish here if this is the top.",
            };
            XRUi.CreateText(t, "Title", title, 22, TextAlignmentOptions.MidlineLeft, x0, y, fullW, 44, XRUi.TextColor, FontStyles.Bold);
            y += 52;
            XRUi.CreateText(t, "Hint", hint, 18, TextAlignmentOptions.TopLeft, x0, y, fullW, 70, XRUi.MutedText);
            y += 76;
            if (state == State.Turn)
            {
                float bw = (fullW - Gap * 2) / 3f;
                XRUi.CreateButton(t, "Landing, turn left", x0, y, bw, RowH, () => TurnLanding(-1), 19);
                XRUi.CreateButton(t, "Landing, turn right", x0 + bw + Gap, y, bw, RowH, () => TurnLanding(+1), 19);
                var fin = XRUi.CreateButton(t, "Finish here", x0 + (bw + Gap) * 2, y, bw, RowH, Finish, 20);
                XRUi.SetTint(fin, XRUi.ButtonOnColor);
            }
            else if (state == State.FlightEnd && hover.HasValue)
            {
                float len = Vector3.Distance(axisStart, hover.Value);
                XRUi.CreateText(t, "Len", $"Width {width:0.00} m", 20, TextAlignmentOptions.MidlineLeft, x0, y, fullW, RowH, XRUi.TextColor);
            }
            y += RowH + Gap;
            float b2 = (fullW - Gap) / 2f;
            XRUi.CreateButton(t, "Back", x0, y, b2, RowH, Back, 22);
            XRUi.CreateButton(t, "Cancel", x0 + b2 + Gap, y, b2, RowH, CancelPlacing, 22);
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
        XRUi.CreateButton(t, "New stair", x0, y, w3, RowH, () => { StartNew(); RebuildPanel(); }, 22);
        var del = XRUi.CreateButton(t, "Delete", x0 + w3 + Gap, y, w3, RowH, Delete, 22);
        XRUi.SetTint(del, XRUi.ButtonDangerColor);
        XRUi.CreateButton(t, "Close", x0 + (w3 + Gap) * 2, y, w3, RowH, () => { state = State.Idle; ClosePanel(); }, 22);
    }

    void WidthRow(Transform t, ref float y, float w, Action<float> change)
    {
        XRUi.CreateText(t, "Width", $"Width {w:0.00} m", 20, TextAlignmentOptions.MidlineLeft, 20, y, 380, RowH, XRUi.TextColor);
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
        pts.Clear(); dirs.Clear();
        var first = HouseEditsStore.Current.stairs.FirstOrDefault();
        if (first != null) { selectedId = first.id; state = State.Adjusting; }
        else state = State.Idle;
        RebuildPanel();
    }

    async void Finish()
    {
        if (pts.Count < 1) return;
        var all = new List<Vector3>(pts) { pendingEnd };
        var room = DoorCatalog.FindRoomAt(rooms, all[0] + Vector3.up * 0.05f);
        float rise = StairGeometry.TopFloorY(rooms, all[0].y) - all[0].y;
        var s = new StairEdit
        {
            id = Guid.NewGuid().ToString("N"),
            roomUuid = DoorCatalog.RoomId(room),
            width = width,
            localPoints = all.Select(p => new Vector3Data(room != null ? room.transform.InverseTransformPoint(p) : p)).ToList(),
            risers = StairGeometry.DefaultRisers(all, width, rise),
        };
        if (await editMode.Commit(e => e.stairs.Add(s)))
        {
            pts.Clear(); dirs.Clear();
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
