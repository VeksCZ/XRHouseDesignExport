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
        // 1. Try to find an anchor that represents the room itself
        var roomAnchor = room.Anchors.FirstOrDefault(a => a.Label.ToString().ToUpper() == "ROOM");
        if (roomAnchor != null && roomAnchor.Anchor.TryGetComponent<OVRSemanticLabels>(out var roomSemantic))
        {
        #pragma warning disable 0618
            if (!string.IsNullOrEmpty(roomSemantic.Labels))
            {
                foreach (var label in roomSemantic.Labels.Split(','))
                {
                    string l = label.Trim().ToUpperInvariant();
                    if (l != "ROOM" && l != "OTHER") return l.Replace(" ", "_");
                }
            }
        #pragma warning restore 0618
        }

        // 2. Fallback to existing semantic labels on any anchor
        if (room.Anchor.TryGetComponent<OVRSemanticLabels>(out var semantic))
        {
    #pragma warning disable 0618
            if (!string.IsNullOrEmpty(semantic.Labels))
            {
                foreach (var label in semantic.Labels.Split(','))
                {
                    string l = label.Trim().ToUpperInvariant();
                    if (l != "ROOM" && l != "OTHER" && l != "SPACE" && l != "STORAGE" && l != "INNER_WALL_FACE" && l != "CEILING" && l != "FLOOR")
                    {
                        return l.Replace(" ", "_");
                    }
                }
            }
    #pragma warning restore 0618
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

        // 3. Furniture-based heuristics
        var anchors = room.Anchors.ToList();
        foreach (var a in anchors)
        {
            string l = a.Label.ToString().ToUpperInvariant();
            if (l.Contains("BED")) return "BEDROOM";
            if (l.Contains("KITCHEN") || l.Contains("OVEN") || l.Contains("STOVE")) return "KITCHEN";
            if (l.Contains("SINK") || l.Contains("TOILET") || l.Contains("SHOWER")) return "BATHROOM";
            if (l.Contains("COUCH") || l.Contains("TELEVISION")) return "LIVING_ROOM";
        }
    #endif

        string cleanName = room.name;
        if (cleanName.StartsWith("Room - ")) cleanName = cleanName.Replace("Room - ", "");
        else if (cleanName.StartsWith("Room_")) cleanName = cleanName.Replace("Room_", "");
        
        return "Room_" + (string.IsNullOrEmpty(cleanName) ? "Unknown" : cleanName);
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
    #pragma warning disable 0618
                    if (!string.IsNullOrEmpty(s.Labels))
                    {
                        foreach (var l in s.Labels.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            labelList.Add(l.Trim());
                        }
                    }
    #pragma warning restore 0618
                }
    #endif
                ua.allLabels = labelList;
                ur.anchors.Add(ua);
            }
            data.rooms.Add(ur);
        }

        return JsonUtility.ToJson(data, true);
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
