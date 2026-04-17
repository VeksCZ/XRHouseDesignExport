using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

public static class OBJLoader
{
    public static GameObject LoadFromString(string objData)
    {
        if (string.IsNullOrEmpty(objData)) return null;

        var vertices = new List<Vector3>();
        var submeshes = new Dictionary<string, List<int>>();
        string currentMaterial = "Default";
        submeshes[currentMaterial] = new List<int>();

        using (var reader = new StringReader(objData))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("v "))
                {
                    var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 4 && 
                        float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                        float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                        float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    {
                        vertices.Add(new Vector3(x, y, z));
                    }
                }
                else if (line.StartsWith("usemtl "))
                {
                    currentMaterial = line.Substring(7).Trim();
                    if (!submeshes.ContainsKey(currentMaterial)) submeshes[currentMaterial] = new List<int>();
                }
                else if (line.StartsWith("f "))
                {
                    var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 4)
                    {
                        try {
                            submeshes[currentMaterial].Add(int.Parse(p[1].Split('/')[0]) - 1);
                            submeshes[currentMaterial].Add(int.Parse(p[2].Split('/')[0]) - 1);
                            submeshes[currentMaterial].Add(int.Parse(p[3].Split('/')[0]) - 1);
                        } catch {}
                    }
                }
            }
        }

        if (vertices.Count == 0) return null;

        var go = new GameObject("OBJModel");
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        var mesh = new Mesh { vertices = vertices.ToArray() };
        
        var matNames = submeshes.Keys.ToList();
        mesh.subMeshCount = matNames.Count;
        var materials = new Material[matNames.Count];

        for (int i = 0; i < matNames.Count; i++)
        {
            mesh.SetTriangles(submeshes[matNames[i]], i);
            materials[i] = GetMaterial(matNames[i]);
        }

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mf.mesh = mesh;
        mr.materials = materials;
        return go;
    }

    private static Material GetMaterial(string name)
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        var mat = new Material(shader);
        switch (name.ToLower())
        {
            case "wall": mat.color = new Color(0.42f, 0.42f, 0.45f, 1f); break;
            case "floor": mat.color = new Color(0.75f, 0.75f, 0.78f, 1f); break;
            case "door": mat.color = new Color(0.55f, 0.35f, 0.18f, 1f); break;
            case "window": mat.color = new Color(0.25f, 0.60f, 0.92f, 1f); break;
            default: mat.color = Color.white; break;
        }
        return mat;
    }
}
