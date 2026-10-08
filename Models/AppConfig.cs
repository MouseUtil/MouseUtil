using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseUtil.Models;

public sealed class AppConfig
{
    public double IntervalMinutes { get; set; } = 1;
    public double IntervalSeconds { get; set; } = 0;
    public string Theme { get; set; } = "System";

    // "Pause on movement" has no separate master on/off field - it's considered on for a mode purely
    // by that mode's own flag here (see SettingsPanel.IsPauseOnMovementActiveForMode). Settings' master
    // ToggleSwitch is a pure display of "either is on", not independently persisted state - see
    // SettingsPanel.PauseOnMovementToggle_Toggled's own comment.
    public bool PauseOnMovementForAutoClick { get; set; } = false;
    public bool PauseOnMovementForJiggle { get; set; } = true;

    public string LastMode { get; set; } = "Click";

    // Which mode PowerToggleButton uses while ShowStopButtonDisplay is on: "Counter" shows a running
    // click/jiggle count, "Countdown" (default) shows time remaining until the next action.
    public string StopButtonDisplayMode { get; set; } = "Countdown";

    // Default ON: whether PowerToggleButton shows anything beyond plain "Stop" while running. When
    // off, the count/countdown moves to the status bar instead. Gates StopButtonDisplayMode above.
    public bool ShowStopButtonDisplay { get; set; } = true;

    // Global Start/Stop hotkey, persisted as the raw RegisterHotKey modifier bitmask/virtual-key pair
    // so re-registering at startup never needs to parse a display string. Defaults to bare F6.
    public uint HotkeyModifiers { get; set; } = 0;
    public uint HotkeyKey { get; set; } = 0x75; // VK_F6

    // Which Auto Stop mode (if any) is configured: "None", "Count" (stop after AutoStopCount),
    // "DateTime" (stop at a specific date+time), "Duration" (stop after AutoStopDuration*), or
    // "Time" (stop daily at AutoStopTimeHour:Minute). The specific date+time value itself is
    // session-only and not persisted, unlike the other mode-specific values below.
    public string AutoStopMode { get; set; } = "None";
    public int AutoStopCount { get; set; } = 100;

    // Hours/Minutes/Seconds of the "After a period of time" Auto Stop duration (AutoStopMode.Duration),
    // stored as three ints since System.Text.Json has no built-in TimeSpan converter. Resolved into
    // an absolute stop time fresh each time a run starts. Defaults to 8 hours.
    public int AutoStopDurationHours { get; set; } = 8;
    public int AutoStopDurationMinutes { get; set; } = 0;
    public int AutoStopDurationSeconds { get; set; } = 0;

    // User-customizable quick-preset values for AutoStopDialog's three count preset ToggleButtons -
    // always exactly 3 slots, index-aligned with the buttons' order. Defaults to 30/100/500.
    // CompactIntArrayJsonConverter (below) keeps each array on one config.json line.
    [JsonConverter(typeof(CompactIntArrayJsonConverter))]
    public int[] AutoStopCountPresets { get; set; } = { 30, 100, 500 };

    // User-customizable quick-preset values for AutoStopDialog's three duration preset ToggleButtons -
    // three parallel 3-element arrays (Hours/Minutes/Seconds), index-aligned with each other and the
    // buttons. A slot can never be saved as a fully zero 0h0m0s trio. Defaults to 30m/4h/24h.
    [JsonConverter(typeof(CompactIntArrayJsonConverter))]
    public int[] AutoStopDurationPresetHours { get; set; } = { 0, 4, 24 };
    [JsonConverter(typeof(CompactIntArrayJsonConverter))]
    public int[] AutoStopDurationPresetMinutes { get; set; } = { 30, 0, 0 };
    [JsonConverter(typeof(CompactIntArrayJsonConverter))]
    public int[] AutoStopDurationPresetSeconds { get; set; } = { 0, 0, 0 };

    // Hour/Minute of day for the recurring "Time" Auto Stop mode, resolved into the next occurrence
    // (today or tomorrow) each time a run starts. Also doubles as the remembered time-of-day for
    // AutoStopMode.DateTime, since both modes share one TimePicker in AutoStopDialog. Defaults to 17:00.
    public int AutoStopTimeHour { get; set; } = 17;
    public int AutoStopTimeMinute { get; set; } = 0;

