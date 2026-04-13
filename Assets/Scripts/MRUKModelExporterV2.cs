using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Globalization;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

public static class MRUKModelExporterV2 {
    public static string GenerateOBJ(List<MRUKRoom> rooms, bool rawMesh, float globalRotation = 0, bool includeGlobalMeshes = false) {
        StringBuilder sb = new StringBuilder();
        if (!rawMesh) sb.AppendLine("mtllib house_model.mtl");
        int vOff = 0;
        Quaternion gRot = Quaternion.Euler(0, globalRotation, 0);

        if (rawMesh) {
            List<MeshFilter> meshTargets = new List<MeshFilter>();
            List<Mesh> rawMeshes = new List<Mesh>();
            List<Matrix4x4> rawMeshTransforms = new List<Matrix4x4>();
            
            if (includeGlobalMeshes) {
                foreach(var t in GameObject.FindObjectsByType<Transform>(FindObjectsSortMode.None)) {
                    var mf = t.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null && mf.sharedMesh.vertexCount > 10) {
                        string n = t.name.ToUpper();
                        if (n.Contains("XRMENU") || n.Contains("XRRAY") || (mf.sharedMesh.name == "Cube" && mf.sharedMesh.vertexCount == 24)) continue;
                        meshTargets.Add(mf);
                    }
                    var ovrMesh = t.GetComponent("OVRSceneMesh");
                    if (ovrMesh != null) {
                        try {
                            var type = ovrMesh.GetType();
                            var meshProp = type.GetProperty("Mesh") ?? type.GetProperty("sharedMesh") ?? type.GetProperty("_mesh");
                            var m = meshProp?.GetValue(ovrMesh) as Mesh;
                            if (m == null) {
                                var mfOnOvr = t.GetComponent<MeshFilter>();
                                if (mfOnOvr != null) m = mfOnOvr.sharedMesh;
                            }
                            if (m != null && m.vertexCount > 0) { 
                                if (!meshTargets.Any(mt => mt.sharedMesh == m)) {
                                    rawMeshes.Add(m); rawMeshTransforms.Add(t.localToWorldMatrix); 
                                }
                            }
                        } catch {}
                    }
                }
            } else {
                foreach (var r in rooms) meshTargets.AddRange(r.GetComponentsInChildren<MeshFilter>(true));
            }

            // If we found NO raw scan vertices, fallback to generating a clean mesh from structural planes
            if (meshTargets.Count == 0 && rawMeshes.Count == 0) {
                Debug.LogWarning("No raw scan found, falling back to plane-based reconstruction for mesh.obj");
                return GeneratePolygonalReconstruction(rooms, globalRotation);
            }

            foreach (var mf in meshTargets) {
                var m = mf.sharedMesh;
                for (int i = 0; i < m.vertexCount; i++) {
                    Vector3 v = gRot * mf.transform.TransformPoint(m.vertices[i]);
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
                }
                for (int i = 0; i < m.triangles.Length; i += 3)
                    sb.AppendLine($"f {m.triangles[i] + 1 + vOff} {m.triangles[i+2] + 1 + vOff} {m.triangles[i+1] + 1 + vOff}");
                vOff += m.vertexCount;
            }

            for (int j = 0; j < rawMeshes.Count; j++) {
                var m = rawMeshes[j]; var mat = rawMeshTransforms[j];
                for (int i = 0; i < m.vertexCount; i++) {
                    Vector3 v = gRot * mat.MultiplyPoint(m.vertices[i]);
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
                }
                for (int i = 0; i < m.triangles.Length; i += 3)
                    sb.AppendLine($"f {m.triangles[i] + 1 + vOff} {m.triangles[i+2] + 1 + vOff} {m.triangles[i+1] + 1 + vOff}");
                vOff += m.vertexCount;
            }
            return sb.ToString();
        }

