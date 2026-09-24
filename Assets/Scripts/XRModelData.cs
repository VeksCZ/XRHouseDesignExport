using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class XRMeshPart
{
    public string name;
    public List<Vector3> vertices = new List<Vector3>();
    public List<int> triangles = new List<int>();
    public Color color = Color.white;
    public string materialName = "Default";
}

[System.Serializable]
public class XRRoomModel
{
    public string roomName;
    public List<XRMeshPart> parts = new List<XRMeshPart>();
}

/// <summary>A single 3D dimension annotation (wall length, opening width/height, sill height...) for the
/// Dollhouse's "with dimensions" mode. Position/rotation are already in the model's own local space (the same
/// space XRMeshPart vertices are in - see XRModelFactory.CreateBoxPart), so a viewer can parent it directly
/// under the same root as the rest of the model with no further transform.</summary>
[System.Serializable]
public class XRDimensionLabel
{
    public Vector3 position;
    public Quaternion rotation;
    /// <summary>The actual measured span, drawn as a line alongside the number - both ends in the same model
    /// space as 'position'.</summary>
    public Vector3 lineStart, lineEnd;
    public string text;
}

[System.Serializable]
public class XRHouseModel
{
    public List<XRRoomModel> rooms = new List<XRRoomModel>();
    public List<XRDimensionLabel> dimensions = new List<XRDimensionLabel>();
    public Vector3 center;
    public float globalRotation;

    public IEnumerable<XRMeshPart> GetAllParts()
    {
        foreach (var room in rooms)
            foreach (var part in room.parts)
                yield return part;
    }
}