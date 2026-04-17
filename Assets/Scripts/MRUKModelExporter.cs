using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;
using Unity.Collections;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR;
using Meta.XR.MRUtilityKit;
#endif

public static class MRUKModelExporter
{
    public static async System.Threading.Tasks.Task<string> GenerateRawHighFidelityMesh(float globalRotation = 0f, Vector3 houseCenter = default)
    {
#if META_XR_SDK_INSTALLED
        var sb = new StringBuilder();
        int vertexOffset = 0;
        var globalRot = Quaternion.Euler(0, globalRotation, 0);
        try {
            if (MRUK.Instance == null || MRUK.Instance.Rooms.Count == 0) return "# MRUK not loaded";
            sb.AppendLine("# === RAW HIGH-FIDELITY ROOM SCAN ===\n# Center: " + houseCenter);
            foreach (var room in MRUK.Instance.Rooms) {
                string id = string.IsNullOrEmpty(room.name) ? room.Anchor.Uuid.ToString().Substring(0, 8) : room.name;
                if (room.GlobalMeshAnchor != null && room.GlobalMeshAnchor.Anchor.TryGetComponent<OVRTriangleMesh>(out var mesh)) {
                    AppendTriangleMesh(sb, mesh, room.GlobalMeshAnchor.Anchor, globalRot, houseCenter, ref vertexOffset, "GlobalMesh_" + id);
                } else {
                    foreach (var a in room.Anchors) {
                        if (a != null && a.Anchor.TryGetComponent<OVRTriangleMesh>(out var m))
                            AppendTriangleMesh(sb, m, a.Anchor, globalRot, houseCenter, ref vertexOffset, a.Label + "_" + id);
                    }
                }
            }
            await System.Threading.Tasks.Task.Yield();
            return sb.ToString();
        } catch (Exception ex) { return "# ERROR: " + ex.Message; }
#else
        return "";
#endif
    }

