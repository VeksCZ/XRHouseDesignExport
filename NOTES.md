# Stav práce (předávka mezi PC a cloudem)

Aktualizuj na konci každé session. Pište stručně, nejnovější nahoře. Pravidla a architektura jsou v `CLAUDE.md`.

## Aktuální stav

- Větev: `claude/new-session-gmxcr2` (cloud), `main` je s ní sloučená (poslední cloudový commit c6106f7, pak PC 9ada87e).
- Poslední dokončeno (cloud, 2026-11-02): dveře - kolizní otevírání do 120° a zárubně, dveře bez "otevírá se do" míří do naskenované místnosti;
  3D scan - váha podle vzdálenosti, unsharp mask, vyrovnání expozice; PERF řádek `PERF calls:`; menší dragovací ikona; nástroje
  `Tools/extract_scan_geometry.py`, `analyze_scan_geometry.py`, `simulate_wall_slabs.py`, `stairs_fit.py`, `list_scan_formats.py`;
  poznámky `docs/novostavba/FINDINGS.md`, `docs/UPGRADE_META_207.md`; kontrolní seznam `docs/TEST_CHECKLIST.md` (body 21-28).
- Rozpracováno / čeká na ověření v renderu nebo headsetu: střecha nad terasou (`docs/novostavba/95_Data_Furniture.proposed.json`,
  `Strecha_terasa.z1 = -2.30`), zdi vyšších místností dole nad y 0, díra v rohu místnosti b3293e15 (Missing wall na kopii), vzhled zárubní,
  kolizní otevírání dveří, 3D scan kvalita.
- Další krok: až bude headset na USB, nainstalovat build a nechat uživatele projít `docs/TEST_CHECKLIST.md`; poslat `PERF calls:` řádky z `Logs/Quest`.

## Jen na PC (cloud to nezvládne)

- Unity build a release (`rel`, `BuildQuest.Build`, `Tools\UploadQuest.ps1`), podpisový klíč, `%USERPROFILE%\.xrhouse\meta.json`
- Data skenů v `Exports/RoomData` (mimo git)

## Známé problémy / pozor na

- MRUK 207: Scene V2 je vypnutý, 22 z 37 uložených scanů je V2 -> NEPŘECHÁZET na 207, dokud se V2 scany zkusmo nenačtou (`docs/UPGRADE_META_207.md`).
- Schody v Novostavbě jsou správně (uzavřeno): zasahují do neskenovaných Pracovna_dole / Satna (`extra_rooms` ve spec).
- Cloud nemá scany ani Unity: vše z cloudu je ověřeno jen offline (FullCompile, WallTest, syntax).

## Historie

- 2026-11-02 (cloud): dávka dveře + 3D scan + nástroje pro geometrii Novostavby, viz Aktuální stav.
- 2026-11-02: soubor založen.
