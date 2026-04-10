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

public static class MRUKModelExporter {
    public static string GenerateOBJ(List<MRUKRoom> rooms, bool rawMesh) {
        StringBuilder sb = new StringBuilder();
        if (!rawMesh) sb.AppendLine("mtllib house_model.mtl");
        int vOff = 0;

        foreach (var r in rooms) {
            if (rawMesh) {
                var mfs = r.GetComponentsInChildren<MeshFilter>(true);
                foreach (var mf in mfs) {
                    if (mf.sharedMesh == null) continue;
                    var m = mf.sharedMesh;
                    for (int i = 0; i < m.vertexCount; i++) {
                        Vector3 v = mf.transform.TransformPoint(m.vertices[i]);
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
                    }
                    for (int i = 0; i < m.triangles.Length; i += 3)
                        sb.AppendLine($"f {m.triangles[i] + 1 + vOff} {m.triangles[i + 2] + 1 + vOff} {m.triangles[i + 1] + 1 + vOff}");
                    vOff += m.vertexCount;
                }
                continue;
            }

            // ANALYTICAL EXPORT
            var anchors = r.Anchors.Where(a => a.PlaneRect.HasValue).ToList();
            var walls = anchors.Where(a => a.Label.ToString().ToUpper().Contains("WALL")).ToList();
            var openings = anchors.Where(a => {
                string l = a.Label.ToString().ToUpper();
                return l.Contains("DOOR") || l.Contains("WINDOW");
            }).ToList();
            var floors = anchors.Where(a => a.Label.ToString().ToUpper().Contains("FLOOR")).ToList();

            foreach (var f in floors) {
                AppendBox(sb, ref vOff, f.transform.position, f.transform.rotation, 
                    new Vector3(f.PlaneRect.Value.width, f.PlaneRect.Value.height, 0.1f), "Floor", Vector3.zero);
            }

            foreach (var w in walls) {
                float wW = w.PlaneRect.Value.width; float wH = w.PlaneRect.Value.height;
                var wallHoles = openings.Where(o => {
                    Vector3 lp = w.transform.InverseTransformPoint(o.transform.position);
                    return Mathf.Abs(lp.z) < 0.25f && Mathf.Abs(lp.x) < (wW/2f + 0.1f) && Mathf.Abs(lp.y) < (wH/2f + 0.1f);
                }).ToList();

                if (wallHoles.Count > 0) {
                    var xC = new List<float> { -wW/2f, wW/2f };
                    var yC = new List<float> { -wH/2f, wH/2f };
                    foreach(var h in wallHoles) {
                        Vector3 lp = w.transform.InverseTransformPoint(h.transform.position);
                        xC.Add(Mathf.Clamp(lp.x - h.PlaneRect.Value.width/2f, -wW/2f, wW/2f));
                        xC.Add(Mathf.Clamp(lp.x + h.PlaneRect.Value.width/2f, -wW/2f, wW/2f));
                        yC.Add(Mathf.Clamp(lp.y - h.PlaneRect.Value.height/2f, -wH/2f, wH/2f));
                        yC.Add(Mathf.Clamp(lp.y + h.PlaneRect.Value.height/2f, -wH/2f, wH/2f));
                    }
                    var sX = xC.Distinct().OrderBy(x => x).ToList(); var sY = yC.Distinct().OrderBy(y => y).ToList();
                    for (int i=0; i<sX.Count-1; i++) {
                        for (int j=0; j<sY.Count-1; j++) {
                            float x1=sX[i], x2=sX[i+1], y1=sY[j], y2=sY[j+1];
                            if (x2-x1 < 0.01f || y2-y1 < 0.01f) continue;
                            Vector2 mid = new Vector2((x1+x2)/2f, (y1+y2)/2f);
                            bool isH = false;
                            foreach(var h in wallHoles) {
                                Vector3 lp = w.transform.InverseTransformPoint(h.transform.position);
                                if (mid.x > lp.x-h.PlaneRect.Value.width/2f+0.01f && mid.x < lp.x+h.PlaneRect.Value.width/2f-0.01f && 
                                    mid.y > lp.y-h.PlaneRect.Value.height/2f+0.01f && mid.y < lp.y+h.PlaneRect.Value.height/2f-0.01f) { isH=true; break; }
                            }
                            if (!isH) AppendBox(sb, ref vOff, w.transform.TransformPoint(new Vector3(mid.x, mid.y, 0)), w.transform.rotation, new Vector3(x2-x1, y2-y1, 0.25f), "Wall", Vector3.zero);
                        }
                    }
                } else AppendBox(sb, ref vOff, w.transform.position, w.transform.rotation, new Vector3(wW, wH, 0.25f), "Wall", Vector3.zero);
            }

            foreach (var o in openings) {
                string l = o.Label.ToString().ToUpper();
                AppendBox(sb, ref vOff, o.transform.position, o.transform.rotation, new Vector3(o.PlaneRect.Value.width, o.PlaneRect.Value.height, l.Contains("DOOR") ? 0.10f : 0.12f), l.Contains("DOOR") ? "Door" : "Window", Vector3.zero);
            }
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
        for (int i = 0; i < bt.Length; i += 3)
            sb.AppendLine($"f {bt[i] + 1 + vOff} {bt[i + 2] + 1 + vOff} {bt[i + 1] + 1 + vOff}");
        vOff += 8;
    }

    public static string GenerateMTL() {
        return "newmtl Wall\nKd 0.4 0.4 0.4\nnewmtl Door\nKd 0.5 0.3 0.1\nnewmtl Window\nKd 0.3 0.6 0.9\nnewmtl Floor\nKd 0.7 0.7 0.7";
    }
}
