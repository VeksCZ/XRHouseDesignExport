using System.Text;
using System.Globalization;
using System.IO;

public static class OBJWriter
{
    public static string WriteToString(XRHouseModel model, string mtlName = "01_Materials.mtl")
    {
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

    public static string GenerateMTL() => "newmtl Wall\nKd 0.42 0.42 0.45\nnewmtl Floor\nKd 0.75 0.75 0.78\nnewmtl Door\nKd 0.55 0.35 0.18\nnewmtl Window\nKd 0.25 0.60 0.92";
}