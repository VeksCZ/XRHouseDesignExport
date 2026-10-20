using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Edit mode "Routes" tool: traces installation routes (cables, cold/hot water, waste, heating) by clicking points along
/// them. Over passthrough on the live scan the points land on the real walls/floor/ceiling (the scan's surfaces); in the
/// walk-through on the model's. Pick the kind first (colour), then trigger on the start, every bend and the end;
/// "Finish route" (or trigger twice on the same spot) ends it. A point near an existing route snaps onto it (branches,
/// T-pieces); a segment within a few degrees of vertical or level is straightened. Each route's centre runs half its
/// diameter in front of the surface - on the floor a pipe lies on it, in a wall chase it shows on the face.
/// Routes are stored house-wide (RouteStore), not in a saved scan's edits.
/// </summary>
public class RouteEditTool : MonoBehaviour
{
    public XRMenu uiLog;
    public EditModeController editMode;
    public WalkThroughMode walk;
    public MRUKExporter exporter;

    const string KindPref = "Routes.Kind", MarkerPref = "Routes.MarkerType";
    const float SnapToRoute = 0.05f, StraightenDeg = 7f;

    RouteKind kind;
    int diaIdx, labelIdx;
    string drawingId;
    bool deleteMode, wasActive, preparing, markerMode, joinMode;
    string joinFirstId; bool joinFirstAtStart;
    int markerIdx;
    float retryAt;

    Transform head, rightHand;
    bool frameIsWalk; Transform frameRoot; float frameYaw; Vector3 frameCenter;
    Vector3? hover; Vector3 hoverNormal;

    LineRenderer preview, highlight;
    GameObject cursor;
    Material lineMat, cursorMat;

    Canvas panel;
    PanelDrag panelDrag;
    TMP_Text titleText, hintText;
    readonly Dictionary<RouteKind, Button> kindButtons = new Dictionary<RouteKind, Button>();
    Button diaButton, labelButton, finishButton, deleteButton, joinButton, modeButton, markerButton, acceptButton;
    bool panelPlaced;
    const float PW = 640f, PH = 430f, PScale = 0.00055f, RowH = 58f, Gap = 8f;

#if META_XR_SDK_INSTALLED
    static List<MRUKRoom> roomCache = new List<MRUKRoom>();
    static int roomCacheCount = -1;
    static List<MRUKRoom> Rooms
    {
        get
        {
            var m = MRUK.Instance;
            // keyed by the room count AND the scene version: another scan with as many rooms must not reuse the old list
            int n = (m != null ? m.Rooms.Count : 0) + 1000 * MRUKExporter.SceneVersion;
            if (n != roomCacheCount) { roomCache = m != null ? MRUKDataProcessor.GetValidRooms(m) : new List<MRUKRoom>(); roomCacheCount = n; }
            return roomCache;
        }
    }
#endif

    void Start()
    {
        kind = (RouteKind)Mathf.Clamp(PlayerPrefs.GetInt(KindPref, (int)RouteKind.ColdWater), 0, RouteStyle.Kinds.Length - 1);
        markerIdx = Mathf.Clamp(PlayerPrefs.GetInt(MarkerPref, 0), 0, RouteStyle.MarkerTypes.Length - 1);
        if (editMode != null) editMode.TriggerPressed += OnTrigger;
        RouteStore.Changed += OnRoutesChanged;
    }

    void OnDestroy()
    {
        if (editMode != null) editMode.TriggerPressed -= OnTrigger;
        RouteStore.Changed -= OnRoutesChanged;
        ClosePanel();
        if (preview) Destroy(preview.gameObject);
        if (highlight) Destroy(highlight.gameObject);
        if (cursor) Destroy(cursor);
        if (lineMat) Destroy(lineMat);
        if (cursorMat) Destroy(cursorMat);
    }

    bool Active => editMode != null && editMode.IsOn && editMode.CurrentTool == EditModeController.Tool.Routes;

    void Update()
    {
        if (!Active)
        {
            if (wasActive)
            {
                wasActive = false;
                drawingId = null; deleteMode = false; joinMode = false; joinFirstId = null;
                WallOverlays.ForceRoutes = false;
                ClosePanel();
                ShowPreview(false);
            }
            return;
        }
        if (!wasActive) Begin();
        if (preparing) return;
        if (!ResolveFrame())
        {
            hover = null; ShowPreview(false);
            if (!frameIsWalk && Time.time >= retryAt) EnsureLive();
            return;
        }
        hover = Pointed(out hoverNormal);
        UpdatePreview();
    }

    void Begin()
    {
        wasActive = true;
        var rig = FindFirstObjectByType<OVRCameraRig>();
        head = rig ? rig.centerEyeAnchor : Camera.main ? Camera.main.transform : null;
        rightHand = rig ? rig.rightHandAnchor : null;
        WallOverlays.ForceRoutes = true;
        RouteStore.Reload();
        panelPlaced = false;
        BuildPanel();
        uiLog?.AddLog("Routes: pick the kind, then trigger on the start, every bend and the end of a pipe/cable. Finish route = done.");
    }

