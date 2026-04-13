using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

public static class MRUKDataProcessor {
    public static string GenerateSceneDump(List<MRUKRoom> rooms) {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("=== SUPER DUMP V19 - HARDENED SCAN ===");
        try {
            foreach (var r in rooms) {
                if (r == null) continue;
                sb.AppendLine($"\n>>> [ROOM] {r.name}");
                DumpRecursive(r.gameObject, sb, "  ", 0);
            }

            sb.AppendLine("\n\n=== GLOBAL SEARCH (TOP-LEVEL OBJECTS) ===");
            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            foreach(var rootGo in roots) {
                if (rootGo.name.Contains("OVR") || rootGo.name.Contains("MRUK") || rootGo.name.Contains("Room"))
                    DumpRecursive(rootGo, sb, "  ", 0);
            }
        } catch (Exception ex) { sb.AppendLine("FATAL DUMP ERROR: " + ex.Message); }
        return sb.ToString();
    }

    private static void DumpRecursive(GameObject obj, StringBuilder sb, string indent, int depth) {
        if (obj == null || depth > 10) return;
        try {
            sb.AppendLine($"{indent}[OBJ] {obj.name}");
            var comps = obj.GetComponents<Component>();
            foreach (var c in comps) {
                if (c == null) continue;
                string tName = c.GetType().Name;
                sb.AppendLine($"{indent}  - Comp: {tName}");
                if (tName.Contains("Mesh") || tName.Contains("Label") || tName.Contains("Anchor")) {
                    DumpMeshInsight(c, sb, indent + "    ");
                }
            }
            foreach (Transform child in obj.transform) {
                DumpRecursive(child.gameObject, sb, indent + "  ", depth + 1);
            }
        } catch {}
    }

    private static void DumpMeshInsight(Component c, StringBuilder sb, string indent) {
        try {
            var type = c.GetType();
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                try {
                    var val = p.GetValue(c);
                    if (val is Mesh m) sb.AppendLine($"{indent}Property '{p.Name}': Mesh found! Verts: {m.vertexCount}");
                    else if (p.Name.Contains("Vertex") || p.Name.Contains("Count") || p.Name.Contains("Labels") || p.Name.Contains("Mesh")) {
                        sb.AppendLine($"{indent}Property '{p.Name}': {val ?? "null"}");
                    }
                } catch {}
            }
        } catch {}
    }

    public static string GetRoomLabel(MRUKRoom r) {
        try {
            var allSemanticLabels = r.GetComponentsInChildren<Component>(true).Where(c => c.GetType().Name == "OVRSemanticLabels");
            foreach (var sl in allSemanticLabels) {
                var prop = sl.GetType().GetProperty("Labels");
                string text = prop?.GetValue(sl) as string;
                if (!string.IsNullOrEmpty(text)) {
                    var parts = text.Split(',').Select(p => p.Trim().ToUpper()).ToList();
                    string custom = parts.FirstOrDefault(p => p != "ROOM" && p != "OTHER" && p != "STORAGE" && p != "SPACE");
                    if (!string.IsNullOrEmpty(custom)) return custom;
                }
            }
        } catch {}
        return "Pokoj_" + r.name.Substring(Math.Max(0, r.name.Length - 4));
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
        string path = "/" + obj.name;
        Transform t = obj.transform;
        while (t.parent != null) { t = t.parent; path = "/" + t.name + path; }
        return path;
    }
}
