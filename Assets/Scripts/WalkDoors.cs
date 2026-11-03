using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Real leaves in the walk-through for every door/window the user has set up in edit mode:
/// hinged doors/casement windows swing 90 degrees into the room they open into, sliding ones slide aside, a garage
/// roller door rolls up into its box, a tilt window tips its top in. Every leaf has a handle on both faces at the
/// latch edge (a lever pointing towards the hinge on doors, a small handle on windows).
/// Opening: point the ray at a handle and pull the trigger (open/close). In edit mode doors (not windows) also open
/// by themselves as you come close, so they never get in the way while editing.
/// Closed leaves block you like walls. Openings without settings keep their plain panels.
/// </summary>
public class WalkDoors
{
    const float LeafThickness = 0.04f, WindowThickness = 0.06f;
    const float HandleHeight = 1.05f;    // doors: above the floor
    const float HandleInset = 0.07f;     // from the leaf's free (latch) edge
    const float AutoOpenDist = 1.1f;     // edit mode: horizontal distance from the door's middle
    const float OpenSpeed = 2f;          // full open/close per second (~0.5 s); the garage door is slower
    const float TiltDeg = 8f;
    static readonly Color LeafColor = new Color(0.74f, 0.58f, 0.40f);   // door leaf: light brown (lighter, as asked)
    static readonly Color GarageColor = new Color(0.72f, 0.73f, 0.75f);
    /// <summary>Window glass - opening sashes and (WalkThroughMode) the fixed panes use the same look.</summary>
    internal static readonly Color GlassColor = new Color(0.70f, 0.85f, 1f, 0.35f);
    static readonly Color HandleColor = new Color(0.30f, 0.30f, 0.32f);

    /// <summary>Fallback opening angle of a hinged leaf when the sweep below can't tell (and for windows): not a full 90, so a
    /// door hinged a few cm from a corner doesn't lie flat in the side wall.</summary>
    const float DefaultSwingDeg = 86f;
    /// <summary>A door opens as wide as it can without touching anything, up to this.</summary>
    const float MaxSwingDeg = 120f;

    enum Motion { Swing, Slide, Roll, Tilt }

    class Leaf
    {
        public Transform pivot;
        public Motion motion;
        public float sign;          // swing/tilt: which way round opens it
        public float openDeg = DefaultSwingDeg; // swing: the angle it opens to (the free angle, see FreeSwing)
        public Vector3 axis;        // tilt: hinge axis (model space)
        public Vector3 closedPos;   // slide: pivot position when closed
        public Vector3 slideDir;    // slide: model-space direction it slides open
        public float slide;         // slide distance
    }

    class Door
    {
        public Vector3 center;      // model space, at the bottom of the opening
        public bool isWindow, garage;
        public readonly List<Leaf> leaves = new List<Leaf>();
        public readonly List<Collider> handles = new List<Collider>(); // trigger zones around the handles
        public float open;          // 0 closed .. 1 open
        public bool manual;         // opened by hand (trigger on a handle)
        public bool nearOpens;      // opens by itself when you come close (sliding shower doors)
    }

    // Sliding parts of the furniture (shower doors) - registered by the walk-through after the furniture is built,
    // kept across door rebuilds (Build), dropped with the furniture.
    readonly List<Door> furnitureDoors = new List<Door>();
    const float NearOpenDist = 0.75f;

    /// <summary>Makes the furniture's sliding parts move: each group slides by its opening vector (model space =
    /// visual-local) when you come close, or on the trigger pointed at it.</summary>
    public void SetFurnitureSliders(GameObject visual, List<(List<Transform> parts, Vector3 open)> sliders)
    {
        furnitureDoors.Clear();
        if (!visual || sliders == null) return;
        foreach (var (parts, open) in sliders)
        {
            if (parts == null || parts.Count == 0 || open.sqrMagnitude < 1e-4f) continue;
            var bounds = new Bounds(visual.transform.InverseTransformPoint(parts[0].position), Vector3.zero);
            var door = new Door { nearOpens = true };
            foreach (var t in parts)
            {
                if (!t) continue;
                var r = t.GetComponent<Renderer>();
                if (r) { bounds.Encapsulate(visual.transform.InverseTransformPoint(r.bounds.min)); bounds.Encapsulate(visual.transform.InverseTransformPoint(r.bounds.max)); }
                // the opening vector in the part's parent space (what localPosition moves in)
                Vector3 local = t.parent ? t.parent.InverseTransformVector(visual.transform.TransformVector(open)) : open;
                door.leaves.Add(new Leaf { pivot = t, motion = Motion.Slide, closedPos = t.localPosition, slideDir = local.normalized, slide = local.magnitude });
                var col = t.GetComponent<Collider>();
                if (col) door.handles.Add(col);   // trigger on the glass opens/closes it
            }
            door.center = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            furnitureDoors.Add(door);
        }
    }

    readonly List<Door> doors = new List<Door>();

