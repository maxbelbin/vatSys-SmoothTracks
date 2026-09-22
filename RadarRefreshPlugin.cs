using System.Runtime.CompilerServices;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel.Composition;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using vatsys.Plugin;

namespace vatsys.RadarRefreshPlugin
{
   [Export(typeof(IPlugin))]
public class RadarRefreshPlugin : IPlugin
{
    private static bool patched;

    private const double MAX_PREDICTION_SECONDS = 6.0;

    private const double AIR_SMOOTHING_SECONDS = 0.16;
    private const double GROUND_SMOOTHING_SECONDS = 0.10;

    private const double SNAP_DISTANCE_NM = 2.0;

// Keep the normal vatSys-style history spacing.
// One visual history dot every 5 seconds.
private const double HISTORY_SAMPLE_SECONDS = 5.0;

// Keep more than vatSys currently needs so changing the
// History Trails setting doesn't immediately empty the trail.
private const int VISUAL_HISTORY_SIZE = 20;

private sealed class SmoothingState
{
    public Coordinate DisplayPosition;
    public DateTime LastRenderTime;

    // History made from the SMOOTHED displayed aircraft position.
    public DateTime LastHistorySampleTime = DateTime.MinValue;

    public Queue<RDP.RadarTrackHistory> VisualHistory =
        new Queue<RDP.RadarTrackHistory>();
}

    private static readonly ConditionalWeakTable<RDP.RadarTrack, SmoothingState>
        SmoothingStates =
            new ConditionalWeakTable<RDP.RadarTrack, SmoothingState>();

    public string Name => "Smooth Tracks v0.1.0-alpha";
        public RadarRefreshPlugin()
        {
            try
            {
                ApplyPatches();
                Log("Plugin loaded and display patches applied.");
            }
            catch (Exception ex)
            {
                Log("PATCH ERROR:\r\n" + ex);
            }
        }

        private static void ApplyPatches()
        {
            if (patched)
                return;

            Harmony harmony =
                new Harmony("vatsys.radarrefresh.display.05");

            Assembly vatsysAssembly = typeof(RDP).Assembly;

            Type asdControlType =
                vatsysAssembly.GetType("vatsys.ASDControlDX");

            if (asdControlType == null)
                throw new Exception("Could not find vatsys.ASDControlDX.");

            // AIR RADAR SYMBOL
            MethodInfo paintASDTrack = AccessTools.Method(
                asdControlType,
                "PaintASDTrack"
            );

            if (paintASDTrack == null)
                throw new Exception("Could not find PaintASDTrack.");

            harmony.Patch(
                paintASDTrack,
                transpiler: new HarmonyMethod(
                    typeof(RadarRefreshPlugin),
                    nameof(AirPositionTranspiler)
                )
            );

            // AIR RADAR LABEL
            MethodInfo paintASDLabel = AccessTools.Method(
                asdControlType,
                "PaintASDLabel"
            );

            if (paintASDLabel == null)
                throw new Exception("Could not find PaintASDLabel.");

            harmony.Patch(
                paintASDLabel,
                transpiler: new HarmonyMethod(
                    typeof(RadarRefreshPlugin),
                    nameof(AirPositionTranspiler)
                )
            );

            // GROUND / ASMGCS
            MethodInfo paintGroundTracks = AccessTools.Method(
                asdControlType,
                "PaintGroundTracks"
            );

            if (paintGroundTracks == null)
                throw new Exception("Could not find PaintGroundTracks.");

            harmony.Patch(
                paintGroundTracks,
                transpiler: new HarmonyMethod(
                    typeof(RadarRefreshPlugin),
                    nameof(GroundPositionTranspiler)
                )
            );
// INTERACTIVE LABEL / TRACK POSITION
// Used when moving labels with right-click.
// Makes the drag anchor use the same smoothed position
// as the displayed aircraft symbol.
MethodInfo getScreenLocation = AccessTools.Method(
    asdControlType,
    "GetScreenLocation"
);

if (getScreenLocation == null)
    throw new Exception("Could not find GetScreenLocation.");

harmony.Patch(
    getScreenLocation,
    transpiler: new HarmonyMethod(
        typeof(RadarRefreshPlugin),
        nameof(AirPositionTranspiler)
    )
); // LABEL DRAG OVERLAY / INTERACTIVE TOOLS
// Render() directly calls Track.GetLocation() while a label is
// being moved, bypassing GetScreenLocation().
MethodInfo renderMethod = AccessTools.Method(
    asdControlType,
    "Render"
);

if (renderMethod == null)
    throw new Exception("Could not find ASDControlDX.Render.");

harmony.Patch(
    renderMethod,
    transpiler: new HarmonyMethod(
        typeof(RadarRefreshPlugin),
        nameof(AirPositionTranspiler)
    )
);
            patched = true;
        }

