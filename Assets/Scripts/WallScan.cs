using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// The "3D scan" of the bare house as textures on its scanned planes: every 3D-scan frame (WallPhotoCapture dense
/// mode) is projected onto the walls, floor and ceilings of its room and blended in (WallScanSplat - the best views
/// win). Seen in passthrough, the walk-through and the Dollhouse (WallOverlays, layer "Wall scan"); what no frame has
/// seen stays a hole. While scanning, every new frame is painted in at once, so you see where to look next.
/// Each plane's texture is cached as a PNG (+ which frames it holds) next to the photos - WallPhotos/WallScan on the
/// headset, Exports/RoomData/WallScan on the PC - and only new frames are added; a deleted frame rebuilds that plane.
/// </summary>
public static class WallScan
{
    public const string Folder = "WallScan";
    const float PxPerMeter = 220f;   // ~4.5 mm per texel (the passthrough camera resolves ~2 mm at 2 m)
    const int MaxSize = 2048;
    const float LoadWeight = 1f;     // a saved texel counts like one good straight-on view

    public class Plane
    {
        public string id, roomUuid;
        public Vector3 origin, axisU, axisV, normal;
        public int pxW, pxH;
        public Material show, cover;
        public HashSet<string> frames = new HashSet<string>();
        public bool dirty;

        RenderTexture accTex;
        /// <summary>The accumulation texture, allocated on first use: a house of 45 rooms has ~300 planes and most of them
        /// never see a frame (each is up to several MB at half-float with mips).</summary>
        public RenderTexture acc => accTex ? accTex : accTex = Allocate(this);
        public bool Allocated => accTex != null;
        /// <summary>Something is painted on it - worth drawing.</summary>
        public bool HasData => accTex != null && frames.Count > 0;
        public void Release() { if (accTex) { accTex.Release(); UnityEngine.Object.Destroy(accTex); } accTex = null; }
    }

    /// <summary>Mips (the Show shader reads them for distance and to fill pinholes) are built by hand after painting,
    /// anisotropic filtering keeps walls seen at a grazing angle sharp.</summary>
    static RenderTexture Allocate(Plane p)
    {
        var rt = new RenderTexture(p.pxW, p.pxH, 0, RenderTextureFormat.ARGBHalf)
        {
            name = "WallScan_" + p.id.Substring(0, 8), wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear,
            useMipMap = true, autoGenerateMips = false, anisoLevel = 4,
        };
        rt.Create();
        var prev = RenderTexture.active; RenderTexture.active = rt; GL.Clear(false, true, Color.clear); RenderTexture.active = prev;
        return rt;
    }

    [Serializable] class ManifestEntry { public string id; public List<string> frames = new List<string>(); }
    /// <summary>Bumped when the way frames are painted changes - older caches are rebuilt.</summary>
    const int CacheVersion = 3; // 2: occlusion by the room's other planes; 3: denser textures (4.5 mm per texel)
    [Serializable] class Manifest { public int version; public List<ManifestEntry> planes = new List<ManifestEntry>(); }

    static readonly Dictionary<string, Plane> planes = new Dictionary<string, Plane>();
    static Material splat;
    static int builtFor = -1;
    static Texture2D frameTex;

    /// <summary>Bumped when the set of planes changes (views rebuild their quads).</summary>
    public static int Version { get; private set; }
    /// <summary>Progress of the background build, e.g. "Wall scan: 120 / 480 frames" (empty when idle).</summary>
    public static string Status { get; private set; } = "";
    public static bool Building { get; private set; }
    public static IEnumerable<Plane> Planes => planes.Values;

    public static string Root => MRUKPathUtility.IsDesktop
        ? Path.Combine(Path.GetFullPath(MRUKPathUtility.GetExportRoot()), Folder)
        : Path.Combine(Application.persistentDataPath, WallPhotoCapture.RootFolder, Folder);

