# Co vyzkoušet po novém buildu (Quest + PC)

Změny z posledních dní, které ještě nikdo neviděl běžet. U každého: co udělat a co by se mělo stát.
Když něco nevyjde, napište mi (nebo PC session) číslo bodu a log (`Logs/Quest`, menu „Pull logs from Quest“).

## Živý scan a ukládání
1. **Úprava na živém scanu** (schody, otvor, typ dveří): nic se nesmí uložit samo. Data tab zoraní, tlačítko „Save live scan“ svítí.
2. **Save live scan**: vznikne nový scan, edice jsou v něm, aplikace na něj přepne. Starý scan zůstal beze změny.
3. **Nové dveře / chybějící zeď na živém scanu**: odmítne se hláškou „save it first“, tlačítka jsou šedá.
4. Na **uloženém scanu** (zkoušejte na kopii!): nové dveře projdou na první pokus a po přestavbě stojíte na stejném místě.

## Chybějící zeď (nástroj Doors)
5. Namiřte na místo neskenované stěny (šatna u obýváku), spusťte trigger → „Missing wall instead“ (nebo trigger přímo do mezery) →
   náhled žlutého obdélníku mezi dvěma volnými konci zdí → „Add wall“. Zeď se objeví v modelu i v půdorysech.
   Když je otočená špatně (dovnitř/ven), nebo se nenajde mezera, pošlete mi `Logs` a scan.
6. Po přidání: v adresáři scanu jsou `*.bak_doors` a `*.bak_last`. V panelu přidání dveří / zdi je tlačítko **Undo last scan edit**
   (vrátí scan na stav před poslední úpravou a načte ho znovu).

## Schody a otvory
7. **Novostavba, schody jinde**: otevřete nástroj Stairs, vyberte schody. V logu „put back on the floor“ znamená automatickou
   opravu o patro. Zbytek: **Move** (10 cm) a **Turn** (5°) na panelu. Otvory (Openings): **Move** na panelu vybraného otvoru.
8. Export: schody jsou v OBJ/GLB a otvory jsou vyříznuté z podlah.

## 3D scan stěn (hlavní věc)
9. **Tools > 3D scan**: HUD ukazuje „walls NN %“; po 90 % zdí v místnosti zpráva „Walls of this room are done“, klik a vibrace.
10. Po skenování: vrstva **Wall scan** ve Walk i Dollhouse – fotky sedí na zdech, žádné růžové plochy (shader), žádné mihotání na dálku.
11. **První načtení po updatu** přestaví cache ze snímků (CacheVersion 3, hustota 4,5 mm) – v menu stav „Wall scan: x / y frames“. Trvá déle.
12. **Menu při zapnutých vrstvách**: fotky/trasy už nezakrývají menu, i když jsou blíž než ono.
13. Málo místa: při < 2 GB varování v logu, při < 400 MB se nahrávání zastaví (HUD to píše).

## Export na PC (`Exports/RoomData/...`)
14. `16_Model_Wall_Photos.glb` – otevřít vedle `11_Model_Analytical_Anchors.glb` (Windows 3D Viewer / Blender): fotky na rovinách, stejné souřadnice.
15. `97_Walls_Routes.html` – na pohledech stěn je fotka stěny jako pozadí (zaškrtávátko „Fotka stěny“), trasy nad ní; fotka není zrcadlově
    obrácená (levý/pravý roh sedí s kótami). Složka `97_Wall_Photos/` musí ležet vedle HTML.
16. Trasy: smažte trasu na Questu, spusťte **Pull** → na PC se nevrátí. Upravte edity scanu na PC (desktopová appka) a zkuste **Pull**:
    novější PC edity zůstanou, starší se přepíšou a záloha je `93_Data_Edits.json.before_pull`.
17. Smazání scanu (koš / „Remove old copies“): soubory jsou v `ScanCache/_trash/` (PC: `Exports/RoomData/_trash/`), ne pryč.
    Záložka Data > **Restore last deleted scan** ho vrátí (se všemi edity) a objeví se v seznamu scanů.

## Ovládání a vzhled
18. Ray: tenká čára, kroužek na konci; nad panelem/Dollhouse čtyři šipky, po stisknutí gripu vyplněný.
19. Menu: podtitulky pod tlačítky; **Align to real walls** je teď v záložce Layers.
20. Walk / Dollhouse: rychlé dvojité kliknutí na Walk nebo přepínání detailu nesmí vytvořit druhý model.

## Dveře a prohlídka (2026-11-02)
21. **Dveře se otevírají tak daleko, jak mohou, až do 120°** (kolizní sweep křídla proti zdím). Otevřete několik dveří různými směry:
    nemají procházet zdí; u dveří s pantem těsně u rohu (dětský pokoj nahoře u záchodu, dveře `e1baa5`) se mají zastavit u boční zdi.
    Když se dveře otevřou jen o pár stupňů nebo naopak do zdi, napište mi, které (pokoj + strana).
22. **Zárubně** kolem dveří (šedobílé sloupky + překlad, 6 cm): nemají zužovat průchod, ani trčet víc než pár cm do pokoje. Okna a garážová
    vrata je nemají.
