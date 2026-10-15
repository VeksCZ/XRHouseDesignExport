"""2026-10-15: 50 cm outer walls (roofs/attic walls/terrace moved out by the extra 25 cm), new ground-floor layout:
bathroom in the former walk-in closet (49A5), utility room (6461) with the washer/dryer in a cabinet wall + laundry
chute + sink, the room behind the stairs (extra room) becomes a study with a desk."""
import json, shutil
p = r'C:\Users\veks\source\repos\XRHouseDesignExport\Exports\RoomData\Scan_20261002_0848_koupelna_wc\95_Data_Furniture.json'
shutil.copy(p, p + '.bak7')
d = json.load(open(p, encoding='utf-8'))
d['walls'] = {"exterior": 0.5, "note": "obvodove zdi 50 cm (cihla 50 s presahem pres zakladovku), pricky ~15 cm (dopocitane ze skenu)"}

# roofs / attic walls: outer faces now 50 cm out from the scanned faces
R = {r['name']: r for r in d['roofs']}
def set_box(o, x0, x1, z0, z1): o.update({"x0": x0, "x1": x1, "z0": z0, "z1": z1})
set_box(R['Strecha_hlavni'], -7.89, 6.91, -3.04, 4.33)
set_box(R['Strecha_hlavni']['walls'], -7.49, 6.51, -2.64, 3.93)
set_box(R['Strecha_obyvak'], -5.24, 5.29, -8.27, -2.30)
set_box(R['Strecha_obyvak']['walls'], -4.84, 4.89, -7.87, -2.30)
set_box(R['Strecha_garaz'], -4.11, 5.34, 3.43, 10.66)
set_box(R['Strecha_garaz']['walls'], -3.71, 4.94, 3.43, 10.26)
set_box(R['Strecha_terasa'], -8.10, -4.84, -8.27, -2.64)
liv = R['Strecha_obyvak']['plane']
post = R['Sloup_terasa']
set_box(post, -7.80, -7.65, -8.15, -8.00)
post['t'] = round(liv[0] + liv[2] * -8.07 + 1.35, 3)
for a in d['site']['areas']:
    if a['name'] == 'terasa': a['poly'] = [[-7.80, -8.20], [-4.84, -8.20], [-4.84, -2.64], [-7.80, -2.64]]
    if a['name'] == 'zamkova_dlazba': a['poly'] = [[10.82, 9.92], [4.94, 10.3], [4.94, 1.4], [10.02, 1.11]]
for er in d['extra_rooms']:
    er['wall_t'] = 0.5
    if er['name'] == 'Pokoj_dole':
        er['name'] = 'Pracovna_dole'
        er['note'] = 'pracovna (puvodne koupelna ve vykresu) - dole za schody, dvere z chodby'

def room(key):
    for r in d['rooms']:
        if r['room'].lower().startswith(key.lower()): return r
    r = {"room": key, "items": []}; d['rooms'].append(r); return r

# new bathroom (was the walk-in closet): shower and washbasin on the left as you come in (-x wall)
room('49A5')['items'] = [
    {"type": "shower", "name": "Sprcha_dole", "wall": "-x", "along": 0.0, "w": 0.9, "d": 0.9,
     "glass": [{"side": "front", "sliding": True}]},
    {"type": "washbasin", "name": "Umyvadlo_koupelna_dole", "wall": "-x", "along": 1.25, "w": 0.6, "d": 0.45, "mirror_off": 0.05},
]
# utility room: washer + dryer raised 30 cm in a cabinet wall (+x), laundry chute in the corner by the door,
# sink in the back corner opposite
room('6461')['items'] = [
    {"type": "box", "name": "Sokl_pracka", "wall": "+x", "along": 0.05, "w": 1.25, "d": 0.6, "h": 0.3, "color": "fronts"},
    {"type": "washer", "name": "Pracka", "wall": "+x", "along": 0.07, "w": 0.6, "y": 0.3},
    {"type": "washer", "name": "Susicka", "wall": "+x", "along": 0.68, "w": 0.6, "y": 0.3},
    {"type": "box", "name": "Skrin_nad_prackou", "wall": "+x", "along": 0.05, "w": 1.25, "d": 0.6, "y": 1.2, "h": 1.1, "color": "fronts"},
    {"type": "box", "name": "Skrin_vysoka", "wall": "+x", "along": 1.3, "w": 0.75, "d": 0.6, "h": 2.3, "color": "fronts"},
    {"type": "box", "name": "Shoz_pradla", "wall": "+x", "along": 2.05, "w": 0.5, "d": 0.6, "h": 2.3, "color": "#dcdcdc"},
    {"type": "washbasin", "name": "Drez", "wall": "-x", "along": 0.0, "w": 0.6, "d": 0.5},
]
# study behind the stairs (extra room x -7.20..-2.26, z 1.66..3.43): desk + chair at the back wall
b = room('B329')
b['items'] = [i for i in b['items'] if i.get('name') != 'Psaci_stul_pracovna']
b['items'].append({"type": "desk", "name": "Psaci_stul_pracovna", "wall": "-x", "x": -7.20, "z": 1.85, "w": 1.4, "d": 0.7})
open(p, 'w', encoding='utf-8').write(json.dumps(d, ensure_ascii=False, indent=2))
shutil.copy(p, r'C:\Users\veks\source\repos\XRHouseDesignExport\Exports\RoomData\Export_20261002_105316_20261002_0848_15rooms\95_Data_Furniture.json')
print('ok', post['t'])