    static Material SplatMat
    {
        get
        {
            if (!splat)
            {
                var sh = Shader.Find("Hidden/XRHouse/WallScanSplat");
                if (sh == null) { Debug.LogError("[WallScan] shader Hidden/XRHouse/WallScanSplat missing from the build"); return null; }
                splat = new Material(sh) { name = "WallScanSplat" };
            }
            return splat;
        }
    }

    /// <summary>Every 3D-scan frame on this device (all sessions), oldest first: key "Photos_x/Dense/0001".</summary>
    public static List<(string key, string jpg, string json)> ListFrames()
    {
        var res = new List<(string, string, string)>();
        try
        {
            string root = MRUKPathUtility.IsDesktop
                ? Path.GetFullPath(MRUKPathUtility.GetExportRoot())
                : Path.Combine(Application.persistentDataPath, WallPhotoCapture.RootFolder);
            if (!Directory.Exists(root)) return res;
            foreach (var dir in Directory.GetDirectories(root, WallPhotoCapture.SessionPrefix + "*").OrderBy(d => d, StringComparer.Ordinal))
            {
                string dd = Path.Combine(dir, WallPhotoCapture.DenseFolder);
                if (!Directory.Exists(dd)) continue;
                foreach (var json in Directory.GetFiles(dd, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    string jpg = Path.ChangeExtension(json, ".jpg");
                    if (!File.Exists(jpg)) continue;
                    res.Add((Key(Path.GetFileName(dir), Path.GetFileNameWithoutExtension(json)), jpg, json));
                }
            }
        }
        catch (Exception e) { Debug.LogWarning($"[WallScan] listing frames: {e.Message}"); }
        return res;
    }

    public static string Key(string session, string index) => $"{session}/{WallPhotoCapture.DenseFolder}/{index}";

#if META_XR_SDK_INSTALLED
    const MRUKAnchor.SceneLabels Surfaces = MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.CEILING;

    static List<MRUKRoom> validRooms = new List<MRUKRoom>();
    static int validFor = -1;
    /// <summary>MRUKDataProcessor.GetValidRooms of the loaded scan, worked out once per load - it re-extracts every
    /// room's plan and costs a few hundred ms, far too much for every frame.</summary>
    public static List<MRUKRoom> Rooms()
    {
        var m = MRUK.Instance;
        int key = MRUKExporter.SceneVersion * 1000 + (m != null ? m.Rooms.Count : 0);
        if (key != validFor) { validRooms = m != null ? MRUKDataProcessor.GetValidRooms(m) : new List<MRUKRoom>(); validFor = key; }
        return validRooms;
    }

    /// <summary>The planes of the loaded scan (rebuilt when another scan is loaded), with their cached textures.</summary>
    public static void EnsurePlanes(List<MRUKRoom> rooms)
    {
        if (builtFor == MRUKExporter.SceneVersion && planes.Count > 0) return;
        SaveAll();
        Clear();
        builtFor = MRUKExporter.SceneVersion;
        var manifest = ReadManifest();
        foreach (var room in rooms)
        {
            if (room == null) continue;
            Vector3 inside = room.GetRoomBounds().center;
            string rid = DoorCatalog.RoomId(room);
            foreach (var a in room.Anchors)
            {
                if (a == null || !a.PlaneRect.HasValue || !a.HasAnyLabel(Surfaces)) continue;
                var r = a.PlaneRect.Value;
                if (r.width < 0.2f || r.height < 0.2f) continue;
                var t = a.transform;
                var p = new Plane
                {
                    id = a.Anchor.Uuid.ToString("N"), roomUuid = rid,
                    origin = t.TransformPoint(new Vector3(r.xMin, r.yMin, 0)),
                    axisU = t.TransformVector(new Vector3(r.width, 0, 0)),
                    axisV = t.TransformVector(new Vector3(0, r.height, 0)),
                };
                Vector3 n = t.forward;
                Vector3 c = p.origin + (p.axisU + p.axisV) / 2f;
                // walls: MRUK's wall anchors face into their room (the room's centre lies outside a concave room's
                // nooks, so it can't decide); floors/ceilings: towards the room's middle height
                p.normal = a.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE) ? n : (Vector3.Dot(inside - c, n) >= 0 ? n : -n);
                if (planes.ContainsKey(p.id)) continue;
                p.pxW = Mathf.Clamp(Mathf.CeilToInt(r.width * PxPerMeter), 16, MaxSize);
                p.pxH = Mathf.Clamp(Mathf.CeilToInt(r.height * PxPerMeter), 16, MaxSize);
                LoadCached(p, manifest);   // allocates the texture only for a plane that has saved frames
                planes[p.id] = p;
            }
        }
        if (LogOcclusion)
            foreach (var p in planes.Values)
                Debug.Log($"[WallScan] plane {p.id.Substring(0, 6)} room {p.roomUuid.Substring(0, 8)} o {p.origin} u {p.axisU} v {p.axisV} n {p.normal}");
        Version++;
        Debug.Log($"[WallScan] {planes.Count} plane(s) for scene {builtFor}, {planes.Values.Sum(x => x.frames.Count)} frame-plane(s) from the cache");
    }

