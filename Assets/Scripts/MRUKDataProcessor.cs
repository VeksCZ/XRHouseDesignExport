using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
using Meta.XR;
#endif

public static class MRUKDataProcessor
{
    public static string GetRoomLabel(MRUKRoom room)
    {
        if (room == null) return "Unknown";

    #if META_XR_SDK_INSTALLED
        if (RoomNames.TryGet(room.Anchor.Uuid.ToString(), out var customName)) return customName;

        // Note: as of the currently installed SDK, OVRSemanticLabels.Classification has no room-type
        // values (no Bedroom/Kitchen/LivingRoom/...) - only object/surface labels (Table, Bed, Couch,
        // WallFace, ...). Quest's Space Setup doesn't expose a free-text or picked room name to apps
        // either. So these two tiers can only ever surface an object classification that happens to be
        // attached to the room's own anchors, not an actual room name; tier 3 below (furniture-based
        // heuristics) is what actually determines BEDROOM/KITCHEN/etc. today.
        var classifications = new List<OVRSemanticLabels.Classification>();

        // 1. Try to find an anchor that represents the room itself
        var roomAnchor = room.Anchors.FirstOrDefault(a => a.Label.ToString().ToUpper() == "ROOM");
        if (roomAnchor != null && roomAnchor.Anchor.TryGetComponent<OVRSemanticLabels>(out var roomSemantic))
        {
            roomSemantic.GetClassifications(classifications);
            foreach (var c in classifications)
            {
                string l = c.ToString().ToUpperInvariant();
                if (l != "OTHER") return l;
            }
        }

        // 2. Fallback to existing semantic labels on any anchor
        if (room.Anchor.TryGetComponent<OVRSemanticLabels>(out var semantic))
        {
            semantic.GetClassifications(classifications);
            foreach (var c in classifications)
            {
                string l = c.ToString().ToUpperInvariant();
                if (l != "OTHER" && l != "STORAGE" && l != "CEILING" && l != "FLOOR" && !l.Contains("WALL"))
                {
                    return l;
                }
            }
        }

        // 2. MRUKAnchor Label
var mrukAnchor = room.GetComponent<MRUKAnchor>();
        if (mrukAnchor != null)
        {
            string label = mrukAnchor.Label.ToString().ToUpperInvariant();
            if (label != "NONE" && label != "OTHER" && label != "ROOM" && label != "STORAGE" && label != "INNER_WALL_FACE" && label != "CEILING" && label != "FLOOR")
            {
                return label;
            }
        }

        // 3. Furniture-based heuristics, most telling piece first
        bool Has(params string[] parts) => room.Anchors.Any(a => a != null && parts.Any(p => a.Label.ToString().ToUpperInvariant().Contains(p)));
        if (Has("BED")) return "BEDROOM";
        if (Has("KITCHEN", "OVEN", "STOVE")) return "KITCHEN";
        if (Has("SINK", "TOILET", "SHOWER")) return "BATHROOM";
        if (Has("COUCH", "TELEVISION")) return "LIVING_ROOM";
        if (Has("SCREEN") && Has("TABLE")) return "OFFICE";
    #endif

        // No hint at all: "ROOM_" plus the first characters of the room's id, so unnamed rooms stay distinguishable.
        string id = room.name;
        foreach (var prefix in new[] { "Room - ", "Room_" }) if (id.StartsWith(prefix)) id = id.Substring(prefix.Length);
        id = id.Replace("-", "");
        return "ROOM_" + (id.Length >= 4 ? id.Substring(0, 4).ToUpperInvariant() : (string.IsNullOrEmpty(id) ? "UNKNOWN" : id.ToUpperInvariant()));
        }

