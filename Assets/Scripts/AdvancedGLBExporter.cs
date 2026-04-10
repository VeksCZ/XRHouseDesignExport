using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Globalization;
using UnityEngine;

public class AdvancedGLBExporter
{
    public static void Export(List<Meta.XR.MRUtilityKit.MRUKRoom> rooms, string path, bool rawMesh, float globalRotation = 0) {
        List<Vector3> verts = new List<Vector3>();
        List<int> tris = new List<int>();
        Quaternion gRot = Quaternion.Euler(0, globalRotation, 0);

        foreach (var r in rooms) {
            if (rawMesh) {
                var mfs = r.GetComponentsInChildren<MeshFilter>(true);
                foreach (var mf in mfs) {
                    if (!mf.sharedMesh || !mf.gameObject.activeInHierarchy) continue;
                    int vOff = verts.Count;
                    var m = mf.sharedMesh;
                    foreach (var v in m.vertices) {
                        Vector3 wv = gRot * mf.transform.TransformPoint(v);
                        verts.Add(new Vector3(wv.x, wv.y, -wv.z));
                    }
                    for (int i = 0; i < m.triangles.Length; i += 3) {
                        tris.Add(m.triangles[i] + vOff);
                        tris.Add(m.triangles[i + 2] + vOff);
                        tris.Add(m.triangles[i + 1] + vOff);
                    }
                }
            } else {
                // Analytical logic with deduplication
                var allAnchors = rooms.SelectMany(rm => rm.Anchors).GroupBy(a => a.name).Select(g => g.First()).ToList();
                foreach (var a in allAnchors) {
                    string label = a.Label.ToString().ToUpper();
                    if (label.Contains("CEILING") || label.Contains("ROOF") || !a.PlaneRect.HasValue) continue;
                    
                    Vector3 s = label.Contains("FLOOR") ? new Vector3(a.PlaneRect.Value.width, a.PlaneRect.Value.height, 0.1f) : 
                               (label.Contains("DOOR") ? new Vector3(a.PlaneRect.Value.width, a.PlaneRect.Value.height, 0.10f) : 
                               (label.Contains("WINDOW") ? new Vector3(a.PlaneRect.Value.width, a.PlaneRect.Value.height, 0.12f) : 
                                new Vector3(a.PlaneRect.Value.width, a.PlaneRect.Value.height, 0.25f)));

                    int vOff = verts.Count;
                    Vector3[] bv = { new Vector3(-0.5f,-0.5f,0.5f), new Vector3(0.5f,-0.5f,0.5f), new Vector3(0.5f,0.5f,0.5f), new Vector3(-0.5f,0.5f,0.5f), new Vector3(-0.5f,-0.5f,-0.5f), new Vector3(0.5f,-0.5f,-0.5f), new Vector3(0.5f,0.5f,-0.5f), new Vector3(-0.5f,-0.5f,-0.5f) };
                    int[] bt = { 0,1,2, 0,2,3, 5,4,7, 5,7,6, 4,0,3, 4,3,7, 1,5,6, 1,6,2, 3,2,6, 3,6,7, 4,5,1, 4,1,0 };

                    for (int i = 0; i < 8; i++) {
                        Vector3 v = gRot * a.transform.TransformPoint(Vector3.Scale(bv[i], s));
                        verts.Add(new Vector3(v.x, v.y, -v.z));
                    }
                    for (int i = 0; i < bt.Length; i += 3) {
                        tris.Add(bt[i] + vOff);
                        tris.Add(bt[i + 2] + vOff);
                        tris.Add(bt[i + 1] + vOff);
                    }
                }
            }
        }

        if (verts.Count == 0) return;

        StringBuilder json = new StringBuilder();
        json.Append("{\"asset\":{\"version\":\"2.0\"},\"scenes\":[{\"nodes\":[0]}],\"scene\":0,\"nodes\":[{\"mesh\":0}],\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}],");
        
        byte[] vData = new byte[verts.Count * 12];
        for (int i = 0; i < verts.Count; i++) {
            Buffer.BlockCopy(BitConverter.GetBytes(verts[i].x), 0, vData, i * 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(verts[i].y), 0, vData, i * 12 + 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(verts[i].z), 0, vData, i * 12 + 8, 4);
        }
        byte[] iData = new byte[tris.Count * 4];
        for (int i = 0; i < tris.Count; i++) Buffer.BlockCopy(BitConverter.GetBytes(tris[i]), 0, iData, i * 4, 4);

        byte[] allData = new byte[vData.Length + iData.Length];
        Buffer.BlockCopy(vData, 0, allData, 0, vData.Length);
        Buffer.BlockCopy(iData, 0, allData, vData.Length, iData.Length);

        string b64 = Convert.ToBase64String(allData);
        json.Append("\"buffers\":[{\"byteLength\":" + allData.Length + ",\"uri\":\"data:application/octet-stream;base64," + b64 + "\"}],");
        json.Append("\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":" + vData.Length + ",\"target\":34962},{\"buffer\":0,\"byteOffset\":" + vData.Length + ",\"byteLength\":" + iData.Length + ",\"target\":34963}],");
        json.Append("\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":" + verts.Count + ",\"type\":\"VEC3\"},{\"bufferView\":1,\"componentType\":5125,\"count\":" + tris.Count + ",\"type\":\"SCALAR\"}]}");

        File.WriteAllText(path, json.ToString());
    }
}
