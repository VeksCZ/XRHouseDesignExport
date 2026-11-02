# Úkol pro Claude na PC: úklid modelu "Novostavba" (na KOPII scanu)

Stav: zadáno 2026-11-02. Cloudová session nemá scany (jsou v .gitignore, jen na PC), proto to musí začít tady.

## Co chce uživatel
- Zdi v Novostavbě nejsou hezky zarovnané, někde prosvítají díry, na některých zdech je výstupek (nejspíš zeď jiné místnosti),
  dveře v dětském pokoji se zavírají do zdi.
- Cíl: vše klasicky - pravé úhly zdí, žádné výstupky ani díry.
- Střecha: má být nad obývákem a protažená přes terasu až tam, kde končí prostřední část stavby; zvenku nedosedá na zeď nad terasou.
- Schody v novém modelu (uživatel, 2026-11-02): ze spodního patra v chodbě, začínají na levé straně u dveří ze vstupní chodby
  do domu. Dva schody směrem ke stěně, tam podesta, dalších x schodů podél stěny, další podesta, pak dva nebo tři schody do
  horního patra. Zábradlí kovo-dřevěné, i nahoře nad vzniklou dírou (otvorem ve stropu/podlaze horního patra).
- Pracuj na KOPII scanu (např. složka `Scan_Novostavba Osove_clean`), originál nesahej.

## Postup
1. ~~Spusť `python3 Tools/extract_scan_geometry.py "Exports/RoomData/Scan_Novostavba Osove/92_Data_Scan.scene.json"
   novostavba.geometry.json`~~ - hotovo (2026-11-02): 15 místností, 68 zdí, 29 dveří, 17 oken, běžel bez úprav nástroje.
   `novostavba.geometry.json` + `93_Data_Edits.json` + `95_Data_Furniture.json` té scény jsou v tomto commitu.
2. Kam sahat v kódu: `XRModelFactory` (SnapToPerpendicular, EstimateWallThickness, otvory/díry), `DoorCatalog` (otevírání dveří),
   `Furniture.InstallCeilings` / střechy (RoofPart), `StairGeometry` + `StairFloorSnap`.
3. Před commitem C#: `python3 Tools/OfflineTests/FullCompile/check.py --base <poslední revize, co PC přeložil>` (a `--player`).

## Navíc
Uživatel nemá nic proti dalším logům, pokud pomohou najít zbytečná volání a zrychlit (GetValidRooms, přestavby pohledů).
Větev `claude/new-session-gmxcr2` má ~26 dalších commitů proti main - slouč ji u sebe obvyklým způsobem.
