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
    private static string GetGameObjectPath(GameObject obj) {
        string path = "/" + obj.name;
        Transform t = obj.transform;
        while (t.parent != null) {
            t = t.parent;
            path = "/" + t.name + path;
        }
        return path;
    }

    public static string GenerateOBJ(List<MRUKRoom> rooms, bool rawMesh, float globalRotation = 0, bool includeGlobalMeshes = false) {
        StringBuilder sb = new StringBuilder();
        if (!rawMesh) sb.AppendLine("mtllib house_model.mtl");
        int vOff = 0;
        Quaternion gRot = Quaternion.Euler(0, globalRotation, 0);

        if (rawMesh) {
            // Collect all meshes to export
            List<MeshFilter> meshTargets = new List<MeshFilter>();
            
            if (includeGlobalMeshes) {
                var allInScene = GameObject.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
                foreach(var mf in allInScene) {
                    string n = mf.name.ToUpper();
                    string path = GetGameObjectPath(mf.gameObject).ToUpper();
                    
                    // EXCLUDE: Anything in Dollhouse, UI, or our analytical cube names
                    if (path.Contains("DOLLHOUSE") || n.Contains("XRMENU")) continue;
                    // Exclude analytical cubes by exact label name if they are our cubes
                    if (n.Contains("WALL") || n.Contains("FLOOR") || n.Contains("DOOR") || n.Contains("WINDOW") || n.Contains("OPENING")) {
                        // If it's a simple Cube mesh, it's likely our analytical one
                        if (mf.sharedMesh != null && mf.sharedMesh.name == "Cube" && mf.sharedMesh.vertexCount == 24) continue;
                    }

                    if (mf.sharedMesh == null || mf.sharedMesh.vertexCount < 10) continue;

                    if (!meshTargets.Contains(mf)) meshTargets.Add(mf);
                }
            } else {
                foreach (var r in rooms) {
                    meshTargets.AddRange(r.GetComponentsInChildren<MeshFilter>(true));
                }
            }

            foreach (var mf in meshTargets) {
                if (mf.sharedMesh == null) continue;
                var m = mf.sharedMesh;
                for (int i = 0; i < m.vertexCount; i++) {
                    Vector3 v = gRot * mf.transform.TransformPoint(m.vertices[i]);
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
                }
                if (m.normals.Length == m.vertexCount) {
                    for (int i = 0; i < m.normals.Length; i++) {
                        Vector3 n = gRot * mf.transform.TransformDirection(m.normals[i]);
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "vn {0:F4} {1:F4} {2:F4}", n.x, n.y, -n.z));
                    }
                }
                for (int i = 0; i < m.triangles.Length; i += 3) {
                    int i1 = m.triangles[i] + 1 + vOff; int i2 = m.triangles[i+1] + 1 + vOff; int i3 = m.triangles[i+2] + 1 + vOff;
                    sb.AppendLine($"f {i1} {i3} {i2}");
                }
                vOff += m.vertexCount;
            }
            return sb.ToString();
        }

        var allAnchors = rooms.SelectMany(r => r.Anchors).ToList();
        var walls = allAnchors.Where(a => a.Label.ToString().ToUpper().Contains("WALL") && a.PlaneRect.HasValue).ToList();
        var floors = allAnchors.Where(a => a.Label.ToString().ToUpper().Contains("FLOOR") && a.PlaneRect.HasValue).ToList();
        
        // openingsToExport: only those belonging to the rooms we are currently exporting
        var openingsToExport = allAnchors.Where(a => (a.Label.ToString().ToUpper().Contains("DOOR") || a.Label.ToString().ToUpper().Contains("WINDOW")) && a.PlaneRect.HasValue)
            .GroupBy(a => a.transform.position.ToString("F3")).Select(g => g.First()).ToList();

        // openingsForHoleCutting: potentially ALL openings in the house to ensure holes are cut even if the door "belongs" to a neighbor room
        List<MRUKAnchor> openingsForHoleCutting = openingsToExport;
        #if META_XR_SDK_INSTALLED
        if (MRUK.Instance != null) {
            openingsForHoleCutting = MRUK.Instance.Rooms.SelectMany(r => r.Anchors)
                .Where(a => (a.Label.ToString().ToUpper().Contains("DOOR") || a.Label.ToString().ToUpper().Contains("WINDOW")) && a.PlaneRect.HasValue)
                .GroupBy(a => a.transform.position.ToString("F3")).Select(g => g.First()).ToList();
        }
        #endif

        foreach (var f in floors) {
            // Apply 0.1m thickness. Local Z points DOWN at 270 deg X rot.
            // Move it 0.05m in local Z to make the top flush with anchor.
            AppendBox(sb, ref vOff, gRot * f.transform.position, gRot * f.transform.rotation, 
                new Vector3(f.PlaneRect.Value.width, f.PlaneRect.Value.height, 0.1f), "Floor", new Vector3(0, 0, 0.05f));
        }

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
                        if (!isH) AppendBox(sb, ref vOff, gRot * w.transform.TransformPoint(new Vector3(mid.x, mid.y, 0)), gRot * w.transform.rotation, new Vector3(x2-x1, y2-y1, 0.25f), "Wall", Vector3.zero);
                    }
                }
            } else AppendBox(sb, ref vOff, gRot * w.transform.position, gRot * w.transform.rotation, new Vector3(wW, wH, 0.25f), "Wall", Vector3.zero);
        }

        foreach (var o in usedOpenings) {
            string l = o.Label.ToString().ToUpper();
            AppendBox(sb, ref vOff, gRot * o.transform.position, gRot * o.transform.rotation, new Vector3(o.PlaneRect.Value.width, o.PlaneRect.Value.height, l.Contains("DOOR") ? 0.10f : 0.12f), l.Contains("DOOR") ? "Door" : "Window", Vector3.zero);
        }
        return sb.ToString();
    }

    private static void AppendBox(StringBuilder sb, ref int vOff, Vector3 pos, Quaternion rot, Vector3 s, string mat, Vector3 localOffset) {
        sb.AppendLine("usemtl " + mat);
        Vector3[] bv = { new Vector3(-0.5f,-0.5f,0.5f), new Vector3(0.5f,-0.5f,0.5f), new Vector3(0.5f,0.5f,0.5f), new Vector3(-0.5f,0.5f,0.5f), new Vector3(-0.5f,-0.5f,-0.5f), new Vector3(0.5f,-0.5f,-0.5f), new Vector3(0.5f,0.5f,-0.5f), new Vector3(-0.5f,0.5f,-0.5f) };
        int[] bt = { 0,1,2, 0,2,3, 5,4,7, 5,7,6, 4,0,3, 4,3,7, 1,5,6, 1,6,2, 3,2,6, 3,6,7, 4,5,1, 4,1,0 };
        for (int i = 0; i < 8; i++) {
            Vector3 v = pos + rot * (Vector3.Scale(bv[i], s) + localOffset);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
        }
        sb.AppendLine("vn 0 0 1\nvn 0 0 -1\nvn 0 1 0\nvn 0 -1 0\nvn 1 0 0\nvn -1 0 0");
        for (int i = 0; i < bt.Length; i += 3)
            sb.AppendLine($"f {bt[i] + 1 + vOff} {bt[i + 2] + 1 + vOff} {bt[i + 1] + 1 + vOff}");
        vOff += 8;
    }

    public static string GenerateMTL() {
        return "newmtl Wall\nKd 0.4 0.4 0.4\nnewmtl Door\nKd 0.5 0.3 0.1\nnewmtl Window\nKd 0.3 0.6 0.9\nnewmtl Floor\nKd 0.7 0.7 0.7";
    }
}
