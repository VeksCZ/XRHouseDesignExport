# XRHouseDesignExporter

**Unity tool for exporting scanned Mixed Reality Utility Kit (MRUK) scene data from Meta Quest devices.**

## Overview
XRHouseDesignExporter is a specialized utility designed for Meta Quest developers and designers. It allows users to capture and export environmental data scanned using the Meta Mixed Reality Utility Kit (MRUK), facilitating the transition from physical space capture to digital design workflows.

## Key Features
- **MRUK Data Export**: Streamlined process for exporting scene anchors, meshes, and plane data (.obj, .glb, .json, .html).
- **GLB/glTF Support**: Modern binary format for direct import into Blender, Unity, or Windows 3D Viewer.
- **Quest Native**: Optimized for standalone execution on Meta Quest headsets.
- **Design Ready**: Outputs data in formats compatible with further architectural or interior design processing.

## Versioning
Current Version: **0.2.0** (Beta)
We follow [Semantic Versioning](https://semver.org/).

## Requirements
- **Unity**: 6000.3.9f1 (Unity 6)
- **Target Platform**: Android (Meta Quest)
- **SDKs**: Meta XR SDK (v65+ recommended), MRUK.
- **Render Pipeline**: URP (Universal Render Pipeline).

## Installation & Setup
1. **Clone the Repository**:
   ```bash
   git clone https://github.com/[Your-Username]/XRHouseDesignExporter.git
   ```
2. **Open in Unity**: Use Unity Hub to open the project with version `6000.3.9f1`.
3. **Check Project Settings**:
   - Ensure **OVR Metrics Tool** and **MRUK** are correctly configured.
   - Verify **Android** is the active build platform.

## Contributing
1. Fork the project.
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`).
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`).
4. Push to the Branch (`git push origin feature/AmazingFeature`).
5. Open a Pull Request.

## License
Distributed under the MIT License. See `LICENSE` for more information.
