using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Draws what was documented on the bare walls into every view of the house, each switchable on its own:
///  - Routes (RouteStore): cable chases, water/waste/heating pipes as coloured tubes.
///  - Photos on walls: every cable photo (WallPhotoCapture) projected back onto the surfaces it shows - an "X-ray" of
///    the walls as they were before plastering.
/// Views: over passthrough (the live scan, world-aligned), the 1:1 walk-through and the Dollhouse - whichever are
/// on. Everything is built in the scan's world frame and parented under each view's model root through the model
/// mapping (model = R(yaw) * (scan - center)), so it follows the model wherever it is moved or scaled.
/// </summary>
public class WallOverlays : MonoBehaviour
{
    public WalkThroughMode walk;
    public DollHouseVisualizer dollhouse;
    public MRUKExporter exporter;

    const string RoutesPref = "Overlay.Routes", PhotosPref = "Overlay.Photos", FittingsPref = "Overlay.Fittings";

    static int showRoutes = -1, showPhotos = -1, showFittings = -1; // cached: PlayerPrefs is a JNI call on Android, read every frame here

    /// <summary>Over passthrough: the planned fittings (bath, shower, basins... as see-through ghosts) and the niches
    /// (as recesses "behind" the real wall) from the furnished saved scan, on the live scan's matching rooms.</summary>
    public static bool ShowFittings
    {
        get { if (showFittings < 0) showFittings = PlayerPrefs.GetInt(FittingsPref, 0); return showFittings == 1; }
        set { showFittings = value ? 1 : 0; PlayerPrefs.SetInt(FittingsPref, showFittings); PlayerPrefs.Save(); Bump(); }
    }

    /// <summary>Tests (desktop -overlays fittings): the fittings ghost and niche recesses in the walk-through too, to
    /// check they land on the model's own furniture and niches.</summary>
    public static bool FittingsInWalk;

    /// <summary>What the last fittings build found (for the menu log).</summary>
    public static string FittingsInfo { get; private set; } = "";

    /// <summary>Routes drawn in the views (remembered between sessions).</summary>
    public static bool ShowRoutes
    {
        get { if (showRoutes < 0) showRoutes = PlayerPrefs.GetInt(RoutesPref, 1); return showRoutes == 1; }
        set { showRoutes = value ? 1 : 0; PlayerPrefs.SetInt(RoutesPref, showRoutes); PlayerPrefs.Save(); Bump(); }
    }

    /// <summary>Cable photos projected onto the walls (remembered between sessions).</summary>
    public static bool ShowPhotos
    {
        get { if (showPhotos < 0) showPhotos = PlayerPrefs.GetInt(PhotosPref, 0); return showPhotos == 1; }
        set { showPhotos = value ? 1 : 0; PlayerPrefs.SetInt(PhotosPref, showPhotos); PlayerPrefs.Save(); if (value) photosDirty = true; Bump(); }
    }

    /// <summary>Routes shown even when switched off (while the Routes tool is drawing them).</summary>
    public static bool ForceRoutes { get => forceRoutes; set { if (forceRoutes != value) { forceRoutes = value; Bump(); } } }
    static bool forceRoutes;

    /// <summary>Wall photos taken/deleted - reload the photo set next time it is shown.</summary>
    public static void PhotosChanged() { photosDirty = true; Bump(); }
    static bool photosDirty = true;

    static int toggleVersion;
    static void Bump() => toggleVersion++;

    class Ctx
    {
        public string name;
        public GameObject go, routesGo, photosGo;
        public Transform root; public float yaw; public Vector3 center;
        public string stamp;
        public readonly List<Mesh> meshes = new List<Mesh>();
        /// <summary>Photos still waiting for their texture: hidden until it is there (no grey placeholders).</summary>
        public readonly List<(Photo photo, MeshRenderer mr)> waiting = new List<(Photo, MeshRenderer)>();
    }
    readonly Ctx worldCtx = new Ctx { name = "World" }, walkCtx = new Ctx { name = "Walk" }, dollCtx = new Ctx { name = "Dollhouse" };

