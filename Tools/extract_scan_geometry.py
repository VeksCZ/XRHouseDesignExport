#!/usr/bin/env python3
"""Pulls the geometry out of a saved scan (92_Data_Scan.scene.json) into a small file: rooms with their wall segments, door / window
frames, floor outline and heights - without the global mesh (which is most of the scan's size). The result is what the wall /
corner / door clean-up is developed and tested against (it fits in a git commit, the scan itself doesn't).

    python3 Tools/extract_scan_geometry.py "Exports/RoomData/Scan_Novostavba Osove/92_Data_Scan.scene.json" [out.json]

Coordinates are those of the scan file (metres, y up; the file's x / z may be mirrored against Unity's - irrelevant for angles, gaps
and overlaps). Both scan formats are read: V1 (one entity per wall / door, roomLayoutMETA) and V2 (one room mesh, faces labelled
WALL_FACE / DOOR_FRAME ...). Pure standard library."""
import base64, json, math, struct, sys, os

def b64f(s):
    d = base64.b64decode(s or ''); return struct.unpack('<%df' % (len(d) // 4), d)

def b64u(s):
    d = base64.b64decode(s or ''); return struct.unpack('<%dI' % (len(d) // 4), d)

def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx, aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz)

def qrot(q, v):
    x, y, z, w = q
    u = (x, y, z); uv = (u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0])
    uuv = (u[1]*uv[2]-u[2]*uv[1], u[2]*uv[0]-u[0]*uv[2], u[0]*uv[1]-u[1]*uv[0])
    return (v[0] + 2*(w*uv[0] + uuv[0]), v[1] + 2*(w*uv[1] + uuv[1]), v[2] + 2*(w*uv[2] + uuv[2]))

def pose(loc):
    if not loc: return (0, 0, 0), (0, 0, 0, 1)
    p, o = loc.get('position', {}), loc.get('orientation', {})
    return (p.get('x', 0), p.get('y', 0), p.get('z', 0)), (o.get('x', 0), o.get('y', 0), o.get('z', 0), o.get('w', 1))

def to_world(p, q, v):
    r = qrot(q, v); return (p[0] + r[0], p[1] + r[1], p[2] + r[2])

def r3(v): return [round(float(c), 4) for c in v]
def xz(v): return [round(float(v[0]), 4), round(float(v[2]), 4)]

def guid_fix(u):
    """The scan file's uuid fields are byte-reversed in their first three groups against the System.Guid Unity reads
    at runtime (MRUKRoom/anchor uuids, roomUuid in 93_Data_Edits.json, DoorCatalog.RoomId): group 1 (4 bytes), group 2
    (2 bytes) and group 3 (2 bytes) each need their own bytes reversed; groups 4/5 are already the same both ways."""
    if not u: return u
    parts = str(u).split('-')
    if len(parts) != 5: return u
    for i in range(3): parts[i] = bytes.fromhex(parts[i])[::-1].hex()
    return '-'.join(parts)

def hull(points):
    pts = sorted(set((round(x, 4), round(z, 4)) for x, z in points))
    if len(pts) < 3: return [list(p) for p in pts]
    def cross(o, a, b): return (a[0]-o[0])*(b[1]-o[1]) - (a[1]-o[1])*(b[0]-o[0])
    lo, up = [], []
    for p in pts:
        while len(lo) >= 2 and cross(lo[-2], lo[-1], p) <= 0: lo.pop()
        lo.append(p)
    for p in reversed(pts):
        while len(up) >= 2 and cross(up[-2], up[-1], p) <= 0: up.pop()
        up.append(p)
    return [list(p) for p in lo[:-1] + up[:-1]]

def wall_from_points(ws):
    """Two ends (the horizontally farthest pair), bottom / top and the normal of a wall given its world corner points."""
    best, e0, e1 = 0, ws[0], ws[0]
    for i in range(len(ws)):
        for j in range(i + 1, len(ws)):
            d = math.hypot(ws[j][0]-ws[i][0], ws[j][2]-ws[i][2])
            if d > best: best, e0, e1 = d, ws[i], ws[j]
    if best < 0.05: return None
    return {'a': xz(e0), 'b': xz(e1), 'bottom': round(min(p[1] for p in ws), 4), 'top': round(max(p[1] for p in ws), 4), 'length': round(best, 4)}

LABELS_DOOR = ('DOOR_FRAME',); LABELS_WINDOW = ('WINDOW_FRAME',)

def v2_room(ent, uuid):
    rp, rq = pose(ent.get('locatableMETA'))
    mesh = ent['roomMeshMETA']
    vs = b64f(mesh.get('vertices')); verts = [(vs[i], vs[i+1], vs[i+2]) for i in range(0, len(vs) - 2, 3)]
    out = {'id': guid_fix(uuid), 'format': 'V2', 'walls': [], 'doors': [], 'windows': [], 'floor': [], 'ceilingY': None}
    floor_pts, ceil_y = [], []
    for f in mesh.get('faces', []):
        label = f.get('semanticLabel', '')
        idx = [i for i in b64u(f.get('indices')) if i < len(verts)]
        if not idx: continue
        ws = [to_world(rp, rq, verts[i]) for i in sorted(set(idx))]
        if label in ('WALL_FACE', 'INNER_WALL_FACE', 'INVISIBLE_WALL_FACE'):
            w = wall_from_points(ws)
            if w: w['id'] = guid_fix(f.get('uuid')); w['label'] = label; out['walls'].append(w)
        elif label in LABELS_DOOR + LABELS_WINDOW:
            lo = [min(p[k] for p in ws) for k in range(3)]; hi = [max(p[k] for p in ws) for k in range(3)]
            w = wall_from_points(ws)
            item = {'id': guid_fix(f.get('uuid')), 'parent': guid_fix(f.get('parentUuid')), 'label': label, 'center': r3([(lo[k]+hi[k])/2 for k in range(3)]),
                    'width': w['length'] if w else 0, 'height': round(hi[1]-lo[1], 4), 'sill': round(lo[1], 4)}
            if w: item['a'], item['b'] = w['a'], w['b']
            (out['doors'] if label in LABELS_DOOR else out['windows']).append(item)
        elif label == 'FLOOR': floor_pts += [(p[0], p[2]) for p in ws]; out['floorY'] = round(min(p[1] for p in ws), 4)
        elif label == 'CEILING': ceil_y += [p[1] for p in ws]
    out['floor'] = hull(floor_pts)
    if ceil_y: out['ceilingY'] = round(sum(ceil_y) / len(ceil_y), 4)
    return out

def v1_room(ent, uuid, by_id):
    layout = ent['roomLayoutMETA']
    out = {'id': guid_fix(uuid), 'format': 'V1', 'walls': [], 'doors': [], 'windows': [], 'floor': [], 'ceilingY': None}
    def rect(e):
        p, q = pose(e.get('locatableMETA')); b = e.get('bounded2DMETA') or {}
        o = b.get('offset', {}); x = b.get('extent', {})
        ox, oy, w, h = o.get('x', 0), o.get('y', 0), x.get('width', 0), x.get('height', 0)
        return p, q, ox, oy, w, h
    for wid in layout.get('wallUuids', []):
        e = by_id.get(wid.lower())
        if not e: continue
        p, q, ox, oy, w, h = rect(e)
        a = to_world(p, q, (ox, oy, 0)); b = to_world(p, q, (ox + w, oy, 0)); t = to_world(p, q, (ox, oy + h, 0))
        n = qrot(q, (0, 0, 1))
        out['walls'].append({'id': guid_fix(e['uuid']), 'label': 'WALL_FACE', 'a': xz(a), 'b': xz(b), 'bottom': round(min(a[1], b[1]), 4), 'top': round(max(t[1], a[1] + h), 4), 'length': round(w, 4), 'normal': [round(n[0], 4), round(n[2], 4)]})
    fid = (layout.get('floorUuid') or '').lower(); cid = (layout.get('ceilingUuid') or '').lower()
    if fid in by_id:
        fe = by_id[fid]; p, q = pose(fe.get('locatableMETA')); vs = b64f((fe.get('boundary2DMETA') or {}).get('vertices'))
        pts = [to_world(p, q, (vs[i], vs[i+1], 0)) for i in range(0, len(vs) - 1, 2)]
        out['floor'] = [xz(v) for v in pts]; out['floorY'] = round(p[1], 4)
    if cid in by_id: out['ceilingY'] = round(pose(by_id[cid].get('locatableMETA'))[0][1], 4)
    for uid in (ent.get('spaceContainerMETA') or {}).get('uuids', []):
        e = by_id.get(uid.lower())
        if not e: continue
        label = e.get('semanticLabelMETA', '')
        if label not in LABELS_DOOR + LABELS_WINDOW: continue
        p, q, ox, oy, w, h = rect(e)
        c = to_world(p, q, (ox + w / 2, oy + h / 2, 0)); n = qrot(q, (0, 0, 1))
        item = {'id': guid_fix(e['uuid']), 'label': label, 'center': r3(c), 'width': round(w, 4), 'height': round(h, 4), 'sill': round(c[1] - h / 2, 4), 'normal': [round(n[0], 4), round(n[2], 4)]}
        (out['doors'] if label in LABELS_DOOR else out['windows']).append(item)
    return out

def extract(path):
    data = json.load(open(path, encoding='utf-8'))
    ents = data.get('spatialEntities', [])
    by_id = {str(e.get('uuid', '')).lower(): e for e in ents}
    rooms = []
    for e in ents:
        if 'roomMeshMETA' in e: rooms.append(v2_room(e, e['uuid']))
        elif 'roomLayoutMETA' in e: rooms.append(v1_room(e, e['uuid'], by_id))
    return {'format': 'xrhouse-geometry/1', 'source': os.path.basename(os.path.dirname(os.path.abspath(path))) or os.path.basename(path),
            'frame': 'scan file (metres, y up; x / z may be mirrored against Unity)', 'rooms': rooms}

if __name__ == '__main__':
    if len(sys.argv) < 2: print(__doc__); sys.exit(1)
    res = extract(sys.argv[1])
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.splitext(sys.argv[1])[0] + '.geometry.json'
    json.dump(res, open(out, 'w', encoding='utf-8'), indent=1)
    n = lambda k: sum(len(r[k]) for r in res['rooms'])
    print(f"{len(res['rooms'])} room(s), {n('walls')} wall(s), {n('doors')} door(s), {n('windows')} window(s) -> {out} ({os.path.getsize(out) // 1024} KB)")