        // Analytical box model (what works well for user now)
        return GenerateBoxModel(rooms, globalRotation);
    }

    private static string GenerateBoxModel(List<MRUKRoom> rooms, float globalRotation) {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("mtllib house_model.mtl");
        int vOff = 0;
        var allAnchors = rooms.SelectMany(r => r.Anchors).ToList();
        var walls = allAnchors.Where(a => a.Label.ToString().ToUpper().Contains("WALL") && a.PlaneRect.HasValue).ToList();
        var floors = allAnchors.Where(a => a.Label.ToString().ToUpper().Contains("FLOOR") && a.PlaneRect.HasValue).ToList();
        var openingsForHoleCutting = allAnchors.Where(a => (a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW")) && a.PlaneRect.HasValue).GroupBy(a => a.transform.position.ToString("F3")).Select(g => g.First()).ToList();

        foreach (var f in floors) MRUKGeometryHelper.AppendBox(sb, ref vOff, f.transform.position, f.transform.rotation, new Vector3(f.PlaneRect.Value.width, f.PlaneRect.Value.height, 0.1f), "Floor", new Vector3(0, 0, 0.05f), globalRotation);

        HashSet<MRUKAnchor> usedOpenings = new HashSet<MRUKAnchor>();
        foreach (var w in walls) {
            float wW = w.PlaneRect.Value.width; float wH = w.PlaneRect.Value.height;
            var wallHoles = openingsForHoleCutting.Where(o => {
                Vector3 lp = w.transform.InverseTransformPoint(o.transform.position);
                bool intersect = Mathf.Abs(lp.z) < 0.3f && Mathf.Abs(lp.x) < (wW/2f + 0.1f) && Mathf.Abs(lp.y) < (wH/2f + 0.1f);
                if (intersect) usedOpenings.Add(o);
                return intersect;
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
                        float x1=sX[i], x2=sX[i+1], y1=sY[j], y2=sY[j+1];
                        if (x2-x1 < 0.02f || y2-y1 < 0.02f) continue;
                        Vector2 mid = new Vector2((x1+x2)/2f, (y1+y2)/2f);
                        bool isH = false;
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

        foreach (var o in usedOpenings) {
            string l = o.Label.ToString().ToUpper();
            MRUKGeometryHelper.AppendBox(sb, ref vOff, o.transform.position, o.transform.rotation, new Vector3(o.PlaneRect.Value.width, o.PlaneRect.Value.height, l.Contains("DOOR") ? 0.10f : 0.12f), l.Contains("DOOR") ? "Door" : "Window", Vector3.zero, globalRotation);
        }
        return sb.ToString();
    }

    private static string GeneratePolygonalReconstruction(List<MRUKRoom> rooms, float globalRotation) {
        StringBuilder sb = new StringBuilder();
        int vOff = 0;
        Quaternion gRot = Quaternion.Euler(0, globalRotation, 0);

        foreach (var room in rooms) {
            // Use FloorAnchors property via reflection to avoid obsolete warning and support multiple floors
            MRUKAnchor floorAnchor = null;
            try {
                var prop = room.GetType().GetProperty("FloorAnchors");
                var list = prop?.GetValue(room) as System.Collections.IEnumerable;
                if (list != null) {
                    foreach (var item in list) { floorAnchor = item as MRUKAnchor; if (floorAnchor != null) break; }
                }
                if (floorAnchor == null) {
                    floorAnchor = room.GetType().GetProperty("FloorAnchor")?.GetValue(room) as MRUKAnchor;
                }
            } catch {}

            if (floorAnchor == null || floorAnchor.PlaneBoundary2D == null) continue;
            var boundary = floorAnchor.PlaneBoundary2D;
            Vector3 pos = floorAnchor.transform.position;
            Quaternion rot = floorAnchor.transform.rotation;

            // Use CeilingAnchors property via reflection to avoid obsolete warning
            MRUKAnchor ceilingAnchor = null;
            try {
                var prop = room.GetType().GetProperty("CeilingAnchors");
                var list = prop?.GetValue(room) as System.Collections.IEnumerable;
                if (list != null) {
                    foreach (var item in list) { ceilingAnchor = item as MRUKAnchor; if (ceilingAnchor != null) break; }
                }
                if (ceilingAnchor == null) {
                    ceilingAnchor = room.GetType().GetProperty("CeilingAnchor")?.GetValue(room) as MRUKAnchor;
                }
            } catch {}

            float height = ceilingAnchor != null ? Mathf.Abs(ceilingAnchor.transform.position.y - pos.y) : 2.5f;

            // Floor
            for (int i = 0; i < boundary.Count; i++) {
                Vector3 v = gRot * (pos + rot * new Vector3(boundary[i].x, 0, boundary[i].y));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
            }
            for (int i = 0; i < boundary.Count - 2; i++) sb.AppendLine($"f {vOff + 1} {vOff + i + 2} {vOff + i + 3}");
            int floorV = boundary.Count;

            // Ceiling
            for (int i = 0; i < boundary.Count; i++) {
                Vector3 v = gRot * (pos + rot * new Vector3(boundary[i].x, height, boundary[i].y));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
            }
            for (int i = 0; i < boundary.Count - 2; i++) sb.AppendLine($"f {vOff + floorV + 1} {vOff + floorV + i + 3} {vOff + floorV + i + 2}");
            int ceilV = boundary.Count;

            // Walls
            int wallStartV = vOff + floorV + ceilV;
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
                int baseIdx = wallStartV + i * 4 + 1;
                sb.AppendLine($"f {baseIdx} {baseIdx+1} {baseIdx+2}");
                sb.AppendLine($"f {baseIdx} {baseIdx+2} {baseIdx+3}");
            }
            vOff += floorV + ceilV + boundary.Count * 4;
        }
        return sb.ToString();
    }

    public static byte[] GenerateGLB(List<MRUKRoom> rooms, bool rawMesh, float globalRotation) {
        // Logic to create a Unity Mesh first, then convert to GLB bytes
        // This is a complex process, so we create a temporary mesh and use a minimal GLB formatter
        Mesh tempMesh = null;
        if (rawMesh) {
            // For now, if we have raw meshes, we'd need to combine them. 
            // Simplifying: if no raw scan data, we reconstruct from polygons.
            tempMesh = CreateReconstructedMesh(rooms, globalRotation);
        } else {
            tempMesh = CreateBoxReconstructedMesh(rooms, globalRotation);
        }

        if (tempMesh == null || tempMesh.vertexCount == 0) return null;
        return ExportMeshToGLB(tempMesh);
    }

    private static Mesh CreateReconstructedMesh(List<MRUKRoom> rooms, float globalRotation) {
        // Create a single mesh from the polygonal reconstruction
        List<Vector3> verts = new List<Vector3>();
        List<int> tris = new List<int>();
        Quaternion gRot = Quaternion.Euler(0, globalRotation, 0);

        foreach (var room in rooms) {
            MRUKAnchor floor = null;
            try { floor = (room.GetType().GetProperty("FloorAnchors")?.GetValue(room) as System.Collections.IEnumerable)?.Cast<MRUKAnchor>().FirstOrDefault(); } catch {}
            if (floor == null || floor.PlaneBoundary2D == null) continue;

            int baseV = verts.Count;
            var boundary = floor.PlaneBoundary2D;
            
            // Use CeilingAnchors property via reflection to avoid obsolete warning
            MRUKAnchor ceiling = null;
            try { ceiling = (room.GetType().GetProperty("CeilingAnchors")?.GetValue(room) as System.Collections.IEnumerable)?.Cast<MRUKAnchor>().FirstOrDefault(); } catch {}
            if (ceiling == null) {
                try { ceiling = room.GetType().GetProperty("CeilingAnchor")?.GetValue(room) as MRUKAnchor; } catch {}
            }

            float h = ceiling != null ? Mathf.Abs(ceiling.transform.position.y - floor.transform.position.y) : 2.5f;
            Vector3 p = floor.transform.position; Quaternion r = floor.transform.rotation;

            foreach (var b in boundary) verts.Add(gRot * (p + r * new Vector3(b.x, 0, b.y)));
            for (int i = 0; i < boundary.Count - 2; i++) { tris.Add(baseV); tris.Add(baseV + i + 1); tris.Add(baseV + i + 2); }
            
            int ceilBase = verts.Count;
            foreach (var b in boundary) verts.Add(gRot * (p + r * new Vector3(b.x, h, b.y)));
            for (int i = 0; i < boundary.Count - 2; i++) { tris.Add(ceilBase); tris.Add(ceilBase + i + 2); tris.Add(ceilBase + i + 1); }

            int wallBase = verts.Count;
            for (int i = 0; i < boundary.Count; i++) {
                int next = (i + 1) % boundary.Count;
                int vIdx = wallBase + i * 4;
                verts.Add(gRot * (p + r * new Vector3(boundary[i].x, 0, boundary[i].y)));
                verts.Add(gRot * (p + r * new Vector3(boundary[next].x, 0, boundary[next].y)));
                verts.Add(gRot * (p + r * new Vector3(boundary[next].x, h, boundary[next].y)));
                verts.Add(gRot * (p + r * new Vector3(boundary[i].x, h, boundary[i].y)));
                tris.Add(vIdx); tris.Add(vIdx + 1); tris.Add(vIdx + 2);
                tris.Add(vIdx); tris.Add(vIdx + 2); tris.Add(vIdx + 3);
            }
        }
        Mesh m = new Mesh(); m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        m.vertices = verts.ToArray(); m.triangles = tris.ToArray(); m.RecalculateNormals();
        return m;
    }

    private static Mesh CreateBoxReconstructedMesh(List<MRUKRoom> rooms, float globalRotation) {
        // Simplified box-based reconstruction for GLB
        // Since we can't easily combine multiple textured boxes into one Mesh without complex logic, 
        // we'll use the same polygonal reconstruction for the analytical GLB too, but simplified.
        return CreateReconstructedMesh(rooms, globalRotation);
    }

    private static byte[] ExportMeshToGLB(Mesh mesh) {
        // Minimal GLB exporter implementation
        var vertices = mesh.vertices; var triangles = mesh.triangles;
        int vCount = vertices.Length; int tCount = triangles.Length;

        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms)) {
            // Placeholder GLB logic - in a real scenario we'd use a library, 
            // but we'll use the binary structure from your SimpleGltfExporter
            var posBytes = new byte[vCount * 12]; Buffer.BlockCopy(vertices, 0, posBytes, 0, posBytes.Length);
            var idxBytes = new byte[tCount * 4]; Buffer.BlockCopy(triangles, 0, idxBytes, 0, idxBytes.Length);

            // JSON part
            string json = "{\"asset\":{\"version\":\"2.0\"},\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}],\"nodes\":[{\"mesh\":0}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0,\"bufferViews\":[{\"buffer\":0,\"byteLength\":" + posBytes.Length + "},{\"buffer\":0,\"byteOffset\":" + posBytes.Length + ",\"byteLength\":" + idxBytes.Length + "}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":" + vCount + ",\"type\":\"VEC3\"},{\"bufferView\":1,\"componentType\":5125,\"count\":" + tCount + ",\"type\":\"SCALAR\"}],\"buffers\":[{\"byteLength\":" + (posBytes.Length + idxBytes.Length) + "}]}";
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            int jsonPadding = (4 - (jsonBytes.Length % 4)) % 4;
            int binPadding = (4 - ((posBytes.Length + idxBytes.Length) % 4)) % 4;

            bw.Write(0x46546C67); bw.Write(2); // Header
            bw.Write(12 + 8 + jsonBytes.Length + jsonPadding + 8 + posBytes.Length + idxBytes.Length + binPadding); // Total
            bw.Write(jsonBytes.Length + jsonPadding); bw.Write(0x4E4F534A); bw.Write(jsonBytes);
            for (int i=0; i<jsonPadding; i++) bw.Write((byte)0x20);
            bw.Write(posBytes.Length + idxBytes.Length + binPadding); bw.Write(0x004E4942);
            bw.Write(posBytes); bw.Write(idxBytes);
            for (int i=0; i<binPadding; i++) bw.Write((byte)0);
            return ms.ToArray();
        }
    }

    public static string GenerateRawScanOBJ(float globalRotation) {
        StringBuilder sb = new StringBuilder();
        int vOff = 0;
        Quaternion gRot = Quaternion.Euler(0, globalRotation, 0);
        
        var allT = GameObject.FindObjectsByType<Transform>(FindObjectsSortMode.None);
        foreach(var t in allT) {
            var ovrMesh = t.GetComponent("OVRSceneMesh");
            if (ovrMesh != null) {
                try {
                    var type = ovrMesh.GetType();
                    var meshProp = type.GetProperty("Mesh") ?? type.GetProperty("sharedMesh") ?? type.GetProperty("_mesh");
                    var m = meshProp?.GetValue(ovrMesh) as Mesh;
                    if (m == null) {
                        var mfOnOvr = t.GetComponent<MeshFilter>();
                        if (mfOnOvr != null) m = mfOnOvr.sharedMesh;
                    }
                    if (m != null && m.vertexCount > 0) { 
                        for (int i = 0; i < m.vertexCount; i++) {
                            Vector3 v = gRot * t.TransformPoint(m.vertices[i]);
                            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
                        }
                        for (int i = 0; i < m.triangles.Length; i += 3)
                            sb.AppendLine($"f {m.triangles[i] + 1 + vOff} {m.triangles[i+2] + 1 + vOff} {m.triangles[i+1] + 1 + vOff}");
                        vOff += m.vertexCount;
                    }
                } catch {}
            }
        }
        return sb.ToString();
    }

    public static byte[] GenerateRawScanGLB(float globalRotation) {
        List<Vector3> verts = new List<Vector3>();
        List<int> tris = new List<int>();
        Quaternion gRot = Quaternion.Euler(0, globalRotation, 0);

        var allT = GameObject.FindObjectsByType<Transform>(FindObjectsSortMode.None);
        foreach(var t in allT) {
            var ovrMesh = t.GetComponent("OVRSceneMesh");
            if (ovrMesh != null) {
                try {
                    var type = ovrMesh.GetType();
                    var mProp = type.GetProperty("Mesh") ?? type.GetProperty("sharedMesh") ?? type.GetProperty("_mesh");
                    var m = mProp?.GetValue(ovrMesh) as Mesh;
                    if (m == null) m = t.GetComponent<MeshFilter>()?.sharedMesh;
                    if (m != null && m.vertexCount > 0) {
                        int baseV = verts.Count;
                        foreach(var v in m.vertices) verts.Add(gRot * t.TransformPoint(v));
                        foreach(var i in m.triangles) tris.Add(baseV + i);
                    }
                } catch {}
            }
        }
        if (verts.Count == 0) return null;
        Mesh final = new Mesh(); final.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        final.vertices = verts.ToArray(); final.triangles = tris.ToArray(); final.RecalculateNormals();
        return ExportMeshToGLB(final);
    }

    public static string GenerateMTL() {
        return "newmtl Wall\nKd 0.4 0.4 0.4\nnewmtl Door\nKd 0.5 0.3 0.1\nnewmtl Window\nKd 0.3 0.6 0.9\nnewmtl Floor\nKd 0.7 0.7 0.7";
    }
}
