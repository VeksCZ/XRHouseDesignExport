#!/usr/bin/env python3
"""
Adds furniture (bathroom fittings and simple boxes) to an exported house model - locally on the PC, from an export
folder, without Unity or the headset. Pure Python standard library, nothing to install.

  python Tools\\furnish.py <export folder> --list
      Rooms of the model: name, story (floor height), floor area, extent. Use it to find the room to furnish.

  python Tools\\furnish.py <export folder> [--spec 95_Data_Furniture.json] [--model 13_Model_Reconstruction.glb]
      Reads the furniture spec (default: 95_Data_Furniture.json in the export folder), writes
      16_Model_Furnished.glb (the chosen model + a FURNITURE node) and 16_Furnished_<room>.svg (top view per room,
      to check the placement against the floor plan).

Coordinates are the model's own (meters, Y up, walls square to the X/Z axes - the export already rotated them).
In the SVG +X points right and +Z points down. Every item is placed against one wall of its room's floor rectangle:

  {
    "rooms": [
      { "room": "ROOM_93EE",                 # node name in the GLB, or the 4-char room id ("93EE")
        "items": [
          { "type": "bathtub", "wall": "-z", "along": 0.0, "w": 1.70, "d": 0.75 },
          { "type": "washbasin", "wall": "+x", "along": 0.40, "w": 0.80, "d": 0.48, "mirror": true },
          { "type": "wc", "wall": "-x", "along": 1.20 },
          { "type": "shower", "wall": "+z", "along": 0.0, "w": 0.90, "d": 0.90, "glass": ["front", "left"] },
          { "type": "box", "wall": "+x", "along": 1.5, "w": 0.6, "d": 0.6, "h": 0.85, "color": "#dddddd", "name": "Pracka" }
        ] } ] }

  wall   which side of the room the item stands against: "-x", "+x", "-z", "+z" (the room's floor bounding box sides)
  along  meters along that wall, measured from its lower-coordinate corner (the -x end for "-z"/"+z" walls, the -z end
         for "-x"/"+x" walls) to the item's near edge
  gap    meters between the wall and the item's back (default 0)
  w, d, h  width along the wall, depth into the room, height (each type has sensible defaults)
  x, z   alternatively an absolute position of the item's corner nearest the wall's start (then "wall" still sets
         which way the item faces)
  y      lift above the floor (default 0)

Types: bathtub, shower ("glass": ["front", "left", "right"] or {"side": "right", "from": 0, "to": 0.55}),
       washbasin (vanity + top; "basins": n, "vessel": true = bowls standing on the top, "mirror": true with
       "mirror_w"/"mirror_y"/"mirror_h", "cabinet_from" = underside height), wc (wall-hung bowl; "prewall": depth,
       0 = none), washer, radiator (towel rail, "y" = bottom), mirror, cabinet (wall cabinet, "y" default 1.4),
       box (anything else; "color" = material name or "#rrggbb", "y" = lift).
"""
import argparse, json, math, os, struct, sys

# ---------------------------------------------------------------------------------------------------------- GLB I/O

def read_glb(path):
    data = open(path, 'rb').read()
    magic, ver, length = struct.unpack_from('<4sII', data, 0)
    if magic != b'glTF' or ver != 2: raise SystemExit(f'{path}: not a glTF 2.0 binary')
    off, js, binc = 12, None, b''
    while off < length:
        clen, ctype = struct.unpack_from('<II', data, off)
        chunk = data[off + 8: off + 8 + clen]
        if ctype == 0x4E4F534A: js = json.loads(chunk.decode('utf-8'))
        elif ctype == 0x004E4942: binc = chunk
        off += 8 + clen
    return js, bytearray(binc)


