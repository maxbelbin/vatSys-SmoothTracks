<img width="3780" height="1890" alt="SmoothTracks - a vatSys Plugin" src="https://github.com/user-attachments/assets/a5197df0-5f9c-449c-98bd-485035728537" />

# Smooth Tracks for vatSys

**Smooth Tracks** is an experimental vatSys plugin that improves the visual movement of aircraft tracks between network position updates.

Instead of leaving a target at its last reported position and then jumping it forward when the next update arrives, Smooth Tracks predicts where the aircraft should currently be and smoothly moves the displayed track towards that position.

The plugin works on both **air radar displays** and **ASMGCS/ground displays**, while leaving vatSys's underlying surveillance data unchanged.

> Smooth Tracks is currently under active development. Behaviour may change between development builds and releases.

---

## Features

### Smooth aircraft movement

Smooth Tracks predicts movement between real position reports using the aircraft's latest:

- Position
- Groundspeed
- Heading
- Time since the last report

The displayed track is then smoothly moved towards the predicted position each render frame.

Air and ground tracks can use separate smoothing values so fast airborne aircraft and slow taxiing aircraft can be tuned independently.

### Curved turn prediction

Smooth Tracks can estimate an aircraft's turn rate from changes in heading between real radar reports.

When a sustained turn is detected, the plugin predicts the aircraft along a **curved path** rather than continuing to project it in a straight line.

Turn prediction includes:

- Configurable turn-rate smoothing
- Separate maximum turn rates for airborne and ground aircraft
- Filtering of very small heading changes to reduce noise
- Automatic fallback to straight-line prediction when no meaningful turn is detected

Turn rate is updated only when a **new real radar report** is received. It is not calculated from Smooth Tracks' own predicted positions.

### Ground smoothing

Ground tracks use their own smoothing value, allowing ASMGCS movement to remain responsive while still reducing the visible jumping caused by network update intervals.

Additional ground-specific prediction improvements are planned.

### Smoothed altitude display

Smooth Tracks can estimate altitude between reports using the latest corrected altitude and vertical speed.

Altitude display behaviour is configurable, including:

- Altitude smoothing
- Display step size
- Correction/snap threshold

The default altitude display step is **100 ft**.

This only affects the displayed altitude used by the patched label rendering. The underlying vatSys radar-track altitude is not replaced.

### Smoothed history trails

Smooth Tracks maintains its own visual history trail using the smoothed/predicted track position.

This avoids history dots being based only on the original jumping network positions.

You can configure:

- History sample interval
- Number of history dots displayed
- Visual history buffer size

### Track labels stay attached

Track labels and interactive label movement use the same smoothed position as the aircraft symbol.

This includes the right-click label-moving overlay, reducing cases where a label would appear attached to the raw network position while the aircraft symbol was being smoothed elsewhere.

---

## Settings

Smooth Tracks adds its own menu to vatSys:

```text
Tools
└── Smooth Tracks
    ├── Disable/Enable Smoothing
    ├── Settings...
    └── Reset to Defaults
```

The menu is available in the main vatSys window, ASD and ASMGCS.

The settings window uses vatSys-style fonts, colours and controls and is **modeless**, meaning it can remain open while you continue interacting with the radar, moving labels, panning the display or using other vatSys windows.

### Track movement

| Setting | Default | Description |
| --- | ---: | --- |
| Visual refresh interval | 0 ms | `0` updates the visual prediction every render frame |
| Maximum prediction time | 6.0 s | Maximum time Smooth Tracks will extrapolate beyond the latest real report |
| Air smoothness | 0.16 s | Response time for airborne track corrections |
| Ground smoothness | 0.10 s | Response time for ground track corrections |
| Correction snap distance | 2.0 NM | Large errors above this threshold snap to the predicted target |

Lower smoothing values follow the predicted target more tightly. Higher values produce softer corrections but introduce more visual lag.

### Turn prediction

| Setting | Default | Description |
| --- | ---: | --- |
| Curved turn prediction | Enabled | Enables turn-rate based curved prediction |
| Turn-rate smoothing | 4.0 s | Filters rapid changes in estimated turn rate |
| Maximum air turn rate | 3.0°/s | Limits airborne turn prediction |
| Maximum ground turn rate | 20.0°/s | Limits ground turn prediction |

### History trails

| Setting | Default | Description |
| --- | ---: | --- |
| History sample interval | 5.0 s | Time between visual history samples |
| History dots displayed | vatSys value / 5 | Uses the current vatSys value where available |
| History buffer size | 20 points | Maximum number of visual history samples retained |

### Altitude

| Setting | Default | Description |
| --- | ---: | --- |
| Altitude smoothing | Enabled | Enables estimated altitude between reports |
| Altitude smoothness | 0.20 s | Response time for altitude corrections |
| Altitude display step | 100 ft | Rounds the displayed estimate to this increment |
| Altitude snap threshold | 1500 ft | Large altitude errors snap rather than slowly correct |

