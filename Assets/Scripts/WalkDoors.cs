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
    static readonly Color LeafColor = new Color(0.62f, 0.45f, 0.28f);
    static readonly Color GarageColor = new Color(0.72f, 0.73f, 0.75f);
    static readonly Color GlassColor = new Color(0.70f, 0.85f, 1f, 0.35f);
    static readonly Color HandleColor = new Color(0.30f, 0.30f, 0.32f);

    enum Motion { Swing, Slide, Roll, Tilt }

    class Leaf
    {
        public Transform pivot;
        public Motion motion;
        public float sign;          // swing/tilt: which way round opens it
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
    }

    readonly List<Door> doors = new List<Door>();
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

            DoorCatalog.Swing(d, e, out var hinge, out var swing, out var toOther);
            Vector3 hingeM = M(hinge), swingM = g * swing, toOtherM = g * toOther;
            float h = d.height - 0.01f, w = d.width;
            var door = new Door { center = M(d.center - Vector3.up * (d.height / 2f)), isWindow = d.isWindow, garage = e.kind == DoorKind.Garage };
            door.manual = wasOpen.Any(p => Vector3.Distance(p, door.center) < 0.1f);
            float handleH = d.isWindow ? h / 2f : HandleHeight;

            switch (e.kind)
            {
                case DoorKind.Single:
                    door.leaves.Add(Hinged(visual, layer, door, hingeM, toOtherM, swingM, w, h, handleH));
                    break;
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
            doors.Add(door);
        }
    }

    enum LeafStyle { Door, SlidingDoor, Window, SlidingWindow, Garage }

    Leaf Hinged(GameObject visual, int layer, Door door, Vector3 hinge, Vector3 toOther, Vector3 swing, float width, float h, float handleH)
    {
        // Rotating a vector by +90 degrees about up turns it into Cross(up, v) - so that's the way to open if it
        // points into the swing room, otherwise the other way round.
        float sign = Vector3.Dot(Vector3.Cross(Vector3.up, toOther), swing) >= 0f ? 1f : -1f;
        var style = door.isWindow ? LeafStyle.Window : LeafStyle.Door;
        return new Leaf { motion = Motion.Swing, pivot = MakeLeaf(visual, layer, door, hinge, toOther, width, h, handleH, style), sign = sign };
    }

    /// <summary>A pivot at 'pos' (model space) with a leaf running along 'along' for 'width' (garage: hanging down
    /// from the pivot), handles on both faces, and a trigger zone around them for opening by hand.</summary>
    Transform MakeLeaf(GameObject visual, int layer, Door door, Vector3 pos, Vector3 along, float width, float h, float handleH, LeafStyle style)
    {
        var pivot = new GameObject("LeafPivot");
        pivot.layer = layer;
        pivot.transform.SetParent(visual.transform, false);
        pivot.transform.localPosition = pos;
        created.Add(pivot);

        bool window = style == LeafStyle.Window || style == LeafStyle.SlidingWindow;
        bool garage = style == LeafStyle.Garage;
        float thick = window ? WindowThickness : LeafThickness;
        float bottom = garage ? -h : 0f; // garage leaf hangs below its pivot
        Quaternion leafRot = Quaternion.LookRotation(along, Vector3.up);
        Vector3 face = Vector3.Cross(Vector3.up, along); // leaf normal (one side; the other is -face)

        var parts = new List<XRMeshPart>();
        var leafPart = XRModelFactory.CreateBoxPart(window ? "WINDOW_LEAF" : "DOOR_LEAF", new Vector3(0, bottom + h / 2f, 0) + along * (width / 2f),
            leafRot, new Vector3(thick, h, width), "DOORLEAF", Vector3.zero, 0f);
        leafPart.color = window ? new Color(GlassColor.r, GlassColor.g, GlassColor.b) : garage ? GarageColor : LeafColor;
        parts.Add(leafPart);
        if (garage)
            for (int k = 1; k < 8; k++) // slats
                parts.Add(HandlePart(new Vector3(0, -h + h * k / 8f, 0) + along * (width / 2f), leafRot, new Vector3(thick + 0.01f, 0.012f, width), GarageColor * 0.85f));

        Vector3 handleBase = garage ? along * (width / 2f) + Vector3.up * (handleH - h) : along * (width - HandleInset) + Vector3.up * handleH;
        foreach (float side in new[] { 1f, -1f })
        {
            Vector3 n = face * side;
            if (garage) parts.Add(HandlePart(handleBase + n * (thick / 2f + 0.015f), leafRot, new Vector3(0.025f, 0.03f, 0.2f), HandleColor));
            else if (style == LeafStyle.SlidingDoor || style == LeafStyle.SlidingWindow)
                parts.Add(HandlePart(handleBase + n * (thick / 2f + 0.015f), leafRot, new Vector3(0.025f, window ? 0.12f : 0.25f, 0.025f), HandleColor));
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
                var m = new Material(glass) { color = new Color(1f, 1f, 1f, GlassColor.a) };
                materials.Add(m);
                mr.sharedMaterial = m;
            }
        }

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
        foreach (var d in doors)
        {
            bool target = d.manual;
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
                    case Motion.Swing: l.pivot.localRotation = Quaternion.Euler(0f, l.sign * 90f * ease, 0f); break;
                    case Motion.Slide: l.pivot.localPosition = l.closedPos + l.slideDir * (l.slide * ease); break;
                    case Motion.Roll: l.pivot.localScale = new Vector3(1f, Mathf.Lerp(1f, 0.04f, ease), 1f); break;
                    case Motion.Tilt: l.pivot.localRotation = Quaternion.AngleAxis(l.sign * TiltDeg * ease, l.axis); break;
                }
            }
            moved = true;
        }
        return moved;
    }

    /// <summary>Trigger on a handle: open it if it's (mostly) closed, else close it.</summary>
    public bool Toggle(Collider c)
    {
        var d = doors.FirstOrDefault(x => x.handles.Contains(c));
        if (d == null) return false;
        d.manual = d.open < 0.5f;
        return true;
    }

    public void Clear()
    {
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
}
