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
    /// <summary>Direction the measurement faces (into the room), model space - orients the exported number.</summary>
    public Vector3 normal;
    /// <summary>Same outer/inner split as a floor plan's own dimension lines: true for a whole wall's length or
    /// its ceiling height (measured "from outside", drawn green), false for an opening's width/height/sill
    /// (measured "from inside", drawn purple) - see DimensionColors and MRUKReportBuilder's .dimline CSS.</summary>
    public bool outer;
}

/// <summary>The exact green/purple used by a floor plan's own outer/inner dimension lines (MRUKReportBuilder's
/// .dimline.outer and .dimline CSS rules), reused everywhere else a dimension line is drawn - the 3D Dollhouse
/// and the dimensioned export model - so the same convention reads the same way in every view.</summary>
public static class DimensionColors
{
    public static readonly Color Outer = new Color32(0x2f, 0x85, 0x5a, 255);
    public static readonly Color Inner = new Color32(0x80, 0x5a, 0xd5, 255);
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