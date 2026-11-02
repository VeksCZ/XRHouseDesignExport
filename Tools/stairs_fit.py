#!/usr/bin/env python3
"""Where does a three-flight (U) stair fit in a room? Tries every position / turn / mirror on a grid and lists the placements whose
flights and landings lie inside the room and keep the doors free.

    python3 Tools/stairs_fit.py novostavba.geometry.json 162b8c39 [--risers 3,11,3] [--width 0.9] [--tread 0.27] [--tol 0.04] [--door-clear 0.55]

Layout (like the Stairs tool): flight 1 runs straight, a square landing, flight 2 runs back parallel to flight 1 (the long run along
the wall), a landing, flight 3 as flight 1. needs: pip install shapely. Output: footprint size and the best placements (start point
of flight 1, direction of flight 1, mirror) in the geometry file's frame."""
import json, math, sys
from shapely.geometry import Polygon, Point, box
from shapely import affinity

def arg(name, default):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default

def footprint(risers, width, tread):
    """Rectangles (x0,z0,x1,z1) in the stair's own frame, as the Stairs tool lays a three-flight stair out: the long middle flight runs
    along +x (z 0..width) between two square landings; flights 1 and 3 leave the landings towards +z (a U open to +z).
    A flight of n risers is n * tread long (e.g. 3 x 0.27 = 0.81; the saved 4-point path has 0.81 + half a landing between its points)."""
    f1, f2, f3 = (r * tread for r in risers)
    w = width
    return [(-w, 0, 0, w),                       # landing 1
            (0, 0, f2, w),                       # flight 2 (the long one)
            (f2, 0, f2 + w, w),                  # landing 2
            (-w, w, 0, w + f1),                  # flight 1
            (f2, w, f2 + w, w + f3)]             # flight 3


def main():
    if len(sys.argv) < 3: print(__doc__); sys.exit(1)
    g = json.load(open(sys.argv[1], encoding='utf-8'))
    room = [r for r in g['rooms'] if r['id'].startswith(sys.argv[2])][0]
    risers = [int(x) for x in arg('--risers', '3,11,3').split(',')]
    width = float(arg('--width', 0.9)); tread = float(arg('--tread', 0.27)); tol = float(arg('--tol', 0.04))
    P = Polygon(room['floor']).buffer(-tol)
    doors = []
    for d in room['doors']:
        c = (d['center'][0], d['center'][2])
        doors.append(Point(c).buffer(d['width'] / 2 + float(arg('--door-clear', 0.55))))
    rects = footprint(risers, width, tread)
    shape = [box(*r) for r in rects]
    xs = [r[0] for r in rects] + [r[2] for r in rects]; zs = [r[1] for r in rects] + [r[3] for r in rects]
    print(f"room {room['id'][:8]} area {Polygon(room['floor']).area:.1f} m2; stair footprint {max(xs)-min(xs):.2f} x {max(zs)-min(zs):.2f} m (risers {risers}, width {width}, tread {tread})")
    minx, minz, maxx, maxz = Polygon(room['floor']).bounds
    found = []
    for mirror in (False, True):
        for turn in range(0, 360, 90):
            parts = [affinity.scale(s, xfact=-1 if mirror else 1, origin=(0, 0)) for s in shape]
            parts = [affinity.rotate(s, turn, origin=(0, 0)) for s in parts]
            x = minx - 3
            while x < maxx + 3:
                z = minz - 3
                while z < maxz + 3:
                    moved = [affinity.translate(s, x, z) for s in parts]
                    if all(P.contains(m) for m in moved) and not any(m.intersects(dz) for m in moved for dz in doors):
                        found.append((round(x, 2), round(z, 2), turn, mirror))
                    z += 0.1
                x += 0.1
    print(len(found), 'placements fit' if found else 'placements fit - the stair does not fit this room with these numbers')
    for f in found[:12]: print('  start (%.2f, %.2f) turn %d deg mirror %s' % f)

if __name__ == '__main__': main()
