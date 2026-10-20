using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Proposes routes from the cable photos (WallPhotoCapture) - on the PC, where every photo's exact camera pose and the
/// scan are at hand. Each photo is "unrolled" onto every scanned wall/floor/ceiling plane it sees (rectified at 1 cm per
/// pixel, occlusion-checked against the scan), so cable chases and pipes - which run plumb or level - become straight
/// vertical/horizontal stripes there. A stripe = a narrow band that differs from the surface on BOTH sides along at
/// least ~35 cm; regularly spaced level stripes (mortar bed joints of the blocks) are dropped. Stripes found in several
/// photos of the same surface are merged, and their colour picks the kind (blue = cold water, red = hot water, light
/// neutral band on the masonry = plaster-filled chase / electric, saturated orange = waste (KG), ...).
/// The result is only a proposal: routes with proposed = true (drawn faint/dashed), to be kept or deleted in the
/// headset's Routes tool. Debug images (rectified surface + what was found) go to Routes/detect_debug.
/// </summary>
public static class RouteDetector
{
    public const string Source = "photos";
    const float Px = 0.01f;                 // rectified image: 1 cm per pixel
    const float MinLenWall = 0.35f, MinLenFloor = 0.5f;
    const float MaxCamDist = 4.5f, MaxIncidence = 72f;
    const int MaxDebugImages = 80;

    public class Result { public int photos, views, stripes, routes; public string debugDir; public string Summary => $"{routes} proposed route(s) from {photos} photo(s) ({views} surface views, {stripes} stripes)"; }

#if META_XR_SDK_INSTALLED
    class Surface
    {
        public MRUKRoom room; public MRUKAnchor anchor; public Rect rect; public bool horizontal;
        public List<(Rect r, float tol)> holes = new List<(Rect, float)>(); // openings in this plane (anchor-local)
    }

    /// <summary>One stripe in a surface's plane: vertical (constant x) or level (constant y), anchor-local metres.</summary>
    class Stripe
    {
        public Surface s; public bool vertical; public float pos, from, to, strength; public Color color, background;
        public int photos = 1; public RouteKind kind; public float conf;
    }

