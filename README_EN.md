# Build Water

**Author:** AGA7ON  
**Version:** 0.9.0

## Description
Build Water adds placeable water blocks with proper water volume behavior. It integrates with terrain so ground becomes wet and water vegetation appears near the water surface, and it lets the hoe/cultivator work through water blocks. Configuration changes apply live without restarting the game.

## Features
- Placeable water blocks with real water volume.
- Terrain moisture and water-edge vegetation support.
- Hoe/cultivator ignores water block collisions.
- Live config updates (no restart required).

## Installation
1. Install **BepInExPack for Valheim**.
2. Copy `BuildWater.dll` to `BepInEx/plugins/`.
3. Launch the game once to generate `BepInEx/config/buildwater.cfg`.

## Requirements
- Valheim
- BepInExPack_Valheim

## Dependencies
- `denikson-BepInExPack_Valheim`

## Configuration
Config file: `BepInEx/config/buildwater.cfg`
Key options:
- `SurfacePaddingMeters` (can be negative)
- `TerrainInfluenceRadiusMeters`
- `TerrainInfluenceMaxDepthMeters`
- `PlayerCheckAboveSurface`
- `CurtainMinDepth`

## Credits
Author: AGA7ON
