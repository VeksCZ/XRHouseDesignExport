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
6. Po přidání: v adresáři scanu jsou `*.bak_doors` a `*.bak_last`.

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

## Ovládání a vzhled
18. Ray: tenká čára, kroužek na konci; nad panelem/Dollhouse čtyři šipky, po stisknutí gripu vyplněný.
19. Menu: podtitulky pod tlačítky; **Align to real walls** je teď v záložce Layers.
20. Walk / Dollhouse: rychlé dvojité kliknutí na Walk nebo přepínání detailu nesmí vytvořit druhý model.
