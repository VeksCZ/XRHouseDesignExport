using System.Collections.Generic;
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
    readonly Button[] kindButtons = new Button[4];
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
            var e = DoorCatalog.FindEdit(HouseEditsStore.Current, d.anchor, rooms);
            if (e != null) DrawSwing(d, e);
        }
        if (hovered != null && hovered != selected) DrawOutline(hovered, HoverColor);
        if (selected != null) DrawOutline(selected, SelectColor);
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
        selected = hovered;
        if (selected == null) { ClosePanel(); return; }
        OpenPanel();
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
                Rect3(hinge, hinge + toOther * w, h, SwingColor);
                break;
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

    const float PW = 640f, PH = 470f, PScale = 0.0006f;

    void OpenPanel()
    {
        ClosePanel();
        var d = selected;
        panel = XRUi.CreateWorldCanvas("DoorEditPanel", new Vector2(PW, PH), PScale);
        var t = panel.transform;
        // In front of you, a bit low, facing you (stays put until the next pick).
        Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
        Vector3 pos = head.position + fwd * 0.6f - Vector3.up * 0.18f;
        t.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head.position, Vector3.up));

        XRUi.CreatePanel(t, "Background", XRUi.PanelColor, 0, 0, PW, PH);
        string a = RoomLabel(d.roomFront), b = RoomLabel(d.roomBack);
        panelTitle = XRUi.CreateText(t, "Title", "", 26, TextAlignmentOptions.MidlineLeft, 20, 8, PW - 40, 44, XRUi.TextColor, FontStyles.Bold);

        float x0 = 20, rowH = 66, gap = 8;
        float y = 60;
        XRUi.CreateText(t, "TypeLbl", "Type", 20, TextAlignmentOptions.MidlineLeft, x0, y, 110, rowH, XRUi.MutedText);
        string[] kinds = { "Single", "Double", "Sliding", "Opening" };
        float bw = (PW - 130 - 20 - gap * 3) / 4f;
        for (int i = 0; i < 4; i++)
        {
            var k = (DoorKind)i;
            kindButtons[i] = XRUi.CreateButton(t, kinds[i], 130 + i * (bw + gap), y, bw, rowH, () => Change(e => e.kind = k), 22);
        }

        y += rowH + gap;
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
        XRUi.CreateText(t, "Hint", "Hinge side as seen from the room the door opens into (the leaf swings towards you).",
            17, TextAlignmentOptions.TopLeft, x0, y, PW - 40, 50, XRUi.MutedText);

        y += 56;
        var clear = XRUi.CreateButton(t, "Clear", x0, y, (PW - 40 - gap) / 2f, rowH, Clear, 22);
        XRUi.SetTint(clear, XRUi.ButtonDangerColor);
        XRUi.CreateButton(t, "Close", x0 + (PW - 40 - gap) / 2f + gap, y, (PW - 40 - gap) / 2f, rowH, () => { selected = null; ClosePanel(); }, 22);

        RefreshPanel();
    }

    void ClosePanel()
    {
        if (panel) Destroy(panel.gameObject);
        panel = null;
    }

    static string RoomLabel(MRUKRoom r) => r != null ? MRUKDataProcessor.GetRoomLabel(r) : "Outside";

    void RefreshPanel()
    {
        if (!panel || selected == null) return;
        var e = DoorCatalog.FindEdit(HouseEditsStore.Current, selected.anchor, rooms);
        panelTitle.text = $"Door {RoomLabel(selected.roomFront)} / {RoomLabel(selected.roomBack)}  {selected.width:0.00} m" + (e == null ? "  (not set)" : "");
        for (int i = 0; i < 4; i++) XRUi.SetTint(kindButtons[i], e != null && (int)e.kind == i ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        bool leaf = e == null || e.kind == DoorKind.Single || e.kind == DoorKind.Double || e.kind == DoorKind.Sliding;
        for (int i = 0; i < 2; i++)
        {
            XRUi.SetTint(hingeButtons[i], e != null && (int)e.hinge == i && leaf ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
            XRUi.SetTint(intoButtons[i], e != null && (e.opensIntoRoomUuid ?? "") == intoIds[i] ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        }
    }

    /// <summary>Creates the door's edit on first change (defaults: single leaf, left hinge, opening into the room
    /// you're standing on the side of), applies 'mutate', saves - undoable.</summary>
    async void Change(System.Action<DoorEdit> mutate)
    {
        var d = selected;
        if (d == null || editMode == null) return;
        string viewerSide = ViewerSideRoomId(d);
        await editMode.Commit(edits =>
        {
            var e = DoorCatalog.FindEdit(edits, d.anchor, rooms);
            if (e == null)
            {
                e = DoorCatalog.NewEdit(d);
                e.opensIntoRoomUuid = viewerSide;
                edits.doors.Add(e);
            }
            mutate(e);
        });
        RefreshPanel();
    }

    async void Clear()
    {
        var d = selected;
        if (d == null || editMode == null) return;
        if (DoorCatalog.FindEdit(HouseEditsStore.Current, d.anchor, rooms) == null) return;
        await editMode.Commit(edits =>
        {
            var e = DoorCatalog.FindEdit(edits, d.anchor, rooms);
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