def write_glb(path, js, binc):
    while len(binc) % 4: binc.append(0)
    js['buffers'] = [{'byteLength': len(binc)}]
    jb = json.dumps(js, separators=(',', ':')).encode('utf-8')
    jb += b' ' * ((4 - len(jb) % 4) % 4)
    total = 12 + 8 + len(jb) + 8 + len(binc)
    with open(path, 'wb') as f:
        f.write(struct.pack('<4sII', b'glTF', 2, total))
        f.write(struct.pack('<II', len(jb), 0x4E4F534A)); f.write(jb)
        f.write(struct.pack('<II', len(binc), 0x004E4942)); f.write(binc)


def accessor_values(js, binc, i):
    a = js['accessors'][i]; v = js['bufferViews'][a['bufferView']]
    o = v.get('byteOffset', 0) + a.get('byteOffset', 0)
    fmt = {5126: 'f', 5125: 'I', 5123: 'H', 5121: 'B'}[a['componentType']]
    n = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}[a['type']]
    stride = v.get('byteStride', struct.calcsize(fmt) * n)
    vals = [struct.unpack_from('<' + fmt * n, binc, o + stride * k) for k in range(a['count'])]
    return [x[0] for x in vals] if n == 1 else vals


def add_view(js, binc, raw, target):
    while len(binc) % 4: binc.append(0)
    js['bufferViews'].append({'buffer': 0, 'byteOffset': len(binc), 'byteLength': len(raw), 'target': target})
    binc.extend(raw)
    return len(js['bufferViews']) - 1


def add_accessor(js, binc, values, kind):
    if kind == 'VEC3':
        raw = b''.join(struct.pack('<3f', *v) for v in values)
        view = add_view(js, binc, raw, 34962)
        acc = {'bufferView': view, 'componentType': 5126, 'count': len(values), 'type': 'VEC3',
               'min': [min(v[i] for v in values) for i in range(3)], 'max': [max(v[i] for v in values) for i in range(3)]}
    else:
        raw = b''.join(struct.pack('<I', v) for v in values)
        view = add_view(js, binc, raw, 34963)
        acc = {'bufferView': view, 'componentType': 5125, 'count': len(values), 'type': 'SCALAR'}
    js['accessors'].append(acc)
    return len(js['accessors']) - 1

# ------------------------------------------------------------------------------------------------------ room geometry

def rooms_of(js, binc):
    """Per room node: floor height, floor polygon(s) (triangles), floor bounding box, door/window boxes."""
    out = {}
    for node in js['nodes']:
        if 'mesh' not in node or not node.get('name'): continue
        mesh = js['meshes'][node['mesh']]
        floor_tris, floor_y, openings = [], None, []
        for pr in mesh['primitives']:
            pts = accessor_values(js, binc, pr['attributes']['POSITION'])
            if not pts: continue
            ys = [p[1] for p in pts]
            mat = pr.get('material')
            if mat == 0 and max(ys) - min(ys) < 0.01:          # material 0 = floor/ceiling slabs; flat = a surface
                idx = accessor_values(js, binc, pr['indices']) if 'indices' in pr else list(range(len(pts)))
                y = pts[0][1]
                tris = [(pts[idx[t]], pts[idx[t + 1]], pts[idx[t + 2]]) for t in range(0, len(idx) - 2, 3)]
                if floor_y is None or y < floor_y: floor_y, floor_tris = y, tris
            elif mat in (2, 3):                                  # 2 = door, 3 = window (XRModelFactory colours)
                openings.append(('door' if mat == 2 else 'window',
                                 min(p[0] for p in pts), max(p[0] for p in pts), min(p[2] for p in pts), max(p[2] for p in pts),
                                 min(ys), max(ys)))
        if floor_y is None: continue
        # a room's mesh also carries openings of the story above/below (cross-story walls) - keep this story's
        openings = [o for o in openings if floor_y - 0.2 <= o[5] <= floor_y + 2.0]
        xs = [p[0] for t in floor_tris for p in t]; zs = [p[2] for t in floor_tris for p in t]
        area = sum(abs((b[0] - a[0]) * (c[2] - a[2]) - (c[0] - a[0]) * (b[2] - a[2])) / 2 for a, b, c in floor_tris)
        out[node['name']] = {'y': floor_y, 'tris': floor_tris, 'xmin': min(xs), 'xmax': max(xs), 'zmin': min(zs), 'zmax': max(zs),
                             'area': area, 'openings': openings}
    return out