    /// <summary>Runs the detection over every cable photo of the export root's Photos_* sessions whose room is loaded,
    /// replaces the earlier proposals in RouteStore and returns what it did.</summary>
    public static Result DetectAll(List<MRUKRoom> rooms)
    {
        var res = new Result();
        var photos = new List<(WallPhotoMeta m, string jpg)>();
        string root = Path.GetFullPath(MRUKPathUtility.GetExportRoot());
        foreach (var dir in Directory.GetDirectories(root, WallPhotoCapture.SessionPrefix + "*"))
            foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!char.IsDigit(Path.GetFileName(f)[0])) continue;
                string jpg = Path.ChangeExtension(f, ".jpg");
                if (!File.Exists(jpg)) continue;
                WallPhotoMeta m;
                try { m = JsonUtility.FromJson<WallPhotoMeta>(File.ReadAllText(f)); } catch { continue; }
                if (m == null || m.dense || m.fu <= 0) continue;
                if (rooms.Any(r => DoorCatalog.RoomId(r) == m.roomUuid)) photos.Add((m, jpg));
            }

        var surfaces = Surfaces(rooms);
        res.debugDir = Path.Combine(Path.GetDirectoryName(RouteStore.FilePath), "detect_debug");
        try { if (Directory.Exists(res.debugDir)) Directory.Delete(res.debugDir, true); Directory.CreateDirectory(res.debugDir); } catch { }
        int debugN = 0;
        var all = new List<Stripe>();
        foreach (var (m, jpg) in photos)
        {
            var room = rooms.First(r => DoorCatalog.RoomId(r) == m.roomUuid);
            Vector3 cam = room.transform.TransformPoint(m.localPosition);
            Quaternion rot = room.transform.rotation * m.localRotation;
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                if (!tex.LoadImage(File.ReadAllBytes(jpg), false)) continue;
                var px = tex.GetPixels32();
                res.photos++;
                foreach (var s in surfaces)
                {
                    if (!Rectify(s, px, tex.width, tex.height, m, cam, rot, rooms, out var img, out var ok, out int w, out int h)) continue;
                    res.views++;
                    var found = Find(s, img, ok, w, h);
                    all.AddRange(found);
                    if (found.Count > 0 && debugN < MaxDebugImages)
                    {
                        debugN++;
                        WriteDebug(Path.Combine(res.debugDir, $"{Path.GetFileNameWithoutExtension(jpg)}_{s.anchor.Label}_{DoorCatalog.RoomId(s.room).Substring(0, 4)}_{debugN:000}.png"), img, ok, w, h, found);
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[RouteDetector] {jpg}: {e.Message}"); }
            finally { UnityEngine.Object.Destroy(tex); }
        }
        res.stripes = all.Count;

        var merged = Merge(all);
        var routes = new List<PipeRoute>();
        foreach (var st in merged)
        {
            Classify(st);
            float r = RouteStyle.Diameters(st.kind)[0] / 2f;
            Vector3 n = st.s.anchor.transform.forward;
            Vector3 a = St(st, st.from) + n * r, b = St(st, st.to) + n * r;
            var route = new PipeRoute
            {
                id = Guid.NewGuid().ToString("N"), kind = st.kind, roomUuid = DoorCatalog.RoomId(st.s.room),
                created = DateTime.Now.ToString("s"), proposed = true, source = Source, confidence = st.conf,
            };
            route.localPoints.Add(new Vector3Data(st.s.room.transform.InverseTransformPoint(a)));
            route.localPoints.Add(new Vector3Data(st.s.room.transform.InverseTransformPoint(b)));
            routes.Add(route);
        }
        res.routes = routes.Count;
        RouteStore.Apply(f => { f.routes.RemoveAll(x => x.proposed && x.source == Source); f.routes.AddRange(routes); });
        Debug.Log($"[RouteDetector] {res.Summary}; debug images in {res.debugDir}");
        return res;
    }

    static Vector3 St(Stripe st, float along) => st.s.anchor.transform.TransformPoint(st.vertical ? new Vector3(st.pos, along, 0) : new Vector3(along, st.pos, 0));

    // ---------- surfaces ----------

    static List<Surface> Surfaces(List<MRUKRoom> rooms)
    {
        var list = new List<Surface>();
        var openings = rooms.SelectMany(r => r.Anchors).Where(a => a != null && a.PlaneRect.HasValue &&
            (a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW"))).ToList();
        foreach (var room in rooms)
            foreach (var a in room.Anchors)
            {
                if (a == null || !a.PlaneRect.HasValue) continue;
                bool wall = MRUKDataProcessor.IsStructuralWall(a);
                bool flat = a.Label == MRUKAnchor.SceneLabels.FLOOR || a.Label == MRUKAnchor.SceneLabels.CEILING;
                if (!wall && !flat) continue;
                var s = new Surface { room = room, anchor = a, rect = a.PlaneRect.Value, horizontal = flat };
                if (wall)
                    foreach (var o in openings)
                    {
                        Vector3 l = a.transform.InverseTransformPoint(o.transform.position);
                        if (Mathf.Abs(l.z) > 0.35f || Mathf.Abs(Vector3.Dot(o.transform.forward, a.transform.forward)) < 0.9f) continue;
                        var or = o.PlaneRect.Value;
                        s.holes.Add((new Rect(l.x - or.width / 2f - 0.03f, l.y - or.height / 2f - 0.03f, or.width + 0.06f, or.height + 0.06f), 0f));
                    }
                list.Add(s);
            }
        return list;
    }

    static bool InHole(Surface s, float x, float y)
    {
        foreach (var (r, _) in s.holes) if (r.Contains(new Vector2(x, y))) return true;
        return false;
    }

    static bool InBoundary(MRUKAnchor a, float x, float y)
    {
        var b = a.PlaneBoundary2D;
        if (b == null || b.Count < 3) return true;
        bool inside = false;
        for (int i = 0, j = b.Count - 1; i < b.Count; j = i++)
            if ((b[i].y > y) != (b[j].y > y) && x < (b[j].x - b[i].x) * (y - b[i].y) / (b[j].y - b[i].y) + b[i].x) inside = !inside;
        return inside;
    }

    // ---------- rectification ----------

    /// <summary>The photo resampled onto the surface's plane, 1 cm per pixel over its PlaneRect (img = RGB 0..1, ok = seen
    /// in this photo, in front of the camera, not hidden behind other scanned surfaces, not an opening).</summary>
    static bool Rectify(Surface s, Color32[] px, int tw, int th, WallPhotoMeta m, Vector3 cam, Quaternion rot, List<MRUKRoom> rooms,
        out Color[] img, out bool[] ok, out int w, out int h)
    {
        img = null; ok = null; w = h = 0;
        var t = s.anchor.transform;
        Vector3 n = t.forward;
        if (Vector3.Dot(n, cam - t.position) < 0.05f) return false; // looking at its back
        var r = s.rect;
        // quick test: does any part of the plane project into the image?
        bool any = false;
        for (int i = 0; i <= 4 && !any; i++) for (int j = 0; j <= 4 && !any; j++)
                any = Project(t.TransformPoint(new Vector3(r.xMin + r.width * i / 4f, r.yMin + r.height * j / 4f, 0)), m, cam, rot, out _, out _);
        if (!any) return false;

        w = Mathf.Max(1, Mathf.CeilToInt(r.width / Px)); h = Mathf.Max(1, Mathf.CeilToInt(r.height / Px));
        img = new Color[w * h]; ok = new bool[w * h];
        Quaternion inv = Quaternion.Inverse(rot);
        const int Block = 10;
        int bw = (w + Block - 1) / Block, bh = (h + Block - 1) / Block;
        var visible = new sbyte[bw * bh]; // 0 unknown, 1 visible, -1 hidden
        int seen = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float lx = r.xMin + (x + 0.5f) * Px, ly = r.yMin + (y + 0.5f) * Px;
                if (s.horizontal ? !InBoundary(s.anchor, lx, ly) : InHole(s, lx, ly)) continue;
                Vector3 P = t.TransformPoint(new Vector3(lx, ly, 0));
                if (!Project(P, m, cam, rot, out var vx, out var vy)) continue;
                Vector3 d = P - cam;
                float dist = d.magnitude;
                if (dist > MaxCamDist || Vector3.Angle(-d, n) > MaxIncidence) continue;
                int bi = (y / Block) * bw + x / Block;
                if (visible[bi] == 0)
                {
                    // something scanned in between (another wall, a cabinet) hides this block
                    visible[bi] = 1;
                    float best = float.MaxValue;
                    var ray = new Ray(cam, d / dist);
                    foreach (var room in rooms)
                        if (room.Raycast(ray, dist + 0.5f, out RaycastHit hit, out _) && hit.distance < best) best = hit.distance;
                    if (best < dist - 0.08f) visible[bi] = -1;
                }
                if (visible[bi] < 0) continue;
                img[y * w + x] = Sample(px, tw, th, vx, vy);
                ok[y * w + x] = true;
                seen++;
            }
        return seen > 400; // at least ~0.04 m2 of it
    }

    static bool Project(Vector3 P, WallPhotoMeta m, Vector3 cam, Quaternion rot, out float vx, out float vy)
    {
        Vector3 c = Quaternion.Inverse(rot) * (P - cam);
        vx = vy = 0;
        if (c.z < 0.15f) return false;
        vx = m.cu + m.fu * c.x / c.z; vy = m.cv + m.fv * c.y / c.z;
        return vx > 0.01f && vx < 0.99f && vy > 0.01f && vy < 0.99f;
    }

    static Color Sample(Color32[] px, int w, int h, float u, float v)
    {
        float fx = u * w - 0.5f, fy = v * h - 0.5f; // texture rows bottom-up = viewport v
        int x0 = Mathf.Clamp((int)fx, 0, w - 2), y0 = Mathf.Clamp((int)fy, 0, h - 2);
        float ax = Mathf.Clamp01(fx - x0), ay = Mathf.Clamp01(fy - y0);
        Color c00 = px[y0 * w + x0], c10 = px[y0 * w + x0 + 1], c01 = px[(y0 + 1) * w + x0], c11 = px[(y0 + 1) * w + x0 + 1];
        return Color.Lerp(Color.Lerp(c00, c10, ax), Color.Lerp(c01, c11, ax), ay);
    }

    // ---------- stripes ----------

    static List<Stripe> Find(Surface s, Color[] img, bool[] ok, int w, int h)
    {
        var res = new List<Stripe>();
        float minLen = s.horizontal ? MinLenFloor : MinLenWall;
        float thr = s.horizontal ? 0.13f : 0.10f;
        // vertical stripes: scan across x in every row; level ones: the same on the transposed image
        res.AddRange(Scan(s, img, ok, w, h, true, minLen, thr));
        res.AddRange(Scan(s, img, ok, w, h, false, minLen, thr));
        DropJoints(res, !s.horizontal);
        return res;
    }

    /// <summary>vertical = stripes along y (the response is taken across x).</summary>
    static List<Stripe> Scan(Surface s, Color[] img, bool[] ok, int w, int h, bool vertical, float minLen, float thr)
    {
        int len = vertical ? w : h, lines = vertical ? h : w; // "across" length and number of scan lines
        Color At(int across, int line) => vertical ? img[line * w + across] : img[across * w + line];
        bool Ok(int across, int line) => vertical ? ok[line * w + across] : ok[across * w + line];

        var resp = new float[len * lines];
        var inner = new Color[len * lines];
        var outer = new Color[len * lines];
        var pre = new Color[len + 1]; var cnt = new int[len + 1];
        int[] halfW = { 1, 3 };          // inner band 3 cm / 7 cm
        const int gap = 2, side = 6;     // then 2 cm gap, then 6 cm of background on each side
        for (int L = 0; L < lines; L++)
        {
            pre[0] = Color.clear; cnt[0] = 0;
            for (int i = 0; i < len; i++)
            {
                bool o = Ok(i, L);
                pre[i + 1] = pre[i] + (o ? At(i, L) : Color.clear);
                cnt[i + 1] = cnt[i] + (o ? 1 : 0);
            }
            Color Mean(int a, int b, out int n) // [a, b)
            {
                a = Mathf.Clamp(a, 0, len); b = Mathf.Clamp(b, 0, len);
                n = b > a ? cnt[b] - cnt[a] : 0;
                return n > 0 ? (pre[b] - pre[a]) / n : Color.clear;
            }
            for (int i = 0; i < len; i++)
            {
                if (!Ok(i, L)) continue;
                float best = 0; Color bi = default, bo = default;
                foreach (int hw in halfW)
                {
                    var cin = Mean(i - hw, i + hw + 1, out int nin);
                    var cl = Mean(i - hw - gap - side, i - hw - gap, out int nl);
                    var cr = Mean(i + hw + gap + 1, i + hw + gap + 1 + side, out int nr);
                    if (nin < hw + 1 || nl < side / 2 || nr < side / 2) continue;
                    float dl = Dist(cin, cl), dr = Dist(cin, cr);
                    float rsp = Mathf.Min(dl, dr) - 0.5f * Dist(cl, cr); // a band, not an edge between two areas
                    if (rsp > best) { best = rsp; bi = cin; bo = (cl + cr) * 0.5f; }
                }
                resp[L * len + i] = best; inner[L * len + i] = bi; outer[L * len + i] = bo;
            }
        }
        // runs along each "across" position: lines where the response is above thr, small gaps allowed
        var runs = new List<Stripe>();
        int minRun = Mathf.CeilToInt(minLen / Px);
        for (int i = 0; i < len; i++)
        {
            int start = -1, lastOn = -1, n = 0; float sum = 0; Color ci = default, co = default;
            for (int L = 0; L <= lines; L++)
            {
                bool on = L < lines && resp[L * len + i] > thr && LocalMax(resp, len, i, L);
                if (on)
                {
                    if (start < 0) { start = L; n = 0; sum = 0; ci = co = Color.clear; }
                    lastOn = L; n++; sum += resp[L * len + i]; ci += inner[L * len + i]; co += outer[L * len + i];
                }
                else if (start >= 0 && (L >= lines || L - lastOn > 4))
                {
                    if (lastOn - start + 1 >= minRun && n >= (lastOn - start + 1) * 0.6f)
                    {
                        float across = (vertical ? s.rect.xMin : s.rect.yMin) + (i + 0.5f) * Px;
                        float a0 = (vertical ? s.rect.yMin : s.rect.xMin) + start * Px, a1 = (vertical ? s.rect.yMin : s.rect.xMin) + (lastOn + 1) * Px;
                        runs.Add(new Stripe { s = s, vertical = vertical, pos = across, from = a0, to = a1, strength = sum / n, color = ci / n, background = co / n });
                    }
                    start = -1;
                }
            }
        }
        // one stripe per band: neighbours within 4 cm of a stronger one with an overlapping run go
        runs.Sort((a, b) => b.strength.CompareTo(a.strength));
        var keep = new List<Stripe>();
        foreach (var r in runs)
            if (!keep.Any(k => Mathf.Abs(k.pos - r.pos) < 0.04f && Overlap(k, r) > 0.3f * Mathf.Min(k.to - k.from, r.to - r.from))) keep.Add(r);
        return keep;
    }

    static bool LocalMax(float[] resp, int len, int i, int L)
    {
        float v = resp[L * len + i];
        return (i == 0 || resp[L * len + i - 1] <= v) && (i == len - 1 || resp[L * len + i + 1] <= v)
               || (i > 1 && resp[L * len + i - 2] <= v && i < len - 2 && resp[L * len + i + 2] <= v);
    }

    static float Dist(Color a, Color b) { float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b; return Mathf.Sqrt(dr * dr + dg * dg + db * db); }
    static float Overlap(Stripe a, Stripe b) => Mathf.Min(a.to, b.to) - Mathf.Max(a.from, b.from);

    /// <summary>Level stripes repeating every ~25 cm are the mortar bed joints of the blocks, not routes.</summary>
    static void DropJoints(List<Stripe> list, bool masonry)
    {
        var level = masonry ? list.Where(x => !x.vertical).ToList() : new List<Stripe>();
        var drop = new HashSet<Stripe>();
        foreach (var a in level)
        {
            int family = level.Count(b => b != a && Overlap(a, b) > 0.2f && IsCourse(Mathf.Abs(a.pos - b.pos)));
            if (family >= 2) drop.Add(a);
        }
        // many parallel stripes close together = a texture (curtain folds, a stack of boards, a pallet), not routes;
        // a few side by side (cold + hot + circulation) stay
        foreach (var a in list)
            if (list.Count(b => b != a && b.vertical == a.vertical && Mathf.Abs(a.pos - b.pos) < 0.4f && Overlap(a, b) > 0.2f) >= 4) drop.Add(a);
        list.RemoveAll(drop.Contains);
    }

    static bool IsCourse(float d)
    {
        foreach (float c in new[] { 0.25f, 0.50f, 0.75f }) if (Mathf.Abs(d - c) < 0.025f) return true;
        return false;
    }

    // ---------- merging & kinds ----------

    static List<Stripe> Merge(List<Stripe> all)
    {
        var res = new List<Stripe>();
        foreach (var g in all.GroupBy(x => (x.s.anchor, x.vertical)))
        {
            var items = g.OrderByDescending(x => x.strength).ToList();
            var clusters = new List<Stripe>();
            foreach (var st in items)
            {
                var c = clusters.FirstOrDefault(k => Mathf.Abs(k.pos - st.pos) < 0.05f && Overlap(k, st) > -0.15f);
                if (c == null) { clusters.Add(new Stripe { s = st.s, vertical = st.vertical, pos = st.pos, from = st.from, to = st.to, strength = st.strength, color = st.color, background = st.background }); continue; }
                c.from = Mathf.Min(c.from, st.from); c.to = Mathf.Max(c.to, st.to);
                float wgt = c.photos;
                c.pos = (c.pos * wgt + st.pos) / (wgt + 1);
                c.color = (c.color * wgt + st.color) / (wgt + 1); c.background = (c.background * wgt + st.background) / (wgt + 1);
                c.strength = Mathf.Max(c.strength, st.strength);
                c.photos++;
            }
            res.AddRange(clusters);
        }
        return res;
    }

    static void Classify(Stripe st)
    {
        Color.RGBToHSV(st.color, out float hh, out float s, out float v);
        Color.RGBToHSV(st.background, out _, out float sb, out float vb);
        float hue = hh * 360f;
        RouteKind k; float conf;
        if (hue > 185 && hue < 250 && s > 0.25f) { k = RouteKind.ColdWater; conf = 0.7f; }
        else if ((hue > 340 || hue < 8) && s > 0.45f && v > 0.3f) { k = RouteKind.HotWater; conf = 0.55f; }
        else if (hue >= 14 && hue <= 35 && s > 0.65f && v > 0.6f) { k = RouteKind.Waste; conf = 0.4f; }
        else if (s < 0.22f && v > vb + 0.05f && sb > s + 0.08f) { k = RouteKind.Electric; conf = 0.5f; }  // plaster in the chase, masonry around
        else if (s < 0.25f && v < 0.35f) { k = RouteKind.Electric; conf = 0.35f; }                        // dark conduit / cable
        else { k = RouteKind.Other; conf = 0.25f; }
        float len = st.to - st.from;
        conf *= Mathf.Clamp01(0.5f + 0.25f * st.photos + 0.3f * (len - 0.35f)) * Mathf.Clamp01(st.strength / 0.2f + 0.3f);
        st.kind = k; st.conf = Mathf.Clamp01(conf);
    }

    // ---------- debug ----------

    static void WriteDebug(string path, Color[] img, bool[] ok, int w, int h, List<Stripe> found)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        var px = new Color[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = ok[i] ? img[i] : new Color(0.2f, 0f, 0.25f);
        foreach (var st in found)
        {
            var r = st.s.rect;
            int a = Mathf.RoundToInt((st.pos - (st.vertical ? r.xMin : r.yMin)) / Px);
            int f0 = Mathf.RoundToInt((st.from - (st.vertical ? r.yMin : r.xMin)) / Px), f1 = Mathf.RoundToInt((st.to - (st.vertical ? r.yMin : r.xMin)) / Px);
            for (int t = f0; t < f1; t++)
                for (int d = -1; d <= 1; d++)
                {
                    int x = st.vertical ? a + d + 4 : t, y = st.vertical ? t : a + d + 4; // drawn 4 cm beside the stripe
                    if (x >= 0 && x < w && y >= 0 && y < h) px[y * w + x] = Color.magenta;
                }
        }
        tex.SetPixels(px); tex.Apply();
        try { File.WriteAllBytes(path, tex.EncodeToPNG()); } catch { }
        UnityEngine.Object.Destroy(tex);
    }
#endif
}
