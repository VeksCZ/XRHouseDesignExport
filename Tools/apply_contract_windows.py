#!/usr/bin/env python3
"""Writes the window parts of the Ryzi Okna contract (SOD 251000, Veka Softline 82-MD) into a COPY of a scan's 93_Data_Edits.json:
for every scan window the contract item that fits its size gets its `sections` (kind, hinge, width in mm). Needs novostavba.geometry.json.

    python3 Tools/apply_contract_windows.py <edits.json> <geometry.json> <out.json>

Section kinds: 6 Fixed, 8 TiltTurn (otevirave-sklopne); hinge 0 = left, 1 = right, seen from the room (interior view in the contract).
The original file is never written."""
import json, sys

FIXED, TT = 6, 8; L, R = 0, 1
# window anchor id prefix -> (contract item, sections [(kind, hinge, width_mm)])
PLAN = {
    '0da02b': ('#8/#9 2NP pokoj 1720x1195', [(TT, L, 733), (TT, R, 987)]),
    '95853a': ('#8/#9 2NP pokoj 1720x1195', [(TT, L, 733), (TT, R, 987)]),
    'b6b99a': ('#10 2NP detsky pokoj vlevo 1220x950', [(TT, L, 1220)]),
    'fb2b7e': ('#11 2NP WC 720x950', [(TT, R, 720)]),
    '6740bf': ('#12 2NP koupelna 1970x950, vetsi kridlo 1266', [(TT, L, 1266)]),
    '952551': ('#12 2NP koupelna 1970x950, mensi kridlo 704', [(TT, R, 704)]),
    '70936a': ('#13 2NP loznice 2520x1445', [(TT, L, 1087), (TT, R, 1433)]),
    '79a321': ('#4 1NP obyvak balkonove dvere 2520x2450 (fix 1470 + dvere 1050)', [(FIXED, L, 1470), (TT, R, 1050)]),
    'd908c8': ('#3 1NP kuchyn 2220x1560 (750 OS + fix 1470)', [(TT, L, 750), (FIXED, R, 1470)]),
}

def main():
    edits_p, geo_p, out_p = sys.argv[1:4]
    e = json.load(open(edits_p, encoding='utf-8')); g = json.load(open(geo_p, encoding='utf-8'))
    wins = {}
    for r in g['rooms']:
        for w in r['windows']: wins[w['id'][:6]] = (r, w)
    changed = added = 0
    for key, (item, secs) in PLAN.items():
        if key not in wins: print('not in the scan:', key); continue
        r, w = wins[key]
        sections = [{'kind': k, 'hinge': h, 'width': wd} for k, h, wd in secs]
        hit = [d for d in e['doors'] if d['anchorUuid'].startswith(key)]
        if hit:
            for d in hit: d['sections'] = sections
            changed += 1
        else:
            c = w['center']
            e['doors'].append({'anchorUuid': w['id'], 'roomUuid': r['id'], 'localPos': {'x': c[0], 'y': c[1], 'z': -c[2]},
                               'kind': 0, 'hinge': 0, 'opensIntoRoomUuid': r['id'], 'sections': sections})
            added += 1
        print(f'{key}  {item}  -> {len(secs)} part(s)')
    json.dump(e, open(out_p, 'w', encoding='utf-8'), indent=2)
    print(f'{changed} window edit(s) changed, {added} added -> {out_p}')

if __name__ == '__main__': main()
