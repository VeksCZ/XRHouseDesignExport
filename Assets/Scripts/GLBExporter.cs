using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public static class GLBExporter
{
    public static byte[] ExportToGLB(XRHouseModel model)
    {
        if (model == null || model.rooms.Count == 0) return null;

        try
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                var uniqueColors = new List<Color>();
                int totalVerts = 0;
                int totalTris = 0;

                foreach (var part in model.GetAllParts())
                {
                    totalVerts += part.vertices.Count;
                    totalTris += part.triangles.Count;
                    if (!uniqueColors.Contains(part.color)) uniqueColors.Add(part.color);
                }

                int bufferLength = (totalVerts * 12) + (totalTris * 4);
                string json = BuildJSON(model, uniqueColors, out int finalOffset);
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
                
                foreach (var part in model.GetAllParts())
                {
                    foreach (var v in part.vertices)
                    {
                        bw.Write(v.x);
                        bw.Write(v.y);
                        bw.Write(-v.z); // Mirror Z for GLTF (RH system)
                    }
                    // Reverse winding order because of Z mirror to keep faces outward
                    for (int i = 0; i < part.triangles.Count; i += 3)
                    {
                        bw.Write(part.triangles[i]);
                        bw.Write(part.triangles[i + 2]);
                        bw.Write(part.triangles[i + 1]);
                    }
                }
                
                for (int i = 0; i < binPad; i++) bw.Write((byte)0);

                return ms.ToArray();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Structured GLB export failed: {ex.Message}");
            return null;
        }
    }

    private static string BuildJSON(XRHouseModel model, List<Color> colors, out int totalByteOffset)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"asset\":{\"version\":\"2.0\"},\"scene\":0,");
        
        // Materials
        sb.Append("\"materials\":[");
        for (int i = 0; i < colors.Count; i++)
        {
            Color c = colors[i];
            sb.Append("{\"pbrMetallicRoughness\":{\"baseColorFactor\":[" + 
                c.r.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," + 
                c.g.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," + 
                c.b.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + ",1.0],\"metallicFactor\":0.0,\"roughnessFactor\":1.0}}");
            if (i < colors.Count - 1) sb.Append(",");
        }
        sb.Append("],");

        // Scenes & Nodes
        sb.Append("\"scenes\":[{\"nodes\":[");
        for (int i = 0; i < model.rooms.Count; i++) sb.Append(i + (i == model.rooms.Count - 1 ? "" : ","));
        sb.Append("]}],");

        sb.Append("\"nodes\":[");
        for (int i = 0; i < model.rooms.Count; i++)
        {
            sb.Append("{\"mesh\":" + i + ",\"name\":\"" + model.rooms[i].roomName + "\"}");
            if (i < model.rooms.Count - 1) sb.Append(",");
        }
        sb.Append("],");

        // Meshes
        sb.Append("\"meshes\":[");
        int accIdx = 0;
        for (int i = 0; i < model.rooms.Count; i++)
        {
            var room = model.rooms[i];
            sb.Append("{\"name\":\"" + room.roomName + "\",\"primitives\":[");
            for (int j = 0; j < room.parts.Count; j++)
            {
                int matIdx = colors.IndexOf(room.parts[j].color);
                sb.Append("{\"attributes\":{\"POSITION\":" + (accIdx * 2) + "},\"indices\":" + (accIdx * 2 + 1) + ",\"material\":" + matIdx + "}");
                if (j < room.parts.Count - 1) sb.Append(",");
                accIdx++;
            }
            sb.Append("]}");
            if (i < model.rooms.Count - 1) sb.Append(",");
        }
        sb.Append("],");

        // Accessors
        sb.Append("\"accessors\":[");
        int off = 0;
        bool first = true;
        foreach (var p in model.GetAllParts())
        {
            if (!first) sb.Append(",");
            sb.Append("{\"bufferView\":0,\"byteOffset\":" + off + ",\"componentType\":5126,\"count\":" + p.vertices.Count + ",\"type\":\"VEC3\"},");
            off += p.vertices.Count * 12;
            sb.Append("{\"bufferView\":0,\"byteOffset\":" + off + ",\"componentType\":5125,\"count\":" + p.triangles.Count + ",\"type\":\"SCALAR\"}");
            off += p.triangles.Count * 4;
            first = false;
        }
        totalByteOffset = off;
        sb.Append("],");

        // BufferViews & Buffers
        sb.Append("\"bufferViews\":[{\"buffer\":0,\"byteLength\":" + off + "}],");
        sb.Append("\"buffers\":[{\"byteLength\":" + off + "}]}");

        return sb.ToString();
    }
}
