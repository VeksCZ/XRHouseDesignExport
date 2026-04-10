using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public class SimpleGLBExporter
{
    public struct GLBBox
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 size;
        public Color color;
        public string name;
    }

    public static void SaveBoxesToGLB(List<GLBBox> boxes, string filePath)
    {
        List<byte> binBuffer = new List<byte>();
        
        // Vertices for a standard cube (-0.5 to 0.5)
        Vector3[] cubeVertices = new Vector3[] {
            new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(0.5f, -0.5f,  0.5f), new Vector3(0.5f, 0.5f,  0.5f), new Vector3(-0.5f, 0.5f,  0.5f)
        };
        
        // Indices for 12 triangles
        ushort[] cubeIndices = new ushort[] {
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6,
            0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5
        };

        // For each box, we'll store its transformed vertices
        int totalVertices = boxes.Count * 8;
        int totalIndices = boxes.Count * 36;
        
        // Vertex Buffer (Position: 3 * float)
        int vertexBufferOffset = 0;
        int vertexBufferSize = totalVertices * 3 * 4;
        foreach (var box in boxes)
        {
            foreach (var v in cubeVertices)
            {
                Vector3 worldV = box.position + box.rotation * Vector3.Scale(v, box.size);
                // glTF uses RHS (X, Y, -Z)
                binBuffer.AddRange(BitConverter.GetBytes(worldV.x));
                binBuffer.AddRange(BitConverter.GetBytes(worldV.y));
                binBuffer.AddRange(BitConverter.GetBytes(-worldV.z));
            }
        }

        // Index Buffer (ushort)
        // Align to 4 bytes
        while (binBuffer.Count % 4 != 0) binBuffer.Add(0);
        int indexBufferOffset = binBuffer.Count;
        int indexBufferSize = totalIndices * 2;
        for (int b = 0; b < boxes.Count; b++)
        {
            foreach (var idx in cubeIndices)
            {
                binBuffer.AddRange(BitConverter.GetBytes((ushort)(idx + b * 8)));
            }
        }

        // JSON Header
        StringBuilder json = new StringBuilder();
        json.Append("{");
        json.Append("\"asset\": {\"version\": \"2.0\"},");
        json.Append("\"scene\": 0,");
        json.Append("\"scenes\": [{\"nodes\": [0]}],");
        json.Append("\"nodes\": [{\"mesh\": 0}],");
        json.Append("\"meshes\": [{\"primitives\": [{\"attributes\": {\"POSITION\": 0}, \"indices\": 1}]}],");
        
        json.Append("\"accessors\": [");
        // Accessor 0: Positions
        json.Append("{");
        json.Append("\"bufferView\": 0, \"componentType\": 5126, \"count\": " + totalVertices + ", \"type\": \"VEC3\",");
        // Calculate min/max for glTF compliance
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var box in boxes)
        {
            foreach (var v in cubeVertices)
            {
                Vector3 worldV = box.position + box.rotation * Vector3.Scale(v, box.size);
                min.x = Math.Min(min.x, worldV.x); min.y = Math.Min(min.y, worldV.y); min.z = Math.Min(min.z, -worldV.z);
                max.x = Math.Max(max.x, worldV.x); max.y = Math.Max(max.y, worldV.y); max.z = Math.Max(max.z, -worldV.z);
            }
        }
        json.Append("\"min\": [" + min.x.ToString("F3") + "," + min.y.ToString("F3") + "," + min.z.ToString("F3") + "],");
        json.Append("\"max\": [" + max.x.ToString("F3") + "," + max.y.ToString("F3") + "," + max.z.ToString("F3") + "]");
        json.Append("},");
        // Accessor 1: Indices
        json.Append("{");
        json.Append("\"bufferView\": 1, \"componentType\": 5123, \"count\": " + totalIndices + ", \"type\": \"SCALAR\"");
        json.Append("}");
        json.Append("],");

        json.Append("\"bufferViews\": [");
        // View 0: Vertices
        json.Append("{\"buffer\": 0, \"byteOffset\": " + vertexBufferOffset + ", \"byteLength\": " + vertexBufferSize + ", \"target\": 34962},");
        // View 1: Indices
        json.Append("{\"buffer\": 0, \"byteOffset\": " + indexBufferOffset + ", \"byteLength\": " + indexBufferSize + ", \"target\": 34963}");
        json.Append("],");

        json.Append("\"buffers\": [{\"byteLength\": " + binBuffer.Count + "}]");
        json.Append("}");

        byte[] jsonBytes = Encoding.UTF8.GetBytes(json.ToString());
        int jsonPadding = (4 - (jsonBytes.Length % 4)) % 4;
        int binPadding = (4 - (binBuffer.Count % 4)) % 4;

        using (BinaryWriter writer = new BinaryWriter(File.Open(filePath, FileMode.Create)))
        {
            // GLB Header
            writer.Write(0x46546C67); // magic: "glTF"
            writer.Write(2);          // version
            writer.Write(12 + 8 + jsonBytes.Length + jsonPadding + 8 + binBuffer.Count + binPadding); // total length

            // JSON Chunk
            writer.Write(jsonBytes.Length + jsonPadding);
            writer.Write(0x4E4F534A); // type: "JSON"
            writer.Write(jsonBytes);
            for (int i = 0; i < jsonPadding; i++) writer.Write((byte)0x20); // space padding

            // BIN Chunk
            writer.Write(binBuffer.Count + binPadding);
            writer.Write(0x004E4942); // type: "BIN"
            writer.Write(binBuffer.ToArray());
            for (int i = 0; i < binPadding; i++) writer.Write((byte)0x00); // null padding
        }
    }
}
