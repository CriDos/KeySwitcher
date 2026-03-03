using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Serilog;

namespace KeySwitcher.Services;

internal sealed partial class KeyboardLayoutService
{
    private const uint KlfActivate = 0x00000001;
    private const uint InputLangChangeRequestMessage = 0x0050;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int SendMessageTimeoutMs = 120;
    private const int VerifyAttempts = 5;
    private const int VerifyDelayMs = 35;
    private const string DefaultFirstLayoutId = "00000409";
    private const string DefaultSecondLayoutId = "00000419";
    private const string PreloadRegistryPath = @"Keyboard Layout\Preload";
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private static readonly HashSet<string> IgnoredWindowClasses =
    [
        "Shell_TrayWnd",
        "NotifyIconOverflowWindow",
        "#32768",
        "Progman",
        "WorkerW",
    ];

    private IntPtr _lastUsableWindow;
    private string _firstLayoutId = DefaultFirstLayoutId;
    private string _secondLayoutId = DefaultSecondLayoutId;
    private ushort _firstPrimaryLanguageId = GetPrimaryLanguageIdFromLayoutId(DefaultFirstLayoutId);
    private ushort _secondPrimaryLanguageId = GetPrimaryLanguageIdFromLayoutId(
        DefaultSecondLayoutId
    );

    public string FirstLayoutId => _firstLayoutId;

    public string SecondLayoutId => _secondLayoutId;

