# OpenDAoC-BuildNav

A fork of the [buildnav tool](https://github.com/thekroko/uthgard-opensource/tree/master/pathing/buildnav) from the [uthgard-opensource repository](https://github.com/thekroko/uthgard-opensource), originally created by [thekroko](https://github.com/thekroko).

## How it works

1.  The tool reads game asset files from DAoC’s `zones` folder and writes intermediate geometry (`.obj` / `.gset`) plus ladder definitions (`.ladders.json`).
2.  `RecastDemo.exe` builds a walkable navmesh.
3.  The final navigation meshes (`*.nav`) are used by the game server.

Shared Detour P/Invoke lives in `OpenDAoC-Core/Pathing/Detour.Managed` (also used by the game server’s `LocalPathfindingMgr`).

## Prerequisites

1.  **64-bit Environment:** All components must be built and run as 64-bit.
2.  **Detour.dll:** Build the native library from `OpenDAoC-Core/Pathing/Detour`. The build copies it to `base/lib/Detour.dll` (process working directory is `base/`). Required for ladder link placement.
3.  **Sibling repo layout:** `OpenDAoC-BuildNav` and `OpenDAoC-Core` should sit next to each other so the `Detour.Managed` project reference resolves.

## Recast integration

Recast is included as a precompiled executable in the `base` directory.

This `RecastDemo.exe` is a repurposed 64-bit build of the [official RecastDemo](https://github.com/recastnavigation/recastnavigation/tree/main/RecastDemo), slightly tweaked to accept command-line arguments and handle large worlds.
Parameters for navmesh generation (agent size, etc.) are currently hardcoded and not configurable.

It can also be used to manually inspect the generated meshes:
1.  Open `RecastDemo.exe` from the output directory.
2.  Select "Tile Mesh" as the sample.
3.  Select any generated `*.gset` as the input mesh.
4.  The corresponding `*.nav` file will also be loaded for inspection.

## Usage instructions

1.  Build and run `OpenDAoC-BuildNav.exe --daoc=<gamedir>`. Use either `--all=true`, or `--zones="id1,id2..."` and/or `--regions="id1,id2..."`.
2.  Copy the generated `*.nav` files from the output directory into your server's `/navmesh` directory.  

## Output folder structure

```
bin/Release/.../
├── OpenDAoC-BuildNav.exe
└── base/                  # Working directory at runtime
    ├── RecastDemo.exe     # Pre-compiled and tweaked Recast executable
    ├── lib/Detour.dll     # Native Detour (ladder second-pass queries)
    └── zones/             # Intermediate files (*.obj, *.gset, *.ladders.json) and *.nav
```
