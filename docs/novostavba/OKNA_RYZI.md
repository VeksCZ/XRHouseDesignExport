# Okna a vchodové dveře - smlouva o dílo č. 251000 (Ryzi Okna, 13.10.2025)

Systém Veka Softline 82-MD, **zevnitř bílá (RAL 9016), zvenku Antracit hladký (4367003)**, trojsklo 4/18/4/18/4 (balkonové dveře 6/14/6/16/6),
kování Roto NX, klika Rotoline Secustik, těsnění černé, skryté panty, spodní profil Purenit. Pohledy ve smlouvě jsou zevnitř ("vnitřní pohled").
NP: 1NP = dolní patro (podlaha ~ -3,2 ve scanu), 2NP = horní (~ 0).

| # | místnost | rozměr (š x v mm) | členění |
|---|---|---|---|
| 1, 2 | garáž 1NP | 1220 x 950 (2x, zrcadlově) | otevíravě-sklopné; #1 klika vpravo, #2 vlevo |
| 3 | kuchyň 1NP | 2220 x 1560 | 750 OS + 1470 pevné (zákazník podezdívá na 1610) |
| 4 | obývák 1NP, balkonové dveře | 2520 x 2450 | 1470 pevné + 1050 dveře |
| 5 | obývací pokoj 1NP | 2700 x 2490 | HS portál 2 x 1350 (posuvné, Purenit sendvič 140) |
| 6 | pracovna 1NP, balkonové dveře | 2500 x 2440 | 1050 dveře + 1450 pevné |
| 7 | kumbál 1NP | 720 x 1195 | OS |
| 8 | 2NP pokoj vpravo | 1720 x 1195 | 733 + 987, obě OS |
| 9 | 2NP pokoj vlevo | 1720 x 1195 | 733 + 987, obě OS |
| 10 | 2NP dětský pokoj vlevo | 1220 x 950 | OS (klika vpravo) |
| 11 | 2NP WC | 720 x 950 | OS (klika vlevo) |
| 12 | 2NP koupelna | 1970 x 950 | 1266 + 704, obě OS |
| 13 | 2NP ložnice | 2520 x 1445 | 1087 (zvětšeno z 1050) + 1433, obě OS |
| - | **vchodové dveře** (jiná objednávka) | 1480 x 2195 | boční světlík 400 (matné) + křídlo 1080, antracit uvnitř / bílé venku, otevírání ven |

## Napárování na okna ve scanu a strany (zrcadlové dvojice)
Rozměry ve smlouvě jsou skutečné (rám okna); model je kreslí vystředěné v naskenovaném hrubém otvoru, 3 cm nad parapetem (podkladní Purenit); větší než otvor se ignoruje.
Typ částí a strany pantů: **u oken, kde je uživatel už nastavil, zůstávají beze změny** (jen se doplní šířky částí); u ostatních z výkresu smlouvy (pohled zevnitř).
Poloha vlevo / vpravo je určená z geometrie scanu při pohledu zevnitř místnosti:
- koupelna 3bf3e3: `6740bf` (1,18) je vlevo od `952551` (0,72) -> větší křídlo 1266 vlevo, menší 704 vpravo (jako výkres); obě mají kliku uprostřed (panty venku).
- garáž 2e5efb: `785c4e` je vlevo, `548077` vpravo -> č. 1 (klika vpravo) vlevo, č. 2 (klika vlevo) vpravo, kliky směrem ke středu (předpoklad: položky jsou řazené zleva doprava).
- pracovna / obývák 34b261: `d90eeb` (1,39) je vlevo, `03389a` (1,11) vpravo -> proti výkresu #6 je to zrcadlově: pevná část 1450 vlevo, dveře 1050 vpravo (panty vpravo, jak je máte v úpravách).
- obývák `79a321` = #5 HS portál 2 x 1350 (vaše úprava: posuvná + pevná), kuchyň `d908c8` = #3 (750 OS + 1470 pevné).
- ložnice `70936a` = #13 1087 + 1433; vaše úprava má druhou část pevnou, smlouva otevíravou (ponechána vaše).
- 2NP: `0da02b` = #8 pokoj vpravo, `95853a` = #9 pokoj vlevo (stejné 733 + 987), `b6b99a` = #10 dětský pokoj vlevo, `fb2b7e` = #11 WC.
- Nenapárováno: #4 (balkonové dveře 2520 x 2450), #7 kumbál 720 x 1195; `09f62a` (horní chodba) ve smlouvě není.

## V aplikaci
- Rám oken: bílý uvnitř, antracitový zvenku (`WalkDoors.AddWindowFrame`), sloupek mezi částmi; části nestejně široké (`SectionEdit.width`, mm).
- `python3 Tools/apply_contract_windows.py <93_Data_Edits.json> novostavba.geometry.json <out.json>` -> `docs/novostavba/93_Data_Edits.windows.proposed.json`:
  `widthMm`/`heightMm` (skutečný rám) + části oken (4 existující úpravy dostanou jen šířky částí a rozměr, 9 nových oken celé části z výkresu); originál nedotčen.
  PC ho použije na kopii scanu `Scan_Novostavba Osove_clean`.
- Pozor v edits: dveře `2d30356d` mají v `93_Data_Edits.json` 9 duplicitních záznamů (kind 0 / 2); pravděpodobně chyba ukládání, stojí za kontrolu.