    Material routeMat;
    string sceneSig = "";
    int sigFrame = -100;

    // ---------- photos ----------
    class Photo { public WallPhotoMeta meta; public string jpg; public Material mat; public Texture tex; }
    readonly List<Photo> photos = new List<Photo>();
    readonly Dictionary<string, Photo> photoByPath = new Dictionary<string, Photo>();
    Coroutine loader;
    int photoVersion;
    const int PhotoTexW = 640, PhotoTexH = 480, PhotoGridX = 12, PhotoGridY = 9;

    void OnDestroy()
    {
        foreach (var c in new[] { worldCtx, walkCtx, dollCtx }) Clear(c);
        foreach (var p in photos) { if (p.mat) Destroy(p.mat); if (p.tex is RenderTexture rt) { rt.Release(); Destroy(rt); } }
        if (routeMat) Destroy(routeMat);
        if (ghostMat) Destroy(ghostMat);
    }

    void LateUpdate()
    {
        bool routes = ShowRoutes || ForceRoutes, pics = ShowPhotos;
        if (pics && photosDirty) RefreshPhotoList();
        if (Time.frameCount - sigFrame >= 20) { sigFrame = Time.frameCount; sceneSig = SceneSignature(); }

        Transform r = null; float y = 0f; Vector3 c = default;
        bool walkOn = walk != null && walk.TryGetModelFrame(out r, out y, out c);
        if (walkOn) Sync(walkCtx, r, y, c, routes, pics, ShowFittings && FittingsInWalk); else Clear(walkCtx);
        if (dollhouse != null && dollhouse.TryGetModelFrame(out r, out y, out c)) Sync(dollCtx, r, y, c, routes, pics); else Clear(dollCtx);
        // Over passthrough: only on a scan lined up with the real house (the live one), and not while inside the model.
        if (exporter != null && exporter.WorldAligned && !walkOn) Sync(worldCtx, null, 0f, Vector3.zero, routes, pics, ShowFittings); else Clear(worldCtx);

        foreach (var ctx in new[] { worldCtx, walkCtx, dollCtx })
            for (int i = ctx.waiting.Count - 1; i >= 0; i--)
            {
                var (ph, mr) = ctx.waiting[i];
                if (ph.tex == null) continue;
                if (mr) { mr.sharedMaterial = PhotoMaterial(ph); mr.enabled = true; }
                ctx.waiting.RemoveAt(i);
            }
    }

    static string SceneSignature()
    {
#if META_XR_SDK_INSTALLED
        var m = MRUK.Instance;
        if (m == null || m.Rooms.Count == 0) return "none";
        var r0 = m.Rooms[0];
        return r0 == null ? "x" : $"{m.Rooms.Count}:{r0.Anchor.Uuid}:{r0.transform.position.GetHashCode()}:{r0.transform.rotation.GetHashCode()}";
#else
        return "";
#endif
    }

    void Sync(Ctx ctx, Transform root, float yaw, Vector3 center, bool routes, bool pics, bool fittings = false)
    {
        if (!routes && !pics && !fittings) { Clear(ctx); return; }
        string stamp = $"{(root ? root.GetInstanceID() : 0)}|{yaw:F3}|{center}|{sceneSig}|{RouteStore.Version}|{photoVersion}|{routes}|{pics}|{fittings}|{toggleVersion}";
        if (ctx.go != null && stamp == ctx.stamp) return;
        Clear(ctx);
        ctx.stamp = stamp; ctx.root = root; ctx.yaw = yaw; ctx.center = center;
        ctx.go = new GameObject("Overlays_" + ctx.name);
        if (root != null)
        {
            ctx.go.transform.SetParent(root, false);
            Quaternion g = Quaternion.Euler(0, yaw, 0);
            ctx.go.transform.localRotation = g;
            ctx.go.transform.localPosition = g * -center;
            ctx.go.transform.localScale = Vector3.one;
        }
        float scale = root != null ? Mathf.Max(1e-4f, root.lossyScale.x) : 1f;
#if META_XR_SDK_INSTALLED
        var rooms = MRUK.Instance != null ? MRUK.Instance.Rooms.Where(x => x != null).ToList() : new List<MRUKRoom>();
        if (routes) BuildRoutes(ctx, rooms, scale);
        if (pics) BuildPhotos(ctx, rooms);
        if (fittings) BuildFittings(ctx, rooms);
#endif
    }

