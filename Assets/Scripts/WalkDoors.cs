using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Real door leaves in the walk-through for every door the user has set up in edit mode (type, hinge, swing):
/// hinged leaves swing open 90 degrees into the room they open into, sliding ones slide aside. A door opens by
/// itself when you come close and closes again once you've walked on; the right trigger on a leaf opens/closes it
/// by hand (that choice sticks until you walk away). Closed leaves block you like walls. Doors without settings
/// keep the plain see-through panel you simply walk through.
/// </summary>
public class WalkDoors
{
    const float LeafThickness = 0.04f;
    const float AutoOpenDist = 1.1f;     // horizontal distance from the door's middle
    const float ForgetManualDist = 3f;   // walk this far away and a door goes back to opening by itself
    const float OpenSpeed = 2f;          // full open/close per second (~0.5 s)
    static readonly Color LeafColor = new Color(0.62f, 0.45f, 0.28f);

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
        public readonly List<Collider> colliders = new List<Collider>();
        public float open;          // 0 closed .. 1 open
        public bool? manual;
    }

    readonly List<Door> doors = new List<Door>();
    readonly List<GameObject> created = new List<GameObject>();
    /// <summary>The plain see-through door panels replaced by real leaves - the walk-through keeps them hidden.</summary>
    public readonly HashSet<Renderer> Suppressed = new HashSet<Renderer>();

    public void Build(GameObject visual, float yaw, Vector3 center, List<MRUKRoom> rooms, HouseEdits edits, int layer, IEnumerable<Renderer> doorPanels)
    {
        Clear();
        if (!visual || edits == null || edits.doors.Count == 0) return;
        Quaternion g = Quaternion.Euler(0, yaw, 0);
        Vector3 M(Vector3 p) => g * (p - center);

        foreach (var d in DoorCatalog.Build(rooms))
        {
            var e = DoorCatalog.FindEdit(edits, d.anchor, rooms);
            if (e == null) continue;

            Vector3 doorMid = M(d.center);
            foreach (var r in doorPanels)
                if (r && Vector3.Distance(visual.transform.InverseTransformPoint(r.bounds.center), doorMid) < 0.35f) { Suppressed.Add(r); r.enabled = false; }
            if (e.kind == DoorKind.Opening) continue; // just the empty opening

            DoorCatalog.Swing(d, e, out var hinge, out var swing, out var toOther);
            Vector3 hingeM = M(hinge), swingM = g * swing, toOtherM = g * toOther;
            float h = d.height - 0.01f, w = d.width;
            var door = new Door { center = M(d.center - Vector3.up * (d.height / 2f)) };

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
                    leaf.pivot = MakeLeaf(visual, layer, door, p, toOtherM, w, h);
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
        return new Leaf { pivot = MakeLeaf(visual, layer, door, hinge, toOther, width, h), sign = sign };
    }

    /// <summary>A pivot at 'pos' (model space, floor height) with a leaf box running along 'along' for 'width'.</summary>
    Transform MakeLeaf(GameObject visual, int layer, Door door, Vector3 pos, Vector3 along, float width, float h)
    {
        var pivot = new GameObject("DoorLeafPivot");
        pivot.layer = layer;
        pivot.transform.SetParent(visual.transform, false);
        pivot.transform.localPosition = pos;
        created.Add(pivot);

        var part = XRModelFactory.CreateBoxPart("DOOR_LEAF", new Vector3(0, h / 2f, 0) + along * (width / 2f),
            Quaternion.LookRotation(along, Vector3.up), new Vector3(LeafThickness, h, width), "DOORLEAF", Vector3.zero, 0f);
        part.color = LeafColor;
        var model = new XRHouseModel();
        model.rooms.Add(new XRRoomModel { roomName = "Leaf", parts = { part } });
        var go = UnityModelLoader.LoadToScene(model, shaded: true);
        go.transform.SetParent(pivot.transform, false);
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
        {
            mf.gameObject.layer = layer;
            var col = mf.gameObject.AddComponent<MeshCollider>();
            col.sharedMesh = mf.sharedMesh;
            door.colliders.Add(col);
        }
        return pivot.transform;
    }

    /// <summary>Opens/closes doors around you. headLocal = your head in the model's own (visual-local) space.</summary>
    public bool Tick(Vector3 headLocal, float dt)
    {
        bool moved = false;
        foreach (var d in doors)
        {
            float dist = Vector2.Distance(new Vector2(headLocal.x, headLocal.z), new Vector2(d.center.x, d.center.z));
            bool sameStory = headLocal.y > d.center.y - 0.5f && headLocal.y < d.center.y + 2.8f;
            if (d.manual.HasValue && (dist > ForgetManualDist || !sameStory)) d.manual = null;
            bool target = d.manual ?? (sameStory && dist < AutoOpenDist);
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

    /// <summary>Trigger on a leaf: open it if it's (mostly) closed, else close it.</summary>
    public bool Toggle(Collider c)
    {
        var d = doors.FirstOrDefault(x => x.colliders.Contains(c));
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
            Object.Destroy(go);
        }
        created.Clear();
        doors.Clear();
    }
}
