# vatSys Smooth Tracks

Experimental vatSys plugin that provides visually smoother aircraft
movement by interpolatingextrapolating between network position reports.

## Features

- Smooth airborne radar tracks
- Smooth groundASMGCS tracks
- Continuous movement between network updates
- Smoothed position corrections
- Smoothed history trails
- Track labels remain attached to the smoothed aircraft position

## Status

Experimental  Alpha.

This plugin modifies some vatSys display methods at runtime using Harmony.
It does not modify vatSys.exe on disk.

Use for testing and feedback only.

## Installation

1. Download the latest release.
2. Create

   vatSysbinPluginsSmoothTracks

3. Copy
   - vatsys.RadarRefreshPlugin.dll
   - 0Harmony.dll

4. Restart vatSys.

## Removal

Delete the SmoothTracks plugin directory and restart vatSys.

## Known limitations

- Displayed positions are estimated between actual network reports.
- The estimated position may differ slightly from the latest true network position.
- Sudden headingspeed changes may require correction when the next report arrives.
- Compatibility with future vatSys versions is not guaranteed because Harmony patches depend on internal vatSys methods.