    public static string GenerateSceneDump(List<MRUKRoom> rooms)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SCENE DUMP - CLEAN SEMANTIC SCAN ===");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Total rooms: {rooms.Count}\n");

        foreach (var room in rooms)
        {
            string label = GetRoomLabel(room);
            sb.AppendLine($">>> [ROOM] {room.name} | Semantic: {label} | UUID: {room.Anchor.Uuid}");
            sb.AppendLine($"    Pos: {room.transform.position.ToString("F3")} | Rot: {room.transform.eulerAngles.ToString("F1")}");

    #if META_XR_SDK_INSTALLED
            if (room.GlobalMeshAnchor != null)
                sb.AppendLine("   → GlobalMeshAnchor: AVAILABLE");

            var anchors = room.Anchors.ToList();
            foreach (var anchorComp in anchors)
            {
                if (anchorComp == null) continue;

                sb.AppendLine($"   --- [ANCHOR] {anchorComp.name} | Label: {anchorComp.Label} | Rot: {anchorComp.transform.eulerAngles.ToString("F1")}");

                var anchor = anchorComp.Anchor;
                if (anchor.TryGetComponent<OVRTriangleMesh>(out var triangleMesh))
                {
                    if (triangleMesh.TryGetCounts(out int verts, out int tris))
                        sb.AppendLine($"        TriangleMesh: {verts} vertices, {tris} triangles");
                }
            }
    #endif
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string GenerateJson(List<MRUKRoom> rooms)
    {
        var data = new UltraHouseData
        {
            exportDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            rooms = new List<UltraRoom>()
        };

        foreach (var r in rooms)
        {
            var ur = new UltraRoom
            {
                name = GetRoomLabel(r),
                guid = r.Anchor.Uuid.ToString(),
                pos = new Vector3Data(r.transform.position),
                rot = new Vector4Data(r.transform.rotation),
                anchors = new List<UltraAnchor>()
            };

            foreach (var a in r.Anchors)
            {
                if (a == null) continue;

                var ua = new UltraAnchor
                {
                    label = a.Label.ToString(),
                    pos = new Vector3Data(a.transform.position),
                    rot = new Vector4Data(a.transform.rotation),
                    rect = a.PlaneRect.HasValue 
                        ? new OfflineRect { w = a.PlaneRect.Value.width, h = a.PlaneRect.Value.height } 
                        : null,
                    volume = a.VolumeBounds.HasValue 
                        ? new Vector3Data(a.VolumeBounds.Value.size) 
                        : null,
                    points = a.PlaneBoundary2D != null 
                        ? a.PlaneBoundary2D.Select(p => new Vector2Data(p)).ToList()
                        : new List<Vector2Data>()
                };

                // Semantic labels handling
                List<string> labelList = new List<string>();
    #if META_XR_SDK_INSTALLED
                if (a.Anchor.TryGetComponent<OVRSemanticLabels>(out var s))
                {
                    var anchorClassifications = new List<OVRSemanticLabels.Classification>();
                    s.GetClassifications(anchorClassifications);
                    foreach (var c in anchorClassifications) labelList.Add(c.ToString());
                }
    #endif
                ua.allLabels = labelList;
                ur.anchors.Add(ua);
            }
            data.rooms.Add(ur);
        }

        return JsonUtility.ToJson(data, true);
    }

    /// <summary>
    /// Single source of truth for which rooms are considered real, exportable rooms.
    /// Used by MRUKExporter and DollHouseVisualizer so the export and the in-headset
    /// preview always agree on the same room set (dedup by anchor UUID + area filtering).
    /// </summary>
    public static List<MRUKRoom> GetValidRooms(MRUK mruk)
    {
        if (mruk == null) return new List<MRUKRoom>();

        var valid = mruk.Rooms
            .GroupBy(r => r.Anchor.Uuid)
            .Select(g => g.First())
            .Where(r => {
                // "ROOM" isn't a value in the enum-based Classification type (only object/surface
                // labels are), so unlike the other .Labels usages in this file this one can't be
                // swapped for GetClassifications() without losing the check entirely.
                if (r.Anchor.TryGetComponent<OVRSemanticLabels>(out var labels)) {
                #pragma warning disable 0618
                    if (labels.Labels.ToUpperInvariant().Contains("ROOM")) return true;
                #pragma warning restore 0618
                }
                return r.Anchors.Any(a => a.Label == MRUKAnchor.SceneLabels.FLOOR);
            })
            .Where(IsRoomAreaValid)
            .ToList();
        return DropDuplicateScans(valid);
    }

    /// <summary>
    /// MRUK keeps every Space Setup scan, so one physical room can appear as several rooms lying on
    /// top of each other. Keep the most detailed scan of each, preserving the original order.
    /// </summary>
    static List<MRUKRoom> DropDuplicateScans(List<MRUKRoom> rooms)
    {
        var keptRooms = new List<MRUKRoom>();
        var keptOutlines = new List<RoomOutline>();
        foreach (var room in rooms.OrderByDescending(r => r.Anchors.Count))
        {
            var outlines = MRUKPlanExtractor.Extract(new List<MRUKRoom> { room });
            if (outlines.Count > 0 && outlines.Any(o => keptOutlines.Any(k => FloorPlanBuilder.IsSameRoom(k, o)))) continue;
            keptRooms.Add(room);
            keptOutlines.AddRange(outlines);
        }
        return rooms.Where(keptRooms.Contains).ToList();
    }

    private static bool IsRoomAreaValid(MRUKRoom room)
    {
        var floor = room.Anchors.FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR && a.PlaneRect.HasValue);
        if (floor == null) return false;

        float area = floor.PlaneRect.Value.width * floor.PlaneRect.Value.height;
        if (area < 0.8f) return false;

        if (area < 3.5f)
        {
            bool isSignificant = room.Anchors.Any(a => {
                string l = a.Label.ToString().ToUpperInvariant();
                return l.Contains("DOOR") || l.Contains("WINDOW") ||
                       (!l.Contains("WALL") && !l.Contains("FLOOR") && !l.Contains("CEILING") &&
                        !l.Contains("OTHER") && !l.Contains("STORAGE"));
            });
            if (!isSignificant) return false;
        }
        return true;
    }

    /// <summary>
    /// True for anchors that represent an actual physical wall surface that should be built as solid
    /// geometry: WALL_FACE, or INNER_WALL_FACE (e.g. a pillar, represented as several wall-face
    /// segments per MRUK's High Fidelity scene model). INVISIBLE_WALL_FACE anchors mark a conceptual
    /// boundary between open-plan spaces (e.g. living room/kitchen) with no real wall there, and must
    /// be excluded even though the label also contains "WALL".
    /// </summary>
    public static bool IsStructuralWall(MRUKAnchor a)
    {
        if (a == null) return false;
        // Label is a flags value: an "invisible" wall is stored as WALL_FACE | INVISIBLE_WALL_FACE, so compare
        // flags, not the text of the enum.
        var l = a.Label;
        bool wall = (l & (MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INNER_WALL_FACE)) != 0;
        return wall && (l & MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE) == 0;
    }

    /// <summary>
    /// True for anything you can walk through: a DOOR_FRAME, or a WINDOW_FRAME whose bottom edge sits
    /// at floor level (e.g. a balcony/patio door, which MRUK has no dedicated label for and reports
    /// as a window). A window next to it that doesn't reach the floor stays a window.
    /// </summary>
    public static bool IsDoor(MRUKAnchor a)
    {
        if (a == null) return false;
        string l = a.Label.ToString().ToUpperInvariant();
        if (l.Contains("DOOR")) return true;
        if (!l.Contains("WINDOW") || !a.PlaneRect.HasValue || a.Room == null || a.Room.FloorAnchors.Count == 0) return false;

        float floorY = a.Room.FloorAnchors.Min(f => f.transform.position.y);
        float bottomY = a.transform.position.y - a.PlaneRect.Value.height / 2f;
        return bottomY - floorY < 0.15f;
    }

    public static bool IsWindow(MRUKAnchor a)
    {
        return a != null && a.Label.ToString().ToUpperInvariant().Contains("WINDOW") && !IsDoor(a);
    }

    public static string GetSafeName(string name)
    {
        if (string.IsNullOrEmpty(name)) 
            return "Room";

        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid))
                     .Replace(" ", "_")
                     .Trim('_')
                     .Trim();
    }
}