    // ---- exterior blinds (anthracite, in the reveal of every window in an outer wall) ----
    const string BlindsPref = "Model.BlindsDown";
    /// <summary>The exterior blinds are down (lowered over the windows); remembered. They slide up / down by themselves.</summary>
    public static bool BlindsDown
    {
        get { try { return PlayerPrefs.GetInt(BlindsPref, 0) == 1; } catch { return false; } }
        set { try { PlayerPrefs.SetInt(BlindsPref, value ? 1 : 0); PlayerPrefs.Save(); } catch { } }
    }
    class Blind { public Transform pivot; public float full, down; }
    readonly List<Blind> blinds = new List<Blind>();
    readonly List<GameObject> created = new List<GameObject>();
    readonly List<Material> materials = new List<Material>();
    /// <summary>The plain door/window panels replaced by real leaves - the walk-through keeps them hidden.</summary>
    public readonly HashSet<Renderer> Suppressed = new HashSet<Renderer>();
    readonly List<Collider> suppressedColliders = new List<Collider>();

    public void Build(GameObject visual, float yaw, Vector3 center, List<MRUKRoom> rooms, HouseEdits edits, int layer, IEnumerable<Renderer> panels)
    {
        // Keep doors that were open open across a rebuild (an edit elsewhere shouldn't slam them shut).
        var wasOpen = doors.Where(d => d.manual).Select(d => d.center).ToList();
        Clear();
        if (!visual || edits == null || edits.doors.Count == 0) return;
        Quaternion g = Quaternion.Euler(0, yaw, 0);
        Quaternion gInv = Quaternion.Inverse(g);
        Vector3 M(Vector3 p) => g * (p - center);
        var panelList = panels.Where(r => r).ToList();

        foreach (var d in DoorCatalog.Build(rooms))
        {
            var e = DoorCatalog.FindEdit(edits, d, rooms);
            if (e == null || e.kind == DoorKind.Wall || e.kind == DoorKind.Fixed) continue; // walled up / stays as scanned

            // Hide this opening's plain panels - one per room that captured it, on that room's wall face.
            foreach (var r in panelList)
            {
                Vector3 scanPos = gInv * visual.transform.InverseTransformPoint(r.bounds.center) + center;
                if (!DoorCatalog.SameDoorway(d, scanPos, d.normal)) continue;
                Suppressed.Add(r); r.enabled = false;
                var col = r.GetComponent<Collider>();
                if (col && col.enabled) { col.enabled = false; suppressedColliders.Add(col); }
            }
            if (e.kind == DoorKind.Opening) continue; // just the empty opening
            if (d.isWindow) { BuildWindow(visual, layer, d, e, g, center, wasOpen); continue; }

            DoorCatalog.Swing(d, e, out var hinge, out var swing, out var toOther);
            Vector3 hingeM = M(hinge), swingM = g * swing, toOtherM = g * toOther;
            float h = d.height - 0.01f, w = d.width;
            float frameInset = 0f, frameDrop = 0f; bool entranceDone = false;   // how much of the opening the frame fills at the sides / at the top
            var door = new Door { center = M(d.center - Vector3.up * (d.height / 2f)), isWindow = d.isWindow, garage = e.kind == DoorKind.Garage };
            door.manual = wasOpen.Any(p => Vector3.Distance(p, door.center) < 0.1f);
            float handleH = d.isWindow ? h / 2f : HandleHeight;

            switch (e.kind)
            {
                case DoorKind.Single:
                {
                    if (!d.isWindow && w >= EntranceMinW && w <= EntranceMaxW)
                    {
                        // an entrance door (about 1.48 m wide): fixed sidelight + leaf, opening OUTWARD (the edit says inward -> the other way)
                        BuildEntrance(visual, layer, door, door.center, -swingM, w, h);
                        entranceDone = true;
                        break;
                    }
                    // the scan measures the rough opening; the leaf is a standard one inside a frame that fills the rest
                    LeafSize(w, h, out float lw, out float lh);
                    frameInset = (w - lw) / 2f; frameDrop = h - lh;
                    door.leaves.Add(Hinged(visual, layer, door, hingeM + toOtherM * frameInset, toOtherM, swingM, lw, lh, handleH));
                    break;
                }
                case DoorKind.Double:
                    door.leaves.Add(Hinged(visual, layer, door, hingeM, toOtherM, swingM, w / 2f, h, handleH));
                    door.leaves.Add(Hinged(visual, layer, door, hingeM + toOtherM * w, -toOtherM, swingM, w / 2f, h, handleH));
                    break;
                case DoorKind.Sliding:
                {
                    float t = d.isWindow ? WindowThickness : LeafThickness;
                    Vector3 p = hingeM + swingM * (t / 2f + 0.02f);
                    var leaf = new Leaf { motion = Motion.Slide, closedPos = p, slideDir = -toOtherM, slide = d.isWindow ? w * 0.5f : w };
                    leaf.pivot = MakeLeaf(visual, layer, door, p, toOtherM, w, h, handleH, d.isWindow ? LeafStyle.SlidingWindow : LeafStyle.SlidingDoor);
                    door.leaves.Add(leaf);
                    break;
                }
                case DoorKind.Garage:
                {
                    // Pivot at the top of the opening, leaf hanging down; opening = rolling it up (scale to a sliver).
                    Vector3 top = hingeM + Vector3.up * h;
                    var leaf = new Leaf { motion = Motion.Roll };
                    leaf.pivot = MakeLeaf(visual, layer, door, top, toOtherM, w, h, HandleHeight, LeafStyle.Garage);
                    door.leaves.Add(leaf);
                    break;
                }
                case DoorKind.Tilt:
                {
                    var leaf = new Leaf
                    {
                        motion = Motion.Tilt, axis = toOtherM,
                        sign = Vector3.Dot(Vector3.Cross(toOtherM, Vector3.up), swingM) >= 0f ? 1f : -1f,
                    };
                    leaf.pivot = MakeLeaf(visual, layer, door, hingeM, toOtherM, w, h, h * 0.85f, LeafStyle.Window);
                    door.leaves.Add(leaf);
                    break;
                }
            }
            if (e.kind != DoorKind.Garage && !entranceDone)
            {
                // no jamb on a side where the door sits at a room corner: the frame would poke into the wall that meets here
                bool cornerH = DoorCatalog.JambAtCorner(rooms, d, hinge, -toOther, 0.08f);
                bool cornerO = DoorCatalog.JambAtCorner(rooms, d, hinge + toOther * w, toOther, 0.08f);
                AddFrame(visual, layer, hingeM, toOtherM, w, h, !cornerH, !cornerO, frameInset, frameDrop);
            }
            doors.Add(door);
        }
    }

