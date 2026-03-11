using Avalonia;
using Avalonia.Controls;
using KeySwitcher.Infra;
using Serilog;

namespace KeySwitcher.AppHost;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\KeySwitcher.SingleInstance";
    private const string WaitForMutexArgument = "--wait-for-mutex";
    private static readonly TimeSpan RestartMutexWaitTimeout = TimeSpan.FromSeconds(10);

    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(
            initiallyOwned: false,
            name: SingleInstanceMutexName
        );

        if (!TryAcquireSingleInstanceMutex(singleInstanceMutex, args))
        {
            return;
        }

        try
        {
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
        finally
        {
            singleInstanceMutex.ReleaseMutex();
        }
    }

    private static bool TryAcquireSingleInstanceMutex(Mutex singleInstanceMutex, string[] args)
    {
        var shouldWaitForMutex = args.Any(arg =>
            string.Equals(arg, WaitForMutexArgument, StringComparison.OrdinalIgnoreCase)
        );

        try
        {
            return shouldWaitForMutex
                ? singleInstanceMutex.WaitOne(RestartMutexWaitTimeout)
                : singleInstanceMutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
    }
}
