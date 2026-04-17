using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public class GLBScene
{
    public class SubMesh
    {
        public Vector3[] vertices;
        public int[] triangles;
        public Color color = Color.white;
        public string materialName;
    }

    public class MeshNode
    {
        public string name;
        public List<SubMesh> subMeshes = new List<SubMesh>();
    }

    public List<MeshNode> nodes = new List<MeshNode>();
}

public static class GLBExporter
{
    public static byte[] ExportToGLB(GLBScene scene)
    {
        if (scene == null || scene.nodes.Count == 0) return null;

        try
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                var materials = new List<Color>();
                int totalVerts = 0;
                int totalTris = 0;
                int subMeshCount = 0;

                foreach (var node in scene.nodes)
                {
                    foreach (var sm in node.subMeshes)
                    {
                        totalVerts += sm.vertices.Length;
                        totalTris += sm.triangles.Length;
                        subMeshCount++;
                        if (!materials.Contains(sm.color)) materials.Add(sm.color);
                    }
                }

                int bufferLength = (totalVerts * 12) + (totalTris * 4);
                string json = BuildJSON(scene, materials, out int byteOffset);
                byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
                int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
                int binPad = (4 - (bufferLength % 4)) % 4;

                // Header
                bw.Write(0x46546C67); // magic
                bw.Write(2);          // version
                bw.Write(12 + 8 + jsonBytes.Length + jsonPad + 8 + bufferLength + binPad);

                // JSON Chunk
                bw.Write(jsonBytes.Length + jsonPad);
                bw.Write(0x4E4F534A);
                bw.Write(jsonBytes);
                for (int i = 0; i < jsonPad; i++) bw.Write((byte)0x20);

                // BIN Chunk
                bw.Write(bufferLength + binPad);
                bw.Write(0x004E4942);
                
                foreach (var node in scene.nodes)
                {
                    foreach (var sm in node.subMeshes)
                    {
                        byte[] vBytes = new byte[sm.vertices.Length * 12];
                        Buffer.BlockCopy(sm.vertices, 0, vBytes, 0, vBytes.Length);
                        bw.Write(vBytes);
                        
                        byte[] tBytes = new byte[sm.triangles.Length * 4];
                        Buffer.BlockCopy(sm.triangles, 0, tBytes, 0, tBytes.Length);
                        bw.Write(tBytes);
                    }
                }
                
                for (int i = 0; i < binPad; i++) bw.Write((byte)0);

                return ms.ToArray();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Hierarchical Colored GLB export failed: {ex.Message}");
            return null;
        }
    }

    private static string BuildJSON(GLBScene scene, List<Color> uniqueColors, out int totalByteOffset)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"asset\":{\"version\":\"2.0\"},");
        
        // Materials
        sb.Append("\"materials\":[");
        for (int i = 0; i < uniqueColors.Count; i++)
        {
            Color c = uniqueColors[i];
            sb.Append("{\"pbrMetallicRoughness\":{\"baseColorFactor\":[" + 
                c.r.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," + 
                c.g.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," + 
                c.b.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ",1.0],\"metallicFactor\":0.0,\"roughnessFactor\":0.8}}");
            if (i < uniqueColors.Count - 1) sb.Append(",");
        }
        sb.Append("],");

        // Scenes & Nodes
        sb.Append("\"scenes\":[{\"nodes\":[");
        for (int i = 0; i < scene.nodes.Count; i++) sb.Append(i + (i == scene.nodes.Count - 1 ? "" : ","));
        sb.Append("]}],\"scene\":0,");

        sb.Append("\"nodes\":[");
        for (int i = 0; i < scene.nodes.Count; i++)
        {
            sb.Append("{\"mesh\":" + i + ",\"name\":\"" + scene.nodes[i].name + "\"}");
            if (i < scene.nodes.Count - 1) sb.Append(",");
        }
        sb.Append("],");

        // Meshes
        sb.Append("\"meshes\":[");
        int accessorIdx = 0;
        for (int i = 0; i < scene.nodes.Count; i++)
        {
            var node = scene.nodes[i];
            sb.Append("{\"name\":\"" + node.name + "\",\"primitives\":[");
            for (int j = 0; j < node.subMeshes.Count; j++)
            {
                int matIdx = uniqueColors.IndexOf(node.subMeshes[j].color);
                sb.Append("{\"attributes\":{\"POSITION\":" + (accessorIdx * 2) + "},\"indices\":" + (accessorIdx * 2 + 1) + ",\"material\":" + matIdx + "}");
                if (j < node.subMeshes.Count - 1) sb.Append(",");
                accessorIdx++;
            }
            sb.Append("]}");
            if (i < scene.nodes.Count - 1) sb.Append(",");
        }
        sb.Append("],");

        // Accessors
        sb.Append("\"accessors\":[");
        int byteOffset = 0;
        for (int i = 0; i < scene.nodes.Count; i++)
        {
            foreach (var sm in scene.nodes[i].subMeshes)
            {
                // Vertices
                sb.Append("{\"bufferView\":0,\"byteOffset\":" + byteOffset + ",\"componentType\":5126,\"count\":" + sm.vertices.Length + ",\"type\":\"VEC3\"},");
                byteOffset += sm.vertices.Length * 12;
                // Indices
                sb.Append("{\"bufferView\":0,\"byteOffset\":" + byteOffset + ",\"componentType\":5125,\"count\":" + sm.triangles.Length + ",\"type\":\"SCALAR\"}");
                byteOffset += sm.triangles.Length * 4;
                
                if (accessorIdx > 0) { /* logic for commas handled by global count or similar */ }
                // This is slightly tricky with nested loops, let's just track a global count
            }
        }
        // Correcting the loop for commas
        sb.Clear(); BuildCorrectAccessors(sb, scene, out totalByteOffset);
        string accessorsPart = sb.ToString();

        // Final Assembly
        StringBuilder final = new StringBuilder();
        final.Append("{\"asset\":{\"version\":\"2.0\"},");
        
        final.Append("\"materials\":[");
        for (int i = 0; i < uniqueColors.Count; i++) {
            Color c = uniqueColors[i];
            final.Append("{\"pbrMetallicRoughness\":{\"baseColorFactor\":[" + 
                c.r.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," + 
                c.g.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," + 
                c.b.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ",1.0],\"metallicFactor\":0.0,\"roughnessFactor\":1.0}}");
            if (i < uniqueColors.Count - 1) final.Append(",");
        }
        final.Append("],");

        final.Append("\"scenes\":[{\"nodes\":[");
        for (int i = 0; i < scene.nodes.Count; i++) final.Append(i + (i == scene.nodes.Count - 1 ? "" : ","));
        final.Append("]}],\"scene\":0,");

        final.Append("\"nodes\":[");
        for (int i = 0; i < scene.nodes.Count; i++) {
            final.Append("{\"mesh\":" + i + ",\"name\":\"" + scene.nodes[i].name + "\"}");
            if (i < scene.nodes.Count - 1) final.Append(",");
        }
        final.Append("],");

        final.Append("\"meshes\":[");
        accessorIdx = 0;
        for (int i = 0; i < scene.nodes.Count; i++) {
            final.Append("{\"name\":\"" + scene.nodes[i].name + "\",\"primitives\":[");
            for (int j = 0; j < scene.nodes[i].subMeshes.Count; j++) {
                int matIdx = uniqueColors.IndexOf(scene.nodes[i].subMeshes[j].color);
                final.Append("{\"attributes\":{\"POSITION\":" + (accessorIdx * 2) + "},\"indices\":" + (accessorIdx * 2 + 1) + ",\"material\":" + matIdx + "}");
                if (j < scene.nodes[i].subMeshes.Count - 1) final.Append(",");
                accessorIdx++;
            }
            final.Append("]}");
            if (i < scene.nodes.Count - 1) final.Append(",");
        }
        final.Append("],");

        final.Append("\"accessors\":[" + accessorsPart + "],");
        final.Append("\"bufferViews\":[{\"buffer\":0,\"byteLength\":" + totalByteOffset + "}],");
        final.Append("\"buffers\":[{\"byteLength\":" + totalByteOffset + "}]}");

        return final.ToString();
    }

    private static void BuildCorrectAccessors(StringBuilder sb, GLBScene scene, out int totalOffset)
    {
        int offset = 0;
        bool first = true;
        foreach (var node in scene.nodes)
        {
            foreach (var sm in node.subMeshes)
            {
                if (!first) sb.Append(",");
                sb.Append("{\"bufferView\":0,\"byteOffset\":" + offset + ",\"componentType\":5126,\"count\":" + sm.vertices.Length + ",\"type\":\"VEC3\"},");
                offset += sm.vertices.Length * 12;
                sb.Append("{\"bufferView\":0,\"byteOffset\":" + offset + ",\"componentType\":5125,\"count\":" + sm.triangles.Length + ",\"type\":\"SCALAR\"}");
                offset += sm.triangles.Length * 4;
                first = false;
            }
        }
        totalOffset = offset;
    }

    public static byte[] ExportToGLB(string objData)
    {
        if (string.IsNullOrEmpty(objData)) return null;
        GLBScene scene = new GLBScene();
        GLBScene.MeshNode node = new GLBScene.MeshNode { name = "ImportedOBJ" };
        
        // Basic OBJ to SubMeshes parser
        var verts = new List<Vector3>();
        var currentSub = new GLBScene.SubMesh { materialName = "Default", color = Color.white };
        var tris = new List<int>();
        
        string[] lines = objData.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var l in lines)
        {
            if (l.StartsWith("v ")) {
                var p = l.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4 && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                    verts.Add(new Vector3(x, y, z));
            }
            else if (l.StartsWith("usemtl ")) {
                if (tris.Count > 0) {
                    currentSub.vertices = verts.ToArray();
                    currentSub.triangles = tris.ToArray();
                    node.subMeshes.Add(currentSub);
                    tris = new List<int>();
                }
                string matName = l.Substring(7).Trim();
                currentSub = new GLBScene.SubMesh { materialName = matName, color = GetColorForMaterial(matName) };
            }
            else if (l.StartsWith("f ")) {
                var p = l.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4) {
                    tris.Add(int.Parse(p[1].Split('/')[0]) - 1);
                    tris.Add(int.Parse(p[3].Split('/')[0]) - 1);
                    tris.Add(int.Parse(p[2].Split('/')[0]) - 1);
                }
            }
        }
        if (tris.Count > 0 || verts.Count > 0) {
            currentSub.vertices = verts.ToArray();
            currentSub.triangles = tris.ToArray();
            node.subMeshes.Add(currentSub);
        }
        
        scene.nodes.Add(node);
        return ExportToGLB(scene);
    }

    private static Color GetColorForMaterial(string name)
    {
        switch (name.ToLower())
        {
            case "wall": return new Color(0.42f, 0.42f, 0.45f);
            case "floor": return new Color(0.75f, 0.75f, 0.78f);
            case "door": return new Color(0.55f, 0.35f, 0.18f);
            case "window": return new Color(0.25f, 0.60f, 0.92f);
            default: return Color.white;
        }
    }
}
