using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Real door leaves in the walk-through for every door the user has set up in edit mode (type, hinge, swing):
/// hinged leaves swing open 90 degrees into the room they open into, sliding ones slide aside. Every leaf has a
/// lever handle on both faces at the latch edge, pointing towards the hinge like a real one.
/// Opening: point the ray at a handle and pull the trigger (open/close). In edit mode doors also open by
/// themselves as you come close, so they never get in the way while editing.
/// Closed leaves block you like walls. Doors without settings keep the plain see-through panel.
/// </summary>
public class WalkDoors
{
    const float LeafThickness = 0.04f;
    const float HandleHeight = 1.05f;    // above the floor
    const float HandleInset = 0.07f;     // from the leaf's free (latch) edge
    const float AutoOpenDist = 1.1f;     // edit mode: horizontal distance from the door's middle
    const float OpenSpeed = 2f;          // full open/close per second (~0.5 s)
    static readonly Color LeafColor = new Color(0.62f, 0.45f, 0.28f);
    static readonly Color HandleColor = new Color(0.30f, 0.30f, 0.32f);

    class Leaf
    {
        public Transform pivot;
        public bool sliding;
        public float sign;          // hinged: +1/-1 = which way round 90 degrees opens it
        public Vector3 closedPos;   // sliding: pivot position when closed
        public Vector3 slideDir;    // sliding: model-space direction it slides open
        public float slide;         // sliding distance
    }

    class Door
    {
        public Vector3 center;      // model space, at floor height
        public readonly List<Leaf> leaves = new List<Leaf>();
        public readonly List<Collider> handles = new List<Collider>(); // trigger zones around the handles
        public float open;          // 0 closed .. 1 open
        public bool manual;         // opened by hand (trigger on a handle)
    }

    readonly List<Door> doors = new List<Door>();
    readonly List<GameObject> created = new List<GameObject>();
    /// <summary>The plain see-through door panels replaced by real leaves - the walk-through keeps them hidden.</summary>
    public readonly HashSet<Renderer> Suppressed = new HashSet<Renderer>();

    public void Build(GameObject visual, float yaw, Vector3 center, List<MRUKRoom> rooms, HouseEdits edits, int layer, IEnumerable<Renderer> doorPanels)
    {
        // Keep doors that were open open across a rebuild (an edit elsewhere shouldn't slam them shut).
        var wasOpen = doors.Where(d => d.manual).Select(d => d.center).ToList();
        Clear();
        if (!visual || edits == null || edits.doors.Count == 0) return;
        Quaternion g = Quaternion.Euler(0, yaw, 0);
        Quaternion gInv = Quaternion.Inverse(g);
        Vector3 M(Vector3 p) => g * (p - center);

        foreach (var d in DoorCatalog.Build(rooms))
        {
            var e = DoorCatalog.FindEdit(edits, d, rooms);
            if (e == null) continue;

            // Hide this doorway's see-through panels - one per room that captured it, on that room's wall face.
            foreach (var r in doorPanels)
            {
                if (!r) continue;
                Vector3 scanPos = gInv * visual.transform.InverseTransformPoint(r.bounds.center) + center;
                if (DoorCatalog.SameDoorway(d, scanPos, d.normal)) { Suppressed.Add(r); r.enabled = false; }
            }
            if (e.kind == DoorKind.Opening) continue; // just the empty opening

            DoorCatalog.Swing(d, e, out var hinge, out var swing, out var toOther);
            Vector3 hingeM = M(hinge), swingM = g * swing, toOtherM = g * toOther;
            float h = d.height - 0.01f, w = d.width;
            var door = new Door { center = M(d.center - Vector3.up * (d.height / 2f)) };
            door.manual = wasOpen.Any(p => Vector3.Distance(p, door.center) < 0.1f);

            switch (e.kind)
            {
                case DoorKind.Single:
                    door.leaves.Add(Hinged(visual, layer, door, hingeM, toOtherM, swingM, w, h));
                    break;
                case DoorKind.Double:
                    door.leaves.Add(Hinged(visual, layer, door, hingeM, toOtherM, swingM, w / 2f, h));
                    door.leaves.Add(Hinged(visual, layer, door, hingeM + toOtherM * w, -toOtherM, swingM, w / 2f, h));
                    break;
                case DoorKind.Sliding:
                {
                    Vector3 p = hingeM + swingM * (LeafThickness / 2f + 0.02f);
                    var leaf = new Leaf { sliding = true, closedPos = p, slideDir = -toOtherM, slide = w };
                    leaf.pivot = MakeLeaf(visual, layer, door, p, toOtherM, w, h, true);
                    door.leaves.Add(leaf);
                    break;
                }
            }
            doors.Add(door);
        }
    }

    Leaf Hinged(GameObject visual, int layer, Door door, Vector3 hinge, Vector3 toOther, Vector3 swing, float width, float h)
    {
        // Rotating a vector by +90 degrees about up turns it into Cross(up, v) - so that's the way to open if it
        // points into the swing room, otherwise the other way round.
        float sign = Vector3.Dot(Vector3.Cross(Vector3.up, toOther), swing) >= 0f ? 1f : -1f;
        return new Leaf { pivot = MakeLeaf(visual, layer, door, hinge, toOther, width, h, false), sign = sign };
    }

