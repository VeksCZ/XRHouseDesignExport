#!/usr/bin/env python3
"""Lists every saved scan (a folder with 92_Data_Scan.scene.json) and its scene format: V1 (one entity per wall / door, roomLayoutMETA)
or V2 (HiFi: one room mesh with labelled faces, roomMeshMETA) - which of them MRUK 207 (Scene V2 disabled) could not load as before.

    python3 Tools/list_scan_formats.py [Exports/RoomData]          (default: Exports/RoomData)
    also the headset's ScanCache after "Pull" (they come as Scan_<name> folders in the same place)

Reads the files as text (no JSON parsing: the global mesh makes them big). Output: one line per scan, then a summary."""
import os, sys

def kind(path):
    v1 = v2 = 0
    with open(path, 'r', encoding='utf-8', errors='ignore') as f:
        tail = ''
        while True:
            chunk = f.read(4 << 20)
            if not chunk: break
            buf = tail + chunk
            v1 += buf.count('"roomLayoutMETA"'); v2 += buf.count('"roomMeshMETA"')
            tail = buf[-32:]
    return v1, v2

def main():
    root = sys.argv[1] if len(sys.argv) > 1 else 'Exports/RoomData'
    rows = []
    for name in sorted(os.listdir(root)):
        p = os.path.join(root, name, '92_Data_Scan.scene.json')
        if not os.path.isfile(p): continue
        v1, v2 = kind(p)
        fmt = 'V2' if v2 and not v1 else 'V1' if v1 and not v2 else 'MIXED' if v1 and v2 else '?'
        rows.append((name, fmt, v1, v2, os.path.getsize(p) / 1048576))
    for n, f, v1, v2, mb in rows:
        print(f"{f:5} {n}   (V1 rooms {v1}, V2 rooms {v2}, {mb:.1f} MB)")
    print(f"\n{len(rows)} scan(s): V1 {sum(r[1]=='V1' for r in rows)}, V2 {sum(r[1]=='V2' for r in rows)}, mixed {sum(r[1]=='MIXED' for r in rows)}, unknown {sum(r[1]=='?' for r in rows)}")
    if any(r[1] in ('V2', 'MIXED') for r in rows):
        print("V2 / MIXED scans must be test-loaded before upgrading to MRUK 207 (see docs/UPGRADE_META_207.md).")

if __name__ == '__main__': main()
