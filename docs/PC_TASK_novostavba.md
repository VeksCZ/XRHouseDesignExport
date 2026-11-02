# Úkol pro Claude na PC: úklid modelu "Novostavba" (na KOPII scanu)

Stav: zadáno 2026-11-02. Cloudová session nemá scany (jsou v .gitignore, jen na PC), proto to musí začít tady.

## Co chce uživatel
- Zdi v Novostavbě nejsou hezky zarovnané, někde prosvítají díry, na některých zdech je výstupek (nejspíš zeď jiné místnosti),
  dveře v dětském pokoji se zavírají do zdi.
- Cíl: vše klasicky - pravé úhly zdí, žádné výstupky ani díry.
- Střecha: má být nad obývákem a protažená přes terasu až tam, kde končí prostřední část stavby; zvenku nedosedá na zeď nad terasou.
- Schody v novém modelu: uživatel už psal, kde a jak mají být - zeptej se ho, nebo najdi v jeho zprávách (cloud je nemá).
- Pracuj na KOPII scanu (např. složka `Scan_Novostavba Osove_clean`), originál nesahej.

## Postup
1. Spusť (z kořene projektu):
   `python3 Tools/extract_scan_geometry.py "Exports/RoomData/Scan_Novostavba Osove/92_Data_Scan.scene.json" novostavba.geometry.json`
   Výstup (desítky KB: stěny, dveře, okna, obrysy podlah, výšky; v souřadnicích souboru, x/z může být zrcadlově proti Unity) pak
   pošli do repozitáře (commit) spolu s `93_Data_Edits.json` a spec nábytku (`95_*`/Furniture JSON) té scény - cloudová session
   na nich opraví kód a ověří ho offline (WallTest / FullCompile).
2. Nástroj je nový a na reálném scanu ještě nebyl spuštěn - pokud selže nebo vrátí 0 zdí, oprav ho (formát V1 vs V2 je v kódu).
3. Kam sahat v kódu: `XRModelFactory` (SnapToPerpendicular, EstimateWallThickness, otvory/díry), `DoorCatalog` (otevírání dveří),
   `Furniture.InstallCeilings` / střechy (RoofPart), `StairGeometry` + `StairFloorSnap`.
4. Před commitem C#: `python3 Tools/OfflineTests/FullCompile/check.py --base <poslední revize, co PC přeložil>` (a `--player`).

## Navíc
Uživatel nemá nic proti dalším logům, pokud pomohou najít zbytečná volání a zrychlit (GetValidRooms, přestavby pohledů).
Větev `claude/new-session-gmxcr2` má ~26 dalších commitů proti main - slouč ji u sebe obvyklým způsobem.
