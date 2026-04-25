using UnityEngine;
using System.Collections.Generic;

public static class UnityModelLoader
{
    public static GameObject LoadToScene(XRHouseModel house)
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
                
                var mesh = new Mesh { name = part.name };
                mesh.SetVertices(part.vertices);
                mesh.SetTriangles(part.triangles, 0);
                mesh.RecalculateNormals();
                filter.mesh = mesh;
                
                var mat = GetMaterial(part.materialName, part.color);
                renderer.material = mat;
                // Debug.Log($"[UnityModelLoader] Created part {part.name} with material {mat.name} ({mat.shader.name})");
            }
        }
        return root;
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
        
        return new Material(shader) { name = name, color = color };
    }
}