    static void LoadCached(Plane p, Manifest manifest)
    {
        var entry = manifest.planes.FirstOrDefault(e => e.id == p.id);
        string png = Path.Combine(Root, p.id + ".png");
        if (entry == null || !File.Exists(png) || SplatMat == null) return;
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!tex.LoadImage(File.ReadAllBytes(png))) return;
            SplatMat.SetFloat("_LoadWeight", LoadWeight);
            Graphics.Blit(tex, p.acc, SplatMat, 1);
            p.acc.GenerateMips();
            foreach (var f in entry.frames) p.frames.Add(f);
        }
        catch (Exception e) { Debug.LogWarning($"[WallScan] {png}: {e.Message}"); }
        finally { UnityEngine.Object.Destroy(tex); }
    }

    /// <summary>Paints one frame (its texture + sidecar) onto the planes of its room. True if anything was hit.</summary>
    public static bool Splat(Texture photo, WallPhotoMeta m, List<MRUKRoom> rooms, string key)
    {
        if (photo == null || m == null || m.fu <= 0 || SplatMat == null) return false;
        var room = rooms.FirstOrDefault(x => x != null && DoorCatalog.RoomId(x) == m.roomUuid);
        if (room == null) return false;
        Vector3 pos = room.transform.TransformPoint(m.localPosition);
        Quaternion rot = room.transform.rotation * m.localRotation;
        var worldToCam = Matrix4x4.TRS(pos, rot, Vector3.one).inverse;
        var mat = SplatMat;
        mat.SetMatrix("_WorldToCam", worldToCam);
        mat.SetVector("_CamPos", pos);
        mat.SetVector("_Intr", new Vector4(m.fu, m.fv, m.cu, m.cv));
        mat.SetFloat("_Sharp", Mathf.Clamp(m.relativeSharpness > 0 ? m.relativeSharpness : 1f, 0.5f, 1.3f));
        Vector3 fwd = rot * Vector3.forward;
        Func<Vector3, Vector3> toCam = wp => worldToCam.MultiplyPoint3x4(wp);   // once per frame, not per plane
        bool any = false;
        var roomPlanes = planes.Values.Where(x => x.roomUuid == m.roomUuid).ToList();
        foreach (var p in roomPlanes)
        {
            if (key != null && p.frames.Contains(key)) continue;
            // facing the camera at all, and in front of it
            Vector3 c = p.origin + (p.axisU + p.axisV) / 2f;
            if (Vector3.Dot(pos - c, p.normal) <= 0.05f) continue;
            float half = (p.axisU.magnitude + p.axisV.magnitude) / 2f;
            if (Vector3.Dot(c - pos, fwd) < -half) continue;
            // the frame has to look at some part of the plane at all (most planes of a room are out of view)
            if (!FrameCulling.SeesPlane(toCam, m.fu, m.fv, m.cu, m.cv, p.origin, p.axisU, p.axisV)) continue;
            mat.SetVector("_Origin", p.origin); mat.SetVector("_AxisU", p.axisU); mat.SetVector("_AxisV", p.axisV); mat.SetVector("_Normal", p.normal);
            // the room's other planes hide what is behind them
            int k = 0;
            foreach (var o in roomPlanes)
            {
                if (o == p || k >= MaxOccluders) continue;
                occO[k] = o.origin; occU[k] = o.axisU; occV[k] = o.axisV; k++;
            }
            if (LogOcclusion && logged < 40)
            {
                // the same test as the shader, for the plane's centre (tests)
                Vector3 d = c - pos;
                foreach (var o in roomPlanes)
                {
                    if (o == p) continue;
                    Vector3 nn = Vector3.Cross(o.axisU, o.axisV); float den = Vector3.Dot(nn, d);
                    if (Mathf.Abs(den) < 1e-6f) continue;
                    float tt = Vector3.Dot(nn, o.origin - pos) / den;
                    if (tt <= 0.01f || tt >= 0.985f) continue;
                    Vector3 x = pos + tt * d - o.origin;
                    float a = Vector3.Dot(x, o.axisU) / o.axisU.sqrMagnitude, b = Vector3.Dot(x, o.axisV) / o.axisV.sqrMagnitude;
                    if (a > 0.01f && a < 0.99f && b > 0.01f && b < 0.99f)
                    { Debug.Log($"[WallScan] occl: frame {key} plane {p.id.Substring(0, 6)} (n {p.normal}) hidden by {o.id.Substring(0, 6)} (n {o.normal}, {o.axisU.magnitude:0.00}x{o.axisV.magnitude:0.00}) t {tt:0.00} cam {pos}"); logged++; break; }
                }
            }
            mat.SetVectorArray("_OccO", occO); mat.SetVectorArray("_OccU", occU); mat.SetVectorArray("_OccV", occV);
            mat.SetInteger("_OccCount", k);
            bool fresh = !p.Allocated;
            Graphics.Blit(photo, p.acc, mat, 0);
            p.acc.GenerateMips();
            if (fresh) { versionDirty = true; if (!Building) BumpVersion(); }   // a plane that had nothing now has a texture - the views add its quad
            if (key != null) p.frames.Add(key);
            p.dirty = true;
            any = true;
        }
        return any;
    }

    static bool versionDirty;
    static float lastBump;
    /// <summary>Tells the views to rebuild their quads. While the background build runs it is at most once every 1.5 s:
    /// every rebuild redoes all overlays, and a new plane gets its first frame every few frames.</summary>
    static void BumpVersion() { Version++; versionDirty = false; lastBump = Time.realtimeSinceStartup; }

    const int MaxOccluders = 24;
    /// <summary>Tests: log which plane hides another from a frame.</summary>
    public static bool LogOcclusion; static int logged;
    static readonly Vector4[] occO = new Vector4[MaxOccluders], occU = new Vector4[MaxOccluders], occV = new Vector4[MaxOccluders];

    /// <summary>Adds every frame on this device the loaded planes don't hold yet - one per Unity frame (JPG decode).
    /// A frame a plane holds that is gone from the disk (Undo photo) rebuilds that plane from scratch.</summary>
    public static IEnumerator BuildMissing(Func<List<MRUKRoom>> rooms)
    {
        if (Building) yield break;
        Building = true;
        try
        {
            var frames = ListFrames();
            var onDisk = new HashSet<string>(frames.Select(f => f.key));
            foreach (var p in planes.Values.Where(p => p.frames.Any(k => !onDisk.Contains(k))).ToList())
            {
                var prev = RenderTexture.active; RenderTexture.active = p.acc; GL.Clear(false, true, Color.clear); RenderTexture.active = prev;
                p.acc.GenerateMips();
                p.frames.Clear(); p.dirty = true;
            }
            var roomIds = new HashSet<string>(planes.Values.Select(p => p.roomUuid));
            var todo = new List<(string key, string jpg, WallPhotoMeta meta)>();
            foreach (var f in frames)
            {
                WallPhotoMeta m;
                try { m = JsonUtility.FromJson<WallPhotoMeta>(File.ReadAllText(f.json)); } catch { continue; }
                if (m == null || !roomIds.Contains(m.roomUuid)) continue;
                if (planes.Values.All(p => p.roomUuid != m.roomUuid || p.frames.Contains(f.key))) continue;
                todo.Add((f.key, f.jpg, m));
            }
            Debug.Log($"[WallScan] {frames.Count} frame(s) on this device, {todo.Count} to add to {planes.Count} plane(s) of {roomIds.Count} room(s)");
            if (todo.Count == 0) { Status = ""; yield break; }
            int scene = builtFor, done = 0;
            if (!frameTex) frameTex = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "WallScanFrame" };
            foreach (var t in todo)
            {
                if (builtFor != scene) { Status = ""; yield break; } // another scan was loaded meanwhile
                Status = $"Wall scan: {done} / {todo.Count} frames";
                try { if (frameTex.LoadImage(File.ReadAllBytes(t.jpg), false)) Splat(frameTex, t.meta, rooms(), t.key); }
                catch (Exception e) { Debug.LogWarning($"[WallScan] {t.jpg}: {e.Message}"); }
                done++;
                if (done % 60 == 0) SaveAll();
                if (versionDirty && Time.realtimeSinceStartup - lastBump > 1.5f) BumpVersion();
                yield return null;
            }
            if (versionDirty) BumpVersion();
            SaveAll();
            Debug.Log($"[WallScan] added {done} frame(s)");
            Status = "";
        }
        finally { Building = false; }
    }
