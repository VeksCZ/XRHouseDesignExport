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
takže "výstupek" nevzniká v půdorysu.

**Oprava dřívějšího odhadu (zdi přes patro):** místnosti 34b261d2 a 2e5efb11 mají strop na y +0,76 / +0,67 (4 m nad podlahou), ale jejich
půdorys se s žádnou místností horního patra NEPŘEKRÝVÁ (průnik 0 m2) - zdi tedy neprocházejí podlahou horního patra. Jde o křídlo domu
s vysokým stropem vedle horního bloku: jejich dlouhé zdi (da4cac 8,71 m a ff59ed 7,54 m) vedou podél hranic horních pokojů (1,3 - 5 m u
6df884, 93ee51, 1c9c52, 3bf3e3, e040fd, 6590e5) a nad y 0 (do 0,76) leží vedle zdí horních pokojů - desky obou zdí se tam překrývají
(z-fighting), ne vyčnívají do pokoje. Zdi už končí na stropu své místnosti (anchor top = ceilingY; spec `ceilings` "z_meshe" je navíc
sklání podle sítě), takže ořez na "skutečnou výšku" nic nezmění. Co zkontrolovat v render pohledu (`-test views`): zda zeď da4cac / ff59ed
nad y 0 není vidět zevnitř horního pokoje jako lišta u stropu/podlahy (pak ořezat desku zdi nižšího křídla na y podlahy horní místnosti
tam, kde s ní sousedí).

## Schody (edit d84267ab, místnost 162b8c39 = chodba dole) - UZAVŘENO, nic nepřesouvat
Uživatel (2026-11-02): schody stojí jen v dolní chodbě a končí v horní chodbě; horní podesta a horní tři schody zasahují nad strop
neskenované pracovny (`Pracovna_dole`) a nahoře je o to kratší šatna (neskenovaná `Satna`), její stěna je posunutá oproti pracovně.
- Dráha (room-local): P0 (3,61; 0,75), P1 (3,47; -0,51), P2 (-0,33; -0,10), P3 (-0,19; 1,16), šířka 0,9, risers [3, 11, 3].
- Pásmo schodů (5,7 m2): 4,0 m2 leží v dolní chodbě 162b8c39, 5,2 m2 v horní chodbě 6590e5f3, 1,15 m2 mimo dolní chodbu nad horní chodbou
  (= nad pracovnou) a jen 0,5 m2 (konec horního ramene) mimo obě naskenované chodby - to je prostor `Satna` (neskenovaná, v spec jako
  `extra_rooms`); otvor `holes` č. 2 (z 0,30 .. 1,21) je nakreslen přesně tam. Spec to už odráží: `Satna` má x1 = -3,27 oproti
  `Pracovna_dole` x1 = -2,26 a pracovna má snížený strop 2,42 m pod horním ramenem (`low_ceilings`).
- Takže P2 / P3 "mimo chodbu" jsou správně a `Tools/stairs_fit.py` (U-schodiště se do SKENOVANÉ chodby nevejde) to jen potvrzuje: schody
  zasahují do neskenovaných částí domu. Další zásah není potřeba.

## Střecha (95_Data_Furniture.json -> roofs)
- `Strecha_obyvak` končí na z1 = -2,30 (zasahuje do budovy), `Strecha_terasa` na z1 = -2,64 = přesně čára zdí hlavní střechy
  (`walls.z0`). Terasa tak může zvenku zůstat o škvíru před zdí nad terasou.
- Návrh: `Strecha_terasa.z1 = -2,30` (jako obývák) -> `docs/novostavba/95_Data_Furniture.proposed.json` (jediný řádek, originál nedotčen).
  Ověřit v render pohledu zvenku; pokud by trčela přes zeď, vrátit na -2,64.