def find_room(rooms, key):
    if key in rooms: return key
    hits = [n for n in rooms if n.upper().endswith('_' + key.upper()) or n.upper() == key.upper()]
    if len(hits) == 1: return hits[0]
    raise SystemExit(f'Room "{key}" not found (or ambiguous). Rooms: {", ".join(sorted(rooms))}')

# ---------------------------------------------------------------------------------------------------- furniture shapes
# Every shape is a list of (material, box) with box = (u0, u1, v0, v1, y0, y1) in the item's local frame:
# u along the wall (0..w), v away from the wall into the room (0..d), y up from the floor.

MATERIALS = {
    'ceramic': ([0.96, 0.96, 0.95, 1.0], 0.0, 0.25, None),
    'water':   ([0.80, 0.86, 0.90, 1.0], 0.0, 0.15, None),
    'glass':   ([0.70, 0.85, 0.95, 0.30], 0.0, 0.05, 'BLEND'),
    'wood':    ([0.62, 0.48, 0.34, 1.0], 0.0, 0.70, None),
    'mirror':  ([0.85, 0.90, 0.95, 1.0], 1.0, 0.05, None),
    'chrome':  ([0.80, 0.80, 0.82, 1.0], 1.0, 0.20, None),
    'tiles':   ([0.88, 0.88, 0.86, 1.0], 0.0, 0.40, None),
    'dark':    ([0.25, 0.25, 0.27, 1.0], 0.0, 0.50, None),
    'fronts':  ([0.66, 0.68, 0.70, 1.0], 0.0, 0.40, None),
    'oak':     ([0.68, 0.56, 0.44, 1.0], 0.0, 0.60, None),
    'steel':   ([0.74, 0.75, 0.77, 1.0], 1.0, 0.25, None),
    'gap':     ([0.35, 0.36, 0.38, 1.0], 0.0, 0.60, None),
}

DEFAULTS = {  # w, d, h
    'bathtub': (1.70, 0.75, 0.58), 'shower': (0.90, 0.90, 2.00), 'washbasin': (0.80, 0.48, 0.85),
    'wc': (0.40, 0.56, 1.15), 'washer': (0.60, 0.60, 0.85), 'radiator': (0.50, 0.10, 1.20),
    'mirror': (0.80, 0.02, 0.80), 'cabinet': (0.60, 0.20, 0.70), 'box': (0.60, 0.60, 0.85),
    # kitchen (keep in step with Furniture.cs): base run with worktop (sink/hob/dishwasher as [from, to] along it),
    # wall units ("glass": true = glazed), tall units (oven/microwave/fridge), free-standing island (use x/z)
    'kitchen_base': (1.20, 0.60, 0.90), 'kitchen_wall': (1.20, 0.35, 0.72),
    'kitchen_tall': (0.60, 0.60, 2.15), 'island': (1.30, 0.80, 0.90),
}


def _doors(B, w, d, y0, y1, unit=0.6):
    """Thin dark lines on a run's front face every ~unit wide (door/drawer gaps)."""
    n = max(1, round(w / unit))
    for k in range(1, n):
        u = w * k / n
        B.append(('gap', (u - 0.002, u + 0.002, d, d + 0.003, y0 + 0.01, y1 - 0.01)))


