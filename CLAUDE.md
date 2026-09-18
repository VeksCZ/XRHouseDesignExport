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
