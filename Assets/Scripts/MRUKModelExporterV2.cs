using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Globalization;
using UnityEngine;
using Unity.Collections;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
using Meta.XR;
#endif

public static class MRUKModelExporterV2 {
    
    public static async System.Threading.Tasks.Task<string> GenerateRawHighFidelityMesh(float globalRotation) {
        #if META_XR_SDK_INSTALLED
        StringBuilder sb = new StringBuilder();
        int vOff = 0;
        Quaternion gRot = Quaternion.Euler(0, globalRotation, 0);

        try {
            if (MRUK.Instance == null) return "";
            
            // Iterate through all rooms and their anchors to find High-Fidelity Triangle Meshes
            foreach (var room in MRUK.Instance.Rooms) {
                foreach (var anchorComp in room.Anchors) {
                    var anchor = anchorComp.Anchor;
                    
                    // Priority 1: OVRTriangleMesh (The most modern way for 2026)
                    if (anchor.TryGetComponent<OVRTriangleMesh>(out var triangleMesh)) {
                        if (triangleMesh.TryGetCounts(out var vertexCount, out var triangleCount)) {
                            using var vertices = new NativeArray<Vector3>(vertexCount, Allocator.Temp);
                            using var indices = new NativeArray<int>(triangleCount * 3, Allocator.Temp);
                            
                            if (triangleMesh.TryGetMesh(vertices, indices)) {
                                Matrix4x4 matrix = Matrix4x4.identity;
                                if (anchor.TryGetComponent<OVRLocatable>(out var loc) && loc.TryGetSceneAnchorPose(out var pose)) {
                                    matrix = Matrix4x4.TRS(pose.Position ?? Vector3.zero, pose.Rotation ?? Quaternion.identity, Vector3.one);
                                }

                                for (int i = 0; i < vertexCount; i++) {
                                    Vector3 v = gRot * matrix.MultiplyPoint(vertices[i]);
                                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
                                }
                                for (int i = 0; i < indices.Length; i += 3) {
                                    sb.AppendLine($"f {indices[i] + 1 + vOff} {indices[i+2] + 1 + vOff} {indices[i+1] + 1 + vOff}");
                                }
                                vOff += vertexCount;
                            }
                        }
                    }
                    // Priority 2: OVRRoomMesh (Classic high-fidelity fallback)
                    else if (anchor.TryGetComponent<OVRRoomMesh>(out var roomMesh)) {
                        if (roomMesh.TryGetRoomMeshCounts(out var vC, out var fC)) {
                            using var vertices = new NativeArray<Vector3>(vC, Allocator.Temp);
                            using var faces = new NativeArray<OVRRoomMesh.Face>(fC, Allocator.Temp);
                            
                            if (roomMesh.TryGetRoomMesh(vertices, faces)) {
                                Matrix4x4 matrix = Matrix4x4.identity;
                                if (anchor.TryGetComponent<OVRLocatable>(out var loc) && loc.TryGetSceneAnchorPose(out var pose)) {
                                    matrix = Matrix4x4.TRS(pose.Position ?? Vector3.zero, pose.Rotation ?? Quaternion.identity, Vector3.one);
                                }

                                for (int i = 0; i < vC; i++) {
                                    Vector3 v = gRot * matrix.MultiplyPoint(vertices[i]);
                                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
                                }

                                foreach (var face in faces) {
                                    if (roomMesh.TryGetRoomFaceIndexCount(face.Uuid, out var indexCount)) {
                                        using var fIndices = new NativeArray<uint>(indexCount, Allocator.Temp);
                                        if (roomMesh.TryGetRoomFaceIndices(face.Uuid, fIndices)) {
                                            for (int i = 0; i < indexCount; i += 3)
                                                sb.AppendLine($"f {fIndices[i] + 1 + vOff} {fIndices[i+2] + 1 + vOff} {fIndices[i+1] + 1 + vOff}");
                                        }
                                    }
                                }
                                vOff += vC;
                            }
                        }
                    }
                }
            }
        } catch (Exception ex) {
            Debug.LogError("GenerateRawHighFidelityMesh Error: " + ex.Message);
        }

        await System.Threading.Tasks.Task.Yield();
        return sb.ToString();
        #else
        await System.Threading.Tasks.Task.Yield();
        return "";
        #endif
    }

    public static string GenerateOBJ(List<MRUKRoom> rooms, bool rawMesh, float globalRotation = 0) {
        if (rawMesh) return GeneratePolygonalReconstruction(rooms, globalRotation);
        return GenerateBoxModel(rooms, globalRotation);
    }

