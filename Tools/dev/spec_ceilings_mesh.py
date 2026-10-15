import json, shutil
p = r'C:\Users\veks\source\repos\XRHouseDesignExport\Exports\RoomData\Scan_20261002_0848_koupelna_wc\95_Data_Furniture.json'
shutil.copy(p, p + '.bak5')
d = json.load(open(p, encoding='utf-8'))
up = [5.3355, 0, -0.145]
d['ceilings'] = [
    {"name": "z_meshe", "rooms": ["6DF8", "6590", "E040", "1C9C", "3BF3", "34B2", "2E5E"], "from": "mesh",
     "note": "sklon stropu dopocitany ze skenovane site (CeilingFit) - jen kde je strop dost nasnimany"},
    {"name": "odhad", "rooms": ["93EE", "Satna"], "plane": up,
     "note": "WC (sit tam strop nema) a satna (neskenovana): pultova rovina horniho patra, GLB y = a + bx*x + bz*z"},
]
walls = {
    "Strecha_hlavni": {"x0": -7.24, "x1": 6.26, "z0": -2.39, "z1": 3.68, "drop": 0.6},
    "Strecha_obyvak": {"x0": -4.59, "x1": 4.64, "z0": -7.62, "z1": -2.30, "drop": 0.6},
    "Strecha_garaz": {"x0": -3.46, "x1": 4.69, "z0": 3.43, "z1": 10.01, "drop": 0.6},
}
for r in d['roofs']:
    if r['name'] in walls: r['walls'] = walls[r['name']]
open(p, 'w', encoding='utf-8').write(json.dumps(d, ensure_ascii=False, indent=2))
shutil.copy(p, r'C:\Users\veks\source\repos\XRHouseDesignExport\Exports\RoomData\Export_20261002_105316_20261002_0848_15rooms\95_Data_Furniture.json')
print('ok')