    void Clear(Ctx ctx)
    {
        if (ctx.go) Destroy(ctx.go);
        foreach (var m in ctx.meshes) if (m) Destroy(m);
        ctx.meshes.Clear();
        ctx.waiting.Clear();
        ctx.go = ctx.routesGo = ctx.photosGo = null;
        ctx.stamp = null;
    }

    static MeshRenderer AddRenderer(GameObject go, Mesh mesh, Material mat)
    {
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        return mr;
    }

#if META_XR_SDK_INSTALLED
    // ---------- routes ----------

    void BuildRoutes(Ctx ctx, List<MRUKRoom> rooms, float scale)
    {
        var v = new List<Vector3>(); var col = new List<Color>(); var tri = new List<int>();
        var pts = new List<Vector3>();
        // In the Dollhouse (a small copy) a 2 cm pipe would vanish - never thinner than ~2.5 mm as you see it.
        float minR = 0.0025f / scale;
        foreach (var route in RouteStore.Routes)
        {
            if (!RouteStore.WorldPoints(route, rooms, pts)) continue;
            float rad = Mathf.Max(RouteStyle.Diameter(route) / 2f, minR);
            Color c = RouteStyle.Color(route.kind);
            if (route.proposed) c.a = 0.45f; // found in the photos, not confirmed yet
            if (pts.Count == 1) RouteMesh.AddBall(v, col, tri, pts[0], rad * 1.6f, c);
            else RouteMesh.AddTube(v, col, tri, pts, rad, c);
        }
        // boxes, sockets, outlets...: a plate on the surface (outlets/valves: a short stub out of it)
        float minPlate = 0.004f / scale;
        foreach (var m in RouteStore.Current.markers)
        {
            if (!RouteStore.WorldMarker(m, rooms, out var p, out var n)) continue;
            Color c = RouteStyle.Color(m.kind);
            if (RouteStyle.IsStub(m.type))
            {
                float r = Mathf.Max(0.012f, minR);
                pts.Clear(); pts.Add(p); pts.Add(p + n * Mathf.Max(0.06f, minPlate * 10));
                RouteMesh.AddTube(v, col, tri, pts, r, c);
                RouteMesh.AddPlate(v, col, tri, p + n * Mathf.Max(0.06f, minPlate * 10), n, r * 3.2f, r * 3.2f, Mathf.Max(0.01f, minPlate), Color.Lerp(c, Color.white, 0.4f));
            }
            else
            {
                var sz = RouteStyle.MarkerSize(m.type);
                RouteMesh.AddPlate(v, col, tri, p, n, Mathf.Max(sz.x, minPlate * 3), Mathf.Max(sz.y, minPlate * 3), Mathf.Max(0.015f, minPlate), c);
                // white face, so a plate reads as a box/socket and not as a blob of pipe
                RouteMesh.AddPlate(v, col, tri, p + n * Mathf.Max(0.015f, minPlate), n, Mathf.Max(sz.x, minPlate * 3) * 0.7f, Mathf.Max(sz.y, minPlate * 3) * 0.7f, 0.002f, Color.Lerp(c, Color.white, 0.6f));
            }
        }
        if (v.Count == 0) return;
        if (!routeMat) routeMat = new Material(Shader.Find("Sprites/Default")) { name = "Routes" };
        var mesh = RouteMesh.ToMesh("Routes", v, col, tri);
        ctx.meshes.Add(mesh);
        ctx.routesGo = new GameObject("Routes");
        ctx.routesGo.transform.SetParent(ctx.go.transform, false);
        AddRenderer(ctx.routesGo, mesh, routeMat);
    }

