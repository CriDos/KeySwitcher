using System.Runtime.InteropServices;
using Serilog;

namespace KeySwitcher.Services;

internal sealed partial class InputScopeService
{
    private const uint SpiGetThreadLocalInputSettings = 0x104E;
    private const uint SpiSetThreadLocalInputSettings = 0x104F;
    private const uint SpifSendChange = 0x0002;

    public InputScopeStatus GetStatus()
    {
        var value = 0;
        if (!SystemParametersInfo(SpiGetThreadLocalInputSettings, 0, ref value, 0))
        {
            var error = Marshal.GetLastWin32Error();
            return new InputScopeStatus(
                Success: false,
                UsePerWindowScope: false,
                ErrorCode: error,
                ErrorMessage: $"SystemParametersInfo GET failed. Error={error}"
            );
        }

        return new InputScopeStatus(true, value != 0, 0, null);
    }

    public InputScopeApplyResult Ensure(bool usePerWindowScope)
    {
        var before = GetStatus();
        if (before.Success && before.UsePerWindowScope == usePerWindowScope)
        {
            return new InputScopeApplyResult(true, before, "Already configured.");
        }

        var value = usePerWindowScope ? 1 : 0;
        if (!SystemParametersInfo(SpiSetThreadLocalInputSettings, 0, ref value, SpifSendChange))
        {
            var error = Marshal.GetLastWin32Error();
            Log.Warning(
                "Failed to apply input scope mode. UsePerWindowScope={UsePerWindowScope}. Error={Error}",
                usePerWindowScope,
                error
            );
            var status = new InputScopeStatus(
                false,
                before.UsePerWindowScope,
                error,
                $"SystemParametersInfo SET failed. Error={error}"
            );
            return new InputScopeApplyResult(false, status, status.ErrorMessage ?? "Unknown error.");
        }

        var after = GetStatus();
        var success = after.Success && after.UsePerWindowScope == usePerWindowScope;
        var message = success
            ? "Input scope setting applied."
            : "Input scope verification mismatch after apply.";
        return new InputScopeApplyResult(success, after, message);
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(
        uint uiAction,
        uint uiParam,
        ref int pvParam,
        uint fWinIni
    );
}

internal readonly record struct InputScopeStatus(
    bool Success,
    bool UsePerWindowScope,
    int ErrorCode,
    string? ErrorMessage
);

internal readonly record struct InputScopeApplyResult(
    bool Success,
    InputScopeStatus Status,
    string Message
);
