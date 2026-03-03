namespace KeySwitcher.Infra;

internal static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var directory = Path.Combine(appData, "KeySwitcher");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");

    public static string LogDirectory
    {
        get
        {
            var directory = Path.Combine(DataDirectory, "logs");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static string LogFilePath => Path.Combine(LogDirectory, "keyswitcher-.log");
}
