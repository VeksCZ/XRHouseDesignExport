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

[System.Serializable]
public class XRHouseModel
{
    public List<XRRoomModel> rooms = new List<XRRoomModel>();
    public Vector3 center;
    public float globalRotation;

    public IEnumerable<XRMeshPart> GetAllParts()
    {
        foreach (var room in rooms)
            foreach (var part in room.parts)
                yield return part;
    }
}