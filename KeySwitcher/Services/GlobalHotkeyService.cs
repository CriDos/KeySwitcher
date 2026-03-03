using System.Runtime.InteropServices;
using KeySwitcher.Core;
using Serilog;

namespace KeySwitcher.Services;

internal sealed class GlobalHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int HookThreadJoinTimeoutMs = 2000;
    private const uint QueueNoFilterMin = 0;
    private const uint QueueNoFilterMax = 0;
    private const uint QueueNoRemove = 0;

    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;
    private const uint WmApplyHotkey = 0x8001;

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLMenu = 0xA4;
    private const int VkRMenu = 0xA5;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private readonly object _pendingLock = new();
    private readonly ManualResetEventSlim _threadReady = new(false);
    private readonly HashSet<int> _pressedKeys = [];
    private readonly Thread _hookThread;

    private HookProc? _hookProc;
    private IntPtr _hookHandle;
    private uint _hookThreadId;
    private bool _hookAvailable;
    private bool _disposed;

    private bool _hasPendingHotkey;
    private Hotkey _pendingHotkey;

    private bool _hasActiveHotkey;
    private Hotkey _activeHotkey;
    private bool _comboActive;

    public event EventHandler? HotkeyPressed;

    public GlobalHotkeyService()
    {
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "KeySwitcher.GlobalHotkey",
        };

        _hookThread.Start();
        _threadReady.Wait();
    }

    public bool Register(Hotkey hotkey)
    {
        if (_disposed)
        {
            return false;
        }

        if (!_hookAvailable)
        {
            Log.Error("Hotkey listener is unavailable; cannot register {Hotkey}.", hotkey);
            return false;
        }

        if (!hotkey.TryGetNativeRegistration(out _, out _))
        {
            Log.Warning("Rejected hotkey {Hotkey}: validation failed.", hotkey);
            return false;
        }

        lock (_pendingLock)
        {
            _pendingHotkey = hotkey;
            _hasPendingHotkey = true;
        }

        if (!PostThreadMessage(_hookThreadId, WmApplyHotkey, UIntPtr.Zero, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            Log.Error("PostThreadMessage(WmApplyHotkey) failed. Error={Error}", error);
            return false;
        }

        Log.Information("Queued global hotkey registration: {Hotkey}", hotkey);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hookThreadId != 0)
        {
            _ = PostThreadMessage(_hookThreadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
        }

        _hookThread.Join(HookThreadJoinTimeoutMs);
        _threadReady.Dispose();
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();

        // Ensure this thread has a message queue before other threads post messages to it.
        _ = PeekMessage(
            out _,
            IntPtr.Zero,
            QueueNoFilterMin,
            QueueNoFilterMax,
            QueueNoRemove
        );

        _hookProc = HookCallback;
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, IntPtr.Zero, 0);

        if (_hookHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            Log.Error("SetWindowsHookEx(WH_KEYBOARD_LL) failed. Error={Error}", error);
            _threadReady.Set();
            return;
        }

        _hookAvailable = true;
        Log.Information("Global keyboard hook installed on thread {ThreadId}.", _hookThreadId);
        _threadReady.Set();

        while (true)
        {
            var result = GetMessage(out var msg, IntPtr.Zero, QueueNoFilterMin, QueueNoFilterMax);
            if (result == 0)
            {
                break;
            }

            if (result < 0)
            {
                var error = Marshal.GetLastWin32Error();
                Log.Error("GetMessage failed in hotkey thread. Error={Error}", error);
                break;
            }

            if (msg.message == WmApplyHotkey)
            {
                ApplyPendingHotkey();
                continue;
            }

            _ = TranslateMessage(ref msg);
            _ = DispatchMessage(ref msg);
        }

        if (_hookHandle != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        _hookAvailable = false;
        Log.Information("Global keyboard hook stopped.");
    }

    private void ApplyPendingHotkey()
    {
        lock (_pendingLock)
        {
            if (!_hasPendingHotkey)
            {
                return;
            }

            _activeHotkey = _pendingHotkey;
            _hasActiveHotkey = true;
            _hasPendingHotkey = false;
        }

        _pressedKeys.Clear();
        _comboActive = false;

        Log.Information("Active global hotkey changed to: {Hotkey}", _activeHotkey);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _hasActiveHotkey)
        {
            var message = (uint)wParam.ToInt64();
            if (message is WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp)
            {
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                var isDown = message is WmKeyDown or WmSysKeyDown;
                ProcessKeyEvent((int)data.vkCode, isDown);
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void ProcessKeyEvent(int virtualKey, bool isDown)
    {
        if (isDown)
        {
            _pressedKeys.Add(virtualKey);
        }
        else
        {
            _pressedKeys.Remove(virtualKey);
        }

        if (!_hasActiveHotkey)
        {
            return;
        }

        var active = IsHotkeyActive(_activeHotkey);
        if (active && !_comboActive)
        {
            _comboActive = true;
            Log.Information("Global hotkey fired: {Hotkey}", _activeHotkey);
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
        else if (!active)
        {
            _comboActive = false;
        }
    }

    private bool IsHotkeyActive(Hotkey hotkey)
    {
        if (hotkey.BaseModifiers.HasFlag(HotkeyModifiers.Control) && !IsControlPressed())
        {
            return false;
        }

        if (hotkey.BaseModifiers.HasFlag(HotkeyModifiers.Alt) && !IsAltPressed())
        {
            return false;
        }

        if (hotkey.BaseModifiers.HasFlag(HotkeyModifiers.Shift) && !IsShiftPressed())
        {
            return false;
        }

        if (hotkey.BaseModifiers.HasFlag(HotkeyModifiers.Win) && !IsWinPressed())
        {
            return false;
        }

        return IsVirtualKeyPressed(hotkey.VirtualKey);
    }

    private bool IsVirtualKeyPressed(int virtualKey)
    {
        return virtualKey switch
        {
            VkShift => IsShiftPressed(),
            VkControl => IsControlPressed(),
            VkMenu => IsAltPressed(),
            VkLWin => _pressedKeys.Contains(VkLWin),
            VkRWin => _pressedKeys.Contains(VkRWin),
            _ => _pressedKeys.Contains(virtualKey),
        };
    }

    private bool IsShiftPressed()
    {
        return _pressedKeys.Contains(VkShift)
            || _pressedKeys.Contains(VkLShift)
            || _pressedKeys.Contains(VkRShift);
    }

    private bool IsControlPressed()
    {
        return _pressedKeys.Contains(VkControl)
            || _pressedKeys.Contains(VkLControl)
            || _pressedKeys.Contains(VkRControl);
    }

    private bool IsAltPressed()
    {
        return _pressedKeys.Contains(VkMenu)
            || _pressedKeys.Contains(VkLMenu)
            || _pressedKeys.Contains(VkRMenu);
    }

    private bool IsWinPressed()
    {
        return _pressedKeys.Contains(VkLWin) || _pressedKeys.Contains(VkRWin);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        HookProc callback,
        IntPtr hMod,
        uint threadId
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr wParam,
        IntPtr lParam
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(
        out Msg msg,
        IntPtr hWnd,
        uint minMessage,
        uint maxMessage
    );

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(
        out Msg msg,
        IntPtr hWnd,
        uint minMessage,
        uint maxMessage,
        uint removeMessage
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        UIntPtr wParam,
        IntPtr lParam
    );

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point point;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }
}