    private static void AppendTriangleMesh(StringBuilder sb, OVRTriangleMesh triangleMesh, OVRAnchor anchor, Quaternion globalRot, Vector3 houseCenter, ref int vertexOffset, string meshName)
    {
        if (!triangleMesh.TryGetCounts(out int vCount, out int tCount) || vCount == 0) return;
        using var vertices = new NativeArray<Vector3>(vCount, Allocator.Temp);
        using var indices = new NativeArray<int>(tCount * 3, Allocator.Temp);
        if (!triangleMesh.TryGetMesh(vertices, indices)) return;
        Matrix4x4 worldMatrix = Matrix4x4.identity;
        if (anchor.TryGetComponent<OVRLocatable>(out var loc) && loc.TryGetSceneAnchorPose(out var pose))
            worldMatrix = Matrix4x4.TRS(pose.Position ?? Vector3.zero, pose.Rotation ?? Quaternion.identity, Vector3.one);
        sb.AppendLine("\ng " + meshName);
        for (int i = 0; i < vCount; i++) {
            Vector3 worldPos = worldMatrix.MultiplyPoint3x4(vertices[i]);
            Vector3 v = globalRot * (worldPos - houseCenter);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F6} {1:F6} {2:F6}", v.x, v.y, -v.z));
        }
        for (int i = 0; i < indices.Length; i += 3)
            sb.AppendLine($"f {indices[i] + 1 + vertexOffset} {indices[i + 2] + 1 + vertexOffset} {indices[i + 1] + 1 + vertexOffset}");
        vertexOffset += vCount;
    }

    public static async System.Threading.Tasks.Task<string> GenerateCleanColoredAnalytical(float globalRotation = 0f, Vector3 houseCenter = default)
    {
    #if META_XR_SDK_INSTALLED
        var sb = new StringBuilder();
        sb.AppendLine("mtllib " + MRUKPathUtility.MODEL_MTL);
        int vertexOffset = 0;
        var globalRot = Quaternion.Euler(0, globalRotation, 0);
        foreach (var room in MRUK.Instance.Rooms) {
            var floorAnchor = room.Anchors.FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR && a.PlaneRect.HasValue);
            if (floorAnchor == null || (floorAnchor.PlaneRect.Value.width * floorAnchor.PlaneRect.Value.height) < 1.0f) continue;
            sb.AppendLine("usemtl Wall");
            if (room.GlobalMeshAnchor != null && room.GlobalMeshAnchor.Anchor.TryGetComponent<OVRTriangleMesh>(out var mesh)) {
                AppendColoredTriangleMesh(sb, mesh, room.GlobalMeshAnchor.Anchor, globalRot, houseCenter, ref vertexOffset, room.name);
            } else {
                foreach (var a in room.Anchors.Where(x => x.Label.ToString().ToUpper().Contains("WALL") || x.Label == MRUKAnchor.SceneLabels.FLOOR)) {
                    if (a.Anchor.TryGetComponent<OVRTriangleMesh>(out var triMesh)) {
                        sb.AppendLine(a.Label == MRUKAnchor.SceneLabels.FLOOR ? "usemtl Floor" : "usemtl Wall");
                        AppendColoredTriangleMesh(sb, triMesh, a.Anchor, globalRot, houseCenter, ref vertexOffset, room.name + "_" + a.Label);
                    }
                }
            }
            foreach (var o in room.Anchors.Where(a => a.PlaneRect.HasValue && (a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW")))) {
                string mat = o.Label.ToString().ToUpper().Contains("DOOR") ? "Door" : "Window";
                MRUKGeometryHelper.AppendBox(sb, ref vertexOffset, o.transform.position, o.transform.rotation, new Vector3(o.PlaneRect.Value.width, o.PlaneRect.Value.height, 0.08f), mat, houseCenter, globalRotation);
            }
        }
        await System.Threading.Tasks.Task.Yield();
        return sb.ToString();
    #else
        return "";
    #endif
    }

    public static string GenerateAnchorAnalytical(List<MRUKRoom> rooms, float globalRotation = 0f, Vector3 houseCenter = default)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("mtllib " + MRUKPathUtility.MODEL_MTL);
        int vOff = 0;
        foreach (var room in rooms) {
            var anchors = room.Anchors.Where(a => a != null && a.PlaneRect.HasValue).ToList();
            var openings = anchors.Where(a => a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW")).ToList();
            foreach (var f in anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.FLOOR))
                MRUKGeometryHelper.AppendBox(sb, ref vOff, f.transform.position, f.transform.rotation, new Vector3(f.PlaneRect.Value.width, f.PlaneRect.Value.height, 0.1f), "Floor", houseCenter, globalRotation, new Vector3(0,0,0.05f));
            foreach (var w in anchors.Where(a => a.Label.ToString().ToUpper().Contains("WALL"))) {
                float wW = w.PlaneRect.Value.width, wH = w.PlaneRect.Value.height;
                var wallHoles = openings.Where(o => {
                    Vector3 lp = w.transform.InverseTransformPoint(o.transform.position);
                    return Mathf.Abs(lp.z) < 0.35f && Mathf.Abs(lp.x) < (wW/2f + 0.15f);
                }).ToList();
                if (wallHoles.Count > 0) {
                    var xC = new List<float> { -wW/2f, wW/2f };
                    foreach(var h in wallHoles) {
                        Vector3 lp = w.transform.InverseTransformPoint(h.transform.position);
                        float hw = h.PlaneRect.Value.width/2f;
                        xC.Add(Mathf.Clamp(lp.x - hw, -wW/2f, wW/2f)); xC.Add(Mathf.Clamp(lp.x + hw, -wW/2f, wW/2f));
                    }
                    var sX = xC.Distinct().OrderBy(x => x).ToList();
                    for (int i=0; i<sX.Count-1; i++) {
                        float segW = sX[i+1] - sX[i]; if (segW < 0.02f) continue;
                        float midX = (sX[i] + sX[i+1]) / 2f;
                        bool isHole = wallHoles.Any(h => {
                            Vector3 lp = w.transform.InverseTransformPoint(h.transform.position);
                            float hw = h.PlaneRect.Value.width/2f;
                            return midX > lp.x - hw + 0.01f && midX < lp.x + hw - 0.01f;
                        });
                        if (!isHole) MRUKGeometryHelper.AppendBox(sb, ref vOff, w.transform.TransformPoint(new Vector3(midX, 0, 0)), w.transform.rotation, new Vector3(segW, wH, 0.25f), "Wall", houseCenter, globalRotation);
                    }
                } else MRUKGeometryHelper.AppendBox(sb, ref vOff, w.transform.position, w.transform.rotation, new Vector3(wW, wH, 0.25f), "Wall", houseCenter, globalRotation);
            }
            foreach (var o in openings) {
                bool isDoor = o.Label.ToString().Contains("DOOR");
                float thick = isDoor ? 0.10f : 0.12f;
                MRUKGeometryHelper.AppendBox(sb, ref vOff, o.transform.position, o.transform.rotation, new Vector3(o.PlaneRect.Value.width, o.PlaneRect.Value.height, thick), isDoor ? "Door" : "Window", houseCenter, globalRotation);
            }
        }
        return sb.ToString();
    }

    private static void AppendColoredTriangleMesh(StringBuilder sb, OVRTriangleMesh triMesh, OVRAnchor anchor, Quaternion globalRot, Vector3 houseCenter, ref int vertexOffset, string roomName)
    {
        if (!triMesh.TryGetCounts(out int vCount, out int tCount) || vCount == 0) return;
        using var verts = new NativeArray<Vector3>(vCount, Allocator.Temp);
        using var tris = new NativeArray<int>(tCount * 3, Allocator.Temp);
        if (!triMesh.TryGetMesh(verts, tris)) return;
        Matrix4x4 m = Matrix4x4.identity;
        if (anchor.TryGetComponent<OVRLocatable>(out var loc) && loc.TryGetSceneAnchorPose(out var pose))
            m = Matrix4x4.TRS(pose.Position ?? Vector3.zero, pose.Rotation ?? Quaternion.identity, Vector3.one);
        for (int i = 0; i < vCount; i++) {
            Vector3 worldPos = m.MultiplyPoint3x4(verts[i]);
            Vector3 v = globalRot * (worldPos - houseCenter);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F6} {1:F6} {2:F6}", v.x, v.y, -v.z));
        }
        for (int i = 0; i < tris.Length; i += 3)
            sb.AppendLine($"f {tris[i] + 1 + vertexOffset} {tris[i + 2] + 1 + vertexOffset} {tris[i + 1] + 1 + vertexOffset}");
        vertexOffset += vCount;
    }

    public static string GenerateOBJ(List<MRUKRoom> rooms, bool rawMesh, float globalRotation = 0, Vector3 houseCenter = default)
    {
        return rawMesh ? GeneratePolygonalReconstruction(rooms, globalRotation, houseCenter) : GenerateBoxModel(rooms, globalRotation, houseCenter);
    }

    private static string GenerateBoxModel(List<MRUKRoom> rooms, float globalRotation, Vector3 houseCenter) {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("mtllib " + MRUKPathUtility.MODEL_MTL);
        int vOff = 0;
        var anchors = rooms.SelectMany(r => r.Anchors).Where(a => a != null && a.PlaneRect.HasValue).ToList();
        foreach (var f in anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.FLOOR))
            MRUKGeometryHelper.AppendBox(sb, ref vOff, f.transform.position, f.transform.rotation, new Vector3(f.PlaneRect.Value.width, f.PlaneRect.Value.height, 0.1f), "Floor", houseCenter, globalRotation, new Vector3(0,0,0.05f));
        foreach (var w in anchors.Where(a => a.Label.ToString().ToUpper().Contains("WALL")))
            MRUKGeometryHelper.AppendBox(sb, ref vOff, w.transform.position, w.transform.rotation, new Vector3(w.PlaneRect.Value.width, w.PlaneRect.Value.height, 0.25f), "Wall", houseCenter, globalRotation);
        foreach (var o in anchors.Where(a => a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW")))
            MRUKGeometryHelper.AppendBox(sb, ref vOff, o.transform.position, o.transform.rotation, new Vector3(o.PlaneRect.Value.width, o.PlaneRect.Value.height, 0.08f), o.Label.ToString().Contains("DOOR") ? "Door" : "Window", houseCenter, globalRotation);
        return sb.ToString();
    }

    private static string GeneratePolygonalReconstruction(List<MRUKRoom> rooms, float globalRotation, Vector3 houseCenter) {
        StringBuilder sb = new StringBuilder();
        int vOff = 0;
        Quaternion gRot = Quaternion.Euler(0, globalRotation, 0);
        float baseY = rooms.Count > 0 && rooms[0].FloorAnchors.Count > 0 ? rooms[0].FloorAnchors[0].transform.position.y : 0f;
        foreach (var room in rooms) {
            MRUKAnchor floor = room.FloorAnchors.FirstOrDefault();
            if (floor == null || floor.PlaneBoundary2D == null) continue;
            Vector3 pos = floor.transform.position;
            if (Mathf.Abs(pos.y - baseY) < 0.3f) pos.y = baseY;
            Quaternion rot = floor.transform.rotation;
            float h = room.CeilingAnchors.Count > 0 ? Mathf.Abs(room.CeilingAnchors[0].transform.position.y - pos.y) : 2.5f;
            sb.AppendLine("usemtl Floor");
            foreach (var p in floor.PlaneBoundary2D) {
                Vector3 v = gRot * (pos + rot * new Vector3(p.x, 0, p.y) - houseCenter);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
            }
            int c = floor.PlaneBoundary2D.Count;
            for (int i = 0; i < c - 2; i++) sb.AppendLine($"f {vOff + 1} {vOff + i + 2} {vOff + i + 3}");
            sb.AppendLine("usemtl Wall");
            foreach (var p in floor.PlaneBoundary2D) {
                Vector3 v = gRot * (pos + rot * new Vector3(p.x, h, p.y) - houseCenter);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
            }
            for (int i = 0; i < c - 2; i++) sb.AppendLine($"f {vOff + c + 1} {vOff + c + i + 3} {vOff + c + i + 2}");
            int sStart = vOff + c * 2;
            for (int i = 0; i < c; i++) {
                Vector2 p1 = floor.PlaneBoundary2D[i], p2 = floor.PlaneBoundary2D[(i + 1) % c];
                Vector3 b1 = gRot * (pos + rot * new Vector3(p1.x, 0, p1.y) - houseCenter), b2 = gRot * (pos + rot * new Vector3(p2.x, 0, p2.y) - houseCenter);
                Vector3 t1 = gRot * (pos + rot * new Vector3(p1.x, h, p1.y) - houseCenter), t2 = gRot * (pos + rot * new Vector3(p2.x, h, p2.y) - houseCenter);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", b1.x, b1.y, -b1.z));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", b2.x, b2.y, -b2.z));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", t2.x, t2.y, -t2.z));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", t1.x, t1.y, -t1.z));
                int idx = sStart + i * 4 + 1;
                sb.AppendLine($"f {idx} {idx + 1} {idx + 2}"); sb.AppendLine($"f {idx} {idx + 2} {idx + 3}");
            }
            vOff += c * 2 + c * 4;
        }
        return sb.ToString();
    }

    public static GLBScene GenerateGLBScene(List<MRUKRoom> rooms, bool rawMesh, float globalRotation = 0, Vector3 houseCenter = default)
    {
        GLBScene scene = new GLBScene();
        foreach (var room in rooms) {
            string obj = GenerateOBJ(new List<MRUKRoom> { room }, rawMesh, globalRotation, houseCenter);
            if (!string.IsNullOrEmpty(obj)) {
                var node = ParseOBJToNode(obj, MRUKDataProcessor.GetRoomLabel(room));
                if (node != null && node.subMeshes.Count > 0) scene.nodes.Add(node);
            }
        }
        return scene;
    }

    private static GLBScene.MeshNode ParseOBJToNode(string objData, string nodeName) {
        var node = new GLBScene.MeshNode { name = nodeName };
        var verts = new List<Vector3>();
        var sub = new GLBScene.SubMesh { materialName = "Wall", color = new Color(0.42f, 0.42f, 0.45f) };
        var tris = new List<int>();
        foreach (var l in objData.Split('\n')) {
            if (l.StartsWith("v ")) {
                var p = l.Split(' ');
                if (p.Length >= 4 && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    verts.Add(new Vector3(x, y, z));
            } else if (l.StartsWith("usemtl ")) {
                if (tris.Count > 0) { sub.vertices = verts.ToArray(); sub.triangles = tris.ToArray(); node.subMeshes.Add(sub); tris = new List<int>(); }
                string m = l.Substring(7).Trim();
                sub = new GLBScene.SubMesh { materialName = m, color = GetColorForMaterial(m) };
            } else if (l.StartsWith("f ")) {
                var p = l.Trim().Split(' ');
                if (p.Length >= 4) {
                    try {
                        tris.Add(int.Parse(p[1].Split('/')[0]) - 1);
                        tris.Add(int.Parse(p[3].Split('/')[0]) - 1);
                        tris.Add(int.Parse(p[2].Split('/')[0]) - 1);
                    } catch {}
                }
            }
        }
        if (tris.Count > 0) { sub.vertices = verts.ToArray(); sub.triangles = tris.ToArray(); node.subMeshes.Add(sub); }
        return node;
    }

    private static Color GetColorForMaterial(string n) {
        switch (n.ToLower()) {
            case "wall": return new Color(0.42f, 0.42f, 0.45f);
            case "floor": return new Color(0.75f, 0.75f, 0.78f);
            case "door": return new Color(0.55f, 0.35f, 0.18f);
            case "window": return new Color(0.25f, 0.60f, 0.92f);
            default: return Color.white;
        }
    }

    public static string GenerateMTL() => "newmtl Wall\nKd 0.42 0.42 0.45\nnewmtl Floor\nKd 0.75 0.75 0.78\nnewmtl Door\nKd 0.55 0.35 0.18\nnewmtl Window\nKd 0.25 0.60 0.92";
}