    async void EnsureLive()
    {
        if (preparing || exporter == null) return;
        preparing = true;
        retryAt = Time.time + 5f;
        try
        {
            SetHint("Loading the scan onto the real walls...");
            if (!await exporter.EnsureLiveLoaded(uiLog)) SetHint("<color=#ff6060>This scan isn't lined up with the house here - walk through the model and trace there, or pick Live scan with < >.</color>");
            else RefreshPanel();
        }
        finally { preparing = false; }
    }

    // ---------- frame & picking ----------

    bool ResolveFrame()
    {
        if (!head || !rightHand) return false;
        if (walk != null && walk.TryGetModelFrame(out var r, out var y, out var c))
        {
            frameIsWalk = true; frameRoot = r; frameYaw = y; frameCenter = c;
            return true;
        }
        frameIsWalk = false; frameRoot = null;
        return exporter != null && exporter.WorldAligned;
    }

    Quaternion G => Quaternion.Euler(0, frameYaw, 0);
    /// <summary>Scan frame -> what you see (the model in the walk-through, the real house over passthrough).</summary>
    Vector3 ToView(Vector3 p) => frameIsWalk ? frameRoot.TransformPoint(G * (p - frameCenter)) : p;

    float Radius => RouteStyle.Diameters(kind)[Mathf.Clamp(diaIdx, 0, RouteStyle.Diameters(kind).Length - 1)] / 2f;

    /// <summary>The surface point under the right controller's ray (scan frame) and its normal. The ray stops at the
    /// walls of the room you stand in (the scan's flat walls, also under the walk-through model), so it never runs
    /// through a wall into the next room.</summary>
    Vector3? Pointed(out Vector3 normal)
    {
        normal = Vector3.up;
        var ray = new Ray(rightHand.position, rightHand.forward);
        Vector3 headScan = head.position;
        Ray scanRay = ray;
        float best = float.MaxValue; Vector3 p = default;
        if (frameIsWalk)
        {
            Quaternion gi = Quaternion.Inverse(G);
            scanRay = new Ray(gi * frameRoot.InverseTransformPoint(ray.origin) + frameCenter,
                              (gi * frameRoot.InverseTransformDirection(ray.direction)).normalized);
            headScan = gi * frameRoot.InverseTransformPoint(head.position) + frameCenter;
            if (walk.RaycastModel(ray, out var hit))
            {
                p = gi * frameRoot.InverseTransformPoint(hit.point) + frameCenter;
                normal = (gi * frameRoot.InverseTransformDirection(hit.normal)).normalized;
                best = Vector3.Distance(scanRay.origin, p);
            }
        }
#if META_XR_SDK_INSTALLED
        // the room you stand in first: its walls, floor and ceiling face you, nothing behind them counts
        // (walking the model: only its shell is a backstop - furniture is whatever the model shows)
        var here = CurrentRoom(headScan);
        var filter = frameIsWalk ? LabelFilter.Included(Shell) : new LabelFilter();
        bool hitHere = false;
        if (here != null && here.Raycast(scanRay, 15f, filter, out RaycastHit hh, out _) && hh.distance < best)
        { best = hh.distance; p = hh.point; normal = hh.normal; hitHere = true; }
        if (here == null || (!hitHere && best == float.MaxValue)) // in a doorway / outside every room: any room
            foreach (var room in Rooms)
                if (room != here && room.Raycast(scanRay, 15f, filter, out RaycastHit h, out _) && h.distance < best) { best = h.distance; p = h.point; normal = h.normal; }
#endif
        return best < float.MaxValue ? p : (Vector3?)null;
    }

#if META_XR_SDK_INSTALLED
    const MRUKAnchor.SceneLabels Shell = MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE
        | MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.CEILING;
    MRUKRoom currentRoom; float currentRoomAt = -1f;
    /// <summary>The room your head is in (scan frame), checked a few times a second.</summary>
    MRUKRoom CurrentRoom(Vector3 headScan)
    {
        if (Time.unscaledTime - currentRoomAt < 0.25f && (currentRoom == null || Rooms.Contains(currentRoom))) return currentRoom;
        currentRoomAt = Time.unscaledTime;
        MRUKRoom found = null; float bestY = float.MaxValue;
        foreach (var r in Rooms)
        {
            if (r == null || !r.IsPositionInRoom(headScan, true)) continue;
            // stacked/overlapping rooms: the one whose floor is right below the head
            var f = r.FloorAnchor;
            float dy = f ? headScan.y - f.transform.position.y : 10f;
            if (dy >= -0.2f && dy < bestY) { bestY = dy; found = r; }
        }
        currentRoom = found; // null in a doorway / outside: then every room counts
        return currentRoom;
    }
#endif

