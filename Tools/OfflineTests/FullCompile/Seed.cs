using System; using System.Collections.Generic; using System.Linq;
namespace UnityEngine {
  public static class Mathf { public const float Rad2Deg = 57.29578f, Deg2Rad = 0.0174532924f;
    public static float Min(float a, float b) => a < b ? a : b; public static float Max(float a, float b) => a > b ? a : b;
    public static float Abs(float a) => Math.Abs(a); public static int CeilToInt(float a) => (int)Math.Ceiling(a); public static int Clamp(int v, int a, int b) => v < a ? a : v > b ? b : v; public static int Abs(int a) => Math.Abs(a); public static int Max(int a, int b) => a > b ? a : b; public static int Min(int a, int b) => a < b ? a : b; public static float Clamp(float v, float a, float b) => v < a ? a : v > b ? b : v;
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
    public static Quaternion Euler(float x, float y, float z) { var qx = AngleAxis(x, Vector3.right); var qy = AngleAxis(y, Vector3.up); var qz = AngleAxis(z, Vector3.forward); return qy * qx * qz; }
    public static Quaternion Euler(Vector3 e) => Euler(e.x, e.y, e.z);
    public static Quaternion LookRotation(Vector3 forward) => identity; public static Quaternion LookRotation(Vector3 forward, Vector3 up) => identity;
    public static Quaternion FromToRotation(Vector3 a, Vector3 b) => identity; public static Quaternion Slerp(Quaternion a, Quaternion b, float t) => a;
    public Vector3 eulerAngles => new Vector3(0, 0, 0);
    public static Quaternion operator *(Quaternion a, Quaternion b) => new Quaternion(
      a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y, a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
      a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x, a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
    public static Vector3 operator *(Quaternion q, Vector3 v) { var u = new Vector3(q.x, q.y, q.z); float s = q.w;
      return u * (2f * Vector3.Dot(u, v)) + v * (s * s - Vector3.Dot(u, u)) + Vector3.Cross(u, v) * (2f * s); } }
  public struct Rect { public float x, y, width, height; public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
    public float xMin => x; public float xMax => x + width; public float yMin => y; public float yMax => y + height; }

}

namespace UnityEngine.Events {
  public delegate void UnityAction();
  public delegate void UnityAction<T0>(T0 a);
  public class UnityEvent { public void AddListener(UnityAction a) {} public void RemoveListener(UnityAction a) {} public void Invoke() {} }
  public class UnityEvent<T0> { public void AddListener(UnityAction<T0> a) {} public void RemoveListener(UnityAction<T0> a) {} public void Invoke(T0 a) {} }
}

public enum FontStyles { Normal = 0, Bold = 1, Italic = 2, Underline = 4 }
namespace UnityEngine { public enum RuntimeInitializeLoadType { AfterSceneLoad, BeforeSceneLoad, AfterAssembliesLoaded, BeforeSplashScreen, SubsystemRegistration } }
namespace Meta.XR.MRUtilityKit { public class MRUKAnchor { [Flags] public enum SceneLabels { FLOOR = 1, CEILING = 2, WALL_FACE = 4, DOOR_FRAME = 8, INNER_WALL_FACE = 16, INVISIBLE_WALL_FACE = 32 } } }
namespace UnityEngine.UI {
  public class VertexHelper {}
  public class Graphic : MonoBehaviour { protected virtual void OnPopulateMesh(VertexHelper vh) {} protected virtual void OnRectTransformDimensionsChange() {} }
  public class MaskableGraphic : Graphic {}
}

namespace UnityEngine {
  public class Object { public string name; public int GetInstanceID() => 0; public static implicit operator bool(Object o) => !ReferenceEquals(o, null);
    public static void Destroy(Object o) {} public static void Destroy(Object o, float t) {} public static void DestroyImmediate(Object o) {}
    public static T Instantiate<T>(T o) where T : Object => o; public static T FindFirstObjectByType<T>() where T : Object => default; public static T FindAnyObjectByType<T>() where T : Object => default;
    public static T[] FindObjectsByType<T>(FindObjectsInactive i, FindObjectsSortMode m) where T : Object => null; public static void DontDestroyOnLoad(Object o) {} }
  public enum FindObjectsInactive { Exclude, Include } public enum FindObjectsSortMode { None, InstanceID }
  public class Component : Object { public Transform transform; public GameObject gameObject;
    public T GetComponent<T>() => default; public T GetComponentInChildren<T>() => default; public T GetComponentInParent<T>() => default; public T[] GetComponentsInChildren<T>() => null; public T[] GetComponentsInChildren<T>(bool inactive) => null;
    public bool TryGetComponent<T>(out T c) { c = default; return false; } }
  public class Behaviour : Component { public bool enabled; }
  public class MonoBehaviour : Behaviour { public Coroutine StartCoroutine(System.Collections.IEnumerator r) => null; public void StopCoroutine(Coroutine c) {} public void StopAllCoroutines() {} public static void print(object o) {} }
  public class Coroutine {}
  public sealed class Transform : Component { public Vector3 position, localPosition, localScale, lossyScale, eulerAngles, forward, right, up; public Quaternion rotation, localRotation; public Transform parent; public int childCount;
    public Vector3 TransformPoint(Vector3 p) => position + rotation * p; public Vector3 InverseTransformPoint(Vector3 p) => Quaternion.Inverse(rotation) * (p - position);
    public Vector3 TransformDirection(Vector3 d) => rotation * d; public Vector3 InverseTransformDirection(Vector3 d) => Quaternion.Inverse(rotation) * d; public Vector3 TransformVector(Vector3 d) => rotation * d;
    public void SetParent(Transform p) {} public void SetParent(Transform p, bool worldPositionStays) {} public void SetPositionAndRotation(Vector3 p, Quaternion r) {} public void SetLocalPositionAndRotation(Vector3 p, Quaternion r) {}
    public Transform GetChild(int i) => null; public Transform Find(string n) => null; public bool IsChildOf(Transform t) => false; public void Rotate(Vector3 e) {} public void RotateAround(Vector3 p, Vector3 a, float ang) {} public void Translate(Vector3 t) {} }
  public class GameObject : Object { public Transform transform; public int layer; public bool activeSelf; public GameObject() {} public GameObject(string n) {} public GameObject(string n, params System.Type[] c) {}
    public void SetActive(bool b) {} public T AddComponent<T>() where T : Component => default; public T GetComponent<T>() => default; public T GetComponentInChildren<T>() => default; public T[] GetComponentsInChildren<T>() => null; public T[] GetComponentsInChildren<T>(bool inactive) => null;
    public bool TryGetComponent<T>(out T c) { c = default; return false; } public static GameObject CreatePrimitive(PrimitiveType t) => null; }
  public enum PrimitiveType { Sphere, Capsule, Cylinder, Cube, Plane, Quad }
  public static class Debug { public static void Log(object o) {} public static void LogWarning(object o) {} public static void LogError(object o) {} public static void LogException(Exception e) {} public static void Log(object o, Object c) {} }
  public class Canvas : Behaviour { }
  public class Camera : Behaviour { public static Camera main; public float nearClipPlane; }
}
