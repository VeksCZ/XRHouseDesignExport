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
   novostavba.geometry.json`~~ - hotovo (2026-11-02): 15 místností, 68 zdí, 29 dveří, 17 oken.
   `novostavba.geometry.json` + `93_Data_Edits.json` + `95_Data_Furniture.json` té scény jsou v tomto commitu.
   **Oprava (2026-11-02):** nástroj psal id (room/wall/door/window) v obráceném byte-order prvních tří skupin GUID -
   neshodovala se s `roomUuid`/`anchorUuid` v `93_Data_Edits.json` ani s tím, co vrací MRUK za běhu (`DoorCatalog.RoomId`).
   Opraveno (`guid_fix` v nástroji), `novostavba.geometry.json` přegenerován a id teď sedí (ověřeno proti door
   `anchorUuid` i room `roomUuid` v edits souboru). Pokud ještě vidíš nesedící id, zkontroluj, že pracuješ s touto
   opravenou verzí souboru, ne se starší z předchozího commitu.
2. Stávající schody v edits (`risers: [3, 11, 3]`, 4-bodová lomená čára) zhruba odpovídají zadání uživatele (2-3 schody,
   podesta, dlouhý úsek, podesta, 2-3 schody) a projdou desktopovým `-test stairs`; dřevěné zábradlí nahoře je už v
   render vidět. Není jisté, že přesná pozice/natočení odpovídá "u dveří ze vstupní chodby vlevo" - ověř/doladit podle
   popisu výše (StairEditTool, nebo přímo `localPoints`/`risers` v edits).
3. `-test fall` na tomhle scanu selhává - u otvoru (prvního v `holes`) blokuje paprsek ve všech 4 směrech nějaká zeď/stěna
   (floor=True, clear=False). To je pravděpodobně přesně ten "výstupek" z popisu výše - najdi tu zeď (teď už se správnými
   id to jde přes `novostavba.geometry.json` porovnáním sousedních místností) a oprav/smaž.
4. Kam sahat v kódu: `XRModelFactory` (SnapToPerpendicular, EstimateWallThickness, otvory/díry), `DoorCatalog` (otevírání dveří),
   `Furniture.InstallCeilings` / střechy (RoofPart), `StairGeometry` + `StairFloorSnap`.
5. Před commitem C#: `python3 Tools/OfflineTests/FullCompile/check.py --base <poslední revize, co PC přeložil>` (a `--player`).

## Navíc
Uživatel nemá nic proti dalším logům, pokud pomohou najít zbytečná volání a zrychlit (GetValidRooms, přestavby pohledů).
Větev `claude/new-session-gmxcr2` má ~26 dalších commitů proti main - slouč ji u sebe obvyklým způsobem.