def _kitchen(item, t, w, d, h):
    B = []
    fr = item.get('color', 'fronts')
    if t == 'kitchen_base':
        top = item.get('top', 'oak'); plinth = 0.10; wt = 0.04
        B += [('dark', (0, w, 0, d - 0.06, 0, plinth)), (fr, (0, w, 0, d - 0.02, plinth, h - wt)),
              (top, (0, w, 0, d + 0.02, h - wt, h))]
        _doors(B, w, d - 0.02, plinth, h - wt)
        if 'dishwasher' in item:
            a0, a1 = item['dishwasher']; B.append(('steel', (a0 + 0.005, a1 - 0.005, d - 0.02, d - 0.01, plinth + 0.02, h - wt - 0.02)))
        if 'sink' in item:
            a0, a1 = item['sink']; tc = a0 + (a1 - a0) * 0.35
            B += [('steel', (a0, a1, 0.08, d - 0.08, h - 0.002, h + 0.004)),
                  ('water', (a0 + 0.04, a0 + (a1 - a0) * 0.62, 0.12, d - 0.12, h + 0.004, h + 0.006)),
                  ('chrome', (tc - 0.02, tc + 0.02, 0.03, 0.07, h, h + 0.30)),
                  ('chrome', (tc - 0.015, tc + 0.015, 0.05, 0.24, h + 0.27, h + 0.30))]
        if 'hob' in item:
            a0, a1 = item['hob']; B.append(('dark', (a0, a1, 0.05, d - 0.03, h, h + 0.006)))
        return B, item.get('y', 0.0)
    if t == 'kitchen_wall':
        y0 = item.get('y', 1.45); glass = item.get('glass', False)
        B.append((fr, (0, w, 0, d - (0.02 if glass else 0), y0, y0 + h)))
        if glass: B.append(('glass', (0, w, d - 0.02, d, y0, y0 + h)))
        else: _doors(B, w, d, y0, y0 + h)
        return B, 0.0
    if t == 'kitchen_tall':
        B += [('dark', (0, w, 0, d - 0.06, 0, 0.10)), (fr, (0, w, 0, d, 0.10, h))]
        if item.get('oven'):
            B += [('steel', (0.03, w - 0.03, d, d + 0.01, 0.80, 1.40)), ('dark', (0.08, w - 0.08, d + 0.01, d + 0.012, 0.88, 1.28))]
        if item.get('microwave'):
            B += [('steel', (0.03, w - 0.03, d, d + 0.01, 1.45, 1.85)), ('dark', (0.06, w * 0.68, d + 0.01, d + 0.012, 1.50, 1.80))]
        if item.get('fridge'):
            B.append(('gap', (0, w, d, d + 0.004, 0.88, 0.90)))
        B.append(('gap', (0, 0.004, d, d + 0.004, 0.10, h)))
        return B, item.get('y', 0.0)
    # island
    side = 0.04; wt = 0.04
    B += [('oak', (0, w, 0, d, h - wt, h)), ('oak', (0, side, 0, d, 0, h - wt)), ('oak', (w - side, w, 0, d, 0, h - wt)),
          (fr, (side, w - side, 0.05, d - 0.05, 0.10, h - wt)), ('dark', (side, w - side, 0.10, d - 0.10, 0, 0.10))]
    return B, item.get('y', 0.0)