23. Dveře, které v úpravách neměly vyplněné „otevírá se do“, se otevírají do naskenované místnosti (ne ven).

## 3D scan – kvalita (2026-11-02)
24. Nafoťte jeden pokoj a podívejte se na stěnu zblízka: obraz má být o něco ostřejší (unsharp mask), bez světlých lemů u děr. Když je moc
    ostrý nebo se objevují halo, zmenšit `_Sharpen` v `WallScanShow.shader` (0.35).
25. Snímky z menší vzdálenosti než 0,9 m mají menší váhu: stěna zblízka by neměla být z jediného rozmazaného záběru; díry se i tak plní.
26. Expozice: fotky stejné stěny z různých míst mají mít podobný jas (sidecar `exposureGain`, 0.85–1.18). Starší snímky bez hodnoty se nemění.
27. Ukazatel (ray): ikona se čtyřmi šipkami je teď menší a průhlednější.

## PERF log (po 5+ minutách používání)
28. V `Logs/Quest` hledejte řádky `PERF calls:` – `validRooms(hit/miss/loading)`: hodně `miss` nebo velké `computeMs` mimo načítání scanu =
    zbytečný přepočet; `overlayRebuilds` vysoké ve stoje = pohledy se přestavují zbytečně. Pošlete mi je.

## Vchodové dveře (2026-11-03)
29. Vchodové dveře (otvor 1,35-1,70 m, v Novostavbě `5cdd3e`, 1,49 m): vlevo (zevnitř) pevný matný světlík 40 cm, vpravo křídlo 1,08 m s úzkým okénkem a dvěma
    příčnými lištami; venku bílé a dlouhá inox tyč, uvnitř antracit a klika; otevírají se ven; zárubeň antracit; nad dveřmi (2,195 m) vyplněný antracitový pás.
    Pokud se křídlo otevírá dovnitř, nebo je světlík na druhé straně, napište mi to.

## Vchodové dveře, okna, žaluzie (2026-11-03)
30. **Venkovní žaluzie**: View > **Exterior blinds** (antracit): v otvorech oken ve vnějších zdech se plynule (~3 s) spustí / vytáhnou lamely do parapetu; kastlík je schovaný v překladu nad otvorem (neviditelný), vodicí lišty v ostění na vnějším
    konci (zeď 50 cm). Pokud lamely nevylézají ze správného místa nebo hloubka neodpovídá, napište kam.
31. Barvy dveří: křídla světlejší hnědá, **zárubně** sytá hnědá (vchodové dveře: antracit / bílá).

## Nábytek (2026-11-03) - v kopii spec `docs/novostavba/95_Data_Furniture.proposed.json`
32. Sprchy: skutečná sestava (termostat s podomítkovou deskou a 2 knoflíky, rameno 450 mm, hlavová sprcha 300x300, výtok s držákem, hadice 1600 mm ve smyčce a ruční sprcha); pozici ramene lze posunout `set_u`.
33. Kuchyň: trouba (ocelový rám, ovládací pás s displejem a knoflíky, černé sklo, madlo) a vestavná mikrovlnka (okénko, svislé madlo, ovládání).
34. Obývák: stolek pod TV (3 dvířka, plinth) a konferenční stolek před sedačkou (poloha `along` / `gap` je odhad). Vstupní hala `3DE1`: botník a věšák na severní stěně vpravo po vstupu (poloha `along` je odhad). Šatna (`Satna`): skříně na delší stěně `+z`.
35. Spodní koupelna `49A5`: sprcha vzadu vlevo s pevným bočním sklem + posuvnými dveřmi, umyvadlo vlevo vpředu (už bylo), žebřík na topení na pravé stěně.

## Opravy podle zpětné vazby (2026-11-03)
36. **Tools > Repair the scan > Missing wall**: samostatný nástroj (bez dveří): zamiřte do mezery, trigger, potvrďte panel. Jen na uloženém scanu.
37. **Save live scan**: po uložení se vždy přepne na uložený scan (úpravy jdou do něj).
38. Walk: po přidání dveří / po přestavbě se vrátíte na místo, kde jste stál. Když ne, pošlete `Logs/Quest` - jsou tam řádky `[Walk] ...` (proč).
39. Žaluzie: jen ve scanu, který má ve spec `house_style.blinds`; jedna žaluzie přes celé okno (balkonové dveře + pevná část). V bytě tlačítko Exterior blinds není.
40. Finished floors: dveře i zárubně stojí na hotové podlaze (výš a o to nižší).
41. Horní koupelna: sestava sprchy uprostřed krátké boční stěny kabiny; nad vanou na krátké stěně podomítková baterie, výtok a malá sprška.
42. Terén (návrh): terasa před balkonovkami v obýváku, mez 1,5 m dolů do zahrady, rovná plocha vedle zastřešené terasy (rozměry jsou odhad - napište, co upravit).
43. PC: `python3 Tools/apply_proposed_to_scan.py "Exports/RoomData/Scan_Novostavba Osove_clean"` přenese nábytek (stolek pod TV, konferenční stolek, hala, šatna), terén, okna a house_style na kopii scanu.
