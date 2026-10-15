"""Site of RD Osove (parcela 442/3) for the furniture spec, traced from the situation plan C.3 (1:250, north up)
fitted onto the scanned house (garage = north-north-east end, its door to the west). GLB axes, metres."""
import json, shutil
p = r'C:\Users\veks\source\repos\XRHouseDesignExport\Exports\RoomData\Scan_20261002_0848_koupelna_wc\95_Data_Furniture.json'
shutil.copy(p, p + '.bak6')
d = json.load(open(p, encoding='utf-8'))
G = -1.47   # terrain: 15 cm below the ground floor (-1.32)

def bx(z):  # parcel boundary towards the road (15/5, 15/1), x at z
    return 10.71 - 0.01627 * (z - 13.62)

d['site'] = {
    "note": "RD Osove, parc. 442/3 - z koordinacni situace C.3; sever = GLB (0.342, 0.940), garaz na SSV",
    "lat": 49.325, "lon": 16.015, "north": [0.342, 0.940],
    "ground_y": G,
    "ground": {"size": 180, "color": "grass"},
    "areas": [
        {"name": "pole_42_5", "color": "field", "y": G + 0.01,
         "poly": [[-16.6, 14.7], [-80, 14.0], [-80, -90], [-17.0, -90]]},
        {"name": "mez_42_7", "color": "bank",
         "poly": [[10.71, G + 0.01, 13.62], [-15.97, G + 0.01, 14.71], [-16.6, G + 2.6, 32], [11.0, G + 2.6, 32]]},
        {"name": "cesta_15_5", "color": "gravel", "y": G + 0.03,
         "poly": [[8.9, -2.6], [11.5, -3.0], [14.6, 34], [12.0, 34]]},
        {"name": "zamkova_dlazba", "color": "paving", "y": -1.40,
         "poly": [[10.82, 9.92], [4.69, 10.3], [4.69, 1.4], [10.02, 1.11]]},
        {"name": "terasa", "color": "concrete", "y": -1.35,
         "poly": [[-7.55, -7.95], [-4.59, -7.95], [-4.59, -2.39], [-7.55, -2.39]]},
    ],
    "fences": [
        {"line": [[10.71, 13.62], [bx(9.2), 9.2]], "h": 1.8},
        {"line": [[bx(4.5), 4.5], [bx(-52.7), -52.7]], "h": 1.8},
    ],
    "hedges": [{"line": [[-16.05, -6.0], [-16.35, -53.0]], "w": 1.6, "h": 2.6}],
    "trees": [{"at": [x, -27.0], "h": 4.2, "r": 1.6} for x in (-11, -5, 1, 7)] +
             [{"at": [x, -35.0], "h": 4.0, "r": 1.5} for x in (-8, -2, 4)],
    "buildings": [
        {"name": "stodola_19_1", "x0": 21.7, "x1": 30.6, "z0": -6.5, "z1": 6.4, "eave": 3.5, "ridge": 7.0},
        {"name": "stodola_19_2", "x0": 22.8, "x1": 31.8, "z0": -18.0, "z1": -11.9, "eave": 3.2, "ridge": 6.2},
    ],
}
# terrace: the living-room roof carried over it, on one wooden post in the outer corner
roofs = [r for r in d['roofs'] if r['name'] not in ('Strecha_terasa', 'Sloup_terasa')]
liv = next(r for r in roofs if r['name'] == 'Strecha_obyvak')
roofs.append({"name": "Strecha_terasa", "x0": -7.85, "x1": -4.59, "z0": -8.02, "z1": -2.39, "plane": liv['plane'], "t": 0.3})
post_top = liv['plane'][0] + liv['plane'][2] * -7.70
roofs.append({"name": "Sloup_terasa", "x0": -7.55, "x1": -7.40, "z0": -7.95, "z1": -7.80, "plane": [-1.35, 0, 0],
              "t": round(post_top + 1.35, 3), "color": "wood"})
d['roofs'] = roofs
open(p, 'w', encoding='utf-8').write(json.dumps(d, ensure_ascii=False, indent=2))
shutil.copy(p, r'C:\Users\veks\source\repos\XRHouseDesignExport\Exports\RoomData\Export_20261002_105316_20261002_0848_15rooms\95_Data_Furniture.json')
print('ok', post_top)
