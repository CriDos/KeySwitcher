using System.Diagnostics;
using Microsoft.Win32;
using Serilog;

namespace KeySwitcher.Services;

internal sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "KeySwitcher";

    public AutoStartStatus GetStatus()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var registeredCommand = runKey?.GetValue(RunValueName) as string;
            var expectedCommand = BuildCommand();

            if (string.IsNullOrWhiteSpace(registeredCommand))
            {
                return new AutoStartStatus(false, false, null, expectedCommand);
            }

            var registeredPath = ExtractExecutablePath(registeredCommand);
            var expectedPath = ExtractExecutablePath(expectedCommand);
            var pathMatches = PathsEqual(registeredPath, expectedPath);

            return new AutoStartStatus(true, pathMatches, registeredCommand, expectedCommand);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read startup registry entry.");
            return new AutoStartStatus(false, false, null, BuildCommand(), ex.Message);
        }
    }

    public AutoStartApplyResult Ensure(bool enabled)
    {
        var before = GetStatus();
        if (IsAlreadyDesired(before, enabled))
        {
            return new AutoStartApplyResult(true, before, "Already configured.");
        }

        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (runKey is null)
            {
                return new AutoStartApplyResult(false, before, "Cannot open Run registry key.");
            }

            if (enabled)
            {
                runKey.SetValue(RunValueName, BuildCommand(), RegistryValueKind.String);
            }
            else
            {
                runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update startup registry entry. Enabled={Enabled}", enabled);
            return new AutoStartApplyResult(false, before, ex.Message);
        }

        var after = GetStatus();
        var success = IsAlreadyDesired(after, enabled);
        var message = success
            ? "Startup setting applied."
            : "Startup registry value mismatch after write verification.";
        return new AutoStartApplyResult(success, after, message);
    }

    private static bool IsAlreadyDesired(AutoStartStatus status, bool enabled)
    {
        return enabled ? status.Enabled && status.PathMatchesCurrentExecutable : !status.Enabled;
    }

    private static string BuildCommand()
    {
        return $"\"{GetExecutablePath()}\"";
    }

    private static string GetExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return Path.GetFullPath(processPath);
        }

        using var process = Process.GetCurrentProcess();
        var fallback = process.MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return Path.GetFullPath(fallback);
        }

        return Path.GetFullPath(AppContext.BaseDirectory);
    }

    private static string ExtractExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                return NormalizePath(trimmed[1..closingQuote]);
            }
        }

        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            return NormalizePath(trimmed[..(exeIndex + 4)]);
        }

        return NormalizePath(trimmed);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            StringComparison.OrdinalIgnoreCase
        );
    }
}

internal readonly record struct AutoStartStatus(
    bool Enabled,
    bool PathMatchesCurrentExecutable,
    string? RegisteredCommand,
    string ExpectedCommand,
    string? Error = null
);

internal readonly record struct AutoStartApplyResult(
    bool Success,
    AutoStartStatus Status,
    string Message
);
