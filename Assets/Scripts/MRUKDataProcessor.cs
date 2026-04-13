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
    /// <summary>
    /// Vrátí nejlepší dostupný label místnosti (bez zbytečné reflexe)
    /// </summary>
    public static string GetRoomLabel(MRUKRoom room)
    {
        if (room == null) return "Unknown";

    #if META_XR_SDK_INSTALLED
        // 1. Nejlepší cesta: OVRSemanticLabels na Anchoru
    #pragma warning disable 0618
        if (room.Anchor.TryGetComponent<OVRSemanticLabels>(out var semantic) && semantic.Labels != null)
        {
            foreach (var label in semantic.Labels)
            {
                string l = label.ToString().ToUpperInvariant();
                if (l != "ROOM" && l != "OTHER" && l != "SPACE" && l != "STORAGE")
                {
                    return l.Replace(" ", "_");
                }
            }
        }
    #pragma warning restore 0618

        // 2. Fallback přes MRUKAnchor.Label
        var mrukAnchor = room.GetComponent<MRUKAnchor>();
        if (mrukAnchor != null)
        {
            string label = mrukAnchor.Label.ToString().ToUpperInvariant();
            if (label != "NONE" && label != "OTHER" && label != "ROOM" && label != "STORAGE")
            {
                return label;
            }
        }

        // 3. Fallback: Prohledat nábytek uvnitř místnosti pro lepší název
        var furnitureLabels = new[] { "KITCHEN", "BEDROOM", "BATHROOM", "LIVING_ROOM", "OFFICE", "DINING", "GARAGE" };
        var anchors = room.Anchors.ToList(); // Safely iterate
        foreach (var a in anchors)
        {
            string l = a.Label.ToString().ToUpperInvariant();
            // Pokud najdeme postel, je to bedroom, atd.
            if (l.Contains("BED")) return "BEDROOM";
            if (l.Contains("KITCHEN") || l.Contains("OVEN") || l.Contains("STOVE")) return "KITCHEN";
            if (l.Contains("SINK") || l.Contains("TOILET") || l.Contains("SHOWER")) return "BATHROOM";
            if (l.Contains("COUCH") || l.Contains("TELEVISION")) return "LIVING_ROOM";
        }
    #endif

        // Absolutní fallback
        return "Pokoj_" + (string.IsNullOrEmpty(room.name) ? "Unknown" : room.name);
    }

    public static string GenerateSceneDump(List<MRUKRoom> rooms)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SUPER DUMP V28 - CLEAN SEMANTIC SCAN ===");
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
            exportDate = DateTime.Now.ToString("O"),
            rooms = rooms.Select(r => new UltraRoom
            {
                name = GetRoomLabel(r),
                guid = r.name,
                pos = new Vector3Data(r.transform.position),
                rot = new Vector4Data(r.transform.rotation),
                anchors = r.Anchors.Where(a => a != null).Select(a => new UltraAnchor
                {
                    label = a.Label.ToString(),
                    pos = new Vector3Data(a.transform.position),
                    rot = new Vector4Data(a.transform.rotation),
                    rect = a.PlaneRect.HasValue 
                        ? new OfflineRect { w = a.PlaneRect.Value.width, h = a.PlaneRect.Value.height } 
                        : null,
                    volume = a.VolumeBounds.HasValue 
                        ? new Vector3Data(a.VolumeBounds.Value.size) 
                        : null
                }).ToList()
            }).ToList()
        };

        return JsonUtility.ToJson(data, true);
    }

    public static string GetSafeName(string name)
    {
        if (string.IsNullOrEmpty(name)) 
            return "Room";

        // Odstranění neplatných znaků pro souborový systém
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid))
                     .Replace(" ", "_")
                     .Trim('_')
                     .Trim();
    }
}
