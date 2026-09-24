using System.Text;
using System.Globalization;
using System.IO;

public static class OBJWriter
{
    public static string WriteToString(XRHouseModel model, string mtlName = null)
    {
        // Must match MRUKPathUtility.MODEL_MTL, the name the .mtl file is actually written under - this
        // constant used to be "01_Materials.mtl" and got renumbered to "00_..." without updating this default,
        // so every exported OBJ's "mtllib" line pointed at a file that didn't exist and viewers silently
        // dropped every material (walls, floor, and the door/window colors that actually carry meaning).
        mtlName ??= MRUKPathUtility.MODEL_MTL;
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("mtllib " + mtlName);
        int vOff = 0;

        foreach (var room in model.rooms)
        {
            sb.AppendLine("\ng " + room.roomName);
            foreach (var part in room.parts)
            {
                sb.AppendLine("usemtl " + part.materialName);
                foreach (var v in part.vertices)
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F6} {1:F6} {2:F6}", v.x, v.y, -v.z));
                
                for (int i = 0; i < part.triangles.Count; i += 3)
                    sb.AppendLine($"f {part.triangles[i] + 1 + vOff} {part.triangles[i + 1] + 1 + vOff} {part.triangles[i + 2] + 1 + vOff}");
                
                vOff += part.vertices.Count;
            }
        }
        return sb.ToString();
    }

    public static string GenerateMTL() => "newmtl WALL\nKd 0.42 0.42 0.45\nnewmtl FLOOR\nKd 0.75 0.75 0.78\nnewmtl DOOR\nKd 0.55 0.35 0.18\nnewmtl WINDOW\nKd 0.25 0.60 0.92";
}