    static readonly Color FrameColor = new Color(0.62f, 0.42f, 0.22f);   // door frame: the saturated brown
    const float FrameWidth = 0.06f, FrameDepth = 0.12f;

    const float EntranceMinW = 1.35f, EntranceMaxW = 1.70f, SidelightW = 0.40f, EntranceTotalH = 2.195f, EntranceThickness = 0.06f;
    static readonly Color AnthraciteColor = new Color(0.16f, 0.19f, 0.20f);   // RAL 7016
    static readonly Color WhiteColor = new Color(0.945f, 0.94f, 0.92f);        // RAL 9016
    static readonly Color InoxColor = new Color(0.76f, 0.77f, 0.79f), SlitColor = new Color(0.58f, 0.66f, 0.70f);

    /// <summary>The entrance door (1480 x 2195 mm Aluprof): a fixed 400 mm sidelight (matte triple glazing) on the left as seen from
    /// inside and the 1080 mm leaf hinged on the right, opening to the outside; anthracite inside, white outside, anthracite frame.
    /// 'outward' points from the house to the outside.</summary>
    void BuildEntrance(GameObject visual, int layer, Door door, Vector3 floorCentre, Vector3 outward, float w, float h)
    {
        float lh = Mathf.Min(EntranceTotalH, h - 0.05f), lw = w - SidelightW - 0.01f;
        Vector3 rightIn = Vector3.Cross(Vector3.up, outward).normalized;   // the right hand of someone inside looking out
        Vector3 left = floorCentre - rightIn * (w / 2f), right = floorCentre + rightIn * (w / 2f);
        MakeLeaf(visual, layer, door, left, rightIn, SidelightW, lh, 0f, LeafStyle.FixedPane, outward, true);
        door.leaves.Add(Hinged(visual, layer, door, right, -rightIn, outward, lw, lh, HandleHeight, LeafStyle.Entrance));
        AddFrame(visual, layer, left, rightIn, w, h, true, true, 0f, h - lh, AnthraciteColor, SidelightW);
    }

    /// <summary>The leaf of a single door in a rough opening of this size: a standard 80 cm one (70 cm in a narrow opening, 90 cm in a wide
    /// one), 205 cm high - never bigger than the opening less a frame.</summary>
    static void LeafSize(float openW, float openH, out float leafW, out float leafH)
    {
        float std = openW < 0.86f ? 0.70f : openW >= 1.05f ? 0.90f : 0.80f;
        leafW = Mathf.Min(std, openW - 0.04f);
        leafH = Mathf.Min(2.05f, openH - 0.05f);
    }

    /// <summary>A door frame (zarubne): two jambs and a head just outside the opening, centred on the door's plane. No
    /// colliders - the opening stays as wide as scanned; where the wall is thicker than the frame it is hidden in it, where the
    /// door sits at a wall face it shows as a casing about 6 cm wide.</summary>
    void AddFrame(GameObject visual, int layer, Vector3 hingeM, Vector3 toOtherM, float w, float h, bool jambHinge = true, bool jambOther = true, float inset = 0f, float drop = 0f, Color? color = null, float mullion = -1f)
    {
        Quaternion rot = Quaternion.LookRotation(toOtherM, Vector3.up);
        var parts = new List<XRMeshPart>();
        void Span(float a0, float a1, float y0, float y1)   // a box from a0..a1 along the opening, y0..y1 high
        {
            if (a1 - a0 < 0.003f || y1 - y0 < 0.003f) return;
            parts.Add(XRModelFactory.CreateBoxPart("DOOR_FRAME", hingeM + toOtherM * ((a0 + a1) / 2f) + Vector3.up * ((y0 + y1) / 2f), rot,
                new Vector3(FrameDepth, y1 - y0, a1 - a0), "DOORLEAF", Vector3.zero, 0f));
        }
        float x0 = jambHinge ? -FrameWidth : 0f, x1 = jambOther ? w + FrameWidth : w;   // the head stops where a jamb is left out
        Span(x0, inset, 0f, h);                       // hinge-side jamb (+ the infill up to a narrower leaf)
        Span(w - inset, x1, 0f, h);                   // the other jamb
        Span(x0, x1, h - drop, h + FrameWidth);       // head (+ the infill down to a lower leaf)
        if (mullion > 0f) Span(mullion - 0.02f, mullion + 0.01f, 0f, h - drop);   // between a sidelight and the leaf
        foreach (var part in parts) part.color = color ?? FrameColor;
        var model = new XRHouseModel();
        model.rooms.Add(new XRRoomModel { roomName = "Frame", parts = parts });
        var go = UnityModelLoader.LoadToScene(model, shaded: true);
        go.transform.SetParent(visual.transform, false);
        foreach (var t in go.GetComponentsInChildren<Transform>()) t.gameObject.layer = layer;
        created.Add(go);
    }

