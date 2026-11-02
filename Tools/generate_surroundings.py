#!/usr/bin/env python3
"""Generates the house's surroundings (sloped garden, driveway, barn, trees/hedges, terrace+pool, the car's start
point) as the "site" block of a saved scan's 95_Data_Furniture.json, from the scan's own real geometry (the garage
door position, the kitchen's exterior corner) plus the relative layout described from the aerial photo. The block
is pure data - SiteModel.cs (static geometry) and CarDrive.cs ("car_start") already render/use it; this script
writes no Unity code.

    python3 Tools/generate_surroundings.py "Exports/RoomData/<scan folder>"

Needs that scan's rooms named GARAGE and (for the terrace/pool corner) KITCHEN first - Data tab > scan list > the
room-naming UI in the app, or 94_Data_RoomNames.json already present (pulled from the headset). Merges into any
existing 95_Data_Furniture.json (keeps "items" etc.), and never overwrites a "site" that already has a "driveway_v1"
marker without --force, so re-running after the user has nudged things in the app doesn't clobber their changes.

Coordinates are GLB axes (metres, y up) - the same frame SiteModel/Furniture already use for "site".
"""
import json, math, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import extract_scan_geometry as esg


def load_room_names(scan_dir):
    path = os.path.join(scan_dir, "94_Data_RoomNames.json")
    if not os.path.exists(path):
        return {}
    try:
        store = json.load(open(path, encoding="utf-8"))
        return dict(zip(store.get("keys", []), store.get("values", [])))
    except Exception:
        return {}


def sub(a, b): return (a[0] - b[0], a[1] - b[1])
def add(a, b): return (a[0] + b[0], a[1] + b[1])
def scale(a, k): return (a[0] * k, a[1] * k)
def length(a): return math.hypot(a[0], a[1])
def norm(a):
    l = length(a)
    return (a[0] / l, a[1] / l) if l > 1e-9 else (0.0, 0.0)
def lerp(a, b, t): return a + (b - a) * t


def wall_outward_normal(w, floor_centroid):
    a, b = w["a"], w["b"]
    d = norm(sub(b, a))
    n = (-d[1], d[0])
    mid = scale(add(a, b), 0.5)
    # points away from the room's own floor
    return n if (n[0] * (mid[0] - floor_centroid[0]) + n[1] * (mid[1] - floor_centroid[1])) >= 0 else (-n[0], -n[1])


def floor_centroid(room):
    pts = room.get("floor") or []
    if not pts:
        # fall back to the average of the wall midpoints
        pts = [scale(add(w["a"], w["b"]), 0.5) for w in room["walls"]]
    return (sum(p[0] for p in pts) / len(pts), sum(p[1] for p in pts) / len(pts))


def is_exterior(room, wall, all_rooms, tol=0.35):
    """No other room has a wall close to the same line (shared walls are interior)."""
    mid = scale(add(wall["a"], wall["b"]), 0.5)
    for other in all_rooms:
        if other is room:
            continue
        for w2 in other["walls"]:
            m2 = scale(add(w2["a"], w2["b"]), 0.5)
            if length(sub(mid, m2)) < tol:
                return False
    return True


def find_room(rooms, names, want):
    for r in rooms:
        if names.get(r["id"], "").upper() == want:
            return r
    return None


def garage_door(rooms, garage):
    """(x, z) centre and outward normal (x, z) of the garage's door to the drive."""
    fc = floor_centroid(garage)
    ext_walls = [w for w in garage["walls"] if is_exterior(garage, w, rooms)]
    if not ext_walls:
        ext_walls = garage["walls"]
    # a real garage door, if the scan has one wide enough to be it
    wide_doors = [d for d in garage["doors"] if d.get("width", 0) > 1.8]
    if wide_doors:
        d = max(wide_doors, key=lambda d: d["width"])
        w = min(ext_walls, key=lambda w: length(sub(scale(add(w["a"], w["b"]), 0.5), tuple(d["center"][::2]))))
        return tuple(d["center"][::2]), wall_outward_normal(w, fc)
    w = max(ext_walls, key=lambda w: length(sub(w["a"], w["b"])))
    return scale(add(w["a"], w["b"]), 0.5), wall_outward_normal(w, fc)


