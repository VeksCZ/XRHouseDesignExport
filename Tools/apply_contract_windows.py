#!/usr/bin/env python3
"""Adds the part WIDTHS of the Ryzi Okna contract (SOD 251000) to the windows in a COPY of a scan's 93_Data_Edits.json. It keeps every
part's kind and hinge side exactly as the user set them (those are right) and never adds a window: only an existing window edit whose
number of parts equals the contract item's gets `width` (mm) on each part, so the parts keep the contract's proportions.

    python3 Tools/apply_contract_windows.py <edits.json> <out.json>

The original file is never written."""
import json, sys

# window anchor id prefix -> (contract item, widths in mm left to right as seen from the room)
WIDTHS = {
    'd908c8': ('#3 kuchyn 2220x1560', [750, 1470]),
    '70936a': ('#13 loznice 2520x1445', [1087, 1433]),
    '79a321': ('#5 HS portal 2700x2490 (2 x 1350)', [1350, 1350]),
}

def main():
    edits_p, out_p = sys.argv[1:3]
    e = json.load(open(edits_p, encoding='utf-8'))
    changed = 0
    for key, (item, widths) in WIDTHS.items():
        hit = [d for d in e['doors'] if d['anchorUuid'].startswith(key) and d.get('sections')]
        if not hit: print(f'{key} {item}: no window edit - left alone'); continue
        for d in hit:
            if len(d['sections']) != len(widths):
                print(f'{key} {item}: the edit has {len(d["sections"])} part(s), the contract {len(widths)} - left alone'); continue
            for s, w in zip(d['sections'], widths): s['width'] = w
            changed += 1; print(f'{key} {item}: widths {widths}, kinds / hinges kept')
    json.dump(e, open(out_p, 'w', encoding='utf-8'), indent=2)
    print(f'{changed} window edit(s) got widths -> {out_p}')

if __name__ == '__main__': main()