    private static string GenerateBoxModel(List<MRUKRoom> rooms, float globalRotation) {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("mtllib house_model.mtl");
        int vOff = 0;
        var allAnchors = rooms.SelectMany(r => r.Anchors).ToList();
        var walls = allAnchors.Where(a => a.Label.ToString().ToUpper().Contains("WALL") && a.PlaneRect.HasValue).ToList();
        var floors = allAnchors.Where(a => a.Label.ToString().ToUpper().Contains("FLOOR") && a.PlaneRect.HasValue).ToList();
        var openings = allAnchors.Where(a => (a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW")) && a.PlaneRect.HasValue).GroupBy(a => a.transform.position.ToString("F3")).Select(g => g.First()).ToList();

        foreach (var f in floors) MRUKGeometryHelper.AppendBox(sb, ref vOff, f.transform.position, f.transform.rotation, new Vector3(f.PlaneRect.Value.width, f.PlaneRect.Value.height, 0.1f), "Floor", new Vector3(0, 0, 0.05f), globalRotation);

        foreach (var w in walls) {
            float wW = w.PlaneRect.Value.width; float wH = w.PlaneRect.Value.height;
            var wallHoles = openings.Where(o => {
                Vector3 lp = w.transform.InverseTransformPoint(o.transform.position);
                return Mathf.Abs(lp.z) < 0.3f && Mathf.Abs(lp.x) < (wW/2f + 0.1f) && Mathf.Abs(lp.y) < (wH/2f + 0.1f);
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
        }
        return sb.ToString();
    }

    private static string GeneratePolygonalReconstruction(List<MRUKRoom> rooms, float globalRotation) {
        StringBuilder sb = new StringBuilder();
        int vOff = 0;
        Quaternion gRot = Quaternion.Euler(0, globalRotation, 0);

        foreach (var room in rooms) {
            MRUKAnchor floor = null;
            try {
                var prop = room.GetType().GetProperty("FloorAnchors");
                var list = prop?.GetValue(room) as System.Collections.IEnumerable;
                if (list != null) foreach (var item in list) { floor = item as MRUKAnchor; if (floor != null) break; }
                if (floor == null) floor = room.GetType().GetProperty("FloorAnchor")?.GetValue(room) as MRUKAnchor;
            } catch {}

            if (floor == null || floor.PlaneBoundary2D == null) continue;
            var boundary = floor.PlaneBoundary2D;
            Vector3 pos = floor.transform.position; Quaternion rot = floor.transform.rotation;

            MRUKAnchor ceiling = null;
            try {
                var prop = room.GetType().GetProperty("CeilingAnchors");
                var list = prop?.GetValue(room) as System.Collections.IEnumerable;
                if (list != null) foreach (var item in list) { ceiling = item as MRUKAnchor; if (ceiling != null) break; }
                if (ceiling == null) ceiling = room.GetType().GetProperty("CeilingAnchor")?.GetValue(room) as MRUKAnchor;
            } catch {}

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
        }
        return sb.ToString();
    }

    public static byte[] GenerateGLB(List<MRUKRoom> rooms, bool polygonal, float globalRotation) {
        Mesh m = new Mesh(); m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        string obj = polygonal ? GeneratePolygonalReconstruction(rooms, globalRotation) : GenerateBoxModel(rooms, globalRotation);
        if (string.IsNullOrEmpty(obj)) return null;
        
        List<Vector3> verts = new List<Vector3>(); List<int> tris = new List<int>();
        string[] lines = obj.Split('\n');
        foreach(var l in lines) {
            if (l.StartsWith("v ")) {
                var p = l.Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4)
                    verts.Add(new Vector3(float.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture), float.Parse(p[3], CultureInfo.InvariantCulture)));
            } else if (l.StartsWith("f ")) {
                var p = l.Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4) {
                    tris.Add(int.Parse(p[1].Split('/')[0]) - 1); tris.Add(int.Parse(p[3].Split('/')[0]) - 1); tris.Add(int.Parse(p[2].Split('/')[0]) - 1);
                }
            }
        }
        m.vertices = verts.ToArray(); m.triangles = tris.ToArray(); m.RecalculateNormals();
        return ExportMeshToGLB(m);
    }

    public static byte[] ExportMeshToGLB(Mesh mesh) {
        var vertices = mesh.vertices; var triangles = mesh.triangles;
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

    public static string GenerateMTL() {
        return "newmtl Wall\nKd 0.4 0.4 0.4\nnewmtl Door\nKd 0.5 0.3 0.1\nnewmtl Window\nKd 0.3 0.6 0.9\nnewmtl Floor\nKd 0.7 0.7 0.7";
    }
}