    enum LeafStyle { Door, SlidingDoor, Window, SlidingWindow, Garage, FixedPane, Entrance }

    /// <summary>A window as its parts side by side (fixed panes, tilt-turn/casement sashes, tilt sashes, HS sliding
    /// sash in front of its neighbour), each opening part with its own handle.</summary>
    void BuildWindow(GameObject visual, int layer, DoorInfo d, DoorEdit e, Quaternion g, Vector3 center, List<Vector3> wasOpen)
    {
        Vector3 M(Vector3 p) => g * (p - center);
        DoorCatalog.LeftFrame(d, e, out var left, out var toRight, out var swing);
        Vector3 leftM = M(left), rM = g * toRight, sM = g * swing;
        float W = d.width, h = d.height - 0.01f;
        Vector3 openLeftM = leftM;                          // the scanned opening's left jamb at the sill (before the real frame size)
        if (e.widthMm > 0f && e.heightMm > 0f)
        {
            // the real frame size (from the order): centred in the scanned rough opening (also where it is bigger), on the 3 cm sill profile
            float cw = e.widthMm / 1000f, ch = e.heightMm / 1000f;
            W = cw;                                           // the order's size wins, also where the scanned opening is a bit smaller
            float lift = ch < 2.0f ? 0.03f : 0f;
            h = ch;
            leftM += rM * ((d.width - W) / 2f) + Vector3.up * lift;
        }
        bool portal = d.height > 1.8f;            // floor-to-ceiling glazing (HS portal): handles at door height
        float handleH = portal ? HandleHeight : h * 0.5f;
        foreach (var (s, f0, f1) in DoorCatalog.Sections(e))
        {
            float sw = W * (f1 - f0);
            Vector3 a = leftM + rM * (W * f0), b = leftM + rM * (W * f1);
            var door = new Door { center = (a + b) / 2f, isWindow = true };
            door.manual = wasOpen.Any(p => Vector3.Distance(p, door.center) < 0.1f);
            bool hingeLeft = s.hinge == HingeSide.Left;
            switch (s.kind)
            {
                case DoorKind.Fixed:
                    MakeLeaf(visual, layer, door, a, rM, sw, h, handleH, LeafStyle.FixedPane);
                    break;
                case DoorKind.Tilt:
                {
                    var leaf = new Leaf { motion = Motion.Tilt, axis = rM, sign = Vector3.Dot(Vector3.Cross(rM, Vector3.up), sM) >= 0f ? 1f : -1f };
                    leaf.pivot = MakeLeaf(visual, layer, door, a, rM, sw, h, h * 0.85f, LeafStyle.Window);
                    door.leaves.Add(leaf);
                    break;
                }
                case DoorKind.Sliding:
                {
                    // Slides towards its hinge side, just inside the plane of the neighbouring part.
                    Vector3 start = hingeLeft ? a : b, along = hingeLeft ? rM : -rM;
                    Vector3 p = start + sM * (WindowThickness + 0.02f);
                    var leaf = new Leaf { motion = Motion.Slide, closedPos = p, slideDir = -along, slide = sw * 0.95f };
                    leaf.pivot = MakeLeaf(visual, layer, door, p, along, sw, h, handleH, LeafStyle.SlidingWindow);
                    door.leaves.Add(leaf);
                    break;
                }
                default: // Single (casement), TiltTurn (opens like a casement), Double
                    door.leaves.Add(Hinged(visual, layer, door, hingeLeft ? a : b, hingeLeft ? rM : -rM, sM, sw, h, handleH));
                    break;
            }
            doors.Add(door);
        }
        AddWindowFrame(visual, layer, leftM, rM, sM, W, h, DoorCatalog.Sections(e).Select(x => x.f1).ToList());
        bool exterior = d.roomFront == null || d.roomBack == null;
        if (exterior && d.height > 0.3f)
        {
            Vector3 nM = g * d.normal;                      // points into the front room
            AddBlind(visual, layer, openLeftM, rM, d.roomFront != null ? -nM : nM, d.width, d.height);
        }
    }

    const float BlindBox = 0.17f, BlindRail = 0.03f, BlindDepth = 0.12f;

