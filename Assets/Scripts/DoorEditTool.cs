using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Edit mode "Doors" tool: point at a door (in the walk-through or on the Dollhouse) and pull the trigger to pick
/// it; a panel then sets its type, hinge side and the room it opens into. Every door that already has settings
/// shows its open leaf and swing arc (green), the door under the ray is outlined yellow, the picked one orange.
/// All geometry is worked out in the scan's own world frame (DoorCatalog) and mapped onto whichever model is
/// showing, so the same code serves the 1:1 walk-through and the scaled Dollhouse.
/// </summary>
public class DoorEditTool : MonoBehaviour
{
    public XRMenu uiLog;
    public EditModeController editMode;
    public WalkThroughMode walk;
    public DollHouseVisualizer dollhouse;

    static readonly Color HoverColor = new Color(1f, 0.9f, 0.2f, 0.95f);
    static readonly Color SelectColor = new Color(1f, 0.55f, 0.1f, 1f);
    static readonly Color SwingColor = new Color(0.3f, 0.95f, 0.45f, 0.95f);
    static readonly Color WallColor = new Color(1f, 0.3f, 0.3f, 0.95f);

    Transform frameRoot; float frameYaw; Vector3 frameCenter; bool frameIsWalk;
    List<MRUKRoom> rooms = new List<MRUKRoom>();
    List<DoorInfo> doors = new List<DoorInfo>();
    DoorInfo hovered, selected;
    Transform head, rightHand;

    readonly List<LineRenderer> pool = new List<LineRenderer>();
    int used;
    Material lineMat;
    readonly List<Vector3> pts = new List<Vector3>();

    Canvas panel;
    TMP_Text panelTitle;
    readonly Button[] kindButtons = new Button[6];
    readonly Button[] hingeButtons = new Button[2];
    readonly Button[] intoButtons = new Button[2];
    readonly string[] intoIds = new string[2];

    void Start()
    {
        if (editMode != null) editMode.TriggerPressed += OnTrigger;
        HouseEditsStore.Changed += RefreshPanel;
    }

    void OnDestroy()
    {
        if (editMode != null) editMode.TriggerPressed -= OnTrigger;
        HouseEditsStore.Changed -= RefreshPanel;
        ClosePanel();
        foreach (var lr in pool) if (lr) Destroy(lr.gameObject);
        if (lineMat) Destroy(lineMat);
    }

    bool Active => editMode != null && editMode.IsOn && editMode.CurrentTool == EditModeController.Tool.Doors;

