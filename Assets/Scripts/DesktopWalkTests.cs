using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Scripted checks of the walk-through in the desktop app, so behaviour can be verified without a headset:
///   XRHouseWalk.exe -scan &lt;name&gt; -test &lt;views|doors|windows|stairs|fall&gt; -out &lt;folder&gt; [-views "x,y,z,yaw,pitch;..."]
/// views: camera at the given scan-frame points (y = floor height), one PNG each. doors / windows: a PNG from in front
/// of every door with a set-up leaf / of every window. stairs: walks up every stair along its walking line and logs
/// the height (TRACE lines). fall: walks from the upper floor into the first drawn opening and logs the height.
/// Each writes TEST lines to the log and quits (exit 0 = passed).
/// </summary>
public partial class DesktopWalkApp
{
    const float EyeHeight = 1.65f;

    async Task<bool> RunTests()
    {
        string test = Arg("-test");
        if (test == null) return false;
        string outDir = Arg("-out") ?? Path.Combine(Application.persistentDataPath, "Tests");
        Directory.CreateDirectory(outDir);
        bool ok = false;
        try
        {
            if (!walk.IsOn) { Debug.Log("TEST FAILED: no walk (scan not loaded)"); }
            else
            {
                SetMenu(false);
                rig.InputEnabled = false;
                ok = test switch
                {
                    "views" => await TestViews(outDir),
                    "doors" => await TestOpenings(outDir, windows: false),
                    "windows" => await TestOpenings(outDir, windows: true),
                    "stairs" => await TestStairs(outDir),
                    "fall" => await TestFall(outDir),
                    "holes" => await TestHoles(outDir),
                    "doorinfo" => DumpDoors(),
                    "roomplan" => DumpRoomPlan(Arg("-room")),
                    _ => false,
                };
            }
        }
        catch (Exception ex) { Debug.LogException(ex); }
        Debug.Log($"TEST {test}: {(ok ? "PASSED" : "FAILED")}");
        await Frames(3);
        Application.Quit(ok ? 0 : 3);
        return true;
    }

    static async Task Frames(int n)
    {
        int f0 = Time.frameCount;
        while (Time.frameCount - f0 < n) await Task.Yield();
    }