def opposite_exterior_point(rooms, room, away_from_normal):
    """The exterior wall of `room` facing most opposite to away_from_normal - the garden/kitchen side."""
    fc = floor_centroid(room)
    ext = [w for w in room["walls"] if is_exterior(room, w, rooms)] or room["walls"]
    best = min(ext, key=lambda w: wall_outward_normal(w, fc)[0] * away_from_normal[0] + wall_outward_normal(w, fc)[1] * away_from_normal[1])
    return scale(add(best["a"], best["b"]), 0.5), wall_outward_normal(best, fc)


def rounded_rect(cx, cz, w, d, r, n=6):
    """A rounded-rectangle polygon (w x d, corner radius r), n points per corner, centred at (cx, cz)."""
    hw, hd = w / 2 - r, d / 2 - r
    corners = [(hw, hd, 0), (-hw, hd, 90), (-hw, -hd, 180), (hw, -hd, 270)]
    pts = []
    for ox, oz, a0 in corners:
        for i in range(n + 1):
            a = math.radians(a0 + i * 90 / n)
            pts.append([cx + ox + r * math.cos(a), cz + oz + r * math.sin(a)])
    return pts


def band(p0, p1, half_w, y0, y1, n=1):
    """One or more quad "areas" (as an s-curve control segment) from p0 to p1, half_w wide, sloped y0->y1."""
    d = norm(sub(p1, p0))
    nrm = (-d[1], d[0])
    a, b = add(p0, scale(nrm, half_w)), sub(p0, scale(nrm, half_w))
    c, e = sub(p1, scale(nrm, half_w)), add(p1, scale(nrm, half_w))
    return [[a[0], y0, a[1]], [b[0], y0, b[1]], [c[0], y1, c[1]], [e[0], y1, e[1]]]


def s_curve(p_start, p_end, bulge, n):
    """n+1 points of a gentle S from p_start to p_end, bulging sideways by `bulge` (metres) then back."""
    d = sub(p_end, p_start)
    perp = norm((-d[1], d[0]))
    pts = []
    for i in range(n + 1):
        t = i / n
        side = math.sin(t * math.pi) * math.sin(t * math.pi * 2) * bulge  # one S wiggle
        p = add(p_start, scale(d, t))
        pts.append(add(p, scale(perp, side)))
    return pts