def shape(item, w, d, h):
    t = item['type']; B = []
    if t in ('kitchen_base', 'kitchen_wall', 'kitchen_tall', 'island'):
        return _kitchen(item, t, w, d, h)
    if t == 'bathtub':
        r = 0.07  # rim thickness
        B += [('tiles', (0, w, 0, d, 0, h - 0.02)),                                   # tiled front/side panel block
              ('ceramic', (0, w, 0, r, h - 0.02, h)), ('ceramic', (0, w, d - r, d, h - 0.02, h)),
              ('ceramic', (0, r, r, d - r, h - 0.02, h)), ('ceramic', (w - r, w, r, d - r, h - 0.02, h)),
              ('water', (r, w - r, r, d - r, h - 0.03, h - 0.025))]                    # inside of the tub
    elif t == 'shower':
        tray = 0.05
        B.append(('ceramic', (0, w, 0, d, 0, tray)))
        g = 0.008
        for side in item.get('glass', ['front']):
            # "front" / "left" / "right", or {"side": ..., "from": m, "to": m} for a partial panel (measured along
            # that side: from the left end for "front", from the wall for "left"/"right")
            sd = side if isinstance(side, dict) else {'side': side}
            a0 = sd.get('from', 0.0)
            if sd['side'] == 'front': B.append(('glass', (a0, sd.get('to', w), d - g, d, tray, h)))
            elif sd['side'] == 'left': B.append(('glass', (0, g, a0, sd.get('to', d), tray, h)))
            elif sd['side'] == 'right': B.append(('glass', (w - g, w, a0, sd.get('to', d), tray, h)))
        B.append(('chrome', (w / 2 - 0.08, w / 2 + 0.08, 0.0, 0.25, h - 0.02, h)))      # rain head
    elif t == 'washbasin':
        top = h; cab0 = item.get('cabinet_from', 0.30)
        n = item.get('basins', 1); vessel = item.get('vessel', False)
        B += [('wood', (0, w, 0, d - 0.02, cab0, top - 0.04)),
              ('wood' if vessel else 'ceramic', (0, w, 0, d, top - 0.04, top))]
        for k in range(n):
            c = w * (k + 0.5) / n
            if vessel:   # bowl standing on the top, tap from the wall
                B += [('ceramic', (c - 0.25, c + 0.25, 0.08, 0.44, top, top + 0.13)),
                      ('water', (c - 0.21, c + 0.21, 0.12, 0.40, top + 0.13, top + 0.132)),
                      ('chrome', (c - 0.015, c + 0.015, 0.0, 0.18, top + 0.22, top + 0.25))]
            else:
                B += [('water', (c - 0.2, c + 0.2, 0.12, d - 0.08, top - 0.001, top + 0.001)),
                      ('chrome', (c - 0.02, c + 0.02, 0.03, 0.15, top, top + 0.18))]
        if item.get('mirror'):
            mw = item.get('mirror_w', w); m0 = item.get('mirror_y', top + 0.25)
            B.append(('mirror', ((w - mw) / 2, (w + mw) / 2, 0, 0.02, m0, m0 + item.get('mirror_h', 0.80))))
    elif t == 'wc':
        pre = item.get('prewall', 0.20)
        if pre > 0: B.append(('tiles', (-0.10, w + 0.10, 0, pre, 0, h)))
        bw = min(w, 0.37)
        B += [('ceramic', ((w - bw) / 2, (w + bw) / 2, pre, pre + 0.54, 0.20, 0.40)),
              ('dark', ((w - bw) / 2 + 0.02, (w + bw) / 2 - 0.02, pre + 0.02, pre + 0.52, 0.40, 0.42)),  # seat
              ('chrome', (w / 2 - 0.12, w / 2 + 0.12, pre - 0.005, pre, 0.95, 1.10))]                # flush plate
    elif t == 'washer':
        B += [('ceramic', (0, w, 0, d, 0, h)), ('dark', (w * 0.2, w * 0.8, d, d + 0.01, h * 0.25, h * 0.75))]
    elif t == 'radiator':
        y0 = item.get('y', 0.30)
        for k in range(int(h / 0.08)):
            B.append(('chrome', (0, w, 0.02, d, y0 + k * 0.08, y0 + k * 0.08 + 0.03)))
        B += [('chrome', (0, 0.03, 0.02, d, y0, y0 + h)), ('chrome', (w - 0.03, w, 0.02, d, y0, y0 + h))]
        return B, 0.0
    elif t == 'mirror':
        y0 = item.get('y', 1.10); B.append(('mirror', (0, w, 0, d, y0, y0 + h))); return B, 0.0
    elif t == 'cabinet':
        y0 = item.get('y', 1.40); B.append(('wood', (0, w, 0, d, y0, y0 + h))); return B, 0.0
    else:  # box: "color" is a material name (wood, ceramic, tiles, ...) or "#rrggbb"
        B.append((item.get('color', 'wood'), (0, w, 0, d, 0, h)))
        return B, item.get('y', 0.0)
    return B, item.get('y', 0.0)


