using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using MouseUtil.Interop;
using MouseUtil.Services;
using Windows.System;
using Windows.UI.Core;

namespace MouseUtil.Controls;

/// <summary>
/// The settings rows shown in MainWindow's Settings overlay (see MainWindow.xaml's SettingsOverlay
/// and ShowSettingsOverlay/SettingsBackButton_Click) - a single instance with exactly one
/// AutomationId per control, hosted in exactly one place for its whole lifetime.
///
/// Everything window/system-level stays in MainWindow (GlobalHotkeyService's actual RegisterHotKey
/// calls, RootGrid.RequestedTheme, TrayIconService, TaskbarProgressService) - this control only owns
/// the rows' own state/persistence and exposes events/delegates for the handful of things that still
/// need to reach MainWindow. See each event's doc comment below for which MainWindow-side effect it
/// drives.
/// </summary>
public sealed partial class SettingsPanel : UserControl
{
    // Matches UpdateButtonIcon's XAML-declared default exactly, so ApplyPendingUpdate(null) can
    // restore it explicitly (Glyph has no ambient/inherited fallback the way Style does).
    private const string UpdateButtonCheckGlyph = "\uE72C";
    private const string UpdateButtonAvailableGlyph = "\uE896";

    /// <summary>Raised (guarded by _isInitializing) whenever the Theme selection changes, so MainWindow can run ApplyTheme.</summary>
    public event EventHandler<string>? ThemeSelectionChanged;

    /// <summary>Raised whenever "Keep running in system tray" changes, so MainWindow can refresh its own _closeToTray mirror and UpdateTrayIconVisibility.</summary>
    public event EventHandler? CloseToTrayChanged;

    /// <summary>Raised whenever "Show countdown progress on taskbar icon" changes, so MainWindow can refresh its own _showTaskbarProgress mirror and clear the taskbar progress bar if it was just turned off.</summary>
    public event EventHandler? ShowTaskbarProgressChanged;

    /// <summary>Raised whenever "Pause spinning on movement" changes (by the user or via TogglePauseOnMovement), so MainWindow can call ResetStatusToOffIfNotRunning.</summary>
    public event EventHandler? PauseOnMovementChanged;

    /// <summary>Raised whenever "Show click/spin counter" changes, so MainWindow can refresh PowerToggleButton's live display.</summary>
    public event EventHandler? ShowActionCounterChanged;

    /// <summary>Raised whenever "Display advanced interval" changes, so MainWindow can swap BasicIntervalRow/AdvancedIntervalRow to match (see MainWindow.UpdateAdvancedIntervalDisplayMode).</summary>
    public event EventHandler? ShowAdvancedIntervalDisplayChanged;

    /// <summary>Raised once an update has been downloaded and the installer launched, so MainWindow can close itself via its own _isClosingConfirmed/Close() pattern.</summary>
    public event EventHandler? CloseAppRequested;

    /// <summary>
    /// MainWindow supplies this so hotkey recording can still go through its GlobalHotkeyService
    /// (the actual RegisterHotKey call requires the WndProc subclass installed on the window itself,
    /// which can't move into this UserControl). Returns whether registration succeeded, exactly like
    /// GlobalHotkeyService.TryRegister.
    /// </summary>
    public Func<uint, uint, bool>? TryRegisterHotkey { get; set; }

    /// <summary>
    /// MainWindow supplies this alongside TryRegisterHotkey so recording can unregister the current
    /// hotkey for the duration of the capture - see HotkeyButton_Click's doc comment for why.
    /// </summary>
    public Action? UnregisterHotkey { get; set; }

