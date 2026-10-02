using UnityEngine;
using System.Collections.Generic;

public static class UnityModelLoader
{
    /// <summary>
    /// Builds GameObjects for a model. Plain mode (Dollhouse/minimap): one flat unlit colour per part, as before.
    /// Shaded mode (walk-through): every triangle gets its own vertices, a palette colour (light-brown floors,
    /// white walls/ceilings) and a baked brightness from its facing, so walls meeting each other, the floor or the
    /// ceiling stay distinguishable - drawn with the vertex-colour shader. classifyByNormal: for scanned meshes
    /// (one "WALL" part holding floor, walls and ceiling together) the colour is picked per triangle from its facing.
    /// </summary>
    public static GameObject LoadToScene(XRHouseModel house, bool shaded = false, bool classifyByNormal = false)
    {
        if (house == null) return null;
        Debug.Log($"[UnityModelLoader] Loading house with {house.rooms.Count} rooms.");
        var root = new GameObject("HouseModel");
        foreach (var room in house.rooms)
        {
            var roomGo = new GameObject(room.roomName);
            roomGo.transform.SetParent(root.transform, false);
            foreach (var part in room.parts)
            {
                var partGo = new GameObject(part.name);
                partGo.transform.SetParent(roomGo.transform, false);
                var filter = partGo.AddComponent<MeshFilter>();
                var renderer = partGo.AddComponent<MeshRenderer>();

                Mesh mesh = shaded ? BuildShadedMesh(part, classifyByNormal) : BuildPlainMesh(part);
                filter.mesh = mesh;

                var mat = shaded ? GetShadedMaterial(part.materialName) : GetMaterial(part.materialName, part.color);
                renderer.material = mat;
            }
        }
        return root;
    }

    static Mesh BuildPlainMesh(XRMeshPart part)
    {
        var mesh = new Mesh { name = part.name, indexFormat = part.vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
        mesh.SetVertices(part.vertices);
        mesh.SetTriangles(part.triangles, 0);
        mesh.RecalculateNormals();
        return mesh;
    }

    // ---------- shaded (walk-through) ----------

    public static readonly Color FloorColor = new Color(0.80f, 0.66f, 0.50f);
    public static readonly Color WallColor = new Color(0.96f, 0.96f, 0.95f);
    public static readonly Color CeilingColor = new Color(0.98f, 0.98f, 0.97f);
    static readonly Vector3 LightDir = new Vector3(0.94f, 0f, 0.34f); // walls facing X vs Z get clearly different shades

    static Color Palette(string material, Color partColor)
    {
        string m = (material ?? "").ToUpperInvariant();
        if (m == "FLOOR") return FloorColor;
        if (m == "WALL") return WallColor;
        if (m == "CEILING") return CeilingColor;
        return partColor; // doors, windows, stairs, leaves keep their own colour
    }

    /// <summary>Baked "lighting": tops full, undersides a bit darker, walls by which way they face.</summary>
    static float Shade(Vector3 n)
    {
        if (n.y > 0.7f) return 1f;
        if (n.y < -0.7f) return 0.9f;
        Vector3 h = new Vector3(n.x, 0f, n.z);
        if (h.sqrMagnitude < 1e-6f) return 0.85f;
        return 0.78f + 0.17f * Mathf.Abs(Vector3.Dot(h.normalized, LightDir));
    }

    static Mesh BuildShadedMesh(XRMeshPart part, bool classifyByNormal)
    {
        var src = part.vertices;
        var tris = part.triangles;
        int n = tris.Count;
        var verts = new List<Vector3>(n);
        var colors = new List<Color>(n);
        var idx = new List<int>(n);
        Color baseColor = Palette(part.materialName, part.color);
        bool classify = classifyByNormal && (part.materialName ?? "").ToUpperInvariant() == "WALL";
        for (int i = 0; i + 2 < n; i += 3)
        {
            Vector3 a = src[tris[i]], b = src[tris[i + 1]], c = src[tris[i + 2]];
            Vector3 nrm = Vector3.Cross(b - a, c - a);
            nrm = nrm.sqrMagnitude > 1e-12f ? nrm.normalized : Vector3.up;
            Color col = baseColor;
            if (classify) col = nrm.y > 0.7f ? FloorColor : nrm.y < -0.7f ? CeilingColor : WallColor;
            float s = Shade(nrm);
            col = new Color(col.r * s, col.g * s, col.b * s, 1f);
            int k = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c);
            colors.Add(col); colors.Add(col); colors.Add(col);
            idx.Add(k); idx.Add(k + 1); idx.Add(k + 2);
        }
        var mesh = new Mesh { name = part.name, indexFormat = verts.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
        mesh.SetVertices(verts);
        mesh.SetColors(colors);
        mesh.SetTriangles(idx, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    static Material GetShadedMaterial(string name)
    {
        var shader = Shader.Find("XRHouse/VertexColorUnlit");
        if (shader == null)
        {
            Debug.LogError("[UnityModelLoader] XRHouse/VertexColorUnlit shader missing - falling back to flat colours.");
            return GetMaterial(name, Color.white);
        }
        // Material colour stays white - the colour is in the vertices (the walk-through's see-through doors rely
        // on that too: Sprites/Default also multiplies by vertex colour).
        return new Material(shader) { name = name, color = Color.white };
    }

    private static Material GetMaterial(string name, Color color)
    {
        // For URP, "Universal Render Pipeline/Unlit" is standard. 
        // Let's also try "Universal Render Pipeline/Lit" as a fallback if Unlit is missing.
        var shader = Shader.Find("Universal Render Pipeline/Unlit") 
                  ?? Shader.Find("Universal Render Pipeline/Lit")
                  ?? Shader.Find("Unlit/Color")
                  ?? Shader.Find("Standard");
                  
        if (shader == null) Debug.LogError("[UnityModelLoader] Could not find any suitable shader!");

        var mat = new Material(shader) { name = name, color = color };
        // Every part here is a thin box (wall, door, window...) that gets walked around and looked at from
        // both sides - single-sided (the shader's own default) made a wall render solid from whichever side
        // happens to face its winding and invisible from the other, and a mesh-mode door/window box only
        // showed up looking in from outside the room, not from inside it.
        if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        return mat;
    }
}
