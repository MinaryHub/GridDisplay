using System.Runtime.InteropServices;
using GridDisplay.Interop;

namespace GridDisplay.Services;

/// <summary>
/// Low-level keyboard + mouse hooks.
///
/// Why hooks instead of an overlay window: cells host windows owned by other processes, so
/// they consume input before any managed control can see it. The hooks let the grid stay in
/// charge of clicks and the input-mode key while still handing every other keystroke to the
/// hosted app once the user asks for it.
///
/// Callbacks return <c>true</c> to swallow the event. They run on the UI thread and must be
/// cheap — anything slow will drag on system-wide input latency.
/// </summary>
public sealed class InputHook : IDisposable
{
    private readonly NativeMethods.HookProc _keyboardProc;
    private readonly NativeMethods.HookProc _mouseProc;
    private readonly HashSet<uint> _swallowedKeys = new();
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private bool _disposed;

    public InputHook()
    {
        // Kept in fields: the delegates must outlive the P/Invoke or the GC will collect them.
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
    }

    public Func<Keys, bool>? OnKeyDown { get; set; }
    public Func<MouseButtons, Point, bool>? OnMouseDown { get; set; }
    public Func<MouseButtons, Point, bool>? OnMouseUp { get; set; }
    public Action<Point>? OnMouseMove { get; set; }

    /// <summary>When false the mouse hook passes everything through (input mode).</summary>
    public bool MouseCaptureEnabled { get; set; } = true;

    public void Install()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        var module = NativeMethods.GetModuleHandle(null);
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == NativeMethods.HC_ACTION)
        {
            int msg = (int)wParam;
            if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                var key = (Keys)data.vkCode | CurrentModifiers();
                try
                {
                    if (OnKeyDown?.Invoke(key) == true)
                    {
                        _swallowedKeys.Add(data.vkCode);
                        return 1;
                    }
                }
                catch
                {
                    // Never let a handler fault break global input.
                }
            }
            else if (msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                // Swallow the release of any key whose press we ate, so the hosted app never
                // sees a key-up without its key-down.
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                if (_swallowedKeys.Remove(data.vkCode)) return 1;
            }
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == NativeMethods.HC_ACTION && MouseCaptureEnabled)
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var pt = new Point(data.pt.X, data.pt.Y);
            int msg = (int)wParam;
            try
            {
                switch (msg)
                {
                    case NativeMethods.WM_MOUSEMOVE:
                        OnMouseMove?.Invoke(pt);
                        break;
                    case NativeMethods.WM_LBUTTONDOWN:
                        if (OnMouseDown?.Invoke(MouseButtons.Left, pt) == true) return 1;
                        break;
                    case NativeMethods.WM_LBUTTONUP:
                        if (OnMouseUp?.Invoke(MouseButtons.Left, pt) == true) return 1;
                        break;
                    case NativeMethods.WM_RBUTTONDOWN:
                        if (OnMouseDown?.Invoke(MouseButtons.Right, pt) == true) return 1;
                        break;
                    case NativeMethods.WM_RBUTTONUP:
                        if (OnMouseUp?.Invoke(MouseButtons.Right, pt) == true) return 1;
                        break;
                    case NativeMethods.WM_MBUTTONDOWN:
                        if (OnMouseDown?.Invoke(MouseButtons.Middle, pt) == true) return 1;
                        break;
                    case NativeMethods.WM_MBUTTONUP:
                        if (OnMouseUp?.Invoke(MouseButtons.Middle, pt) == true) return 1;
                        break;
                }
            }
            catch
            {
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_keyboardHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
    }

    /// <summary>
    /// Modifier state read with GetAsyncKeyState rather than <see cref="Control.ModifierKeys"/>:
    /// inside a low-level hook the key has not been dispatched to any message queue yet, so the
    /// queue-based API can report stale state.
    /// </summary>
    private static Keys CurrentModifiers()
    {
        var mods = Keys.None;
        if (Down(0x11)) mods |= Keys.Control;   // VK_CONTROL
        if (Down(0x10)) mods |= Keys.Shift;     // VK_SHIFT
        if (Down(0x12)) mods |= Keys.Alt;       // VK_MENU
        return mods;

        static bool Down(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
    }
}