def place(room, item, w, d):
    """Local (u, v) -> model (x, z). Returns a function mapping a local box to a model-space box."""
    wall = item.get('wall', '-z'); along = item.get('along', 0.0); gap = item.get('gap', 0.0)
    if wall not in ('-x', '+x', '-z', '+z'): raise SystemExit(f'wall must be -x/+x/-z/+z, not {wall!r}')
    x0 = item.get('x'); z0 = item.get('z')
    if wall in ('-z', '+z'):
        bx = x0 if x0 is not None else room['xmin'] + along
        bz = z0 if z0 is not None else (room['zmin'] + gap if wall == '-z' else room['zmax'] - gap)
        sgn = 1 if wall == '-z' else -1
        return lambda u, v: (bx + u, bz + sgn * v)
    bz = z0 if z0 is not None else room['zmin'] + along
    bx = x0 if x0 is not None else (room['xmin'] + gap if wall == '-x' else room['xmax'] - gap)
    sgn = 1 if wall == '-x' else -1
    return lambda u, v: (bx + sgn * v, bz + u)


def box_mesh(x0, x1, y0, y1, z0, z1):
    """24 vertices (flat normals) + 36 indices for an axis-aligned box."""
    x0, x1 = sorted((x0, x1)); z0, z1 = sorted((z0, z1)); y0, y1 = sorted((y0, y1))
    faces = [((1, 0, 0), [(x1, y0, z0), (x1, y1, z0), (x1, y1, z1), (x1, y0, z1)]),
             ((-1, 0, 0), [(x0, y0, z1), (x0, y1, z1), (x0, y1, z0), (x0, y0, z0)]),
             ((0, 1, 0), [(x0, y1, z0), (x0, y1, z1), (x1, y1, z1), (x1, y1, z0)]),
             ((0, -1, 0), [(x0, y0, z1), (x0, y0, z0), (x1, y0, z0), (x1, y0, z1)]),
             ((0, 0, 1), [(x1, y0, z1), (x1, y1, z1), (x0, y1, z1), (x0, y0, z1)]),
             ((0, 0, -1), [(x0, y0, z0), (x0, y1, z0), (x1, y1, z0), (x1, y0, z0)])]
    P, N, I = [], [], []
    for n, quad in faces:
        b = len(P); P += quad; N += [n] * 4
        I += [b, b + 2, b + 1, b, b + 3, b + 2]   # counter-clockwise seen from outside
    return P, N, I

# ----------------------------------------------------------------------------------------------------------- building