    /// <summary>
    /// MainWindow supplies its own single UpdateService instance here, rather than this control
    /// creating a private one - shared with MainWindow's own launch-time auto-check (see
    /// MainWindow.InitializeAutoUpdateCheck) so both surfaces reuse the same HttpClient instead of
    /// each hitting GitHub Releases separately. The two deliberately do NOT share UI state, though:
    /// UpdateButton below always starts idle regardless of what MainWindow's auto-check found - only
    /// MainWindow's own accent-colored icon reacts to that result. Always set by MainWindow right
    /// after construction (see InitializeSettingsPanel), so the null-forgiving operator at each call
    /// site below is safe.
    /// </summary>
    public UpdateService? UpdateChecker { get; set; }

    public bool PauseOnMovement => PauseOnMovementToggle.IsOn;
    public bool ShowActionCounter => ShowActionCounterToggle.IsOn;
    public bool CloseToTray => CloseToTrayToggle.IsOn;
    public bool ShowTaskbarProgress => ShowTaskbarProgressToggle.IsOn;
    public bool ShowAdvancedIntervalDisplay => ShowAdvancedIntervalDisplayToggle.IsOn;

    private bool _isInitializing;
    private uint _hotkeyModifiers;
    private uint _hotkeyKey;
    private bool _isRecordingHotkey;

    private UpdateCheckResult? _pendingUpdate;

    // Delayed-dismiss grace period for a found-but-not-installed update - see HandleHostClosing. Null
    // whenever nothing is pending or no grace period is running; started once (never restarted) by
    // the first HandleHostClosing call after a check finds an update, so reopening/reclosing Settings
    // during the 15 minutes doesn't push the deadline back. Purely in-memory (like _pendingUpdate
    // itself), so quitting the app clears it with no extra code needed.
    private DispatcherTimer? _pendingUpdateDismissTimer;

    public SettingsPanel()
    {
        InitializeComponent();

        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        _isInitializing = true;

        var config = ConfigService.Load();

        PauseOnMovementToggle.IsOn = config.PauseOnMovement;
        ShowActionCounterToggle.IsOn = config.ShowActionCounter;
        CloseToTrayToggle.IsOn = config.CloseToTray;
        ShowTaskbarProgressToggle.IsOn = config.ShowTaskbarProgress;
        AutoCheckForUpdatesToggle.IsOn = config.AutoCheckForUpdates;
        ShowAdvancedIntervalDisplayToggle.IsOn = config.ShowAdvancedIntervalDisplay;

        _hotkeyModifiers = config.HotkeyModifiers;
        _hotkeyKey = config.HotkeyKey;
        HotkeyButtonLabel.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);

        ThemeComboBox.SelectedItem = config.Theme switch
        {
            "Light" => ThemeLightItem,
            "Dark" => ThemeDarkItem,
            _ => ThemeSystemItem
        };

