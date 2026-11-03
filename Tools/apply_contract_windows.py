#!/usr/bin/env python3
"""Writes the Ryzi Okna contract (SOD 251000, Veka Softline 82-MD) into a COPY of a scan's 93_Data_Edits.json:
  - widthMm / heightMm: the real frame size of every matched window (the model draws it centred in the scanned rough opening);
  - the parts of a window: where the user already set them (kind + hinge side) they are KEPT and only get their contract widths; where
    a window has no edit yet, the parts come from the contract drawing (interior view, tilt-turn = 8, fixed = 6; hinge 0 = left, 1 = right).
Mirrored items are placed by where the windows really are (seen from inside the room; see the notes in docs/novostavba/OKNA_RYZI.md).

    python3 Tools/apply_contract_windows.py <edits.json> <geometry.json> <out.json>

The original file is never written."""
import json, sys

FIXED, TT = 6, 8; L, R = 0, 1
# window anchor id prefix -> (contract item, width mm, height mm, parts [(kind, hinge, width mm)] for a window WITHOUT an edit)
PLAN = {
    '0da02b': ('#8 2NP pokoj vpravo',            1720, 1195, [(TT, L, 733), (TT, R, 987)]),
    '95853a': ('#9 2NP pokoj vlevo',             1720, 1195, [(TT, L, 733), (TT, R, 987)]),
    'b6b99a': ('#10 2NP detsky pokoj vlevo',     1220,  950, [(TT, L, 1220)]),               # handle on the right -> hinge left
    'fb2b7e': ('#11 2NP WC',                      720,  950, [(TT, R, 720)]),                 # handle on the left -> hinge right
    '6740bf': ('#12 2NP koupelna, vetsi kridlo (vlevo)', None, None, [(TT, L, 1266)]),       # scan splits the window in two anchors
    '952551': ('#12 2NP koupelna, mensi kridlo (vpravo)', None, None, [(TT, R, 704)]),
    '70936a': ('#13 2NP loznice',                2520, 1445, [(TT, L, 1087), (TT, R, 1433)]),
    '785c4e': ('#1 1NP garaz, vlevo (klika vpravo)', 1220, 950, [(TT, L, 1220)]),            # the left window of the pair = item 1
    '548077': ('#2 1NP garaz, vpravo (klika vlevo)', 1220, 950, [(TT, R, 1220)]),            # the right one = item 2 (handles towards the middle)
    'd908c8': ('#3 1NP kuchyn',                  2220, 1560, [(TT, L, 750), (FIXED, R, 1470)]),
    '79a321': ('#5 1NP HS portal',               2700, 2490, [(2, R, 1350), (FIXED, L, 1350)]),
    'd90eeb': ('#6 1NP pracovna, pevna cast vlevo', 1450, 2440, [(FIXED, L, 1450)]),         # the pair is mirrored against the drawing: fixed part LEFT,
    '03389a': ('#6 1NP pracovna, dvere vpravo',  1050, 2440, [(TT, R, 1050)]),               # tilt-turn door RIGHT (the user's hinge: right)
}

def main():
    edits_p, geo_p, out_p = sys.argv[1:4]
    e = json.load(open(edits_p, encoding='utf-8')); g = json.load(open(geo_p, encoding='utf-8'))
    wins = {}
    for r in g['rooms']:
        for w in r['windows']: wins[w['id'][:6]] = (r, w)
    changed = added = 0
    for key, (item, wmm, hmm, parts) in PLAN.items():
        if key not in wins: print('not in the scan:', key); continue
        r, w = wins[key]
        hit = [d for d in e['doors'] if d['anchorUuid'].startswith(key)]
        if hit:
            for d in hit:
                secs = d.get('sections') or []
                if len(secs) == len(parts):                           # keep the user's kinds and hinges, add the contract widths
                    for s, (k, h, wd) in zip(secs, parts): s['width'] = wd
                    note = 'user parts kept, widths added'
                else: note = f'user has {len(secs)} part(s), contract {len(parts)} - parts left alone'
                if wmm: d['widthMm'], d['heightMm'] = wmm, hmm
            changed += 1
        else:
            c = w['center']
            e['doors'].append({'anchorUuid': w['id'], 'roomUuid': r['id'], 'localPos': {'x': c[0], 'y': c[1], 'z': -c[2]}, 'kind': 0, 'hinge': 0,
                               'opensIntoRoomUuid': r['id'], 'sections': [{'kind': k, 'hinge': h, 'width': wd} for k, h, wd in parts],
                               **({'widthMm': wmm, 'heightMm': hmm} if wmm else {})})
            added += 1; note = 'new edit from the contract'
        print(f'{key}  {item}: {note}')
    json.dump(e, open(out_p, 'w', encoding='utf-8'), indent=2)
    print(f'{changed} existing window edit(s) updated, {added} added -> {out_p}')

if __name__ == '__main__': main()