---

## Settings storage

Settings are saved outside the plugin directory so updating the plugin DLL does not remove your configuration.

```text
%LOCALAPPDATA%\vatSys\SmoothTracks\settings.ini
```

The settings file is created automatically.

---

## Does Smooth Tracks delay the feed?

**No.**

Smooth Tracks does not intentionally delay VATSIM or vatSys position reports.

Real position reports continue to arrive and be processed by vatSys normally. Smooth Tracks changes only the **visual position used while rendering**.

The basic process is:

```text
Latest real position
        ↓
Groundspeed + heading + elapsed time
        ↓
Turn-rate estimation where applicable
        ↓
Predicted current position
        ↓
Display smoothing
        ↓
Rendered track
```

This is primarily **real-time extrapolation/dead reckoning with interpolation-style smoothing**.

It is not traditional delayed interpolation, where the display waits for a future position report before drawing movement between two known positions.

---

## Installation

1. Download the latest Smooth Tracks release.
2. Extract the plugin folder into your vatSys plugins directory.
3. Ensure the plugin folder contains:

```text
Smooth Tracks Plugin\
├── vatsys.SmoothTracks.dll
└── 0Harmony.dll
```

4. Place that folder under:

```text
<vatSys installation>\bin\Plugins\
```

For a standard vatSys installation this will normally be under:

```text
C:\Program Files (x86)\vatSys\bin\Plugins\
```

5. Restart vatSys.
6. Open:

```text
Tools → Smooth Tracks
```

The plugin should also appear as **Smooth Tracks** in vatSys's plugin/about information.

---

## Updating

For normal Smooth Tracks updates, replace:

```text
vatsys.SmoothTracks.dll
```

with the DLL from the new release.

`0Harmony.dll` only needs to be replaced when the Harmony dependency included with the release changes.

Your settings remain stored separately under `%LOCALAPPDATA%`.

---

## Building from source

Smooth Tracks currently targets:

- **.NET Framework 4.7.2**
- **x86**
- **Lib.Harmony 2.4.2**

The project references the standard vatSys installation:

```text
C:\Program Files (x86)\vatSys\bin\vatSys.exe
```

Build with:

```powershell
dotnet build SmoothTracksPlugin.csproj -c Release
```

The compiled plugin assembly is:

```text
vatsys.SmoothTracks.dll
```

The current project file contains a local development `OutputPath`. If you clone the repository to a different location, update that path in `SmoothTracksPlugin.csproj` or replace it with an output path suitable for your environment.

---

## How it integrates with vatSys

Smooth Tracks uses the standard vatSys plugin interface and **Harmony** runtime patches to substitute smoothed display values in selected rendering paths.

Current patches cover areas including:

- Air radar track position
- Air radar label position
- Ground/ASMGCS track position
- Track history rendering
- Altitude label rendering
- Interactive track/label screen position
- Label-drag rendering

The intention is to keep the underlying `RadarTrack` data intact wherever possible and modify only the values used for presentation.

This is important because vatSys continues to use its normal surveillance information for its own internal processing.

---

## Debug log

Smooth Tracks writes diagnostic information to:

```text
%TEMP%\vatSys-SmoothTracks.log
```

The log can be useful when checking whether Harmony patches loaded successfully or when diagnosing behaviour after a vatSys update.

---

## Current limitations

Smooth Tracks is experimental and prediction will never perfectly reproduce an aircraft's movement between real network reports.

Examples include:

- Sudden heading changes
- Very sharp turns
- Rapid changes in groundspeed
- Aircraft stopping while taxiing
- Large network/report timing gaps
- Unusual or incorrect heading, speed or vertical-speed data

Curved turn prediction improves sustained turns, but it is still an estimate based on the latest available reports.

If the prediction becomes significantly different from the target, Smooth Tracks uses configurable correction/snap thresholds to recover.

---

## Planned improvements

Current development priorities include:

- Improved ground stop detection
- Raw vs smoothed position comparison/debug mode
- Per-track diagnostics
- Smoothing presets
- Adaptive smoothing
- Improved speed and heading filtering
- Additional ground-specific prediction behaviour
- Compatibility checks for future vatSys versions

---

## Development status

Smooth Tracks is currently an **experimental/alpha project**.

Testing and feedback are particularly useful for:

- Turns onto final
- Departure turns
- Vectoring and STAR turns
- Runway exits
- Taxiway turns
- Aircraft stopping on the ground
- Changes between climb, level flight and descent
- Long periods between network reports

When reporting an issue, including the callsign, approximate situation and `%TEMP%\vatSys-SmoothTracks.log` can help identify the cause.

---

## Disclaimer

Smooth Tracks is an independent experimental plugin. It does not increase the VATSIM network position-report rate and does not replace vatSys's underlying surveillance data.

Use development builds with the expectation that prediction and rendering behaviour may still require tuning.
