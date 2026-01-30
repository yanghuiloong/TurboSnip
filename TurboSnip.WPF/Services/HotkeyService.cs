using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace TurboSnip.WPF.Services;

public interface IHotkeyService
{
    bool Register(ModifierKeys modifier, Key key, Action callback);
    void Unregister();
}

public partial class HotkeyService : IHotkeyService, IDisposable
{
    private const int HOTKEY_ID = 9000;
    private const int WM_HOTKEY = 0x0312;

    private HwndSource? _source;
    private Action? _callback;
    private bool _isRegistered;

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    public bool Register(ModifierKeys modifier, Key key, Action callback)
    {
        Unregister(); // Clear previous

        _callback = callback;

        // Create a message-only window for hotkey processing if not attached to main window
        // For simplicity in WPF, we usually attach to the MainWindow handle or create a dummy HwndSource.
        // We'll create a generic HwndSource.

        if (_source == null)
        {
            var parameters = new HwndSourceParameters("TurboSnipHotkeyListener")
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                ParentWindow = IntPtr.Zero // Message-only if HWND_MESSAGE (-3) ? Or just use 0.
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        uint mod = (uint)modifier; // WPF ModifierKeys maps directly to Win32 modifiers (Alt=1, Ctrl=2, Shift=4, Win=8)

        // Note: KeyInterop.VirtualKeyFromKey returns 0 for some keys if not careful, but Alt (Key.System) + Q (Key.Q) works.
        // ModifierKeys.Alt = 1.

        _isRegistered = RegisterHotKey(_source.Handle, HOTKEY_ID, mod, vk);
        return _isRegistered;
    }

    public void Unregister()
    {
        if (_isRegistered && _source != null)
        {
            UnregisterHotKey(_source.Handle, HOTKEY_ID);
            _isRegistered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            _callback?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