    /// <summary>An exterior blind in the reveal of a window in an outer wall (the wall is as thick as the spec's "exterior"):
    /// a head box at the top of the opening and two guide rails at its outer end, anthracite, and the curtain that slides down
    /// to the sill (<see cref="BlindsDown"/>). 'outN' points to the outside.</summary>
    void AddBlind(GameObject visual, int layer, Vector3 openLeftM, Vector3 rM, Vector3 outN, float openW, float openH)
    {
        float wall = Mathf.Max(0.2f, XRModelFactory.ExteriorWallThickness);
        Vector3 baseP = openLeftM + outN * (wall - BlindDepth / 2f - 0.01f);   // at the outer end of the reveal, left jamb, sill
        Quaternion rot = Quaternion.LookRotation(rM, Vector3.up);
        float railH = openH - BlindBox;
        var parts = new List<XRMeshPart>();
        void Box(Vector3 c, Vector3 size) { var p = XRModelFactory.CreateBoxPart("BLIND", c, rot, size, "DOORLEAF", Vector3.zero, 0f); p.color = AnthraciteColor; parts.Add(p); }
        Box(baseP + rM * (openW / 2f) + Vector3.up * (openH - BlindBox / 2f), new Vector3(BlindDepth, BlindBox, openW));                 // head box
        Box(baseP + rM * (BlindRail / 2f) + Vector3.up * (railH / 2f), new Vector3(0.04f, railH, BlindRail));                              // guide rails
        Box(baseP + rM * (openW - BlindRail / 2f) + Vector3.up * (railH / 2f), new Vector3(0.04f, railH, BlindRail));
        var model = new XRHouseModel();
        model.rooms.Add(new XRRoomModel { roomName = "BlindBox", parts = parts });
        var go = UnityModelLoader.LoadToScene(model, shaded: true);
        go.transform.SetParent(visual.transform, false);
        foreach (var t in go.GetComponentsInChildren<Transform>()) t.gameObject.layer = layer;
        created.Add(go);
        // the curtain: a unit-high box hanging from a pivot under the head box, scaled in y (up = nothing shows, down = to the sill)
        var pivot = new GameObject("BlindCurtain");
        pivot.layer = layer;
        pivot.transform.SetParent(visual.transform, false);
        pivot.transform.localPosition = baseP + rM * (openW / 2f) + Vector3.up * railH;
        created.Add(pivot);
        var cp = XRModelFactory.CreateBoxPart("BLIND_CURTAIN", new Vector3(0f, -0.5f, 0f), rot, new Vector3(0.02f, 1f, openW - 2f * BlindRail), "DOORLEAF", Vector3.zero, 0f);
        cp.color = AnthraciteColor * 0.92f;
        var cm = new XRHouseModel();
        cm.rooms.Add(new XRRoomModel { roomName = "BlindCurtain", parts = new List<XRMeshPart> { cp } });
        var cgo = UnityModelLoader.LoadToScene(cm, shaded: true);
        cgo.transform.SetParent(pivot.transform, false);
        foreach (var t in cgo.GetComponentsInChildren<Transform>()) t.gameObject.layer = layer;
        float down = BlindsDown ? 1f : 0f;
        pivot.transform.localScale = new Vector3(1f, Mathf.Max(0.001f, railH * down), 1f);
        pivot.SetActive(down > 0.005f);
        blinds.Add(new Blind { pivot = pivot.transform, full = railH, down = down });
    }

    const float WinFrameW = 0.065f, WinFrameD = 0.09f, MullionW = 0.05f;

    /// <summary>The window's frame: the outer profile and a post between the parts, white on the room side (RAL 9016) and
    /// anthracite on the outside (Veka Softline 82, "Bila / Antracit hladky"). 'interiorM' points into the room.</summary>
    void AddWindowFrame(GameObject visual, int layer, Vector3 leftM, Vector3 rM, Vector3 interiorM, float W, float h, List<float> splits)
    {
        Quaternion rot = Quaternion.LookRotation(rM, Vector3.up);
        Vector3 face = Vector3.Cross(Vector3.up, rM);
        Vector3 inN = Vector3.Dot(face, interiorM) >= 0f ? face : -face, outN = -inN;
        var parts = new List<XRMeshPart>();
        void Span(float a0, float a1, float y0, float y1)
        {
            if (a1 - a0 < 0.003f || y1 - y0 < 0.003f) return;
            Vector3 c = leftM + rM * ((a0 + a1) / 2f) + Vector3.up * ((y0 + y1) / 2f);
            var sz = new Vector3(WinFrameD / 2f, y1 - y0, a1 - a0);
            var inside = XRModelFactory.CreateBoxPart("WINDOW_FRAME", c + inN * (WinFrameD / 4f), rot, sz, "DOORLEAF", Vector3.zero, 0f); inside.color = WhiteColor;
            var outside = XRModelFactory.CreateBoxPart("WINDOW_FRAME", c + outN * (WinFrameD / 4f), rot, sz, "DOORLEAF", Vector3.zero, 0f); outside.color = AnthraciteColor;
            parts.Add(inside); parts.Add(outside);
        }
        float fw = Mathf.Min(WinFrameW, Mathf.Min(W / 4f, h / 4f));
        Span(0f, fw, 0f, h); Span(W - fw, W, 0f, h);          // jambs
        Span(fw, W - fw, h - fw, h); Span(fw, W - fw, 0f, fw); // head, sill
        foreach (float f in splits) if (f > 0.01f && f < 0.99f) Span(f * W - MullionW / 2f, f * W + MullionW / 2f, fw, h - fw);
        if (parts.Count == 0) return;
        var model = new XRHouseModel();
        model.rooms.Add(new XRRoomModel { roomName = "WindowFrame", parts = parts });
        var go = UnityModelLoader.LoadToScene(model, shaded: true);
        go.transform.SetParent(visual.transform, false);
        foreach (var t in go.GetComponentsInChildren<Transform>()) t.gameObject.layer = layer;
        created.Add(go);
    }

