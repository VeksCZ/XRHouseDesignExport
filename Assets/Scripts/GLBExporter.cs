using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// Writes an XRHouseModel as a binary glTF 2.0 (.glb): one node + mesh per room, one primitive per part, one
/// material per distinct part colour. Kept strictly spec-valid (checked with the Khronos glTF validator), because
/// strict viewers such as Windows 3D Viewer refuse files that lenient tools still open:
///   - every accessor gets its own bufferView with the right target (vertex vs. index data), no shared views;
///   - POSITION min/max are computed from the exact float values written to the BIN chunk (and printed
///     round-trip exact), so they always match the data bit for bit;
///   - empty parts (no vertices/triangles) are skipped - a zero-count accessor is invalid - and a room left with
///     nothing to draw becomes a node without a mesh instead of a mesh with no primitives.
/// </summary>
public static class GLBExporter
{
    const int ArrayBuffer = 34962, ElementArrayBuffer = 34963, Float = 5126, UInt = 5125;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string F(float v) => v.ToString("R", Inv);

    static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static byte[] ExportToGLB(XRHouseModel model)
    {
        if (model == null || model.rooms.Count == 0) return null;

        try
        {
            var colors = new List<Color>();
            var bin = new MemoryStream();
            var bw = new BinaryWriter(bin);
            var bufferViews = new List<string>();
            var accessors = new List<string>();
            var meshes = new List<string>();
            var nodes = new List<string>();

            foreach (var room in model.rooms)
            {
                var prims = new List<string>();
                foreach (var part in room.parts)
                {
                    if (part == null || part.vertices.Count == 0 || part.triangles.Count < 3) continue;
                    int vCount = part.vertices.Count;
                    int triCount = part.triangles.Count / 3 * 3;
                    if (part.triangles.Take(triCount).Any(t => t < 0 || t >= vCount)) continue; // corrupt part - skip, don't break the file

                    if (!colors.Contains(part.color)) colors.Add(part.color);
                    int mat = colors.IndexOf(part.color);

                    // Positions (Z mirrored: Unity is left-handed, glTF right-handed).
                    long posOffset = bin.Position;
                    float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                    float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                    foreach (var v in part.vertices)
                    {
                        float x = v.x, y = v.y, z = -v.z;
                        bw.Write(x); bw.Write(y); bw.Write(z);
                        minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
                        maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
                    }
                    bufferViews.Add($"{{\"buffer\":0,\"byteOffset\":{posOffset},\"byteLength\":{vCount * 12},\"target\":{ArrayBuffer}}}");
                    int posAcc = accessors.Count;
                    accessors.Add($"{{\"bufferView\":{bufferViews.Count - 1},\"componentType\":{Float},\"count\":{vCount},\"type\":\"VEC3\"," +
                                  $"\"min\":[{F(minX)},{F(minY)},{F(minZ)}],\"max\":[{F(maxX)},{F(maxY)},{F(maxZ)}]}}");

                    // Indices - winding reversed because of the Z mirror, so faces keep pointing outward.
                    long idxOffset = bin.Position;
                    for (int i = 0; i < triCount; i += 3)
                    {
                        bw.Write((uint)part.triangles[i]);
                        bw.Write((uint)part.triangles[i + 2]);
                        bw.Write((uint)part.triangles[i + 1]);
                    }
                    bufferViews.Add($"{{\"buffer\":0,\"byteOffset\":{idxOffset},\"byteLength\":{triCount * 4},\"target\":{ElementArrayBuffer}}}");
                    int idxAcc = accessors.Count;
                    accessors.Add($"{{\"bufferView\":{bufferViews.Count - 1},\"componentType\":{UInt},\"count\":{triCount},\"type\":\"SCALAR\"}}");

                    prims.Add($"{{\"attributes\":{{\"POSITION\":{posAcc}}},\"indices\":{idxAcc},\"material\":{mat}}}");
                }

                string name = Esc(room.roomName);
                if (prims.Count > 0)
                {
                    meshes.Add($"{{\"name\":\"{name}\",\"primitives\":[{string.Join(",", prims)}]}}");
                    nodes.Add($"{{\"mesh\":{meshes.Count - 1},\"name\":\"{name}\"}}");
                }
                else nodes.Add($"{{\"name\":\"{name}\"}}");
            }
            if (meshes.Count == 0) return null;

            // doubleSided: every wall/door/window is a thin box seen from both sides; glTF defaults to false,
            // which makes strict viewers backface-cull one side (walls look see-through from outside/inside).
            var materials = colors.Select(c =>
                $"{{\"pbrMetallicRoughness\":{{\"baseColorFactor\":[{c.r.ToString("0.###", Inv)},{c.g.ToString("0.###", Inv)},{c.b.ToString("0.###", Inv)},1.0]," +
                "\"metallicFactor\":0.0,\"roughnessFactor\":1.0},\"doubleSided\":true}");

            int binLength = (int)bin.Length;
            int binPad = (4 - binLength % 4) % 4;
            string json =
                "{\"asset\":{\"version\":\"2.0\",\"generator\":\"XRHouseDesignExport\"},\"scene\":0," +
                $"\"scenes\":[{{\"nodes\":[{string.Join(",", Enumerable.Range(0, nodes.Count))}]}}]," +
                $"\"nodes\":[{string.Join(",", nodes)}]," +
                $"\"meshes\":[{string.Join(",", meshes)}]," +
                $"\"materials\":[{string.Join(",", materials)}]," +
                $"\"accessors\":[{string.Join(",", accessors)}]," +
                $"\"bufferViews\":[{string.Join(",", bufferViews)}]," +
                $"\"buffers\":[{{\"byteLength\":{binLength + binPad}}}]}}";

            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            int jsonPad = (4 - jsonBytes.Length % 4) % 4;

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(0x46546C67); // "glTF"
                w.Write(2);
                w.Write(12 + 8 + jsonBytes.Length + jsonPad + 8 + binLength + binPad);

                w.Write(jsonBytes.Length + jsonPad);
                w.Write(0x4E4F534A); // "JSON"
                w.Write(jsonBytes);
                for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);

                w.Write(binLength + binPad);
                w.Write(0x004E4942); // "BIN\0"
                w.Write(bin.GetBuffer(), 0, binLength);
                for (int i = 0; i < binPad; i++) w.Write((byte)0);

                return ms.ToArray();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Structured GLB export failed: {ex.Message}");
            return null;
        }
    }
}
