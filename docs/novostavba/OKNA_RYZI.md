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

## Napárování na okna ve scanu (podle rozměru a místnosti)
- `0da02b` (e040fd) a `95853a` (1c9c52): #8/#9 1720 x 1195 (scan 1,70 x 1,20).
- `b6b99a` (1c9c52): #10 1220 x 950; `fb2b7e` (93ee51): #11 WC; `70936a` (6df884): #13 ložnice 2520 x 1445.
- koupelna 3bf3e3: dvě okna ve scanu `6740bf` (1,18) + `952551` (0,72) = jedno okno #12 (1266 + 704); větší vlevo (předpoklad).
- obývák 34b261: `79a321` (#4, 2,60 x 2,45) a `d908c8` (#3 kuchyň, 2,25 x 1,55); `03389a` (1,11) + `d90eeb` (1,39) odpovídají #6 (1050 + 1450, pracovna).
- garáž 2e5efb: `548077`, `785c4e` (výška 1,0 / 0,95 m v 1,8 m) = #1, #2 (která je zrcadlová, nelze poznat).
- Nenapárováno: #5 HS portál 2700 x 2490, #7 kumbál 720 x 1195 (ve scanu nenalezeno); `09f62a` (0,75 x 2,05, horní chodba) ve smlouvě není.

## V aplikaci
- Rám oken: bílý uvnitř, antracitový zvenku (`WalkDoors.AddWindowFrame`), sloupek mezi částmi; části nestejně široké (`SectionEdit.width`, mm).
- Návrh úprav oken z této smlouvy: `python3 Tools/apply_contract_windows.py <93_Data_Edits.json> novostavba.geometry.json <out.json>` ->
  `docs/novostavba/93_Data_Edits.windows.proposed.json` (3 okna změněna, 6 přidáno; originál nedotčen). PC ho použije na kopii scanu `Scan_Novostavba Osove_clean`.
- Pozor v edits: dveře `2d30356d` mají v `93_Data_Edits.json` 9 duplicitních záznamů (kind 0 / 2); pravděpodobně chyba ukládání, stojí za kontrolu.
