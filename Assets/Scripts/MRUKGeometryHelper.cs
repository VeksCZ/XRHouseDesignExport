using System.Text;
using System.Globalization;
using UnityEngine;

public static class MRUKGeometryHelper {
    public static void AppendBox(StringBuilder sb, ref int vOff, Vector3 pos, Quaternion rot, Vector3 s, string mat, Vector3 localOffset, float globalRotation = 0) {
        sb.AppendLine("usemtl " + mat);
        Quaternion gRot = Quaternion.Euler(0, globalRotation, 0);
        Vector3[] bv = { new Vector3(-0.5f,-0.5f,0.5f), new Vector3(0.5f,-0.5f,0.5f), new Vector3(0.5f,0.5f,0.5f), new Vector3(-0.5f,0.5f,0.5f), new Vector3(-0.5f,-0.5f,-0.5f), new Vector3(0.5f,-0.5f,-0.5f), new Vector3(0.5f,0.5f,-0.5f), new Vector3(-0.5f,0.5f,-0.5f) };
        int[] bt = { 0,1,2, 0,2,3, 5,4,7, 5,7,6, 4,0,3, 4,3,7, 1,5,6, 1,6,2, 3,2,6, 3,6,7, 4,5,1, 4,1,0 };
        for (int i = 0; i < 8; i++) {
            Vector3 v = gRot * (pos + rot * (Vector3.Scale(bv[i], s) + localOffset));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", v.x, v.y, -v.z));
        }
        sb.AppendLine("vn 0 0 1\nvn 0 0 -1\nvn 0 1 0\nvn 0 -1 0\nvn 1 0 0\nvn -1 0 0");
        for (int i = 0; i < bt.Length; i += 3)
            sb.AppendLine($"f {bt[i] + 1 + vOff} {bt[i + 2] + 1 + vOff} {bt[i + 1] + 1 + vOff}");
        vOff += 8;
    }
}
