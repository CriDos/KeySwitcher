using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KeySwitcher.Core;
using KeySwitcher.Infra;
using KeySwitcher.Services;
using Serilog;

namespace KeySwitcher.UI.Settings;

internal partial class SettingsWindow : Window
{
    private Hotkey _selectedHotkey;
    private bool _startWithWindows;
    private string _firstLayoutId;
    private string _secondLayoutId;
    private bool _usePerWindowInputScope;

    public SettingsWindow()
        : this(
            Hotkey.Default,
            false,
            Array.Empty<KeyboardLayoutOption>(),
            "00000409",
            "00000419",
            true
        ) { }

    public SettingsWindow(
        Hotkey currentHotkey,
        bool startWithWindows,
        IReadOnlyList<KeyboardLayoutOption> layoutOptions,
        string firstLayoutId,
        string secondLayoutId,
        bool usePerWindowInputScope
    )
    {
        InitializeComponent();

        _selectedHotkey = currentHotkey;
        _startWithWindows = startWithWindows;
        _firstLayoutId = firstLayoutId;
        _secondLayoutId = secondLayoutId;
        _usePerWindowInputScope = usePerWindowInputScope;
        HotkeyTextBox.Text = currentHotkey.ToString();
        AutoStartCheckBox.IsChecked = startWithWindows;
        AutoStartCheckBox.IsCheckedChanged += AutoStartCheckBox_OnIsCheckedChanged;
        PerWindowScopeCheckBox.IsChecked = usePerWindowInputScope;
        VersionText.Text = $"Version {GetDisplayVersion()}";
        ConfigureLanguageSelectors(layoutOptions);

        Opened += (_, _) => HotkeyTextBox.Focus();
    }

    public event Action<SettingsWindowResult>? SettingsSaved;

    private void HotkeyTextBox_OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (!Hotkey.TryFromAvaloniaKey(e.Key, out var virtualKey))
        {
            HotkeyTextBox.Text = "Unsupported key for global shortcut.";
            Log.Debug("Hotkey capture ignored unsupported key {Key}.", e.Key);
            e.Handled = true;
            return;
        }

        var modifiers = ToHotkeyModifiers(e.KeyModifiers);
        var candidate = new Hotkey(modifiers, virtualKey);

        if (!candidate.TryGetNativeRegistration(out _, out _))
        {
            HotkeyTextBox.Text = "Use at least two keys (for example Ctrl+Shift).";
            Log.Debug(
                "Hotkey capture rejected candidate {Candidate}. Modifiers={Modifiers}, Key={Key}",
                candidate,
                e.KeyModifiers,
                e.Key
            );
            e.Handled = true;
            return;
        }

        _selectedHotkey = candidate;
        HotkeyTextBox.Text = candidate.ToString();
        Log.Debug("Hotkey capture accepted candidate {Candidate}.", candidate);
        e.Handled = true;
    }

    private static HotkeyModifiers ToHotkeyModifiers(KeyModifiers modifiers)
    {
        var result = HotkeyModifiers.None;

        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            result |= HotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            result |= HotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            result |= HotkeyModifiers.Shift;
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            result |= HotkeyModifiers.Win;
        }

        return result;
    }

    private void SaveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryValidateLanguagePair(out var message))
        {
            LanguagePairStatusText.Text = message;
            return;
        }

        SettingsSaved?.Invoke(
            new SettingsWindowResult(
                _selectedHotkey,
                _startWithWindows,
                _firstLayoutId,
                _secondLayoutId,
                _usePerWindowInputScope
            )
        );
        Close();
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AutoStartCheckBox_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        _startWithWindows = AutoStartCheckBox.IsChecked ?? false;
    }

    private void PerWindowScopeCheckBox_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        _usePerWindowInputScope = PerWindowScopeCheckBox.IsChecked ?? true;
    }

    private void FirstLanguageComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FirstLanguageComboBox.SelectedItem is LayoutItem item)
        {
            _firstLayoutId = item.LayoutId;
            UpdateLanguagePairStatus();
        }
    }

    private void SecondLanguageComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SecondLanguageComboBox.SelectedItem is LayoutItem item)
        {
            _secondLayoutId = item.LayoutId;
            UpdateLanguagePairStatus();
        }
    }

    private void OpenLogFolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(
                new ProcessStartInfo { FileName = AppPaths.LogDirectory, UseShellExecute = true }
            );
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open log folder {LogDirectory}.", AppPaths.LogDirectory);
        }
    }

    private void ConfigureLanguageSelectors(IReadOnlyList<KeyboardLayoutOption> options)
    {
        var items = options
            .Select(o => new LayoutItem(o.LayoutId, o.PrimaryLanguageId, o.DisplayText))
            .ToList();
        if (items.Count == 0)
        {
            items.Add(new LayoutItem("00000409", 0x09, "EN - English"));
            items.Add(new LayoutItem("00000419", 0x19, "RU - Russian"));
        }

        FirstLanguageComboBox.ItemsSource = items;
        SecondLanguageComboBox.ItemsSource = items;

        _firstLayoutId = ResolveLayoutId(_firstLayoutId, items, items[0].LayoutId);
        _secondLayoutId = ResolveLayoutId(_secondLayoutId, items, items[^1].LayoutId);

        FirstLanguageComboBox.SelectedItem = FindLayoutItem(_firstLayoutId, items);
        SecondLanguageComboBox.SelectedItem = FindLayoutItem(_secondLayoutId, items);

        UpdateLanguagePairStatus();
    }

    private static string ResolveLayoutId(
        string layoutId,
        IReadOnlyList<LayoutItem> items,
        string fallbackLayoutId
    )
    {
        return FindLayoutItem(layoutId, items)?.LayoutId ?? fallbackLayoutId;
    }

    private static LayoutItem? FindLayoutItem(string layoutId, IEnumerable<LayoutItem> items)
    {
        return items.FirstOrDefault(i =>
            string.Equals(i.LayoutId, layoutId, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static string GetDisplayVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is null)
        {
            return "1.0";
        }

        return version.Build > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
    }

    private bool TryValidateLanguagePair(out string message)
    {
        var first = FirstLanguageComboBox.SelectedItem as LayoutItem;
        var second = SecondLanguageComboBox.SelectedItem as LayoutItem;

        if (first is null || second is null)
        {
            message = "Choose both languages.";
            return false;
        }

        if (first.PrimaryLanguageId == second.PrimaryLanguageId)
        {
            message = "Choose two different languages.";
            return false;
        }

        message = $"Switching pair: {first.DisplayText} <-> {second.DisplayText}";
        return true;
    }

    private void UpdateLanguagePairStatus()
    {
        _ = TryValidateLanguagePair(out var message);
        LanguagePairStatusText.Text = message;
    }

    private sealed class LayoutItem(string layoutId, ushort primaryLanguageId, string displayText)
    {
        public string LayoutId { get; } = layoutId;

        public ushort PrimaryLanguageId { get; } = primaryLanguageId;

        public string DisplayText { get; } = displayText;

        public override string ToString() => DisplayText;
    }
}

public readonly record struct SettingsWindowResult(
    Hotkey Hotkey,
    bool StartWithWindows,
    string FirstLayoutId,
    string SecondLayoutId,
    bool UsePerWindowInputScope
);