    Leaf Hinged(GameObject visual, int layer, Door door, Vector3 hinge, Vector3 toOther, Vector3 swing, float width, float h, float handleH, LeafStyle? styleOverride = null)
    {
        // Rotating a vector by +90 degrees about up turns it into Cross(up, v) - so that's the way to open if it
        // points into the swing room, otherwise the other way round.
        float sign = Vector3.Dot(Vector3.Cross(Vector3.up, toOther), swing) >= 0f ? 1f : -1f;
        var style = styleOverride ?? (door.isWindow ? LeafStyle.Window : LeafStyle.Door);
        float open = door.isWindow ? DefaultSwingDeg : FreeSwing(visual, layer, hinge, toOther, sign, width, h);
        return new Leaf { motion = Motion.Swing, pivot = MakeLeaf(visual, layer, door, hinge, toOther, width, h, handleH, style, swing), sign = sign, openDeg = open };
    }

    static readonly float[] SweepHeights = { 0.15f, 1.0f, 1.9f };
    // The lever handle sticks out roughly LeafThickness/2 + 0.035 (its base offset) + ~0.02 (its own half-size) from
    // the leaf's centre-plane - a plain 2 cm sweep along the leaf's own edge missed it, so the handle could swing
    // into a wall the leaf itself still cleared ("the handle sinks into the door").
    const float HandleProtrusion = 0.08f;

    /// <summary>How far a leaf can swing open before it (or its handle) touches a wall (or anything else in the walk
    /// model): a sphere big enough to cover the handle's protrusion, swept from the hinge to the free end at three
    /// heights, is turned in 2-degree steps up to <see cref="MaxSwingDeg"/>; the first step that hits ends it (minus a
    /// 4-degree margin, never under 60). A hit right at the start is more likely the frame or the jamb than a real
    /// obstacle - then the default angle is used.</summary>
    float FreeSwing(GameObject visual, int layer, Vector3 hinge, Vector3 toOther, float sign, float width, float h)
    {
        var t = visual.transform;
        Physics.SyncTransforms();
        int mask = 1 << layer;
        float len = Mathf.Max(0.1f, width - 0.07f);
        for (int deg = 12; deg <= MaxSwingDeg; deg += 2)
        {
            Vector3 dir = t.TransformDirection(Quaternion.Euler(0f, sign * deg, 0f) * toOther).normalized;
            foreach (float y in SweepHeights)
            {
                if (y > h) continue;
                Vector3 o = t.TransformPoint(hinge + Vector3.up * y) + dir * 0.05f;
                if (!Physics.SphereCast(o, HandleProtrusion, dir, out _, len, mask, QueryTriggerInteraction.Ignore)) continue;
                if (deg <= 12) return DefaultSwingDeg;
                return Mathf.Clamp(deg - 4f, 60f, MaxSwingDeg);
            }
        }
        return MaxSwingDeg;
    }