    // ---------- fittings & niches (passthrough) ----------

    Material ghostMat;

    void BuildFittings(Ctx ctx, List<MRUKRoom> rooms)
    {
        // the furnished saved scan (ListCachedScans puts it first) - its rooms are the live scan's rooms (same UUIDs)
        string scan = MRUKSceneCache.ListCachedScans().FirstOrDefault(n => File.Exists(MRUKSceneCache.FurniturePath(n)));
        var spec = scan != null ? Furniture.Load(scan) : null;
        if (spec == null) { FittingsInfo = "no saved scan with fittings"; return; }
        if (!ghostMat) ghostMat = new Material(Shader.Find("Sprites/Default")) { name = "FittingsGhost" };
        var valid = MRUK.Instance != null ? MRUKDataProcessor.GetValidRooms(MRUK.Instance) : rooms;
        int parts = 0;
        var ghost = Furniture.GhostMesh(spec, valid, 0.5f);
        if (ghost != null)
        {
            ctx.meshes.Add(ghost);
            var go = new GameObject("Fittings");
            go.transform.SetParent(ctx.go.transform, false);
            AddRenderer(go, ghost, ghostMat);
            parts++;
        }
        var niches = Furniture.NichesOf(spec, valid);
        if (niches.Count > 0)
        {
            var v = new List<Vector3>(); var col = new List<Color>(); var tri = new List<int>();
            foreach (var n in niches) AddRecess(v, col, tri, n);
            var mesh = RouteMesh.ToMesh("Niches", v, col, tri);
            ctx.meshes.Add(mesh);
            var go = new GameObject("Niches");
            go.transform.SetParent(ctx.go.transform, false);
            AddRenderer(go, mesh, ghostMat);
        }
        FittingsInfo = ghost == null && niches.Count == 0
            ? $"'{scan}': none of its rooms is in the live scan (different room ids?)"
            : $"from '{scan}': fittings{(ghost == null ? " none" : "")}, {niches.Count} niche(s)";
    }