    static float Yaw(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

    async Task Shot(string path)
    {
        await Frames(15);
        ScreenCapture.CaptureScreenshot(path);
        await Frames(3);
        Debug.Log($"TEST shot {path}");
    }

    /// <summary>Stands at a scan-frame floor point looking along yaw/pitch.</summary>
    async Task StandAt(Vector3 floor, float yawDeg, float pitchDeg)
    {
        walk.PlaceAtScanPoint(floor);
        rig.SetView(yawDeg, pitchDeg);
        await Frames(5);
    }

    async Task<bool> TestViews(string outDir)
    {
        var list = (Arg("-views") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        if (Environment.GetCommandLineArgs().Contains("-opendoors")) { walk.SetAllDoors(true); await Frames(90); } // swing time
        // -glbviews "x,y,z,tx,tz,pitch;..." - the furniture spec's frame (GLB axes: model space, Z mirrored): stand at
        // floor point (x,y,z), look towards (tx,tz).
        if (Arg("-glbviews") != null && walk.TryGetModelFrame(out _, out float yw, out var ctr))
        {
            Quaternion gi = Quaternion.Inverse(Quaternion.Euler(0, yw, 0));
            foreach (var v in Arg("-glbviews").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = v.Split(',').Select(s => float.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
                Vector3 pos = gi * new Vector3(f[0], f[1], -f[2]) + ctr;
                Vector3 dir = gi * new Vector3(f[3] - f[0], 0f, -(f[4] - f[2]));
                await StandAt(pos, Yaw(dir), f.Length > 5 ? f[5] : 15f);
                await Shot(Path.Combine(outDir, $"view_{i++}.png"));
            }
            return i > 0;
        }
        foreach (var v in list)
        {
            var f = v.Split(',').Select(s => float.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
            await StandAt(new Vector3(f[0], f[1], f[2]), f.Length > 3 ? f[3] : 0f, f.Length > 4 ? f[4] : 0f);
            await Shot(Path.Combine(outDir, $"view_{i++}.png"));
        }
        return i > 0;
    }

    static List<MRUKRoom> Rooms() => MRUK.Instance != null ? MRUKDataProcessor.GetValidRooms(MRUK.Instance) : new List<MRUKRoom>();

    /// <summary>A shot of each door with a leaf (or each window) from 1.6 m in front, on the side it opens to.</summary>
    async Task<bool> TestOpenings(string outDir, bool windows)
    {
        var rooms = Rooms();
        int i = 0;
        foreach (var d in DoorCatalog.Build(rooms).Where(x => x.isWindow == windows))
        {
            var e = DoorCatalog.FindEdit(HouseEditsStore.Current, d, rooms);
            if (!windows && e == null) continue;
            // Stand in a room the opening belongs to: the front side if it is a room, else the back.
            Vector3 n = d.roomFront != null ? d.normal : -d.normal;
            Vector3 eye = d.center + n * 1.6f;
            Vector3 floor = new Vector3(eye.x, d.FloorY, eye.z);
            await StandAt(floor, Yaw(-n), windows ? 0f : 5f);
            Debug.Log($"TEST {(windows ? "window" : "door")} {i}: {d.uuid.Substring(0, 8)} kind={(e != null ? e.kind.ToString() : "-")} w={d.width:0.00} h={d.height:0.00}");
            await Shot(Path.Combine(outDir, $"{(windows ? "window" : "door")}_{i++}.png"));
            if (i >= 12) break;
        }
        return i > 0;
    }

    float FeetY() => walk.TryGetScanWorldPose(out var p, out _) ? p.y - EyeHeight : float.NaN;

    /// <summary>Walks every stair along its walking line (bottom, flight ends, landing middles, top) and checks the
    /// feet end up on the floor above.</summary>
    async Task<bool> TestStairs(string outDir)
    {
        var rooms = Rooms();
        bool all = true;
        int si = 0;
        foreach (var s in HouseEditsStore.Current.stairs)
        {
            var r = StairGeometry.Resolve(s, rooms);
            if (r == null) continue;
            var path = new List<Vector3>();
            foreach (var (start, end, dir, risers, y0) in r.flights) { path.Add(start); path.Add(end); }
            Vector3 d0 = r.flights[0].dir;
            path.Add(path[path.Count - 1] + r.flights[r.flights.Count - 1].dir * 0.7f); // step off onto the upper floor
            await StandAt(r.points[0] - d0 * 0.8f, Yaw(d0), 10f);
            await Shot(Path.Combine(outDir, $"stair{si}_start.png"));
            Debug.Log($"TEST stair {si}: bottom {r.bottomY:0.00} top {r.topY:0.00} flights {r.flights.Count} rise {r.rise * 100f:0.0} cm width {r.width:0.00}");
            bool reached = await FollowPath(path, 30f, $"stair{si}");
            float feet = FeetY();
            bool pass = reached && Mathf.Abs(feet - r.topY) < 0.15f;
            Debug.Log($"TEST stair {si}: reached={reached} feet={feet:0.00} expected {r.topY:0.00} -> {(pass ? "OK" : "FAIL")}");
            await Shot(Path.Combine(outDir, $"stair{si}_end.png"));
            all &= pass;
            si++;
        }
        return si > 0 && all;
    }

    /// <summary>Steers the forced forward walk through the points in turn (scan frame, horizontal); logs a TRACE line
    /// every 0.25 s. False if it got stuck (no progress for 2 s) or ran out of time.</summary>
    async Task<bool> FollowPath(List<Vector3> pts, float timeout, string tag)
    {
        float t0 = Time.realtimeSinceStartup, lastLog = -1f, lastProgress = t0;
        Vector3 lastPos = Vector3.zero;
        int k = 0;
        rig.ForcedMove = new Vector2(0f, 1f);
        try
        {
            while (k < pts.Count && Time.realtimeSinceStartup - t0 < timeout)
            {
                await Task.Yield();
                if (!walk.TryGetScanWorldPose(out var p, out _)) return false;
                Vector3 to = pts[k] - p; to.y = 0f;
                if (to.magnitude < 0.2f) { k++; continue; }
                rig.SetView(Yaw(to), 10f);
                float now = Time.realtimeSinceStartup;
                if ((p - lastPos).magnitude > 0.05f) { lastPos = p; lastProgress = now; }
                if (now - lastProgress > 2f) { Debug.Log($"TRACE {tag} STUCK at ({p.x:0.00},{p.y - EyeHeight:0.00},{p.z:0.00}) heading to point {k} ({pts[k].x:0.00},{pts[k].y:0.00},{pts[k].z:0.00}); blocked by {walk.LastBlocker}"); return false; }
                if (now - lastLog >= 0.25f)
                {
                    lastLog = now;
                    Debug.Log($"TRACE {tag} t={now - t0:0.00} feet=({p.x:0.00},{p.y - EyeHeight:0.00},{p.z:0.00}) -> point {k}/{pts.Count}");
                }
            }
            return k >= pts.Count;
        }
        finally { rig.ForcedMove = null; }
    }

    /// <summary>Every doorway: its anchors (one per room that captured it), position, size, the edit it maps to, and
    /// what each single anchor maps to on its own and whether it counts as walled up.</summary>
    bool DumpDoors()
    {
        var all = MRUK.Instance.Rooms;
        var rooms = Rooms();
        var edits = HouseEditsStore.Current;
        foreach (var d in DoorCatalog.Build(rooms).OrderBy(x => x.center.y).ThenBy(x => x.center.x))
        {
            var e = DoorCatalog.FindEdit(edits, d, rooms);
            Debug.Log($"TEST doorway {d.uuid.Substring(0, 8)} {(d.isWindow ? "WINDOW" : "DOOR")} c=({d.center.x:0.00},{d.center.y:0.00},{d.center.z:0.00}) " +
                      $"right=({d.right.x:0.00},{d.right.z:0.00}) w={d.width:0.00} h={d.height:0.00} anchors={d.anchors.Count} edit={(e != null ? e.kind + " " + Short(e.anchorUuid) : "-")} " +
                      $"front={NameOf(d.roomFront)} back={NameOf(d.roomBack)}");
            foreach (var a in d.anchors)
            {
                var ae = DoorCatalog.FindEdit(edits, a, all);
                DoorCatalog.Frame(a, out var c, out _, out _, out float w, out float h);
                Debug.Log($"TEST    anchor {a.Anchor.Uuid.ToString().Substring(0, 8)} room={NameOf(a.Room)} c=({c.x:0.00},{c.y:0.00},{c.z:0.00}) w={w:0.00} " +
                          $"ownEdit={(ae != null ? ae.kind + " " + Short(ae.anchorUuid) : "-")} walledUp={DoorCatalog.IsWalledUp(a)}");
            }
        }
        foreach (var e in edits.doors)
            Debug.Log($"TEST edit {e.kind} anchor={Short(e.anchorUuid)} room={Short(e.roomUuid)} local=({e.localPos?.x:0.00},{e.localPos?.y:0.00},{e.localPos?.z:0.00})");
        return true;
    }

    /// <summary>A room in the furniture spec's frame (GLB axes: model space, Z mirrored): its floor box, and every door
    /// and window of it as wall side ("-x"/"+x"/"-z"/"+z") + "along" range + sill/top heights above the floor.</summary>
    bool DumpRoomPlan(string key)
    {
        if (!walk.TryGetModelFrame(out _, out float yaw, out var center)) return false;
        Quaternion g = Quaternion.Euler(0, yaw, 0);
        var rooms = Rooms();
        foreach (var room in rooms.Where(r => key == null || DoorCatalog.RoomId(r).StartsWith(key, StringComparison.OrdinalIgnoreCase)))
        {
            float xmin = float.MaxValue, xmax = float.MinValue, zmin = float.MaxValue, zmax = float.MinValue, y = float.MaxValue;
            var poly = new List<string>();
            foreach (var f in room.FloorAnchors)
                foreach (var p2 in f.PlaneBoundary2D)
                {
                    Vector3 m = g * (f.transform.TransformPoint(new Vector3(p2.x, p2.y, 0f)) - center);
                    xmin = Mathf.Min(xmin, m.x); xmax = Mathf.Max(xmax, m.x); zmin = Mathf.Min(zmin, -m.z); zmax = Mathf.Max(zmax, -m.z); y = Mathf.Min(y, m.y);
                    poly.Add($"({m.x:0.00},{-m.z:0.00})");
                }
            Debug.Log($"TEST room {NameOf(room)}: x {xmin:0.00}..{xmax:0.00} ({xmax - xmin:0.00}) z {zmin:0.00}..{zmax:0.00} ({zmax - zmin:0.00}) floor y {y:0.00} poly {string.Join(" ", poly)}");
            foreach (var d in DoorCatalog.Build(rooms).Where(d => d.anchors.Any(a => a.Room == room)))
            {
                Vector3 c = g * (d.center - center), r = g * d.right;
                float cx = c.x, cz = -c.z;
                string side = Mathf.Abs(r.x) > Mathf.Abs(r.z)
                    ? (Mathf.Abs(cz - zmin) < Mathf.Abs(cz - zmax) ? "-z" : "+z")
                    : (Mathf.Abs(cx - xmin) < Mathf.Abs(cx - xmax) ? "-x" : "+x");
                float along = side == "-z" || side == "+z" ? cx - xmin : cz - zmin;
                float sill = c.y - d.height / 2f - y;
                Debug.Log($"TEST    {(d.isWindow ? "window" : "door")} wall {side} along {along - d.width / 2f:0.00}..{along + d.width / 2f:0.00} sill {sill:0.00} top {sill + d.height:0.00} (w {d.width:0.00})");
            }
        }
        return true;
    }

    static string Short(string s) => string.IsNullOrEmpty(s) ? "-" : s.Substring(0, Math.Min(8, s.Length));

    static string NameOf(MRUKRoom r) => r == null ? "-" : (RoomNames.TryGet(DoorCatalog.RoomId(r), out var n) ? n : DoorCatalog.RoomId(r).Substring(0, 4).ToUpperInvariant());

    /// <summary>Every drawn opening seen from below (looking up) and from the floor above (looking down into it),
    /// with a list of every renderer whose bounds reach into the opening - there should be none but the stairs.</summary>
    async Task<bool> TestHoles(string outDir)
    {
        var rooms = Rooms();
        int i = 0;
        foreach (var h in HouseEditsStore.Current.holes)
        {
            if (!StairGeometry.HoleWorld(h, rooms, out var a, out var b)) continue;
            Vector3 c = (a + b) / 2f;
            float upper = StairGeometry.TopFloorY(rooms, c.y - 1.5f);
            if (Mathf.Abs(upper - c.y) > 1.2f) upper = c.y;
            float lower = rooms.SelectMany(r => r.FloorAnchors).Where(f => f != null).Select(f => f.transform.position.y)
                .Where(y => y < upper - 1.5f).DefaultIfEmpty(upper - 3f).Max();
            Vector3 half = (b - a) / 2f; half.y = 0f;
            Vector3 dir = Mathf.Abs(half.x) < Mathf.Abs(half.z) ? new Vector3(Mathf.Sign(half.x), 0, 0) : new Vector3(0, 0, Mathf.Sign(half.z));
            float reach = Mathf.Abs(Vector3.Dot(half, dir));
            Debug.Log($"TEST hole {i}: centre ({c.x:0.00},{c.y:0.00},{c.z:0.00}) size {Mathf.Abs(b.x - a.x):0.00} x {Mathf.Abs(b.z - a.z):0.00} upper {upper:0.00} lower {lower:0.00}");

            await StandAt(new Vector3(c.x, lower, c.z), 0f, -75f);
            ListRenderersInHole(a, b, upper);
            await Shot(Path.Combine(outDir, $"hole{i}_below.png"));
            await StandAt(new Vector3(c.x, upper, c.z) - dir * (reach + 0.8f), Yaw(dir), 45f);
            await Shot(Path.Combine(outDir, $"hole{i}_above.png"));
            i++;
        }
        return i > 0;
    }

    void ListRenderersInHole(Vector3 a, Vector3 b, float upperY)
    {
        if (!walk.TryGetModelFrame(out var root, out float yaw, out var center)) return;
        Quaternion g = Quaternion.Euler(0, yaw, 0), gi = Quaternion.Inverse(g);
        float x0 = Mathf.Min(a.x, b.x) + 0.1f, x1 = Mathf.Max(a.x, b.x) - 0.1f, z0 = Mathf.Min(a.z, b.z) + 0.1f, z1 = Mathf.Max(a.z, b.z) - 0.1f;
        foreach (var r in root.GetComponentsInChildren<Renderer>())
        {
            if (!r.enabled) continue;
            // Sample the opening on a grid: a renderer counts if its mesh has a horizontal triangle over it there.
            var mf = r.GetComponent<MeshFilter>();
            if (!mf || !mf.sharedMesh) continue;
            var bnd = r.bounds;
            int hits = 0;
            for (float x = x0; x <= x1; x += 0.25f)
                for (float z = z0; z <= z1; z += 0.25f)
                {
                    // scan point at the slab height -> world
                    Vector3 w = root.TransformPoint(g * (new Vector3(x, upperY - 0.15f, z) - center));
                    if (bnd.Contains(w)) hits++;
                }
            if (hits > 0) Debug.Log($"TEST hole renderer: {r.name} parent={r.transform.parent?.name} mat={r.sharedMaterial?.name} shader={r.sharedMaterial?.shader?.name} hits={hits}");
        }
        // Straight down through the opening: the first thing hit should be the floor below (or the stairs).
        var hitNames = new Dictionary<string, int>();
        for (float x = x0; x <= x1; x += 0.2f)
            for (float z = z0; z <= z1; z += 0.2f)
            {
                Vector3 w = root.TransformPoint(g * (new Vector3(x, upperY + 1.0f, z) - center));
                string key;
                if (Physics.Raycast(w, Vector3.down, out var hit, 10f, 1 << 2, QueryTriggerInteraction.Ignore))
                {
                    float y = (gi * root.InverseTransformPoint(hit.point) + center).y;
                    key = $"{hit.collider.name}/{hit.collider.transform.parent?.name} y~{Mathf.Round(y * 10f) / 10f:0.0}";
                }
                else key = "nothing";
                hitNames[key] = hitNames.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        foreach (var kv in hitNames.OrderByDescending(k => k.Value)) Debug.Log($"TEST hole ray down: {kv.Key} x{kv.Value}");
        // ... and every renderer (also ones without colliders) crossing the slab level inside the opening.
        foreach (var r in root.GetComponentsInChildren<Renderer>())
        {
            if (!r.enabled || r.sharedMaterial == null) continue;
            string sh = r.sharedMaterial.shader.name;
            if (!sh.Contains("Sprites") && !sh.Contains("Transparent") && r.sharedMaterial.color.a > 0.99f) continue;
            Debug.Log($"TEST transparent renderer: {r.name} parent={r.transform.parent?.name} mat={r.sharedMaterial.name} a={r.sharedMaterial.color.a:0.00} centre y~{(gi * root.InverseTransformPoint(r.bounds.center) + center).y:0.0}");
        }
    }

    /// <summary>From the floor above, walks into the first drawn opening: the feet must end up on the story below.</summary>
    async Task<bool> TestFall(string outDir)
    {
        var rooms = Rooms();
        var h = HouseEditsStore.Current.holes.FirstOrDefault();
        if (h == null || !StairGeometry.HoleWorld(h, rooms, out var a, out var b)) { Debug.Log("TEST fall: no opening"); return false; }
        Vector3 c = (a + b) / 2f;
        float upper = StairGeometry.TopFloorY(rooms, c.y - 1.5f);         // a hole drawn on the ceiling sits ~0.3 below the floor above
        if (Mathf.Abs(upper - c.y) > 1.2f) upper = c.y;                   // drawn on the floor from above
        float lower = rooms.SelectMany(r => r.FloorAnchors).Where(f => f != null).Select(f => f.transform.position.y)
            .Where(y => y < upper - 1.5f).DefaultIfEmpty(upper - 3f).Max();
        // Start 1 m outside one of the opening's edges, on real floor with no wall between it and the middle.
        Vector3 half = (b - a) / 2f; half.y = 0f;
        Vector3 start = Vector3.zero, dir = Vector3.zero;
        bool found = false;
        foreach (var d in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
        {
            float reach = Mathf.Abs(Vector3.Dot(half, d));
            Vector3 s = new Vector3(c.x, upper, c.z) - d * (reach + 1.0f);
            await StandAt(s, Yaw(d), 25f);
            if (!walk.TryGetModelFrame(out var root, out float yw, out var ctr)) break;
            Quaternion g = Quaternion.Euler(0, yw, 0);
            Vector3 W(Vector3 p) => root.TransformPoint(g * (p - ctr));
            bool floor = Physics.Raycast(W(s + Vector3.up * 0.5f), Vector3.down, 0.8f, 1 << 2, QueryTriggerInteraction.Ignore);
            bool clear = !Physics.Linecast(W(s + Vector3.up * 1.0f), W(new Vector3(c.x, upper + 1.0f, c.z)), 1 << 2, QueryTriggerInteraction.Ignore);
            Debug.Log($"TEST fall: start candidate {d}: floor={floor} clear={clear}");
            if (floor && clear) { start = s; dir = d; found = true; break; }
        }
        if (!found) { Debug.Log("TEST fall: no usable start point"); return false; }
        await StandAt(start, Yaw(dir), 25f);
        await Frames(30);
        float before = FeetY();
        Debug.Log($"TEST fall: standing at start, feet {before:0.00} (must stay ~{upper:0.00})");
        if (before < upper - 0.3f) { Debug.Log("TEST fall: FELL AT THE START (outside the opening)"); return false; }
        await Shot(Path.Combine(outDir, "fall_start.png"));
        Debug.Log($"TEST fall: opening at ({c.x:0.00},{c.y:0.00},{c.z:0.00}) upper floor {upper:0.00} lower floor {lower:0.00}");
        await FollowPath(new List<Vector3> { new Vector3(c.x, upper, c.z) }, 8f, "fall");
        await Frames(90); // let a fall finish
        float feet = FeetY();
        bool pass = feet < lower + 0.3f;
        Debug.Log($"TEST fall: feet={feet:0.00} expected about {lower:0.00} -> {(pass ? "OK" : "FAIL")}");
        await Shot(Path.Combine(outDir, "fall_end.png"));
        return pass;
    }
}
