using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class UltraHouseData {
    public string exportDate;
    public string schemaVersion = "2.1";
    public List<UltraRoom> rooms;
}

[Serializable]
public class UltraRoom {
    public string name;
    public string guid;
    public Vector3Data pos;
    public Vector4Data rot;
    public List<UltraAnchor> anchors;
}

[Serializable]
public class UltraAnchor {
    public string label;
    public List<string> allLabels;
    public Vector3Data pos;
    public Vector4Data rot;
    public OfflineRect rect;
    public Vector3Data volume;
    public List<Vector2Data> points; // For precise floor/wall boundaries
}

[Serializable]
public class Vector2Data {
    public float x, y;
    public Vector2Data() {}
    public Vector2Data(Vector2 v) { x=v.x; y=v.y; }
}

[Serializable]
public class OfflineRect { public float w, h; }

[Serializable]
public class Vector3Data {
    public float x, y, z;
    public Vector3Data() {}
    public Vector3Data(Vector3 v) { x=v.x; y=v.y; z=v.z; }
}

[Serializable]
public class Vector4Data {
    public float x, y, z, w;
    public Vector4Data() {}
    public Vector4Data(Quaternion q) { x=q.x; y=q.y; z=q.z; w=q.w; }
}