    /// <summary>Where a click would put the next point: on the centre line in front of the surface, snapped onto an
    /// existing route nearby, or straightened against the previous point.</summary>
    Vector3 Candidate(out bool snapped)
    {
        snapped = false;
        Vector3 c = hover.Value + hoverNormal * Radius;
#if META_XR_SDK_INSTALLED
        var pts = new List<Vector3>();
        float best = SnapToRoute; Vector3 bestP = c;
        foreach (var r in RouteStore.Routes)
        {
            if (!RouteStore.WorldPoints(r, MRUKRooms(), pts)) continue;
            for (int i = 0; i < pts.Count; i++)
            {
                if (r.id == drawingId && i == pts.Count - 1) continue;
                float d = Vector3.Distance(pts[i], c);
                if (d < best) { best = d; bestP = pts[i]; snapped = true; }
                if (i + 1 < pts.Count)
                {
                    Vector3 q = ClosestOnSegment(pts[i], pts[i + 1], c);
                    float dq = Vector3.Distance(q, c);
                    if (dq < best * 0.8f) { best = dq; bestP = q; snapped = true; }
                }
            }
        }
        if (snapped) return bestP;
        if (LastPoint(out var last))
        {
            Vector3 d = c - last;
            float h = new Vector2(d.x, d.z).magnitude;
            if (h > 1e-3f && Mathf.Atan2(Mathf.Abs(d.y), h) * Mathf.Rad2Deg < StraightenDeg) c.y = last.y;       // level
            else if (Mathf.Atan2(h, Mathf.Abs(d.y)) * Mathf.Rad2Deg < StraightenDeg) { c.x = last.x; c.z = last.z; } // plumb
        }
#endif
        return c;
    }

    static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float t = ab.sqrMagnitude < 1e-8f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
        return a + ab * t;
    }

#if META_XR_SDK_INSTALLED
    static List<MRUKRoom> MRUKRooms() => MRUK.Instance != null ? MRUK.Instance.Rooms : new List<MRUKRoom>();