def furnish(js, binc, rooms, spec):
    mat_index = {}
    for name, (col, metal, rough, alpha) in MATERIALS.items():
        m = {'name': 'FURN_' + name, 'pbrMetallicRoughness': {'baseColorFactor': col, 'metallicFactor': metal, 'roughnessFactor': rough},
             'doubleSided': True}
        if alpha: m['alphaMode'] = alpha
        js['materials'].append(m); mat_index[name] = len(js['materials']) - 1

    furn_children, placed = [], {}
    for rs in spec['rooms']:
        rname = find_room(rooms, rs['room']); room = rooms[rname]
        for k, item in enumerate(rs['items']):
            t = item.get('type', 'box')
            if t not in DEFAULTS: raise SystemExit(f'unknown type {t!r} (known: {", ".join(DEFAULTS)})')
            dw, dd, dh = DEFAULTS[t]
            w, d, h = item.get('w', dw), item.get('d', dd), item.get('h', dh)
            boxes, lift = shape(item, w, d, h)
            to_xz = place(room, item, w, d)
            by_mat, rect = {}, None
            for mat, (u0, u1, v0, v1, y0, y1) in boxes:
                if mat.startswith('#'):  # custom colour for "box"
                    key = mat
                    if key not in mat_index:
                        c = [int(mat[i:i + 2], 16) / 255 for i in (1, 3, 5)] + [1.0]
                        js['materials'].append({'name': 'FURN_' + mat, 'pbrMetallicRoughness': {'baseColorFactor': c, 'metallicFactor': 0.0, 'roughnessFactor': 0.6}, 'doubleSided': True})
                        mat_index[key] = len(js['materials']) - 1
                xa, za = to_xz(u0, v0); xb, zb = to_xz(u1, v1)
                yb = room['y'] + lift
                P, N, I = box_mesh(xa, xb, yb + y0, yb + y1, za, zb)
                g = by_mat.setdefault(mat, ([], [], []))
                base = len(g[0]); g[0].extend(P); g[1].extend(N); g[2].extend(i + base for i in I)
            fx0, fz0 = to_xz(0, 0); fx1, fz1 = to_xz(w, d)
            prims = []
            for mat, (P, N, I) in by_mat.items():
                prims.append({'attributes': {'POSITION': add_accessor(js, binc, P, 'VEC3'), 'NORMAL': add_accessor(js, binc, N, 'VEC3')},
                              'indices': add_accessor(js, binc, I, 'SCALAR'), 'material': mat_index[mat]})
            label = item.get('name', f'{t}_{k + 1}')
            js['meshes'].append({'name': label, 'primitives': prims})
            js['nodes'].append({'name': label, 'mesh': len(js['meshes']) - 1})
            furn_children.append(len(js['nodes']) - 1)
            placed.setdefault(rname, []).append((label, t, min(fx0, fx1), max(fx0, fx1), min(fz0, fz1), max(fz0, fz1)))
            overflow = (min(fx0, fx1) < room['xmin'] - 0.01 or max(fx0, fx1) > room['xmax'] + 0.01 or
                        min(fz0, fz1) < room['zmin'] - 0.01 or max(fz0, fz1) > room['zmax'] + 0.01)
            if overflow: print(f'  WARNING: {label} sticks out of {rname}\'s floor rectangle')
    js['nodes'].append({'name': 'FURNITURE', 'children': furn_children})
    js['scenes'][js.get('scene', 0)]['nodes'].append(len(js['nodes']) - 1)
    return placed

# ---------------------------------------------------------------------------------------------------------- SVG check

COLORS = {'bathtub': '#9ecae1', 'shower': '#c6dbef', 'washbasin': '#c49c6b', 'wc': '#e0e0e0', 'washer': '#bdbdbd',
          'radiator': '#969696', 'mirror': '#deebf7', 'cabinet': '#c49c6b', 'box': '#d9d9d9'}


