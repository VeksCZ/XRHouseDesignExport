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

public static class MRUKModelExporterV2
{
    /// <summary>
    /// Exportuje SUROVÝ High-Fidelity mesh (nejlepší kvalita z Questu v83+)
    /// Preferuje GlobalMeshAnchor → fallback na jednotlivé OVRTriangleMesh
    /// </summary>
    public static async System.Threading.Tasks.Task<string> GenerateRawHighFidelityMesh(float globalRotation = 0f)
    {
#if META_XR_SDK_INSTALLED
        var sb = new StringBuilder();
        int vertexOffset = 0;
        var globalRot = Quaternion.Euler(0, globalRotation, 0);

        try
        {
            if (MRUK.Instance == null || MRUK.Instance.Rooms.Count == 0)
                return "# MRUK není načtený nebo nejsou žádné místnosti.";

            sb.AppendLine("# === RAW HIGH-FIDELITY ROOM SCAN ===");
            sb.AppendLine($"# Export time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("# Format: OBJ | Y-up | Z flipped for OBJ");

            foreach (var room in MRUK.Instance.Rooms)
            {
                string roomId = string.IsNullOrEmpty(room.name) 
                    ? room.Anchor.Uuid.ToString().Substring(0, 8) 
                    : room.name;

                // 1. Nejlepší varianta – Global Mesh Anchor
                if (room.GlobalMeshAnchor != null)
                {
                    var anchor = room.GlobalMeshAnchor.Anchor;
                    if (anchor.TryGetComponent<OVRTriangleMesh>(out var mesh))
                    {
                        AppendTriangleMesh(sb, mesh, anchor, globalRot, ref vertexOffset, $"GlobalMesh_{roomId}");
                        Debug.Log($"[RawExport] GlobalMesh exportován pro {roomId}");
                        continue; // globální mesh obvykle stačí pro celou místnost
                    }
                }

                // 2. Fallback – jednotlivé anchory
                foreach (var anchorComp in room.Anchors)
                {
                    if (anchorComp == null) continue;
                    var anchor = anchorComp.Anchor;
                    string label = anchorComp.Label.ToString();

                    if (anchor.TryGetComponent<OVRTriangleMesh>(out var mesh))
                    {
                        AppendTriangleMesh(sb, mesh, anchor, globalRot, ref vertexOffset, $"{label}_{roomId}");
                    }
                }
            }

            if (vertexOffset == 0)
                sb.AppendLine("# WARNING: No triangle mesh data found. Run Space Setup in Quest.");

            await System.Threading.Tasks.Task.Yield();
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Debug.LogError($"GenerateRawHighFidelityMesh error: {ex.Message}");
            return $"# ERROR: {ex.Message}";
        }
#else
        await System.Threading.Tasks.Task.Yield();
        return "";
#endif
    }

    private static void AppendTriangleMesh(
        StringBuilder sb,
        OVRTriangleMesh triangleMesh,
        OVRAnchor anchor,
        Quaternion globalRot,
        ref int vertexOffset,
        string meshName)
    {
        if (!triangleMesh.TryGetCounts(out int vertCount, out int triCount) || vertCount == 0)
            return;

        using var vertices = new NativeArray<Vector3>(vertCount, Allocator.Temp);
        using var indices = new NativeArray<int>(triCount * 3, Allocator.Temp);

        if (!triangleMesh.TryGetMesh(vertices, indices))
            return;

        // Získat světovou transformaci anchoru
        Matrix4x4 worldMatrix = Matrix4x4.identity;
        if (anchor.TryGetComponent<OVRLocatable>(out var loc) && loc.TryGetSceneAnchorPose(out var pose))
        {
            worldMatrix = Matrix4x4.TRS(pose.Position ?? Vector3.zero, pose.Rotation ?? Quaternion.identity, Vector3.one);
        }

        sb.AppendLine($"\n# Mesh: {meshName} | {vertCount} verts | {triCount} tris");

        // Vertices
        for (int i = 0; i < vertCount; i++)
        {
            Vector3 v = globalRot * worldMatrix.MultiplyPoint3x4(vertices[i]);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F6} {1:F6} {2:F6}", v.x, v.y, -v.z));
        }

        // Faces
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = indices[i]     + 1 + vertexOffset;
            int b = indices[i + 1] + 1 + vertexOffset;
            int c = indices[i + 2] + 1 + vertexOffset;
            // Order: a, c, b for correct winding after flipping Z
            sb.AppendLine($"f {a} {c} {b}");
        }