#endif

    PipeRoute Drawing => drawingId == null ? null : RouteStore.Routes.FirstOrDefault(r => r.id == drawingId);

    bool LastPoint(out Vector3 p)
    {
        p = default;
        var r = Drawing;
#if META_XR_SDK_INSTALLED
        var pts = new List<Vector3>();
        if (r == null || !RouteStore.WorldPoints(r, MRUKRooms(), pts)) return false;
        p = pts[pts.Count - 1];
        return true;
#else
        return false;
#endif
    }

    // ---------- actions ----------

    void OnTrigger()
    {
        if (!Active || preparing) return;
        if (!hover.HasValue) { uiLog?.AddLog("Routes: point at a wall, floor or ceiling."); return; }
        if (deleteMode) { DeleteNearest(); return; }
        if (joinMode) { JoinPick(); return; }
        if (markerMode) { PlaceMarker(); return; }
        Vector3 c = Candidate(out _);
#if META_XR_SDK_INSTALLED
        var route = Drawing;
        if (route == null)
        {
            var rooms = MRUKRooms();
            var room = DoorCatalog.FindRoomAt(rooms, c) ?? DoorCatalog.FindRoomAt(rooms, c + Vector3.up * 0.2f)
                       ?? rooms.Where(x => x != null).OrderBy(x => Vector3.Distance(x.transform.position, c)).FirstOrDefault();
            if (room == null) { uiLog?.AddLog("Routes: no scanned room here."); return; }
            var r = new PipeRoute
            {
                id = Guid.NewGuid().ToString("N"), kind = kind, diameter = Radius * 2f,
                roomUuid = DoorCatalog.RoomId(room), created = DateTime.Now.ToString("s"),
            };
            r.localPoints.Add(new Vector3Data(room.transform.InverseTransformPoint(c)));
            RouteStore.Apply(f => f.routes.Add(r));
            drawingId = r.id; labelIdx = 0;
            Haptic(0.5f);
            Debug.Log($"[Routes] new {RouteStyle.Name(kind)} route {r.id.Substring(0, 6)} in {DoorCatalog.RoomId(room).Substring(0, 8)} at {c} ({(frameIsWalk ? "walk" : "passthrough")})");
        }
        else
        {
            if (LastPoint(out var last) && Vector3.Distance(last, c) < 0.015f) { Finish(); return; } // same spot twice = done
            var room = MRUKRooms().FirstOrDefault(x => x != null && DoorCatalog.RoomId(x) == route.roomUuid);
            if (room == null) { drawingId = null; return; }
            string id = route.id;
            Vector3 local = room.transform.InverseTransformPoint(c);
            RouteStore.Apply(f => f.routes.First(x => x.id == id).localPoints.Add(new Vector3Data(local)));
            Haptic(0.35f);
            Debug.Log($"[Routes] {RouteStyle.Name(route.kind)} {id.Substring(0, 6)}: point {route.localPoints.Count} at {c} ({(frameIsWalk ? "walk" : "passthrough")})");
        }
#endif
        RefreshPanel();
    }

    /// <summary>A box/socket/outlet... on the surface under the ray, of the current kind and type.</summary>
    void PlaceMarker()
    {
#if META_XR_SDK_INSTALLED
        Vector3 p = hover.Value + hoverNormal * 0.003f;
        var rooms = MRUKRooms();
        var room = DoorCatalog.FindRoomAt(rooms, p) ?? DoorCatalog.FindRoomAt(rooms, p + Vector3.up * 0.2f)
                   ?? rooms.Where(x => x != null).OrderBy(x => Vector3.Distance(x.transform.position, p)).FirstOrDefault();
        if (room == null) { uiLog?.AddLog("Routes: no scanned room here."); return; }
        var m = new RouteMarker
        {
            id = Guid.NewGuid().ToString("N"), kind = kind, type = RouteStyle.MarkerTypes[markerIdx],
            roomUuid = DoorCatalog.RoomId(room), created = DateTime.Now.ToString("s"),
            localPos = new Vector3Data(room.transform.InverseTransformPoint(p)),
            localNormal = new Vector3Data(room.transform.InverseTransformDirection(hoverNormal)),
        };
        RouteStore.Apply(f => f.markers.Add(m));
        Haptic(0.5f);
        uiLog?.AddLog($"Routes: {m.type} placed ({RouteStyle.Name(kind)}).");
#endif
        RefreshPanel();
    }

    void AcceptProposals()
    {
        int n = RouteStore.Routes.Count(r => r.proposed);
        if (n == 0) { uiLog?.AddLog("Routes: no proposals from the photos."); return; }
        RouteStore.Apply(f => { foreach (var r in f.routes) r.proposed = false; });
        uiLog?.AddLog($"Routes: {n} proposal(s) kept as routes. Wrong ones: Delete... then trigger on them.");
        RefreshPanel();
    }

    void Finish()
    {
        var r = Drawing;
        if (r != null && r.localPoints.Count < 2)
        {
            string id = r.id;
            RouteStore.Apply(f => f.routes.RemoveAll(x => x.id == id)); // a single point is no route
        }
        if (r != null) uiLog?.AddLog($"Routes: {RouteStyle.Name(r.kind)} route done ({r.localPoints.Count} points).");
        drawingId = null;
        if (r != null && r.localPoints.Count >= 2) AutoJoinThroughWall(r.id);
        RefreshPanel();
    }

    /// <summary>Join mode: the first trigger picks a route and its nearer end, the second the other route's end.</summary>
    void JoinPick()
    {
#if META_XR_SDK_INSTALLED
        var target = NearestRoute(out var pts, out _);
        if (target == null || pts == null || pts.Count == 0) { uiLog?.AddLog("Routes: point right at a route (near its end)."); return; }
        bool atStart = Vector3.Distance(pts[0], hover.Value) < Vector3.Distance(pts[pts.Count - 1], hover.Value);
        if (joinFirstId == null || joinFirstId == target.id)
        {
            joinFirstId = target.id; joinFirstAtStart = atStart;
            Haptic(0.4f);
            uiLog?.AddLog($"Routes: join - {RouteStyle.Name(target.kind)} picked, now the other route's end.");
        }
        else
        {
            string a = joinFirstId; bool aStart = joinFirstAtStart;
            joinFirstId = null;
            if (Join(a, aStart, target.id, atStart)) { joinMode = false; Haptic(0.6f); }
        }
#endif
        RefreshPanel();
    }

#if META_XR_SDK_INSTALLED
    /// <summary>Joins route B onto route A at the given ends into one route (A's room frame, its kind and size; the
    /// segment between the two ends runs straight through the wall). One undo step.</summary>
    bool Join(string aId, bool aAtStart, string bId, bool bAtStart, bool auto = false)
    {
        var rooms = MRUKRooms();
        var A = RouteStore.Routes.FirstOrDefault(x => x.id == aId);
        var B = RouteStore.Routes.FirstOrDefault(x => x.id == bId);
        if (A == null || B == null || A == B) return false;
        if (A.kind != B.kind) { uiLog?.AddLog($"Routes: can't join {RouteStyle.Name(A.kind)} with {RouteStyle.Name(B.kind)}."); return false; }
        var pa = new List<Vector3>(); var pb = new List<Vector3>();
        if (!RouteStore.WorldPoints(A, rooms, pa) || !RouteStore.WorldPoints(B, rooms, pb)) return false;
        var roomA = rooms.FirstOrDefault(x => x != null && DoorCatalog.RoomId(x) == A.roomUuid);
        if (roomA == null) return false;
        if (aAtStart) pa.Reverse();   // A runs towards the joint
        if (!bAtStart) pb.Reverse();  // B runs away from it
        string startLabel = aAtStart ? A.endLabel : A.startLabel, endLabel = bAtStart ? B.endLabel : B.startLabel;
        float gap = Vector3.Distance(pa[pa.Count - 1], pb[0]);
        if (gap < 0.01f) pb.RemoveAt(0);
        var local = pa.Concat(pb).Select(p => new Vector3Data(roomA.transform.InverseTransformPoint(p))).ToList();
        RouteStore.Apply(f =>
        {
            var a = f.routes.First(x => x.id == aId);
            a.localPoints = local; a.startLabel = startLabel; a.endLabel = endLabel;
            f.routes.RemoveAll(x => x.id == bId);
        });
        string msg = $"Routes: {RouteStyle.Name(A.kind)} joined{(gap > 0.1f ? $" through the wall ({gap * 100f:0} cm)" : "")} - Undo splits it again.";
        uiLog?.AddLog(auto ? "<color=#7fe07f>" + msg + "</color>" : msg);
        Debug.Log($"[Routes] join {aId.Substring(0, 6)}+{bId.Substring(0, 6)} gap {gap:0.00} m{(auto ? " (auto)" : "")}");
        return true;
    }