    public IReadOnlyList<KeyboardLayoutOption> GetAvailableLayouts()
    {
        var options = new List<KeyboardLayoutOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var preloadKey = Registry.CurrentUser.OpenSubKey(
                PreloadRegistryPath,
                writable: false
            );
            if (preloadKey is not null)
            {
                foreach (var valueName in preloadKey.GetValueNames().OrderBy(ParseOrder))
                {
                    var rawLayoutId = preloadKey.GetValue(valueName) as string;
                    var normalizedLayoutId = NormalizeLayoutId(rawLayoutId);
                    if (string.IsNullOrEmpty(normalizedLayoutId))
                    {
                        continue;
                    }

                    if (seen.Add(normalizedLayoutId))
                    {
                        options.Add(CreateOption(normalizedLayoutId));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read keyboard layout list from registry preload key.");
        }

        AddFallbackIfMissing(options, seen, DefaultFirstLayoutId);
        AddFallbackIfMissing(options, seen, DefaultSecondLayoutId);

        return options;
    }

    public bool ConfigureSwitchLayouts(string firstLayoutId, string secondLayoutId)
    {
        var options = GetAvailableLayouts();
        if (options.Count == 0)
        {
            Log.Warning("No keyboard layouts found while configuring switch layouts.");
            return false;
        }

        var first = ResolveOption(firstLayoutId, options) ?? options[0];

        var second = ResolveOption(secondLayoutId, options);
        if (
            second is null
            || second.Value.PrimaryLanguageId == first.PrimaryLanguageId
            || string.Equals(
                second.Value.LayoutId,
                first.LayoutId,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            second = options.FirstOrDefault(o => o.PrimaryLanguageId != first.PrimaryLanguageId);
        }

        if (second is null)
        {
            Log.Warning(
                "Failed to configure switching layouts: need two distinct languages. FirstLayout={FirstLayout}.",
                first.LayoutId
            );
            return false;
        }

        _firstLayoutId = first.LayoutId;
        _secondLayoutId = second.Value.LayoutId;
        _firstPrimaryLanguageId = first.PrimaryLanguageId;
        _secondPrimaryLanguageId = second.Value.PrimaryLanguageId;

        Log.Information(
            "Switching layouts configured: {FirstLayout} ({FirstLabel}) <-> {SecondLayout} ({SecondLabel})",
            _firstLayoutId,
            first.Label,
            _secondLayoutId,
            second.Value.Label
        );

        return true;
    }

    public string GetCurrentLanguageLabel()
    {
        var foregroundWindow = GetForegroundWindow();
        TrackUsableWindow(foregroundWindow);

        var targetWindow = ResolvePrimaryUsableWindow(foregroundWindow);
        var primaryLanguageId = GetPrimaryLanguageIdForWindow(targetWindow);
        return GetLanguageLabel(primaryLanguageId);
    }

    public string GetGlobalLanguageLabel()
    {
        var foregroundWindow = GetForegroundWindow();
        TrackUsableWindow(foregroundWindow);

        var targetWindow = ResolvePrimaryUsableWindow(foregroundWindow);
        var primaryLanguageId = GetPrimaryLanguageIdForWindow(targetWindow);
        return GetLanguageLabel(primaryLanguageId);
    }

    public bool ToggleLanguage()
    {
        var foregroundWindow = GetForegroundWindow();
        TrackUsableWindow(foregroundWindow);

        if (!IsUsableWindow(foregroundWindow))
        {
            Log.Debug(
                "Foreground window {Window} is not usable for per-window toggle. Switching globally.",
                FormatWindow(foregroundWindow)
            );
            return ToggleLanguageGlobal();
        }

        var targetWindow = ResolvePrimaryUsableWindow(foregroundWindow);
        var currentPrimaryLanguageId = GetPrimaryLanguageIdForWindow(targetWindow);
        var targetLayout = GetTargetLayout(currentPrimaryLanguageId);

        var applied = ApplyLayout(targetLayout.LayoutId, targetWindow);
        var changed = VerifyLanguage(
            targetLayout.PrimaryLanguageId,
            () =>
            {
                var window = ResolveVerificationWindow(targetWindow);
                return GetPrimaryLanguageIdForWindow(window);
            }
        );

        if (!changed)
        {
            var fallbackWindow = ResolveFallbackWindow(targetWindow);
            if (fallbackWindow != IntPtr.Zero && fallbackWindow != targetWindow)
            {
                var fallbackApplied = ApplyLayout(targetLayout.LayoutId, fallbackWindow);
                var fallbackChanged = VerifyLanguage(
                    targetLayout.PrimaryLanguageId,
                    () =>
                    {
                        var window = ResolveVerificationWindow(fallbackWindow);
                        return GetPrimaryLanguageIdForWindow(window);
                    }
                );

                Log.Information(
                    "Retry keyboard layout toggle on fallback window {FallbackWindow}. Applied={Applied}. Changed={Changed}.",
                    FormatWindow(fallbackWindow),
                    fallbackApplied,
                    fallbackChanged
                );

                applied |= fallbackApplied;
                changed |= fallbackChanged;
                if (fallbackChanged)
                {
                    targetWindow = fallbackWindow;
                }
            }
        }

        if (!changed)
        {
            Log.Debug(
                "Per-window toggle did not change layout for {Window}. Retrying as global toggle.",
                FormatWindow(targetWindow)
            );
            var globalApplied = ApplyLayout(targetLayout.LayoutId, targetWindow);
            var globalChanged = VerifyLanguage(
                targetLayout.PrimaryLanguageId,
                () =>
                {
                    var window = ResolveVerificationWindow(targetWindow);
                    return GetPrimaryLanguageIdForWindow(window);
                }
            );
            applied |= globalApplied;
            changed |= globalChanged;
        }

        Log.Information(
            "Keyboard layout toggle requested: {CurrentLanguage} -> {TargetLanguage}. Applied={Applied}. Changed={Changed}. TargetWindow={TargetWindow}.",
            GetLanguageLabel(currentPrimaryLanguageId),
            targetLayout.Label,
            applied,
            changed,
            FormatWindow(targetWindow)
        );

        return applied && changed;
    }

    public bool ToggleLanguageGlobal()
    {
        var foregroundWindow = GetForegroundWindow();
        TrackUsableWindow(foregroundWindow);

        var targetWindow = ResolvePrimaryUsableWindow(foregroundWindow);
        var currentPrimaryLanguageId = GetPrimaryLanguageIdForWindow(targetWindow);
        var targetLayout = GetTargetLayout(currentPrimaryLanguageId);

        var applied = ApplyLayout(targetLayout.LayoutId, targetWindow);
        var changed = VerifyLanguage(
            targetLayout.PrimaryLanguageId,
            () =>
            {
                var window = ResolveVerificationWindow(targetWindow);
                return GetPrimaryLanguageIdForWindow(window);
            }
        );

        Log.Information(
            "Global keyboard layout toggle requested: {CurrentLanguage} -> {TargetLanguage}. Applied={Applied}. Changed={Changed}. TargetWindow={TargetWindow}.",
            GetLanguageLabel(currentPrimaryLanguageId),
            targetLayout.Label,
            applied,
            changed,
            FormatWindow(targetWindow)
        );

        return applied && changed;
    }

    private KeyboardLayoutOption GetTargetLayout(ushort currentPrimaryLanguageId)
    {
        return currentPrimaryLanguageId == _firstPrimaryLanguageId
            ? CreateOption(_secondLayoutId)
            : CreateOption(_firstLayoutId);
    }

    private void TrackUsableWindow(IntPtr windowHandle)
    {
        if (IsUsableWindow(windowHandle))
        {
            _lastUsableWindow = windowHandle;
        }
    }

    private IntPtr ResolvePrimaryUsableWindow(IntPtr foregroundWindow)
    {
        if (IsUsableWindow(foregroundWindow))
        {
            return foregroundWindow;
        }

        if (IsUsableWindow(_lastUsableWindow))
        {
            return _lastUsableWindow;
        }

        return foregroundWindow;
    }

    private IntPtr ResolveFallbackWindow(IntPtr currentTarget)
    {
        if (IsUsableWindow(_lastUsableWindow) && _lastUsableWindow != currentTarget)
        {
            return _lastUsableWindow;
        }

        var foregroundWindow = GetForegroundWindow();
        if (IsUsableWindow(foregroundWindow) && foregroundWindow != currentTarget)
        {
            return foregroundWindow;
        }

        return IntPtr.Zero;
    }

    private static bool VerifyLanguage(
        ushort expectedPrimaryLanguageId,
        Func<ushort> languageProvider
    )
    {
        for (var i = 0; i < VerifyAttempts; i++)
        {
            if (languageProvider() == expectedPrimaryLanguageId)
            {
                return true;
            }

            Thread.Sleep(VerifyDelayMs);
        }

        return false;
    }

    private static IntPtr ResolveVerificationWindow(IntPtr preferredWindow)
    {
        if (preferredWindow != IntPtr.Zero && IsWindow(preferredWindow))
        {
            return preferredWindow;
        }

        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero && IsWindow(foreground) ? foreground : IntPtr.Zero;
    }

    private ushort GetPrimaryLanguageIdForWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return GetPrimaryLanguageIdForThread(0);
        }

        var threadId = GetWindowThreadProcessId(windowHandle, out _);
        if (threadId == 0)
        {
            return GetPrimaryLanguageIdForThread(0);
        }

        return GetPrimaryLanguageIdForThread(threadId);
    }

    private static ushort GetPrimaryLanguageIdForThread(uint threadId)
    {
        var layout = GetKeyboardLayout(threadId);
        var languageId = unchecked((ushort)((long)layout & 0xFFFF));
        return (ushort)(languageId & 0x03FF);
    }

    private bool IsUsableWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return false;
        }