    /// <summary>A pivot at 'pos' (model space) with a leaf running along 'along' for 'width' (garage: hanging down
    /// from the pivot), handles on both faces, and a trigger zone around them for opening by hand.</summary>
    Transform MakeLeaf(GameObject visual, int layer, Door door, Vector3 pos, Vector3 along, float width, float h, float handleH, LeafStyle style, Vector3 outward = default, bool matte = false)
    {
        var pivot = new GameObject("LeafPivot");
        pivot.layer = layer;
        pivot.transform.SetParent(visual.transform, false);
        pivot.transform.localPosition = pos;
        created.Add(pivot);

        bool window = style == LeafStyle.Window || style == LeafStyle.SlidingWindow || style == LeafStyle.FixedPane;
        bool fixedPane = style == LeafStyle.FixedPane;
        bool garage = style == LeafStyle.Garage;
        bool entrance = style == LeafStyle.Entrance;
        float thick = entrance ? EntranceThickness : window ? WindowThickness : LeafThickness;
        float bottom = garage ? -h : 0f; // garage leaf hangs below its pivot
        Quaternion leafRot = Quaternion.LookRotation(along, Vector3.up);
        Vector3 face = Vector3.Cross(Vector3.up, along); // leaf normal (one side; the other is -face)

        var parts = new List<XRMeshPart>();
        float ext = Vector3.Dot(face, outward) >= 0f ? 1f : -1f;   // entrance door: which face looks outside
        Vector3 outN = face * ext, inN = -outN;
        if (entrance)
        {
            // two-colour leaf: white outside (RAL 9016), anthracite inside (RAL 7016)
            Vector3 mid = new Vector3(0, h / 2f, 0) + along * (width / 2f);
            var outer = XRModelFactory.CreateBoxPart("DOOR_LEAF", mid + outN * (thick / 4f), leafRot, new Vector3(thick / 2f, h, width), "DOORLEAF", Vector3.zero, 0f);
            outer.color = WhiteColor; parts.Add(outer);
            var inner = XRModelFactory.CreateBoxPart("DOOR_LEAF", mid + inN * (thick / 4f), leafRot, new Vector3(thick / 2f, h, width), "DOORLEAF", Vector3.zero, 0f);
            inner.color = AnthraciteColor; parts.Add(inner);
            // narrow vertical glazing, two inox bars across, a long inox pull bar outside, rose + lever inside
            parts.Add(HandlePart(new Vector3(0, 1.15f, 0) + along * (width * 0.55f) + outN * (thick / 2f + 0.002f), leafRot, new Vector3(0.004f, 1.2f, 0.12f), SlitColor));
            parts.Add(HandlePart(new Vector3(0, 1.15f, 0) + along * (width * 0.55f) + inN * (thick / 2f + 0.002f), leafRot, new Vector3(0.004f, 1.2f, 0.12f), SlitColor));
            foreach (float by in new[] { 0.95f, 1.01f })
                parts.Add(HandlePart(new Vector3(0, by, 0) + along * (width / 2f) + outN * (thick / 2f + 0.003f), leafRot, new Vector3(0.006f, 0.012f, width * 0.9f), InoxColor));
            Vector3 barBase = new Vector3(0, 1.05f, 0) + along * (width - 0.07f) + outN * (thick / 2f + 0.045f);
            parts.Add(HandlePart(barBase, leafRot, new Vector3(0.025f, 1.0f, 0.025f), InoxColor));
            foreach (float py in new[] { 0.65f, 1.45f })
                parts.Add(HandlePart(new Vector3(0, py, 0) + along * (width - 0.07f) + outN * (thick / 2f + 0.0225f), leafRot, new Vector3(0.045f, 0.03f, 0.03f), InoxColor));
            Vector3 inBase = new Vector3(0, handleH, 0) + along * (width - HandleInset) + inN * (thick / 2f);
            parts.Add(HandlePart(inBase + inN * 0.01f, leafRot, new Vector3(0.02f, 0.05f, 0.05f), InoxColor));
            parts.Add(HandlePart(inBase + inN * 0.04f - along * 0.06f, leafRot, new Vector3(0.022f, 0.022f, 0.13f), InoxColor));
        }
        else
        {
            var leafPart = XRModelFactory.CreateBoxPart(window ? "WINDOW_LEAF" : "DOOR_LEAF", new Vector3(0, bottom + h / 2f, 0) + along * (width / 2f),
                leafRot, new Vector3(thick, h, width), "DOORLEAF", Vector3.zero, 0f);
            leafPart.color = window ? (matte ? new Color(0.90f, 0.93f, 0.95f) : new Color(GlassColor.r, GlassColor.g, GlassColor.b)) : garage ? GarageColor : LeafColor;
            parts.Add(leafPart);
        }
        if (garage)
            for (int k = 1; k < 8; k++) // slats
                parts.Add(HandlePart(new Vector3(0, -h + h * k / 8f, 0) + along * (width / 2f), leafRot, new Vector3(thick + 0.01f, 0.012f, width), GarageColor * 0.85f));

        Vector3 handleBase = garage ? along * (width / 2f) + Vector3.up * (handleH - h) : along * (width - HandleInset) + Vector3.up * handleH;
        foreach (float side in fixedPane || entrance ? new float[0] : new[] { 1f, -1f })
        {
            Vector3 n = face * side;
            if (garage) parts.Add(HandlePart(handleBase + n * (thick / 2f + 0.015f), leafRot, new Vector3(0.025f, 0.03f, 0.2f), HandleColor));
            else if (style == LeafStyle.SlidingDoor || style == LeafStyle.SlidingWindow)
                parts.Add(HandlePart(handleBase + n * (thick / 2f + 0.015f), leafRot, new Vector3(0.025f, window ? (h > 1.8f ? 0.3f : 0.12f) : 0.25f, 0.025f), HandleColor));
            else if (window)
            {
                parts.Add(HandlePart(handleBase + n * (thick / 2f + 0.015f), leafRot, new Vector3(0.03f, 0.05f, 0.03f), HandleColor));
                parts.Add(HandlePart(handleBase + n * (thick / 2f + 0.035f) - Vector3.up * 0.05f, leafRot, new Vector3(0.02f, 0.11f, 0.02f), HandleColor));
            }
            else
            {
                // Rose + lever, the lever pointing back towards the hinge.
                parts.Add(HandlePart(handleBase + n * (thick / 2f + 0.025f), leafRot, new Vector3(0.05f, 0.02f, 0.02f), HandleColor));
                parts.Add(HandlePart(handleBase + n * (thick / 2f + 0.05f) - along * 0.06f, leafRot, new Vector3(0.022f, 0.022f, 0.13f), HandleColor));
            }
        }

        var model = new XRHouseModel();
        model.rooms.Add(new XRRoomModel { roomName = "Leaf", parts = parts });
        var go = UnityModelLoader.LoadToScene(model, shaded: true);
        go.transform.SetParent(pivot.transform, false);
        var glass = window ? Shader.Find("Sprites/Default") : null;
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
        {
            mf.gameObject.layer = layer;
            bool isLeaf = mf.name == "DOOR_LEAF" || mf.name == "WINDOW_LEAF";
            if (!isLeaf) continue; // handles/slats are covered by the leaf and the trigger zone below
            mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
            if (glass != null)
            {
                // See-through pane (Sprites/Default multiplies by the vertex colour the shaded mesh carries).
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr.sharedMaterial) Object.Destroy(mr.sharedMaterial);
                var m = new Material(glass) { color = new Color(1f, 1f, 1f, matte ? 0.88f : GlassColor.a) };
                materials.Add(m);
                mr.sharedMaterial = m;
            }
        }

