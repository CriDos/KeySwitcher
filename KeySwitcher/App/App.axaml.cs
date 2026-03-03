using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KeySwitcher.Core;

namespace KeySwitcher.AppHost;

public partial class App : Avalonia.Application
{
    private AppController? _controller;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _controller = new AppController(desktop);
            _controller.Start();
            desktop.Exit += (_, _) => _controller.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
