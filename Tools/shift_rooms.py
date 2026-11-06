"""Shift whole rooms of a saved scan (V1 scene JSON) by a horizontal offset - for rooms the headset placed a bit off
(two rooms either side of a 50 cm wall scanned only 15-20 cm apart). Works on a COPY of a scan folder:

    python Tools/shift_rooms.py <src scan folder> <dst scan folder> ROOM=dx,dz [ROOM=dx,dz ...] [--furniture-z GLBDZ:NAME,NAME ...]

ROOM = the first 4+ hex digits of the room's Unity uuid (as the app prints it: 2E5E, 34B2); dx, dz in metres in the
app's scan frame (Unity axes; the JSON stores z negated). Moves every anchor of the room (floor, ceiling, walls,
doors, windows) and the room-local door / stair / hole edits of that room (a room of a V1 scan sits at the origin, so
room-local = scan frame). Routes and wall photos are not touched (check none are in the moved rooms).
The furniture spec is in the GLB frame and is edited separately (see the call in the notes)."""
import json, shutil, sys, uuid, os


def unity_to_json_uuid(u):
    """The scene JSON stores a UUID with its first three groups byte-reversed against Unity's Guid.ToString()."""
    b = uuid.UUID(u).bytes
    return str(uuid.UUID(bytes=b[3::-1] + b[5:3:-1] + b[7:5:-1] + b[8:]))


def main():
    src, dst = sys.argv[1], sys.argv[2]
    shifts = {}
    for a in sys.argv[3:]:
        k, v = a.split("=")
        dx, dz = (float(x) for x in v.split(","))
        shifts[k.lower()] = (dx, dz)
    if os.path.exists(dst):
        sys.exit(f"{dst} exists - refusing to overwrite")
    shutil.copytree(src, dst)

    scene_p = os.path.join(dst, "92_Data_Scan.scene.json")
    scene = json.load(open(scene_p, encoding="utf-8"))
    ents = {e["uuid"]: e for e in scene["spatialEntities"]}
    rooms = [e for e in scene["spatialEntities"] if "roomLayoutMETA" in e]
    moved = {}
    for r in rooms:
        # Unity uuid = JSON uuid with the first three groups byte-reversed (the same swap both ways)
        unity = unity_to_json_uuid(r["uuid"])
        key = next((k for k in shifts if unity.lower().startswith(k)), None)
        if key is None:
            continue
        dx, dz = shifts[key]
        n = 0
        for cu in r.get("spaceContainerMETA", {}).get("uuids", []):
            e = ents.get(cu)
            loc = e and e.get("locatableMETA")
            if not loc:
                continue
            loc["position"]["x"] += dx
            loc["position"]["z"] -= dz          # JSON z = -Unity z
            n += 1
        moved[unity] = (dx, dz)
        print(f"room {unity[:8]}: {n} anchors moved by ({dx:+.3f}, {dz:+.3f})")
    missing = [k for k in shifts if not any(u.lower().startswith(k) for u in moved)]
    if missing:
        sys.exit(f"rooms not found: {missing}")
    json.dump(scene, open(scene_p, "w", encoding="utf-8"), separators=(",", ":"))

    edits_p = os.path.join(dst, "93_Data_Edits.json")
    if os.path.exists(edits_p):
        ed = json.load(open(edits_p, encoding="utf-8"))
        def mv(p, d):
            p["x"] += d[0]; p["z"] += d[1]
        c = 0
        for door in ed.get("doors", []):
            d = moved.get(door.get("roomUuid", ""))
            if d and door.get("localPos"):
                mv(door["localPos"], d); c += 1
        for s in ed.get("stairs", []):
            d = moved.get(s.get("roomUuid", ""))
            if d:
                for p in s.get("localPoints", []):
                    mv(p, d)
                c += 1
        for h in ed.get("holes", []):
            d = moved.get(h.get("roomUuid", ""))
            if d:
                mv(h["localA"], d); mv(h["localB"], d); c += 1
        json.dump(ed, open(edits_p, "w", encoding="utf-8"), indent=2)
        print(f"{c} edit(s) moved with their rooms")


if __name__ == "__main__":
    main()
