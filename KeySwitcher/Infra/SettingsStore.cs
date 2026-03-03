using System.Text.Json;
using System.Text.Json.Serialization;
using KeySwitcher.Core;
using Serilog;

namespace KeySwitcher.Infra;

internal sealed class SettingsStore
{
    private readonly string _settingsPath;

    public SettingsStore()
    {
        _settingsPath = AppPaths.SettingsFilePath;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize(
                json,
                SettingsJsonContext.Default.AppSettings
            );
            Log.Debug("Settings loaded from {SettingsPath}.", _settingsPath);
            return settings ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "Failed to read settings from {SettingsPath}. Using defaults.",
                _settingsPath
            );
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        File.WriteAllText(_settingsPath, json);
        Log.Debug(
            "Settings saved to {SettingsPath}. Hotkey={Hotkey}. StartWithWindows={StartWithWindows}. Layouts={FirstLayout}<->{SecondLayout}. InputScopeMode={InputScopeMode}",
            _settingsPath,
            settings.GetHotkey(),
            settings.StartWithWindows,
            settings.FirstLayoutId,
            settings.SecondLayoutId,
            ToInputScopeModeLabel(settings.UsePerWindowInputScope)
        );
    }

    private static string ToInputScopeModeLabel(bool? usePerWindowInputScope)
    {
        return usePerWindowInputScope switch
        {
            true => "PerWindow",
            false => "Global",
            null => "SystemDefault",
        };
    }
}

[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext;
