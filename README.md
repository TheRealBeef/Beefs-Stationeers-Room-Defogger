# Beef's Room Defogger

<p align="center" width="100%">
<img alt="Room Defogger Logo" src="./About/thumb.png" width="45%" />
</p>

Removes fog and atmospheric scattering in sealed, pressurized rooms while maintaining normal fog effects outdoors.

## Features

- Fog outside but not inside
- Fog transitions based on room sealing and atmospheric composition
- Distinguishes between sealed rooms, venting rooms, and outdoor areas
- Configurable fog buffer distances and adjustment speeds


- [REMOVED] Improves storm fog and particle effects for terrain-walled rooms
  - This is likely no longer needed as the improved storm system shouldn't have these issues
- [REMOVED] Dims the sun during appropriate storms
  - Unsure if required, will check and replace if necessary

## Requirements

**WARNING:** This is a StationeersLaunchPad Plugin Mod. It requires BepInEx to be installed with the StationeersLaunchPad plugin.

See: [https://github.com/StationeersLaunchPad/StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad)

## Installation

1. Ensure you have BepInEx and StationeersLaunchPad installed.
2. Install from the Steam Workshop, or manually place the DLL file into your `/BepInEx/plugins/` folder.

## Usage

Configuration available through StationeersLaunchPad config or BepInEx config files.

## Changelog
>### Version 1.1.4
> - Remove storm changes as they are fully depreciated following update to storm system on stable branch

>### Version 1.1.3
> - Fix edge case where steel angled frame placed inside sealed room could cause it to be venting

>### Version 1.1.2
> - Fixed bug causing error in coroutine
> - Stopped logging room updates
> - Added scaling for the update popup

>### Version 1.1.1
> - Disabled storm particle changes (fog effect remains stays) in multiplayer until replication is resolved

>### Version 1.1.0
>- Improved handling of terrain-walled rooms during storms. Now your closed-in/pressurized tunnels won't get storm particles
>- Overrides the storm fog effect now as well
>- Added sun dimming for storms (minus solar storm of course)

>### Version 1.0.0
>- Detect and adjust fog based on indoors or outdoors
>- Determines if a room is sealed or venting
>- Traverse and updates cache of nearby rooms to propagate effect
>- Compares internal vs external atmo for interpolation in sealed but not cleaned up rooms
>- Config options added

## Roadmap

## Source Code

The source code is available on GitHub:
[https://github.com/TheRealBeef/Beefs-Stationeers-Room-Defogger](https://github.com/TheRealBeef/Beefs-Stationeers-Room-Defogger)