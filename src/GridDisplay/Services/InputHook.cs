using System.Runtime.InteropServices;
using GridDisplay.Interop;

namespace GridDisplay.Services;

/// <summary>
/// What the hook needs in order to decide, published by the UI thread and read by the hook
/// thread. Immutable and swapped wholesale, so a reader always sees a consistent set.
/// </summary>
public sealed class HitTestMap
{
    public static readonly HitTestMap Empty = new();

    public IntPtr FormHandle { get; init; }
    /// <summary>Screen coordinates of the whole grid area — the cheap first test.</summary>
    public Rectangle GridBounds { get; init; }
    /// <summary>Screen coordinates of each cell, index-aligned with the grid.</summary>
    public Rectangle[] Cells { get; init; } = Array.Empty<Rectangle>();
    /// <summary>False while closing or before a grid is up: clicks are ignored outright.</summary>
    public bool ClicksEnabled { get; init; }
    /// <summary>False while a cell is zoomed: there is no hover to track.</summary>
    public bool HoverEnabled { get; init; }

    /// <summary>
    /// Per cell, whether it has content — what decides if a press may become a drag. Snapshotted
    /// here so the hook never reads the live set, which the UI thread rebuilds underneath it.
    /// </summary>
    public bool[] Configured { get; init; } = Array.Empty<bool>();

    /// <summary>Pixels a press must travel to become a drag, resolved on the UI thread (it needs the DPI).</summary>
    public int DragThreshold { get; init; } = 4;
}

/// <summary>
/// Low-level keyboard + mouse hooks.
///
/// Why hooks instead of an overlay window: cells host windows owned by other processes, so they
/// consume input before any managed control can see it. The hooks let the grid stay in charge of
/// clicks and the input-mode key while still handing every other keystroke to the hosted app.
///
/// Two properties of these hooks decide the whole shape of this class:
///
///  1. The callback runs on the thread that installed the hook, driven by that thread's message
///     pump. It therefore gets its **own thread** here, not the UI thread. On the UI thread, any
///     pause — WebView2 start-up, a relayout, a modal dialog, a GC — stalls the callback, and
///     because of (2) that stalls input for the entire desktop, not just this app.
///  2. Every input event on the machine passes through every installed hook in turn, and waits.
///     Windows gives each one LowLevelHooksTimeout (300ms by default) before moving on. With
///     three copies of this app running, one slow callback is paid three times per event.
///
/// So the callback does as little as possible: it reads a couple of integers out of the event,
/// compares them against a cached <see cref="HitTestMap"/>, and hands anything real to the UI
/// thread. It never touches a Control — those belong to the UI thread and reaching for one here
/// would reintroduce exactly the coupling this design removes.
/// </summary>
public sealed class InputHook : IDisposable
{
    private readonly NativeMethods.HookProc _keyboardProc;
    private readonly NativeMethods.HookProc _mouseProc;
    private readonly HashSet<uint> _swallowedKeys = new();
    private readonly ManualResetEventSlim _installed = new(false);

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private volatile bool _disposed;

    private volatile HitTestMap _map = HitTestMap.Empty;
    private long _lastMoveTicks;

    /// <summary>Hover updates are throttled here rather than in the handler: a discarded event should cost nothing.</summary>
    private const long MoveThrottleMs = 30;

    public InputHook()
    {
        // Kept in fields: the delegates must outlive the P/Invoke or the GC will collect them.
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
    }

    /// <summary>Handlers receive the resolved cell index; the hit test happens on the hook thread.</summary>
    public Func<Keys, bool>? OnKeyDown { get; set; }
    public Func<MouseButtons, Point, int, bool>? OnMouseDown { get; set; }
    public Func<MouseButtons, Point, int, bool>? OnMouseUp { get; set; }
    public Action<Point, int>? OnMouseMove { get; set; }

    /// <summary>When false the mouse hook passes everything through (input mode).</summary>
    public volatile bool MouseCaptureEnabled = true;

    /// <summary>Publish a fresh hit-test snapshot. Called from the UI thread on every relayout.</summary>
    public void SetMap(HitTestMap map) => _map = map;

    /// <summary>The map the hook is currently testing against — for handlers that run on the hook thread.</summary>
    public HitTestMap Map => _map;

    public void Install()
    {
        if (_thread is not null) return;

        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "GridDisplay input hook",
        };
        _thread.Start();

