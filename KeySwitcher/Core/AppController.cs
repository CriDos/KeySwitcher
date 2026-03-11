using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using KeySwitcher.Infra;
using KeySwitcher.Services;
using KeySwitcher.UI.Settings;
using KeySwitcher.UI.Tray;
using Serilog;

namespace KeySwitcher.Core;

internal sealed class AppController : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktopLifetime;
    private readonly SettingsStore _settingsStore;
    private readonly AutoStartService _autoStartService;
    private readonly InputScopeService _inputScopeService;
    private readonly KeyboardLayoutService _layoutService;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly TrayIconService _trayIcon;
    private readonly DispatcherTimer _indicatorTimer;

    private AppSettings _settings;
    private bool _usePerWindowInputScope = true;
    private SettingsWindow? _settingsWindow;

    public AppController(IClassicDesktopStyleApplicationLifetime desktopLifetime)
    {
        _desktopLifetime = desktopLifetime;
        _settingsStore = new SettingsStore();
        _autoStartService = new AutoStartService();
        _inputScopeService = new InputScopeService();
        _layoutService = new KeyboardLayoutService();
        _hotkeyService = new GlobalHotkeyService();
        _trayIcon = new TrayIconService();

        _indicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };

        _settings = _settingsStore.Load();

        _hotkeyService.HotkeyPressed += HotkeyService_OnHotkeyPressed;
        _trayIcon.LanguageToggleRequested += TrayIcon_OnLanguageToggleRequested;
        _trayIcon.SettingsRequested += TrayIcon_OnSettingsRequested;
        _trayIcon.RestartAsAdministratorRequested += TrayIcon_OnRestartAsAdministratorRequested;
        _trayIcon.ExitRequested += TrayIcon_OnExitRequested;
        _indicatorTimer.Tick += IndicatorTimer_OnTick;
    }

    public void Start()
    {
        if (
            !_layoutService.ConfigureSwitchLayouts(
                _settings.FirstLayoutId,
                _settings.SecondLayoutId
            )
        )
        {
            _trayIcon.ShowInfo("Need at least two installed keyboard languages.");
            Log.Warning("Layout pair configuration failed at startup.");
        }

        var layoutsChanged =
            !string.Equals(
                _settings.FirstLayoutId,
                _layoutService.FirstLayoutId,
                StringComparison.OrdinalIgnoreCase
            )
            || !string.Equals(
                _settings.SecondLayoutId,
                _layoutService.SecondLayoutId,
                StringComparison.OrdinalIgnoreCase
            );
        if (layoutsChanged)
        {
            _settings.FirstLayoutId = _layoutService.FirstLayoutId;
            _settings.SecondLayoutId = _layoutService.SecondLayoutId;
            _settingsStore.Save(_settings);
        }

        InitializeInputScopeMode();

        var configuredHotkey = _settings.GetHotkey();
        Log.Information("Starting controller with configured hotkey: {Hotkey}", configuredHotkey);

        if (!_hotkeyService.Register(configuredHotkey))
        {
            configuredHotkey = Hotkey.Default;
            _settings.SetHotkey(configuredHotkey);
            _settingsStore.Save(_settings);
            Log.Warning(
                "Configured hotkey registration failed. Falling back to default {Hotkey}.",
                configuredHotkey
            );

            if (!_hotkeyService.Register(configuredHotkey))
            {
                _trayIcon.ShowInfo("Cannot register hotkey. Another app likely already uses it.");
                Log.Error("Fallback hotkey registration failed: {Hotkey}", configuredHotkey);
            }
            else
            {
                _trayIcon.ShowInfo("Configured hotkey is busy. Fallback hotkey: Ctrl+Alt+Space.");
                Log.Information("Fallback hotkey registered: {Hotkey}", configuredHotkey);
            }
        }
        else
        {
            Log.Information("Hotkey registered: {Hotkey}", configuredHotkey);
        }

        EnsureAutoStartConfiguration();
        UpdateTrayIndicator();
        _indicatorTimer.Start();
    }

    public void Dispose()
    {
        _indicatorTimer.Stop();
        _indicatorTimer.Tick -= IndicatorTimer_OnTick;

        _hotkeyService.HotkeyPressed -= HotkeyService_OnHotkeyPressed;
        _trayIcon.LanguageToggleRequested -= TrayIcon_OnLanguageToggleRequested;
        _trayIcon.SettingsRequested -= TrayIcon_OnSettingsRequested;
        _trayIcon.RestartAsAdministratorRequested -= TrayIcon_OnRestartAsAdministratorRequested;
        _trayIcon.ExitRequested -= TrayIcon_OnExitRequested;

        if (_settingsWindow is not null)
        {
            _settingsWindow.SettingsSaved -= SettingsWindow_OnSettingsSaved;
            _settingsWindow.Closed -= SettingsWindow_OnClosed;
            _settingsWindow.Close();
            _settingsWindow = null;
        }

        _hotkeyService.Dispose();
        _trayIcon.Dispose();
    }

    private void TrayIcon_OnSettingsRequested(object? sender, EventArgs e)
    {
        ShowSettingsWindow();
    }

    private void TrayIcon_OnLanguageToggleRequested(object? sender, EventArgs e)
    {
        ToggleLanguageAndUpdateIndicator("tray-click", global: true);
    }

    private void TrayIcon_OnRestartAsAdministratorRequested(object? sender, EventArgs e)
    {
        RestartAsAdministrator();
    }

    private void TrayIcon_OnExitRequested(object? sender, EventArgs e)
    {
        _desktopLifetime.Shutdown();
    }

    private void IndicatorTimer_OnTick(object? sender, EventArgs e)
    {
        UpdateTrayIndicator();
    }

    private void HotkeyService_OnHotkeyPressed(object? sender, EventArgs e)
    {
        var toggleGlobally = !_usePerWindowInputScope;
        Dispatcher.UIThread.Post(
            () => ToggleLanguageAndUpdateIndicator("global-hotkey", global: toggleGlobally),
            DispatcherPriority.Send
        );
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        var layoutOptions = _layoutService.GetAvailableLayouts();
        _settingsWindow = new SettingsWindow(
            _settings.GetHotkey(),
            _settings.StartWithWindows,
            layoutOptions,
            _settings.FirstLayoutId,
            _settings.SecondLayoutId,
            _settings.UsePerWindowInputScope ?? _usePerWindowInputScope
        );
        _settingsWindow.SettingsSaved += SettingsWindow_OnSettingsSaved;
        _settingsWindow.Closed += SettingsWindow_OnClosed;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void SettingsWindow_OnSettingsSaved(SettingsWindowResult result)
    {
        var changed = false;

        if (_hotkeyService.Register(result.Hotkey))
        {
            _settings.SetHotkey(result.Hotkey);
            changed = true;
            _trayIcon.ShowInfo($"Hotkey updated: {result.Hotkey}");
            Log.Information("Hotkey updated by user: {Hotkey}", result.Hotkey);
        }
        else
        {
            _trayIcon.ShowInfo("Cannot register this shortcut. Try another one.");
            Log.Warning("User selected hotkey could not be registered: {Hotkey}", result.Hotkey);
        }

        var autoStartResult = _autoStartService.Ensure(result.StartWithWindows);
        if (autoStartResult.Success)
        {
            _settings.StartWithWindows = result.StartWithWindows;
            changed = true;
            Log.Information(
                "Autostart setting applied. Enabled={Enabled}. Command={Command}",
                result.StartWithWindows,
                autoStartResult.Status.RegisteredCommand ?? "<none>"
            );
        }
        else
        {
            _trayIcon.ShowInfo("Autostart update failed. See log for details.");
            Log.Warning(
                "Autostart update failed. Enabled={Enabled}. Message={Message}",
                result.StartWithWindows,
                autoStartResult.Message
            );
        }

        var inputScopeResult = _inputScopeService.Ensure(result.UsePerWindowInputScope);
        if (inputScopeResult.Success)
        {
            _usePerWindowInputScope = result.UsePerWindowInputScope;
            if (_settings.UsePerWindowInputScope != result.UsePerWindowInputScope)
            {
                _settings.UsePerWindowInputScope = result.UsePerWindowInputScope;
                changed = true;
            }

            Log.Information(
                "Input scope mode applied. Mode={Mode}.",
                ToInputScopeModeLabel(_usePerWindowInputScope)
            );
        }
        else
        {
            _trayIcon.ShowInfo("Input scope update failed. See log for details.");
            Log.Warning(
                "Input scope update failed. Mode={Mode}. Message={Message}. ErrorCode={ErrorCode}",
                ToInputScopeModeLabel(result.UsePerWindowInputScope),
                inputScopeResult.Message,
                inputScopeResult.Status.ErrorCode
            );
        }

        if (_layoutService.ConfigureSwitchLayouts(result.FirstLayoutId, result.SecondLayoutId))
        {
            if (
                !string.Equals(
                    _settings.FirstLayoutId,
                    _layoutService.FirstLayoutId,
                    StringComparison.OrdinalIgnoreCase
                )
                || !string.Equals(
                    _settings.SecondLayoutId,
                    _layoutService.SecondLayoutId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                _settings.FirstLayoutId = _layoutService.FirstLayoutId;
                _settings.SecondLayoutId = _layoutService.SecondLayoutId;
                changed = true;
            }
        }
        else
        {
            _trayIcon.ShowInfo("Need two different installed languages for switching.");
            Log.Warning(
                "User selected invalid layout pair: {FirstLayout} / {SecondLayout}",
                result.FirstLayoutId,
                result.SecondLayoutId
            );
        }

        if (changed)
        {
            _settingsStore.Save(_settings);
        }
    }

    private void SettingsWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_settingsWindow is null)
        {
            return;
        }

        _settingsWindow.SettingsSaved -= SettingsWindow_OnSettingsSaved;
        _settingsWindow.Closed -= SettingsWindow_OnClosed;
        _settingsWindow = null;
    }

    private void InitializeInputScopeMode()
    {
        var status = _inputScopeService.GetStatus();
        if (status.Success)
        {
            _usePerWindowInputScope = status.UsePerWindowScope;
            Log.Information(
                "Detected current input scope mode: {Mode}.",
                ToInputScopeModeLabel(_usePerWindowInputScope)
            );
        }
        else
        {
            _usePerWindowInputScope = _settings.UsePerWindowInputScope ?? true;
            Log.Warning(
                "Failed to read current input scope mode. Using fallback {Mode}. ErrorCode={ErrorCode}. Message={Message}",
                ToInputScopeModeLabel(_usePerWindowInputScope),
                status.ErrorCode,
                status.ErrorMessage
            );
        }

        if (!_settings.UsePerWindowInputScope.HasValue)
        {
            Log.Information(
                "Input scope preference is not set in app settings. Keeping current system mode {Mode}.",
                ToInputScopeModeLabel(_usePerWindowInputScope)
            );
            return;
        }

        var desiredMode = _settings.UsePerWindowInputScope.Value;
        var applyResult = _inputScopeService.Ensure(desiredMode);
        if (applyResult.Success)
        {
            _usePerWindowInputScope = desiredMode;
            Log.Information(
                "Input scope synchronized at startup. Mode={Mode}.",
                ToInputScopeModeLabel(_usePerWindowInputScope)
            );
            return;
        }

        _trayIcon.ShowInfo("Input scope synchronization failed. See log.");
        Log.Warning(
            "Input scope synchronization failed at startup. DesiredMode={Mode}. Message={Message}. ErrorCode={ErrorCode}",
            ToInputScopeModeLabel(desiredMode),
            applyResult.Message,
            applyResult.Status.ErrorCode
        );
    }

    private void EnsureAutoStartConfiguration()
    {
        var autoStartResult = _autoStartService.Ensure(_settings.StartWithWindows);
        if (autoStartResult.Success)
        {
            Log.Information(
                "Autostart synchronized at startup. Enabled={Enabled}",
                _settings.StartWithWindows
            );
            return;
        }

        _trayIcon.ShowInfo("Autostart check failed. See log.");
        Log.Warning(
            "Autostart synchronization failed at startup. Enabled={Enabled}. Message={Message}",
            _settings.StartWithWindows,
            autoStartResult.Message
        );
    }

    private void UpdateTrayIndicator()
    {
        var languageLabel = _usePerWindowInputScope
            ? _layoutService.GetCurrentLanguageLabel()
            : _layoutService.GetGlobalLanguageLabel();
        _trayIcon.UpdateLanguage(languageLabel);
    }

    private void ToggleLanguageAndUpdateIndicator(string source, bool global)
    {
        if (global)
        {
            Log.Debug("Global language toggle requested via {Source}.", source);
        }
        else
        {
            Log.Debug("Language toggle requested via {Source}.", source);
        }

        var changed = global
            ? _layoutService.ToggleLanguageGlobal()
            : _layoutService.ToggleLanguage();
        var label = global
            ? _layoutService.GetGlobalLanguageLabel()
            : _layoutService.GetCurrentLanguageLabel();
        _trayIcon.UpdateLanguage(label);

        if (!changed)
        {
            if (global)
            {
                Log.Warning(
                    "Global language toggle reported no effective change. Source={Source}.",
                    source
                );
            }
            else
            {
                Log.Warning(
                    "Language toggle reported no effective change. Source={Source}.",
                    source
                );
            }
        }
    }

    private void RestartAsAdministrator()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            _trayIcon.ShowInfo("Cannot restart as administrator.");
            Log.Warning("Restart as administrator failed: process path is unavailable.");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                Arguments = "--wait-for-mutex",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };

            _ = Process.Start(startInfo);
            Log.Information("Restart as administrator requested.");
            _desktopLifetime.Shutdown();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _trayIcon.ShowInfo("Administrator restart canceled.");
            Log.Information("Restart as administrator canceled by user.");
        }
        catch (Exception ex)
        {
            _trayIcon.ShowInfo("Administrator restart failed.");
            Log.Warning(ex, "Restart as administrator failed.");
        }
    }

    private static string ToInputScopeModeLabel(bool usePerWindowInputScope)
    {
        return usePerWindowInputScope ? "PerWindow" : "Global";
    }
}