    /// <summary>A niche as a dark box sunk into the real wall (drawn over passthrough it reads as a hole) + a bright rim.</summary>
    static void AddRecess(List<Vector3> v, List<Color> col, List<int> tri, XRModelFactory.WallNiche n)
    {
        Vector3 t = n.b - n.a; float w = t.magnitude; if (w < 0.01f) return; t /= w;
        Vector3 into = -n.inward; float d = n.depth, h = n.y1 - n.y0;
        Vector3 p0 = n.a, p1 = n.b, up = Vector3.up * h;
        Vector3 q0 = p0 + into * d, q1 = p1 + into * d;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 e, Color k)
        {
            int i = v.Count; v.Add(a); v.Add(b); v.Add(c); v.Add(e);
            col.Add(k); col.Add(k); col.Add(k); col.Add(k);
            tri.Add(i); tri.Add(i + 1); tri.Add(i + 2); tri.Add(i); tri.Add(i + 2); tri.Add(i + 3);
        }
        Quad(q0, q1, q1 + up, q0 + up, new Color(0.16f, 0.13f, 0.12f, 0.92f));          // back
        Quad(p0, q0, q0 + up, p0 + up, new Color(0.33f, 0.28f, 0.25f, 0.92f));          // left side
        Quad(p1, q1, q1 + up, p1 + up, new Color(0.30f, 0.25f, 0.22f, 0.92f));          // right side
        Quad(p0, p1, q1, q0, new Color(0.40f, 0.35f, 0.31f, 0.92f));                    // bottom
        Quad(p0 + up, p1 + up, q1 + up, q0 + up, new Color(0.22f, 0.18f, 0.16f, 0.92f)); // top
        // rim on the wall face, a touch in front of it
        Color rim = new Color(1f, 0.85f, 0.15f, 1f); const float r = 0.008f;
        Vector3 o = n.inward * 0.004f;
        Quad(p0 + o, p1 + o, p1 + o + Vector3.up * r, p0 + o + Vector3.up * r, rim);
        Quad(p0 + up + o, p1 + up + o, p1 + up + o - Vector3.up * r, p0 + up + o - Vector3.up * r, rim);
        Quad(p0 + o, p0 + up + o, p0 + up + o + t * r, p0 + o + t * r, rim);
        Quad(p1 + o, p1 + up + o, p1 + up + o - t * r, p1 + o - t * r, rim);
    }

    // ---------- photos ----------

    void BuildPhotos(Ctx ctx, List<MRUKRoom> rooms)
    {
        ctx.photosGo = new GameObject("WallPhotos");
        ctx.photosGo.transform.SetParent(ctx.go.transform, false);
        int k = 0;
        foreach (var p in photos)
        {
            var room = rooms.FirstOrDefault(x => DoorCatalog.RoomId(x) == p.meta.roomUuid);
            if (room == null) continue;
            Vector3 pos = room.transform.TransformPoint(p.meta.localPosition);
            Quaternion rot = room.transform.rotation * p.meta.localRotation;
            var mesh = PhotoMesh(p.meta, pos, rot, rooms, 0.012f + 0.0006f * (k++ % 10));
            if (mesh == null) continue;
            ctx.meshes.Add(mesh);
            var go = new GameObject("Photo_" + p.meta.index);
            go.transform.SetParent(ctx.photosGo.transform, false);
            var mr = AddRenderer(go, mesh, PhotoMaterial(p));
            if (p.tex == null) { mr.enabled = false; ctx.waiting.Add((p, mr)); }
        }
    }

    /// <summary>The photo draped over the scanned surfaces it shows: a grid of rays through the image hit against the
    /// scan, UV = image position, lifted 'lift' metres towards the camera (overlapping photos get different lifts).</summary>
    static Mesh PhotoMesh(WallPhotoMeta s, Vector3 pos, Quaternion rot, List<MRUKRoom> rooms, float lift)
    {
        var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
        var idx = new int[PhotoGridX, PhotoGridY];
        for (int j = 0; j < PhotoGridY; j++)
            for (int i = 0; i < PhotoGridX; i++)
            {
                idx[i, j] = -1;
                var uv = new Vector2(i / (float)(PhotoGridX - 1), j / (float)(PhotoGridY - 1));
                Vector3 dir = rot * new Vector3((uv.x - s.cu) / s.fu, (uv.y - s.cv) / s.fv, 1f).normalized;
                if (!Hit(new Ray(pos, dir), rooms, out var hp)) continue;
                idx[i, j] = verts.Count;
                verts.Add(hp - dir * lift);
                uvs.Add(uv);
            }
        for (int j = 0; j < PhotoGridY - 1; j++)
            for (int i = 0; i < PhotoGridX - 1; i++)
            {
                int a = idx[i, j], b = idx[i + 1, j], c = idx[i + 1, j + 1], d = idx[i, j + 1];
                if (a < 0 || b < 0 || c < 0 || d < 0) continue;
                // not across a depth jump (a door opening, the edge of a wall)
                if (Mathf.Max(Vector3.Distance(verts[a], verts[c]), Vector3.Distance(verts[b], verts[d])) > 0.8f) continue;
                tris.AddRange(new[] { a, b, c, a, c, d });
            }
        if (tris.Count == 0) return null;
        var m = new Mesh { name = "WallPhoto_" + s.index };
        m.SetVertices(verts); m.SetUVs(0, uvs); m.SetTriangles(tris, 0);
        var cols = new Color[verts.Count];
        for (int i = 0; i < cols.Length; i++) cols[i] = Color.white;
        m.colors = cols;
        m.RecalculateBounds();
        return m;
    }

    static bool Hit(Ray ray, List<MRUKRoom> rooms, out Vector3 point)
    {
        point = default; float best = float.MaxValue;
        foreach (var room in rooms)
            if (room.Raycast(ray, 12f, out RaycastHit hit, out _) && hit.distance < best) { best = hit.distance; point = hit.point; }
        return best < float.MaxValue;
    }