#endif

    /// <summary>After a route is finished: if one of its ends lies just behind a wall from an end of another route
    /// of the same kind in another room (drawn on the other side), join them through the wall.</summary>
    void AutoJoinThroughWall(string id)
    {
#if META_XR_SDK_INSTALLED
        const float MaxGap = 0.7f; // wall + both pipes' offsets
        var rooms = MRUKRooms();
        var r = RouteStore.Routes.FirstOrDefault(x => x.id == id);
        var pr = new List<Vector3>();
        if (r == null || !RouteStore.WorldPoints(r, rooms, pr) || pr.Count < 2) return;
        var po = new List<Vector3>();
        string bestId = null; bool bestMineStart = false, bestOtherStart = false; float best = MaxGap;
        foreach (var o in RouteStore.Routes)
        {
            if (o.id == id || o.kind != r.kind || o.roomUuid == r.roomUuid || o.proposed) continue;
            if (!RouteStore.WorldPoints(o, rooms, po) || po.Count < 2) continue;
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                {
                    float d = Vector3.Distance(i == 0 ? pr[0] : pr[pr.Count - 1], j == 0 ? po[0] : po[po.Count - 1]);
                    if (d < best) { best = d; bestId = o.id; bestMineStart = i == 0; bestOtherStart = j == 0; }
                }
        }
        if (bestId != null) Join(bestId, bestOtherStart, id, bestMineStart, auto: true);
#endif
    }

    void DeleteNearest()
    {
#if META_XR_SDK_INSTALLED
        // pick along the controller ray (the whole drawn line, not just where the ray hits the wall)
        var marker = NearestMarker(out float md);
        var target = NearestRoute(out _, out float rd);
        if (marker != null && md <= rd)
        {
            string mid = marker.id;
            RouteStore.Apply(f => f.markers.RemoveAll(x => x.id == mid));
            uiLog?.AddLog($"Routes: deleted a {marker.type} (Undo brings it back).");
            deleteMode = false; RefreshPanel();
            return;
        }
        if (target == null) { uiLog?.AddLog("Routes: nothing there - point right at a route or a box."); return; }
        string id = target.id;
        RouteStore.Apply(f => f.routes.RemoveAll(x => x.id == id));
        if (drawingId == id) drawingId = null;
        uiLog?.AddLog($"Routes: deleted a {RouteStyle.Name(target.kind)} route (Undo brings it back).");
#endif
        deleteMode = false;
        RefreshPanel();
    }

