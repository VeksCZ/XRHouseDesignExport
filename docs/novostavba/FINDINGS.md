# Novostavba - nálezy z geometrie (cloud, 2026-11-02)

Zdroj: `novostavba.geometry.json` (15 místností, 2 patra: podlaha ~ -3,2 a ~ 0,0), `93_Data_Edits.json`, `95_Data_Furniture.json`.
Nástroje: `Tools/analyze_scan_geometry.py`, `Tools/simulate_wall_slabs.py` (půdorysná simulace tlouštěk zdí z `EstimateWallThickness`).

## Scan sám je čistý
- Všechny zdi do 1,6 deg od pravého úhlu (3 zdi nad 1,5 deg); model je stejně srovnává `SnapToPerpendicular`.
- Půdorysy podlah se nepřekrývají (jediný průnik 0,11 m2, místnosti 6df884 / 3bf3e3 v horním patře).
- **Jediná díra v rohu:** místnost b3293e15, zdi 3ff743b7 a a1ff496a, konce (-4.31, -4.12) a (-4.11, -4.10), mezera 20 cm.
  Nástroj "Missing wall" (SceneJsonEdit.AddWall) ji uzavře na kopii scanu.
- Dveře jsou u konce zdi do 15 cm téměř všude (0-13 cm): běžný stav u dveří v rohu, ne chyba scanu. Dveře do rohu = kandidát na
  "dveře dětského pokoje se zavírají do zdi" - tam je třeba hlídat směr otevírání (hinge / opensInto), ne geometrii scanu.

## Sousední zdi
29 dvojic rovnoběžných zdí sousedních místností (mezera 2-30 cm; většinou 2-17 cm). Mezera >0,5 m se nepáruje (použije se 25 cm).
17 zdí nemá sousedku (venkovní zdi = fallback 25 cm). Simulace desek: jen 3 průniky do sousední místnosti (6 cm2 / 3 cm2 / 3 cm2),
takže "výstupek" nevzniká v půdorysu. Podezřelé zbývá **výška**: velké místnosti 34b261d2 a 2e5efb11 (dolní patro, 44 a 47 m2)
mají strop na y = +0,76 / +0,67 (4 m nad podlahou -3,24), tj. jejich zdi procházejí horním patrem (podlaha y ~ 0). To je
nejpravděpodobnější "výstupek" na zdech horního patra - ověř v render pohledu (`-test views`) zdi těchto dvou místností.

## Schody (edit d84267ab, místnost 162b8c39 = chodba dole)
- Dráha (room-local): P0 (3,61; 0,75), P1 (3,47; -0,51), P2 (-0,33; -0,10), P3 (-0,19; 1,16), šířka 0,9, risers [3, 11, 3].
- **P2 a P3 leží mimo půdorys chodby** (o 0,54 a 0,65 m za západní zdí 291071). Severní část chodby je široká jen ~3,7 m
  (x 0,15 .. 3,89), schody potřebují ~4,8 m (11 stupňů x 0,27 = 2,97 m + dvě podesty 0,9) -> nevejdou se. Buď kratší rameno
  (např. 2 / 8 / 2 stupně nebo menší podesty), nebo jiné místo. Zadání: "vlevo u dveří ze vstupní chodby".
- Který z dveří je vstupní? Chodba má dveře: 0e7188 (1,76; -2,09), ebcca3 (4,13; -1,21), bd3274 (3,89; -4,51), 831a9c (4,31; -2,84),
  043b44 (0,07; -2,27), 73c163 (-0,63; -1,18). Bez odpovědi uživatele schody nepřesouvám.

## Střecha (95_Data_Furniture.json -> roofs)
- `Strecha_obyvak` končí na z1 = -2,30 (zasahuje do budovy), `Strecha_terasa` na z1 = -2,64 = přesně čára zdí hlavní střechy
  (`walls.z0`). Terasa tak může zvenku zůstat o škvíru před zdí nad terasou.
- Návrh: `Strecha_terasa.z1 = -2,30` (jako obývák) -> `docs/novostavba/95_Data_Furniture.proposed.json` (jediný řádek, originál nedotčen).
  Ověřit v render pohledu zvenku; pokud by trčela přes zeď, vrátit na -2,64.
