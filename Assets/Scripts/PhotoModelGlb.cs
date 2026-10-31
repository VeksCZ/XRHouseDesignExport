using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>One scanned plane (wall / floor / ceiling) with its 3D-scan texture: the four corners in model space (Unity axes,
/// the same space as XRMeshPart vertices) - origin, origin + U, origin + U + V, origin + V - and the texture as a PNG
/// (colour + coverage in alpha; image row 0 is the top, i.e. v = 1).</summary>
public class PhotoQuad
{
    public string name;
    public Vector3 p0, p1, p2, p3;
    public byte[] png;
}

/// <summary>
/// The wall scan as a textured glTF 2.0 (.glb): one quad per plane with its photo texture embedded as PNG, unlit
/// (KHR_materials_unlit - the photos already carry the light), alpha-masked so what no frame saw stays see-through. Shown next
/// to the clean model in any viewer, it shows the bare walls with their pipes and cables. Kept strictly spec-valid like
/// GLBExporter: own bufferView per accessor, POSITION min/max, 4-byte aligned image views.
/// </summary>
public static class PhotoModelGlb
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

    public static byte[] Write(List<PhotoQuad> quads)
    {
        quads = quads?.Where(q => q != null && q.png != null && q.png.Length > 0).ToList();
        if (quads == null || quads.Count == 0) return null;

        var bin = new MemoryStream();
        var bw = new BinaryWriter(bin);
        void Pad4() { while (bin.Position % 4 != 0) bw.Write((byte)0); }
        var bufferViews = new List<string>(); var accessors = new List<string>();
        var meshes = new List<string>(); var nodes = new List<string>();
        var materials = new List<string>(); var textures = new List<string>(); var images = new List<string>();

        for (int qi = 0; qi < quads.Count; qi++)
        {
            var q = quads[qi];
            // the photo
            Pad4();
            long imgOffset = bin.Position;
            bw.Write(q.png);
            bufferViews.Add($"{{\"buffer\":0,\"byteOffset\":{imgOffset},\"byteLength\":{q.png.Length}}}");
            images.Add($"{{\"bufferView\":{bufferViews.Count - 1},\"mimeType\":\"image/png\"}}");
            textures.Add($"{{\"sampler\":0,\"source\":{images.Count - 1}}}");
            // unlit + alpha-masked: a texel no frame saw has alpha 0; a seen one at least ~0.02
            materials.Add($"{{\"pbrMetallicRoughness\":{{\"baseColorTexture\":{{\"index\":{textures.Count - 1}}},\"metallicFactor\":0.0,\"roughnessFactor\":1.0}}," +
                          "\"alphaMode\":\"MASK\",\"alphaCutoff\":0.02,\"doubleSided\":true,\"extensions\":{\"KHR_materials_unlit\":{}}}");

            // positions (Z mirrored: Unity is left-handed, glTF right-handed)
            var pts = new[] { q.p0, q.p1, q.p2, q.p3 };
            Pad4();
            long posOffset = bin.Position;
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            foreach (var v in pts)
            {
                float x = v.x, y = v.y, z = -v.z;
                bw.Write(x); bw.Write(y); bw.Write(z);
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
            }
            bufferViews.Add($"{{\"buffer\":0,\"byteOffset\":{posOffset},\"byteLength\":48,\"target\":{ArrayBuffer}}}");
            int posAcc = accessors.Count;
            accessors.Add($"{{\"bufferView\":{bufferViews.Count - 1},\"componentType\":{Float},\"count\":4,\"type\":\"VEC3\",\"min\":[{F(minX)},{F(minY)},{F(minZ)}],\"max\":[{F(maxX)},{F(maxY)},{F(maxZ)}]}}");

            // texture coordinates: u along the plane's U axis, v up; glTF's v runs downwards
            long uvOffset = bin.Position;
            foreach (var uv in new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) }) { bw.Write(uv.x); bw.Write(1f - uv.y); }
            bufferViews.Add($"{{\"buffer\":0,\"byteOffset\":{uvOffset},\"byteLength\":32,\"target\":{ArrayBuffer}}}");
            int uvAcc = accessors.Count;
            accessors.Add($"{{\"bufferView\":{bufferViews.Count - 1},\"componentType\":{Float},\"count\":4,\"type\":\"VEC2\"}}");

            // indices - winding reversed because of the Z mirror
            long idxOffset = bin.Position;
            foreach (uint i in new uint[] { 0, 2, 1, 0, 3, 2 }) bw.Write(i);
            bufferViews.Add($"{{\"buffer\":0,\"byteOffset\":{idxOffset},\"byteLength\":24,\"target\":{ElementArrayBuffer}}}");
            int idxAcc = accessors.Count;
            accessors.Add($"{{\"bufferView\":{bufferViews.Count - 1},\"componentType\":{UInt},\"count\":6,\"type\":\"SCALAR\"}}");

            string name = Esc(string.IsNullOrEmpty(q.name) ? "Plane" + qi : q.name);
            meshes.Add($"{{\"name\":\"{name}\",\"primitives\":[{{\"attributes\":{{\"POSITION\":{posAcc},\"TEXCOORD_0\":{uvAcc}}},\"indices\":{idxAcc},\"material\":{qi}}}]}}");
            nodes.Add($"{{\"mesh\":{qi},\"name\":\"{name}\"}}");
        }

        int binLength = (int)bin.Length;
        int binPad = (4 - binLength % 4) % 4;
        string json =
            "{\"asset\":{\"version\":\"2.0\",\"generator\":\"XRHouseDesignExport\"},\"extensionsUsed\":[\"KHR_materials_unlit\"],\"scene\":0," +
            $"\"scenes\":[{{\"nodes\":[{string.Join(",", Enumerable.Range(0, nodes.Count))}]}}]," +
            $"\"nodes\":[{string.Join(",", nodes)}]," +
            $"\"meshes\":[{string.Join(",", meshes)}]," +
            $"\"materials\":[{string.Join(",", materials)}]," +
            "\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":33071,\"wrapT\":33071}]," +
            $"\"textures\":[{string.Join(",", textures)}]," +
            $"\"images\":[{string.Join(",", images)}]," +
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
}