        if (fixedPane) return pivot.transform; // nothing to open
        // Generous trigger zone around both handles - walking ignores triggers, the handle ray doesn't.
        var zone = new GameObject("HandleZone");
        zone.layer = layer;
        zone.transform.SetParent(pivot.transform, false);
        zone.transform.localPosition = handleBase;
        zone.transform.localRotation = leafRot;
        var box = zone.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(thick + 0.24f, 0.35f, garage ? 0.6f : 0.3f);
        door.handles.Add(box);
        return pivot.transform;
    }

    static XRMeshPart HandlePart(Vector3 pos, Quaternion rot, Vector3 size, Color c)
    {
        var p = XRModelFactory.CreateBoxPart("DOOR_HANDLE", pos, rot, size, "HANDLE", Vector3.zero, 0f);
        p.color = c;
        return p;
    }

    /// <summary>Animates doors/windows. autoOpen (edit mode): doors near you open by themselves; otherwise only by
    /// hand. headLocal = your head in the model's own (visual-local) space.</summary>
    public bool Tick(Vector3 headLocal, float dt, bool autoOpen)
    {
        bool moved = false;
        float bt = BlindsDown ? 1f : 0f;
        foreach (var b in blinds)
        {
            if (b.pivot == null) continue;
            if (!Mathf.Approximately(b.down, bt)) b.down = Mathf.MoveTowards(b.down, bt, dt * 0.35f);   // ~3 s up / down
            bool vis = b.down > 0.005f;
            if (b.pivot.gameObject.activeSelf != vis) b.pivot.gameObject.SetActive(vis);
            if (vis) b.pivot.localScale = new Vector3(1f, b.full * b.down, 1f);
        }
        foreach (var d in doors.Concat(furnitureDoors))
        {
            bool target = d.manual;
            if (d.nearOpens)
            {
                float dist = Vector2.Distance(new Vector2(headLocal.x, headLocal.z), new Vector2(d.center.x, d.center.z));
                target |= dist < NearOpenDist && headLocal.y > d.center.y && headLocal.y < d.center.y + 2.5f;
            }
            if (autoOpen && !d.isWindow)
            {
                float dist = Vector2.Distance(new Vector2(headLocal.x, headLocal.z), new Vector2(d.center.x, d.center.z));
                bool sameStory = headLocal.y > d.center.y - 0.5f && headLocal.y < d.center.y + 2.8f;
                target |= sameStory && dist < AutoOpenDist;
            }
            float next = Mathf.MoveTowards(d.open, target ? 1f : 0f, dt * (d.garage ? OpenSpeed / 4f : OpenSpeed));
            if (Mathf.Approximately(next, d.open)) continue;
            d.open = next;
            float ease = Mathf.SmoothStep(0f, 1f, next);
            foreach (var l in d.leaves)
            {
                if (!l.pivot) continue;
                switch (l.motion)
                {
                    case Motion.Swing: l.pivot.localRotation = Quaternion.Euler(0f, l.sign * l.openDeg * ease, 0f); break;
                    case Motion.Slide: l.pivot.localPosition = l.closedPos + l.slideDir * (l.slide * ease); break;
                    case Motion.Roll: l.pivot.localScale = new Vector3(1f, Mathf.Lerp(1f, 0.04f, ease), 1f); break;
                    case Motion.Tilt: l.pivot.localRotation = Quaternion.AngleAxis(l.sign * TiltDeg * ease, l.axis); break;
                }
            }
            moved = true;
        }
        return moved;
    }

    /// <summary>Opens (or closes) every door at once - the desktop app's scripted tests.</summary>
    public void SetAllDoors(bool open)
    {
        foreach (var d in doors) if (!d.isWindow) d.manual = open;
    }

    /// <summary>Trigger on a handle: open it if it's (mostly) closed, else close it.</summary>
    public bool Toggle(Collider c)
    {
        var d = doors.Concat(furnitureDoors).FirstOrDefault(x => x.handles.Contains(c));
        if (d == null) return false;
        d.manual = d.open < 0.5f;
        return true;
    }

    public void Clear()
    {
        blinds.Clear();
        foreach (var r in Suppressed) if (r) r.enabled = true;
        Suppressed.Clear();
        foreach (var c in suppressedColliders) if (c) c.enabled = true;
        suppressedColliders.Clear();
        foreach (var go in created)
        {
            if (!go) continue;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true)) if (mf.sharedMesh) Object.Destroy(mf.sharedMesh);
            foreach (var mr in go.GetComponentsInChildren<Renderer>(true)) if (mr.sharedMaterial) Object.Destroy(mr.sharedMaterial);
            go.transform.SetParent(null, false);
            Object.Destroy(go);
        }
        created.Clear();
        materials.Clear();
        doors.Clear();
    }

    /// <summary>Everything, the furniture's sliding doors too (leaving the walk-through).</summary>
    public void ClearAll()
    {
        Clear();
        furnitureDoors.Clear();
    }
}