#if META_XR_SDK_INSTALLED
    /// <summary>The controller ray in the scan frame, up to just behind the wall it hits (so nothing is picked
    /// through a wall).</summary>
    bool PickRay(out Vector3 o, out Vector3 d, out float len)
    {
        o = d = default; len = 0;
        if (!rightHand || !hover.HasValue) return false;
        o = rightHand.position; d = rightHand.forward;
        if (frameIsWalk)
        {
            Quaternion gi = Quaternion.Inverse(G);
            o = gi * frameRoot.InverseTransformPoint(o) + frameCenter;
            d = (gi * frameRoot.InverseTransformDirection(d)).normalized;
        }
        len = Vector3.Distance(o, hover.Value) + 0.25f;
        return true;
    }

    /// <summary>Distance between the segment o..o+d*len and the segment a..b.</summary>
    static float SegSegDist(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        if (a < 1e-9f && e < 1e-9f) return Vector3.Distance(p1, p2);
        if (a < 1e-9f) { s = 0; t = Mathf.Clamp01(f / e); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e < 1e-9f) { t = 0; s = Mathf.Clamp01(-c / a); }
            else
            {
                float b = Vector3.Dot(d1, d2), den = a * e - b * b;
                s = den > 1e-9f ? Mathf.Clamp01((b * f - c * e) / den) : 0f;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Mathf.Clamp01(-c / a); }
                else if (t > 1) { t = 1; s = Mathf.Clamp01((b - c) / a); }
            }
        }
        return Vector3.Distance(p1 + d1 * s, p2 + d2 * t);
    }

    /// <summary>Pick tolerance around the ray: a few cm plus the pipe's own radius.</summary>
    const float RayPick = 0.06f;

    RouteMarker NearestMarker(out float dist)
    {
        dist = float.MaxValue; RouteMarker best = null;
        if (!PickRay(out var o, out var d, out var len)) return null;
        Vector3 e = o + d * len;
        foreach (var m in RouteStore.Current.markers)
        {
            if (!RouteStore.WorldMarker(m, MRUKRooms(), out var mp, out _)) continue;
            float dd = SegSegDist(o, e, mp, mp);
            if (dd < RayPick * 1.5f && dd < dist) { dist = dd; best = m; }
        }
        return best;
    }

    PipeRoute NearestRoute(out List<Vector3> points, out float dist)
    {
        points = null; dist = float.MaxValue;
        if (!PickRay(out var o, out var d, out var len)) return null;
        Vector3 e = o + d * len;
        PipeRoute best = null;
        var pts = new List<Vector3>();
        foreach (var r in RouteStore.Routes)
        {
            if (!RouteStore.WorldPoints(r, MRUKRooms(), pts) || pts.Count == 0) continue;
            float tol = RayPick + RouteStyle.Diameter(r) / 2f;
            for (int i = 0; i < pts.Count; i++)
            {
                float dd = i + 1 < pts.Count ? SegSegDist(o, e, pts[i], pts[i + 1]) : SegSegDist(o, e, pts[i], pts[i]);
                if (dd < tol && dd < dist) { dist = dd; best = r; points = new List<Vector3>(pts); }
            }
        }
        return best;
    }