        vertexOffset += vertCount;
    }

    /// <summary>
    /// BAREVNÝ analytický model založený na raw High-Fidelity mesh (doporučeno)
    /// - Stěny = šedé
    /// - Podlaha = světle šedá
    /// - Dveře = hnědé
    /// - Okna = modré
    /// - Vyrovnává podle podlahy
    /// </summary>
    public static async System.Threading.Tasks.Task<string> GenerateCleanColoredAnalytical(float globalRotation = 0f)
    {
    #if META_XR_SDK_INSTALLED
        var sb = new StringBuilder();
        sb.AppendLine("mtllib house_model.mtl");
        int vertexOffset = 0;
        var globalRot = Quaternion.Euler(0, globalRotation, 0);

        try
        {
            if (MRUK.Instance == null || MRUK.Instance.Rooms.Count == 0)
                return "# No rooms loaded";

            sb.AppendLine("# === BAREVNÝ CLEAN ANALYTICAL MODEL FROM RAW MESH ===");
            sb.AppendLine($"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            foreach (var room in MRUK.Instance.Rooms)
            {
                // === Filtr malých "místností" (skříně, šachty atd.) ===
                var floorAnchor = room.Anchors.FirstOrDefault(a => 
                    a.Label == MRUKAnchor.SceneLabels.FLOOR && a.PlaneRect.HasValue);

                if (floorAnchor == null) continue;

                float floorArea = floorAnchor.PlaneRect.Value.width * floorAnchor.PlaneRect.Value.height;
                if (floorArea < 2.0f) continue;   // ignorujeme malé prostory

                string roomName = MRUKDataProcessor.GetRoomLabel(room);

                // === 1. Raw mesh stěn a podlahy (Global Mesh) ===
                if (room.GlobalMeshAnchor != null)
                {
                    var anchor = room.GlobalMeshAnchor.Anchor;
                    if (anchor.TryGetComponent<OVRTriangleMesh>(out var triMesh))
                    {
                        AppendColoredTriangleMesh(sb, triMesh, anchor, globalRot, ref vertexOffset, roomName);
                    }
}
                else
                {
                    // Fallback na jednotlivé anchory (jen stěny, podlahy, stropy)
                    foreach (var anchorComp in room.Anchors)
                    {
                        if (anchorComp == null) continue;
                        var label = anchorComp.Label.ToString().ToUpperInvariant();
                        if (!label.Contains("WALL") && !label.Contains("FLOOR") && !label.Contains("CEILING")) continue;

                        if (anchorComp.Anchor.TryGetComponent<OVRTriangleMesh>(out var triMesh))
                        {
                            AppendColoredTriangleMesh(sb, triMesh, anchorComp.Anchor, globalRot, ref vertexOffset, $"{roomName}_{label}");
                        }
}
                }

                // === 2. Samostatně přidáme dveře a okna jako barevné plochy ===
                var openings = room.Anchors.Where(a =>
                {
                    if (a == null || !a.PlaneRect.HasValue) return false;
                    string l = a.Label.ToString().ToUpperInvariant();
                    return l.Contains("DOOR") || l.Contains("WINDOW") || l.Contains("OPENING");
                });

                foreach (var o in openings)
                {
                    string label = o.Label.ToString().ToUpperInvariant();
                    string material = label.Contains("DOOR") ? "Door" : "Window";

                    MRUKGeometryHelper.AppendBox(sb, ref vertexOffset,
                        o.transform.position,
                        o.transform.rotation,
                        new Vector3(o.PlaneRect.Value.width, o.PlaneRect.Value.height, 0.08f),
                        material,
                        Vector3.zero,
                        globalRotation);
                }
            }

            await System.Threading.Tasks.Task.Yield();
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Debug.LogError($"GenerateCleanColoredAnalytical error: {ex.Message}");
            return $"# ERROR: {ex.Message}";
        }
    #else
        await System.Threading.Tasks.Task.Yield();
        return "";
    #endif
    }

    private static void AppendColoredTriangleMesh(StringBuilder sb, OVRTriangleMesh triMesh, OVRAnchor anchor,
        Quaternion globalRot, ref int vertexOffset, string roomName)
{
        if (!triMesh.TryGetCounts(out int vertCount, out int triCount) || vertCount == 0)
            return;

        using var vertices = new NativeArray<Vector3>(vertCount, Allocator.Temp);
        using var indices = new NativeArray<int>(triCount * 3, Allocator.Temp);

        if (!triMesh.TryGetMesh(vertices, indices))
            return;

        Matrix4x4 worldMatrix = Matrix4x4.identity;
        if (anchor.TryGetComponent<OVRLocatable>(out var loc) && loc.TryGetSceneAnchorPose(out var pose))
        {
            worldMatrix = Matrix4x4.TRS(pose.Position ?? Vector3.zero, pose.Rotation ?? Quaternion.identity, Vector3.one);
        }

        sb.AppendLine($"\n# Colored Room: {roomName} | {vertCount} verts");

        for (int i = 0; i < vertCount; i++)
        {
            Vector3 v = globalRot * worldMatrix.MultiplyPoint3x4(vertices[i]);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F6} {1:F6} {2:F6}", v.x, v.y, -v.z));
        }

        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = indices[i]     + 1 + vertexOffset;
            int b = indices[i + 1] + 1 + vertexOffset;
            int c = indices[i + 2] + 1 + vertexOffset;
            // Face winding for correct normals
            sb.AppendLine($"f {a} {c} {b}");
        }

        vertexOffset += vertCount;
    }

    public static string GenerateOBJ(List<MRUKRoom> rooms, bool rawMesh, float globalRotation = 0)
{
        return rawMesh 
            ? GeneratePolygonalReconstruction(rooms, globalRotation) 
            : GenerateBoxModel(rooms, globalRotation);
    }

    public static byte[] GenerateGLB(List<MRUKRoom> rooms, bool polygonal, float globalRotation) {
        try {
            string obj = polygonal ? GeneratePolygonalReconstruction(rooms, globalRotation) : GenerateBoxModel(rooms, globalRotation);
            if (string.IsNullOrEmpty(obj)) return null;
            
            List<Vector3> verts = new List<Vector3>(); List<int> tris = new List<int>();
            string[] lines = obj.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach(var l in lines) {
                if (l.StartsWith("v ")) {
                    var p = l.Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 4) {
                        if (float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                            float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                            float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                        {
                            verts.Add(new Vector3(x, y, z));
                        }
                    }
                } else if (l.StartsWith("f ")) {
                    var p = l.Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 4) {
                        try {
                            tris.Add(int.Parse(p[1].Split('/')[0]) - 1); 
                            tris.Add(int.Parse(p[3].Split('/')[0]) - 1); 
                            tris.Add(int.Parse(p[2].Split('/')[0]) - 1);
                        } catch {}
                    }
                }
            }
            if (verts.Count == 0 || tris.Count == 0) return null;
            return ExportRawToGLB(verts.ToArray(), tris.ToArray());
        } catch (Exception ex) {
            Debug.LogError($"GenerateGLB crash: {ex.Message}");
            return null;
        }
    }

    private static byte[] ExportRawToGLB(Vector3[] vertices, int[] triangles) {
        int vCount = vertices.Length; int tCount = triangles.Length;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms)) {
            var posBytes = new byte[vCount * 12]; Buffer.BlockCopy(vertices, 0, posBytes, 0, posBytes.Length);
            var idxBytes = new byte[tCount * 4]; Buffer.BlockCopy(triangles, 0, idxBytes, 0, idxBytes.Length);
            string json = "{\"asset\":{\"version\":\"2.0\"},\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}],\"nodes\":[{\"mesh\":0}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0,\"bufferViews\":[{\"buffer\":0,\"byteLength\":" + posBytes.Length + "},{\"buffer\":0,\"byteOffset\":" + posBytes.Length + ",\"byteLength\":" + idxBytes.Length + "}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":" + vCount + ",\"type\":\"VEC3\"},{\"bufferView\":1,\"componentType\":5125,\"count\":" + tCount + ",\"type\":\"SCALAR\"}],\"buffers\":[{\"byteLength\":" + (posBytes.Length + idxBytes.Length) + "}]}";
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            int jsonPad = (4 - (jsonBytes.Length % 4)) % 4; int binPad = (4 - ((posBytes.Length + idxBytes.Length) % 4)) % 4;
            bw.Write(0x46546C67); bw.Write(2); bw.Write(12 + 8 + jsonBytes.Length + jsonPad + 8 + posBytes.Length + idxBytes.Length + binPad);
            bw.Write(jsonBytes.Length + jsonPad); bw.Write(0x4E4F534A); bw.Write(jsonBytes);
            for (int i=0; i<jsonPad; i++) bw.Write((byte)0x20);
            bw.Write(posBytes.Length + idxBytes.Length + binPad); bw.Write(0x004E4942);
            bw.Write(posBytes); bw.Write(idxBytes);
            for (int i=0; i<binPad; i++) bw.Write((byte)0);
            return ms.ToArray();
        }
    }

    public static string GenerateMTL() => 
        "newmtl Wall\nKd 0.4 0.4 0.4\nnewmtl Floor\nKd 0.7 0.7 0.7\nnewmtl Door\nKd 0.5 0.3 0.1\nnewmtl Window\nKd 0.3 0.6 0.9";

    private static string GenerateBoxModel(List<MRUKRoom> rooms, float globalRotation) {
        try {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("mtllib house_model.mtl");
            int vOff = 0;
            var allAnchors = rooms.SelectMany(r => r.Anchors).Where(a => a != null && a.PlaneRect.HasValue).ToList();
            
            var walls = allAnchors.Where(a => a.Label.ToString().ToUpperInvariant().Contains("WALL")).ToList();
            var floors = allAnchors.Where(a => a.Label.ToString().ToUpperInvariant().Contains("FLOOR")).ToList();
            
            // Správné sbírání otvorů (dveře + okna)
            var openings = allAnchors.Where(a => {
                string l = a.Label.ToString().ToUpperInvariant();
                return l.Contains("DOOR") || l.Contains("WINDOW") || l.Contains("OPENING");
            }).ToList();

            // Unique openings for hole cutting to avoid double processing
            var uniqueOpenings = openings.GroupBy(a => a.transform.position.ToString("F3")).Select(g => g.First()).ToList();

            // === FLOORS ===
            foreach (var f in floors) {
                try {
                    MRUKGeometryHelper.AppendBox(sb, ref vOff, f.transform.position, f.transform.rotation, new Vector3(f.PlaneRect.Value.width, f.PlaneRect.Value.height, 0.1f), "Floor", new Vector3(0, 0, 0.05f), globalRotation);
                } catch (Exception ex) { Debug.LogWarning($"Floor box append failed: {ex.Message}"); }
            }

            // === WALLS with holes ===
            foreach (var w in walls) {
                try {
                    float wW = w.PlaneRect.Value.width; float wH = w.PlaneRect.Value.height;
                    var wallHoles = uniqueOpenings.Where(o => {
                        Vector3 lp = w.transform.InverseTransformPoint(o.transform.position);
                        return Mathf.Abs(lp.z) < 0.35f && Mathf.Abs(lp.x) < (wW/2f + 0.2f) && Mathf.Abs(lp.y) < (wH/2f + 0.2f);
                    }).ToList();

                    if (wallHoles.Count > 0) {
                        var xC = new List<float> { -wW/2f, wW/2f }; var yC = new List<float> { -wH/2f, wH/2f };
                        foreach(var h in wallHoles) {
                            Vector3 lp = w.transform.InverseTransformPoint(h.transform.position);
                            float hw = h.PlaneRect.Value.width/2f; float hh = h.PlaneRect.Value.height/2f;
                            xC.Add(Mathf.Clamp(lp.x - hw, -wW/2f, wW/2f)); xC.Add(Mathf.Clamp(lp.x + hw, -wW/2f, wW/2f));
                            yC.Add(Mathf.Clamp(lp.y - hh, -wH/2f, wH/2f)); yC.Add(Mathf.Clamp(lp.y + hh, -wH/2f, wH/2f));
                        }
                        var sX = xC.Distinct().OrderBy(x => x).ToList(); var sY = yC.Distinct().OrderBy(y => y).ToList();
                        for (int i=0; i<sX.Count-1; i++) {
                            for (int j=0; j<sY.Count-1; j++) {
                                float x1=sX[i], x2=sX[i+1], y1=sY[j], y2=sY[j+1]; if (x2-x1 < 0.02f || y2-y1 < 0.02f) continue;
                                Vector2 mid = new Vector2((x1+x2)/2f, (y1+y2)/2f); bool isH = false;
                                foreach(var h in wallHoles) {
                                    Vector3 lp = w.transform.InverseTransformPoint(h.transform.position);
                                    float hw = h.PlaneRect.Value.width/2f; float hh = h.PlaneRect.Value.height/2f;
                                    if (mid.x > lp.x-hw+0.01f && mid.x < lp.x+hw-0.01f && mid.y > lp.y-hh+0.01f && mid.y < lp.y+hh-0.01f) { isH=true; break; }
                                }
                                if (!isH) MRUKGeometryHelper.AppendBox(sb, ref vOff, w.transform.TransformPoint(new Vector3(mid.x, mid.y, 0)), w.transform.rotation, new Vector3(x2-x1, y2-y1, 0.25f), "Wall", Vector3.zero, globalRotation);
                            }
                        }
                    } else MRUKGeometryHelper.AppendBox(sb, ref vOff, w.transform.position, w.transform.rotation, new Vector3(wW, wH, 0.25f), "Wall", Vector3.zero, globalRotation);
                } catch (Exception ex) { Debug.LogWarning($"Wall box append failed: {ex.Message}"); }
            }

            // === DOORS AND WINDOWS (infills) ===
            foreach (var o in openings) {
                try {
                    string label = o.Label.ToString().ToUpperInvariant();
                    string mat = label.Contains("DOOR") ? "Door" : "Window";
                    float thickness = 0.08f;
                    MRUKGeometryHelper.AppendBox(sb, ref vOff, o.transform.position, o.transform.rotation, new Vector3(o.PlaneRect.Value.width, o.PlaneRect.Value.height, thickness), mat, Vector3.zero, globalRotation);
                } catch (Exception ex) { Debug.LogWarning($"Opening infill append failed: {ex.Message}"); }
            }

            return sb.ToString();
        } catch (Exception ex) {
            Debug.LogError($"GenerateBoxModel crash: {ex.Message}");
            return "# Error generating box model";
        }
    }

    private static string GeneratePolygonalReconstruction(List<MRUKRoom> rooms, float globalRotation) {
        try {
            StringBuilder sb = new StringBuilder();
            int vOff = 0;
            Quaternion gRot = Quaternion.Euler(0, globalRotation, 0);

            foreach (var room in rooms) {
                if (room == null) continue;
                try {
                    MRUKAnchor floor = room.FloorAnchors.FirstOrDefault();
                    if (floor == null || floor.PlaneBoundary2D == null) continue;
                    var boundary = floor.PlaneBoundary2D;
                    Vector3 pos = floor.transform.position; Quaternion rot = floor.transform.rotation;

                    MRUKAnchor ceiling = room.CeilingAnchors.FirstOrDefault();
                    float height = ceiling != null ? Mathf.Abs(ceiling.transform.position.y - pos.y) : 2.5f;

                    for (int i = 0; i < boundary.Count; i++) {
                        Vector3 v = gRot * (pos + rot * new Vector3(boundary[i].x, 0, boundary[i].y));
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
                    }
                    for (int i = 0; i < boundary.Count - 2; i++) sb.AppendLine($"f {vOff + 1} {vOff + i + 2} {vOff + i + 3}");
                    int fV = boundary.Count;

                    for (int i = 0; i < boundary.Count; i++) {
                        Vector3 v = gRot * (pos + rot * new Vector3(boundary[i].x, height, boundary[i].y));
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
                    }
                    for (int i = 0; i < boundary.Count - 2; i++) sb.AppendLine($"f {vOff + fV + 1} {vOff + fV + i + 3} {vOff + fV + i + 2}");
                    int cV = boundary.Count;

                    int wStart = vOff + fV + cV;
                    for (int i = 0; i < boundary.Count; i++) {
                        Vector2 p1 = boundary[i]; Vector2 p2 = boundary[(i + 1) % boundary.Count];
                        Vector3 b1 = gRot * (pos + rot * new Vector3(p1.x, 0, p1.y));
                        Vector3 b2 = gRot * (pos + rot * new Vector3(p2.x, 0, p2.y));
                        Vector3 t1 = gRot * (pos + rot * new Vector3(p1.x, height, p1.y));
                        Vector3 t2 = gRot * (pos + rot * new Vector3(p2.x, height, p2.y));
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", b1.x, b1.y, -b1.z));
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", b2.x, b2.y, -b2.z));
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", t2.x, t2.y, -t2.z));
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", t1.x, t1.y, -t1.z));
                        int bIdx = wStart + i * 4 + 1;
                        sb.AppendLine($"f {bIdx} {bIdx+1} {bIdx+2}"); sb.AppendLine($"f {bIdx} {bIdx+2} {bIdx+3}");
                    }
                    vOff += fV + cV + boundary.Count * 4;
                } catch (Exception ex) { Debug.LogWarning($"Room reconstruction failed: {ex.Message}"); }
            }
            return sb.ToString();
        } catch (Exception ex) {
            Debug.LogError($"GeneratePolygonalReconstruction crash: {ex.Message}");
            return "# Error generating polygonal reconstruction";
        }
    }
}
