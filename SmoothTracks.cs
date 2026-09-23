using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Label = System.Windows.Forms.Label;
using HarmonyLib;
using vatsys.Plugin;

namespace vatsys.SmoothTracks
{
    [Export(typeof(IPlugin))]
    public class SmoothTracksPlugin : IPlugin
    {
        private static bool patched;

        private static ConditionalWeakTable<RDP.RadarTrack, SmoothingState>
            SmoothingStates =
                new ConditionalWeakTable<RDP.RadarTrack, SmoothingState>();

        private static volatile PluginSettings currentSettings;

        private readonly List<ToolStripMenuItem> enableMenuItems =
            new List<ToolStripMenuItem>();

        private readonly List<ToolStripMenuItem> presetMenuItems =
            new List<ToolStripMenuItem>();

        // Keep one modeless settings window open at a time.
        private SettingsForm settingsForm;
        private readonly List<ToolStripMenuItem> groundAreaMenuItems = new List<ToolStripMenuItem>();

        public string Name => "Smooth Tracks";

        private enum SmoothingPreset
        {
            Realistic,
            Balanced,
            UltraSmooth,
            Custom
        }

        private sealed class PluginSettings
        {
            public bool Enabled;

            // 0 = every render frame.
            public int VisualRefreshIntervalMs;

            public double MaxPredictionSeconds;
            public double AirSmoothingSeconds;
            public double GroundSmoothingSeconds;
            public double SnapDistanceNm;

            public bool GroundMapConstraintEnabled;
            public double GroundMapBufferMetres;

            public bool GroundStopDetectionEnabled;
            public double GroundStopSpeedKnots;
            public double GroundPredictionFadeSpeedKnots;

            public bool TurnPredictionEnabled;
            public double TurnRateSmoothingSeconds;
            public double MaxAirTurnRateDegPerSecond;
            public double MaxGroundTurnRateDegPerSecond;

            public double HistorySampleSeconds;
            public int HistoryDots;
            public int VisualHistorySize;

            public bool AltitudeSmoothingEnabled;
            public double AltitudeSmoothingSeconds;
            public int AltitudeStepFeet;
            public double AltitudeSnapFeet;

            public PluginSettings Clone()
            {
                return (PluginSettings)MemberwiseClone();
            }
        }

        private sealed class SmoothingState
        {
            public Coordinate DisplayPosition;
            public DateTime LastRenderTime = DateTime.MinValue;

            // Turn-rate estimation is updated only when a new real
            // radar report arrives.
            public bool HasHeadingSample;
            public double LastRawHeading = double.NaN;
            public DateTime LastRawHeadingTimestamp = DateTime.MinValue;
            public double LastHeadingSampleIntervalSeconds;
            public double SmoothedTurnRateDegPerSecond;

            // Ground deceleration is estimated only from new real
            // radar reports. Only negative acceleration is used for
            // prediction so Smooth Tracks does not aggressively
            // accelerate taxiing aircraft between updates.
            public bool HasGroundSpeedSample;
            public double LastRawGroundSpeed = double.NaN;
            public DateTime LastGroundSpeedTimestamp = DateTime.MinValue;
            public double SmoothedGroundDecelerationKnotsPerSecond;

            public double DisplayAltitude = double.NaN;
            public DateTime LastAltitudeRenderTime = DateTime.MinValue;

            public DateTime LastHistorySampleTime = DateTime.MinValue;

            public Queue<RDP.RadarTrackHistory> VisualHistory =
                new Queue<RDP.RadarTrackHistory>();
        }

        public SmoothTracksPlugin()
        {
            try
            {
                currentSettings = LoadSettings();

                // Let the plugin control the number of displayed history dots.
                MMI.HistoryDots = currentSettings.HistoryDots;

                ApplyPatches();
                RegisterMenus();

                Log(
                    "Plugin loaded. Settings file: " +
                    GetSettingsPath()
                );

                Log(
                    "Curved turn prediction support loaded."
                );

                Log(
                    "Ground stop detection support loaded."
                );

                Log(
                    "Smoothing preset support loaded. Current preset: " +
                    GetPresetDisplayName(
                        DetectSmoothingPreset(
                            currentSettings
                        )
                    )
                );
            }
            catch (Exception ex)
            {
                Log("PLUGIN START ERROR:\r\n" + ex);
            }
        }

        // ----------------------------------------------------------
        // DEFAULTS / SETTINGS STORAGE
        // ----------------------------------------------------------

        private static PluginSettings CreateDefaultSettings()
        {
            return new PluginSettings
            {
                Enabled = true,

                // Every render frame by default.
                VisualRefreshIntervalMs = 0,

                MaxPredictionSeconds = 6.0,
                AirSmoothingSeconds = 0.16,
                GroundSmoothingSeconds = 0.10,
                SnapDistanceNm = 2.0,

                // Reduce low-speed taxi creep and predict deceleration
                // towards a stop between real position reports.
                GroundMapConstraintEnabled = true,
                GroundMapBufferMetres = 10.0,
                GroundStopDetectionEnabled = true,
                GroundStopSpeedKnots = 3.0,
                GroundPredictionFadeSpeedKnots = 15.0,

                // Curved prediction between real position reports.
                TurnPredictionEnabled = true,
                TurnRateSmoothingSeconds = 4.0,
                MaxAirTurnRateDegPerSecond = 3.0,
                MaxGroundTurnRateDegPerSecond = 20.0,

                HistorySampleSeconds = 5.0,

                // Use vatSys's current value if available, otherwise 5.
                HistoryDots =
                    MMI.HistoryDots > 0
                        ? MMI.HistoryDots
                        : 5,

                VisualHistorySize = 20,

                AltitudeSmoothingEnabled = true,
                AltitudeSmoothingSeconds = 0.20,
                AltitudeStepFeet = 100,
                AltitudeSnapFeet = 1500.0
            };
        }

        private static void ApplySmoothingPreset(
            PluginSettings settings,
            SmoothingPreset preset)
        {
            if (settings == null ||
                preset == SmoothingPreset.Custom)
            {
                return;
            }

            // Presets intentionally leave history-trail counts and
            // the master Enabled state alone. They tune the motion,
            // prediction and altitude behaviour only.
            settings.VisualRefreshIntervalMs =
                0;

            settings.GroundStopDetectionEnabled =
                true;

            settings.TurnPredictionEnabled =
                true;

            settings.AltitudeSmoothingEnabled =
                true;

            settings.AltitudeStepFeet =
                100;

            switch (preset)
            {
                case SmoothingPreset.Realistic:
                    settings.MaxPredictionSeconds =
                        5.0;

                    settings.AirSmoothingSeconds =
                        0.10;

                    settings.GroundSmoothingSeconds =
                        0.07;

                    settings.SnapDistanceNm =
                        1.5;

                    settings.GroundStopSpeedKnots =
                        3.0;

                    settings.GroundPredictionFadeSpeedKnots =
                        12.0;

                    settings.TurnRateSmoothingSeconds =
                        2.5;

                    settings.MaxAirTurnRateDegPerSecond =
                        3.0;

                    settings.MaxGroundTurnRateDegPerSecond =
                        20.0;

                    settings.AltitudeSmoothingSeconds =
                        0.12;

                    settings.AltitudeSnapFeet =
                        1000.0;

                    break;

                case SmoothingPreset.UltraSmooth:
                    settings.MaxPredictionSeconds =
                        6.0;

                    settings.AirSmoothingSeconds =
                        0.30;

                    settings.GroundSmoothingSeconds =
                        0.18;

                    settings.SnapDistanceNm =
                        3.0;

                    settings.GroundStopSpeedKnots =
                        3.0;

                    settings.GroundPredictionFadeSpeedKnots =
                        18.0;

                    settings.TurnRateSmoothingSeconds =
                        5.5;

                    settings.MaxAirTurnRateDegPerSecond =
                        3.0;

                    settings.MaxGroundTurnRateDegPerSecond =
                        20.0;

                    settings.AltitudeSmoothingSeconds =
                        0.35;

                    settings.AltitudeSnapFeet =
                        2000.0;

                    break;

                case SmoothingPreset.Balanced:
                default:
                    settings.MaxPredictionSeconds =
                        6.0;

                    settings.AirSmoothingSeconds =
                        0.16;

                    settings.GroundSmoothingSeconds =
                        0.10;

                    settings.SnapDistanceNm =
                        2.0;

                    settings.GroundStopSpeedKnots =
                        3.0;

                    settings.GroundPredictionFadeSpeedKnots =
                        15.0;

                    settings.TurnRateSmoothingSeconds =
                        4.0;

                    settings.MaxAirTurnRateDegPerSecond =
                        3.0;

                    settings.MaxGroundTurnRateDegPerSecond =
                        20.0;

                    settings.AltitudeSmoothingSeconds =
                        0.20;

                    settings.AltitudeSnapFeet =
                        1500.0;

                    break;
            }
        }

        private static SmoothingPreset DetectSmoothingPreset(
            PluginSettings settings)
        {
            if (settings == null)
                return SmoothingPreset.Custom;

            SmoothingPreset[] presets =
            {
                SmoothingPreset.Realistic,
                SmoothingPreset.Balanced,
                SmoothingPreset.UltraSmooth
            };

            foreach (
                SmoothingPreset preset
                in presets)
            {
                PluginSettings reference =
                    CreateDefaultSettings();

                ApplySmoothingPreset(
                    reference,
                    preset
                );

                if (PresetControlledSettingsMatch(
                    settings,
                    reference))
                {
                    return preset;
                }
            }

            return SmoothingPreset.Custom;
        }

        private static bool PresetControlledSettingsMatch(
            PluginSettings a,
            PluginSettings b)
        {
            if (a == null ||
                b == null)
            {
                return false;
            }

            return
                a.VisualRefreshIntervalMs ==
                    b.VisualRefreshIntervalMs &&

                NearlyEqual(
                    a.MaxPredictionSeconds,
                    b.MaxPredictionSeconds
                ) &&

                NearlyEqual(
                    a.AirSmoothingSeconds,
                    b.AirSmoothingSeconds
                ) &&

                NearlyEqual(
                    a.GroundSmoothingSeconds,
                    b.GroundSmoothingSeconds
                ) &&

                NearlyEqual(
                    a.SnapDistanceNm,
                    b.SnapDistanceNm
                ) &&

                a.GroundStopDetectionEnabled ==
                    b.GroundStopDetectionEnabled &&

                NearlyEqual(
                    a.GroundStopSpeedKnots,
                    b.GroundStopSpeedKnots
                ) &&

                NearlyEqual(
                    a.GroundPredictionFadeSpeedKnots,
                    b.GroundPredictionFadeSpeedKnots
                ) &&

                a.TurnPredictionEnabled ==
                    b.TurnPredictionEnabled &&

                NearlyEqual(
                    a.TurnRateSmoothingSeconds,
                    b.TurnRateSmoothingSeconds
                ) &&

                NearlyEqual(
                    a.MaxAirTurnRateDegPerSecond,
                    b.MaxAirTurnRateDegPerSecond
                ) &&

                NearlyEqual(
                    a.MaxGroundTurnRateDegPerSecond,
                    b.MaxGroundTurnRateDegPerSecond
                ) &&

                a.AltitudeSmoothingEnabled ==
                    b.AltitudeSmoothingEnabled &&

                NearlyEqual(
                    a.AltitudeSmoothingSeconds,
                    b.AltitudeSmoothingSeconds
                ) &&

                a.AltitudeStepFeet ==
                    b.AltitudeStepFeet &&

                NearlyEqual(
                    a.AltitudeSnapFeet,
                    b.AltitudeSnapFeet
                );
        }

        private static bool NearlyEqual(
            double a,
            double b)
        {
            return Math.Abs(
                a - b
            ) < 0.0001;
        }

        private static string GetPresetDisplayName(
            SmoothingPreset preset)
        {
            switch (preset)
            {
                case SmoothingPreset.Realistic:
                    return "Realistic";

                case SmoothingPreset.Balanced:
                    return "Balanced";

                case SmoothingPreset.UltraSmooth:
                    return "Ultra Smooth";

                default:
                    return "Custom";
            }
        }

        private static string GetSettingsPath()
        {
            string directory =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData
                    ),
                    "vatSys",
                    "SmoothTracks"
                );

