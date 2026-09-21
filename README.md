# XRHouseDesignExport

**Unity tool for Meta Quest that scans a home with the Mixed Reality Utility Kit (MRUK) and exports 3D models, floor plans and reports from the scan.**

## What it does
- **Scan once, export anywhere** – "Save scan" stores the scene MRUK currently holds (including the global mesh) in the app's local cache. Any saved scan can be exported later, away from the building; the scan row in the menu picks the source (live scene or a saved scan).
- **3D export** (OBJ + GLB, valid glTF 2.0) in four flavours: clean anchor reconstruction (the Blender-ready one: real wall thickness, blue windows, brown doors, floor-length balcony doors), mesh-based, analytical mesh and raw scan. Multi-room, multi-floor and sloped ceilings are handled.
- **HTML report** with a floor plan per story and per room, dimension chains (exterior walls from outside, openings from inside), axis-aligned plans, and tables.
- **In-headset viewers** – a dollhouse of the whole scan and a floor-plan viewer with paging. Rooms can be named from a preset list (Quest does not share its room names with apps); the names are stored per room and used in plans, reports and folders.
- Exports go to dated session folders, `Export_<timestamp>_<scan name>`.

## Using the app
The menu floats in front of you (follows your gaze); only the right controller is needed. Point and pull the trigger.

| Button | Shortcut | |
|---|---|---|
| Export | B | Runs the full export of the selected source |
| Dollhouse | A | Toggles the in-headset dollhouse |
| Floor plans | left stick | Plan viewer (`<` `>` page, "Rename room" names the room) |
| Save scan | X (left) | Stores the live scene in the scan cache |
| Open report | right stick | Opens the HTML report |
| Load scan | | Loads the selected saved scan |

## Requirements
- **Unity** 6000.3.9f1 (Unity 6), Android build target (IL2CPP, ARM64), URP.
- **Meta XR SDK / MRUK 205.0.0** (pinned in `Packages/manifest.json`). Target the newest SDK APIs.
- A Quest 3 with a completed Space Setup (Scene permission granted to the app).

## Editor tooling (menu **MRUK**)
Build, install to a USB-connected Quest, pull exports and scan cache, and export from a scan opened in the Editor. `MRUKExporter.EditorFastBuildAndInstall()` is the one-call build + `adb install` + launch used for iteration; build results are in the Unity `Editor.log` ("BUILD COMPLETED SUCCESSFULLY", "FAST BUILD AND INSTALL DONE" or "BUILD FAILED"). Do not edit project files or enter Play Mode while it builds.

Exports and scan cache pulled from the headset land in `Exports/` (git-ignored).

## Tests
`Assets/Tests` – EditMode tests for the floor-plan geometry and room names, PlayMode tests that run the whole export pipeline against MRUK's sample scenes. Run them from Window > General > Test Runner.

## Notes
- Don't install packages that spawn helper processes (e.g. `com.unity.ai.assistant`'s `relay_win.exe` inherits the MCP bridge's listening socket on Windows, so the bridge can't rebind after a domain reload).
- The app reads/writes only inside its own storage (`persistentDataPath`) because of Android scoped storage; use the Editor menu to pull files to the PC.

## Versioning
Current version: see `Assets/Resources/BuildInfo.txt` (written on every build). [Semantic Versioning](https://semver.org/).

## License
Distributed under the MIT License. See `LICENSE`.