#endif

    void SetKind(RouteKind k)
    {
        kind = k; diaIdx = 0;
        PlayerPrefs.SetInt(KindPref, (int)k); PlayerPrefs.Save();
        var r = Drawing;
        if (r != null) { string id = r.id; float d = Radius * 2f; RouteStore.Apply(f => { var x = f.routes.First(q => q.id == id); x.kind = k; x.diameter = d; }); }
        RefreshPanel();
    }

    void CycleDiameter()
    {
        diaIdx = (diaIdx + 1) % RouteStyle.Diameters(kind).Length;
        var r = Drawing;
        if (r != null) { string id = r.id; float d = Radius * 2f; RouteStore.Apply(f => f.routes.First(q => q.id == id).diameter = d); }
        RefreshPanel();
    }

    /// <summary>What the end of the route (being drawn, else the last one) leads to.</summary>
    void CycleLabel()
    {
        var r = Drawing ?? RouteStore.Routes.LastOrDefault();
        if (r == null) { uiLog?.AddLog("Routes: draw a route first."); return; }
        labelIdx = (Array.IndexOf(RouteStyle.EndLabels, r.endLabel ?? "") + 1) % RouteStyle.EndLabels.Length;
        string id = r.id, label = RouteStyle.EndLabels[labelIdx];
        RouteStore.Apply(f => f.routes.First(q => q.id == id).endLabel = label);
        RefreshPanel();
    }

    void OnUndo()
    {
        if (!RouteStore.Undo()) { uiLog?.AddLog("Routes: nothing to undo."); return; }
        if (Drawing == null) drawingId = null;
        RefreshPanel();
    }

    void OnRoutesChanged() { if (wasActive) RefreshPanel(); }

    // ---------- preview ----------

    void UpdatePreview()
    {
        if (!hover.HasValue) { ShowPreview(false); return; }
        EnsurePreview();
        bool snapped = false;
        Vector3 c = markerMode ? hover.Value + hoverNormal * 0.01f : Candidate(out snapped);
        Color col = deleteMode ? new Color(1f, 0.25f, 0.25f) : RouteStyle.Color(kind);
        cursor.SetActive(true);
        cursor.transform.position = ToView(c);
        float s = frameIsWalk ? Mathf.Max(0.01f, frameRoot.lossyScale.x) : 1f;
        cursor.transform.localScale = Vector3.one * Mathf.Max(0.012f, Radius * 2.6f) * s * (snapped ? 1.5f : 1f);
        cursorMat.color = snapped ? Color.white : col;

        if (!deleteMode && !markerMode && LastPoint(out var last))
        {
            preview.gameObject.SetActive(true);
            preview.positionCount = 2;
            preview.SetPosition(0, ToView(last)); preview.SetPosition(1, ToView(c));
            preview.startColor = preview.endColor = new Color(col.r, col.g, col.b, 0.9f);
            preview.widthMultiplier = Mathf.Max(0.004f, Radius) * s;
        }
        else preview.gameObject.SetActive(false);

#if META_XR_SDK_INSTALLED
        if ((deleteMode || joinMode) && NearestRoute(out var pts, out _) != null)
        {
            highlight.gameObject.SetActive(true);
            highlight.positionCount = pts.Count;
            for (int i = 0; i < pts.Count; i++) highlight.SetPosition(i, ToView(pts[i]));
            highlight.startColor = highlight.endColor = joinMode ? new Color(0.3f, 1f, 0.4f, 0.9f) : new Color(1f, 0.2f, 0.2f, 0.9f);
            highlight.widthMultiplier = 0.03f * s;
        }
        else highlight.gameObject.SetActive(false);
#endif
    }

    void EnsurePreview()
    {
        if (!lineMat) lineMat = new Material(Shader.Find("Sprites/Default")) { name = "RoutePreview" };
        if (!preview) preview = NewLine("RoutePreview");
        if (!highlight) highlight = NewLine("RouteDeletePick");
        if (!cursor)
        {
            cursor = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            cursor.name = "RouteCursor";
            Destroy(cursor.GetComponent<Collider>());
            cursorMat = new Material(Shader.Find("Sprites/Default")) { name = "RouteCursor" };
            cursor.GetComponent<MeshRenderer>().sharedMaterial = cursorMat;
        }
    }

    LineRenderer NewLine(string name)
    {
        var lr = new GameObject(name).AddComponent<LineRenderer>();
        lr.sharedMaterial = lineMat;
        lr.useWorldSpace = true;
        lr.numCapVertices = 3;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    void ShowPreview(bool on)
    {
        if (preview) preview.gameObject.SetActive(on);
        if (highlight) highlight.gameObject.SetActive(on);
        if (cursor) cursor.SetActive(on);
    }

    // ---------- panel ----------

    void BuildPanel()
    {
        ClosePanel();
        if (!head) return;
        panel = XRUi.CreateWorldCanvas("RoutePanel", new Vector2(PW, PH), PScale);
        var t = panel.transform;
        panelDrag = PanelDrag.Attach(panel, PW, PH);
        // back where you last put it (this session), if that is still near you
        if (savedPanel.HasValue && Vector3.Distance(savedPanel.Value.position, head.position) < 2.5f)
        { t.SetPositionAndRotation(savedPanel.Value.position, savedPanel.Value.rotation); panelPlaced = true; }
        else PlacePanel();
        XRUi.CreatePanel(t, "Background", XRUi.PanelColor, 0, 0, PW, PH);

        float x0 = 16, fullW = PW - 32, y = 8;
        titleText = XRUi.CreateText(t, "Title", "", 23, TextAlignmentOptions.MidlineLeft, x0, y, fullW, 40, XRUi.TextColor, FontStyles.Bold);
        y += 44;
        kindButtons.Clear();
        int n = RouteStyle.Kinds.Length;
        float wk = (fullW - Gap * (n - 1)) / n;
        for (int i = 0; i < n; i++)
        {
            var k = RouteStyle.Kinds[i];
            kindButtons[k] = XRUi.CreateButton(t, RouteStyle.Name(k), x0 + i * (wk + Gap), y, wk, RowH, () => SetKind(k), 17);
        }
        y += RowH + Gap;
        float w3 = (fullW - Gap * 2) / 3f;
        diaButton = XRUi.CreateButton(t, "", x0, y, w3, RowH, CycleDiameter, 19);
        labelButton = XRUi.CreateButton(t, "", x0 + w3 + Gap, y, w3, RowH, CycleLabel, 17);
        finishButton = XRUi.CreateButton(t, "Finish route", x0 + (w3 + Gap) * 2, y, w3, RowH, Finish, 20);
        y += RowH + Gap;
        modeButton = XRUi.CreateButton(t, "", x0, y, w3, RowH, () => { markerMode = !markerMode; deleteMode = false; if (markerMode) Finish(); RefreshPanel(); }, 18);
        markerButton = XRUi.CreateButton(t, "", x0 + w3 + Gap, y, w3, RowH, () =>
        {
            markerIdx = (markerIdx + 1) % RouteStyle.MarkerTypes.Length;
            PlayerPrefs.SetInt(MarkerPref, markerIdx); PlayerPrefs.Save();
            markerMode = true; RefreshPanel();
        }, 18);
        acceptButton = XRUi.CreateButton(t, "", x0 + (w3 + Gap) * 2, y, w3, RowH, AcceptProposals, 16);
        y += RowH + Gap;
        float w4 = (fullW - Gap * 3) / 4f;
        var undo = XRUi.CreateButton(t, "Undo", x0, y, w4, RowH, OnUndo, 20);
        XRUi.SetTint(undo, XRUi.ButtonToggleColor);
        deleteButton = XRUi.CreateButton(t, "Delete...", x0 + (w4 + Gap), y, w4, RowH, () => { deleteMode = !deleteMode; if (deleteMode) { drawingId = null; joinMode = false; } RefreshPanel(); }, 19);
        joinButton = XRUi.CreateButton(t, "Join...", x0 + (w4 + Gap) * 2, y, w4, RowH, () =>
        {
            joinMode = !joinMode; joinFirstId = null;
            if (joinMode) { Finish(); deleteMode = false; markerMode = false; }
            RefreshPanel();
        }, 19);
        XRUi.CreateButton(t, "Close", x0 + (w4 + Gap) * 3, y, w4, RowH, () => { if (editMode != null && editMode.IsOn) editMode.Toggle(); }, 20);
        y += RowH + 6;
        hintText = XRUi.CreateText(t, "Hint", "", 16, TextAlignmentOptions.TopLeft, x0, y, fullW, PH - y - 4, XRUi.MutedText);
        RefreshPanel();
    }

    void PlacePanel()
    {
        if (!panel || !head || (panelDrag && panelDrag.Moved)) return;
        XRUi.PlaceInFront(panel.transform, head, 0.6f, 0.12f, 0.2f);
        panelPlaced = true;
    }

    void LateUpdate()
    {
        if (panel && head && !(panelDrag && panelDrag.Moved) && (!panelPlaced || XRUi.LeftBehind(panel.transform, head))) PlacePanel();
    }

    void RefreshPanel()
    {
        if (!panel) return;
        var r = Drawing;
        int count = RouteStore.Routes.Count;
        titleText.text = r != null
            ? $"Drawing: {RouteStyle.Name(r.kind)} - {r.localPoints.Count} point(s)"
            : deleteMode ? "Delete: trigger on a route or a box"
            : joinMode ? (joinFirstId == null ? "Join: trigger on the end of the first route" : "Join: now the end of the second route")
            : markerMode ? $"Boxes: {RouteStore.Current.markers.Count}   -   next: {RouteStyle.Name(kind)} {RouteStyle.MarkerTypes[markerIdx]}"
            : $"Routes: {count}   -   next: {RouteStyle.Name(kind)}";
        foreach (var kv in kindButtons)
            XRUi.SetTint(kv.Value, kv.Key == kind ? Darken(RouteStyle.Color(kv.Key)) : XRUi.ButtonColor);
        XRUi.SetLabel(diaButton, $"Size: {Mathf.RoundToInt(Radius * 2000f)} mm");
        var lr = r ?? RouteStore.Routes.LastOrDefault();
        string end = lr != null && !string.IsNullOrEmpty(lr.endLabel) ? lr.endLabel : "-";
        XRUi.SetLabel(labelButton, $"Leads to: {end}");
        XRUi.SetTint(finishButton, r != null ? XRUi.ButtonOnColor : XRUi.ButtonDisabled);
        XRUi.SetTint(deleteButton, deleteMode ? XRUi.ButtonDangerColor : XRUi.ButtonToggleColor);
        XRUi.SetTint(joinButton, joinMode ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        XRUi.SetLabel(modeButton, markerMode ? "Placing: boxes" : "Placing: routes");
        XRUi.SetTint(modeButton, markerMode ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        XRUi.SetLabel(markerButton, $"Box type: {RouteStyle.MarkerTypes[markerIdx]}");
        int prop = RouteStore.Routes.Count(x => x.proposed);
        XRUi.SetLabel(acceptButton, prop > 0 ? $"Keep {prop} from photos" : "No photo proposals");
        XRUi.SetTint(acceptButton, prop > 0 ? XRUi.ButtonOnColor : XRUi.ButtonDisabled);
        SetHint(r != null
            ? "Trigger on every bend and on the end. A point near another route joins it. Finish route (or trigger twice on the same spot) when done."
            : deleteMode ? "Point at the route or box to delete and pull the trigger. Undo brings it back."
            : joinMode ? "Joins two routes into one - e.g. a pipe drawn in two rooms on both sides of a wall: trigger near the end of one, then near the end of the other. Undo splits them again."
            : markerMode ? "Trigger on the wall/floor where the box, socket or outlet is. Colour = the kind picked above."
            : "Pick the kind, then trigger on the start of a pipe or cable. Pipes on the floor lie on it; chases in walls show on the face. A route that ends right behind a wall where another of the same kind ends is joined through the wall.");
    }

    static Color Darken(Color c) => new Color(c.r * 0.75f, c.g * 0.75f, c.b * 0.75f, 1f);

    void SetHint(string s) { if (hintText) hintText.text = s; }

    static Pose? savedPanel;

    void ClosePanel()
    {
        if (panel && panelDrag && panelDrag.Moved) savedPanel = new Pose(panel.transform.position, panel.transform.rotation);
        if (panel) Destroy(panel.gameObject);
        panel = null;
    }

    void Haptic(float strength)
    {
        OVRInput.SetControllerVibration(0.25f, strength, OVRInput.Controller.RTouch);
        Invoke(nameof(StopHaptic), 0.06f);
    }
    void StopHaptic() => OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);
}