            return Path.Combine(
                directory,
                "settings.ini"
            );
        }

        private static PluginSettings LoadSettings()
        {
            PluginSettings settings =
                CreateDefaultSettings();

            string path =
                GetSettingsPath();

            if (!File.Exists(path))
                return NormaliseSettings(settings);

            try
            {
                Dictionary<string, string> values =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase
                    );

                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line =
                        rawLine.Trim();

                    if (string.IsNullOrWhiteSpace(line) ||
                        line.StartsWith("#") ||
                        line.StartsWith(";"))
                    {
                        continue;
                    }

                    int separator =
                        line.IndexOf('=');

                    if (separator <= 0)
                        continue;

                    string key =
                        line.Substring(0, separator).Trim();

                    string value =
                        line.Substring(separator + 1).Trim();

                    values[key] =
                        value;
                }

                settings.Enabled =
                    ReadBool(
                        values,
                        "Enabled",
                        settings.Enabled
                    );

                settings.VisualRefreshIntervalMs =
                    ReadInt(
                        values,
                        "VisualRefreshIntervalMs",
                        settings.VisualRefreshIntervalMs
                    );

                settings.MaxPredictionSeconds =
                    ReadDouble(
                        values,
                        "MaxPredictionSeconds",
                        settings.MaxPredictionSeconds
                    );

                settings.AirSmoothingSeconds =
                    ReadDouble(
                        values,
                        "AirSmoothingSeconds",
                        settings.AirSmoothingSeconds
                    );

                settings.GroundSmoothingSeconds =
                    ReadDouble(
                        values,
                        "GroundSmoothingSeconds",
                        settings.GroundSmoothingSeconds
                    );

                settings.SnapDistanceNm =
                    ReadDouble(
                        values,
                        "SnapDistanceNm",
                        settings.SnapDistanceNm
                    );

                settings.GroundMapConstraintEnabled = ReadBool(values,
                    "GroundMapConstraintEnabled", settings.GroundMapConstraintEnabled);
                settings.GroundMapBufferMetres = ReadDouble(values,
                    "GroundMapBufferMetres", settings.GroundMapBufferMetres);

                settings.GroundStopDetectionEnabled =
                    ReadBool(
                        values,
                        "GroundStopDetectionEnabled",
                        settings.GroundStopDetectionEnabled
                    );

                settings.GroundStopSpeedKnots =
                    ReadDouble(
                        values,
                        "GroundStopSpeedKnots",
                        settings.GroundStopSpeedKnots
                    );

                settings.GroundPredictionFadeSpeedKnots =
                    ReadDouble(
                        values,
                        "GroundPredictionFadeSpeedKnots",
                        settings.GroundPredictionFadeSpeedKnots
                    );

                settings.TurnPredictionEnabled =
                    ReadBool(
                        values,
                        "TurnPredictionEnabled",
                        settings.TurnPredictionEnabled
                    );

                settings.TurnRateSmoothingSeconds =
                    ReadDouble(
                        values,
                        "TurnRateSmoothingSeconds",
                        settings.TurnRateSmoothingSeconds
                    );

                settings.MaxAirTurnRateDegPerSecond =
                    ReadDouble(
                        values,
                        "MaxAirTurnRateDegPerSecond",
                        settings.MaxAirTurnRateDegPerSecond
                    );

                settings.MaxGroundTurnRateDegPerSecond =
                    ReadDouble(
                        values,
                        "MaxGroundTurnRateDegPerSecond",
                        settings.MaxGroundTurnRateDegPerSecond
                    );

                settings.HistorySampleSeconds =
                    ReadDouble(
                        values,
                        "HistorySampleSeconds",
                        settings.HistorySampleSeconds
                    );

                settings.HistoryDots =
                    ReadInt(
                        values,
                        "HistoryDots",
                        settings.HistoryDots
                    );

                settings.VisualHistorySize =
                    ReadInt(
                        values,
                        "VisualHistorySize",
                        settings.VisualHistorySize
                    );

                settings.AltitudeSmoothingEnabled =
                    ReadBool(
                        values,
                        "AltitudeSmoothingEnabled",
                        settings.AltitudeSmoothingEnabled
                    );

                settings.AltitudeSmoothingSeconds =
                    ReadDouble(
                        values,
                        "AltitudeSmoothingSeconds",
                        settings.AltitudeSmoothingSeconds
                    );

                settings.AltitudeStepFeet =
                    ReadInt(
                        values,
                        "AltitudeStepFeet",
                        settings.AltitudeStepFeet
                    );

                settings.AltitudeSnapFeet =
                    ReadDouble(
                        values,
                        "AltitudeSnapFeet",
                        settings.AltitudeSnapFeet
                    );
            }
            catch (Exception ex)
            {
                Log(
                    "Could not load settings; using defaults.\r\n" +
                    ex
                );
            }

            return NormaliseSettings(settings);
        }

        private static void SaveSettings(
            PluginSettings settings)
        {
            try
            {
                string path =
                    GetSettingsPath();

                string directory =
                    Path.GetDirectoryName(path);

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(
                        directory
                    );
                }

                string[] lines =
                {
                    "# Smooth Tracks settings",
                    "# 0 VisualRefreshIntervalMs = every render frame",
                    "",
                    "Enabled=" + settings.Enabled,
                    "VisualRefreshIntervalMs=" +
                        settings.VisualRefreshIntervalMs,
                    "MaxPredictionSeconds=" +
                        FormatDouble(settings.MaxPredictionSeconds),
                    "AirSmoothingSeconds=" +
                        FormatDouble(settings.AirSmoothingSeconds),
                    "GroundSmoothingSeconds=" +
                        FormatDouble(settings.GroundSmoothingSeconds),
                    "SnapDistanceNm=" +
                        FormatDouble(settings.SnapDistanceNm),
                    "GroundMapConstraintEnabled=" + settings.GroundMapConstraintEnabled,
                    "GroundMapBufferMetres=" + FormatDouble(settings.GroundMapBufferMetres),
                    "GroundStopDetectionEnabled=" +
                        settings.GroundStopDetectionEnabled,
                    "GroundStopSpeedKnots=" +
                        FormatDouble(settings.GroundStopSpeedKnots),
                    "GroundPredictionFadeSpeedKnots=" +
                        FormatDouble(settings.GroundPredictionFadeSpeedKnots),
                    "TurnPredictionEnabled=" +
                        settings.TurnPredictionEnabled,
                    "TurnRateSmoothingSeconds=" +
                        FormatDouble(settings.TurnRateSmoothingSeconds),
                    "MaxAirTurnRateDegPerSecond=" +
                        FormatDouble(settings.MaxAirTurnRateDegPerSecond),
                    "MaxGroundTurnRateDegPerSecond=" +
                        FormatDouble(settings.MaxGroundTurnRateDegPerSecond),
                    "HistorySampleSeconds=" +
                        FormatDouble(settings.HistorySampleSeconds),
                    "HistoryDots=" +
                        settings.HistoryDots,
                    "VisualHistorySize=" +
                        settings.VisualHistorySize,
                    "AltitudeSmoothingEnabled=" +
                        settings.AltitudeSmoothingEnabled,
                    "AltitudeSmoothingSeconds=" +
                        FormatDouble(settings.AltitudeSmoothingSeconds),
                    "AltitudeStepFeet=" +
                        settings.AltitudeStepFeet,
                    "AltitudeSnapFeet=" +
                        FormatDouble(settings.AltitudeSnapFeet)
                };

                File.WriteAllLines(
                    path,
                    lines
                );
            }
            catch (Exception ex)
            {
                Log(
                    "Could not save settings.\r\n" +
                    ex
                );
            }
        }

        private static string FormatDouble(
            double value)
        {
            return value.ToString(
                "0.###",
                CultureInfo.InvariantCulture
            );
        }

        private static bool ReadBool(
            Dictionary<string, string> values,
            string key,
            bool fallback)
        {
            string value;

            if (values.TryGetValue(key, out value))
            {
                bool parsed;

                if (bool.TryParse(
                    value,
                    out parsed))
                {
                    return parsed;
                }
            }

            return fallback;
        }

        private static int ReadInt(
            Dictionary<string, string> values,
            string key,
            int fallback)
        {
            string value;

            if (values.TryGetValue(key, out value))
            {
                int parsed;

                if (int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out parsed))
                {
                    return parsed;
                }
            }

            return fallback;
        }

        private static double ReadDouble(
            Dictionary<string, string> values,
            string key,
            double fallback)
        {
            string value;

            if (values.TryGetValue(key, out value))
            {
                double parsed;

                if (double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out parsed))
                {
                    return parsed;
                }
            }

            return fallback;
        }

        private static PluginSettings NormaliseSettings(
            PluginSettings settings)
        {
            settings.VisualRefreshIntervalMs =
                ClampInt(
                    settings.VisualRefreshIntervalMs,
                    0,
                    2000
                );

            settings.MaxPredictionSeconds =
                ClampDouble(
                    settings.MaxPredictionSeconds,
                    0.5,
                    15.0
                );

            settings.AirSmoothingSeconds =
                ClampDouble(
                    settings.AirSmoothingSeconds,
                    0.01,
                    2.0
                );

            settings.GroundSmoothingSeconds =
                ClampDouble(
                    settings.GroundSmoothingSeconds,
                    0.01,
                    2.0
                );

            settings.SnapDistanceNm =
                ClampDouble(
                    settings.SnapDistanceNm,
                    0.1,
                    20.0
                );

            if (double.IsNaN(settings.GroundMapBufferMetres) ||
                double.IsInfinity(settings.GroundMapBufferMetres))
                settings.GroundMapBufferMetres = 10.0;
            settings.GroundMapBufferMetres = ClampDouble(settings.GroundMapBufferMetres, 0, GroundMaximumBufferMetres);

            settings.GroundStopSpeedKnots =
                ClampDouble(
                    settings.GroundStopSpeedKnots,
                    0.0,
                    10.0
                );

            settings.GroundPredictionFadeSpeedKnots =
                ClampDouble(
                    settings.GroundPredictionFadeSpeedKnots,
                    2.0,
                    40.0
                );

            // The fade range must always end above the hard-stop
            // threshold or there is no useful transition region.
            if (settings.GroundPredictionFadeSpeedKnots <=
                settings.GroundStopSpeedKnots)
            {
                settings.GroundPredictionFadeSpeedKnots =
                    Math.Min(
                        40.0,
                        settings.GroundStopSpeedKnots +
                        2.0
                    );
            }

            settings.TurnRateSmoothingSeconds =
                ClampDouble(
                    settings.TurnRateSmoothingSeconds,
                    0.1,
                    15.0
                );

            settings.MaxAirTurnRateDegPerSecond =
                ClampDouble(
                    settings.MaxAirTurnRateDegPerSecond,
                    0.1,
                    6.0
                );

            settings.MaxGroundTurnRateDegPerSecond =
                ClampDouble(
                    settings.MaxGroundTurnRateDegPerSecond,
                    1.0,
                    45.0
                );

            settings.HistorySampleSeconds =
                ClampDouble(
                    settings.HistorySampleSeconds,
                    0.5,
                    30.0
                );

            settings.HistoryDots =
                ClampInt(
                    settings.HistoryDots,
                    0,
                    30
                );

            settings.VisualHistorySize =
                ClampInt(
                    settings.VisualHistorySize,
                    1,
                    200
                );

            if (settings.VisualHistorySize <
                settings.HistoryDots)
            {
                settings.VisualHistorySize =
                    Math.Max(
                        1,
                        settings.HistoryDots
                    );
            }

            settings.AltitudeSmoothingSeconds =
                ClampDouble(
                    settings.AltitudeSmoothingSeconds,
                    0.01,
                    2.0
                );

            settings.AltitudeStepFeet =
                ClampInt(
                    settings.AltitudeStepFeet,
                    100,
                    1000
                );

            // Keep altitude steps aligned to 100 ft.
            settings.AltitudeStepFeet =
                (int)(
                    Math.Round(
                        settings.AltitudeStepFeet / 100.0,
                        MidpointRounding.AwayFromZero
                    ) * 100.0
                );

            settings.AltitudeSnapFeet =
                ClampDouble(
                    settings.AltitudeSnapFeet,
                    100.0,
                    10000.0
                );

            return settings;
        }

        private static int ClampInt(
            int value,
            int minimum,
            int maximum)
        {
            if (value < minimum)
                return minimum;

            if (value > maximum)
                return maximum;

            return value;
        }

        private static double ClampDouble(
            double value,
            double minimum,
            double maximum)
        {
            if (value < minimum)
                return minimum;

            if (value > maximum)
                return maximum;

            return value;
        }

        // ----------------------------------------------------------
        // TOOLS MENU
        // ----------------------------------------------------------

        private void RegisterMenus()
        {
            AddToolsMenu(
                CustomToolStripMenuItemWindowType.Main
            );

            AddToolsMenu(
                CustomToolStripMenuItemWindowType.ASD
            );

            AddToolsMenu(
                CustomToolStripMenuItemWindowType.ASMGCS
            );
        }

        private void AddToolsMenu(
            CustomToolStripMenuItemWindowType window)
        {
            ToolStripMenuItem parent =
                new ToolStripMenuItem(
                    "Smooth Tracks"
                );

            ToolStripMenuItem enabledItem =
                new ToolStripMenuItem(
                    currentSettings.Enabled
                        ? "Disable Smoothing"
                        : "Enable Smoothing"
                );

            enabledItem.Checked =
                currentSettings.Enabled;

            enabledItem.Click +=
                EnabledMenuItem_Click;

            enableMenuItems.Add(
                enabledItem
            );

            ToolStripMenuItem presetMenu =
                new ToolStripMenuItem(
                    "Smoothing Preset"
                );

            AddPresetMenuItem(
                presetMenu,
                "Realistic",
                SmoothingPreset.Realistic,
                clickable: true
            );

            AddPresetMenuItem(
                presetMenu,
                "Balanced",
                SmoothingPreset.Balanced,
                clickable: true
            );

            AddPresetMenuItem(
                presetMenu,
                "Ultra Smooth",
                SmoothingPreset.UltraSmooth,
                clickable: true
            );

            presetMenu.DropDownItems.Add(
                new ToolStripSeparator()
            );

            AddPresetMenuItem(
                presetMenu,
                "Custom",
                SmoothingPreset.Custom,
                clickable: false
            );

            ToolStripMenuItem settingsItem =
                new ToolStripMenuItem(
                    "Settings..."
                );

            settingsItem.Click +=
                SettingsMenuItem_Click;

            ToolStripMenuItem resetItem =
                new ToolStripMenuItem(
                    "Reset to Defaults"
                );

            resetItem.Click +=
                ResetMenuItem_Click;

            parent.DropDownItems.Add(
                enabledItem
            );

            parent.DropDownItems.Add(
                new ToolStripSeparator()
            );

            parent.DropDownItems.Add(
                presetMenu
            );

            parent.DropDownItems.Add(
                settingsItem
            );

            parent.DropDownItems.Add(
                resetItem
            );

            var groundAreasItem = new ToolStripMenuItem("Show detected ground areas (outlines)");
            groundAreasItem.Checked = showGroundAreas;
            groundAreasItem.ToolTipText = "Solid: pavement edges. Dashed: buildings/cutouts. The configured edge buffer is not drawn.";
            groundAreasItem.Click += delegate
            {
                showGroundAreas = !showGroundAreas;
                groundOverlayFailed = false;
                foreach (ToolStripMenuItem entry in groundAreaMenuItems) entry.Checked = showGroundAreas;
                MMI.RequestRedraw(redrawBackground: true);
            };
            groundAreaMenuItems.Add(groundAreasItem);
            parent.DropDownItems.Add(new ToolStripSeparator());
            parent.DropDownItems.Add(groundAreasItem);
            var reloadGroundAreas = new ToolStripMenuItem("Rebuild ground areas from loaded maps");
            reloadGroundAreas.Click += delegate
            {
                lock (groundMapsLock)
                {
                    groundMaps = null;
                    groundMapRetryAfter = DateTime.MinValue;
                }
                GetGroundMaps();
                MMI.RequestRedraw(redrawBackground: true);
            };
            parent.DropDownItems.Add(reloadGroundAreas);

            ApplyVatSysMenuStyle(
                parent
            );

            MMI.AddCustomMenuItem(
                new CustomToolStripMenuItem(
                    window,
                    CustomToolStripMenuItemCategory.Tools,
                    parent
                )
            );
        }

        private void AddPresetMenuItem(
            ToolStripMenuItem parent,
            string text,
            SmoothingPreset preset,
            bool clickable)
        {
            ToolStripMenuItem item =
                new ToolStripMenuItem(
                    text
                );

            item.Tag =
                preset;

            item.Checked =
                DetectSmoothingPreset(
                    currentSettings
                ) ==
                preset;

            item.Enabled =
                clickable;

            if (clickable)
            {
                item.Click +=
                    PresetMenuItem_Click;
            }

            presetMenuItems.Add(
                item
            );

            parent.DropDownItems.Add(
                item
            );
        }

        private void PresetMenuItem_Click(
            object sender,
            EventArgs e)
        {
            ToolStripMenuItem item =
                sender as ToolStripMenuItem;

            if (item == null ||
                !(item.Tag is SmoothingPreset))
            {
                return;
            }

            SmoothingPreset preset =
                (SmoothingPreset)item.Tag;

            if (preset ==
                SmoothingPreset.Custom)
            {
                return;
            }

            PluginSettings settings =
                currentSettings.Clone();

            ApplySmoothingPreset(
                settings,
                preset
            );

            ApplySettings(
                settings,
                save: true
            );

            if (settingsForm != null &&
                !settingsForm.IsDisposed)
            {
                settingsForm.LoadControls(
                    settings.Clone()
                );

                settingsForm.BringToFront();
            }

            Log(
                "Applied smoothing preset: " +
                GetPresetDisplayName(
                    preset
                )
            );
        }

        private void UpdatePresetMenuChecks(
            PluginSettings settings)
        {
            SmoothingPreset active =
                DetectSmoothingPreset(
                    settings
                );

            foreach (
                ToolStripMenuItem item
                in presetMenuItems)
            {
                if (item.Tag
                    is SmoothingPreset preset)
                {
                    item.Checked =
                        preset ==
                        active;
                }
            }
        }

        private static void ApplyVatSysMenuStyle(
            ToolStripMenuItem item)
        {
            item.Font =
                MMI.eurofont_winsml;

            item.BackColor =
                Colours.GetColour(
                    Colours.Identities.WindowBackground
                );

            item.ForeColor =
                Colours.GetColour(
                    Colours.Identities.InteractiveText
                );

            foreach (
                ToolStripItem child
                in item.DropDownItems)
            {
                child.Font =
                    MMI.eurofont_winsml;

                child.BackColor =
                    Colours.GetColour(
                        Colours.Identities.WindowBackground
                    );

                child.ForeColor =
                    Colours.GetColour(
                        Colours.Identities.InteractiveText
                    );

                ToolStripMenuItem childMenu =
                    child as ToolStripMenuItem;

                if (childMenu != null)
                {
                    ApplyVatSysMenuStyle(
                        childMenu
                    );
                }
            }

            item.DropDown.BackColor =
                Colours.GetColour(
                    Colours.Identities.WindowBackground
                );

            item.DropDown.ForeColor =
                Colours.GetColour(
                    Colours.Identities.InteractiveText
                );

            item.DropDown.Font =
                MMI.eurofont_winsml;
        }

        private void EnabledMenuItem_Click(
            object sender,
            EventArgs e)
        {
            PluginSettings settings =
                currentSettings.Clone();

            settings.Enabled =
                !settings.Enabled;

            ApplySettings(
                settings,
                save: true
            );
        }

        private void SettingsMenuItem_Click(
            object sender,
            EventArgs e)
        {
            // The settings window is modeless so the controller can
            // keep interacting with vatSys while tuning Smooth Tracks.
            if (settingsForm != null &&
                !settingsForm.IsDisposed)
            {
                settingsForm.BringToFront();
                settingsForm.Activate();
                return;
            }

            SettingsForm form =
                new SettingsForm(
                    currentSettings.Clone()
                );

            settingsForm =
                form;

            form.FormClosed +=
                delegate
                {
                    if (form.DialogResult ==
                            DialogResult.OK &&
                        form.ResultSettings != null)
                    {
                        ApplySettings(
                            form.ResultSettings,
                            save: true
                        );
                    }

                    if (ReferenceEquals(
                        settingsForm,
                        form))
                    {
                        settingsForm = null;
                    }
                };

            form.Show();
            form.BringToFront();
            form.Activate();
        }

        private void ResetMenuItem_Click(
            object sender,
            EventArgs e)
        {
            DialogResult result =
                ShowVatSysDialog(
                    "Reset Smooth Tracks",
                    "Reset Smooth Tracks settings to their defaults?",
                    confirmation: true
                );

            if (result != DialogResult.Yes)
                return;

            PluginSettings defaults =
                CreateDefaultSettings();

            ApplySettings(
                defaults,
                save: true
            );
        }

        private static DialogResult ShowVatSysDialog(
            string title,
            string message,
            bool confirmation)
        {
            try
            {
                Assembly vatsysAssembly =
                    typeof(MMI).Assembly;

                Type confirmationType =
                    vatsysAssembly.GetType(
                        "vatsys.GenericConfirmationWindow"
                    );

                if (confirmationType != null)
                {
                    if (confirmation)
                    {
                        MethodInfo showYesNo =
                            confirmationType.GetMethod(
                                "Show",
                                BindingFlags.Public |
                                BindingFlags.Static,
                                null,
                                new Type[]
                                {
                                    typeof(string),
                                    typeof(string)
                                },
                                null
                            );

                        if (showYesNo != null)
                        {
                            object result =
                                showYesNo.Invoke(
                                    null,
                                    new object[]
                                    {
                                        title,
                                        message
                                    }
                                );

                            if (result is DialogResult)
                            {
                                return (DialogResult)result;
                            }
                        }
                    }
                    else
                    {
                        Type buttonsType =
                            vatsysAssembly.GetType(
                                "vatsys.GenericConfirmationWindow+WindowButtons"
                            );

                        if (buttonsType != null)
                        {
                            object okayValue =
                                Enum.Parse(
                                    buttonsType,
                                    "Ok"
                                );

                            MethodInfo showOkay =
                                confirmationType.GetMethod(
                                    "Show",
                                    BindingFlags.Public |
                                    BindingFlags.Static,
                                    null,
                                    new Type[]
                                    {
                                        typeof(string),
                                        typeof(string),
                                        buttonsType
                                    },
                                    null
                                );

                            if (showOkay != null)
                            {
                                object result =
                                    showOkay.Invoke(
                                        null,
                                        new object[]
                                        {
                                            title,
                                            message,
                                            okayValue
                                        }
                                    );

                                if (result is DialogResult)
                                {
                                    return (DialogResult)result;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(
                    "Could not show native vatSys dialog.\r\n" +
                    ex
                );
            }

            return MessageBox.Show(
                message,
                title,
                confirmation
                    ? MessageBoxButtons.YesNo
                    : MessageBoxButtons.OK,
                confirmation
                    ? MessageBoxIcon.Question
                    : MessageBoxIcon.Information
            );
        }

        private void ApplySettings(
            PluginSettings settings,
            bool save)
        {
            settings =
                NormaliseSettings(
                    settings
                );

            currentSettings =
                settings;

            MMI.HistoryDots =
                settings.HistoryDots;

            // Reset interpolation state so major setting changes do
            // not carry stale visual state into the new configuration.
            SmoothingStates =
                new ConditionalWeakTable<
                    RDP.RadarTrack,
                    SmoothingState>();

            foreach (
                ToolStripMenuItem item
                in enableMenuItems)
            {
                item.Checked =
                    settings.Enabled;

                item.Text =
                    settings.Enabled
                        ? "Disable Smoothing"
                        : "Enable Smoothing";
            }

            UpdatePresetMenuChecks(
                settings
            );

            if (save)
            {
                SaveSettings(
                    settings
                );
            }

            MMI.RequestRedraw(
                redrawBackground: false
            );

            Log(
                "Settings applied."
            );
        }

        // ----------------------------------------------------------
        // HARMONY PATCH SETUP
        // ----------------------------------------------------------

        private static void ApplyPatches()
        {
            if (patched)
                return;

            Harmony harmony =
                new Harmony(
                    "vatsys.smoothtracks.display"
                );

            Assembly vatsysAssembly =
                typeof(RDP).Assembly;

            Type asdControlType =
                vatsysAssembly.GetType(
                    "vatsys.ASDControlDX"
                );

            if (asdControlType == null)
            {
                throw new Exception(
                    "Could not find vatsys.ASDControlDX."
                );
            }

            // AIR RADAR SYMBOL + HISTORY
            MethodInfo paintASDTrack =
                AccessTools.Method(
                    asdControlType,
                    "PaintASDTrack"
                );

            if (paintASDTrack == null)
            {
                throw new Exception(
                    "Could not find PaintASDTrack."
                );
            }

            harmony.Patch(
                paintASDTrack,
                transpiler:
                    new HarmonyMethod(
                        typeof(SmoothTracksPlugin),
                        nameof(AirPositionTranspiler)
                    )
            );

            // AIR RADAR LABEL POSITION
            MethodInfo paintASDLabel =
                AccessTools.Method(
                    asdControlType,
                    "PaintASDLabel"
                );

            if (paintASDLabel == null)
            {
                throw new Exception(
                    "Could not find PaintASDLabel."
                );
            }

            harmony.Patch(
                paintASDLabel,
                transpiler:
                    new HarmonyMethod(
                        typeof(SmoothTracksPlugin),
                        nameof(AirPositionTranspiler)
                    )
            );

            // ACTUAL LABEL CONTENT / ALTITUDE
            MethodInfo paintLabel =
                AccessTools.Method(
                    asdControlType,
                    "PaintLabel"
                );

            if (paintLabel == null)
            {
                throw new Exception(
                    "Could not find PaintLabel."
                );
            }

            harmony.Patch(
                paintLabel,
                transpiler:
                    new HarmonyMethod(
                        typeof(SmoothTracksPlugin),
                        nameof(AltitudeTranspiler)
                    )
            );

            // GROUND / ASMGCS POSITION + HISTORY
            MethodInfo paintGroundTracks =
                AccessTools.Method(
                    asdControlType,
                    "PaintGroundTracks"
                );

            if (paintGroundTracks == null)
            {
                throw new Exception(
                    "Could not find PaintGroundTracks."
                );
            }

            harmony.Patch(
                paintGroundTracks,
                transpiler:
                    new HarmonyMethod(
                        typeof(SmoothTracksPlugin),
                        nameof(GroundPositionTranspiler)
                    )
            );

            // INTERACTIVE LABEL / TRACK POSITION
            MethodInfo getScreenLocation =
                AccessTools.Method(
                    asdControlType,
                    "GetScreenLocation"
                );

            if (getScreenLocation == null)
            {
                throw new Exception(
                    "Could not find GetScreenLocation."
                );
            }

            harmony.Patch(
                getScreenLocation,
                transpiler:
                    new HarmonyMethod(
                        typeof(SmoothTracksPlugin),
                        nameof(AirPositionTranspiler)
                    )
            );

            // LABEL DRAG OVERLAY
            MethodInfo renderMethod =
                AccessTools.Method(
                    asdControlType,
                    "Render"
                );

            if (renderMethod == null)
            {
                throw new Exception(
                    "Could not find ASDControlDX.Render."
                );
            }

            harmony.Patch(
                renderMethod,
                transpiler:
                    new HarmonyMethod(
                        typeof(SmoothTracksPlugin),
                        nameof(AirPositionTranspiler)
                    )
            );

            // Optional diagnostic overlay. A changed drawing API must not
            // prevent the movement patches or ground constraints from loading.
            try
            {
                groundDisplayType = AccessTools.Property(asdControlType, "ASDType");
                computeGroundOverlay = AccessTools.Method(asdControlType, "ComputeMapElements");
                paintGroundOverlay = AccessTools.Method(asdControlType, "PaintMap");
                MethodInfo paintMaps = AccessTools.Method(asdControlType, "PaintMaps");
                if (groundDisplayType == null || computeGroundOverlay == null || paintGroundOverlay == null || paintMaps == null)
                    throw new MissingMethodException("Ground map drawing methods were not found.");
                harmony.Patch(paintMaps, postfix: new HarmonyMethod(typeof(SmoothTracksPlugin), nameof(PaintGroundAreasPostfix)));
            }
            catch (Exception ex)
            {
                groundOverlayFailed = true;
                Log("Could not enable ground outline preview.\r\n" + ex);
            }

            patched = true;
        }

        // ----------------------------------------------------------
        // AIR POSITION / HISTORY TRANSPILER
        // ----------------------------------------------------------

        private static IEnumerable<CodeInstruction>
            AirPositionTranspiler(
                IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo originalLocation =
                AccessTools.Method(
                    typeof(Track),
                    nameof(Track.GetLocation)
                );

            MethodInfo replacementLocation =
                AccessTools.Method(
                    typeof(SmoothTracksPlugin),
                    nameof(GetDisplayLocation)
                );

            MethodInfo originalHistory =
                AccessTools.PropertyGetter(
                    typeof(RDP.RadarTrack),
                    nameof(RDP.RadarTrack.PositionHistory)
                );

            MethodInfo replacementHistory =
                AccessTools.Method(
                    typeof(SmoothTracksPlugin),
                    nameof(GetDisplayHistory)
                );

            int locationReplacements = 0;
            int historyReplacements = 0;

            foreach (
                CodeInstruction instruction
                in instructions)
            {
                if (instruction.operand
                    is MethodInfo method)
                {
                    if (method ==
                        originalLocation)
                    {
                        instruction.opcode =
                            OpCodes.Call;

                        instruction.operand =
                            replacementLocation;

                        locationReplacements++;
                    }
                    else if (
                        method ==
                        originalHistory)
                    {
                        instruction.opcode =
                            OpCodes.Call;

                        instruction.operand =
                            replacementHistory;

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

        // ----------------------------------------------------------
        // GROUND POSITION / HISTORY TRANSPILER
        // ----------------------------------------------------------

        private static IEnumerable<CodeInstruction>
            GroundPositionTranspiler(
                IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo originalPosition =
                AccessTools.PropertyGetter(
                    typeof(RDP.RadarTrack),
                    nameof(RDP.RadarTrack.LatLong)
                );

            MethodInfo replacementPosition =
                AccessTools.Method(
                    typeof(SmoothTracksPlugin),
                    nameof(GetDisplayPosition)
                );

            MethodInfo originalHistory =
                AccessTools.PropertyGetter(
                    typeof(RDP.RadarTrack),
                    nameof(RDP.RadarTrack.PositionHistory)
                );

            MethodInfo replacementHistory =
                AccessTools.Method(
                    typeof(SmoothTracksPlugin),
                    nameof(GetDisplayHistory)
                );

            int positionReplacements = 0;
            int historyReplacements = 0;

            foreach (
                CodeInstruction instruction
                in instructions)
            {
                if (instruction.operand
                    is MethodInfo method)
                {
                    if (method ==
                        originalPosition)
                    {
                        instruction.opcode =
                            OpCodes.Call;

                        instruction.operand =
                            replacementPosition;

                        positionReplacements++;
                    }
                    else if (
                        method ==
                        originalHistory)
                    {
                        instruction.opcode =
                            OpCodes.Call;

                        instruction.operand =
                            replacementHistory;

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

        // ----------------------------------------------------------
        // ALTITUDE TRANSPILER
        // ----------------------------------------------------------

        private static IEnumerable<CodeInstruction>
            AltitudeTranspiler(
                IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo originalAltitude =
                AccessTools.PropertyGetter(
                    typeof(RDP.RadarTrack),
                    nameof(RDP.RadarTrack.CorrectedAltitude)
                );

            MethodInfo replacementAltitude =
                AccessTools.Method(
                    typeof(SmoothTracksPlugin),
                    nameof(GetDisplayAltitude)
                );

            int altitudeReplacements = 0;

            foreach (
                CodeInstruction instruction
                in instructions)
            {
                if (instruction.operand
                        is MethodInfo method &&
                    method ==
                        originalAltitude)
                {
                    instruction.opcode =
                        OpCodes.Call;

                    instruction.operand =
                        replacementAltitude;

                    altitudeReplacements++;
                }

                yield return instruction;
            }

            Log(
                "Altitude transpiler: " +
                altitudeReplacements +
                " altitude replacement(s)."
            );
        }

        // ----------------------------------------------------------
        // DISPLAY POSITION
        // ----------------------------------------------------------

        public static Coordinate GetDisplayLocation(
            Track track)
        {
            if (track == null)
                return null;

            PluginSettings settings =
                currentSettings;

            if (settings == null ||
                !settings.Enabled)
            {
                return track.GetLocation();
            }

            if (track.Type !=
                Track.TrackTypes.TRACK_TYPE_RADAR)
            {
                return track.GetLocation();
            }

            RDP.RadarTrack radarTrack =
                track.GetRadarTrack();

            if (radarTrack == null)
                return track.GetLocation();

            return GetDisplayPosition(
                radarTrack
            );
        }

        public static Coordinate GetDisplayPosition(
            RDP.RadarTrack radarTrack)
        {
            if (radarTrack == null)
                return null;

            Coordinate actual =
                radarTrack.LatLong;

            if (actual == null)
                return null;

            PluginSettings settings =
                currentSettings;

            if (settings == null ||
                !settings.Enabled)
            {
                return CopyCoordinate(
                    actual
                );
            }

            DateTime now =
                DateTime.UtcNow;

            double speed =
                radarTrack.GroundSpeed;

            double heading =
                radarTrack.Heading;

            if ((speed < 0 ||
                 heading < 0) &&
                radarTrack.ActualAircraft != null)
            {
                speed =
                    radarTrack.ActualAircraft
                        .GroundSpeed;

                heading =
                    radarTrack.ActualAircraft
                        .Heading;
            }

            SmoothingState state =
                SmoothingStates
                    .GetOrCreateValue(
                        radarTrack
                    );

            lock (state)
            {
                UpdateTurnRateEstimate(
                    radarTrack,
                    state,
                    heading,
                    speed,
                    settings
                );

                UpdateGroundMotionEstimate(
                    radarTrack,
                    state,
                    speed,
                    settings
                );

                GroundMapSnapshot pavement = null;
                if (settings.GroundMapConstraintEnabled && radarTrack.OnGround &&
                    !radarTrack.Cancelled && !radarTrack.Coasting)
                {
                    GroundMapSnapshot available = GetGroundMaps();
                    // Ground state comes from vatSys (which checks speed and
                    // altitude). A map footprint alone never makes a track ground.
                    if (available != null && available.Contains(actual, settings.GroundMapBufferMetres))
                        pavement = available;
                }

                Coordinate target =
                    new Coordinate(
                        actual.Latitude,
                        actual.Longitude
                    );

                if (!radarTrack.Cancelled &&
                    !radarTrack.Coasting &&
                    speed > 0 &&
                    heading >= 0)
                {
                    double elapsed =
                        (now -
                         radarTrack.Timestamp)
                        .TotalSeconds;

                    if (elapsed < 0)
                        elapsed = 0;

                    if (elapsed >
                        settings.MaxPredictionSeconds)
                    {
                        elapsed =
                            settings.MaxPredictionSeconds;
                    }

                    double predictionSpeed =
                        speed;

                    double predictionTime =
                        elapsed;

                    bool groundStopped =
                        false;

                    if (radarTrack.OnGround &&
                        settings.GroundStopDetectionEnabled)
                    {
                        ApplyGroundStopPrediction(
                            state,
                            settings,
                            speed,
                            elapsed,
                            out predictionSpeed,
                            out predictionTime,
                            out groundStopped
                        );
                    }

                    double turnRate =
                        settings.TurnPredictionEnabled &&
                        !groundStopped
                            ? state
                                .SmoothedTurnRateDegPerSecond
                            : 0.0;

                    double startHeading =
                        heading;

                    // vatSys derives RadarTrack.Heading from the course
                    // between the two most recent real reports. During
                    // a steady turn that course approximately represents
                    // the heading at the midpoint of that interval.
                    //
                    // Advance it by half an update interval to estimate
                    // the instantaneous heading at the latest report.
                    if (settings.TurnPredictionEnabled &&
                        !groundStopped &&
                        Math.Abs(turnRate) > 0.001 &&
                        state.LastHeadingSampleIntervalSeconds > 0)
                    {
                        startHeading =
                            NormaliseHeading(
                                heading +
                                turnRate *
                                state.LastHeadingSampleIntervalSeconds *
                                0.5
                            );
                    }

                    target =
                        PredictCurvedPosition(
                            actual,
                            predictionSpeed,
                            startHeading,
                            turnRate,
                            predictionTime
                        );

                    if (pavement != null)
                    {
                        target = LimitGroundPath(pavement, actual, settings.GroundMapBufferMetres,
                            predictionSpeed * predictionTime * 1852.0 / 3600.0,
                            fraction => PredictCurvedPosition(actual, predictionSpeed,
                                startHeading, turnRate, predictionTime * fraction));
                    }
                }

                if (pavement != null && state.DisplayPosition != null)
                    state.DisplayPosition = KeepGroundInterpolationOnPavement(pavement, actual,
                        state.DisplayPosition, target, settings.GroundMapBufferMetres);

                if (state.DisplayPosition ==
                    null)
                {
                    state.DisplayPosition =
                        CopyCoordinate(
                            target
                        );

                    state.LastRenderTime =
                        now;

                    SampleVisualHistory(
                        radarTrack,
                        state,
                        now,
                        settings
                    );

                    return CopyCoordinate(
                        state.DisplayPosition
                    );
                }

                if (
                    settings.VisualRefreshIntervalMs >
                    0 &&
                    state.LastRenderTime !=
                        DateTime.MinValue &&
                    (now -
                     state.LastRenderTime)
                        .TotalMilliseconds <
                    settings.VisualRefreshIntervalMs)
                {
                    return CopyCoordinate(
                        state.DisplayPosition
                    );
                }

                double frameTime =
                    (now -
                     state.LastRenderTime)
                    .TotalSeconds;

                if (frameTime <= 0)
                {
                    return CopyCoordinate(
                        state.DisplayPosition
                    );
                }

                if (frameTime > 0.5)
                    frameTime = 0.5;

                double difference =
                    Conversions.CalculateDistance(
                        state.DisplayPosition,
                        target
                    );

                if (difference >
                    settings.SnapDistanceNm)
                {
                    state.DisplayPosition =
                        CopyCoordinate(
                            target
                        );

                    state.LastRenderTime =
                        now;

                    SampleVisualHistory(
                        radarTrack,
                        state,
                        now,
                        settings
                    );

                    return CopyCoordinate(
                        state.DisplayPosition
                    );
                }

                double smoothingTime =
                    radarTrack.OnGround
                        ? settings
                            .GroundSmoothingSeconds
                        : settings
                            .AirSmoothingSeconds;

                // Once the real report indicates a near-stop, settle
                // back onto the real ground position quickly instead
                // of allowing the previous prediction to creep.
                if (radarTrack.OnGround &&
                    settings.GroundStopDetectionEnabled &&
                    speed <= settings.GroundStopSpeedKnots)
                {
                    smoothingTime =
                        Math.Min(
                            smoothingTime,
                            0.06
                        );
                }

                double alpha =
                    1.0 -
                    Math.Exp(
                        -frameTime /
                        smoothingTime
                    );

                state.DisplayPosition =
                    InterpolateCoordinate(
                        state.DisplayPosition,
                        target,
                        alpha
                    );

                if (pavement != null)
                    state.DisplayPosition = KeepGroundInterpolationOnPavement(pavement, actual,
                        state.DisplayPosition, target, settings.GroundMapBufferMetres);

                state.LastRenderTime =
                    now;

                SampleVisualHistory(
                    radarTrack,
                    state,
                    now,
                    settings
                );

                return CopyCoordinate(
                    state.DisplayPosition
                );
            }
        }

        // Ground geometry is copied from the active vatSys profile. It is never
        // written back to DisplayMaps, RadarTrack or the network aircraft.
        private const double GroundMetresPerDegree = 111195.0;
        private const double GroundGridDegrees = 0.005;
        private const double GroundMaximumBufferMetres = 50.0;
        private static readonly object groundMapsLock = new object();
        private static volatile GroundMapSnapshot groundMaps;
        private static DateTime groundMapRetryAfter = DateTime.MinValue;
        private static volatile bool showGroundAreas;
        private static bool groundOverlayFailed;
        private static MethodInfo computeGroundOverlay;
        private static MethodInfo paintGroundOverlay;
        private static PropertyInfo groundDisplayType;

        private static double GroundLongitudeDelta(double longitude, double origin)
        {
            double delta = (longitude - origin) % 360.0;
            if (delta > 180.0) delta -= 360.0;
            if (delta < -180.0) delta += 360.0;
            return delta;
        }

        private sealed class GroundPolygon
        {
            public readonly Coordinate[] Points;
            public readonly double MinLat, MaxLat, MinLon, MaxLon, OriginLon;
            private readonly double longitudeScale;
            private readonly double[] xs, ys;

            public GroundPolygon(List<Coordinate> points)
            {
                if (points == null || points.Count < 3 || points[0] == null)
                    throw new ArgumentException("Ground polygon needs at least three points.");
                Points = new Coordinate[points.Count];
                OriginLon = points[0].Longitude;
                MinLat = MinLon = double.PositiveInfinity;
                MaxLat = MaxLon = double.NegativeInfinity;
                for (int i = 0; i < points.Count; i++)
                {
                    Coordinate p = points[i];
                    if (p == null || double.IsNaN(p.Latitude) || double.IsNaN(p.Longitude) ||
                        double.IsInfinity(p.Latitude) || double.IsInfinity(p.Longitude) ||
                        Math.Abs(p.Latitude) > 85.0 || Math.Abs(p.Longitude) > 180.0)
                        throw new ArgumentException("Invalid ground polygon coordinate.");
                    Points[i] = new Coordinate { Latitude = p.Latitude, Longitude = p.Longitude };
                    double lon = GroundLongitudeDelta(p.Longitude, OriginLon);
                    MinLat = Math.Min(MinLat, p.Latitude);
                    MaxLat = Math.Max(MaxLat, p.Latitude);
                    MinLon = Math.Min(MinLon, lon);
                    MaxLon = Math.Max(MaxLon, lon);
                }
                // A ground polygon spanning a degree is not an airport surface.
                if (MaxLat - MinLat > 1.0 || MaxLon - MinLon > 1.0)
                    throw new ArgumentException("Ground polygon is too large.");
                longitudeScale = GroundMetresPerDegree * Math.Cos((MinLat + MaxLat) * Math.PI / 360.0);
                xs = new double[Points.Length];
                ys = new double[Points.Length];
                for (int i = 0; i < Points.Length; i++)
                {
                    xs[i] = GroundLongitudeDelta(Points[i].Longitude, OriginLon) * longitudeScale;
                    ys[i] = (Points[i].Latitude - MinLat) * GroundMetresPerDegree;
                }
            }

            public bool Contains(Coordinate position, double bufferMetres)
            {
                double lon = GroundLongitudeDelta(position.Longitude, OriginLon);
                double lat = position.Latitude;
                double latMargin = bufferMetres / GroundMetresPerDegree;
                double lonMargin = bufferMetres / longitudeScale;
                if (lat < MinLat - latMargin || lat > MaxLat + latMargin ||
                    lon < MinLon - lonMargin || lon > MaxLon + lonMargin) return false;
                double px = lon * longitudeScale, py = (lat - MinLat) * GroundMetresPerDegree;
                bool inside = false;
                double marginSquared = bufferMetres * bufferMetres;
                for (int i = 0, j = Points.Length - 1; i < Points.Length; j = i++)
                {
                    double ax = xs[j], ay = ys[j], bx = xs[i], by = ys[i];
                    if ((ay > py) != (by > py) && px < (bx - ax) * (py - ay) / (by - ay) + ax)
                        inside = !inside;
                    // Distance to each finite edge; repeated bridge vertices and
                    // concave polygons retain the renderer's even/odd fill rule.
                    double x = ax - px;
                    double y = ay - py;
                    double dx = bx - ax;
                    double dy = by - ay;
                    double lengthSquared = dx * dx + dy * dy;
                    double t = lengthSquared == 0 ? 0 : Math.Max(0, Math.Min(1, -(x * dx + y * dy) / lengthSquared));
                    double ex = x + t * dx, ey = y + t * dy;
                    if (ex * ex + ey * ey <= marginSquared + 0.000001) return true;
                }
                return inside;
            }
        }

        private sealed class GroundSurface
        {
            public GroundPolygon Polygon;
            public GroundPolygon[] Cutouts;
            public bool Building;
        }

        private sealed class GroundMapSnapshot
        {
            public readonly List<DisplayMaps.Map> Source;
            public readonly int SourceCount;
            public readonly Dictionary<uint, DisplayMaps.Map> Outlines = new Dictionary<uint, DisplayMaps.Map>();
            public readonly Dictionary<long, List<GroundSurface>> Cells = new Dictionary<long, List<GroundSurface>>();
            public int PavementCount;
            public int SkippedCount;

            public GroundMapSnapshot(List<DisplayMaps.Map> source)
            {
                Source = source;
                SourceCount = source.Count;
                foreach (DisplayMaps.Map map in source.ToArray())
                {
                    bool building = map.Type == DisplayMaps.MapTypes.Ground_BLD;
                    if (!building && map.Type != DisplayMaps.MapTypes.Ground_RWY &&
                        map.Type != DisplayMaps.MapTypes.Ground_TWY && map.Type != DisplayMaps.MapTypes.Ground_APR)
                        continue;
                    var normal = new List<GroundPolygon>();
                    var cutouts = new List<GroundPolygon>();
                    foreach (DisplayMaps.Map.Infill infill in map.Infills.ToArray())
                    {
                        if (infill.Type == DisplayMaps.Map.InfillTypes.None) continue;
                        try
                        {
                            var polygon = new GroundPolygon(infill.Points);
                            if (infill.Type == DisplayMaps.Map.InfillTypes.Background) cutouts.Add(polygon);
                            else normal.Add(polygon);
                        }
                        catch (ArgumentException) { SkippedCount++; }
                    }
                    GroundPolygon[] holes = cutouts.ToArray();
                    // Only real filled surfaces count. Taxi centre lines, runway
                    // extensions, labels and map backgrounds are not pavement.
                    foreach (GroundPolygon polygon in normal)
                    {
                        Index(new GroundSurface { Polygon = polygon, Cutouts = holes, Building = building });
                        if (!building) PavementCount++;
                    }
                    if (normal.Count == 0) continue;
                    var outline = new DisplayMaps.Map
                    {
                        Name = "Smooth Tracks ground edges: " + map.Name,
                        Category = DisplayMaps.MapCategories.Ground,
                        Type = DisplayMaps.MapTypes.Ground_INF
                    };
                    foreach (GroundPolygon polygon in normal)
                        AddOutline(outline, polygon, building);
                    foreach (GroundPolygon polygon in cutouts)
                        AddOutline(outline, polygon, true);
                    Outlines[map.Id] = outline;
                }
            }

            private static void AddOutline(DisplayMaps.Map map, GroundPolygon polygon, bool excluded)
            {
                var line = new DisplayMaps.Map.Line
                {
                    Width = 1.5f,
                    Pattern = excluded ? DisplayMaps.Map.Patterns.Dashed : DisplayMaps.Map.Patterns.Solid,
                    Points = new List<Coordinate>(polygon.Points)
                };
                line.Points.Add(polygon.Points[0]);
                map.Lines.Add(line);
            }

            private static long CellKey(int latitudeIndex, int longitudeIndex)
            {
                const int longitudeCells = 72000;
                longitudeIndex = ((longitudeIndex % longitudeCells) + longitudeCells) % longitudeCells;
                return ((long)latitudeIndex << 32) | (uint)longitudeIndex;
            }

            private void Index(GroundSurface surface)
            {
                GroundPolygon p = surface.Polygon;
                double latMargin = GroundMaximumBufferMetres / GroundMetresPerDegree;
                double lonMargin = latMargin / Math.Cos(Math.Max(Math.Abs(p.MinLat), Math.Abs(p.MaxLat)) * Math.PI / 180.0);
                int minLat = (int)Math.Floor((p.MinLat - latMargin + 90.0) / GroundGridDegrees);
                int maxLat = (int)Math.Floor((p.MaxLat + latMargin + 90.0) / GroundGridDegrees);
                int minLon = (int)Math.Floor((p.OriginLon + p.MinLon - lonMargin + 180.0) / GroundGridDegrees);
                int maxLon = (int)Math.Floor((p.OriginLon + p.MaxLon + lonMargin + 180.0) / GroundGridDegrees);
                for (int lat = minLat; lat <= maxLat; lat++)
                    for (int lon = minLon; lon <= maxLon; lon++)
                    {
                        long key = CellKey(lat, lon);
                        List<GroundSurface> list;
                        if (!Cells.TryGetValue(key, out list)) Cells[key] = list = new List<GroundSurface>();
                        list.Add(surface);
                    }
            }

            public bool Contains(Coordinate position, double bufferMetres)
            {
                if (position == null || double.IsNaN(position.Latitude) || double.IsNaN(position.Longitude)) return false;
                long key = CellKey((int)Math.Floor((position.Latitude + 90.0) / GroundGridDegrees),
                    (int)Math.Floor((position.Longitude + 180.0) / GroundGridDegrees));
                List<GroundSurface> candidates;
                if (!Cells.TryGetValue(key, out candidates)) return false;
                bool pavement = false;
                foreach (GroundSurface surface in candidates)
                {
                    // Never expand a building. Explicit cutouts remain excluded
                    // from their own surface even when an outer-edge buffer is used.
                    if (!surface.Polygon.Contains(position, surface.Building ? 0 : bufferMetres)) continue;
                    bool cutout = false;
                    foreach (GroundPolygon hole in surface.Cutouts)
                        if (hole.Contains(position, 0)) { cutout = true; break; }
                    if (cutout) continue;
                    if (surface.Building) return false;
                    pavement = true;
                }
                return pavement;
            }
        }

        private static GroundMapSnapshot GetGroundMaps()
        {
            if (!DisplayMaps.Loaded) return null;
            List<DisplayMaps.Map> source = DisplayMaps.Maps;
            GroundMapSnapshot snapshot = groundMaps;
            if (snapshot != null && ReferenceEquals(snapshot.Source, source) && snapshot.SourceCount == source.Count)
                return snapshot;
            lock (groundMapsLock)
            {
                snapshot = groundMaps;
                if (snapshot != null && ReferenceEquals(snapshot.Source, source) && snapshot.SourceCount == source.Count)
                    return snapshot;
                if (DateTime.UtcNow < groundMapRetryAfter) return null;
                try
                {
                    snapshot = new GroundMapSnapshot(source);
                    groundMaps = snapshot;
                    Log("Ground maps loaded: " + snapshot.PavementCount + " pavement polygons; " +
                        snapshot.SkippedCount + " invalid polygons skipped.");
                    return snapshot;
                }
                catch (Exception ex)
                {
                    groundMapRetryAfter = DateTime.UtcNow.AddSeconds(30);
                    Log("Ground maps unavailable; using normal smoothing.\r\n" + ex);
                    return null;
                }
            }
        }

        private static Coordinate LimitGroundPath(GroundMapSnapshot maps, Coordinate actual,
            double buffer, double pathLengthMetres, Func<double, Coordinate> pointAtFraction)
        {
            // Test the whole path, not just its end: a prediction must not jump
            // across a grass island onto another valid taxiway. Sample <= 2 m.
            if (maps == null || !maps.Contains(actual, buffer)) return pointAtFraction(1.0);
            if (double.IsNaN(pathLengthMetres) || double.IsInfinity(pathLengthMetres)) return actual;
            if (pathLengthMetres > 2000.0) return actual; // Implausible ground report.
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(0, pathLengthMetres) / 2.0));
            double lastSafe = 0;
            Coordinate safe = actual;
            for (int i = 1; i <= steps; i++)
            {
                double fraction = (double)i / steps;
                Coordinate point = pointAtFraction(fraction);
                if (!maps.Contains(point, buffer))
                {
                    double low = lastSafe, high = fraction;
                    for (int j = 0; j < 10; j++)
                    {
                        double middle = (low + high) * 0.5;
                        Coordinate probe = pointAtFraction(middle);
                        if (maps.Contains(probe, buffer)) { low = middle; safe = probe; }
                        else high = middle;
                    }
                    return safe;
                }
                lastSafe = fraction;
                safe = point;
            }
            return safe;
        }

        private static Coordinate KeepGroundInterpolationOnPavement(GroundMapSnapshot maps,
            Coordinate actual, Coordinate candidate, Coordinate target, double buffer)
        {
            // Smoothing can itself cut a corner, or retain an old off-pavement
            // estimate after a correction. In that case use the valid prediction.
            if (maps == null) return candidate;
            double distance = Conversions.CalculateDistance(actual, candidate) * 1852.0;
            Coordinate constrained = LimitGroundPath(maps, actual, buffer, distance,
                fraction => InterpolateCoordinate(actual, candidate, fraction));
            return Conversions.CalculateDistance(constrained, candidate) * 1852.0 > 0.25
                ? target : candidate;
        }

        private static void PaintGroundAreasPostfix(object __instance, object[] __args)
        {
            if (!showGroundAreas || groundOverlayFailed) return;
            try
            {
                if (groundDisplayType == null ||
                    !string.Equals(Convert.ToString(groundDisplayType.GetValue(__instance, null)),
                        "Ground", StringComparison.Ordinal)) return;
                GroundMapSnapshot snapshot = GetGroundMaps();
                if (snapshot == null) return;
                foreach (DisplayMaps.Map map in (IEnumerable<DisplayMaps.Map>)__args[2])
                {
                    DisplayMaps.Map outline;
                    if (!snapshot.Outlines.TryGetValue(map.Id, out outline)) continue;
                    computeGroundOverlay.Invoke(__instance, new object[] { __args[0], outline });
                    paintGroundOverlay.Invoke(__instance, new object[] { __args[0], __args[1], outline });
                }
            }
            catch (Exception ex)
            {
                groundOverlayFailed = true;
                Log("Ground outline preview unavailable.\r\n" + ex);
            }
        }

        private static void UpdateGroundMotionEstimate(
            RDP.RadarTrack radarTrack,
            SmoothingState state,
            double speed,
            PluginSettings settings)
        {
            if (!settings.GroundStopDetectionEnabled ||
                !radarTrack.OnGround ||
                radarTrack.Cancelled ||
                radarTrack.Coasting ||
                speed < 0)
            {
                state.HasGroundSpeedSample =
                    false;

                state.LastRawGroundSpeed =
                    double.NaN;

                state.LastGroundSpeedTimestamp =
                    DateTime.MinValue;

                state.SmoothedGroundDecelerationKnotsPerSecond =
                    0.0;

                return;
            }

            DateTime timestamp =
                radarTrack.Timestamp;

            if (timestamp ==
                DateTime.MinValue)
            {
                return;
            }

            // Only process each real network/radar report once.
            if (state.HasGroundSpeedSample &&
                timestamp ==
                    state.LastGroundSpeedTimestamp)
            {
                return;
            }

            if (!state.HasGroundSpeedSample ||
                double.IsNaN(
                    state.LastRawGroundSpeed))
            {
                state.HasGroundSpeedSample =
                    true;

                state.LastRawGroundSpeed =
                    Math.Max(
                        0.0,
                        speed
                    );

                state.LastGroundSpeedTimestamp =
                    timestamp;

                state.SmoothedGroundDecelerationKnotsPerSecond =
                    0.0;

                return;
            }

            double sampleSeconds =
                (timestamp -
                 state.LastGroundSpeedTimestamp)
                .TotalSeconds;

            if (sampleSeconds >= 0.5 &&
                sampleSeconds <= 20.0)
            {
                double rawAcceleration =
                    (
                        speed -
                        state.LastRawGroundSpeed
                    ) /
                    sampleSeconds;

                // We only use deceleration for predictive stopping.
                // Positive acceleration is deliberately ignored because
                // projecting acceleration aggressively can overshoot
                // taxiway turns and holding points.
                if (rawAcceleration < -0.03)
                {
                    rawAcceleration =
                        ClampDouble(
                            rawAcceleration,
                            -5.0,
                            0.0
                        );

                    double alpha =
                        1.0 -
                        Math.Exp(
                            -sampleSeconds /
                            2.0
                        );

                    state
                        .SmoothedGroundDecelerationKnotsPerSecond +=
                        (
                            rawAcceleration -
                            state
                                .SmoothedGroundDecelerationKnotsPerSecond
                        ) *
                        alpha;
                }
                else
                {
                    // Decay an old braking estimate quickly after the
                    // aircraft becomes steady or accelerates again.
                    state
                        .SmoothedGroundDecelerationKnotsPerSecond *=
                        Math.Exp(
                            -sampleSeconds /
                            1.0
                        );

                    if (Math.Abs(
                        state
                            .SmoothedGroundDecelerationKnotsPerSecond) <
                        0.03)
                    {
                        state
                            .SmoothedGroundDecelerationKnotsPerSecond =
                            0.0;
                    }
                }
            }
            else
            {
                state
                    .SmoothedGroundDecelerationKnotsPerSecond =
                    0.0;
            }

            if (speed <=
                settings.GroundStopSpeedKnots)
            {
                state
                    .SmoothedGroundDecelerationKnotsPerSecond =
                    0.0;
            }

            state.LastRawGroundSpeed =
                Math.Max(
                    0.0,
                    speed
                );

            state.LastGroundSpeedTimestamp =
                timestamp;
        }

        private static void ApplyGroundStopPrediction(
            SmoothingState state,
            PluginSettings settings,
            double reportedSpeed,
            double elapsedSeconds,
            out double predictionSpeed,
            out double predictionTime,
            out bool stopped)
        {
            predictionSpeed =
                Math.Max(
                    0.0,
                    reportedSpeed
                );

            predictionTime =
                Math.Max(
                    0.0,
                    elapsedSeconds
                );

            stopped =
                false;

            if (predictionSpeed <=
                settings.GroundStopSpeedKnots)
            {
                predictionSpeed =
                    0.0;

                predictionTime =
                    0.0;

                stopped =
                    true;

                return;
            }

            // Progressively reduce forward extrapolation as the
            // aircraft approaches the configured stopping range.
            if (predictionSpeed <
                settings.GroundPredictionFadeSpeedKnots)
            {
                double range =
                    settings.GroundPredictionFadeSpeedKnots -
                    settings.GroundStopSpeedKnots;

                double amount =
                    (
                        predictionSpeed -
                        settings.GroundStopSpeedKnots
                    ) /
                    range;

                amount =
                    ClampDouble(
                        amount,
                        0.0,
                        1.0
                    );

                // Smoothstep avoids a sharp change at either end.
                double fade =
                    amount *
                    amount *
                    (
                        3.0 -
                        2.0 *
                        amount
                    );

                predictionSpeed *=
                    fade;
            }

            double deceleration =
                state
                    .SmoothedGroundDecelerationKnotsPerSecond;

            if (deceleration < -0.03 &&
                predictionSpeed > 0.0 &&
                predictionTime > 0.0)
            {
                double stopTime =
                    predictionSpeed /
                    -deceleration;

                double movingTime =
                    Math.Min(
                        predictionTime,
                        stopTime
                    );

                // Convert constant-deceleration distance to an
                // equivalent average speed so PredictCurvedPosition()
                // can retain the same turn integration code.
                double endSpeed =
                    Math.Max(
                        0.0,
                        predictionSpeed +
                        deceleration *
                        movingTime
                    );

                double averageSpeed =
                    (
                        predictionSpeed +
                        endSpeed
                    ) *
                    0.5;

                predictionSpeed =
                    averageSpeed;

                predictionTime =
                    movingTime;

                if (stopTime <=
                    elapsedSeconds)
                {
                    stopped =
                        true;
                }
            }

            if (predictionSpeed <
                0.05 ||
                predictionTime <= 0.0)
            {
                predictionSpeed =
                    0.0;

                predictionTime =
                    0.0;

                stopped =
                    true;
            }
        }

        private static void UpdateTurnRateEstimate(
            RDP.RadarTrack radarTrack,
            SmoothingState state,
            double heading,
            double speed,
            PluginSettings settings)
        {
            if (!settings.TurnPredictionEnabled ||
                radarTrack.Cancelled ||
                radarTrack.Coasting ||
                heading < 0 ||
                speed < 2)
            {
                state.SmoothedTurnRateDegPerSecond =
                    0.0;

                return;
            }

            DateTime timestamp =
                radarTrack.Timestamp;

            if (timestamp ==
                DateTime.MinValue)
            {
                return;
            }

            // Do not recalculate from the same network report on every
            // render frame.
            if (state.HasHeadingSample &&
                timestamp ==
                    state.LastRawHeadingTimestamp)
            {
                return;
            }

            if (!state.HasHeadingSample ||
                double.IsNaN(
                    state.LastRawHeading))
            {
                state.HasHeadingSample =
                    true;

                state.LastRawHeading =
                    NormaliseHeading(
                        heading
                    );

                state.LastRawHeadingTimestamp =
                    timestamp;

                state.LastHeadingSampleIntervalSeconds =
                    0.0;

                state.SmoothedTurnRateDegPerSecond =
                    0.0;

                return;
            }

            double sampleSeconds =
                (timestamp -
                 state.LastRawHeadingTimestamp)
                .TotalSeconds;

            if (sampleSeconds >= 0.5 &&
                sampleSeconds <= 20.0)
            {
                double headingDelta =
                    NormaliseHeadingDelta(
                        heading -
                        state.LastRawHeading
                    );

                double rawTurnRate =
                    headingDelta /
                    sampleSeconds;

                double maximumTurnRate =
                    radarTrack.OnGround
                        ? settings
                            .MaxGroundTurnRateDegPerSecond
                        : settings
                            .MaxAirTurnRateDegPerSecond;

                rawTurnRate =
                    ClampDouble(
                        rawTurnRate,
                        -maximumTurnRate,
                        maximumTurnRate
                    );

                double alpha =
                    1.0 -
                    Math.Exp(
                        -sampleSeconds /
                        settings
                            .TurnRateSmoothingSeconds
                    );

                state.SmoothedTurnRateDegPerSecond +=
                    (
                        rawTurnRate -
                        state.SmoothedTurnRateDegPerSecond
                    ) *
                    alpha;

                // Remove tiny residual rates so straight tracks do not
                // slowly bend because of heading noise.
                if (Math.Abs(
                    state.SmoothedTurnRateDegPerSecond) <
                    0.03)
                {
                    state.SmoothedTurnRateDegPerSecond =
                        0.0;
                }

                state.LastHeadingSampleIntervalSeconds =
                    sampleSeconds;
            }
            else
            {
                // A large timing gap means the old heading sample is no
                // longer reliable enough to estimate a turn.
                state.SmoothedTurnRateDegPerSecond =
                    0.0;

                state.LastHeadingSampleIntervalSeconds =
                    0.0;
            }

            state.LastRawHeading =
                NormaliseHeading(
                    heading
                );

            state.LastRawHeadingTimestamp =
                timestamp;
        }

        private static Coordinate PredictCurvedPosition(
            Coordinate start,
            double speedKnots,
            double startHeading,
            double turnRateDegPerSecond,
            double elapsedSeconds)
        {
            if (start == null)
                return null;

            if (elapsedSeconds <= 0 ||
                speedKnots <= 0)
            {
                return CopyCoordinate(
                    start
                );
            }

            // A negligible turn is cheaper and more accurate as a
            // normal straight-line projection.
            if (Math.Abs(
                turnRateDegPerSecond) <
                0.01)
            {
                double distanceNm =
                    speedKnots *
                    elapsedSeconds /
                    3600.0;

                return Conversions
                    .CalculateLLFromBearingRange(
                        start,
                        distanceNm,
                        NormaliseHeading(
                            startHeading
                        )
                    );
            }

            // Integrate the curve in short segments. 0.25 seconds is
            // small enough to make the visual path effectively smooth
            // while the total segment count remains tiny.
            int segments =
                Math.Max(
                    1,
                    Math.Min(
                        32,
                        (int)Math.Ceiling(
                            elapsedSeconds /
                            0.25
                        )
                    )
                );

            double segmentSeconds =
                elapsedSeconds /
                segments;

            double segmentDistanceNm =
                speedKnots *
                segmentSeconds /
                3600.0;

            Coordinate position =
                CopyCoordinate(
                    start
                );

            for (int i = 0;
                 i < segments;
                 i++)
            {
                double midpointSeconds =
                    (i + 0.5) *
                    segmentSeconds;

                double bearing =
                    NormaliseHeading(
                        startHeading +
                        turnRateDegPerSecond *
                        midpointSeconds
                    );

                position =
                    Conversions
                        .CalculateLLFromBearingRange(
                            position,
                            segmentDistanceNm,
                            bearing
                        );
            }

            return position;
        }

        private static double NormaliseHeading(
            double heading)
        {
            heading %= 360.0;

            if (heading < 0)
                heading += 360.0;

            return heading;
        }

        private static double NormaliseHeadingDelta(
            double delta)
        {
            delta %= 360.0;

            if (delta > 180.0)
                delta -= 360.0;

            if (delta < -180.0)
                delta += 360.0;

            return delta;
        }

        private static Coordinate
            InterpolateCoordinate(
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
                (
                    to.Latitude -
                    from.Latitude
                ) *
                amount;

            double longitudeDifference =
                to.Longitude -
                from.Longitude;

            if (longitudeDifference >
                180.0)
            {
                longitudeDifference -=
                    360.0;
            }

            if (longitudeDifference <
                -180.0)
            {
                longitudeDifference +=
                    360.0;
            }

            double longitude =
                from.Longitude +
                longitudeDifference *
                amount;

            if (longitude > 180.0)
                longitude -= 360.0;

            if (longitude < -180.0)
                longitude += 360.0;

            return new Coordinate(
                latitude,
                longitude
            );
        }

        private static Coordinate CopyCoordinate(
            Coordinate source)
        {
            if (source == null)
                return null;

            return new Coordinate(
                source.Latitude,
                source.Longitude
            );
        }

        // ----------------------------------------------------------
        // SMOOTHED / ESTIMATED ALTITUDE
        // ----------------------------------------------------------

        public static int GetDisplayAltitude(
            RDP.RadarTrack radarTrack)
        {
            if (radarTrack == null)
                return -1;

            int actualAltitude =
                radarTrack.CorrectedAltitude;

            if (actualAltitude == -1)
                return -1;

            PluginSettings settings =
                currentSettings;

            if (settings == null ||
                !settings.Enabled ||
                !settings
                    .AltitudeSmoothingEnabled)
            {
                return actualAltitude;
            }

            DateTime now =
                DateTime.UtcNow;

            SmoothingState state =
                SmoothingStates
                    .GetOrCreateValue(
                        radarTrack
                    );

            lock (state)
            {
                if (radarTrack.OnGround ||
                    radarTrack.Coasting ||
                    radarTrack.Cancelled ||
                    radarTrack.VerticalSpeed ==
                        -9999.0)
                {
                    state.DisplayAltitude =
                        actualAltitude;

                    state.LastAltitudeRenderTime =
                        now;

                    return RoundAltitude(
                        actualAltitude,
                        settings.AltitudeStepFeet
                    );
                }

                if (
                    settings.VisualRefreshIntervalMs >
                    0 &&
                    state.LastAltitudeRenderTime !=
                        DateTime.MinValue &&
                    !double.IsNaN(
                        state.DisplayAltitude) &&
                    (now -
                     state.LastAltitudeRenderTime)
                        .TotalMilliseconds <
                    settings.VisualRefreshIntervalMs)
                {
                    return RoundAltitude(
                        state.DisplayAltitude,
                        settings.AltitudeStepFeet
                    );
                }

                double elapsed =
                    (now -
                     radarTrack.Timestamp)
                    .TotalSeconds;

                if (elapsed < 0)
                    elapsed = 0;

                if (elapsed >
                    settings.MaxPredictionSeconds)
                {
                    elapsed =
                        settings.MaxPredictionSeconds;
                }

                double predictedAltitude =
                    actualAltitude +
                    radarTrack.VerticalSpeed *
                    (elapsed / 60.0);

                if (double.IsNaN(
                        state.DisplayAltitude) ||
                    state.LastAltitudeRenderTime ==
                        DateTime.MinValue)
                {
                    state.DisplayAltitude =
                        predictedAltitude;

                    state.LastAltitudeRenderTime =
                        now;

                    return RoundAltitude(
                        state.DisplayAltitude,
                        settings.AltitudeStepFeet
                    );
                }

                double frameTime =
                    (now -
                     state.LastAltitudeRenderTime)
                    .TotalSeconds;

                if (frameTime > 0)
                {
                    if (frameTime > 0.5)
                        frameTime = 0.5;

                    double altitudeDifference =
                        Math.Abs(
                            predictedAltitude -
                            state.DisplayAltitude
                        );

                    if (altitudeDifference >
                        settings.AltitudeSnapFeet)
                    {
                        state.DisplayAltitude =
                            predictedAltitude;
                    }
                    else
                    {
                        double alpha =
                            1.0 -
                            Math.Exp(
                                -frameTime /
                                settings
                                    .AltitudeSmoothingSeconds
                            );

                        state.DisplayAltitude +=
                            (
                                predictedAltitude -
                                state.DisplayAltitude
                            ) *
                            alpha;
                    }

                    state.LastAltitudeRenderTime =
                        now;
                }

                return RoundAltitude(
                    state.DisplayAltitude,
                    settings.AltitudeStepFeet
                );
            }
        }

        private static int RoundAltitude(
            double altitude,
            int stepFeet)
        {
            if (stepFeet < 100)
                stepFeet = 100;

            return (int)(
                Math.Round(
                    altitude /
                    stepFeet,
                    MidpointRounding
                        .AwayFromZero
                ) *
                stepFeet
            );
        }

        // ----------------------------------------------------------
        // SMOOTHED VISUAL HISTORY
        // ----------------------------------------------------------

        private static void SampleVisualHistory(
            RDP.RadarTrack radarTrack,
            SmoothingState state,
            DateTime now,
            PluginSettings settings)
        {
            if (radarTrack == null ||
                state.DisplayPosition == null ||
                radarTrack.Cancelled)
            {
                return;
            }

            if (state.LastHistorySampleTime ==
                DateTime.MinValue)
            {
                state.LastHistorySampleTime =
                    now;

                return;
            }

            double elapsed =
                (now -
                 state.LastHistorySampleTime)
                .TotalSeconds;

            if (elapsed <
                settings.HistorySampleSeconds)
            {
                return;
            }

            Coordinate historyPosition =
                CopyCoordinate(
                    state.DisplayPosition
                );

            RDP.RadarTrackHistory history =
                new RDP.RadarTrackHistory(
                    historyPosition,
                    radarTrack.GroundSpeed,
                    radarTrack.Heading,
                    now,
                    false
                );

            state.VisualHistory.Enqueue(
                history
            );

            while (
                state.VisualHistory.Count >
                settings.VisualHistorySize)
            {
                state.VisualHistory.Dequeue();
            }

            state.LastHistorySampleTime =
                now;
        }

        public static ConcurrentQueue<
            RDP.RadarTrackHistory>
            GetDisplayHistory(
                RDP.RadarTrack radarTrack)
        {
            ConcurrentQueue<
                RDP.RadarTrackHistory>
                result =
                    new ConcurrentQueue<
                        RDP.RadarTrackHistory>();

            if (radarTrack == null)
                return result;

            PluginSettings settings =
                currentSettings;

            // When disabled, give vatSys its real history.
            if (settings == null ||
                !settings.Enabled)
            {
                foreach (
                    RDP.RadarTrackHistory history
                    in radarTrack.PositionHistory)
                {
                    result.Enqueue(
                        history
                    );
                }

                return result;
            }

            SmoothingState state =
                SmoothingStates
                    .GetOrCreateValue(
                        radarTrack
                    );

            lock (state)
            {
                foreach (
                    RDP.RadarTrackHistory history
                    in state.VisualHistory)
                {
                    result.Enqueue(
                        history
                    );
                }
            }

            return result;
        }

        // ----------------------------------------------------------
        // SETTINGS WINDOW
        // ----------------------------------------------------------

        private sealed class VatSysToggleButton : CheckBox
        {
            public VatSysToggleButton()
            {
                AutoSize = false;
                Height = 28;
                Font = MMI.eurofont_sml;
                TextAlign = ContentAlignment.MiddleCenter;
                BackColor =
                    Colours.GetColour(
                        Colours.Identities.WindowBackground
                    );
                ForeColor =
                    Colours.GetColour(
                        Colours.Identities.InteractiveText
                    );
                Cursor = Cursors.Hand;
            }

            protected override void OnCheckedChanged(
                EventArgs e)
            {
                base.OnCheckedChanged(e);
                Invalidate();
            }

            protected override void OnPaint(
                PaintEventArgs e)
            {
                Color backColour =
                    Checked
                        ? Colours.GetColour(
                            Colours.Identities.WindowButtonDepressed
                        )
                        : Colours.GetColour(
                            Colours.Identities.WindowBackground
                        );

                Color textColour =
                    Enabled
                        ? Colours.GetColour(
                            Colours.Identities.InteractiveText
                        )
                        : Colours.GetColour(
                            Colours.Identities.NonInteractiveText
                        );

                using (
                    SolidBrush background =
                        new SolidBrush(
                            backColour
                        )
                )
                {
                    e.Graphics.FillRectangle(
                        background,
                        ClientRectangle
                    );
                }

                using (
                    SolidBrush foreground =
                        new SolidBrush(
                            textColour
                        )
                )
                using (
                    StringFormat format =
                        new StringFormat()
                )
                {
                    format.Alignment =
                        StringAlignment.Center;

                    format.LineAlignment =
                        StringAlignment.Center;

                    e.Graphics.DrawString(
                        Text,
                        Font,
                        foreground,
                        ClientRectangle,
                        format
                    );
                }

                ControlPaint.DrawBorder3D(
                    e.Graphics,
                    ClientRectangle,
                    Checked
                        ? Border3DStyle.Sunken
                        : Border3DStyle.Raised
                );
            }
        }

        private sealed class SettingsForm : BaseForm
        {
            private readonly PictureBox bannerPictureBox;
            private readonly VatSysToggleButton enabledToggle;
            private readonly TextLabel presetStatusLabel;

            private readonly TextField refreshIntervalField;
            private readonly TextField predictionField;
            private readonly TextField airSmoothField;
            private readonly TextField groundSmoothField;
            private readonly TextField snapDistanceField;

            private readonly VatSysToggleButton groundMapToggle;
            private readonly TextField groundMapBufferField;
            private readonly VatSysToggleButton groundStopToggle;
            private readonly TextField groundStopSpeedField;
            private readonly TextField groundFadeSpeedField;

            private readonly VatSysToggleButton turnPredictionToggle;
            private readonly TextField turnRateSmoothField;
            private readonly TextField maxAirTurnRateField;
            private readonly TextField maxGroundTurnRateField;

            private readonly TextField historyIntervalField;
            private readonly TextField historyDotsField;
            private readonly TextField historyBufferField;

            private readonly VatSysToggleButton altitudeToggle;
            private readonly TextField altitudeSmoothField;
            private readonly TextField altitudeStepField;
            private readonly TextField altitudeSnapField;

            public PluginSettings ResultSettings
            {
                get;
                private set;
            }

            public SettingsForm(
                PluginSettings settings)
            {
                Text =
                    "Smooth Tracks Settings";

                BackColor =
                    Colours.GetColour(
                        Colours.Identities.WindowBackground
                    );

                ForeColor =
                    Colours.GetColour(
                        Colours.Identities.GenericText
                    );

                Font =
                    MMI.eurofont_winsml;

                StartPosition =
                    FormStartPosition.CenterScreen;

                FormBorderStyle =
                    FormBorderStyle.FixedToolWindow;

                Resizeable =
                    false;

                HasCloseButton =
                    true;

                HasMinimizeButton =
                    false;

                HasMaximizeButton =
                    false;

                HasSendBackButton =
                    false;

                MiddleClickClose =
                    false;

                ShowInTaskbar =
                    false;

                TopMost =
                    true;

                ClientSize =
                    new Size(
                        590,
                        900
                    );

                TableLayoutPanel layout =
                    new TableLayoutPanel();

                layout.Dock =
                    DockStyle.Fill;

                layout.AutoScroll =
                    true;

                layout.BackColor =
                    Colours.GetColour(
                        Colours.Identities.WindowBackground
                    );

                layout.Padding =
                    new Padding(
                        12
                    );

                layout.ColumnCount =
                    3;

                layout.ColumnStyles.Add(
                    new ColumnStyle(
                        SizeType.Percent,
                        62f
                    )
                );

                layout.ColumnStyles.Add(
                    new ColumnStyle(
                        SizeType.Absolute,
                        115f
                    )
                );

                layout.ColumnStyles.Add(
                    new ColumnStyle(
                        SizeType.Absolute,
                        75f
                    )
                );

                int row = 0;

                bannerPictureBox =
                    new PictureBox();

                bannerPictureBox.Image =
                    LoadBannerImage();

                bannerPictureBox.SizeMode =
                    PictureBoxSizeMode.Zoom;

                bannerPictureBox.BackColor =
                    Color.Black;

                bannerPictureBox.Height =
                    110;

                bannerPictureBox.MinimumSize =
                    new Size(
                        0,
                        110
                    );

                bannerPictureBox.Dock =
                    DockStyle.Fill;

                bannerPictureBox.Margin =
                    new Padding(
                        3,
                        0,
                        3,
                        12
                    );

                if (bannerPictureBox.Image != null)
                {
                    layout.Controls.Add(
                        bannerPictureBox,
                        0,
                        row
                    );

                    layout.SetColumnSpan(
                        bannerPictureBox,
                        3
                    );

                    row++;
                }

                TextLabel intro =
                    CreateVatSysLabel(
                        "Changes affect Smooth Tracks' visual prediction only. " +
                        "They do not change the VATSIM network's real position-report rate.",
                        interactive: false
                    );

                intro.MaximumSize =
                    new Size(
                        545,
                        0
                    );

                intro.Margin =
                    new Padding(
                        3,
                        4,
                        3,
                        12
                    );

                layout.Controls.Add(
                    intro,
                    0,
                    row
                );

                layout.SetColumnSpan(
                    intro,
                    3
                );

                row++;

                enabledToggle =
                    new VatSysToggleButton();

                enabledToggle.Text =
                    settings.Enabled
                        ? "SMOOTH TRACKS ENABLED"
                        : "SMOOTH TRACKS DISABLED";

                enabledToggle.Checked =
                    settings.Enabled;

                enabledToggle.Dock =
                    DockStyle.Fill;

                enabledToggle.Margin =
                    new Padding(
                        3,
                        0,
                        3,
                        10
                    );

                enabledToggle.CheckedChanged +=
                    ToggleButton_CheckedChanged;

                layout.Controls.Add(
                    enabledToggle,
                    0,
                    row
                );

                layout.SetColumnSpan(
                    enabledToggle,
                    3
                );

                row++;

                AddSection(
                    layout,
                    ref row,
                    "SMOOTHING PRESET"
                );

                FlowLayoutPanel presetButtons =
                    new FlowLayoutPanel();

                presetButtons.FlowDirection =
                    FlowDirection.LeftToRight;

                presetButtons.Dock =
                    DockStyle.Fill;

                presetButtons.AutoSize =
                    true;

                presetButtons.WrapContents =
                    false;

                presetButtons.BackColor =
                    Colours.GetColour(
                        Colours.Identities.WindowBackground
                    );

                presetButtons.Margin =
                    new Padding(
                        3,
                        0,
                        3,
                        4
                    );

                GenericButton realisticPresetButton =
                    CreateVatSysButton(
                        "REALISTIC"
                    );

                realisticPresetButton.Width =
                    110;

                realisticPresetButton.Click +=
                    delegate
                    {
                        ApplyPresetToControls(
                            SmoothingPreset.Realistic
                        );
                    };

                GenericButton balancedPresetButton =
                    CreateVatSysButton(
                        "BALANCED"
                    );

                balancedPresetButton.Width =
                    110;

                balancedPresetButton.Click +=
                    delegate
                    {
                        ApplyPresetToControls(
                            SmoothingPreset.Balanced
                        );
                    };

                GenericButton ultraSmoothPresetButton =
                    CreateVatSysButton(
                        "ULTRA SMOOTH"
                    );

                ultraSmoothPresetButton.Width =
                    125;

                ultraSmoothPresetButton.Click +=
                    delegate
                    {
                        ApplyPresetToControls(
                            SmoothingPreset.UltraSmooth
                        );
                    };

                presetButtons.Controls.Add(
                    realisticPresetButton
                );

                presetButtons.Controls.Add(
                    balancedPresetButton
                );

                presetButtons.Controls.Add(
                    ultraSmoothPresetButton
                );

                layout.Controls.Add(
                    presetButtons,
                    0,
                    row
                );

                layout.SetColumnSpan(
                    presetButtons,
                    3
                );

                row++;

                presetStatusLabel =
                    CreateVatSysLabel(
                        "CURRENT: CUSTOM",
                        interactive: true
                    );

                presetStatusLabel.Font =
                    MMI.eurofont_sml;

                presetStatusLabel.Margin =
                    new Padding(
                        3,
                        2,
                        3,
                        8
                    );

                layout.Controls.Add(
                    presetStatusLabel,
                    0,
                    row
                );

                layout.SetColumnSpan(
                    presetStatusLabel,
                    3
                );

                row++;

                AddHint(
                    layout,
                    ref row,
                    "Presets tune movement, ground stopping, turn prediction and altitude smoothing. " +
                    "History-trail settings are left unchanged."
                );

                AddSection(
                    layout,
                    ref row,
                    "TRACK MOVEMENT"
                );

                refreshIntervalField =
                    AddValueRow(
                        layout,
                        ref row,
                        "Visual refresh interval",
                        settings.VisualRefreshIntervalMs.ToString(
                            CultureInfo.InvariantCulture
                        ),
                        "ms"
                    );

                predictionField =
                    AddValueRow(
                        layout,
                        ref row,
                        "Maximum prediction time",
                        FormatDouble(
                            settings.MaxPredictionSeconds
                        ),
                        "SEC"
                    );

                airSmoothField =
                    AddValueRow(
                        layout,
                        ref row,
                        "Air smoothness",
                        FormatDouble(
                            settings.AirSmoothingSeconds
                        ),
                        "SEC"
                    );

                groundSmoothField =
                    AddValueRow(
                        layout,
                        ref row,
                        "Ground smoothness",
                        FormatDouble(
                            settings.GroundSmoothingSeconds
                        ),
                        "SEC"
                    );

                snapDistanceField =
                    AddValueRow(
                        layout,
                        ref row,
                        "Correction snap distance",
                        FormatDouble(
                            settings.SnapDistanceNm
                        ),
                        "NM"
                    );

                AddHint(
                    layout,
                    ref row,
                    "Lower smoothness follows the estimate more tightly. " +
                    "Higher values are softer but add visual lag. " +
                    "A refresh interval of 0 ms updates every render frame."
                );

                AddSection(
                    layout,
                    ref row,
                    "GROUND MOVEMENT"
                );

                groundMapToggle = new VatSysToggleButton();
                groundMapToggle.Checked = settings.GroundMapConstraintEnabled;
                groundMapToggle.Text = settings.GroundMapConstraintEnabled
                    ? "GROUND MAP BOUNDARIES ENABLED" : "GROUND MAP BOUNDARIES DISABLED";
                groundMapToggle.Dock = DockStyle.Fill;
                groundMapToggle.Margin = new Padding(3, 0, 3, 6);
                groundMapToggle.CheckedChanged += delegate
                {
                    groundMapToggle.Text = groundMapToggle.Checked
                        ? "GROUND MAP BOUNDARIES ENABLED" : "GROUND MAP BOUNDARIES DISABLED";
                };
                layout.Controls.Add(groundMapToggle, 0, row);
                layout.SetColumnSpan(groundMapToggle, 3);
                row++;
                groundMapBufferField = AddValueRow(layout, ref row, "Pavement edge margin",
                    FormatDouble(settings.GroundMapBufferMetres), "M");
                AddHint(layout, ref row,
                    "Uses loaded runway, taxiway and apron polygons, excluding buildings. " +
                    "Prediction stops at the edge; real reports remain authoritative. " +
                    "No matching pavement: normal smoothing. Edge margin: 0-50 metres. " +
                    "Tools > Smooth Tracks can show the source edges on ground displays. " +
                    "This applies to tracks vatSys classifies as on ground; take-off/landing rolls " +
                    "above its ground-speed threshold retain normal prediction.");

                groundStopToggle =
                    new VatSysToggleButton();

                groundStopToggle.Text =
                    settings.GroundStopDetectionEnabled
                        ? "GROUND STOP DETECTION ENABLED"
                        : "GROUND STOP DETECTION DISABLED";

                groundMapToggle.Checked = settings.GroundMapConstraintEnabled;
                groundMapBufferField.Text = FormatDouble(settings.GroundMapBufferMetres);

                groundStopToggle.Checked =
                    settings.GroundStopDetectionEnabled;

                groundStopToggle.Dock =
                    DockStyle.Fill;

                groundStopToggle.Margin =
                    new Padding(
                        3,
                        0,
                        3,
                        6
                    );

                groundStopToggle.CheckedChanged +=
                    ToggleButton_CheckedChanged;

                layout.Controls.Add(
                    groundStopToggle,
                    0,
                    row
                );

                layout.SetColumnSpan(
                    groundStopToggle,
                    3
                );

                row++;

                groundStopSpeedField =
                    AddValueRow(
                        layout,
                        ref row,
                        "Ground stop threshold",
                        FormatDouble(
                            settings.GroundStopSpeedKnots
                        ),
                        "KT"
                    );

                groundFadeSpeedField =
                    AddValueRow(
                        layout,
                        ref row,
                        "Low-speed prediction fade",
                        FormatDouble(
                            settings.GroundPredictionFadeSpeedKnots
                        ),
                        "KT"
                    );

                AddHint(
                    layout,
                    ref row,
                    "Below the fade speed, forward prediction is progressively reduced. " +
                    "At or below the stop threshold, Smooth Tracks stops extrapolating and " +
                    "settles onto the latest real position. Recent real deceleration is also " +
                    "used to predict an aircraft coming to a stop."
                );

                AddSection(
                    layout,
                    ref row,
                    "TURN PREDICTION"
                );

                turnPredictionToggle =
                    new VatSysToggleButton();

                turnPredictionToggle.Text =
                    settings.TurnPredictionEnabled
                        ? "CURVED TURN PREDICTION ENABLED"
                        : "CURVED TURN PREDICTION DISABLED";

                turnPredictionToggle.Checked =
                    settings.TurnPredictionEnabled;

                turnPredictionToggle.Dock =
                    DockStyle.Fill;

                turnPredictionToggle.Margin =
                    new Padding(
                        3,
                        0,
                        3,
                        6
                    );

                turnPredictionToggle.CheckedChanged +=
                    ToggleButton_CheckedChanged;

                layout.Controls.Add(
                    turnPredictionToggle,
                    0,
                    row
                );

                layout.SetColumnSpan(
                    turnPredictionToggle,
                    3
                );

                row++;

                turnRateSmoothField =
                    AddValueRow(
                        layout,
                        ref row,
                        "Turn-rate smoothing",
                        FormatDouble(
                            settings.TurnRateSmoothingSeconds
                        ),
                        "SEC"
                    );

                maxAirTurnRateField =
                    AddValueRow(
                        layout,
                        ref row,
                        "Maximum air turn rate",
                        FormatDouble(
                            settings.MaxAirTurnRateDegPerSecond
                        ),
                        "DEG/S"
                    );

                maxGroundTurnRateField =
                    AddValueRow(
                        layout,
                        ref row,
                        "Maximum ground turn rate",
                        FormatDouble(
                            settings.MaxGroundTurnRateDegPerSecond
                        ),
                        "DEG/S"
                    );

                AddHint(
                    layout,
                    ref row,
                    "Smooth Tracks estimates turn rate only from new real radar reports, " +
                    "then follows a curved predicted path between reports. " +
                    "The limits prevent noisy heading changes from creating unrealistic turns."
                );

                AddSection(
                    layout,
                    ref row,
                    "HISTORY TRAILS"
                );

                historyIntervalField =
                    AddValueRow(
                        layout,
                        ref row,
                        "History sample interval",
                        FormatDouble(
                            settings.HistorySampleSeconds
                        ),
                        "SEC"
                    );

                historyDotsField =
                    AddValueRow(
                        layout,
                        ref row,
                        "History dots displayed",
                        settings.HistoryDots.ToString(
                            CultureInfo.InvariantCulture
                        ),
                        "DOTS"
                    );

                historyBufferField =
                    AddValueRow(
                        layout,
                        ref row,
                        "History buffer size",
                        settings.VisualHistorySize.ToString(
                            CultureInfo.InvariantCulture
                        ),
                        "PTS"
                    );

                AddSection(
                    layout,
                    ref row,
                    "ALTITUDE"
                );

                altitudeToggle =
                    new VatSysToggleButton();

                altitudeToggle.Text =
                    settings.AltitudeSmoothingEnabled
                        ? "ALTITUDE SMOOTHING ENABLED"
                        : "ALTITUDE SMOOTHING DISABLED";

                altitudeToggle.Checked =
                    settings.AltitudeSmoothingEnabled;

                altitudeToggle.Dock =
                    DockStyle.Fill;

                altitudeToggle.Margin =
                    new Padding(
                        3,
                        0,
                        3,
                        6
                    );

                altitudeToggle.CheckedChanged +=
                    ToggleButton_CheckedChanged;

                layout.Controls.Add(
                    altitudeToggle,
                    0,
                    row
                );

                layout.SetColumnSpan(
                    altitudeToggle,
                    3
                );

                row++;

                altitudeSmoothField =
                    AddValueRow(
                        layout,
                        ref row,
                        "Altitude smoothness",
                        FormatDouble(
                            settings.AltitudeSmoothingSeconds
                        ),
                        "SEC"
                    );

                altitudeStepField =
                    AddValueRow(
                        layout,
                        ref row,
                        "Altitude display step",
                        settings.AltitudeStepFeet.ToString(
                            CultureInfo.InvariantCulture
                        ),
                        "FT"
                    );

                altitudeSnapField =
                    AddValueRow(
                        layout,
                        ref row,
                        "Altitude snap threshold",
                        FormatDouble(
                            settings.AltitudeSnapFeet
                        ),
                        "FT"
                    );

                FlowLayoutPanel buttons =
                    new FlowLayoutPanel();

                buttons.FlowDirection =
                    FlowDirection.RightToLeft;

                buttons.Dock =
                    DockStyle.Fill;

                buttons.AutoSize =
                    true;

                buttons.BackColor =
                    Colours.GetColour(
                        Colours.Identities.WindowBackground
                    );

                buttons.Margin =
                    new Padding(
                        3,
                        16,
                        3,
                        3
                    );

                GenericButton saveButton =
                    CreateVatSysButton(
                        "SAVE"
                    );

                saveButton.Click +=
                    SaveButton_Click;

                GenericButton cancelButton =
                    CreateVatSysButton(
                        "CANCEL"
                    );

                cancelButton.Click +=
                    delegate
                    {
                        DialogResult =
                            DialogResult.Cancel;

                        Close();
                    };

                GenericButton defaultsButton =
                    CreateVatSysButton(
                        "DEFAULTS"
                    );

                defaultsButton.Click +=
                    DefaultsButton_Click;

                buttons.Controls.Add(
                    saveButton
                );

                buttons.Controls.Add(
                    cancelButton
                );

                buttons.Controls.Add(
                    defaultsButton
                );

                layout.Controls.Add(
                    buttons,
                    0,
                    row
                );

                layout.SetColumnSpan(
                    buttons,
                    3
                );

                Controls.Add(
                    layout
                );

                AttachPresetTracking();
                UpdatePresetStatus();

                AcceptButton =
                    saveButton;
            }

            private void ToggleButton_CheckedChanged(
                object sender,
                EventArgs e)
            {
                enabledToggle.Text =
                    enabledToggle.Checked
                        ? "SMOOTH TRACKS ENABLED"
                        : "SMOOTH TRACKS DISABLED";

                groundStopToggle.Text =
                    groundStopToggle.Checked
                        ? "GROUND STOP DETECTION ENABLED"
                        : "GROUND STOP DETECTION DISABLED";

                turnPredictionToggle.Text =
                    turnPredictionToggle.Checked
                        ? "CURVED TURN PREDICTION ENABLED"
                        : "CURVED TURN PREDICTION DISABLED";

                altitudeToggle.Text =
                    altitudeToggle.Checked
                        ? "ALTITUDE SMOOTHING ENABLED"
                        : "ALTITUDE SMOOTHING DISABLED";

                UpdatePresetStatus();
            }

            private void ApplyPresetToControls(
                SmoothingPreset preset)
            {
                PluginSettings values =
                    CreateDefaultSettings();

                ApplySmoothingPreset(
                    values,
                    preset
                );

                refreshIntervalField.Text =
                    values.VisualRefreshIntervalMs.ToString(
                        CultureInfo.InvariantCulture
                    );

                predictionField.Text =
                    FormatDouble(
                        values.MaxPredictionSeconds
                    );

                airSmoothField.Text =
                    FormatDouble(
                        values.AirSmoothingSeconds
                    );

                groundSmoothField.Text =
                    FormatDouble(
                        values.GroundSmoothingSeconds
                    );

                snapDistanceField.Text =
                    FormatDouble(
                        values.SnapDistanceNm
                    );

                groundStopToggle.Checked =
                    values.GroundStopDetectionEnabled;

                groundStopSpeedField.Text =
                    FormatDouble(
                        values.GroundStopSpeedKnots
                    );

                groundFadeSpeedField.Text =
                    FormatDouble(
                        values.GroundPredictionFadeSpeedKnots
                    );

                turnPredictionToggle.Checked =
                    values.TurnPredictionEnabled;

                turnRateSmoothField.Text =
                    FormatDouble(
                        values.TurnRateSmoothingSeconds
                    );

                maxAirTurnRateField.Text =
                    FormatDouble(
                        values.MaxAirTurnRateDegPerSecond
                    );

                maxGroundTurnRateField.Text =
                    FormatDouble(
                        values.MaxGroundTurnRateDegPerSecond
                    );

                altitudeToggle.Checked =
                    values.AltitudeSmoothingEnabled;

                altitudeSmoothField.Text =
                    FormatDouble(
                        values.AltitudeSmoothingSeconds
                    );

                altitudeStepField.Text =
                    values.AltitudeStepFeet.ToString(
                        CultureInfo.InvariantCulture
                    );

                altitudeSnapField.Text =
                    FormatDouble(
                        values.AltitudeSnapFeet
                    );

                UpdatePresetStatus();
            }

            private void AttachPresetTracking()
            {
                TextField[] fields =
                {
                    refreshIntervalField,
                    predictionField,
                    airSmoothField,
                    groundSmoothField,
                    snapDistanceField,
                    groundStopSpeedField,
                    groundFadeSpeedField,
                    turnRateSmoothField,
                    maxAirTurnRateField,
                    maxGroundTurnRateField,
                    altitudeSmoothField,
                    altitudeStepField,
                    altitudeSnapField
                };

                foreach (
                    TextField field
                    in fields)
                {
                    field.TextChanged +=
                        PresetControlledValueChanged;
                }
            }

            private void PresetControlledValueChanged(
                object sender,
                EventArgs e)
            {
                UpdatePresetStatus();
            }

            private void UpdatePresetStatus()
            {
                if (presetStatusLabel == null ||
                    refreshIntervalField == null ||
                    predictionField == null ||
                    airSmoothField == null ||
                    groundSmoothField == null ||
                    snapDistanceField == null ||
                    groundStopToggle == null ||
                    groundStopSpeedField == null ||
                    groundFadeSpeedField == null ||
                    turnPredictionToggle == null ||
                    turnRateSmoothField == null ||
                    maxAirTurnRateField == null ||
                    maxGroundTurnRateField == null ||
                    altitudeToggle == null ||
                    altitudeSmoothField == null ||
                    altitudeStepField == null ||
                    altitudeSnapField == null)
                {
                    return;
                }

                PluginSettings values =
                    new PluginSettings();

                int refreshInterval = 0;
                double prediction = 0.0;
                double airSmoothness = 0.0;
                double groundSmoothness = 0.0;
                double snapDistance = 0.0;
                double groundStopSpeed = 0.0;
                double groundFadeSpeed = 0.0;
                double turnRateSmoothness = 0.0;
                double maxAirTurnRate = 0.0;
                double maxGroundTurnRate = 0.0;
                double altitudeSmoothness = 0.0;
                int altitudeStep = 0;
                double altitudeSnap = 0.0;

                bool valid =
                    int.TryParse(
                        refreshIntervalField.Text.Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out refreshInterval
                    ) &&
                    double.TryParse(
                        predictionField.Text.Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out prediction
                    ) &&
                    double.TryParse(
                        airSmoothField.Text.Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out airSmoothness
                    ) &&
                    double.TryParse(
                        groundSmoothField.Text.Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out groundSmoothness
                    ) &&
                    double.TryParse(
                        snapDistanceField.Text.Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out snapDistance
                    ) &&
                    double.TryParse(
                        groundStopSpeedField.Text.Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out groundStopSpeed
                    ) &&
                    double.TryParse(
                        groundFadeSpeedField.Text.Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out groundFadeSpeed
                    ) &&
                    double.TryParse(
                        turnRateSmoothField.Text.Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out turnRateSmoothness
                    ) &&
                    double.TryParse(
                        maxAirTurnRateField.Text.Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out maxAirTurnRate
                    ) &&
                    double.TryParse(
                        maxGroundTurnRateField.Text.Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out maxGroundTurnRate
                    ) &&
                    double.TryParse(
                        altitudeSmoothField.Text.Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out altitudeSmoothness
                    ) &&
                    int.TryParse(
                        altitudeStepField.Text.Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out altitudeStep
                    ) &&
                    double.TryParse(
                        altitudeSnapField.Text.Trim(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out altitudeSnap
                    );

                if (!valid)
                {
                    presetStatusLabel.Text =
                        "CURRENT: CUSTOM";

                    return;
                }

                values.VisualRefreshIntervalMs =
                    refreshInterval;

                values.MaxPredictionSeconds =
                    prediction;

                values.AirSmoothingSeconds =
                    airSmoothness;

                values.GroundSmoothingSeconds =
                    groundSmoothness;

                values.SnapDistanceNm =
                    snapDistance;

                values.GroundStopDetectionEnabled =
                    groundStopToggle.Checked;

                values.GroundStopSpeedKnots =
                    groundStopSpeed;

                values.GroundPredictionFadeSpeedKnots =
                    groundFadeSpeed;

                values.TurnPredictionEnabled =
                    turnPredictionToggle.Checked;

                values.TurnRateSmoothingSeconds =
                    turnRateSmoothness;

                values.MaxAirTurnRateDegPerSecond =
                    maxAirTurnRate;

                values.MaxGroundTurnRateDegPerSecond =
                    maxGroundTurnRate;

                values.AltitudeSmoothingEnabled =
                    altitudeToggle.Checked;

                values.AltitudeSmoothingSeconds =
                    altitudeSmoothness;

                values.AltitudeStepFeet =
                    altitudeStep;

                values.AltitudeSnapFeet =
                    altitudeSnap;

                SmoothingPreset preset =
                    DetectSmoothingPreset(
                        values
                    );

                presetStatusLabel.Text =
                    "CURRENT: " +
                    GetPresetDisplayName(
                        preset
                    ).ToUpperInvariant();
            }

            private static Image LoadBannerImage()
            {
                try
                {
                    string assemblyFolder =
                        Path.GetDirectoryName(
                            Assembly
                                .GetExecutingAssembly()
                                .Location
                        );

                    if (string.IsNullOrWhiteSpace(
                        assemblyFolder))
                    {
                        return null;
                    }

                    string[] candidateNames =
                    {
                        "SmoothTracksBanner.png",
                        "SmoothTracks - a vatSys Plugin.png"
                    };

                    foreach (
                        string candidateName
                        in candidateNames)
                    {
                        string imagePath =
                            Path.Combine(
                                assemblyFolder,
                                candidateName
                            );

                        if (!File.Exists(
                            imagePath))
                        {
                            continue;
                        }

                        using (
                            FileStream stream =
                                new FileStream(
                                    imagePath,
                                    FileMode.Open,
                                    FileAccess.Read,
                                    FileShare.ReadWrite
                                )
                        )
                        using (
                            Image image =
                                Image.FromStream(
                                    stream
                                )
                        )
                        {
                            return new Bitmap(
                                image
                            );
                        }
                    }
                }
                catch (Exception ex)
                {
                    SmoothTracksPlugin.Log(
                        "Could not load settings banner.\r\n" +
                        ex
                    );
                }

                return null;
            }

            private void SaveButton_Click(
                object sender,
                EventArgs e)
            {
                PluginSettings settings;

                string errorMessage;

                if (!TryReadControls(
                    out settings,
                    out errorMessage))
                {
                    ShowVatSysDialog(
                        "Smooth Tracks Settings",
                        errorMessage,
                        confirmation: false
                    );

                    return;
                }

                ResultSettings =
                    NormaliseSettings(
                        settings
                    );

                DialogResult =
                    DialogResult.OK;

                Close();
            }

            private void DefaultsButton_Click(
                object sender,
                EventArgs e)
            {
                PluginSettings defaults =
                    CreateDefaultSettings();

                LoadControls(
                    defaults
                );
            }

            private bool TryReadControls(
                out PluginSettings settings,
                out string errorMessage)
            {
                settings =
                    new PluginSettings();

                errorMessage =
                    "";

                int refreshInterval;

                if (!TryReadInt(
                    refreshIntervalField,
                    "Visual refresh interval",
                    0,
                    2000,
                    out refreshInterval,
                    out errorMessage))
                {
                    return false;
                }

                double prediction;

                if (!TryReadDouble(
                    predictionField,
                    "Maximum prediction time",
                    0.5,
                    15.0,
                    out prediction,
                    out errorMessage))
                {
                    return false;
                }

                double airSmoothness;

                if (!TryReadDouble(
                    airSmoothField,
                    "Air smoothness",
                    0.01,
                    2.0,
                    out airSmoothness,
                    out errorMessage))
                {
                    return false;
                }

                double groundSmoothness;

                if (!TryReadDouble(
                    groundSmoothField,
                    "Ground smoothness",
                    0.01,
                    2.0,
                    out groundSmoothness,
                    out errorMessage))
                {
                    return false;
                }

                double snapDistance;

                if (!TryReadDouble(
                    snapDistanceField,
                    "Correction snap distance",
                    0.1,
                    20.0,
                    out snapDistance,
                    out errorMessage))
                {
                    return false;
                }

                double groundStopSpeed;

                if (!TryReadDouble(
                    groundStopSpeedField,
                    "Ground stop threshold",
                    0.0,
                    10.0,
                    out groundStopSpeed,
                    out errorMessage))
                {
                    return false;
                }

                double groundFadeSpeed;

                if (!TryReadDouble(
                    groundFadeSpeedField,
                    "Low-speed prediction fade",
                    2.0,
                    40.0,
                    out groundFadeSpeed,
                    out errorMessage))
                {
                    return false;
                }

                if (groundFadeSpeed <=
                    groundStopSpeed)
                {
                    groundFadeSpeedField.ErrorMode(
                        true
                    );

                    errorMessage =
                        "Low-speed prediction fade must be greater than " +
                        "the ground stop threshold.";

                    groundFadeSpeedField.Focus();

                    return false;
                }

                double turnRateSmoothness;

                if (!TryReadDouble(
                    turnRateSmoothField,
                    "Turn-rate smoothing",
                    0.1,
                    15.0,
                    out turnRateSmoothness,
                    out errorMessage))
                {
                    return false;
                }

                double maxAirTurnRate;

                if (!TryReadDouble(
                    maxAirTurnRateField,
                    "Maximum air turn rate",
                    0.1,
                    6.0,
                    out maxAirTurnRate,
                    out errorMessage))
                {
                    return false;
                }

                double maxGroundTurnRate;

                if (!TryReadDouble(
                    maxGroundTurnRateField,
                    "Maximum ground turn rate",
                    1.0,
                    45.0,
                    out maxGroundTurnRate,
                    out errorMessage))
                {
                    return false;
                }

                double historyInterval;

                if (!TryReadDouble(
                    historyIntervalField,
                    "History sample interval",
                    0.5,
                    30.0,
                    out historyInterval,
                    out errorMessage))
                {
                    return false;
                }

                int historyDots;

                if (!TryReadInt(
                    historyDotsField,
                    "History dots displayed",
                    0,
                    30,
                    out historyDots,
                    out errorMessage))
                {
                    return false;
                }

                int historyBuffer;

                if (!TryReadInt(
                    historyBufferField,
                    "History buffer size",
                    1,
                    200,
                    out historyBuffer,
                    out errorMessage))
                {
                    return false;
                }

                double altitudeSmoothness;

                if (!TryReadDouble(
                    altitudeSmoothField,
                    "Altitude smoothness",
                    0.01,
                    2.0,
                    out altitudeSmoothness,
                    out errorMessage))
                {
                    return false;
                }

                int altitudeStep;

                if (!TryReadInt(
                    altitudeStepField,
                    "Altitude display step",
                    100,
                    1000,
                    out altitudeStep,
                    out errorMessage))
                {
                    return false;
                }

                double altitudeSnap;

                if (!TryReadDouble(
                    altitudeSnapField,
                    "Altitude snap threshold",
                    100.0,
                    10000.0,
                    out altitudeSnap,
                    out errorMessage))
                {
                    return false;
                }

                double groundMapBuffer;
                if (!TryReadDouble(groundMapBufferField, "Pavement edge margin", 0.0, 50.0,
                    out groundMapBuffer, out errorMessage)) return false;
                if (double.IsNaN(groundMapBuffer) || double.IsInfinity(groundMapBuffer))
                {
                    errorMessage = "Pavement edge margin must be a finite number from 0 to 50.";
                    groundMapBufferField.ErrorMode(true);
                    return false;
                }
                settings.GroundMapConstraintEnabled = groundMapToggle.Checked;
                settings.GroundMapBufferMetres = groundMapBuffer;

                settings.Enabled =
                    enabledToggle.Checked;

                settings.VisualRefreshIntervalMs =
                    refreshInterval;

                settings.MaxPredictionSeconds =
                    prediction;

                settings.AirSmoothingSeconds =
                    airSmoothness;

                settings.GroundSmoothingSeconds =
                    groundSmoothness;

                settings.SnapDistanceNm =
                    snapDistance;

                settings.GroundStopDetectionEnabled =
                    groundStopToggle.Checked;

                settings.GroundStopSpeedKnots =
                    groundStopSpeed;

                settings.GroundPredictionFadeSpeedKnots =
                    groundFadeSpeed;

                settings.TurnPredictionEnabled =
                    turnPredictionToggle.Checked;

                settings.TurnRateSmoothingSeconds =
                    turnRateSmoothness;

                settings.MaxAirTurnRateDegPerSecond =
                    maxAirTurnRate;

                settings.MaxGroundTurnRateDegPerSecond =
                    maxGroundTurnRate;

                settings.HistorySampleSeconds =
                    historyInterval;

                settings.HistoryDots =
                    historyDots;

                settings.VisualHistorySize =
                    historyBuffer;

                settings.AltitudeSmoothingEnabled =
                    altitudeToggle.Checked;

                settings.AltitudeSmoothingSeconds =
                    altitudeSmoothness;

                settings.AltitudeStepFeet =
                    altitudeStep;

                settings.AltitudeSnapFeet =
                    altitudeSnap;

                return true;
            }

            private static bool TryReadInt(
                TextField field,
                string label,
                int minimum,
                int maximum,
                out int value,
                out string errorMessage)
            {
                field.ErrorMode(
                    false
                );

                if (!int.TryParse(
                    field.Text.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value) ||
                    value < minimum ||
                    value > maximum)
                {
                    field.ErrorMode(
                        true
                    );

                    errorMessage =
                        label +
                        " must be between " +
                        minimum.ToString(
                            CultureInfo.InvariantCulture
                        ) +
                        " and " +
                        maximum.ToString(
                            CultureInfo.InvariantCulture
                        ) +
                        ".";

                    field.Focus();

                    return false;
                }

                errorMessage =
                    "";

                return true;
            }

            private static bool TryReadDouble(
                TextField field,
                string label,
                double minimum,
                double maximum,
                out double value,
                out string errorMessage)
            {
                field.ErrorMode(
                    false
                );

                if (!double.TryParse(
                    field.Text.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value) ||
                    value < minimum ||
                    value > maximum)
                {
                    field.ErrorMode(
                        true
                    );

                    errorMessage =
                        label +
                        " must be between " +
                        FormatDouble(
                            minimum
                        ) +
                        " and " +
                        FormatDouble(
                            maximum
                        ) +
                        ".";

                    field.Focus();

                    return false;
                }

                errorMessage =
                    "";

                return true;
            }

            public void LoadControls(
                PluginSettings settings)
            {
                settings =
                    NormaliseSettings(
                        settings
                    );

                enabledToggle.Checked =
                    settings.Enabled;

                refreshIntervalField.Text =
                    settings.VisualRefreshIntervalMs.ToString(
                        CultureInfo.InvariantCulture
                    );

                predictionField.Text =
                    FormatDouble(
                        settings.MaxPredictionSeconds
                    );

                airSmoothField.Text =
                    FormatDouble(
                        settings.AirSmoothingSeconds
                    );

                groundSmoothField.Text =
                    FormatDouble(
                        settings.GroundSmoothingSeconds
                    );

                snapDistanceField.Text =
                    FormatDouble(
                        settings.SnapDistanceNm
                    );

                groundMapToggle.Checked = settings.GroundMapConstraintEnabled;
                groundMapBufferField.Text = FormatDouble(settings.GroundMapBufferMetres);

                groundStopToggle.Checked =
                    settings.GroundStopDetectionEnabled;

                groundStopSpeedField.Text =
                    FormatDouble(
                        settings.GroundStopSpeedKnots
                    );

                groundFadeSpeedField.Text =
                    FormatDouble(
                        settings.GroundPredictionFadeSpeedKnots
                    );

                turnPredictionToggle.Checked =
                    settings.TurnPredictionEnabled;

                turnRateSmoothField.Text =
                    FormatDouble(
                        settings.TurnRateSmoothingSeconds
                    );

                maxAirTurnRateField.Text =
                    FormatDouble(
                        settings.MaxAirTurnRateDegPerSecond
                    );

                maxGroundTurnRateField.Text =
                    FormatDouble(
                        settings.MaxGroundTurnRateDegPerSecond
                    );

                historyIntervalField.Text =
                    FormatDouble(
                        settings.HistorySampleSeconds
                    );

                historyDotsField.Text =
                    settings.HistoryDots.ToString(
                        CultureInfo.InvariantCulture
                    );

                historyBufferField.Text =
                    settings.VisualHistorySize.ToString(
                        CultureInfo.InvariantCulture
                    );

                altitudeToggle.Checked =
                    settings.AltitudeSmoothingEnabled;

                altitudeSmoothField.Text =
                    FormatDouble(
                        settings.AltitudeSmoothingSeconds
                    );

                altitudeStepField.Text =
                    settings.AltitudeStepFeet.ToString(
                        CultureInfo.InvariantCulture
                    );

                altitudeSnapField.Text =
                    FormatDouble(
                        settings.AltitudeSnapFeet
                    );

                ToggleButton_CheckedChanged(
                    this,
                    EventArgs.Empty
                );

                ResetErrorModes();
            }

            private void ResetErrorModes()
            {
                TextField[] fields =
                {
                    groundMapBufferField,
                    refreshIntervalField,
                    predictionField,
                    airSmoothField,
                    groundSmoothField,
                    snapDistanceField,
                    groundStopSpeedField,
                    groundFadeSpeedField,
                    turnRateSmoothField,
                    maxAirTurnRateField,
                    maxGroundTurnRateField,
                    historyIntervalField,
                    historyDotsField,
                    historyBufferField,
                    altitudeSmoothField,
                    altitudeStepField,
                    altitudeSnapField
                };

                foreach (
                    TextField field
                    in fields)
                {
                    field.ErrorMode(
                        false
                    );
                }
            }

            private static TextLabel CreateVatSysLabel(
                string text,
                bool interactive)
            {
                TextLabel label =
                    new TextLabel(
                        interactive
                    );

                label.Text =
                    text;

                label.Font =
                    MMI.eurofont_winsml;

                label.TextAlign =
                    ContentAlignment.MiddleLeft;

                label.AutoSize =
                    true;

                label.BackColor =
                    Colours.GetColour(
                        Colours.Identities.WindowBackground
                    );

                return label;
            }

            private static GenericButton CreateVatSysButton(
                string text)
            {
                GenericButton button =
                    new GenericButton();

                button.Text =
                    text;

                button.Font =
                    MMI.eurofont_sml;

                button.Size =
                    new Size(
                        90,
                        28
                    );

                button.BackColor =
                    Colours.GetColour(
                        Colours.Identities.WindowBackground
                    );

                button.ForeColor =
                    Colours.GetColour(
                        Colours.Identities.InteractiveText
                    );

                button.Margin =
                    new Padding(
                        4,
                        0,
                        0,
                        0
                    );

                return button;
            }

            private static void AddSection(
                TableLayoutPanel layout,
                ref int row,
                string text)
            {
                TextLabel label =
                    CreateVatSysLabel(
                        text,
                        interactive: true
                    );

                label.Font =
                    MMI.eurofont_sml;

                label.Margin =
                    new Padding(
                        3,
                        10,
                        3,
                        6
                    );

                layout.Controls.Add(
                    label,
                    0,
                    row
                );

                layout.SetColumnSpan(
                    label,
                    3
                );

                row++;

                Panel separator =
                    new Panel();

                separator.Height =
                    2;

                separator.Dock =
                    DockStyle.Top;

                separator.BackColor =
                    Colours.GetColour(
                        Colours.Identities.WindowBorder
                    );

                separator.Margin =
                    new Padding(
                        3,
                        0,
                        3,
                        8
                    );

                layout.Controls.Add(
                    separator,
                    0,
                    row
                );

                layout.SetColumnSpan(
                    separator,
                    3
                );

                row++;
            }

            private static void AddHint(
                TableLayoutPanel layout,
                ref int row,
                string text)
            {
                TextLabel label =
                    CreateVatSysLabel(
                        text,
                        interactive: false
                    );

                label.Font =
                    MMI.eurofont_winverysml;

                label.MaximumSize =
                    new Size(
                        545,
                        0
                    );

                label.Margin =
                    new Padding(
                        3,
                        5,
                        3,
                        8
                    );

                layout.Controls.Add(
                    label,
                    0,
                    row
                );

                layout.SetColumnSpan(
                    label,
                    3
                );

                row++;
            }

            private static TextField AddValueRow(
                TableLayoutPanel layout,
                ref int row,
                string labelText,
                string value,
                string unit)
            {
                TextLabel label =
                    CreateVatSysLabel(
                        labelText,
                        interactive: false
                    );

                label.Anchor =
                    AnchorStyles.Left;

                TextField field =
                    new TextField();

                field.Text =
                    value;

                field.Font =
                    MMI.eurofont_winsml;

                field.TextAlign =
                    HorizontalAlignment.Center;

                field.BorderStyle =
                    BorderStyle.Fixed3D;

                field.Width =
                    105;

                field.Anchor =
                    AnchorStyles.Left;

                field.Margin =
                    new Padding(
                        3,
                        2,
                        3,
                        2
                    );

                TextLabel unitLabel =
                    CreateVatSysLabel(
                        unit,
                        interactive: true
                    );

                unitLabel.Anchor =
                    AnchorStyles.Left;

                layout.Controls.Add(
                    label,
                    0,
                    row
                );

                layout.Controls.Add(
                    field,
                    1,
                    row
                );

                layout.Controls.Add(
                    unitLabel,
                    2,
                    row
                );

                row++;

                return field;
            }
        }

        // ----------------------------------------------------------
        // vatSys PLUGIN INTERFACE
        // ----------------------------------------------------------

        public void OnFDRUpdate(
            FDP2.FDR updated)
        {
        }

        public void OnRadarTrackUpdate(
            RDP.RadarTrack updated)
        {
            // Deliberately unused.
            //
            // Smooth Tracks predicts during paint/render rather than
            // waiting for vatSys's normal radar update callback.
        }

        // ----------------------------------------------------------
        // DEBUG LOG
        // ----------------------------------------------------------

        private static void Log(
            string message)
        {
            try
            {
                string path =
                    Path.Combine(
                        Path.GetTempPath(),
                        "vatSys-SmoothTracks.log" 
                    );

                File.AppendAllText(
                    path,
                    DateTime.Now.ToString(
                        "HH:mm:ss.fff"
                    ) +
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