        // ----------------------------------------------------------
        // AIR DISPLAY
        // ----------------------------------------------------------

        private static IEnumerable<CodeInstruction> AirPositionTranspiler(
    IEnumerable<CodeInstruction> instructions)
{
    MethodInfo originalLocation =
        AccessTools.Method(
            typeof(Track),
            nameof(Track.GetLocation)
        );

    MethodInfo replacementLocation =
        AccessTools.Method(
            typeof(RadarRefreshPlugin),
            nameof(GetDisplayLocation)
        );

    MethodInfo originalHistory =
        AccessTools.PropertyGetter(
            typeof(RDP.RadarTrack),
            nameof(RDP.RadarTrack.PositionHistory)
        );

    MethodInfo replacementHistory =
        AccessTools.Method(
            typeof(RadarRefreshPlugin),
            nameof(GetDisplayHistory)
        );

    int locationReplacements = 0;
    int historyReplacements = 0;

    foreach (CodeInstruction instruction in instructions)
    {
        if (instruction.operand is MethodInfo method)
        {
            // Replace normal aircraft position with smoothed position.
            if (method == originalLocation)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacementLocation;

                locationReplacements++;
            }

            // Replace REAL history queue with our VISUAL
            // smoothed/estimated history queue.
            else if (method == originalHistory)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacementHistory;

                historyReplacements++;
            }
        }

        yield return instruction;
    }

