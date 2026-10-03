using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>One physical door of the scanned house (shared doorways de-duplicated), in the scan's world frame.</summary>
public class DoorInfo
{
    public MRUKAnchor anchor;
    /// <summary>Every anchor of this doorway (one per room that captured it).</summary>
    public readonly List<MRUKAnchor> anchors = new List<MRUKAnchor>();
    public string uuid;
    public Vector3 center;          // middle of the opening
    public Vector3 right, normal;   // horizontal unit vectors: along the wall / out of the anchor's face
    public float width, height;
    public MRUKRoom roomFront, roomBack; // room on the +normal / -normal side (null = outside / not scanned)
    public bool isWindow;
    public float FloorY => center.y - height / 2f;
}

/// <summary>
/// Doors of the scanned house and how a user's DoorEdit (HouseEdits) maps onto them - shared by the edit tool, the
/// walk-through and the floor plans, so all of them agree on which way a door opens.
/// </summary>
public static class DoorCatalog
{
    const float SideProbe = 0.4f;       // how far either side of the door to look for the room it leads into
    const float EditMatchDist = 0.4f;

    public static string RoomId(MRUKRoom room) => room != null ? room.Anchor.Uuid.ToString() : "";

    /// <summary>
    /// Whether a door frame at c (facing n) is the same physical doorway as d. A doorway between two scanned rooms
    /// is usually captured twice - once from each room, on that room's own face of the wall - so the two anchors
    /// sit a wall's thickness apart (often 20-40 cm), facing opposite ways. Two real doors that close together
    /// in the same wall line don't exist.
    /// </summary>
    public static bool SameDoorway(DoorInfo d, Vector3 c, Vector3 n)
    {
        Vector3 delta = c - d.center;
        return Mathf.Abs(Vector3.Dot(n, d.normal)) > 0.9f
            && Mathf.Abs(Vector3.Dot(delta, d.normal)) < 0.6f
            && Mathf.Abs(Vector3.Dot(delta, d.right)) < 0.35f
            && Mathf.Abs(delta.y) < 0.4f;
    }

    public static List<DoorInfo> Build(List<MRUKRoom> rooms)
    {
        var result = new List<DoorInfo>();
        var all = rooms.SelectMany(r => r.Anchors)
            .Where(a => a != null && a.PlaneRect.HasValue && (MRUKDataProcessor.IsDoor(a) || MRUKDataProcessor.IsWindow(a))).ToList();
        foreach (var a in all)
        {
            bool isWindow = !MRUKDataProcessor.IsDoor(a);
            Frame(a, out var c, out var r, out var n, out float w, out float h);
            var same = result.FirstOrDefault(d => d.isWindow == isWindow && SameDoorway(d, c, n));
            if (same != null)
            {
                // The other room's view of the same doorway: use the middle of the wall between the two faces.
                if (!same.anchors.Contains(a))
                {
                    same.anchors.Add(a);
                    same.center = (same.center + c) / 2f;
                    same.roomFront = FindRoomAt(rooms, same.center + same.normal * SideProbe);
                    same.roomBack = FindRoomAt(rooms, same.center - same.normal * SideProbe);
                }
                continue;
            }
            var door = new DoorInfo
            {
                anchor = a, uuid = a.Anchor.Uuid.ToString(),
                center = c, right = r, normal = n, width = w, height = h,
                roomFront = FindRoomAt(rooms, c + n * SideProbe),
                roomBack = FindRoomAt(rooms, c - n * SideProbe),
                isWindow = isWindow,
            };
            door.anchors.Add(a);
            result.Add(door);
        }
        return result;
    }

    /// <summary>True if the user marked this door anchor's doorway as "no door, wall" (DoorKind.Wall).</summary>
    public static bool IsWalledUp(MRUKAnchor a)
    {
        var edits = HouseEditsStore.Current;
        if (a == null || edits == null || !edits.doors.Any(d => d.kind == DoorKind.Wall)) return false;
        if (!MRUKDataProcessor.IsDoor(a) && !MRUKDataProcessor.IsWindow(a)) return false;
        var rooms = MRUK.Instance != null ? MRUK.Instance.Rooms : new List<MRUKRoom>();
        var e = FindEdit(edits, a, rooms);
        return e != null && e.kind == DoorKind.Wall;
    }

    /// <summary>Changes whenever the set of walled-up doorways changes - views rebuild their model only then.</summary>
    public static string WalledUpSignature(HouseEdits edits) =>
        edits == null ? "" : string.Join(",", edits.doors.Where(d => d.kind == DoorKind.Wall).Select(d => d.anchorUuid).OrderBy(s => s));

    /// <summary>The edit of a doorway, whichever of its anchors it was saved for (or by position).</summary>
    public static DoorEdit FindEdit(HouseEdits edits, DoorInfo d, List<MRUKRoom> rooms)
    {
        foreach (var a in d.anchors)
        {
            var e = FindEdit(edits, a, rooms);
            if (e != null) return e;
        }
        return null;
    }

    /// <summary>A door anchor's centre, horizontal along-wall/normal directions and size.</summary>
    public static void Frame(MRUKAnchor a, out Vector3 center, out Vector3 right, out Vector3 normal, out float width, out float height)
    {
        center = a.transform.position;
        normal = Vector3.ProjectOnPlane(a.transform.forward, Vector3.up);
        if (normal.sqrMagnitude < 1e-6f) normal = Vector3.forward;
        normal.Normalize();
        right = Vector3.Cross(Vector3.up, normal);
        width = a.PlaneRect.Value.width;
        height = a.PlaneRect.Value.height;
    }