        _isInitializing = false;
    }

    /// <summary>
    /// Called by MainWindow's SettingsBackButton_Click when the Settings overlay closes - cancels
    /// any in-progress hotkey capture (otherwise _isRecordingHotkey stays stuck true, permanently
    /// no-opping HotkeyButton_Click) and, since recording leaves the previous hotkey unregistered
    /// (see HotkeyButton_Click), re-registers it so closing Settings mid-capture never leaves the app
    /// with no hotkey active.
    ///
    /// For the update button: if nothing is pending (never checked, or the last check came back
    /// clean), reset immediately - there's nothing to grace-period, and this also clears a lingering
    /// transient "You're up to date"/error message. If a real update WAS found, don't clear it right
    /// away - start (or, if already running, just leave alone) a 15-minute dismiss grace period via
    /// StartPendingUpdateDismissTimerIfNeeded, so a user who glances at "Update to vX.Y.Z" and closes
    /// Settings still has a window to reopen and act on it before it quietly resets to idle.
    /// </summary>
    public void HandleHostClosing()
    {
        if (_isRecordingHotkey)
        {
            _isRecordingHotkey = false;
            HotkeyButtonLabel.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);
            TryRegisterHotkey?.Invoke(_hotkeyModifiers, _hotkeyKey);
        }

        if (_pendingUpdate is null)
        {
            SetUpdateButtonLabel("Check for updates");
            return;
        }

        StartPendingUpdateDismissTimerIfNeeded();
    }

    /// <summary>
    /// Starts the 15-minute pending-update dismiss grace period, but only if one isn't already
    /// running - deliberately a no-op on every subsequent call while _pendingUpdateDismissTimer is
    /// non-null, which is what makes reopening/reclosing Settings NOT push the 15-minute deadline
    /// back (the user's explicit requirement). Once it fires, it clears _pendingUpdate and resets
    /// UpdateButton to idle regardless of whether Settings is open or closed at that moment - this is
    /// a real wall-clock DispatcherTimer, not something tied to Settings' visibility.
    /// </summary>
    private void StartPendingUpdateDismissTimerIfNeeded()
    {
        if (_pendingUpdateDismissTimer is not null)
        {
            return;
        }

        _pendingUpdateDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _pendingUpdateDismissTimer.Tick += (_, _) =>
        {
            _pendingUpdateDismissTimer!.Stop();
            _pendingUpdateDismissTimer = null;
            ApplyPendingUpdate(null);
            SetUpdateButtonLabel("Check for updates");
        };
        _pendingUpdateDismissTimer.Start();
    }

    /// <summary>Flips PauseOnMovementToggle - used by MainWindow's tray "Pause spinning on movement" context menu item so PauseOnMovementToggle_Toggled remains the single place that persists/raises the change.</summary>
    public void TogglePauseOnMovement() => PauseOnMovementToggle.IsOn = !PauseOnMovementToggle.IsOn;

    /// <summary>
    /// Same set of controls MainWindow.SetInputsEnabled used to poke directly before these rows
    /// moved into SettingsPanel.
    /// </summary>
    public void SetInputsEnabled(bool enabled)
    {
        HotkeyButton.IsEnabled = enabled;
        ShowActionCounterToggle.IsEnabled = enabled;
        PauseOnMovementToggle.IsEnabled = enabled;
        ShowAdvancedIntervalDisplayToggle.IsEnabled = enabled;

        HotkeyCaptionTextBlock.IsEnabled = enabled;
        ShowActionCounterCaption.IsEnabled = enabled;
        PauseOnMovementCaption.IsEnabled = enabled;
        ShowAdvancedIntervalDisplayCaption.IsEnabled = enabled;
    }

    private void ShowActionCounterToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ShowActionCounterChanged?.Invoke(this, EventArgs.Empty);
        ConfigService.Update(c => c.ShowActionCounter = ShowActionCounterToggle.IsOn);
    }

    private void ShowAdvancedIntervalDisplayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ShowAdvancedIntervalDisplayChanged?.Invoke(this, EventArgs.Empty);
        ConfigService.Update(c => c.ShowAdvancedIntervalDisplay = ShowAdvancedIntervalDisplayToggle.IsOn);
    }

    private void PauseOnMovementToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        PauseOnMovementChanged?.Invoke(this, EventArgs.Empty);
        ConfigService.Update(c => c.PauseOnMovement = PauseOnMovementToggle.IsOn);
    }

    private void CloseToTrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ConfigService.Update(c => c.CloseToTray = CloseToTrayToggle.IsOn);
        CloseToTrayChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowTaskbarProgressToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ConfigService.Update(c => c.ShowTaskbarProgress = ShowTaskbarProgressToggle.IsOn);
        ShowTaskbarProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    // No MainWindow-facing event needed here (unlike the toggles above) - this setting is only ever
    // read once, at the next launch (see MainWindow.InitializeAutoUpdateCheck), not reacted to live.
    private void AutoCheckForUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ConfigService.Update(c => c.AutoCheckForUpdates = AutoCheckForUpdatesToggle.IsOn);
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (ThemeComboBox.SelectedItem is ComboBoxItem { Tag: string theme })
        {
            ThemeSelectionChanged?.Invoke(this, theme);
            ConfigService.Update(c => c.Theme = theme);
        }
    }

    /// <summary>
    /// Enters hotkey-recording mode: the next key HotkeyButton_KeyDown sees (that isn't itself a bare
    /// modifier) becomes the new global hotkey. Ignored while already recording, so a second click
    /// mid-capture can't start a redundant/overlapping capture.
    ///
    /// Also unregisters the current hotkey for the duration of the capture. Otherwise, if the user
    /// opens the recorder and then presses the *current* hotkey (e.g. out of habit, or because they
    /// didn't mean to open the recorder and don't know Escape cancels it), RegisterHotKey would
    /// intercept that keypress at the OS level as WM_HOTKEY instead of delivering it to this button -
    /// silently triggering Start/Stop while leaving the recorder stuck on "Press a key
    /// combination…" forever, since HotkeyButton_KeyDown never sees the key at all.
    /// </summary>
    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            return;
        }

        _isRecordingHotkey = true;
        UnregisterHotkey?.Invoke();
        HotkeyErrorTextBlock.Visibility = Visibility.Collapsed;
        HotkeyButtonLabel.Text = "Press a key combination…";
    }

    /// <summary>
    /// Captures the next key while recording: Escape cancels (reverts the label and re-registers the
    /// previous hotkey - HotkeyButton_Click unregistered it for the capture - with no save); a bare
    /// modifier key is ignored so recording keeps waiting for the actual key; any other key finalizes
    /// the combination together with whatever modifiers are currently held.
    ///
    /// On success: registers immediately via TryRegisterHotkey (which unregisters the old one
    /// first), persists it, and updates the label. On failure (already claimed by another app): rolls
    /// back to the previous hotkey so the app is never left with nothing registered, and shows an
    /// inline error instead of persisting.
    /// </summary>
    private void HotkeyButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecordingHotkey)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            _isRecordingHotkey = false;
            HotkeyButtonLabel.Text = FormatHotkey(_hotkeyModifiers, _hotkeyKey);
            TryRegisterHotkey?.Invoke(_hotkeyModifiers, _hotkeyKey);
            return;
        }

        if (IsModifierKey(e.Key))
        {
            return;
        }

        uint modifiers = 0;
        if (IsKeyDown(VirtualKey.Control))
        {
            modifiers |= NativeMethods.MOD_CONTROL;
        }

        if (IsKeyDown(VirtualKey.Menu))
        {
            modifiers |= NativeMethods.MOD_ALT;
        }

        if (IsKeyDown(VirtualKey.Shift))
        {
            modifiers |= NativeMethods.MOD_SHIFT;
        }

        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
        {
            modifiers |= NativeMethods.MOD_WIN;
        }

        var virtualKey = (uint)e.Key;

        _isRecordingHotkey = false;

        var previousModifiers = _hotkeyModifiers;
        var previousKey = _hotkeyKey;

        if (TryRegisterHotkey?.Invoke(modifiers, virtualKey) == true)
        {
            _hotkeyModifiers = modifiers;
            _hotkeyKey = virtualKey;
            HotkeyButtonLabel.Text = FormatHotkey(modifiers, virtualKey);
            HotkeyErrorTextBlock.Visibility = Visibility.Collapsed;
            ConfigService.Update(c =>
            {
                c.HotkeyModifiers = modifiers;
                c.HotkeyKey = virtualKey;
            });
        }
        else
        {
            TryRegisterHotkey?.Invoke(previousModifiers, previousKey);
            HotkeyButtonLabel.Text = FormatHotkey(previousModifiers, previousKey);
            HotkeyErrorTextBlock.Text = "That shortcut is already in use by another app.";
            HotkeyErrorTextBlock.Visibility = Visibility.Visible;
        }
    }

    private static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// Formats a modifiers bitmask + virtual-key code as a display string like "F6" or
    /// "Ctrl+Shift+F6". VirtualKey's own ToString() already reads correctly for the keys realistic
    /// hotkeys use (letters, digits, function keys), so no separate name table is needed.
    /// </summary>
    private static string FormatHotkey(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & NativeMethods.MOD_ALT) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & NativeMethods.MOD_SHIFT) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & NativeMethods.MOD_WIN) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(((VirtualKey)virtualKey).ToString());
        return string.Join("+", parts);
    }

    /// <summary>
    /// Single place that reflects "is there a known pending update" onto UpdateButtonIcon/
    /// UpdateButton's visuals and _pendingUpdate itself. Deliberately does NOT touch
    /// UpdateButtonLabel.Text - callers still call SetUpdateButtonLabel themselves. Purely in-memory -
    /// nothing here is ever persisted, so an update check result never survives past Settings closing
    /// (see HandleHostClosing) let alone an app restart.
    /// </summary>
    private void ApplyPendingUpdate(UpdateCheckResult? result)
    {
        _pendingUpdate = result;

        if (result is { IsUpdateAvailable: true })
        {
            UpdateButtonIcon.Glyph = UpdateButtonAvailableGlyph;
            UpdateButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        }
        else
        {
            UpdateButtonIcon.Glyph = UpdateButtonCheckGlyph;
            UpdateButton.ClearValue(Button.StyleProperty);
        }
    }

    /// <summary>
    /// First click (no _pendingUpdate yet): checks GitHub Releases and, if newer, relabels the button
    /// instead of downloading immediately. Second click (on that relabeled button): downloads and
    /// launches the installer, then raises CloseAppRequested so MainWindow can exit via its own
    /// _isClosingConfirmed/Close() pattern, letting the installer overwrite files this process would
    /// otherwise still be holding open - unless the release has no .exe asset yet, in which case it
    /// opens the release page in the browser instead and leaves the app running. This is the only
    /// update check MouseUtil ever performs - purely manual, user-initiated - and its result never
    /// outlives Settings being closed (see HandleHostClosing).
    /// </summary>
    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;

        if (_pendingUpdate is { } pendingUpdate)
        {
            if (pendingUpdate.DownloadUrl is null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pendingUpdate.ReleaseUrl) { UseShellExecute = true });
                UpdateButton.IsEnabled = true;
                return;
            }

            SetUpdateButtonLabel("Downloading...");
            try
            {
                var installerPath = await UpdateChecker!.DownloadInstallerAsync(pendingUpdate.DownloadUrl, CancellationToken.None);
                UpdateChecker!.LaunchInstaller(installerPath);
                CloseAppRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                SetUpdateButtonLabel("Couldn't download the update. Try again later.", isError: true);
                UpdateButton.IsEnabled = true;
            }

            return;
        }

        SetUpdateButtonLabel("Checking...");
        try
        {
            var currentVersion = UpdateService.GetCurrentVersionString();
            var result = await UpdateChecker!.CheckForUpdateAsync(currentVersion, CancellationToken.None);

            if (result.IsUpdateAvailable)
            {
                SetUpdateButtonLabel($"Update to v{result.LatestVersion}");
                ApplyPendingUpdate(result);
            }
            else
            {
                SetUpdateButtonLabel($"You're up to date (v{currentVersion})");
                ApplyPendingUpdate(null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or FormatException)
        {
            SetUpdateButtonLabel("Failed. Try again later.", isError: true);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Sets UpdateButtonLabel's text and (for errors) tints it. isError borrows HotkeyErrorTextBlock's
    /// already-theme-resolved Foreground; the non-error path clears any leftover tint via ClearValue
    /// rather than hardcoding a "default" brush.
    /// </summary>
    private void SetUpdateButtonLabel(string text, bool isError = false)
    {
        UpdateButtonLabel.Text = text;
        if (isError)
        {
            UpdateButtonLabel.Foreground = HotkeyErrorTextBlock.Foreground;
        }
        else
        {
            UpdateButtonLabel.ClearValue(TextBlock.ForegroundProperty);
        }
    }
}
