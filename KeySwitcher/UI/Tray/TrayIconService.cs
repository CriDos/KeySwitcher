using System.Drawing;
using Avalonia.Controls;
using Avalonia.Threading;

namespace KeySwitcher.UI.Tray;

internal sealed class TrayIconService : IDisposable
{
    private readonly TrayIcon _trayIcon;
    private readonly TrayIcons _trayIcons;
    private readonly Dictionary<string, WindowIcon> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly NativeMenuItem _languageItem;
    private readonly NativeMenuItem _settingsItem;
    private readonly NativeMenuItem _exitItem;

    private string _currentLanguage = "EN";

    public event EventHandler? SettingsRequested;

    public event EventHandler? LanguageToggleRequested;

    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        _icons["EN"] = TextIconFactory.Create("EN", Color.White);
        _icons["RU"] = TextIconFactory.Create("RU", Color.White);

        _languageItem = new NativeMenuItem("Language: EN") { IsEnabled = false };

        _settingsItem = new NativeMenuItem("Settings");
        _settingsItem.Click += SettingsItem_OnClick;

        _exitItem = new NativeMenuItem("Exit");
        _exitItem.Click += ExitItem_OnClick;

        var menu = new NativeMenu
        {
            Items = { _languageItem, new NativeMenuItemSeparator(), _settingsItem, _exitItem },
        };

        _trayIcon = new TrayIcon
        {
            Icon = GetOrCreateIcon(_currentLanguage),
            ToolTipText = "KeySwitcher: EN",
            Menu = menu,
            IsVisible = true,
        };

        _trayIcon.Clicked += TrayIcon_OnClicked;

        _trayIcons = new TrayIcons { _trayIcon };

        TrayIcon.SetIcons(Avalonia.Application.Current!, _trayIcons);
    }

    public void UpdateLanguage(string languageLabel)
    {
        _currentLanguage = NormalizeLabel(languageLabel);
        _trayIcon.Icon = GetOrCreateIcon(_currentLanguage);
        _trayIcon.ToolTipText = $"KeySwitcher: {_currentLanguage}";
        _languageItem.Header = $"Language: {_currentLanguage}";
    }

    public void ShowInfo(string text)
    {
        _trayIcon.ToolTipText = $"KeySwitcher: {_currentLanguage} ({text})";
    }

    public void Dispose()
    {
        _trayIcon.Clicked -= TrayIcon_OnClicked;
        _settingsItem.Click -= SettingsItem_OnClick;
        _exitItem.Click -= ExitItem_OnClick;

        _trayIcon.IsVisible = false;

        if (Avalonia.Application.Current is not null)
        {
            TrayIcon.SetIcons(Avalonia.Application.Current, new TrayIcons());
        }
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs e)
    {
        RaiseDeferred(LanguageToggleRequested);
    }

    private void SettingsItem_OnClick(object? sender, EventArgs e)
    {
        RaiseDeferred(SettingsRequested);
    }

    private void ExitItem_OnClick(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(
            () => ExitRequested?.Invoke(this, EventArgs.Empty),
            DispatcherPriority.Background
        );
    }

    private void RaiseDeferred(EventHandler? handler)
    {
        Dispatcher.UIThread.Post(
            () => handler?.Invoke(this, EventArgs.Empty),
            DispatcherPriority.Background
        );
    }

    private WindowIcon GetOrCreateIcon(string label)
    {
        if (_icons.TryGetValue(label, out var icon))
        {
            return icon;
        }

        icon = TextIconFactory.Create(label, Color.White);
        _icons[label] = icon;
        return icon;
    }

    private static string NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "EN";
        }

        var normalized = label.Trim().ToUpperInvariant();
        return normalized.Length <= 6 ? normalized : normalized[..6];
    }
}