#endif

    /// <summary>The planes that hold photos, as textured quads in model space (yaw / centre = the exported model's frame) for
    /// PhotoModelGlb: each plane's saved PNG as it is (colour + coverage in alpha). Planes without photos are left out.</summary>
    public static List<PhotoQuad> ExportQuads(List<MRUKRoom> rooms, float yaw, Vector3 center)
    {
        var res = new List<PhotoQuad>();
        EnsurePlanes(rooms);
        SaveAll();   // what is only in memory goes to disk first
        var manifest = ReadManifest();
        Quaternion g = Quaternion.Euler(0, yaw, 0);
        Vector3 M(Vector3 w) => g * (w - center);
        foreach (var p in planes.Values)
        {
            string png = Path.Combine(Root, p.id + ".png");
            if (!File.Exists(png) || manifest.planes.All(e => e.id != p.id)) continue;
            res.Add(new PhotoQuad
            {
                name = $"Wall scan {res.Count + 1} (room {p.roomUuid.Substring(0, Mathf.Min(8, p.roomUuid.Length))})",
                p0 = M(p.origin), p1 = M(p.origin + p.axisU), p2 = M(p.origin + p.axisU + p.axisV), p3 = M(p.origin + p.axisV),
                png = File.ReadAllBytes(png),
            });
        }
        return res;
    }

    /// <summary>Copies the saved texture of every plane that has photos into dir/folder (for the wall drawings) and returns
    /// plane id (the wall anchor's UUID, "N" format) -> path relative to dir.</summary>
    public static Dictionary<string, string> CopyPhotosTo(string dir, string folder)
    {
        var res = new Dictionary<string, string>();
        try
        {
            SaveAll();   // what is only in memory goes to disk first
            var manifest = ReadManifest();
            string target = Path.Combine(dir, folder);
            foreach (var e in manifest.planes)
            {
                string png = Path.Combine(Root, e.id + ".png");
                if (!File.Exists(png)) continue;
                Directory.CreateDirectory(target);
                File.Copy(png, Path.Combine(target, e.id + ".png"), true);
                res[e.id] = folder + "/" + e.id + ".png";
            }
        }
        catch (Exception ex) { Debug.LogWarning($"[WallScan] copying photos for the drawings: {ex.Message}"); }
        return res;
    }

    /// <summary>Writes every changed plane (PNG: colour + coverage) and the manifest.</summary>
    public static void SaveAll()
    {
        var changed = planes.Values.Where(p => p.dirty && p.Allocated).ToList();
        if (changed.Count == 0 || SplatMat == null) return;
        try
        {
            Directory.CreateDirectory(Root);
            var manifest = ReadManifest();
            foreach (var p in changed)
            {
                var rt = RenderTexture.GetTemporary(p.acc.width, p.acc.height, 0, RenderTextureFormat.ARGB32);
                SplatMat.SetFloat("_LoadWeight", LoadWeight);
                Graphics.Blit(p.acc, rt, SplatMat, 2);
                var prev = RenderTexture.active; RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                tex.Apply(false);
                RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                File.WriteAllBytes(Path.Combine(Root, p.id + ".png"), tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
                manifest.planes.RemoveAll(e => e.id == p.id);
                manifest.planes.Add(new ManifestEntry { id = p.id, frames = p.frames.OrderBy(x => x, StringComparer.Ordinal).ToList() });
                p.dirty = false;
            }
            manifest.version = CacheVersion;
            File.WriteAllText(Path.Combine(Root, "manifest.json"), JsonUtility.ToJson(manifest));
        }
        catch (Exception e) { Debug.LogWarning($"[WallScan] save: {e.Message}"); }
    }

    static Manifest ReadManifest()
    {
        try
        {
            string f = Path.Combine(Root, "manifest.json");
            if (File.Exists(f))
            {
                var m = JsonUtility.FromJson<Manifest>(File.ReadAllText(f));
                if (m != null && m.version == CacheVersion) return m;
                Debug.Log("[WallScan] cache from an older version - rebuilding");
            }
        }
        catch (Exception e) { Debug.LogWarning($"[WallScan] manifest: {e.Message}"); }
        return new Manifest();
    }

    static void Clear()
    {
        foreach (var p in planes.Values)
        {
            p.Release();
            if (p.show) UnityEngine.Object.Destroy(p.show);
            if (p.cover) UnityEngine.Object.Destroy(p.cover);
        }
        planes.Clear();
        Version++;
    }

    /// <summary>While scanning over passthrough: the blue veil that fades where frames have seen the plane.</summary>
    public static Material CoverMaterial(Plane p)
    {
        if (!p.cover)
        {
            var sh = Shader.Find("Hidden/XRHouse/WallScanCover");
            if (sh == null) { Debug.LogError("[WallScan] shader Hidden/XRHouse/WallScanCover missing from the build"); return null; }
            p.cover = new Material(sh) { name = "WallScanCover", mainTexture = p.acc };
            p.cover.SetVector("_Size", new Vector4(p.axisU.magnitude, p.axisV.magnitude, 0, 0));
        }
        return p.cover;
    }

    /// <summary>The display material of a plane (shared by every view).</summary>
    public static Material ShowMaterial(Plane p)
    {
        if (!p.show)
        {
            var sh = Shader.Find("Hidden/XRHouse/WallScanShow");
            if (sh == null) { Debug.LogError("[WallScan] shader Hidden/XRHouse/WallScanShow missing from the build"); return null; }
            p.show = new Material(sh) { name = "WallScanShow", mainTexture = p.acc };
            p.show.SetFloat("_MinWeight", 0.004f);
        }
        return p.show;
    }
}
