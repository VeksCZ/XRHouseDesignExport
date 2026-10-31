# XRHouseDesignExport — instructions for Claude Code

Unity tool for Meta Quest that scans a home with the Mixed Reality Utility Kit (MRUK)
and exports 3D models, floor plans and reports from the scan.

## Standing rules

- **Never `git commit` unless explicitly asked** in that session. Editing files and
  reporting progress is fine; committing is not, even after a successful fix.
- **Shortcuts** (the user typing one is the explicit request to commit/push; don't nag about commits otherwise):
  - `ci` = commit the current changes (clear message, no push).
  - `cip` = commit and push to the session's designated branch.
  - `rel` = release the app to the Meta Horizon Store channel: full (non-development) release-signed Quest build
    (`BuildQuest.Build`), then `Tools\UploadQuest.ps1 [-Channel ALPHA] [-Notes "..."]`. Needs Unity, the signing key and
    `%USERPROFILE%\.xrhouse\meta.json` on the user's PC - in a cloud session none of that exists: say so, give the two
    commands, don't pretend it ran.
  - `ship` = `cip` + `rel` (commit, push, then release).
- **UI text**: short and factual - what it does and how to use it (which button / trigger / stick), no long paragraphs.
  Main-menu buttons carry a one-line subtitle in the muted colour (`<size=72%><color=#9aa6b8>...`).
- **Platform versions** (checked 2026-10-31): Unity 6000.3 LTS (6.3) stays - 6.6 is still alpha, the Oculus XR plugin is
  deprecated from 6.5 (we use OpenXR via Meta XR Core), 6.6 raises the Android minimum to OpenGLES 3.1 (we build Vulkan
  first). Meta XR Core / MRUK 205 in use, Core 207 exists (upgrade when convenient, read its notes first). Passthrough
  camera = MRUK `PassthroughCameraAccess` (the current API; `WebCamTexture` is the old one).
- **Always target the newest available Meta Quest / Meta XR SDK (and MRUK) APIs**, not
  older/deprecated patterns. When in doubt, check current Meta docs/SDK versions rather
  than relying on possibly-stale knowledge. `Packages/manifest.json` pins the SDK version
  in use — check it before assuming what's available.
- **Target feature set** (ongoing goals, not one-off requests):
  - Multi-room export (a scanned "space" with several rooms).
  - Multi-floor / multi-story buildings, including a room with more than one floor
    level (split-level rooms use `MRUKRoom.FloorAnchors`, plural).
  - Sloped / vaulted ceilings — a room can report more than one `CeilingAnchor`;
    don't assume a single flat ceiling height.

## Architecture notes

- `MRUKExporter.cs` drives the export pipeline; `XRModelFactory.cs` builds the actual
  3D geometry (`XRHouseModel`/`XRRoomModel`/`XRMeshPart`) in three flavors: clean
  anchor-box reconstruction, mesh-based reconstruction from the real scanned mesh, and
  a synthetic "clean" reconstruction (`CreateReconstruction`) used for the main
  `03_Model_Reconstruction` export — this is the one place that needs to actively
  handle sloped ceilings and multi-floor rooms rather than relying on raw scan data.
- `MRUKDataProcessor.GetValidRooms()` is the single source of truth for which rooms
  count as real, exportable rooms (dedup + area filtering). `MRUKExporter` and
  `DollHouseVisualizer` (in-headset preview) must both use it so the preview always
  matches what actually gets exported.
- `OBJWriter`/`GLBExporter` write the mesh formats; GLB output must stay valid glTF 2.0
  (e.g. POSITION accessors need `min`/`max`) since strict viewers (Windows 3D Viewer,
  validators) reject non-compliant files even if lenient tools tolerate them.

## Workflow notes

- **Floor plans**: `FloorPlanBuilder` (pure geometry, no MRUK) feeds both the HTML report and the in-headset
  `FloorPlanPanel`; `MRUKPlanExtractor` is the only MRUK-facing part. Room names come from `RoomNames` (user-picked
  presets stored per room UUID) because MRUK exposes no room name.
- **Scan cache**: `MRUKSceneCache` (JSON incl. global mesh) lives in `persistentDataPath/ScanCache` on the headset
  (scoped storage: adb-pushed files elsewhere are invisible to the app). On the PC there is no separate cache:
  `Exports/RoomData` is the one local folder, and every folder in it with a `92_Data_Scan.scene.json` is a scan
  source (exports are self-contained: `93_Data_Edits.json` + `94_Data_RoomNames.json`). Editor menu "Pull" brings
  exports plus the headset's saved scans (as `Scan_<name>` folders); `Tools/BatchExport.ps1` writes there too.
- **Wall photos** (`WallPhotoCapture`, edit-mode tool "Photos"): passthrough-camera shots (MRUK `PassthroughCameraAccess`)
  of bare walls/floors/ceilings before plastering, each with a `NNNN.json` sidecar (camera pose room-local to its room
  UUID + viewport pinhole fu/fv/cu/cv). Needs the live, world-aligned scan (`MRUKExporter.WorldAligned` /
  `EnsureLiveLoaded`). Headset: `persistentDataPath/WallPhotos/Photos_yyyyMMdd` incl. a scan snapshot; "Pull" brings
  them into `Exports/RoomData/Photos_yyyyMMdd` (a scan source of its own). Pixel -> ray -> raycast on the scan gives 3D.
  "Dense" mode (for a Gaussian splat) shoots every ~15 cm / 10 deg (QuestRoomScan defaults) into `Dense/`, with seed points
  (scan hits coloured from the photo). Every photo also has a house-frame pose (local to the room with the smallest
  UUID, fixed per session); menu MRUK > Wall photos > Export COLMAP (`WallPhotoColmap`) writes `Colmap/` (images +
  sparse/0 text) in that frame with z negated - a trained splat negated back in z sits under that room's transform.
- **Routes** (`RouteEditTool`, edit-mode tool "Routes" / main menu "Trace routes"): cables, cold/hot water, waste and
  heating traced as polylines by clicking points on the scan (over passthrough on the live scan, or in the walk-through).
  Stored house-wide, not per scan (`RouteStore`: headset `persistentDataPath/Routes/routes.json`, PC
  `Exports/RoomData/Routes/routes.json`; "Pull" merges both by route id and pushes the result back), points room-local to
  their room UUID - so they show in every scan that has that room. `WallOverlays` draws routes and the cable photos
  projected onto the walls ("Photos on walls") into passthrough / walk / Dollhouse, each switchable (menu, desktop K / P).
  `WallDrawings` writes `97_Walls_Routes.html` (plans + wall elevations with openings, niches and routes, cm from the left
  corner and the rough floor, check boxes to hide routes/dimensions): on Export, and desktop key E (scan folder).
  Desktop tests: `-routes <file>` (never the real file) `-addroutes "kind:x,y,z;...|..."` (GLB frame), `-test walls`.
  Routes/boxes carry `spec` (material preset per kind), `circuit`, `note` and a per-item `modified` (RouteStore.Apply
  stamps what a change touched; Merge keeps the newer copy). Tool panel: Pick... selects a route/box for Spec / Circuit
  / Leads to; points snap onto boxes, plumb above/below boxes and to other routes' levels. Pointing at a route / box /
  photo icon shows a label (WallOverlays.UpdateAim; a photo pops up after a short dwell). "No-drill zones" (Layers tab,
  `WallOverlays.ShowZones`): bands of route width + 10 cm per side on the surface. Export also writes trade sheets
  `98_Elektro.html` / `98_Voda_Odpad_Topeni.html` (material totals, route/box lists, only walls with that work, A4
  landscape print). Desktop: `-overlays routes,zones`, `-test views -views routes` (one shot per route; give `-out`
  as an absolute path).
- **Scan list** (menu top row: name + state lined-up / not saved / not exported; tap opens the list): rows from
  `MRUKSceneCache.DescribeAsync` (rooms, date, furniture/edits/doorway), tap = select + load, bin = delete (press twice).
  "Old copy" = a saved scan whose rooms are all in a newer one and that has no furniture, edits or added doorway
  (`*.bak_doors`); "Remove N old copies" deletes them (press twice). PC: only `Scan_` folders are removable.
- **Main menu** (`XRMenu`, look in `XRUi`: rounded sprites, `XRUiButton` hover/flash, `SetToggle` = switch): tabs
  View (walk, Dollhouse, plans, minimap, detail, finished/rough) / Layers (wall scan, routes, zones, photo markers, photos
  on walls everywhere; fittings + scan outline real walls only, greyed when the loaded scan is of another place) / Tools
  (every edit tool directly - `OnTool` -> `EditModeController.UseTool`; again = leave) / Data. Hold Y: leave the active
  tool, else open the menu on Tools. The Undo/Redo/Done bar shows only for tools without their own panel (not
  Routes/Photos). Desktop `-test menu` shoots every tab + the scan list (`XRMenu.Preview`).
  On the Windows app a button shows its keyboard shortcut as a key cap (`XRUi.SetKeyHint`: K routes, P photos, F next floor,
  U sun). The pointing ray is a thin fading line (`XRMenu.StyleRay`) with a cursor at its end (`RayCursor`: ring + dot on
  clickable things, ring + four arrows over anything the grip can drag, filled while the grip is held).
- **Wall scan** (`WallScan`, layer "Wall scan (3D)", on by default): every 3D-scan (dense) frame is projected onto the
  scanned planes of its room (walls/floors/ceilings, one ARGBHalf accumulation RenderTexture per plane, shader
  `Resources/Shaders/WallScanSplat` - weight = facing^2 x edge x sharpness / distance, ^4; `WallScanShow` divides and
  alpha-tests, so unseen spots stay holes). Live while recording (`WallPhotoCapture.PaintLive`, `WallOverlays.ForceScan`),
  otherwise built in the background from the frames on disk (`BuildMissing`). Cached per plane as PNG + `manifest.json`
  (frames held) in `WallPhotos/WallScan` (headset) / `Exports/RoomData/WallScan` (PC). Desktop: `-overlays scan`,
  `-test views -views rooms`. Single photos (cable photos) are the secondary mode of the same tool.
- **Niches** (furniture spec item `"type": "niche"`): cut into the walls by `XRModelFactory` (`Niches`, installed with the
  spec's ceilings by `Furniture.InstallCeilings`); `furnish.py` only marks them on the wall face.
- **Scan sources**: the selected scan never changes by itself (only a tap in the scan list; taps are debounced, loads
  serialized). A saved scan's edits/routes/names save themselves - "Save live scan" (Data tab) only saves the Quest's
  live scene, orange when the live scan has a room no saved scan has (PendingChanges.CheckLive, by room ids). Edits on
  the live scan wait in `ScanCache/_live.edits.json` (`HouseEditsStore.LiveBound`; Data tab orange while `LiveHasEdits`) and
  "Save live scan" (the user's own press, never automatic) hands them to the NEW scan (`CarryLiveInto`). The live scan
  never touches an existing saved scan and nothing saves it by itself - the Data tab only turns orange. A new doorway /
  missing wall is written into a saved scan's file, so it is refused on the live scan (`EditModeController.EnsureSavedScan`). On the headset a saved scan is
  lined up with the house via shared room UUIDs (AlignToLive -> TrackingSpaceOffset).
- **Finished vs rough model** (menu View tab toggle, `Furniture.Finished`, desktop `-finished 1|0`): spec
  `"floor_buildup": [m per storey, bottom first]` raises floors/furniture/stairs to the finished floor and
  `"suspended_ceilings": [{"rooms": [...], "h"}]` lowers ceilings; off (default) = the rough shell, for tracing pipes in
  the floor and cables above the ceiling.
- **New doorway** (Doors tool: trigger on a bare wall): `SceneJsonEdit.AddDoor` writes a DOOR_FRAME into the saved
  scan's own JSON (V1 entity or V2 room-mesh face; JSON/Unity axis flip calibrated from the walls; backup
  `*.bak_doors`). Desktop test `-test adddoor` (run it on a copy of a scan).
  Not on the live scan (save it first, then pick the saved scan). **Missing wall** (button in the add panel): `MissingWall.Find` takes the
  loose wall ends of a room's outline nearest to where you pointed, `SceneJsonEdit.AddWall` joins them with a WALL_FACE
  (V2 face / V1 entity inserted in `wallUuids`); the walk-through keeps your spot over a reload (`TryGetFeetInRoom`, room + room-local point).
- **Sliding furniture parts** (shower doors, spec glass `{"side":"front","sliding":true}`): parts named `@slide...`
  move in the walk-through (WalkDoors.SetFurnitureSliders) - open when you come close or on the trigger.
- **Logs**: headset session logs are in `persistentDataPath/Logs` (menu "Pull logs from Quest" -> `Logs/Quest`),
  never in the export folder.
- **Build + install to a USB Quest**: call `MRUKExporter.EditorFastBuildAndInstall()` (e.g. via the Unity MCP
  `InvokeComponentMethod`; the call times out because the main thread is busy, the build continues). Read
  `%LOCALAPPDATA%\Unity\Editor\Editor.log` for "BUILD COMPLETED SUCCESSFULLY" / "FAST BUILD AND INSTALL DONE" /
  "BUILD FAILED". No Play Mode, no project edits while building. Mono has no ARM64 player here, so IL2CPP only.
  `MRUKEditorTools` is the one Quest build path (build -> adb install -> launch); the package id comes from Player
  Settings (`cz.veks.XRHouseDesignExport`). Batch (Unity closed): `Unity.exe -batchmode -quit -projectPath .
  -buildTarget Android -executeMethod BuildQuest.BuildAndRun` (exit code 0 = built, installed, started).
- **Windows desktop app** (`DesktopWalkScene`: `DesktopWalkApp` + `DesktopWalkRig` driving the shared
  `WalkThroughMode` with keyboard/mouse; reads `Exports/RoomData` via `DataFolder.txt` next to the exe). Build it with
  menu MRUK > Desktop > Build (background) - it runs in the shadow project `../XRHouseDesignExport_WinBuild`
  (junctions to Assets/Packages/ProjectSettings, own Library on Windows), so this project never switches off Android.
  Never build Windows in this project's Library, and never run the Windows and Quest builds at the same time: they
  share Assets (the Windows build briefly saves the Standalone XR auto-start off; AR Foundation moves XR Simulation
  assets to Assets/XR/Temp during a build) - `Builds/Windows/.building` blocks the Quest build meanwhile. The Windows
  build leaves out Meta's telemetry/tooling native plugins (startup). The scene is not in EditorBuildSettings. Smoke
  test: `XRHouseWalk.exe -scan <folder> -shot <png>` loads, captures a frame, exits 0 if the walk started.
  Scripted checks (DesktopWalkTests.cs), screenshots + `TEST`/`TRACE` log lines, exit 0 = passed:
  `-scan <folder> -test views|doors|windows|stairs|fall|holes -out <dir> [-views "x,y,z,yaw,pitch;..."]`
  (scan-frame floor points). Use these to verify walk-through changes before asking the user to try the headset.
- **Walk-through rules**: stairs cut nothing - only openings drawn with the Hole tool cut floors/ceilings; you can
  fall only inside a drawn opening (never out of a wall your head went into); a walled-up doorway is removed for
  every room that captured it (DoorCatalog.IsWalledUp).
- **Offline compile check**: compile `Assets/Scripts/**/*.cs` with dotnet against `Library/ScriptAssemblies/*.dll`
  (defines `UNITY_EDITOR;UNITY_ANDROID;META_XR_SDK_INSTALLED`) for a fast error check without a Unity reload.
  Without Unity's DLLs (a cloud session; `apt-get install -y dotnet-sdk-8.0` works there): `Tools/OfflineTests/SyntaxCheck`
  parses every script (syntax errors only) and `Tools/OfflineTests/WallTest` runs the Missing-wall logic (`MissingWall`,
  `SceneJsonEdit.AddWall`, V1 + V2 scan formats) against a synthetic room with stand-ins for the Unity types (`Stubs.cs`).
  Run `dotnet run -c Release` inside each folder; keep `WallTest` passing when touching those files.
- **Built-in modules**: unused ones (cloth, terrain, tilemap, wind, video...) are out of `manifest.json`;
  Meta's SDK needs `assetbundle`, `physics2d`, `particlesystem` and (Android) `director` without declaring them - keep
  those. `com.unity.xr.meta-openxr` (+ AR Foundation, XR Simulation assets) was removed - nothing used it; Meta XR Core
  + MRUK do passthrough/scene on their own (backup of the old setup: `Logs/ci/bak_metaopenxr` on the PC). Keep
  `com.unity.xr.management` 4.7.0 declared (4.5.3 can't read our XR settings -> no OpenXR in the APK, app hangs on the
  loading dots) and `Assets/Editor/OpenXRRuntimeLibFix.cs` (forces libUnityOpenXR.so into the build). Check a new APK
  has `lib/arm64-v8a/libUnityOpenXR.so`.
  Desktop tests on a PC whose display is off crawl at ~3 fps with vsync (stairs then "fail") - add `-novsync`.
- **Branding**: store art in `Store/` on the PC (icon 512, covers, hero, mini); the Quest system splash is
  `Assets/Branding/XRHouseSplash.png` (stereo side-by-side, 2x 1600x1000, set in `OculusProjectConfig`, passthrough
  background) - the OS shows it instantly instead of the loading dots. Unity's own splash is off.
- **Do not add packages that spawn helper processes** (`com.unity.ai.assistant`'s `relay_win.exe` inherited the MCP
  bridge's listening socket on Windows and made the bridge unable to rebind after a domain reload).
- Tests: `Assets/Tests/EditMode` (plan geometry, room names) and `Assets/Tests/PlayMode` (whole export pipeline on
  MRUK sample scenes, results in `Exports/Test/`).
