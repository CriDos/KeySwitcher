using Avalonia;
using Avalonia.Controls;
using KeySwitcher.Infra;
using Serilog;

namespace KeySwitcher.AppHost;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\KeySwitcher.SingleInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: SingleInstanceMutexName,
            createdNew: out var isFirstInstance
        );

        if (!isFirstInstance)
        {
            return;
        }

        AppLogging.Configure();

        try
        {
            Log.Information("KeySwitcher is starting.");
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "KeySwitcher terminated unexpectedly.");
            throw;
        }
        finally
        {
            Log.Information("KeySwitcher is stopping.");
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
    }
}