    // Default OFF: when true, closing the main window hides it to the system tray instead of exiting.
    // The app keeps running; the tray icon's "Exit" command is the only way to terminate it then.
    public bool CloseToTray { get; set; } = false;

    // Default OFF: mirrors the running interval countdown as a progress bar on the taskbar icon
    // (via ITaskbarList3). Turns amber/yellow automatically while paused.
    public bool ShowTaskbarProgress { get; set; } = false;

    // Default OFF: whether Advanced interval display mode (Hours/Minutes/Seconds/Milliseconds fields,
    // replacing plain Minutes/Seconds) is active. Persisted; restored into the UI at startup. Owned
    // and edited exclusively by SettingsPanel's "Interval display" dropdown.
    public bool ShowAdvancedIntervalDisplay { get; set; } = false;

    // Which SystemBackdrop material the main window renders: "Mica" (default), "MicaAlt", or "Acrylic".
    // Persisted like Theme above, and read at startup by MainWindow.ApplyBackdrop - see SettingsPanel's
    // "Backdrop" expander.
    public string Backdrop { get; set; } = "Mica";

    // Default OFF: whether automation should start immediately on every app launch, whether from
    // Windows startup or a manual launch. See SettingsPanel's "App launch behavior" expander.
    public bool RunAutomationOnLaunch { get; set; } = false;

    // Which mode is selected at launch: "LastUsed" (default, restores LastMode) or a forced
    // "Click"/"Jiggle". Independent of RunAutomationOnLaunch - controls which mode the UI shows.
    public string PreferredMode { get; set; } = "LastUsed";

    // How the main window appears at launch: "Normal" (default, shown normally), "Minimized"
    // (shown in the taskbar but starts minimized), or "Tray" (starts hidden, tray icon only -
    // only reachable while CloseToTray is on). See SettingsPanel's "Launch window" dropdown.
    public string LaunchWindowMode { get; set; } = "Normal";

    // Default OFF: whether the Interval card's "Randomize interval" toggle is currently on - persisted
    // as live toggle state and restored at startup.
    public bool RandomizeInterval { get; set; } = false;

    // User-customizable quick-preset values for the Interval card's presets flyout. Four parallel,
    // index-aligned arrays (Hours/Minutes/Seconds/Milliseconds), variable-length up to
    // MainWindow.MaxIntervalPresets (6). Defaults to 3 presets: 100ms, 15s, 1m.
    [JsonConverter(typeof(CompactIntArrayJsonConverter))]
    public int[] IntervalPresetHours { get; set; } = { 0, 0, 0 };
    [JsonConverter(typeof(CompactIntArrayJsonConverter))]
    public int[] IntervalPresetMinutes { get; set; } = { 0, 0, 1 };
    [JsonConverter(typeof(CompactIntArrayJsonConverter))]
    public int[] IntervalPresetSeconds { get; set; } = { 0, 15, 0 };
    [JsonConverter(typeof(CompactIntArrayJsonConverter))]
    public int[] IntervalPresetMilliseconds { get; set; } = { 100, 0, 0 };
}

/// <summary>
/// Writes an int[] as a single-line "[1, 2, 3]" JSON array via Utf8JsonWriter.WriteRawValue, instead of
/// ConfigService's WriteIndented spreading each element onto its own line. The read side walks tokens
/// directly rather than delegating to JsonSerializer.Deserialize&lt;int[]&gt;, which would recurse back
/// into this same converter.
/// </summary>
internal sealed class CompactIntArrayJsonConverter : JsonConverter<int[]>
{
    public override int[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected a JSON array, got {reader.TokenType}.");
        }

        var values = new List<int>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(reader.GetInt32());
        }

        return values.ToArray();
    }

    public override void Write(Utf8JsonWriter writer, int[] value, JsonSerializerOptions options)
    {
        writer.WriteRawValue($"[{string.Join(", ", value)}]", skipInputValidation: true);
    }
}