    Log(
        "Air transpiler: " +
        locationReplacements +
        " location replacement(s), " +
        historyReplacements +
        " history replacement(s)."
    );
}

        private static IEnumerable<CodeInstruction> GroundPositionTranspiler(
    IEnumerable<CodeInstruction> instructions)
{
    MethodInfo originalPosition =
        AccessTools.PropertyGetter(
            typeof(RDP.RadarTrack),
            nameof(RDP.RadarTrack.LatLong)
        );

    MethodInfo replacementPosition =
        AccessTools.Method(
            typeof(RadarRefreshPlugin),
            nameof(GetDisplayPosition)
        );

    MethodInfo originalHistory =
        AccessTools.PropertyGetter(
            typeof(RDP.RadarTrack),
            nameof(RDP.RadarTrack.PositionHistory)
        );

    MethodInfo replacementHistory =
        AccessTools.Method(
            typeof(RadarRefreshPlugin),
            nameof(GetDisplayHistory)
        );

    int positionReplacements = 0;
    int historyReplacements = 0;

    foreach (CodeInstruction instruction in instructions)
    {
        if (instruction.operand is MethodInfo method)
        {
            // Smooth ground aircraft position.
            if (method == originalPosition)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacementPosition;

                positionReplacements++;
            }

            // Use our smoothed history instead of raw radar history.
            else if (method == originalHistory)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacementHistory;

                historyReplacements++;
            }
        }

        yield return instruction;
    }

    Log(
        "Ground transpiler: " +
        positionReplacements +
        " position replacement(s), " +
        historyReplacements +
        " history replacement(s)."
    );
}
        public static Coordinate GetDisplayLocation(Track track)
        {
            if (track == null)
                return null;

            if (track.Type != Track.TrackTypes.TRACK_TYPE_RADAR)
                return track.GetLocation();

            RDP.RadarTrack radarTrack = track.GetRadarTrack();

            if (radarTrack == null)
                return track.GetLocation();

            return GetDisplayPosition(radarTrack);
        }

        public static Coordinate GetDisplayPosition(
    RDP.RadarTrack radarTrack)
{
    if (radarTrack == null)
        return null;

    Coordinate actual = radarTrack.LatLong;

    if (actual == null)
        return null;

    DateTime now = DateTime.UtcNow;

    // ----------------------------------------------------------
    // GET MOVEMENT DATA
    // ----------------------------------------------------------

    double speed = radarTrack.GroundSpeed;
    double heading = radarTrack.Heading;

    // Fallback to NetworkPilot if required.
    if ((speed < 0 || heading < 0) &&
        radarTrack.ActualAircraft != null)
    {
        speed = radarTrack.ActualAircraft.GroundSpeed;
        heading = radarTrack.ActualAircraft.Heading;
    }

    // ----------------------------------------------------------
    // CALCULATE CONTINUOUS DEAD-RECKONED POSITION
    // ----------------------------------------------------------

    Coordinate target = new Coordinate(
        actual.Latitude,
        actual.Longitude
    );

    if (!radarTrack.Cancelled &&
        !radarTrack.Coasting &&
        speed > 0 &&
        heading >= 0)
    {
        double elapsed =
            (now - radarTrack.Timestamp).TotalSeconds;

        if (elapsed < 0)
            elapsed = 0;

        if (elapsed > MAX_PREDICTION_SECONDS)
            elapsed = MAX_PREDICTION_SECONDS;

        // NO Math.Floor() anymore.
        //
        // This means elapsed might be:
        //
        // 0.016
        // 0.032
        // 0.048
        // 0.064
        //
        // rather than:
        //
        // 0.0
        // 0.5
        // 1.0
        // 1.5

        double distanceNM =
            speed * elapsed / 3600.0;

        target =
            Conversions.CalculateLLFromBearingRange(
                actual,
                distanceNM,
                heading
            );
    }

    // ----------------------------------------------------------
    // SMOOTH DISPLAY POSITION
    // ----------------------------------------------------------

    SmoothingState state =
        SmoothingStates.GetOrCreateValue(radarTrack);

    lock (state)
    {
        // First frame for this aircraft.
        if (state.DisplayPosition == null)
        {
            state.DisplayPosition = new Coordinate(
                target.Latitude,
                target.Longitude
            );

            state.LastRenderTime = now;

            return new Coordinate(
                target.Latitude,
                target.Longitude
            );
        }

        double frameTime =
            (now - state.LastRenderTime).TotalSeconds;

        if (frameTime <= 0)
        {
            return new Coordinate(
                state.DisplayPosition.Latitude,
                state.DisplayPosition.Longitude
            );
        }

        // Don't let the symbol suddenly race across the screen if
        // rendering was suspended for a moment.
        if (frameTime > 0.5)
            frameTime = 0.5;

        // ------------------------------------------------------
        // EMERGENCY SNAP
        // ------------------------------------------------------

        double difference =
            Conversions.CalculateDistance(
                state.DisplayPosition,
                target
            );

        if (difference > SNAP_DISTANCE_NM)
        {
            state.DisplayPosition = new Coordinate(
                target.Latitude,
                target.Longitude
            );

            state.LastRenderTime = now;

            return new Coordinate(
                state.DisplayPosition.Latitude,
                state.DisplayPosition.Longitude
            );
        }

        // ------------------------------------------------------
        // EXPONENTIAL SMOOTHING
        // ------------------------------------------------------

        double smoothingTime =
            radarTrack.OnGround
                ? GROUND_SMOOTHING_SECONDS
                : AIR_SMOOTHING_SECONDS;

        // Frame-rate independent smoothing.
        //
        // This behaves the same at 30 FPS, 60 FPS, 144 FPS etc.
        double alpha =
            1.0 -
            Math.Exp(
                -frameTime / smoothingTime
            );

        state.DisplayPosition =
            InterpolateCoordinate(
                state.DisplayPosition,
                target,
                alpha
            );

        state.LastRenderTime = now;

        return new Coordinate(
            state.DisplayPosition.Latitude,
            state.DisplayPosition.Longitude
        );
    }
}
private static Coordinate InterpolateCoordinate(
    Coordinate from,
    Coordinate to,
    double amount)
{
    if (amount < 0)
        amount = 0;

    if (amount > 1)
        amount = 1;

    double latitude =
        from.Latitude +
        (to.Latitude - from.Latitude) * amount;

    double longitudeDifference =
        to.Longitude - from.Longitude;

    // Handle crossing ±180 longitude correctly.
    if (longitudeDifference > 180.0)
        longitudeDifference -= 360.0;

    if (longitudeDifference < -180.0)
        longitudeDifference += 360.0;

    double longitude =
        from.Longitude +
        longitudeDifference * amount;

    if (longitude > 180.0)
        longitude -= 360.0;

    if (longitude < -180.0)
        longitude += 360.0;

    return new Coordinate(
        latitude,
        longitude
    );
}
// ----------------------------------------------------------
// SMOOTHED VISUAL HISTORY
// ----------------------------------------------------------

