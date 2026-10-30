# Diagnóza: rozbitý model "Novostavba Osove" (schody jinde)

Čistě diagnostika, nic se neměnilo. Zdroje: `Exports/RoomData/Scan_Novostavba Osove`,
`Exports/RoomData/Export_20261030_155231_Novostavba_Osove`, `Logs/Quest/Session_20261030_151130.txt`.

## Problém 1: schody na špatném místě

### a) 93_Data_Edits.json
- 1 schodiště, `roomUuid = 162b8c39-e861-0d4e-a416-862b1963c455`
- 4 `localPoints`, X rozsah -0.33..3.61 m, Z rozsah -0.51..1.16 m - rozumné, v rámci místnosti
- **Y rozsah: -3.1956 .. -3.1956 m (konstantní, min==max)**
- `width 0.9 m`, `risers [3, 11, 3]` (3 flighty, 17 schodů celkem), `tread 0.27 m`
- `modified`: `null` (edit nikdy neprošel novým "modified" razítkem zavedeným touto session)

### b) roomUuid v 92_Data_Scan.scene.json
- Přímé porovnání stringu neuspělo - `162b8c39-...` se v raw JSON nenajde.
- **Po správné bajtové konverzi (.NET `Guid.ToString()` přehazuje bajty prvních tří skupin -
  `PendingChanges.IdForms`'s "native" tvar) ale room **existuje**: `398c2b16-61e8-4e0d-a416-862b1963c455`.
  → roomUuid je platný, místnost v aktuálním scanu je. Není to "schody patří jiné místnosti, co tu
  není" - tahle hypotéza z zadání se nepotvrdila.
- Floor anchor té místnosti (raw JSON, nativní souřadnice) má `position.y = -3.13742`.
  **To je nápadně blízko Y schodů (-3.1956, rozdíl ~5,8 cm)** - viz závěr níž.

### c) _live.edits.json
- Nenalezen (ani na PC v `Exports/RoomData`, ani v pulled `ScanCache`) - live scan teď nemá
  rozpracované edity čekající na uložení.

### d) Datum úprav
- `93_Data_Edits.json` u `Scan_Novostavba Osove`: 30.10. 16:13 (čas pullu, ne čas skutečné úpravy -
  content shodný s exportem z 15:52).
- Scan `92_Data_Scan.scene.json` téže složky: stejný pull čas - nelze z mtime na PC poznat, kdy
  schody vznikly vs. kdy byl scan naposledy přescanován.

### Log (Session_20261030_151130.txt, export v 15:52:3x)
Při opakovaném načtení **stejného, nezměněného** scanu "Novostavba Osove" těsně po sobě:
```
15:51:05.177 [Furniture] Novostavba Osove: 338 parts in 46 room(s)   <- první load po přepnutí z 7rooms
15:52:28.803 [Furniture] Novostavba Osove: 278 parts in 45 room(s)   <- druhý load, stejný scan
15:52:32.757 [Furniture] Novostavba Osove: 278 parts in 45 room(s)   <- load při exportu (stabilní)
```
Počet místností/kusů furniture se liší mezi prvním a druhým načtením identického scanu - ukazuje na
nekonzistentní/stale cache při přepnutí scanu (podobný vzorec jako `roomCache` keyovaný jen počtem
místností + scene verzí v `RouteEditTool`/`DoorEditTool` - pokud dva různé scany mají shodnou
kombinaci, vrátí se špatný list). Export samotný ale proběhl už na "ustáleném" druhém čtení (278/45),
takže tahle nestabilita pravděpodobně **nevysvětluje přímo schody** (export vzal stabilní data), je
ale samostatně podezřelá a stojí za prošetření.

### Nejpravděpodobnější závěr (schody)
Y souřadnice schodů (-3.1956) je room-local (`room.transform.InverseTransformPoint`, viz
`StairEditTool.cs`). Pokud by schody začínaly na podlaze SVÉ místnosti, lokální Y by mělo být
blízko 0 (room transform typicky sedí na podlaze). Místo toho je skoro identické s absolutní
(raw) Y-pozicí podlahy téhle místnosti ve scaně. To silně napovídá, že schody byly zakreslené
v době, kdy `room.transform.position.y` téhle místnosti bylo ~0 (nebo jiné), a od té doby se scan
**znovu scanoval / Space Setup se přepočítal** a transform místnosti se posunul o ty ~3,1 m - ale
room-local edit (schody) se nikdy nepřepočítal na nový transform. Při zpětné rekonstrukci
(`room.transform.TransformPoint(localPoint)`) tak schody vycházejí o celé patro (3+ m) jinde, než
kde fyzicky jsou - přesně "schody jsou úplně jinde". Je to analogie ke stejnému typu posunu, co
řeší `AlignToLive`/`TrackingSpaceOffset` pro live scan, ale pro samotné **room-local edity po
přescanování** žádná ekvivalentní migrace nenašla.

## Problém 2: scan se "páruje" s live scanem

Log jasně a opakovaně (3x, při každém načtení "Novostavba Osove") hlásí **správné** chování:
```
15:50:49.961 Saved scan: not aligned - no room in common with the live scan (another place).
             It is a model only here.
15:50:49.962 [Align] not aligned - no room in common with the live scan (another place)
```
Stejně tak `Fittings + niches: 'Novostavba Osove': none of its rooms is in the live scan`.
Aplikace tedy KOREKTNĚ poznala, že "Novostavba Osove" nemá nic společného s místem, kde uživatel
fyzicky stojí (byt), a zobrazila scan jako samostatný model (ne napárovaný na live tracking).
V tomto logu se žádné chybné zarovnání/párování nenašlo - pokud uživatel viděl dojem "párování",
je to spíš vizuální/UX dojem (model se objevil "kolem" uživatele i bez skutečného tracking-space
zarovnání), ne chyba v `AlignToLive`. Pro ověření by bylo potřeba log z momentu, kdy k tomu dojmu
došlo konkrétně.

## Problém 3: fotky na zdech (nekontrolováno)
Nebylo součástí tohoto požadavku ("diagnostiku z toho logu, proč se rozbil model") - nechávám na
další kolo, pokud bude potřeba.
