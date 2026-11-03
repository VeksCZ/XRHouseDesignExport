#!/usr/bin/env python3
"""Applies the cloud's proposals to a scan FOLDER (a copy - e.g. Exports/RoomData/Scan_Novostavba Osove_clean):
  - docs/novostavba/95_Data_Furniture.proposed.json  ->  <scan>/95_Data_Furniture.json   (furniture, entrance hall, wardrobes, TV unit, coffee table,
    terrain, roof tweak, window of the kumbal, house_style switches: blinds / entrance door / window frames)
  - docs/novostavba/93_Data_Edits.windows.proposed.json -> window edits merged into <scan>/93_Data_Edits.json (the contract's sizes + parts);
    the scan's own other edits (doors, stairs, openings) are kept; windows with the same anchor are replaced.
Backups: <file>.before_proposed. Nothing else is touched; run with --dry to see what would change.

    python3 Tools/apply_proposed_to_scan.py "Exports/RoomData/Scan_Novostavba Osove_clean" [--dry]"""
import json, os, shutil, sys

def main():
    if len(sys.argv) < 2: print(__doc__); sys.exit(1)
    scan = sys.argv[1]; dry = '--dry' in sys.argv
    here = os.path.dirname(os.path.abspath(__file__)); docs = os.path.join(here, '..', 'docs', 'novostavba')
    fur_src = os.path.join(docs, '95_Data_Furniture.proposed.json'); ed_src = os.path.join(docs, '93_Data_Edits.windows.proposed.json')
    fur_dst = os.path.join(scan, '95_Data_Furniture.json'); ed_dst = os.path.join(scan, '93_Data_Edits.json')
    if not os.path.isdir(scan): print('not a folder:', scan); sys.exit(1)
    # furniture: the proposal replaces the file (it is the original + additions)
    print(f'furniture: {fur_src} -> {fur_dst}')
    if not dry:
        if os.path.exists(fur_dst): shutil.copy2(fur_dst, fur_dst + '.before_proposed')
        shutil.copy2(fur_src, fur_dst)
    # window edits: merge by anchor
    prop = json.load(open(ed_src, encoding='utf-8'))
    wins = [d for d in prop['doors'] if d.get('sections') or d.get('widthMm')]
    cur = json.load(open(ed_dst, encoding='utf-8')) if os.path.exists(ed_dst) else {'schemaVersion': prop.get('schemaVersion', 1), 'doors': [], 'stairs': [], 'holes': []}
    keys = {w['anchorUuid'] for w in wins}
    before = len(cur['doors'])
    cur['doors'] = [d for d in cur['doors'] if d['anchorUuid'] not in keys] + wins
    print(f'window edits: {len(wins)} from the proposal merged ({before} -> {len(cur["doors"])} door / window edits)')
    if not dry:
        if os.path.exists(ed_dst): shutil.copy2(ed_dst, ed_dst + '.before_proposed')
        json.dump(cur, open(ed_dst, 'w', encoding='utf-8'), indent=2)
    print('dry run, nothing written' if dry else 'done')

if __name__ == '__main__': main()
