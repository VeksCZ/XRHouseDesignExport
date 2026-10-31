using System; using System.Collections.Generic; using System.Linq;
namespace UnityEngine {
  public static class Mathf { public const float Rad2Deg = 57.29578f, Deg2Rad = 0.0174532924f;
    public static float Min(float a, float b) => a < b ? a : b; public static float Max(float a, float b) => a > b ? a : b;
    public static float Abs(float a) => Math.Abs(a); public static int Abs(int a) => Math.Abs(a); public static int Max(int a, int b) => a > b ? a : b; public static int Min(int a, int b) => a < b ? a : b; public static float Clamp(float v, float a, float b) => v < a ? a : v > b ? b : v;
    public static float Sqrt(float a) => (float)Math.Sqrt(a); public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
    public static float Sin(float a) => (float)Math.Sin(a); public static float Cos(float a) => (float)Math.Cos(a); public static float Acos(float a) => (float)Math.Acos(a); }
  public struct Vector3 { public float x, y, z;
    public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    public static Vector3 zero => new Vector3(0, 0, 0);
    public static Vector3 one => new Vector3(1, 1, 1);
    public static Vector3 up => new Vector3(0, 1, 0);
    public static Vector3 right => new Vector3(1, 0, 0);
    public static Vector3 forward => new Vector3(0, 0, 1);
    public float magnitude => Mathf.Sqrt(x * x + y * y + z * z); public float sqrMagnitude => x * x + y * y + z * z;
    public Vector3 normalized { get { var m = magnitude; return m > 1e-9f ? this / m : zero; } }
    public void Normalize() { this = normalized; }
    public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
    public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
    public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
    public static Vector3 operator *(float d, Vector3 a) => a * d;
    public static Vector3 operator /(Vector3 a, float d) => new Vector3(a.x / d, a.y / d, a.z / d);
    public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
    public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
    public static Vector3 Scale(Vector3 a, Vector3 b) => new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);
    public static Vector3 ProjectOnPlane(Vector3 v, Vector3 n) => v - n * (Dot(v, n) / Dot(n, n));
    public static float Angle(Vector3 a, Vector3 b) { float d = Dot(a.normalized, b.normalized); return Mathf.Acos(Mathf.Clamp(d, -1f, 1f)) * Mathf.Rad2Deg; }
    public static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis) { float u = Angle(from, to); float s = Math.Sign(Dot(axis, Cross(from, to))); return u * (s == 0 ? 1 : s); }
    public override string ToString() => $"({x:0.###}, {y:0.###}, {z:0.###})"; }
  public struct Ray { public Vector3 origin, direction; public Ray(Vector3 o, Vector3 d) { origin = o; direction = d.normalized; } }
  public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x = x; this.y = y; } }
  public struct Quaternion { public float x, y, z, w;
    public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    public static Quaternion identity => new Quaternion(0, 0, 0, 1);
    public static Quaternion AngleAxis(float deg, Vector3 axis) { axis = axis.normalized; float h = deg * Mathf.Deg2Rad / 2f; float s = Mathf.Sin(h); return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(h)); }
    public static Quaternion Inverse(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);
    public static Quaternion operator *(Quaternion a, Quaternion b) => new Quaternion(
      a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y, a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
      a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x, a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
    public static Vector3 operator *(Quaternion q, Vector3 v) { var u = new Vector3(q.x, q.y, q.z); float s = q.w;
      return u * (2f * Vector3.Dot(u, v)) + v * (s * s - Vector3.Dot(u, u)) + Vector3.Cross(u, v) * (2f * s); } }
  public struct Rect { public float x, y, width, height; public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
    public float xMin => x; public float xMax => x + width; public float yMin => y; public float yMax => y + height; }
  public class Transform { public Vector3 position; public Quaternion rotation = Quaternion.identity;
    public Vector3 TransformPoint(Vector3 p) => position + rotation * p; }
  public class Debug { public static void Log(object o) { } }
}
namespace Meta.XR.MRUtilityKit {
  public class MRUKAnchor { public UnityEngine.Transform transform = new UnityEngine.Transform(); public UnityEngine.Rect? PlaneRect; public string Uuid; }
  public class AnchorRef { public Guid Uuid; }
  public class MRUKRoom { public AnchorRef Anchor = new AnchorRef(); public List<MRUKAnchor> WallAnchors = new List<MRUKAnchor>(); }
}
public static class PendingChanges {
  public static (string a, string b) IdForms(System.Guid g) {
    string hex = System.BitConverter.ToString(g.ToByteArray()).Replace("-", "").ToLowerInvariant();
    string native = $"{hex.Substring(0, 8)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}-{hex.Substring(16, 4)}-{hex.Substring(20)}";
    return (g.ToString(), native); } }
public static class MRUKSceneCache { public static string ScanPath(string n) => System.IO.Path.Combine(System.IO.Path.GetTempPath(), n + ".scene.json"); }