    /// <summary>A pivot at 'pos' (model space, floor height) with a leaf running along 'along' for 'width', a
    /// handle on both faces at the free edge, and a trigger zone around each handle for opening by hand.</summary>
    Transform MakeLeaf(GameObject visual, int layer, Door door, Vector3 pos, Vector3 along, float width, float h, bool sliding)
    {
        var pivot = new GameObject("DoorLeafPivot");
        pivot.layer = layer;
        pivot.transform.SetParent(visual.transform, false);
        pivot.transform.localPosition = pos;
        created.Add(pivot);

        Quaternion leafRot = Quaternion.LookRotation(along, Vector3.up);
        Vector3 face = Vector3.Cross(Vector3.up, along); // leaf normal (one side; the other is -face)
        var parts = new List<XRMeshPart>();
        var leafPart = XRModelFactory.CreateBoxPart("DOOR_LEAF", new Vector3(0, h / 2f, 0) + along * (width / 2f),
            leafRot, new Vector3(LeafThickness, h, width), "DOORLEAF", Vector3.zero, 0f);
        leafPart.color = LeafColor;
        parts.Add(leafPart);

        Vector3 handleBase = along * (width - HandleInset) + Vector3.up * HandleHeight;
        foreach (float side in new[] { 1f, -1f })
        {
            Vector3 n = face * side;
            if (sliding)
            {
                // A vertical pull bar.
                parts.Add(HandlePart(handleBase + n * (LeafThickness / 2f + 0.015f), leafRot, new Vector3(0.025f, 0.25f, 0.025f)));
            }
            else
            {
                // Rose + lever, the lever pointing back towards the hinge.
                parts.Add(HandlePart(handleBase + n * (LeafThickness / 2f + 0.025f), leafRot, new Vector3(0.05f, 0.02f, 0.02f)));
                parts.Add(HandlePart(handleBase + n * (LeafThickness / 2f + 0.05f) - along * 0.06f, leafRot, new Vector3(0.022f, 0.022f, 0.13f)));
            }
        }

        var model = new XRHouseModel();
        model.rooms.Add(new XRRoomModel { roomName = "Leaf", parts = parts });
        var go = UnityModelLoader.LoadToScene(model, shaded: true);
        go.transform.SetParent(pivot.transform, false);
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
        {
            mf.gameObject.layer = layer;
            if (mf.name != "DOOR_LEAF") continue; // handles are covered by the trigger zone below
            mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
        }

        // Generous trigger zone around both handles - walking ignores triggers, the handle ray doesn't.
        var zone = new GameObject("HandleZone");
        zone.layer = layer;
        zone.transform.SetParent(pivot.transform, false);
        zone.transform.localPosition = handleBase;
        zone.transform.localRotation = leafRot;
        var box = zone.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(LeafThickness + 0.24f, 0.35f, 0.3f);
        door.handles.Add(box);
        return pivot.transform;
    }

    static XRMeshPart HandlePart(Vector3 pos, Quaternion rot, Vector3 size)
    {
        var p = XRModelFactory.CreateBoxPart("DOOR_HANDLE", pos, rot, size, "HANDLE", Vector3.zero, 0f);
        p.color = HandleColor;
        return p;
    }

    /// <summary>Animates doors. autoOpen (edit mode): doors near you open by themselves; otherwise only by hand.
    /// headLocal = your head in the model's own (visual-local) space.</summary>
    public bool Tick(Vector3 headLocal, float dt, bool autoOpen)
    {
        bool moved = false;
        foreach (var d in doors)
        {
            bool target = d.manual;
            if (autoOpen)
            {
                float dist = Vector2.Distance(new Vector2(headLocal.x, headLocal.z), new Vector2(d.center.x, d.center.z));
                bool sameStory = headLocal.y > d.center.y - 0.5f && headLocal.y < d.center.y + 2.8f;
                target |= sameStory && dist < AutoOpenDist;
            }
            float next = Mathf.MoveTowards(d.open, target ? 1f : 0f, dt * OpenSpeed);
            if (Mathf.Approximately(next, d.open)) continue;
            d.open = next;
            float ease = Mathf.SmoothStep(0f, 1f, next);
            foreach (var l in d.leaves)
            {
                if (!l.pivot) continue;
                if (l.sliding) l.pivot.localPosition = l.closedPos + l.slideDir * (l.slide * ease);
                else l.pivot.localRotation = Quaternion.Euler(0f, l.sign * 90f * ease, 0f);
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
        foreach (var go in created)
        {
            if (!go) continue;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true)) if (mf.sharedMesh) Object.Destroy(mf.sharedMesh);
            foreach (var mr in go.GetComponentsInChildren<Renderer>(true)) if (mr.sharedMaterial) Object.Destroy(mr.sharedMaterial);
            go.transform.SetParent(null, false);
            Object.Destroy(go);
        }
        created.Clear();
        doors.Clear();
    }
}