public static ConcurrentQueue<RDP.RadarTrackHistory> GetDisplayHistory(
    RDP.RadarTrack radarTrack)
{
    ConcurrentQueue<RDP.RadarTrackHistory> result =
        new ConcurrentQueue<RDP.RadarTrackHistory>();

    if (radarTrack == null)
        return result;

    SmoothingState state =
        SmoothingStates.GetOrCreateValue(radarTrack);

    lock (state)
    {
        DateTime now = DateTime.UtcNow;

        // Don't generate new trail points for a dead track.
        if (!radarTrack.Cancelled &&
            state.DisplayPosition != null)
        {
            // Start the timer when we first see this aircraft.
            if (state.LastHistorySampleTime == DateTime.MinValue)
            {
                state.LastHistorySampleTime = now;
            }
            else
            {
                double elapsed =
                    (now - state.LastHistorySampleTime)
                    .TotalSeconds;

                if (elapsed >= HISTORY_SAMPLE_SECONDS)
                {
                    // IMPORTANT:
                    // Copy the SMOOTHED DISPLAY position,
                    // not radarTrack.LatLong.
                    Coordinate historyPosition =
                        new Coordinate(
                            state.DisplayPosition.Latitude,
                            state.DisplayPosition.Longitude
                        );

                    RDP.RadarTrackHistory history =
                        new RDP.RadarTrackHistory(
                            historyPosition,
                            radarTrack.GroundSpeed,
                            radarTrack.Heading,
                            now,
                            false
                        );

                    state.VisualHistory.Enqueue(history);

                    while (
                        state.VisualHistory.Count >
                        VISUAL_HISTORY_SIZE)
                    {
                        state.VisualHistory.Dequeue();
                    }

                    state.LastHistorySampleTime = now;
                }
            }
        }

        // Give vatSys a COPY.
        //
        // We never give vatSys access to our actual internal queue.
        foreach (
            RDP.RadarTrackHistory history
            in state.VisualHistory)
        {
            result.Enqueue(history);
        }
    }

    return result;
}
        // ----------------------------------------------------------
        // vatSys plugin interface
        // ----------------------------------------------------------

        public void OnFDRUpdate(FDP2.FDR updated)
        {
        }

        public void OnRadarTrackUpdate(RDP.RadarTrack updated)
        {
            // Deliberately unused.
            //
            // vatSys calls this after its normal radar/coast timing,
            // so it isn't suitable for 0.5 second display updates.
        }

        // ----------------------------------------------------------
        // DEBUG LOG
        // ----------------------------------------------------------

        private static void Log(string message)
        {
            try
            {
                string path = Path.Combine(
                    Path.GetTempPath(),
                    "vatSys-RadarRefresh.log"
                );

                File.AppendAllText(
                    path,
                    DateTime.Now.ToString("HH:mm:ss.fff") +
                    "  " +
                    message +
                    Environment.NewLine
                );
            }
            catch
            {
            }
        }
    }
}