    /// <summary>The room whose floor contains p (same story only - p must sit between its floor and ceiling).</summary>
    public static MRUKRoom FindRoomAt(List<MRUKRoom> rooms, Vector3 p)
    {
        foreach (var room in rooms)
            foreach (var f in room.FloorAnchors)
            {
                if (f == null || f.PlaneBoundary2D == null || f.PlaneBoundary2D.Count < 3) continue;
                float dy = p.y - f.transform.position.y;
                if (dy < -0.3f || dy > 2.8f) continue;
                var poly = f.PlaneBoundary2D.Select(q => f.transform.position + f.transform.rotation * new Vector3(q.x, q.y, 0)).ToList();
                if (Inside(poly, p)) return room;
            }
        return null;
    }

    static bool Inside(List<Vector3> poly, Vector3 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            Vector3 a = poly[i], b = poly[j];
            if ((a.z > p.z) != (b.z > p.z) && p.x < (b.x - a.x) * (p.z - a.z) / (b.z - a.z) + a.x) inside = !inside;
        }
        return inside;
    }

    /// <summary>The user's edit for this door anchor, if any: by anchor UUID, else by position (a shared doorway has
    /// one anchor per room - an edit made on either applies to both).</summary>
    public static DoorEdit FindEdit(HouseEdits edits, MRUKAnchor a, List<MRUKRoom> rooms)
    {
        if (edits == null || a == null) return null;
        string id = a.Anchor.Uuid.ToString();
        var byId = edits.doors.FirstOrDefault(d => d.anchorUuid == id);
        if (byId != null) return byId;
        foreach (var d in edits.doors)
        {
            var room = rooms.FirstOrDefault(r => RoomId(r) == d.roomUuid);
            if (room == null || d.localPos == null) continue;
            Vector3 w = room.transform.TransformPoint(new Vector3(d.localPos.x, d.localPos.y, d.localPos.z));
            if (Vector3.Distance(w, a.transform.position) < EditMatchDist) return d;
        }
        return null;
    }

    /// <summary>A new edit keyed to this door (anchor UUID + its room-local position).</summary>
    public static DoorEdit NewEdit(DoorInfo d)
    {
        var room = d.anchor.Room;
        Vector3 local = room != null ? room.transform.InverseTransformPoint(d.center) : d.center;
        return new DoorEdit { anchorUuid = d.uuid, roomUuid = RoomId(room), localPos = new Vector3Data(local) };
    }

    /// <summary>
    /// Where the hinge is and which way the leaf swings, for a door frame (centre/right/normal/width) and its edit.
    /// hinge is on the floor at one jamb; swingDir points into the room the leaf opens into; toOther runs from the
    /// hinge towards the opposite jamb. Hinge side is "as seen standing in the room the door opens into".
    /// </summary>
    public static void Swing(Vector3 center, Vector3 right, Vector3 normal, float width, float height, DoorEdit e,
        MRUKRoom roomFront, MRUKRoom roomBack, out Vector3 hinge, out Vector3 swingDir, out Vector3 toOther)
    {
        float s;
        string into = e.opensIntoRoomUuid ?? "";
        if (RoomId(roomFront) == into) s = 1f;
        else if (RoomId(roomBack) == into) s = -1f;
        // Room not among those passed in (e.g. a single-room plan): it must be the side we know nothing about.
        else if (roomFront != null && roomBack == null) s = -1f;
        else s = 1f;
        swingDir = normal * s;
        // Standing in that room facing the door you look along -swingDir; your right hand is Cross(up, look).
        Vector3 viewerRight = Vector3.Cross(Vector3.up, -swingDir);
        Vector3 floorCenter = center - Vector3.up * (height / 2f);
        float side = e.hinge == HingeSide.Left ? -1f : 1f;
        hinge = floorCenter + viewerRight * (side * width / 2f);
        toOther = -viewerRight * side;
    }

    /// <summary>A window's parts with their span as fractions of its width (left to right, seen from the room it
    /// opens into). A window edited before parts existed is one part of its own kind.</summary>
    public static List<(SectionEdit s, float f0, float f1)> Sections(DoorEdit e)
    {
        var list = new List<(SectionEdit, float, float)>();
        var secs = e.sections != null && e.sections.Count > 0 ? e.sections
            : e.kind == DoorKind.Double
                ? new List<SectionEdit> { new SectionEdit { kind = DoorKind.Single, hinge = HingeSide.Left }, new SectionEdit { kind = DoorKind.Single, hinge = HingeSide.Right } }
                : new List<SectionEdit> { new SectionEdit { kind = e.kind, hinge = e.hinge } };
        for (int i = 0; i < secs.Count; i++) list.Add((secs[i], i / (float)secs.Count, (i + 1) / (float)secs.Count));
        return list;
    }

    /// <summary>Left jamb at the sill (seen from the room it opens into), the direction to the right jamb, and the
    /// direction into that room.</summary>
    public static void LeftFrame(DoorInfo d, DoorEdit e, out Vector3 left, out Vector3 toRight, out Vector3 swingDir)
    {
        var tmp = new DoorEdit { opensIntoRoomUuid = e.opensIntoRoomUuid, hinge = HingeSide.Left };
        Swing(d, tmp, out left, out swingDir, out toRight);
    }

    public static void Swing(DoorInfo d, DoorEdit e, out Vector3 hinge, out Vector3 swingDir, out Vector3 toOther) =>
        Swing(d.center, d.right, d.normal, d.width, d.height, e, d.roomFront, d.roomBack, out hinge, out swingDir, out toOther);
}
