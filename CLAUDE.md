# XRHouseDesignExport — instructions for Claude Code

Unity tool for Meta Quest that scans a home with the Mixed Reality Utility Kit (MRUK)
and exports 3D models, floor plans and reports from the scan.

## Standing rules

- **Never `git commit` unless explicitly asked** in that session. Editing files and
  reporting progress is fine; committing is not, even after a successful fix.
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
  (scoped storage: adb-pushed files elsewhere are invisible to the app); Editor menu "Pull" copies it to `Exports/`.
- **Build + install to a USB Quest**: call `MRUKExporter.EditorFastBuildAndInstall()` (e.g. via the Unity MCP
  `InvokeComponentMethod`; the call times out because the main thread is busy, the build continues). Read
  `%LOCALAPPDATA%\Unity\Editor\Editor.log` for "BUILD COMPLETED SUCCESSFULLY" / "FAST BUILD AND INSTALL DONE" /
  "BUILD FAILED". No Play Mode, no project edits while building. Mono has no ARM64 player here, so IL2CPP only.
- **Offline compile check**: compile `Assets/Scripts/**/*.cs` with dotnet against `Library/ScriptAssemblies/*.dll`
  (defines `UNITY_EDITOR;UNITY_ANDROID;META_XR_SDK_INSTALLED`) for a fast error check without a Unity reload.
- **Do not add packages that spawn helper processes** (`com.unity.ai.assistant`'s `relay_win.exe` inherited the MCP
  bridge's listening socket on Windows and made the bridge unable to rebind after a domain reload).
- Tests: `Assets/Tests/EditMode` (plan geometry, room names) and `Assets/Tests/PlayMode` (whole export pipeline on
  MRUK sample scenes, results in `Exports/Test/`).
