#!/usr/bin/env python3
"""Reports what is wrong with a scan's walls, from the file extract_scan_geometry.py writes:
  - walls that are not at a right angle to the house's main direction (deviation in degrees)
  - loose wall ends: an end with no other wall end of the room within 5 cm (a gap / hole at the corner)
  - protrusions: a wall of one room that lies deep inside another room on the same storey (a wall of the neighbour,
    or a double-scanned wall)
  - doors that sit within 15 cm of a wall end (swing into the corner / the next wall)

    python3 Tools/analyze_scan_geometry.py novostavba.geometry.json [--tol 1.5]

Pure standard library. Output is a plain-text report; the numbers are in the scan file's frame (metres)."""
import json, math, sys

def angle(a, b): return math.degrees(math.atan2(b[1] - a[1], b[0] - a[0]))
def dist(p, q): return math.hypot(p[0] - q[0], p[1] - q[1])

def main_direction(walls):
    """Length-weighted mean wall direction modulo 90 degrees (the 4-theta trick); degrees in (-45, 45]."""
    sx = sy = 0.0
    for a, b, w in walls:
        t = math.radians(angle(a, b) * 4); sx += w * math.cos(t); sy += w * math.sin(t)
    return math.degrees(math.atan2(sy, sx)) / 4 if (sx or sy) else 0.0

def deviation(a, b, main):
    d = (angle(a, b) - main) % 90
    return d - 90 if d > 45 else d

def inside(poly, p):
    c = False
    for i in range(len(poly)):
        a, b = poly[i], poly[(i + 1) % len(poly)]
        if (a[1] > p[1]) != (b[1] > p[1]) and p[0] < (b[0] - a[0]) * (p[1] - a[1]) / (b[1] - a[1]) + a[0]: c = not c
    return c

def edge_dist(poly, p):
    best = 1e9
    for i in range(len(poly)):
        a, b = poly[i], poly[(i + 1) % len(poly)]
        l2 = (b[0]-a[0])**2 + (b[1]-a[1])**2
        t = 0 if l2 == 0 else max(0, min(1, ((p[0]-a[0])*(b[0]-a[0]) + (p[1]-a[1])*(b[1]-a[1])) / l2))
        best = min(best, dist(p, (a[0] + t*(b[0]-a[0]), a[1] + t*(b[1]-a[1]))))
    return best

def analyze(data, tol=1.5):
    rooms = data['rooms']; out = []
    allw = [(w['a'], w['b'], w['length']) for r in rooms for w in r['walls'] if w['length'] > 0.3]
    main = main_direction(allw)
    out.append(f"{len(rooms)} room(s); main direction {main:.2f} deg (walls are checked against it and its right angles)")
    issues = 0
    for r in rooms:
        rid = r['id'][:8]
        for w in r['walls']:
            if w['length'] < 0.3: continue
            dv = deviation(w['a'], w['b'], main)
            if abs(dv) > tol:
                out.append(f"  room {rid} wall {str(w.get('id'))[:8]} {w['length']:.2f} m: {dv:+.1f} deg off a right angle"); issues += 1
        ends = [(w, e) for w in r['walls'] if w['length'] > 0.3 for e in (w['a'], w['b'])]
        for w, e in ends:
            near = min((dist(e, o) for w2, o in ends if w2 is not w), default=1e9)
            if near > 0.05:
                out.append(f"  room {rid} wall {str(w.get('id'))[:8]}: loose end at ({e[0]:.2f}, {e[1]:.2f}), nearest other end {near:.2f} m"); issues += 1
        for d in r['doors']:
            c = (d['center'][0], d['center'][2])
            for w in r['walls']:
                for e in (w['a'], w['b']):
                    if dist(c, e) - d['width'] / 2 < 0.15 and dist(c, e) < d['width'] / 2 + 0.15 and edge_dist([w['a'], w['b']], c) < 0.3:
                        out.append(f"  room {rid} door {str(d.get('id'))[:8]} ({d['width']:.2f} m wide) is {dist(c, e) - d['width'] / 2:.2f} m from a wall end"); issues += 1; break
    for r in rooms:
        for o in rooms:
            if o is r or len(o['floor']) < 3: continue
            if abs((r.get('floorY') or 0) - (o.get('floorY') or 0)) > 0.8: continue    # another storey
            for w in r['walls']:
                if w['length'] < 0.3: continue
                m = ((w['a'][0] + w['b'][0]) / 2, (w['a'][1] + w['b'][1]) / 2)
                if inside(o['floor'], m) and edge_dist(o['floor'], m) > 0.3:
                    out.append(f"  PROTRUSION: room {r['id'][:8]} wall {str(w.get('id'))[:8]} ({w['length']:.2f} m) lies {edge_dist(o['floor'], m):.2f} m inside room {o['id'][:8]}"); issues += 1
    out.append(f"{issues} issue(s)")
    if any(r.get('format') == 'V2' for r in rooms): out.append("note: V2 floors are convex hulls - an L-shaped room can report a false protrusion")
    return out, issues

if __name__ == '__main__':
    if len(sys.argv) < 2: print(__doc__); sys.exit(1)
    tol = float(sys.argv[sys.argv.index('--tol') + 1]) if '--tol' in sys.argv else 1.5
    lines, n = analyze(json.load(open(sys.argv[1], encoding='utf-8')), tol)
    print('\n'.join(lines)); sys.exit(0 if n == 0 else 2)