    void Update()
    {
        used = 0;
        if (!Active || !ResolveFrame())
        {
            hovered = null;
            if (panel) ClosePanel();
            selected = null;
            FinishLines();
            return;
        }

        hovered = PickDoor();
        foreach (var d in doors)
        {
            var e = DoorCatalog.FindEdit(HouseEditsStore.Current, d, rooms);
            if (e != null && d.isWindow && e.kind != DoorKind.Wall) DrawWindow(d, e);
            else if (e != null) DrawSwing(d, e);
        }
        if (hovered != null && hovered != selected) DrawOutline(hovered, HoverColor);
        if (selected != null) DrawOutline(selected, SelectColor);
        UpdateNewDoorDrag();
        if (wallPanelOpen) Rect3(gapP, gapQ, gapH, SelectColor);
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
            // A different model instance (entered/left the walk-through, Dollhouse rebuilt, scan switched).
            frameRoot = r;
#if META_XR_SDK_INSTALLED
            rooms = MRUK.Instance != null ? MRUKDataProcessor.GetValidRooms(MRUK.Instance) : new List<MRUKRoom>();
#endif
            doors = DoorCatalog.Build(rooms);
            selected = null; hovered = null;
            ClosePanel();
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

    Vector3 ScanToWorld(Vector3 p) => frameRoot.TransformPoint(Quaternion.Euler(0, frameYaw, 0) * (p - frameCenter));
    Vector3 WorldToScan(Vector3 p) => Quaternion.Inverse(Quaternion.Euler(0, frameYaw, 0)) * frameRoot.InverseTransformPoint(p) + frameCenter;

    DoorInfo PickDoor()
    {
        Vector3 o = WorldToScan(rightHand.position);
        Vector3 dir = WorldToScan(rightHand.position + rightHand.forward) - o;
        DoorInfo best = null; float bestT = float.MaxValue;
        foreach (var d in doors)
        {
            float denom = Vector3.Dot(dir, d.normal);
            if (Mathf.Abs(denom) < 1e-5f) continue;
            float t = Vector3.Dot(d.center - o, d.normal) / denom;
            if (t <= 0f || t >= bestT) continue;
            Vector3 hit = o + dir * t - d.center;
            if (Mathf.Abs(Vector3.Dot(hit, d.right)) > d.width / 2f + 0.05f || Mathf.Abs(hit.y) > d.height / 2f + 0.05f) continue;
            best = d; bestT = t;
        }
        // In the walk-through a wall in front of the door hides it - don't pick doors through walls.
        if (best != null && frameIsWalk)
        {
            float doorDist = Vector3.Distance(rightHand.position, ScanToWorld(o + dir * bestT));
            if (walk.RaycastModel(new Ray(rightHand.position, rightHand.forward), out var wallHit) && wallHit.distance < doorDist - 0.2f)
                return null;
        }
        return best;
    }

    void OnTrigger()
    {
        if (!Active) return;
        if (!frameRoot) { uiLog?.AddLog("Doors: open the Dollhouse or Walk first."); return; }
        selected = hovered;
        if (selected == null)
        {
            // no door there: on a bare wall, offer a new doorway (one the Space Setup missed)
            if (WallUnderRay(out var room, out var hit, out var normal))
            {
                Vector3 o = WorldToScan(rightHand.position);
                aim = new Ray(o, (WorldToScan(rightHand.position + rightHand.forward) - o).normalized); // for "Missing wall"
                OpenAddPanel(room, hit, normal);
            }
            else ClosePanel();
            return;
        }
        if (DoorCatalog.FindEdit(HouseEditsStore.Current, selected, rooms) == null) GuessAndOpen(selected);
        else OpenPanel();
    }

    /// <summary>First pick of an unset door/window: save a guess straight away (one undo step), then adjust.</summary>
    async void GuessAndOpen(DoorInfo d)
    {
        await editMode.Commit(edits =>
        {
            if (DoorCatalog.FindEdit(edits, d, rooms) == null) edits.doors.Add(GuessedEdit(d));
        });
        if (selected == d) OpenPanel();
        uiLog?.AddLog($"{(d.isWindow ? "Window" : "Door")}: guessed - adjust if it's different.");
    }

    DoorEdit GuessedEdit(DoorInfo d)
    {
        var e = DoorCatalog.NewEdit(d);
        DoorCatalog.Guess(d, e, rooms, ViewerSideRoomId(d));
        return e;
    }

    // ---------- a new doorway on a bare wall ----------

    static readonly float[] NewDoorWidths = { 0.6f, 0.7f, 0.8f, 0.9f, 1.0f, 1.2f, 1.6f };
    int newDoorWidthIdx = 2;
    MRUKRoom newDoorRoom; Vector3 newDoorPoint, newDoorNormal; bool adding;
    /// <summary>The add panel is open: a further trigger hold drags the point along the wall instead of
    /// jumping to a brand new one, and a live preview box shows where the doorway will land.</summary>
    bool addPanelOpen, draggingNewDoor;
    Ray aim;                                  // where the ray pointed when the add panel opened (scan frame)
    bool wallPanelOpen;                       // the "missing wall" panel: a preview of the gap, Add wall / Cancel
    MRUKRoom gapRoom; Vector3 gapP, gapQ; float gapH;
    TMP_Text addTitle;

    /// <summary>The wall (scan frame) under the ray: the nearest wall face of any room, with its outward normal.</summary>
    bool WallUnderRay(out MRUKRoom room, out Vector3 point, out Vector3 normal)
    {
        room = null; point = default; normal = Vector3.forward;
        Vector3 o = WorldToScan(rightHand.position);
        Vector3 dir = (WorldToScan(rightHand.position + rightHand.forward) - o).normalized;
        var ray = new Ray(o, dir);
        float best = 30f;
        var walls = new LabelFilter(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE);
        foreach (var r in rooms)
            if (r != null && r.Raycast(ray, best, walls, out RaycastHit h, out _) && h.distance < best) { best = h.distance; room = r; point = h.point; normal = h.normal; }
        return room != null;
    }

    /// <summary>While the add panel is open: hold the trigger and move the hand to slide the doorway along
    /// the same wall before confirming - lets you see it before it's final instead of only guessing once.</summary>
    void UpdateNewDoorDrag()
    {
        if (!addPanelOpen) { draggingNewDoor = false; return; }
        bool held = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
        if (held && WallUnderRay(out var r, out var p, out var n) && r == newDoorRoom)
        {
            draggingNewDoor = true;
            newDoorPoint = p; newDoorNormal = n;
        }
        else if (!held) draggingNewDoor = false;
        DrawNewDoorPreview();
    }

    void DrawNewDoorPreview()
    {
        float floorY = newDoorRoom.FloorAnchors.Count > 0 ? newDoorRoom.FloorAnchors[0].transform.position.y : newDoorPoint.y - SceneJsonEdit.DefaultDoorHeight;
        Vector3 right = Vector3.Cross(Vector3.up, newDoorNormal).normalized;
        Vector3 baseAtFloor = new Vector3(newDoorPoint.x, floorY, newDoorPoint.z);
        float w = NewDoorWidths[newDoorWidthIdx];
        Rect3(baseAtFloor - right * (w / 2f), baseAtFloor + right * (w / 2f), SceneJsonEdit.DefaultDoorHeight, draggingNewDoor ? SelectColor : HoverColor);
    }

    void OpenAddPanel(MRUKRoom room, Vector3 point, Vector3 normal)
    {
        ClosePanel();
        newDoorRoom = room; newDoorPoint = point; newDoorNormal = normal; addPanelOpen = true;
        const float ph = 372f;
        panel = XRUi.CreateWorldCanvas("NewDoorPanel", new Vector2(PW, ph), PScale);
        var t = panel.transform;
        if (userPlaced) t.SetPositionAndRotation(userPos, userRot);
        else
        {
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            Vector3 pos = head.position + fwd * 0.6f - Vector3.up * 0.18f;
            t.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head.position, Vector3.up));
        }
        panelDrag = PanelDrag.Attach(panel, PW, ph);
        XRUi.CreatePanel(t, "Background", XRUi.PanelColor, 0, 0, PW, ph);
        addTitle = XRUi.CreateText(t, "Title", "", 24, TextAlignmentOptions.MidlineLeft, 20, 8, PW - 40, 44, XRUi.TextColor, FontStyles.Bold);
        float x0 = 20, rowH = 64, gap = 8, y = 60;
        XRUi.CreateText(t, "WidthLbl", "Width", 20, TextAlignmentOptions.MidlineLeft, x0, y, 110, rowH, XRUi.MutedText);
        float bw = (PW - 130 - 20 - gap * 3) / 4f;
        XRUi.CreateButton(t, "-", 130, y, bw, rowH, () => { newDoorWidthIdx = Mathf.Max(0, newDoorWidthIdx - 1); RefreshAddPanel(); }, 30);
        XRUi.CreateButton(t, "+", 130 + bw + gap, y, bw, rowH, () => { newDoorWidthIdx = Mathf.Min(NewDoorWidths.Length - 1, newDoorWidthIdx + 1); RefreshAddPanel(); }, 30);
        y += rowH + gap;
        XRUi.CreateText(t, "Hint", "A doorway the scan missed, standing on the floor (height 197 cm). Hold the trigger and move your hand to slide it along the wall - the box shows where it'll land. It becomes part of this saved scan; then set its type like any door. Undo: the scan file is backed up (*.bak_doors).",
            16, TextAlignmentOptions.TopLeft, x0, y, PW - 40, 80, XRUi.MutedText);
        y += 86;
        var add = XRUi.CreateButton(t, "Add doorway", x0, y, (PW - 40 - gap) / 2f, rowH, AddDoorway, 22);
        XRUi.SetTint(add, XRUi.ButtonOnColor);
        XRUi.CreateButton(t, "Cancel", x0 + (PW - 40 - gap) / 2f + gap, y, (PW - 40 - gap) / 2f, rowH, ClosePanel, 22);
        y += rowH + gap;
        XRUi.CreateButton(t, "Missing wall instead", x0, y, PW - 40, rowH, ShowMissingWall, 22);
        RefreshAddPanel();
    }

    // ---------- a wall the scan missed ----------

    /// <summary>Finds the gap in the room's walls nearest to where you pointed (a mirror the Quest couldn't scan) and
    /// shows it; Add wall then draws it in, between the loose wall ends, as high as the walls beside it.</summary>
    void ShowMissingWall()
    {
#if META_XR_SDK_INSTALLED
        if (!MissingWall.Find(rooms, aim, out var room, out var p, out var q, out float h))
        {
            uiLog?.AddLog("<color=orange>Missing wall: no gap in the walls where you pointed - close the panel, point at the place where the wall should be and pull the trigger again.</color>");
            return;
        }
        OpenWallPanel(room, p, q, h);
#endif
    }

    void OpenWallPanel(MRUKRoom room, Vector3 p, Vector3 q, float h)
    {
        ClosePanel();
        gapRoom = room; gapP = p; gapQ = q; gapH = h; wallPanelOpen = true;
        const float ph = 232f;
        panel = XRUi.CreateWorldCanvas("NewWallPanel", new Vector2(PW, ph), PScale);
        var t = panel.transform;
        if (userPlaced) t.SetPositionAndRotation(userPos, userRot);
        else
        {
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            Vector3 pos = head.position + fwd * 0.6f - Vector3.up * 0.18f;
            t.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head.position, Vector3.up));
        }
        panelDrag = PanelDrag.Attach(panel, PW, ph);
        XRUi.CreatePanel(t, "Background", XRUi.PanelColor, 0, 0, PW, ph);
        float len = Vector3.Distance(new Vector3(p.x, 0, p.z), new Vector3(q.x, 0, q.z));
        XRUi.CreateText(t, "Title", $"Missing wall in {RoomLabel(room)}:  {len * 100:0} x {h * 100:0} cm", 24, TextAlignmentOptions.MidlineLeft, 20, 8, PW - 40, 44, XRUi.TextColor, FontStyles.Bold);
        XRUi.CreateText(t, "Hint", "The two loose wall ends in the outline are joined by a new wall, as high as the walls beside it. It becomes part of this saved scan (backed up as *.bak_doors).",
            16, TextAlignmentOptions.TopLeft, 20, 60, PW - 40, 80, XRUi.MutedText);
        float rowH = 64, gap = 8, y = 150;
        var add = XRUi.CreateButton(t, "Add wall", 20, y, (PW - 40 - gap) / 2f, rowH, AddMissingWall, 22);
        XRUi.SetTint(add, XRUi.ButtonOnColor);
        XRUi.CreateButton(t, "Cancel", 20 + (PW - 40 - gap) / 2f + gap, y, (PW - 40 - gap) / 2f, rowH, ClosePanel, 22);
    }

    async void AddMissingWall()
    {
        if (adding || gapRoom == null) return;
        adding = true;
        try
        {
            if (!await editMode.EnsureSavedScan()) return; // into a saved scan's file, never from the live scan
            var room = gapRoom; Vector3 p = gapP, q = gapQ; float h = gapH;
            string scan = HouseEditsStore.CurrentScan;
            string err = SceneJsonEdit.AddWall(scan, room, p, q, h);
            if (err != null) { uiLog?.AddLog($"<color=red>New wall: {err}.</color>"); return; }
            ClosePanel();
            uiLog?.AddLog($"<color=green>Wall added to '{scan}' - reloading the scan...</color>");
            PendingChanges.Mark("edits");
            if (uiLog != null) await uiLog.ReloadAfterScanEdit();
            frameRoot = null;
        }
        finally { adding = false; }
    }

    void RefreshAddPanel()
    {
        if (addTitle) addTitle.text = $"New doorway in {RoomLabel(newDoorRoom)}:  {NewDoorWidths[newDoorWidthIdx] * 100:0} cm";
    }

    async void AddDoorway()
    {
        if (adding || newDoorRoom == null) return;
        adding = true;
        try
        {
            // the doorway goes into the saved scan's own file; never from the live scan (it is not a file yet)
            if (!await editMode.EnsureSavedScan()) return;
            var room = newDoorRoom; Vector3 point = newDoorPoint;
            float w = NewDoorWidths[newDoorWidthIdx];
            string scan = HouseEditsStore.CurrentScan;
            string err = SceneJsonEdit.AddDoor(scan, room, point, w, SceneJsonEdit.DefaultDoorHeight);
            if (err != null) { uiLog?.AddLog($"<color=red>New doorway: {err}.</color>"); return; }
            ClosePanel();
            uiLog?.AddLog($"<color=green>Doorway {w * 100:0} cm added to '{scan}' - reloading the scan...</color>");
            PendingChanges.Mark("edits");
            if (uiLog != null) await uiLog.ReloadAfterScanEdit();
            frameRoot = null;   // the rooms were reloaded: rebuild the door list
        }
        finally { adding = false; }
    }

    // ---------- drawing ----------

    void DrawSwing(DoorInfo d, DoorEdit e)
    {
        DoorCatalog.Swing(d, e, out var hinge, out var swing, out var toOther);
        float w = d.width, h = d.height;
        switch (e.kind)
        {
            case DoorKind.Single: Leaf(hinge, toOther, swing, w, h); break;
            case DoorKind.Double:
                Leaf(hinge, toOther, swing, w / 2f, h);
                Leaf(hinge + toOther * w, -toOther, swing, w / 2f, h);
                break;
            case DoorKind.Sliding:
            {
                // Leaf parked beside the opening on the hinge side, just off the wall on the swing side.
                Vector3 a = hinge + swing * 0.06f, b = a - toOther * w;
                Rect3(a, b, h, SwingColor);
                break;
            }
            case DoorKind.Opening:
            case DoorKind.Fixed:
                Rect3(hinge, hinge + toOther * w, h, SwingColor);
                break;
            case DoorKind.Garage:
            {
                // Roller door: its outline plus a few slats, and the roll box above.
                Vector3 a = hinge, b = hinge + toOther * w;
                Rect3(a, b, h, SwingColor);
                for (int k = 1; k < 4; k++)
                {
                    pts.Clear(); pts.Add(a + Vector3.up * (h * k / 4f)); pts.Add(b + Vector3.up * (h * k / 4f)); Emit(SwingColor, false);
                }
                Rect3(a + Vector3.up * h + swing * 0.15f, b + Vector3.up * h + swing * 0.15f, 0.3f, SwingColor);
                break;
            }
            case DoorKind.Tilt:
            {
                // Bottom-hung: top tipped ~12 cm into the room.
                Vector3 a = hinge, b = hinge + toOther * w, top = Vector3.up * h + swing * 0.12f;
                pts.Clear(); pts.Add(a); pts.Add(b); pts.Add(b + top); pts.Add(a + top);
                Emit(SwingColor, true);
                break;
            }
            case DoorKind.Wall:
            {
                // Walled up: a red frame with a cross, so it can still be found and reopened.
                // Drawn just in front of the (now solid) wall on your side, else the wall would hide it.
                Vector3 eye = WorldToScan(head.position);
                Vector3 off = d.normal * (Vector3.Dot(eye - d.center, d.normal) >= 0f ? 0.25f : -0.25f);
                Vector3 a = hinge + off, b = hinge + toOther * w + off, up = Vector3.up * h;
                Rect3(a, b, h, WallColor);
                pts.Clear(); pts.Add(a + Vector3.up * 0.01f); pts.Add(b + up); Emit(WallColor, false);
                pts.Clear(); pts.Add(b + Vector3.up * 0.01f); pts.Add(a + up); Emit(WallColor, false);
                break;
            }
        }
    }

    /// <summary>A leaf open at 90 degrees plus its swing arc on the floor.</summary>
    void Leaf(Vector3 hinge, Vector3 along, Vector3 swing, float r, float h)
    {
        pts.Clear();
        Vector3 lift = Vector3.up * 0.01f;
        const int seg = 12;
        for (int k = 0; k <= seg; k++)
        {
            float a = k / (float)seg * Mathf.PI / 2f;
            pts.Add(hinge + lift + along * (Mathf.Cos(a) * r) + swing * (Mathf.Sin(a) * r));
        }
        Vector3 tip = hinge + lift + swing * r;
        pts.Add(tip + Vector3.up * h);
        pts.Add(hinge + lift + Vector3.up * h);
        pts.Add(hinge + lift);
        pts.Add(tip);
        Emit(SwingColor, false);
    }

    void Rect3(Vector3 a, Vector3 b, float h, Color c)
    {
        pts.Clear();
        Vector3 lift = Vector3.up * 0.01f;
        pts.Add(a + lift); pts.Add(b + lift); pts.Add(b + Vector3.up * h); pts.Add(a + Vector3.up * h);
        Emit(c, true);
    }

    void DrawOutline(DoorInfo d, Color c)
    {
        Vector3 bl = d.center - d.right * (d.width / 2f) - Vector3.up * (d.height / 2f);
        Rect3(bl, bl + d.right * d.width, d.height, c);
    }

    /// <summary>Draws pts (scan frame) with the next pooled line, mapped onto the current model.</summary>
    void Emit(Color c, bool loop)
    {
        if (used >= pool.Count) pool.Add(NewLine());
        var lr = pool[used++];
        lr.gameObject.SetActive(true);
        lr.loop = loop;
        lr.positionCount = pts.Count;
        for (int i = 0; i < pts.Count; i++) lr.SetPosition(i, ScanToWorld(pts[i]));
        lr.startColor = lr.endColor = c;
        // ~1.2 cm in the walk-through, thinner (but never invisible) on the scaled-down Dollhouse.
        lr.widthMultiplier = Mathf.Max(0.0025f, 0.012f * frameRoot.lossyScale.x);
    }

    LineRenderer NewLine()
    {
        if (!lineMat) lineMat = new Material(Shader.Find("Sprites/Default"));
        var go = new GameObject("DoorEditLine");
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = lineMat;
        lr.useWorldSpace = true;
        lr.numCapVertices = 2;
        lr.numCornerVertices = 2;
        return lr;
    }

    void FinishLines()
    {
        for (int i = used; i < pool.Count; i++) if (pool[i] && pool[i].gameObject.activeSelf) pool[i].gameObject.SetActive(false);
    }

    // ---------- panel ----------

    const float PW = 640f, PH = 544f, PScale = 0.0006f;

    static readonly (string label, DoorKind kind)[] DoorKinds =
    {
        ("Single", DoorKind.Single), ("Double", DoorKind.Double), ("Sliding", DoorKind.Sliding),
        ("Garage", DoorKind.Garage), ("Opening", DoorKind.Opening), ("Wall", DoorKind.Wall),
    };
    static readonly (string label, DoorKind kind)[] WindowKinds =
    {
        ("Fixed", DoorKind.Fixed), ("Casement", DoorKind.Single), ("Double", DoorKind.Double),
        ("Tilt", DoorKind.Tilt), ("Sliding", DoorKind.Sliding), ("Wall", DoorKind.Wall),
    };
    readonly DoorKind[] kindValues = new DoorKind[6];

    // ---------- windows: parts ----------

    static readonly DoorKind[] PartCycle = { DoorKind.Fixed, DoorKind.TiltTurn, DoorKind.Single, DoorKind.Tilt, DoorKind.Sliding };

    static string PartLabel(DoorKind k) => k switch
    {
        DoorKind.Fixed => "Fixed",
        DoorKind.TiltTurn => "Tilt-turn",
        DoorKind.Single => "Casement",
        DoorKind.Tilt => "Tilt",
        DoorKind.Sliding => "Sliding (HS)",
        _ => k.ToString(),
    };

    /// <summary>Window elevation symbols per part, drawn just in front of the window on your side: the usual
    /// triangle pointing at the hinge side (casement), at the bottom (tilt), both (tilt-turn), an arrow for sliding.</summary>
    void DrawWindow(DoorInfo d, DoorEdit e)
    {
        DoorCatalog.LeftFrame(d, e, out var left, out var toRight, out _);
        Vector3 eye = WorldToScan(head.position);
        Vector3 off = d.normal * (Vector3.Dot(eye - d.center, d.normal) >= 0f ? 0.1f : -0.1f);
        float W = d.width, h = d.height;
        Vector3 up = Vector3.up * h;
        foreach (var (s, f0, f1) in DoorCatalog.Sections(e))
        {
            Vector3 a = left + toRight * (W * f0) + off, b = left + toRight * (W * f1) + off;
            Rect3(a, b, h, SwingColor);
            bool hl = s.hinge == HingeSide.Left;
            Vector3 hingeMid = (hl ? a : b) + up * 0.5f, latchBot = hl ? b : a, latchTop = latchBot + up;
            Vector3 botMid = (a + b) / 2f;
            switch (s.kind)
            {
                case DoorKind.Single:
                case DoorKind.TiltTurn:
                    pts.Clear(); pts.Add(latchTop); pts.Add(hingeMid); pts.Add(latchBot); Emit(SwingColor, false);
                    if (s.kind == DoorKind.TiltTurn) { pts.Clear(); pts.Add(a + up); pts.Add(botMid); pts.Add(b + up); Emit(SwingColor, false); }
                    break;
                case DoorKind.Tilt:
                    pts.Clear(); pts.Add(a + up); pts.Add(botMid); pts.Add(b + up); Emit(SwingColor, false);
                    break;
                case DoorKind.Sliding:
                {
                    Vector3 mid = (a + b) / 2f + up * 0.5f, dir = (hl ? -toRight : toRight) * (W * (f1 - f0) * 0.35f);
                    pts.Clear(); pts.Add(mid - dir); pts.Add(mid + dir); Emit(SwingColor, false);
                    Vector3 tip = mid + dir, back = -dir.normalized * 0.12f;
                    pts.Clear(); pts.Add(tip + back + Vector3.up * 0.08f); pts.Add(tip); pts.Add(tip + back - Vector3.up * 0.08f); Emit(SwingColor, false);
                    break;
                }
            }
        }
    }

    /// <summary>Applies a change to a window's parts (creating its edit / turning old single-kind settings into
    /// parts first), saves, and rebuilds the panel (its rows depend on the parts).</summary>
    async void ChangeParts(System.Action<List<SectionEdit>> mutate)
    {
        var d = selected;
        if (d == null || editMode == null) return;
        await editMode.Commit(edits =>
        {
            var e = DoorCatalog.FindEdit(edits, d, rooms);
            if (e == null) { e = GuessedEdit(d); edits.doors.Add(e); }
            if (e.sections == null || e.sections.Count == 0)
                e.sections = e.kind == DoorKind.Wall ? new List<SectionEdit> { new SectionEdit() }
                    : DoorCatalog.Sections(e).Select(x => new SectionEdit { kind = x.s.kind, hinge = x.s.hinge }).ToList();
            mutate(e.sections);
            e.kind = DoorKind.Single; // "a window made of parts" (not walled up)
        });
        OpenPanel();
    }

    void OpenWindowPanel(DoorInfo d, Transform t, float pw)
    {
        var e = DoorCatalog.FindEdit(HouseEditsStore.Current, d, rooms);
        bool walled = e != null && e.kind == DoorKind.Wall;
        var parts = e != null && !walled ? DoorCatalog.Sections(e).Select(x => x.s).ToList() : new List<SectionEdit> { new SectionEdit() };
        float x0 = 20, rowH = 62, gap = 8, y = 60;

        XRUi.CreateText(t, "PartsLbl", "Parts", 20, TextAlignmentOptions.MidlineLeft, x0, y, 110, rowH, XRUi.MutedText);
        float bw = (pw - 130 - 20 - gap * 3) / 4f;
        for (int i = 1; i <= 4; i++)
        {
            int n = i;
            var btn = XRUi.CreateButton(t, n.ToString(), 130 + (i - 1) * (bw + gap), y, bw, rowH, () => ChangeParts(list =>
            {
                while (list.Count < n) list.Add(new SectionEdit());
                while (list.Count > n) list.RemoveAt(list.Count - 1);
            }), 24);
            XRUi.SetTint(btn, !walled && e != null && parts.Count == n ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        }
        y += rowH + gap;

        float tw = (pw - 130 - 20 - gap) * 0.6f, hw = (pw - 130 - 20 - gap) - tw;
        for (int i = 0; i < parts.Count; i++)
        {
            int idx = i;
            var s = parts[i];
            XRUi.CreateText(t, "Part" + i, $"Part {i + 1}", 20, TextAlignmentOptions.MidlineLeft, x0, y, 110, rowH, XRUi.TextColor);
            XRUi.CreateButton(t, PartLabel(s.kind), 130, y, tw, rowH, () => ChangeParts(list =>
            {
                var cur = list[idx].kind;
                int k = System.Array.IndexOf(PartCycle, cur);
                list[idx].kind = PartCycle[(k + 1) % PartCycle.Length];
            }), 20);
            if (s.kind != DoorKind.Fixed && s.kind != DoorKind.Tilt)
            {
                string side = s.kind == DoorKind.Sliding ? (s.hinge == HingeSide.Left ? "slides <" : "slides >") : (s.hinge == HingeSide.Left ? "hinge L" : "hinge R");
                XRUi.CreateButton(t, side, 130 + tw + gap, y, hw, rowH, () => ChangeParts(list =>
                    list[idx].hinge = list[idx].hinge == HingeSide.Left ? HingeSide.Right : HingeSide.Left), 20);
            }
            y += rowH + gap;
        }

        XRUi.CreateText(t, "IntoLbl", "Opens into", 20, TextAlignmentOptions.MidlineLeft, x0, y, 110, rowH, XRUi.MutedText);
        float bw2 = (pw - 130 - 20 - gap) / 2f;
        intoIds[0] = DoorCatalog.RoomId(d.roomFront); intoIds[1] = DoorCatalog.RoomId(d.roomBack);
        string id0 = intoIds[0], id1 = intoIds[1];
        intoButtons[0] = XRUi.CreateButton(t, RoomLabel(d.roomFront), 130, y, bw2, rowH, () => Change(ed => ed.opensIntoRoomUuid = id0), 20);
        intoButtons[1] = XRUi.CreateButton(t, RoomLabel(d.roomBack), 130 + bw2 + gap, y, bw2, rowH, () => Change(ed => ed.opensIntoRoomUuid = id1), 20);
        y += rowH + gap;

        XRUi.CreateText(t, "Hint", "Parts left to right as seen from the room it opens into. Click a part's type to change it.",
            17, TextAlignmentOptions.TopLeft, x0, y, pw - 40, 44, XRUi.MutedText);
        y += 48;
        float b3 = (pw - 40 - gap * 2) / 3f;
        // Toggles: a walled-up window gets its glazing back (its parts are kept).
        var wall = XRUi.CreateButton(t, "Wall (no window)", x0, y, b3, rowH,
            () => Change(ed => ed.kind = ed.kind == DoorKind.Wall ? DoorKind.Single : DoorKind.Wall), 18);
        XRUi.SetTint(wall, walled ? XRUi.ButtonDangerColor : XRUi.ButtonToggleColor);
        var clear = XRUi.CreateButton(t, "Clear", x0 + b3 + gap, y, b3, rowH, Clear, 20);
        XRUi.SetTint(clear, XRUi.ButtonDangerColor);
        XRUi.CreateButton(t, "Close", x0 + (b3 + gap) * 2, y, b3, rowH, () => { selected = null; ClosePanel(); }, 20);
    }

    static float WindowPanelHeight(int parts) => 60 + (62 + 8) * (1 + parts + 1) + 48 + 62 + 20;

    void OpenPanel()
    {
        // Rebuilt in place (window parts change the layout): keep it exactly where it is.
        bool hadPanel = panel;
        Vector3 keepPos = hadPanel ? panel.transform.position : default;
        Quaternion keepRot = hadPanel ? panel.transform.rotation : default;
        bool keepMoved = hadPanel && panelDrag && panelDrag.Moved;
        ClosePanel();
        for (int i = 0; i < 2; i++) intoButtons[i] = null;
        var d = selected;
        if (d != null && d.isWindow)
        {
            var we = DoorCatalog.FindEdit(HouseEditsStore.Current, d, rooms);
            int n = we != null && we.kind != DoorKind.Wall ? DoorCatalog.Sections(we).Count : 1;
            float ph = WindowPanelHeight(n);
            panel = XRUi.CreateWorldCanvas("WindowEditPanel", new Vector2(PW, ph), PScale);
            var wt = panel.transform;
            if (hadPanel) wt.SetPositionAndRotation(keepPos, keepRot);
            else if (userPlaced) wt.SetPositionAndRotation(userPos, userRot);
            else
            {
                Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
                Vector3 pos = head.position + fwd * 0.6f - Vector3.up * 0.18f;
                wt.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head.position, Vector3.up));
            }
            panelDrag = PanelDrag.Attach(panel, PW, ph);
            if (keepMoved) userPlaced = true;
            XRUi.CreatePanel(wt, "Background", XRUi.PanelColor, 0, 0, PW, ph);
            panelTitle = XRUi.CreateText(wt, "Title", "", 26, TextAlignmentOptions.MidlineLeft, 20, 8, PW - 40, 44, XRUi.TextColor, FontStyles.Bold);
            for (int i = 0; i < kindButtons.Length; i++) kindButtons[i] = null;
            hingeButtons[0] = hingeButtons[1] = null;
            OpenWindowPanel(d, wt, PW);
            windowPanelSig = WindowSignature(we);
            RefreshPanel();
            return;
        }
        panel = XRUi.CreateWorldCanvas("DoorEditPanel", new Vector2(PW, PH), PScale);
        var t = panel.transform;
        // Where you last dragged it (grip), else in front of you, a bit low, facing you.
        if (userPlaced) t.SetPositionAndRotation(userPos, userRot);
        else
        {
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            Vector3 pos = head.position + fwd * 0.6f - Vector3.up * 0.18f;
            t.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head.position, Vector3.up));
        }
        panelDrag = PanelDrag.Attach(panel, PW, PH);

        XRUi.CreatePanel(t, "Background", XRUi.PanelColor, 0, 0, PW, PH);
        string a = RoomLabel(d.roomFront), b = RoomLabel(d.roomBack);
        panelTitle = XRUi.CreateText(t, "Title", "", 26, TextAlignmentOptions.MidlineLeft, 20, 8, PW - 40, 44, XRUi.TextColor, FontStyles.Bold);

        float x0 = 20, rowH = 66, gap = 8;
        float y = 60;
        XRUi.CreateText(t, "TypeLbl", "Type", 20, TextAlignmentOptions.MidlineLeft, x0, y, 110, rowH, XRUi.MutedText);
        var options = d.isWindow ? WindowKinds : DoorKinds;
        float bw = (PW - 130 - 20 - gap * 2) / 3f;
        for (int i = 0; i < options.Length; i++)
        {
            var k = options[i].kind;
            kindValues[i] = k;
            float bx = 130 + (i % 3) * (bw + gap), by = y + (i / 3) * (rowH + gap);
            kindButtons[i] = XRUi.CreateButton(t, options[i].label, bx, by, bw, rowH, () => Change(e => e.kind = k), 20);
        }

        y += 2 * (rowH + gap);
        XRUi.CreateText(t, "HingeLbl", "Hinge", 20, TextAlignmentOptions.MidlineLeft, x0, y, 110, rowH, XRUi.MutedText);
        float bw2 = (PW - 130 - 20 - gap) / 2f;
        hingeButtons[0] = XRUi.CreateButton(t, "Left", 130, y, bw2, rowH, () => Change(e => e.hinge = HingeSide.Left), 22);
        hingeButtons[1] = XRUi.CreateButton(t, "Right", 130 + bw2 + gap, y, bw2, rowH, () => Change(e => e.hinge = HingeSide.Right), 22);

        y += rowH + gap;
        XRUi.CreateText(t, "IntoLbl", "Opens into", 20, TextAlignmentOptions.MidlineLeft, x0, y, 110, rowH, XRUi.MutedText);
        intoIds[0] = DoorCatalog.RoomId(d.roomFront); intoIds[1] = DoorCatalog.RoomId(d.roomBack);
        string id0 = intoIds[0], id1 = intoIds[1];
        intoButtons[0] = XRUi.CreateButton(t, a, 130, y, bw2, rowH, () => Change(e => e.opensIntoRoomUuid = id0), 22);
        intoButtons[1] = XRUi.CreateButton(t, b, 130 + bw2 + gap, y, bw2, rowH, () => Change(e => e.opensIntoRoomUuid = id1), 22);

        y += rowH + gap;
        XRUi.CreateText(t, "Hint", d.isWindow
                ? "Hinge side as seen from the room the window opens into (usually the inside)."
                : "Hinge side as seen from the room the door opens into (the leaf swings towards you).",
            17, TextAlignmentOptions.TopLeft, x0, y, PW - 40, 50, XRUi.MutedText);

        y += 56;
        var clear = XRUi.CreateButton(t, "Clear", x0, y, (PW - 40 - gap) / 2f, rowH, Clear, 22);
        XRUi.SetTint(clear, XRUi.ButtonDangerColor);
        XRUi.CreateButton(t, "Close", x0 + (PW - 40 - gap) / 2f + gap, y, (PW - 40 - gap) / 2f, rowH, () => { selected = null; ClosePanel(); }, 22);

        RefreshPanel();
    }

    PanelDrag panelDrag;
    bool userPlaced; Vector3 userPos; Quaternion userRot;

    void ClosePanel()
    {
        if (panel && panelDrag && panelDrag.Moved) { userPlaced = true; userPos = panel.transform.position; userRot = panel.transform.rotation; }
        if (panel) Destroy(panel.gameObject);
        panel = null;
        addPanelOpen = false; draggingNewDoor = false; newDoorRoom = null; wallPanelOpen = false; gapRoom = null;
    }

    static string RoomLabel(MRUKRoom r) => r != null ? MRUKDataProcessor.GetRoomLabel(r) : "Outside";

    void RefreshPanel()
    {
        if (!panel || selected == null) return;
        var e = DoorCatalog.FindEdit(HouseEditsStore.Current, selected, rooms);
        panelTitle.text = $"{(selected.isWindow ? "Window" : "Door")} {RoomLabel(selected.roomFront)} / {RoomLabel(selected.roomBack)}  {selected.width:0.00} m" + (e == null ? "  (not set)" : "");
        if (selected.isWindow && WindowSignature(e) != windowPanelSig)
        {
            // Parts changed (undo/redo, clear, wall): the panel's rows depend on them.
            OpenPanel();
            return;
        }
        for (int i = 0; i < 6; i++) if (kindButtons[i]) XRUi.SetTint(kindButtons[i], e != null && e.kind == kindValues[i] ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        bool leaf = e == null || e.kind == DoorKind.Single || e.kind == DoorKind.Double || e.kind == DoorKind.Sliding || e.kind == DoorKind.Tilt;
        for (int i = 0; i < 2; i++)
        {
            if (hingeButtons[i]) XRUi.SetTint(hingeButtons[i], e != null && (int)e.hinge == i && leaf ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
            if (intoButtons[i]) XRUi.SetTint(intoButtons[i], e != null && (e.opensIntoRoomUuid ?? "") == intoIds[i] ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        }
    }

    string windowPanelSig;

    static string WindowSignature(DoorEdit e)
    {
        if (e == null) return "none";
        if (e.kind == DoorKind.Wall) return "wall";
        return string.Join(",", DoorCatalog.Sections(e).Select(x => $"{x.s.kind}{x.s.hinge}"));
    }

    /// <summary>Creates the door's edit on first change (defaults: single leaf, left hinge, opening into the room
    /// you're standing on the side of), applies 'mutate', saves - undoable.</summary>
    async void Change(System.Action<DoorEdit> mutate)
    {
        var d = selected;
        if (d == null || editMode == null) return;
        await editMode.Commit(edits =>
        {
            var e = DoorCatalog.FindEdit(edits, d, rooms);
            if (e == null) { e = GuessedEdit(d); edits.doors.Add(e); }
            mutate(e);
        });
        RefreshPanel();
    }

    async void Clear()
    {
        var d = selected;
        if (d == null || editMode == null) return;
        if (DoorCatalog.FindEdit(HouseEditsStore.Current, d, rooms) == null) return;
        await editMode.Commit(edits =>
        {
            var e = DoorCatalog.FindEdit(edits, d, rooms);
            if (e != null) edits.doors.Remove(e);
        });
        RefreshPanel();
    }

    string ViewerSideRoomId(DoorInfo d)
    {
        if (!frameRoot || !head) return DoorCatalog.RoomId(d.roomFront);
        Vector3 eye = WorldToScan(head.position);
        bool front = Vector3.Dot(eye - d.center, d.normal) > 0f;
        // In the Dollhouse you look from outside the house - "your side" means little there, the front room is
        // as good a default as any (one click changes it).
        if (!frameIsWalk) front = true;
        return DoorCatalog.RoomId(front ? d.roomFront : d.roomBack);
    }
}
