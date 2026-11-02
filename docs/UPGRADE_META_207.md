# Meta XR Core / MRUK 205 -> 207 (zjištěno 2026-11-02, z poznámek k vydání; neověřeno v projektu)

Zdroje: https://developers.meta.com/vr/downloads/package/meta-xr-mr-utility-kit-upm/ , https://developers.meta.com/vr/downloads/package/meta-xr-core-sdk/ , https://developers.meta.com/updates

MRUK 207:
- **Scene model V2 (HiFi Scene) je vypnutý** - volba nemá účinek, vždy se načítá Scene V1. Naše uložené scany jsou V1 i V2 (`SceneJsonEdit`,
  `extract_scan_geometry.py` čtou oba). Před upgradem ověřit, že se uložené V2 scany (room-mesh faces) pořád načtou přes
  `MRUK.LoadSceneFromJsonString`; Novostavba je V1.
- Shadery rozdělené na URP a BiRP varianty (kvůli Unity 6.4), výchozí je URP - u nás URP 17, mělo by být bez zásahu; zkontrolovat
  materiály MRUK prefabů (EffectMesh) v buildu.
- Nové `SceneNavigation.MinRegionArea` (NavMesh) - nepoužíváme.
Core 207: FOV simulace v OVRManager / Immersive Debugger, experimentální Device Readiness Check, toolbar jako dokovatelný panel
(Window > Meta > Meta XR SDK) - netýká se kódu.

Doporučení: upgrade dělat na samostatné větvi, po něm spustit `-test views|doors|stairs` a načíst jeden V1 a jeden V2 scan; do té doby zůstat na 205.
