using System.Text;
using System.Globalization;
using UnityEngine;

public static class MRUKGeometryHelper
{
    public static void AppendBox(StringBuilder sb, ref int vOff, Vector3 pos, Quaternion rot, Vector3 size, string material, Vector3 localOffset, float globalRotation = 0f)
    {
        sb.AppendLine($"usemtl {material}");

        Quaternion gRot = Quaternion.Euler(0, globalRotation, 0);

        // Base cube vertices (centered)
        Vector3[] baseVertices = 
        {
            new Vector3(-0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f,  0.5f,  0.5f),
            new Vector3(-0.5f,  0.5f,  0.5f),
            new Vector3(-0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f,  0.5f, -0.5f)
        };

        int[] baseTriangles = 
        {
            0,1,2, 0,2,3,     // front
            5,4,7, 5,7,6,     // back
            4,0,3, 4,3,7,     // left
            1,5,6, 1,6,2,     // right
            3,2,6, 3,6,7,     // top
            4,5,1, 4,1,0      // bottom
        };

        // Generate vertices
        for (int i = 0; i < 8; i++)
        {
            Vector3 v = gRot * (pos + rot * (Vector3.Scale(baseVertices[i], size) + localOffset));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
        }

        // Normals (simple axis-aligned)
        sb.AppendLine("vn 0 0 1\nvn 0 0 -1\nvn 0 1 0\nvn 0 -1 0\nvn 1 0 0\nvn -1 0 0");

        // Triangles
        for (int i = 0; i < baseTriangles.Length; i += 3)
        {
            sb.AppendLine($"f {baseTriangles[i] + 1 + vOff} {baseTriangles[i + 2] + 1 + vOff} {baseTriangles[i + 1] + 1 + vOff}");
        }

        vOff += 8;
    }
}
