using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
using Meta.XR;
#endif

public static class MRUKDataProcessor {
    public static string GetRoomLabel(MRUKRoom r) {
        #if META_XR_SDK_INSTALLED
        if (r.Anchor != null) {
            // 1. Try to get Semantic Labels via OVRAnchor.TryGetComponent (Modern 2026 way)
            if (r.Anchor.TryGetComponent<OVRSemanticLabels>(out var semantic)) {
                try {
                    // Use reflection to access Labels to handle both string and Enum List versions across SDKs
                    var prop = semantic.GetType().GetProperty("Labels");
                    var val = prop?.GetValue(semantic);
                    
                    if (val is string s && !string.IsNullOrEmpty(s)) {
                        var tags = s.Split(',').Select(t => t.Trim().ToUpper()).ToList();
                        string custom = tags.FirstOrDefault(t => t != "ROOM" && t != "OTHER" && t != "SPACE" && t != "STORAGE");
                        if (!string.IsNullOrEmpty(custom)) return custom.Replace(" ", "_");
                    }
                    else if (val is System.Collections.IEnumerable list) {
                        foreach (var item in list) {
                            string l = item.ToString().ToUpper();
                            if (l != "ROOM" && l != "OTHER" && l != "SPACE" && l != "STORAGE") return l;
                        }
                    }
                } catch {}
            }
        }
        #endif

        // 2. Fallback to MRUK's room classification
        try {
            var prop = r.GetType().GetProperty("Label") ?? r.GetType().GetProperty("RoomLabel");
            if (prop != null) {
                string l = prop.GetValue(r).ToString().ToUpper();
                if (l != "OTHER" && l != "ROOM") return l;
            }
        } catch {}

        return "Pokoj_" + r.name.Substring(Math.Max(0, r.name.Length - 4));
    }

    public static string GenerateSceneDump(List<MRUKRoom> rooms) {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("=== SUPER DUMP V23 - CLEAN SEMANTIC SCAN ===");
        try {
            foreach (var r in rooms) {
                string detected = GetRoomLabel(r);
                sb.AppendLine($"\n>>> [ROOM] {r.name} | Semantic: {detected}");
                
                #if META_XR_SDK_INSTALLED
                if (r.Anchor.TryGetComponent<OVRSemanticLabels>(out var sl)) {
                    try {
                        var prop = sl.GetType().GetProperty("Labels");
                        var val = prop?.GetValue(sl);
                        sb.AppendLine($"  - Raw Labels: {val ?? "null"}");
                    } catch {}
                }
                #endif

                foreach (var a in r.Anchors) {
                    if (a == null) continue;
                    sb.AppendLine($"    --- [ANCHOR] {a.name} | Label: {a.Label}");
                    #if META_XR_SDK_INSTALLED
                    if (a.Anchor.TryGetComponent<OVRTriangleMesh>(out var tm)) {
                        if (tm.TryGetCounts(out var v, out var t))
                            sb.AppendLine($"      * Raw Mesh Found: {v} verts, {t} tris");
                    }
                    #endif
                }
            }
        } catch (Exception ex) { sb.AppendLine("DUMP ERROR: " + ex.Message); }
        return sb.ToString();
    }

    public static string GenerateJson(List<MRUKRoom> rooms) {
        var d = new UltraHouseData { exportDate = DateTime.Now.ToString("O"), rooms = rooms.Select(r => new UltraRoom {
            name = GetRoomLabel(r), guid = r.name,
            pos = new Vector3Data(r.transform.position), rot = new Vector4Data(r.transform.rotation),
            anchors = r.Anchors.Select(a => new UltraAnchor {
                label = a.Label.ToString(),
                pos = new Vector3Data(a.transform.position),
                rot = new Vector4Data(a.transform.rotation),
                rect = a.PlaneRect.HasValue ? new OfflineRect { w = a.PlaneRect.Value.width, h = a.PlaneRect.Value.height } : null,
                volume = a.VolumeBounds.HasValue ? new Vector3Data(a.VolumeBounds.Value.size) : null
            }).ToList()
        }).ToList() };
        return JsonUtility.ToJson(d, true);
    }

    public static string GetSafeName(string n) { return string.Join("_", n.Split(System.IO.Path.GetInvalidFileNameChars())).Replace(" ", "_"); }
    public static string GetGameObjectPath(GameObject obj) {
        string path = "/" + obj.name; Transform t = obj.transform;
        while (t.parent != null) { t = t.parent; path = "/" + t.name + path; }
        return path;
    }
}
