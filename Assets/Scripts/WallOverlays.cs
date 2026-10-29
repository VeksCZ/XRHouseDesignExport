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

    const string RoutesPref = "Overlay.Routes", PhotosPref = "Overlay.Photos", FittingsPref = "Overlay.Fittings", ZonesPref = "Overlay.Zones";

    static int showRoutes = -1, showPhotos = -1, showFittings = -1, showZones = -1; // cached: PlayerPrefs is a JNI call on Android, read every frame here

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

    /// <summary>No-drill zones: a see-through band on the surface along every route (and around every box), the route's
    /// width plus ZoneMargin on each side - where not to drill or chase (remembered between sessions).</summary>
    public static bool ShowZones
    {
        get { if (showZones < 0) showZones = PlayerPrefs.GetInt(ZonesPref, 0); return showZones == 1; }
        set { showZones = value ? 1 : 0; PlayerPrefs.SetInt(ZonesPref, showZones); PlayerPrefs.Save(); Bump(); }
    }
    /// <summary>Clearance on each side of a route (m) - the usual 10 cm either side of a chase.</summary>
    public const float ZoneMargin = 0.10f;
    const float ZoneLift = 0.012f;
    /// <summary>Tests: log where every zone band went.</summary>
    public static bool LogZones;

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
#if META_XR_SDK_INSTALLED
        if (aimCanvas) Destroy(aimCanvas.gameObject);
        if (photoCanvas) Destroy(photoCanvas.gameObject);
        if (photoTex) Destroy(photoTex);