        var className = GetWindowClassName(windowHandle);
        return !IgnoredWindowClasses.Contains(className);
    }

    private static bool ApplyLayout(string layoutId, IntPtr targetWindow)
    {
        var layoutHandle = LoadKeyboardLayout(layoutId, KlfActivate);
        if (layoutHandle == IntPtr.Zero)
        {
            Log.Warning("LoadKeyboardLayout failed for layout {LayoutId}.", layoutId);
            return false;
        }

        var resolvedWindow = targetWindow;
        if (resolvedWindow == IntPtr.Zero || !IsWindow(resolvedWindow))
        {
            resolvedWindow = GetForegroundWindow();
        }

        var notified = false;
        if (resolvedWindow != IntPtr.Zero && IsWindow(resolvedWindow))
        {
            notified = SendInputLanguageChangeRequest(resolvedWindow, layoutHandle);
        }

        if (!notified)
        {
            notified = SendInputLanguageChangeRequest(HwndBroadcast, layoutHandle);
        }

        _ = ActivateKeyboardLayout(layoutHandle, 0);
        return notified;
    }

    private static bool SendInputLanguageChangeRequest(IntPtr hWnd, IntPtr layoutHandle)
    {
        var result = SendMessageTimeout(
            hWnd,
            InputLangChangeRequestMessage,
            IntPtr.Zero,
            layoutHandle,
            SmtoAbortIfHung,
            SendMessageTimeoutMs,
            out _
        );

        if (result != IntPtr.Zero)
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != 0)
        {
            Log.Debug(
                "SendMessageTimeout(WM_INPUTLANGCHANGEREQUEST) failed for {Window}. Error={Error}.",
                FormatWindow(hWnd),
                error
            );
        }

        return false;
    }

    private static KeyboardLayoutOption? ResolveOption(
        string layoutId,
        IReadOnlyList<KeyboardLayoutOption> options
    )
    {
        var normalized = NormalizeLayoutId(layoutId);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        foreach (var option in options)
        {
            if (string.Equals(option.LayoutId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        return null;
    }

    private static void AddFallbackIfMissing(
        ICollection<KeyboardLayoutOption> options,
        ISet<string> seen,
        string layoutId
    )
    {
        var normalized = NormalizeLayoutId(layoutId);
        if (string.IsNullOrEmpty(normalized) || !seen.Add(normalized))
        {
            return;
        }

        options.Add(CreateOption(normalized));
    }

    private static KeyboardLayoutOption CreateOption(string layoutId)
    {
        var normalized = NormalizeLayoutId(layoutId);
        var primaryLanguageId = GetPrimaryLanguageIdFromLayoutId(normalized);
        var label = GetLanguageLabel(primaryLanguageId);
        var displayName = GetLanguageDisplayName(primaryLanguageId);
        return new KeyboardLayoutOption(normalized, primaryLanguageId, label, displayName);
    }

    private static string NormalizeLayoutId(string? layoutId)
    {
        if (string.IsNullOrWhiteSpace(layoutId))
        {
            return string.Empty;
        }

        var trimmed = layoutId.Trim();
        if (trimmed.Length > 8)
        {
            trimmed = trimmed[^8..];
        }

        if (
            !uint.TryParse(
                trimmed,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var value
            )
        )
        {
            return string.Empty;
        }

        return value.ToString("X8", CultureInfo.InvariantCulture);
    }

    private static ushort GetPrimaryLanguageIdFromLayoutId(string layoutId)
    {
        if (
            !uint.TryParse(
                NormalizeLayoutId(layoutId),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var value
            )
        )
        {
            return 0x09;
        }

        var languageId = unchecked((ushort)(value & 0xFFFF));
        return (ushort)(languageId & 0x03FF);
    }

    private static int ParseOrder(string valueName)
    {
        return int.TryParse(valueName, out var order) ? order : int.MaxValue;
    }

    private static string GetLanguageDisplayName(ushort primaryLanguageId)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(primaryLanguageId);
            return culture.EnglishName;
        }
        catch
        {
            return $"Language 0x{primaryLanguageId:X3}";
        }
    }

    private static string GetLanguageLabel(ushort primaryLanguageId)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(primaryLanguageId);
            var code = culture.TwoLetterISOLanguageName;
            return string.IsNullOrWhiteSpace(code)
                ? $"0x{primaryLanguageId:X3}"
                : code.ToUpperInvariant();
        }
        catch
        {
            return $"0x{primaryLanguageId:X3}";
        }
    }

    private static string FormatWindow(IntPtr windowHandle)
    {
        return $"0x{windowHandle.ToInt64():X}";
    }

    private static string GetWindowClassName(IntPtr windowHandle)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(windowHandle, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetKeyboardLayout(uint idThread);

    [LibraryImport(
        "user32.dll",
        EntryPoint = "LoadKeyboardLayoutW",
        StringMarshalling = StringMarshalling.Utf16
    )]
    private static partial IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hWnd);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetClassNameW",
        SetLastError = true,
        CharSet = CharSet.Unicode
    )]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static partial IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult
    );
}

internal readonly record struct KeyboardLayoutOption(
    string LayoutId,
    ushort PrimaryLanguageId,
    string Label,
    string DisplayName
)
{
    public string DisplayText => $"{Label} - {DisplayName}";
}