def build_site(scan_dir, out_garage=None, out_kitchen=None):
    scan_path = os.path.join(scan_dir, "92_Data_Scan.scene.json")
    data = esg.extract(scan_path)
    rooms = data["rooms"]
    names = load_room_names(scan_dir)

    garage = find_room(rooms, names, "GARAGE")
    if garage is None:
        raise SystemExit("No room named GARAGE in this scan (name it in the app - Data tab - first; "
                          "94_Data_RoomNames.json must exist next to 92_Data_Scan.scene.json).")
    kitchen = find_room(rooms, names, "KITCHEN")

    doorXZ, outN = garage_door(rooms, garage)
    houseFloorY = min((r.get("floorY") for r in rooms if r.get("floorY") is not None), default=0.0)

    # ---- the slope: 0.30 m below the floor at the garage/top side, 1.25 m below under the living room / garden
    # side, continuing down at the same grade through the whole garden (per the user's measurements).
    gardenPt, gardenN = opposite_exterior_point(rooms, kitchen or garage, outN)
    slopeDir = norm(sub(gardenPt, doorXZ))
    slopeLen = max(1.0, length(sub(gardenPt, doorXZ)))
    grade = (1.25 - 0.30) / slopeLen  # metres of drop per metre of horizontal run

    def ground_y(p):
        run = (p[0] - doorXZ[0]) * slopeDir[0] + (p[1] - doorXZ[1]) * slopeDir[1]
        return houseFloorY - 0.30 - grade * run

    # ---- driveway: an S-curve from the barn/road (up near the top, outN side) down to the garage apron
    driveStart = add(doorXZ, scale(outN, 26.0))          # up at the farmstead/road, ~26 m out
    driveStart = add(driveStart, scale((outN[1], -outN[0]), -8.0))  # a bit to the side (the curve's start)
    apron = add(doorXZ, scale(outN, 3.0))
    curvePts = s_curve(driveStart, apron, 6.0, 10)
    drive_w = 3.0
    areas = []
    for i in range(len(curvePts) - 1):
        p0, p1 = curvePts[i], curvePts[i + 1]
        areas.append({"name": "driveway", "poly": band(p0, p1, drive_w / 2, ground_y(p0), ground_y(p1)),
                      "color": "gravel", "t": 0.12})
    # paved apron right at the garage, sloped down into the door
    areas.append({"name": "apron", "poly": band(apron, doorXZ, 2.6, ground_y(apron), houseFloorY - 0.03),
                  "color": "paving", "t": 0.1})

    # ---- sloped lawn: wide bands stepping down the grade, from just above the garage side to well past the garden
    bands_n = 9
    perp = (slopeDir[1], -slopeDir[0])
    lawn_half_w = 45.0
    for i in range(-1, bands_n):
        p0 = add(doorXZ, scale(slopeDir, i * 8.0))
        p1 = add(doorXZ, scale(slopeDir, (i + 1) * 8.0))
        areas.append({"name": "lawn", "poly": band(p0, p1, lawn_half_w, ground_y(p0), ground_y(p1)), "color": "grass", "t": 0.15})
    # the field beyond the garden, to the east (perpendicular to the slope) - flat at the lawn's edge height
    fieldEdge = add(doorXZ, scale(slopeDir, (bands_n - 3) * 8.0))
    fy = ground_y(fieldEdge)
    fp0 = add(fieldEdge, scale(perp, lawn_half_w))
    fp1 = add(fp0, scale(slopeDir, 60.0))
    fp2 = add(fp1, scale(perp, 70.0))
    fp3 = add(fp0, scale(perp, 70.0))
    areas.append({"name": "field", "poly": [[fp0[0], fy, fp0[1]], [fp1[0], fy, fp1[1]],
                                             [fp2[0], fy + 1.5, fp2[1]], [fp3[0], fy + 1.5, fp3[1]]], "color": "field", "t": 0.1})

    # ---- barn (gabled, brick roof) up near where the drive starts
    barnC = add(driveStart, scale(outN, 9.0))
    bw, bd = 12.0, 7.0
    ang = math.atan2(slopeDir[0], slopeDir[1])  # align roughly with the drive's approach
    ca, sa = math.cos(ang), math.sin(ang)
    def R(x, z): return (barnC[0] + x * ca - z * sa, barnC[1] + x * sa + z * ca)
    bx0, bz0 = R(-bw / 2, -bd / 2); bx1, bz1 = R(bw / 2, bd / 2)
    barnY = ground_y(barnC)
    # NOTE: SiteModel's buildings are x0/x1/z0/z1 axis-aligned (GLB axes); approximate the barn's rotation with
    # its bounding box (good enough at this distance/scale - rotate further in-editor if it doesn't line up).
    buildings = [{"name": "barn", "x0": min(bx0, bx1), "x1": max(bx0, bx1), "z0": min(bz0, bz1), "z1": max(bz0, bz1),
                  "eave": barnY + 3.2 - houseFloorY, "ridge": barnY + 5.8 - houseFloorY, "roof": "tiles", "color": "barn"}]

    # ---- trees (a cluster NW of the house, near the barn) and hedge lines (south of the garden, along the field)
    trees = []
    jitter = [(-3, 2), (2, 4), (-6, 6), (4, 8), (-2, 10), (7, 1), (-8, -1), (1, -4)]
    for jx, jz in jitter:
        p = (barnC[0] + jx, barnC[1] + jz)
        trees.append({"at": [p[0], p[1]], "h": 6.5 if (jx + jz) % 2 == 0 else 5.0, "r": 2.0})

    hedgeS0 = add(fieldEdge, scale(perp, -2.0))
    hedgeS1 = add(hedgeS0, scale(slopeDir, 55.0))
    hedgeE0 = fp0
    hedgeE1 = fp1
    hedges = [{"line": [list(hedgeS0), list(hedgeS1)], "w": 1.3, "h": 1.8},
              {"line": [list(hedgeE0), list(hedgeE1)], "w": 1.3, "h": 1.8}]

    # ---- terrace + pool, by the kitchen corner, raised to the living-room floor, with steps down to the garden
    terraceAnchor, terraceN = (gardenPt, gardenN)
    if kitchen is not None:
        fc = floor_centroid(kitchen)
        ext = [w for w in kitchen["walls"] if is_exterior(kitchen, w, rooms)] or kitchen["walls"]
        w = max(ext, key=lambda w: length(sub(w["a"], w["b"])))
        terraceAnchor, terraceN = scale(add(w["a"], w["b"]), 0.5), wall_outward_normal(w, fc)
    tperp = (terraceN[1], -terraceN[0])
    terraceDepth, terraceWidth = 5.0, 7.0
    tC = add(terraceAnchor, scale(terraceN, terraceDepth / 2 + 0.3))
    t_in, t_out = sub(tC, scale(terraceN, terraceDepth / 2)), add(tC, scale(terraceN, terraceDepth / 2))
    poly = [sub(t_in, scale(tperp, terraceWidth / 2)), add(t_in, scale(tperp, terraceWidth / 2)),
            add(t_out, scale(tperp, terraceWidth / 2)), sub(t_out, scale(tperp, terraceWidth / 2))]
    areas.append({"name": "terrace", "poly": [[p[0], houseFloorY, p[1]] for p in poly], "color": "concrete", "t": 0.25})
    # steps down from the terrace's outer edge to garden grade (4 shallow treads)
    stepY0, stepY1 = houseFloorY, ground_y(t_out)
    for i in range(4):
        s0 = add(t_out, scale(terraceN, i * 0.38))
        s1 = add(t_out, scale(terraceN, (i + 1) * 0.38))
        y0 = lerp(stepY0, stepY1, i / 4); y1 = lerp(stepY0, stepY1, (i + 1) / 4)
        areas.append({"name": "step", "poly": band(s0, s1, terraceWidth / 2, y0, y0), "color": "concrete", "t": 0.06})

    # pool: 8x4 m, rounded corners, in the corner of the terrace away from a straight view out of the living room
    poolC = add(t_in, scale(tperp, terraceWidth / 2 - 4.5))
    poolC = add(poolC, scale(terraceN, 2.2))
    poolDeck = {"name": "pool_deck", "poly": rounded_rect(poolC[0], poolC[1], 8.8, 4.8, 1.0), "y": houseFloorY, "t": 0.05, "color": "paving"}
    poolDeck["poly"] = [[p[0], houseFloorY, p[1]] for p in poolDeck["poly"]]
    pool = {"name": "pool", "poly": rounded_rect(poolC[0], poolC[1], 8.0, 4.0, 0.9), "y": houseFloorY - 0.02, "t": 1.3, "color": "water"}
    pool["poly"] = [[p[0], houseFloorY - 0.02, p[1]] for p in pool["poly"]]
    areas.append(poolDeck); areas.append(pool)

    carYawDeg = math.degrees(math.atan2(-slopeDir[0], -slopeDir[1]))  # facing down the drive, toward the garage
    site = {
        "_marker": "driveway_v1",
        "ground": False,
        "ground_y": houseFloorY - 0.30,
        "areas": areas,
        "buildings": buildings,
        "trees": trees,
        "hedges": hedges,
        "car_start": [driveStart[0], driveStart[1], carYawDeg],
    }
    return site, garage["id"], (kitchen["id"] if kitchen else None)