#endif

    Material PhotoMaterial(Photo p)
    {
        if (!p.mat)
        {
            p.mat = new Material(Shader.Find("Sprites/Default")) { name = "WallPhoto_" + p.meta.index };
            p.mat.color = new Color(1f, 1f, 1f, 0.92f);
            p.mat.mainTexture = p.tex ? p.tex : Texture2D.grayTexture;
        }
        return p.mat;
    }

    /// <summary>Every cable photo of every session (not the dense 3D-scan frames). Textures load in the background.</summary>
    void RefreshPhotoList()
    {
        photosDirty = false;
        var dirs = new List<string>();
        try
        {
            string root = MRUKPathUtility.IsDesktop
                ? Path.GetFullPath(MRUKPathUtility.GetExportRoot())
                : Path.Combine(Application.persistentDataPath, WallPhotoCapture.RootFolder);
            if (Directory.Exists(root)) dirs.AddRange(Directory.GetDirectories(root, WallPhotoCapture.SessionPrefix + "*"));
        }
        catch (Exception e) { Debug.LogWarning($"[WallOverlays] {e.Message}"); }

        var seen = new HashSet<string>();
        foreach (var dir in dirs.OrderBy(d => d, StringComparer.Ordinal))
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*.json"); } catch { continue; }
            foreach (var f in files.OrderBy(f => f, StringComparer.Ordinal))
            {
                if (!char.IsDigit(Path.GetFileName(f)[0])) continue;
                string jpg = Path.ChangeExtension(f, ".jpg");
                seen.Add(jpg);
                if (photoByPath.ContainsKey(jpg) || !File.Exists(jpg)) continue;
                WallPhotoMeta m;
                try { m = JsonUtility.FromJson<WallPhotoMeta>(File.ReadAllText(f)); } catch { continue; }
                if (m == null || m.dense || m.fu <= 0 || string.IsNullOrEmpty(m.roomUuid)) continue;
                var p = new Photo { meta = m, jpg = jpg };
                photos.Add(p); photoByPath[jpg] = p;
            }
        }
        // photos deleted since (Undo photo)
        foreach (var gone in photos.Where(p => !seen.Contains(p.jpg)).ToList())
        {
            photos.Remove(gone); photoByPath.Remove(gone.jpg);
            if (gone.mat) Destroy(gone.mat);
            if (gone.tex is RenderTexture rt) { rt.Release(); Destroy(rt); }
        }
        photoVersion++;
        if (loader == null) loader = StartCoroutine(LoadTextures());
    }

    /// <summary>One JPG per frame, scaled down to PhotoTexW x PhotoTexH (dozens of full-size photos would not fit).</summary>
    IEnumerator LoadTextures()
    {
        while (true)
        {
            var p = photos.FirstOrDefault(x => x.tex == null);
            if (p == null) break;
            Texture2D full = null;
            try
            {
                full = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!full.LoadImage(File.ReadAllBytes(p.jpg), false)) throw new Exception("not a JPG");
                var rt = new RenderTexture(PhotoTexW, PhotoTexH, 0, RenderTextureFormat.ARGB32) { name = "WallPhoto_" + p.meta.index, useMipMap = true, autoGenerateMips = true };
                rt.Create();
                Graphics.Blit(full, rt);
                p.tex = rt;
                if (p.mat) p.mat.mainTexture = rt;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WallOverlays] {p.jpg}: {e.Message}");
                p.tex = Texture2D.blackTexture;
            }
            finally { if (full) Destroy(full); }
            yield return null;
        }
        loader = null;
    }

    /// <summary>How many routes/photos the views have to show (for menus).</summary>
    public int PhotoCount => photos.Count;
}