        // Wait for the hooks to exist so the caller cannot race ahead of them, but never forever.
        _installed.Wait(TimeSpan.FromSeconds(5));
    }

    private void ThreadMain()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        InstallHooks();
        _installed.Set();

        // Windows 7 and later remove a low-level hook *silently* once a callback overruns
        // LowLevelHooksTimeout — and a blocking GC suspends this thread too, managed as it is.
        // There is no notification and no way to ask whether a hook is still installed, so on a
        // wall that runs for weeks view mode would just stop intercepting clicks one day. The
        // cure is to re-install periodically; it costs two system calls every few minutes.
        var renew = NativeMethods.SetTimer(IntPtr.Zero, UIntPtr.Zero, RenewIntervalMs, IntPtr.Zero);

        // A plain pump. Low-level hook callbacks are delivered while retrieving messages, so
        // there is nothing else for this thread to do — which is the entire point.
        while (true)
        {
            int result = NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0);
            if (result is 0 or -1) break;
            if (msg.message == NativeMethods.WM_TIMER && msg.hwnd == IntPtr.Zero && msg.wParam == (IntPtr)(long)(ulong)renew)
            {
                InstallHooks();
                continue;
            }
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        if (renew != UIntPtr.Zero) NativeMethods.KillTimer(IntPtr.Zero, renew);
        if (_keyboardHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
    }

    /// <summary>How often the hooks are re-installed; see <see cref="ThreadMain"/>.</summary>
    private const uint RenewIntervalMs = 5 * 60 * 1000;

    /// <summary>
    /// Install both hooks, replacing any already installed. The new ones go in before the old
    /// ones come out, so there is no instant in which input goes unwatched. Runs on the hook
    /// thread only — a hook belongs to the thread that installed it.
    /// </summary>
    private void InstallHooks()
    {
        var module = NativeMethods.GetModuleHandle(null);
        var keyboard = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        var mouse = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);

        // A failed re-install keeps the old hook rather than leaving none.
        if (keyboard != IntPtr.Zero)
        {
            if (_keyboardHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = keyboard;
        }
        if (mouse != IntPtr.Zero)
        {
            if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = mouse;
        }
    }

    // ------------------------------------------------------------------ callbacks

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != NativeMethods.HC_ACTION) return Next(_keyboardHook, nCode, wParam, lParam);

        long started = HookDiagnostics.Timestamp();
        try
        {
            int msg = (int)wParam;
            // Only vkCode is needed, and it is the first field — cheaper than marshalling the struct.
            uint vkCode = unchecked((uint)Marshal.ReadInt32(lParam));

            if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                var key = (Keys)vkCode | CurrentModifiers();
                try
                {
                    if (OnKeyDown?.Invoke(key) == true)
                    {
                        _swallowedKeys.Add(vkCode);
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
                if (_swallowedKeys.Remove(vkCode)) return 1;
            }
        }
        finally
        {
            HookDiagnostics.Record(started, "keyboard");
        }

        return Next(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != NativeMethods.HC_ACTION || !MouseCaptureEnabled)
            return Next(_mouseHook, nCode, wParam, lParam);

        int msg = (int)wParam;

        // Bail out on the message type before reading anything: most events are moves, and most
        // moves are thrown away.
        if (msg == NativeMethods.WM_MOUSEMOVE)
        {
            if (!_map.HoverEnabled) return Next(_mouseHook, nCode, wParam, lParam);

            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastMoveTicks) < MoveThrottleMs)
                return Next(_mouseHook, nCode, wParam, lParam);
            Interlocked.Exchange(ref _lastMoveTicks, now);
        }
        else if (!_map.ClicksEnabled)
        {
            return Next(_mouseHook, nCode, wParam, lParam);
        }

        long started = HookDiagnostics.Timestamp();
        try
        {
            // POINT is the first field of MSLLHOOKSTRUCT; two reads beat marshalling the struct.
            var pt = new Point(Marshal.ReadInt32(lParam, 0), Marshal.ReadInt32(lParam, 4));
            int index = HitTest(pt);

            switch (msg)
            {
                case NativeMethods.WM_MOUSEMOVE:
                    OnMouseMove?.Invoke(pt, index);
                    break;
                case NativeMethods.WM_LBUTTONDOWN:
                    if (OnMouseDown?.Invoke(MouseButtons.Left, pt, index) == true) return 1;
                    break;
                case NativeMethods.WM_LBUTTONUP:
                    if (OnMouseUp?.Invoke(MouseButtons.Left, pt, index) == true) return 1;
                    break;
                case NativeMethods.WM_RBUTTONDOWN:
                    if (OnMouseDown?.Invoke(MouseButtons.Right, pt, index) == true) return 1;
                    break;
                case NativeMethods.WM_RBUTTONUP:
                    if (OnMouseUp?.Invoke(MouseButtons.Right, pt, index) == true) return 1;
                    break;
                case NativeMethods.WM_MBUTTONDOWN:
                    if (OnMouseDown?.Invoke(MouseButtons.Middle, pt, index) == true) return 1;
                    break;
                case NativeMethods.WM_MBUTTONUP:
                    if (OnMouseUp?.Invoke(MouseButtons.Middle, pt, index) == true) return 1;
                    break;
            }
        }
        catch
        {
        }
        finally
        {
            HookDiagnostics.Record(started, "mouse");
        }

        return Next(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Which cell the pointer is over, or -1.
    ///
    /// Ordered cheapest-first on purpose. A rectangle comparison rules out every event that
    /// happens while the user is working somewhere else on the desktop, which is nearly all of
    /// them, before any call into the window manager.
    /// </summary>
    private int HitTest(Point screen)
    {
        var map = _map;
        if (!map.GridBounds.Contains(screen)) return -1;

        // Dialogs, context menus and other apps live under a different root — leave them alone.
        // This is what keeps the settings window and the cell menu clickable.
        var pt = new NativeMethods.POINT { X = screen.X, Y = screen.Y };
        var window = NativeMethods.WindowFromPoint(pt);
        if (window == IntPtr.Zero) return -1;
        if (NativeMethods.GetAncestor(window, NativeMethods.GA_ROOT) != map.FormHandle) return -1;

        var cells = map.Cells;
        for (int i = 0; i < cells.Length; i++)
            if (cells[i].Contains(screen)) return i;
        return -1;
    }

    private static IntPtr Next(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam) =>
        NativeMethods.CallNextHookEx(hook, nCode, wParam, lParam);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unhooking has to happen on the thread that installed the hooks, so ask the pump to end.
        if (_threadId != 0) NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _installed.Dispose();

        HookDiagnostics.WriteSummary();
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