#endif
    }

    void LateUpdate()
    {
        bool routes = ShowRoutes || ForceRoutes, pics = ShowPhotos;
        if ((pics || routes) && photosDirty) RefreshPhotoList();
        // rebuilt only for a finished load of the selected scan (never from what MRUK holds halfway through a load)
        if (!MRUKExporter.Loading && Time.frameCount - sigFrame >= 10) { sigFrame = Time.frameCount; sceneSig = SceneSignature(); }

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
#if META_XR_SDK_INSTALLED
        UpdateAim(walkOn ? walkCtx : worldCtx, walkOn);
#endif
    }

    static string SceneSignature()
    {
#if META_XR_SDK_INSTALLED
        var m = MRUK.Instance;
        if (m == null || m.Rooms.Count == 0) return "none";
        return $"{MRUKExporter.SceneVersion}:{m.Rooms.Count}";
#else
        return "";
#endif
    }

    void Sync(Ctx ctx, Transform root, float yaw, Vector3 center, bool routes, bool pics, bool fittings = false)
    {
        if (!routes && !pics && !fittings) { Clear(ctx); return; }
        string stamp = $"{(root ? root.GetInstanceID() : 0)}|{yaw:F3}|{center}|{sceneSig}|{RouteStore.Version}|{photoVersion}|{routes}|{pics}|{fittings}|{ShowZones}|{toggleVersion}";
        if (ctx.go != null && stamp == ctx.stamp) return;
        Clear(ctx);
        float t0 = Time.realtimeSinceStartup;
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
        if (routes && ShowZones && root == null || routes && ShowZones && ctx == walkCtx) BuildZones(ctx, rooms);
        if (routes && !pics) BuildPhotoIcons(ctx, rooms);
        if (pics) BuildPhotos(ctx, rooms);
        if (fittings) BuildFittings(ctx, rooms);
#endif
        Debug.Log($"[Overlays] {ctx.name} rebuilt in {(Time.realtimeSinceStartup - t0) * 1000f:0} ms: routes {(routes ? RouteStore.Routes.Count : 0)}, " +
                  $"boxes {(routes ? RouteStore.Current.markers.Count : 0)}, photos {(pics ? photos.Count : 0)}{(fittings ? ", fittings " + FittingsInfo : "")}");
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

    // ---------- no-drill zones ----------

    /// <summary>The surface a route point lies on (the nearest scanned wall/floor/ceiling plane it is in front of, within
    /// 15 cm and inside the plane's rectangle): point on it and its normal towards the route.</summary>
    static bool Surface(List<MRUKRoom> rooms, Vector3 p, out Vector3 sp, out Vector3 n)
    {
        sp = p; n = Vector3.up; float best = 0.15f; bool found = false;
        foreach (var room in rooms)
            foreach (var a in room.Anchors)
            {
                if (a == null || !a.PlaneRect.HasValue) continue;
                if (!a.HasAnyLabel(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE | MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.CEILING)) continue;
                Vector3 l = a.transform.InverseTransformPoint(p);
                var r = a.PlaneRect.Value;
                if (l.x < r.xMin - 0.1f || l.x > r.xMax + 0.1f || l.y < r.yMin - 0.1f || l.y > r.yMax + 0.1f) continue;
                float d = Mathf.Abs(l.z);
                if (d >= best) continue;
                best = d; found = true;
                n = a.transform.forward * (l.z >= 0 ? 1f : -1f);
                sp = p - n * d;
            }
        return found;
    }

    void BuildZones(Ctx ctx, List<MRUKRoom> rooms)
    {
        var v = new List<Vector3>(); var col = new List<Color>(); var tri = new List<int>();
        var pts = new List<Vector3>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color k)
        {
            int i = v.Count; v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            col.Add(k); col.Add(k); col.Add(k); col.Add(k);
            tri.Add(i); tri.Add(i + 1); tri.Add(i + 2); tri.Add(i); tri.Add(i + 2); tri.Add(i + 3);
        }
        foreach (var route in RouteStore.Routes)
        {
            if (route.proposed || !RouteStore.WorldPoints(route, rooms, pts)) continue;
            Color k = RouteStyle.Color(route.kind); k.a = 0.22f;
            float half = RouteStyle.Diameter(route) / 2f + ZoneMargin;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                Vector3 a = pts[i], b = pts[i + 1], mid = (a + b) / 2f;
                if (!Surface(rooms, mid, out var sp, out var n)) continue;
                Vector3 dir = b - a; float len = dir.magnitude;
                if (len < 0.01f) continue; dir /= len;
                if (Mathf.Abs(Vector3.Dot(dir, n)) > 0.7f) continue; // runs into the surface (a riser through the floor)
                if (LogZones) Debug.Log($"[Overlays] zone {RouteStyle.Name(route.kind)} seg {i}: {a} -> {b}, surface n {n}, off {Vector3.Dot(mid - sp, n):0.000}");
                Vector3 lift = n * ZoneLift; // the walk-through model's faces sit a little in front of the scanned planes
                Vector3 A = a - n * Vector3.Dot(a - sp, n) + lift - dir * half, B = b - n * Vector3.Dot(b - sp, n) + lift + dir * half;
                Vector3 across = Vector3.Cross(dir, n).normalized * half;
                Quad(A - across, B - across, B + across, A + across, k);
            }
        }
        foreach (var m in RouteStore.Current.markers)
        {
            if (!RouteStore.WorldMarker(m, rooms, out var p, out var n)) continue;
            Color k = RouteStyle.Color(m.kind); k.a = 0.22f;
            var sz = RouteStyle.MarkerSize(m.type) / 2f + Vector2.one * ZoneMargin;
            Vector3 up = Mathf.Abs(n.y) > 0.9f ? Vector3.forward : Vector3.up;
            Vector3 ax = Vector3.Cross(up, n).normalized * sz.x, ay = Vector3.Cross(n, ax).normalized * sz.y;
            Vector3 c = p + n * ZoneLift;
            Quad(c - ax - ay, c + ax - ay, c + ax + ay, c - ax + ay, k);
        }
        if (v.Count == 0) return;
        if (!routeMat) routeMat = new Material(Shader.Find("Sprites/Default")) { name = "Routes" };
        Debug.Log($"[Overlays] {ctx.name}: {v.Count / 4} no-drill zone band(s)");
        var mesh = RouteMesh.ToMesh("NoDrillZones", v, col, tri);
        ctx.meshes.Add(mesh);
        var go = new GameObject("NoDrillZones");
        go.transform.SetParent(ctx.go.transform, false);
        AddRenderer(go, mesh, routeMat);
    }

    // ---------- photo icons (point at one to see the photo) ----------

    /// <summary>Where a photo looks at: the scanned surface in the middle of the image (scan frame) and its normal.</summary>
    public static bool PhotoTarget(WallPhotoMeta m, List<MRUKRoom> rooms, out Vector3 point, out Vector3 normal)
    {
        point = default; normal = Vector3.up;
        var room = rooms.FirstOrDefault(x => x != null && DoorCatalog.RoomId(x) == m.roomUuid);
        if (room == null) return false;
        Vector3 pos = room.transform.TransformPoint(m.localPosition);
        Quaternion rot = room.transform.rotation * m.localRotation;
        Vector3 dir = rot * new Vector3((0.5f - m.cu) / m.fu, (0.5f - m.cv) / m.fv, 1f).normalized;
        float best = float.MaxValue;
        foreach (var r in rooms)
            if (r.Raycast(new Ray(pos, dir), 8f, out RaycastHit hit, out _) && hit.distance < best) { best = hit.distance; point = hit.point; normal = hit.normal; }
        return best < float.MaxValue;
    }

    /// <summary>The route a photo shows (the nearest one within 0.6 m of where it looks), or null.</summary>
    public static PipeRoute PhotoRoute(Vector3 target, List<MRUKRoom> rooms)
    {
        PipeRoute bestR = null; float best = 0.6f;
        var pts = new List<Vector3>();
        foreach (var r in RouteStore.Routes)
        {
            if (!RouteStore.WorldPoints(r, rooms, pts)) continue;
            for (int i = 0; i < pts.Count; i++)
            {
                float d = i + 1 < pts.Count ? DistToSegment(target, pts[i], pts[i + 1]) : Vector3.Distance(target, pts[i]);
                if (d < best) { best = d; bestR = r; }
            }
        }
        return bestR;
    }

    static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a; float t = ab.sqrMagnitude < 1e-8f ? 0 : Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
        return Vector3.Distance(p, a + ab * t);
    }

    class Icon { public Photo photo; public Vector3 p, n; }
    readonly List<Icon> icons = new List<Icon>();

    void BuildPhotoIcons(Ctx ctx, List<MRUKRoom> rooms)
    {
        if (ctx != worldCtx && ctx != walkCtx) return;
        icons.Clear();
        var v = new List<Vector3>(); var col = new List<Color>(); var tri = new List<int>();
        foreach (var ph in photos)
        {
            if (!PhotoTarget(ph.meta, rooms, out var p, out var n)) continue;
            var route = PhotoRoute(p, rooms);
            Color frame = route != null ? RouteStyle.Color(route.kind) : new Color(0.85f, 0.85f, 0.85f);
            // a little camera on the wall: coloured body (the route it shows), dark lens
            RouteMesh.AddPlate(v, col, tri, p + n * 0.006f, n, 0.09f, 0.065f, 0.012f, frame);
            RouteMesh.AddPlate(v, col, tri, p + n * 0.018f, n, 0.034f, 0.034f, 0.008f, new Color(0.08f, 0.08f, 0.1f));
            icons.Add(new Icon { photo = ph, p = p, n = n });
        }
        if (v.Count == 0) return;
        if (!routeMat) routeMat = new Material(Shader.Find("Sprites/Default")) { name = "Routes" };
        var mesh = RouteMesh.ToMesh("PhotoIcons", v, col, tri);
        ctx.meshes.Add(mesh);
        var go = new GameObject("PhotoIcons");
        go.transform.SetParent(ctx.go.transform, false);
        AddRenderer(go, mesh, routeMat);
    }

    // ---------- pointing at a route / box / photo: what it is ----------

    Transform aimHand, aimHead;
    Canvas aimCanvas; TMPro.TMP_Text aimText;
    Canvas photoCanvas; UnityEngine.UI.RawImage photoImage; Texture2D photoTex; string photoShown;
    Icon aimIcon; float aimIconSince, aimLostAt;
    string aimKey;

    void UpdateAim(Ctx ctx, bool walkOn)
    {
        if (!aimHand)
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            if (rig) { aimHand = rig.rightHandAnchor; aimHead = rig.centerEyeAnchor; }
        }
        string text = null; Vector3 at = default; Icon icon = null;
        if (aimHand && aimHead && ctx.go != null && (ShowRoutes || ForceRoutes) && !MRUKExporter.Loading && !XRMenu.PointingAtAnyUI && MRUK.Instance != null)
            text = Aimed(ctx, walkOn, out at, out icon);
        ShowAimLabel(text, at);
        // a photo icon pointed at for a moment: the photo next to it, until you point away for a while
        if (icon != null && icon == aimIcon) { if (Time.unscaledTime - aimIconSince > 0.35f) ShowPhoto(icon, ctx); aimLostAt = Time.unscaledTime; }
        else if (icon != null) { aimIcon = icon; aimIconSince = Time.unscaledTime; }
        else if (photoCanvas && photoCanvas.gameObject.activeSelf && Time.unscaledTime - aimLostAt > 1.2f) { photoCanvas.gameObject.SetActive(false); aimIcon = null; }
        else if (icon == null && (photoCanvas == null || !photoCanvas.gameObject.activeSelf)) aimIcon = null;
    }

    /// <summary>What the right controller points at (route, box or photo icon) as a label, and where (view frame).</summary>
    string Aimed(Ctx ctx, bool walkOn, out Vector3 at, out Icon icon)
    {
        at = default; icon = null;
        var T = ctx.go.transform;
        // the ray in the scan frame
        Vector3 o = T.InverseTransformPoint(aimHand.position), d = T.InverseTransformDirection(aimHand.forward).normalized;
        float scale = Mathf.Max(1e-4f, T.lossyScale.x);
        var rooms = MRUK.Instance.Rooms.Where(x => x != null).ToList();
        float len = 8f;
        if (walkOn && walk != null && walk.RaycastModel(new Ray(aimHand.position, aimHand.forward), out var wh)) len = wh.distance / scale;
        else foreach (var r in rooms) if (r.Raycast(new Ray(o, d), 8f, out RaycastHit h, out _) && h.distance < len) len = h.distance;
        len += 0.12f;
        Vector3 e = o + d * len;

        float best = float.MaxValue; string label = null; Vector3 hitP = default;
        if (!ShowPhotos) foreach (var ic in icons)
        {
            float dd = SegPoint(o, e, ic.p, out _);
            if (dd < 0.06f && dd < best) { best = dd; icon = ic; hitP = ic.p; label = null; }
        }
        var pts = new List<Vector3>();
        foreach (var r in RouteStore.Routes)
        {
            if (!RouteStore.WorldPoints(r, rooms, pts)) continue;
            float tol = 0.05f + RouteStyle.Diameter(r) / 2f;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                float dd = SegSeg(o, e, pts[i], pts[i + 1], out var q);
                if (dd < tol && dd < best)
                {
                    best = dd; hitP = q; icon = null;
                    string desc = RouteStyle.Describe(r);
                    label = $"<b>{RouteStyle.Name(r.kind)}</b> {Mathf.RoundToInt(RouteStyle.Diameter(r) * 1000)} mm, {RouteStyle.Length(pts):0.0} m{(desc.Length > 0 ? "\n" + desc : "")}\n<size=80%>here {Cm(q.y - FloorBelow(rooms, r.roomUuid, q))} cm above the rough floor</size>";
                }
            }
        }
        foreach (var m in RouteStore.Current.markers)
        {
            if (!RouteStore.WorldMarker(m, rooms, out var mp, out _)) continue;
            float dd = SegPoint(o, e, mp, out _);
            if (dd < 0.07f && dd < best)
            {
                best = dd; hitP = mp; icon = null;
                label = $"<b>{m.type}</b> ({RouteStyle.Name(m.kind)}){(string.IsNullOrEmpty(m.circuit) ? "" : ", circuit " + m.circuit)}\n{Cm(mp.y - FloorBelow(rooms, m.roomUuid, mp))} cm above the rough floor (centre)";
            }
        }
        if (icon != null)
        {
            var route = PhotoRoute(icon.p, rooms);
            label = $"<b>Photo {icon.photo.meta.index}</b> {icon.photo.meta.capturedAt?.Replace('T', ' ')}{(route != null ? "\n" + RouteStyle.Name(route.kind) + " " + RouteStyle.Describe(route) : "")}";
        }
        if (label == null) return null;
        at = T.TransformPoint(hitP);
        return label;
    }

    static string Cm(float m) => Mathf.RoundToInt(m * 100f).ToString();

    /// <summary>The rough floor right below p in the given room (split levels: the highest floor not above p).</summary>
    static float FloorBelow(List<MRUKRoom> rooms, string roomUuid, Vector3 p)
    {
        var room = rooms.FirstOrDefault(x => DoorCatalog.RoomId(x) == roomUuid);
        if (room == null || room.FloorAnchors.Count == 0) return p.y;
        float best = float.MinValue;
        foreach (var f in room.FloorAnchors) if (f != null && f.transform.position.y <= p.y + 0.05f) best = Mathf.Max(best, f.transform.position.y);
        return best > float.MinValue ? best : room.FloorAnchors.Where(f => f != null).Min(f => f.transform.position.y);
    }

    static float SegPoint(Vector3 a, Vector3 b, Vector3 p, out Vector3 q)
    {
        Vector3 ab = b - a; float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
        q = p; return Vector3.Distance(a + ab * t, p);
    }

    /// <summary>Distance between segments p1..q1 (the ray) and p2..q2; 'onSecond' = the closest point on the second.</summary>
    static float SegSeg(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 onSecond)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r), s, t;
        if (e < 1e-9f) { onSecond = p2; return SegPoint(p1, q1, p2, out _); }
        float c = Vector3.Dot(d1, r), b = Vector3.Dot(d1, d2), den = a * e - b * b;
        s = den > 1e-9f ? Mathf.Clamp01((b * f - c * e) / den) : 0f;
        t = (b * s + f) / e;
        if (t < 0) { t = 0; s = Mathf.Clamp01(-c / a); }
        else if (t > 1) { t = 1; s = Mathf.Clamp01((b - c) / a); }
        onSecond = p2 + d2 * t;
        return Vector3.Distance(p1 + d1 * s, onSecond);
    }

    void ShowAimLabel(string text, Vector3 at)
    {
        if (text == null) { if (aimCanvas) aimCanvas.gameObject.SetActive(false); aimKey = null; return; }
        if (!aimCanvas)
        {
            aimCanvas = XRUi.CreateWorldCanvas("AimLabel", new Vector2(560, 130), 0.0005f);
            XRUi.CreatePanel(aimCanvas.transform, "Bg", new Color(0.05f, 0.06f, 0.08f, 0.82f), 0, 0, 560, 130);
            aimText = XRUi.CreateText(aimCanvas.transform, "Text", "", 22, TMPro.TextAlignmentOptions.MidlineLeft, 14, 4, 532, 122, Color.white);
            // a label must never catch the menu ray or the walk-through's raycasts
            foreach (var gr in aimCanvas.GetComponentsInChildren<UnityEngine.UI.Graphic>()) gr.raycastTarget = false;
        }
        aimCanvas.gameObject.SetActive(true);
        if (aimKey != text) { aimKey = text; aimText.text = text; }
        Vector3 toHead = (aimHead.position - at).normalized;
        aimCanvas.transform.position = at + toHead * 0.12f + Vector3.up * 0.09f;
        aimCanvas.transform.rotation = Quaternion.LookRotation(aimCanvas.transform.position - aimHead.position, Vector3.up);
    }

    void ShowPhoto(Icon icon, Ctx ctx)
    {
        if (!photoCanvas)
        {
            photoCanvas = XRUi.CreateWorldCanvas("PhotoPopup", new Vector2(800, 600), 0.0006f);
            var go = new GameObject("Image", typeof(RectTransform));
            go.layer = photoCanvas.gameObject.layer;
            go.transform.SetParent(photoCanvas.transform, false);
            photoImage = go.AddComponent<UnityEngine.UI.RawImage>();
            photoImage.raycastTarget = false;
            XRUi.Place((RectTransform)go.transform, 0, 0, 800, 600);
            foreach (var gr in photoCanvas.GetComponentsInChildren<UnityEngine.UI.Graphic>()) gr.raycastTarget = false;
        }
        if (photoShown != icon.photo.jpg)
        {
            photoShown = icon.photo.jpg;
            try
            {
                if (!photoTex) photoTex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                photoTex.LoadImage(File.ReadAllBytes(icon.photo.jpg), false);
                photoImage.texture = photoTex;
                float aspect = photoTex.height > 0 ? photoTex.width / (float)photoTex.height : 4f / 3f;
                ((RectTransform)photoImage.transform).sizeDelta = new Vector2(600 * aspect, 600);
            }
            catch (Exception e) { Debug.LogWarning($"[WallOverlays] {icon.photo.jpg}: {e.Message}"); }
        }
        photoCanvas.gameObject.SetActive(true);
        Vector3 p = ctx.go.transform.TransformPoint(icon.p);
        Vector3 toHead = aimHead.position - p; toHead.y = 0;
        Vector3 pos = p + toHead.normalized * Mathf.Min(0.45f, toHead.magnitude * 0.5f) + Vector3.up * 0.28f;
        photoCanvas.transform.position = pos;
        photoCanvas.transform.rotation = Quaternion.LookRotation(pos - aimHead.position, Vector3.up);
    }

    // ---------- fittings & niches (passthrough) ----------

    Material ghostMat;

    void BuildFittings(Ctx ctx, List<MRUKRoom> rooms)
    {
        // the fittings of the scan you work on (its edits are bound to it); on the live scan, a furnished saved scan
        // that has the live rooms (same UUIDs) - never some other place's scan
        string scan = HouseEditsStore.CurrentScan;
        if (string.IsNullOrEmpty(scan) || !File.Exists(MRUKSceneCache.FurniturePath(scan)))
            scan = string.IsNullOrEmpty(scan)
                ? MRUKSceneCache.ListCachedScans().FirstOrDefault(n => File.Exists(MRUKSceneCache.FurniturePath(n)))
                : null;
        var spec = scan != null ? Furniture.Load(scan) : null;
        if (spec == null) { FittingsInfo = string.IsNullOrEmpty(HouseEditsStore.CurrentScan) ? "no saved scan with fittings" : $"'{HouseEditsStore.CurrentScan}' has no fittings"; return; }
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
        var seen = new HashSet<string>();
        foreach (var (m, jpg) in ListPhotos(photoByPath.Keys))
        {
            seen.Add(jpg);
            if (m == null || photoByPath.ContainsKey(jpg)) continue;
            var p = new Photo { meta = m, jpg = jpg };
            photos.Add(p); photoByPath[jpg] = p;
        }
        // photos deleted since (Undo photo)
        foreach (var gone in photos.Where(p => !seen.Contains(p.jpg)).ToList())
        {
            photos.Remove(gone); photoByPath.Remove(gone.jpg);
            if (gone.mat) Destroy(gone.mat);
            if (gone.tex is RenderTexture rt) { rt.Release(); Destroy(rt); }
        }
        photoVersion++;
        if (loader == null && ShowPhotos) loader = StartCoroutine(LoadTextures());
    }

    /// <summary>Every cable photo (not the dense 3D-scan frames) of every session with its sidecar, oldest first. Paths in
    /// 'known' are listed without reading their sidecar again (meta null). Main thread (persistentDataPath).</summary>
    public static List<(WallPhotoMeta meta, string jpg)> ListPhotos(ICollection<string> known = null)
    {
        var res = new List<(WallPhotoMeta, string)>();
        var dirs = new List<string>();
        try
        {
            string root = MRUKPathUtility.IsDesktop
                ? Path.GetFullPath(MRUKPathUtility.GetExportRoot())
                : Path.Combine(Application.persistentDataPath, WallPhotoCapture.RootFolder);
            if (Directory.Exists(root)) dirs.AddRange(Directory.GetDirectories(root, WallPhotoCapture.SessionPrefix + "*"));
        }
        catch (Exception e) { Debug.LogWarning($"[WallOverlays] {e.Message}"); }
        foreach (var dir in dirs.OrderBy(d => d, StringComparer.Ordinal))
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*.json"); } catch { continue; }
            foreach (var f in files.OrderBy(f => f, StringComparer.Ordinal))
            {
                if (!char.IsDigit(Path.GetFileName(f)[0])) continue;
                string jpg = Path.ChangeExtension(f, ".jpg");
                if (!File.Exists(jpg)) continue;
                if (known != null && known.Contains(jpg)) { res.Add((null, jpg)); continue; }
                WallPhotoMeta m;
                try { m = JsonUtility.FromJson<WallPhotoMeta>(File.ReadAllText(f)); } catch { continue; }
                if (m == null || m.dense || m.fu <= 0 || string.IsNullOrEmpty(m.roomUuid)) continue;
                res.Add((m, jpg));
            }
        }
        return res;
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
