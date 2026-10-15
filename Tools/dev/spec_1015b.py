"""2026-10-15 b: utility room - laundry chute cabinet in the back corner, washer + dryer in the middle of the wall;
the study's lowered ceiling under the stairs (the wall to the hall is closed)."""
import json, shutil
p = r'C:\Users\veks\source\repos\XRHouseDesignExport\Exports\RoomData\Scan_20261002_0848_koupelna_wc\95_Data_Furniture.json'
shutil.copy(p, p + '.bak8')
d = json.load(open(p, encoding='utf-8'))
for r in d['rooms']:
    if r['room'].lower().startswith('6461'):
        r['items'] = [
            {"type": "box", "name": "Shoz_pradla", "wall": "+x", "along": 0.0, "w": 0.55, "d": 0.6, "h": 2.3, "color": "#dcdcdc"},
            {"type": "box", "name": "Sokl_pracka", "wall": "+x", "along": 0.6, "w": 1.25, "d": 0.6, "h": 0.3, "color": "fronts"},
            {"type": "washer", "name": "Pracka", "wall": "+x", "along": 0.62, "w": 0.6, "y": 0.3},
            {"type": "washer", "name": "Susicka", "wall": "+x", "along": 1.23, "w": 0.6, "y": 0.3},
            {"type": "box", "name": "Skrin_nad_prackou", "wall": "+x", "along": 0.6, "w": 1.25, "d": 0.6, "y": 1.2, "h": 1.1, "color": "fronts"},
            {"type": "box", "name": "Skrin_vysoka", "wall": "+x", "along": 1.9, "w": 0.65, "d": 0.6, "h": 2.3, "color": "fronts"},
            {"type": "washbasin", "name": "Drez", "wall": "-x", "along": 0.0, "w": 0.6, "d": 0.5},
        ]
for e in d['extra_rooms']:
    if e['name'] == 'Pracovna_dole':
        # its own ceiling stops where the stairs pass over it; under them a lower one (the stairs' underside)
        e['ceiling_x1'] = -3.30
        e['low_ceilings'] = [{"x0": -3.30, "x1": -2.26, "z0": 1.66, "z1": 3.43, "h": 2.42}]
open(p, 'w', encoding='utf-8').write(json.dumps(d, ensure_ascii=False, indent=2))
shutil.copy(p, r'C:\Users\veks\source\repos\XRHouseDesignExport\Exports\RoomData\Export_20261002_105316_20261002_0848_15rooms\95_Data_Furniture.json')
print('ok')