def merge_and_write(scan_dir, site, force):
    path = os.path.join(scan_dir, "95_Data_Furniture.json")
    spec = {}
    if os.path.exists(path):
        try:
            spec = json.load(open(path, encoding="utf-8"))
        except Exception:
            spec = {}
    existing = spec.get("site")
    if existing and existing.get("_marker") == "driveway_v1" and not force:
        raise SystemExit(f"{path} already has generated site data - pass --force to overwrite "
                          "(this will discard any in-app tweaks to it).")
    spec["site"] = site
    tmp = path + ".tmp"
    json.dump(spec, open(tmp, "w", encoding="utf-8"), indent=1)
    if os.path.exists(path):
        os.replace(path, path + ".bak")
    os.replace(tmp, path)
    return path


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    force = "--force" in sys.argv
    if not args:
        print(__doc__); sys.exit(1)
    scan_dir = args[0]
    site, garage_id, kitchen_id = build_site(scan_dir)
    out = merge_and_write(scan_dir, site, force)
    print(f"GARAGE room {garage_id}" + (f", KITCHEN room {kitchen_id}" if kitchen_id else " (no KITCHEN room named - "
          "terrace/pool placed off the garden-side wall instead)"))
    print(f"{len(site['areas'])} area(s), {len(site['buildings'])} building(s), {len(site['trees'])} tree(s), "
          f"{len(site['hedges'])} hedge line(s) -> {out}")
