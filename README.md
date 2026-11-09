# AutoPlanter

Android target: Stardew Valley 1.6.15.3 with SMAPI.

This repository contains the source project and an automated GitHub Actions build.

## Planned gameplay

- A 3x3 purchasable farm building.
- A horse with a plow, not a tractor.
- A 3x3 work pass (nine tiles), at twice player walking speed.
- Autonomous work within a circular 20-tile radius of the building.
- Seed loading and planting.

## Current status

The initial project and build pipeline are being established. The current source is only a SMAPI loading stub; the gameplay listed above is not implemented yet and this is not a playable release.

The build targets .NET 6 and uses the SMAPI mod build workflow and Stardew Valley reference assemblies.