def svg_room(path, rname, room, items):
    pad, S = 0.4, 160.0  # meters of margin, pixels per meter
    x0, z0 = room['xmin'] - pad, room['zmin'] - pad
    W, H = (room['xmax'] - room['xmin'] + 2 * pad) * S, (room['zmax'] - room['zmin'] + 2 * pad) * S
    X = lambda x: (x - x0) * S; Z = lambda z: (z - z0) * S
    o = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W:.0f}" height="{H + 40:.0f}" font-family="Segoe UI,Arial" font-size="12">',
         '<rect width="100%" height="100%" fill="white"/>']
    for a, b, c in room['tris']:
        o.append(f'<polygon points="{X(a[0]):.1f},{Z(a[2]):.1f} {X(b[0]):.1f},{Z(b[2]):.1f} {X(c[0]):.1f},{Z(c[2]):.1f}" fill="#f3efe6" stroke="#f3efe6"/>')
    o.append(f'<rect x="{X(room["xmin"]):.1f}" y="{Z(room["zmin"]):.1f}" width="{(room["xmax"] - room["xmin"]) * S:.1f}" '
             f'height="{(room["zmax"] - room["zmin"]) * S:.1f}" fill="none" stroke="#333" stroke-width="3"/>')
    for kind, ax0, ax1, az0, az1, _y0, _y1 in room['openings']:
        col = '#8c510a' if kind == 'door' else '#2b8cbe'
        o.append(f'<rect x="{X(ax0):.1f}" y="{Z(az0):.1f}" width="{max(2, (ax1 - ax0) * S):.1f}" height="{max(2, (az1 - az0) * S):.1f}" fill="{col}"/>')
    seen = {}
    for label, t, ix0, ix1, iz0, iz1 in items:
        key = (round(ix0 + ix1, 2), round(iz0 + iz1, 2)); dy = 14 * seen.get(key, 0); seen[key] = seen.get(key, 0) + 1
        o.append(f'<rect x="{X(ix0):.1f}" y="{Z(iz0):.1f}" width="{(ix1 - ix0) * S:.1f}" height="{(iz1 - iz0) * S:.1f}" '
                 f'fill="{COLORS.get(t, "#ddd")}" stroke="#555"/>')
        o.append(f'<text x="{X((ix0 + ix1) / 2):.1f}" y="{Z((iz0 + iz1) / 2) + dy:.1f}" text-anchor="middle">{label}</text>')
    w_, d_ = room['xmax'] - room['xmin'], room['zmax'] - room['zmin']
    o += [f'<text x="{W / 2:.0f}" y="{Z(room["zmin"]) - 8:.0f}" text-anchor="middle" fill="#c00">-z  ({w_:.2f} m)</text>',
          f'<text x="{W / 2:.0f}" y="{Z(room["zmax"]) + 18:.0f}" text-anchor="middle" fill="#c00">+z</text>',
          f'<text x="{X(room["xmin"]) - 6:.0f}" y="{H / 2:.0f}" text-anchor="end" fill="#c00">-x</text>',
          f'<text x="{X(room["xmax"]) + 6:.0f}" y="{H / 2:.0f}" fill="#c00">+x ({d_:.2f} m)</text>',
          f'<text x="8" y="{H + 28:.0f}">{rname}: floor {room["area"]:.1f} m², brown = door, blue = window</text>', '</svg>']
    open(path, 'w', encoding='utf-8').write('\n'.join(o))

# --------------------------------------------------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('export', help='export folder (Exports\\RoomData\\Export_...)')
    ap.add_argument('--list', action='store_true', help='list the rooms and exit')
    ap.add_argument('--spec', default='95_Data_Furniture.json')
    ap.add_argument('--model', default='13_Model_Reconstruction.glb')
    ap.add_argument('--out', default='16_Model_Furnished.glb')
    a = ap.parse_args()

    js, binc = read_glb(os.path.join(a.export, a.model))
    rooms = rooms_of(js, binc)
    if a.list:
        print(f'{"room":<22}{"floor y":>8}{"area m2":>9}{"extent x * z (m)":>20}')
        for n, r in sorted(rooms.items(), key=lambda kv: (kv[1]['y'], kv[0])):
            print(f'{n:<22}{r["y"]:>8.2f}{r["area"]:>9.1f}{r["xmax"] - r["xmin"]:>11.2f} x {r["zmax"] - r["zmin"]:.2f}'
                  f'   doors {sum(o[0] == "door" for o in r["openings"])}, windows {sum(o[0] == "window" for o in r["openings"])}')
        return
    spec_path = a.spec if os.path.isabs(a.spec) or os.path.exists(a.spec) else os.path.join(a.export, a.spec)
    spec = json.load(open(spec_path, encoding='utf-8'))
    placed = furnish(js, binc, rooms, spec)
    write_glb(os.path.join(a.export, a.out), js, binc)
    print('wrote', os.path.join(a.export, a.out))
    for rname, items in placed.items():
        p = os.path.join(a.export, f'16_Furnished_{rname}.svg'); svg_room(p, rname, rooms[rname], items); print('wrote', p)


if __name__ == '__main__':
    main()
