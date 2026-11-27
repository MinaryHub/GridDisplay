using System.ComponentModel;
using System.Diagnostics;
using GridDisplay.Interop;
using GridDisplay.Models;
using GridDisplay.Services;

namespace GridDisplay.Controls;

/// <summary>
/// Hosts an arbitrary Windows application inside a cell by reparenting its top-level window
/// (<c>SetParent</c>) and stripping the frame styles. Once it is a child window, the OS itself
/// confines it: "maximize" and most fullscreen paths can no longer exceed the parent's client
/// area, which is exactly the containment the grid needs.
/// </summary>
public sealed class NativeAppHost : Panel
{
    private readonly System.Windows.Forms.Timer _guard;
    private Process? _process;
    private IntPtr _child;
    private IntPtr _originalStyle;
    private IntPtr _originalExStyle;
    private bool _adoptedExisting;
    private bool _released;

    public NativeAppHost()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = Color.FromArgb(24, 24, 27);
        // A 1s watchdog is enough to correct apps that fight the imposed geometry, without
        // burning CPU on a display that is idle most of the time.
        _guard = new System.Windows.Forms.Timer { Interval = 1000 };
        _guard.Tick += (_, _) => Guard();
    }

    /// <summary>Raised when the hosted window disappears (app closed or crashed).</summary>
    public event Action<NativeAppHost>? ChildLost;

    public IntPtr ChildHandle => _child;

    public bool IsAttached => _child != IntPtr.Zero && NativeMethods.IsWindow(_child);

    /// <summary>Launches or adopts the app. Returns null on success, or a message to show in the cell.</summary>
    public async Task<string?> AttachAsync(CellConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.ExecutablePath) && !config.AttachToRunning)
            return L.T("실행 파일이 지정되지 않았습니다.", "No executable is set.");

        var processName = string.IsNullOrWhiteSpace(config.ExecutablePath)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(config.ExecutablePath);

        IntPtr self = TopLevelControl?.Handle ?? IntPtr.Zero;
        IntPtr target = IntPtr.Zero;

        if (config.AttachToRunning)
        {
            target = WindowFinder.FindByProcessName(processName, config.WindowTitleMatch, self);
            if (target == IntPtr.Zero)
                target = WindowFinder.FindByTitle(config.WindowTitleMatch, self);
        }

        if (target == IntPtr.Zero)
        {
            if (string.IsNullOrWhiteSpace(config.ExecutablePath))
                return L.T("실행 중인 창을 찾지 못했습니다.", "Could not find the running window.");

            // Every window that is already up is someone else's; see WaitForWindowAsync.
            var before = new HashSet<IntPtr>(WindowFinder.TopLevelWindows());
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = config.ExecutablePath,
                    Arguments = config.Arguments,
                    UseShellExecute = true,
                    WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory)
                        ? (Path.GetDirectoryName(config.ExecutablePath) ?? string.Empty)
                        : config.WorkingDirectory,
                };
                _process = Process.Start(psi);
            }
            catch (Win32Exception ex)
            {
                return L.T($"실행 실패: {ex.Message}", $"Failed to start: {ex.Message}");
            }
            catch (Exception ex)
            {
                return L.T($"실행 실패: {ex.Message}", $"Failed to start: {ex.Message}");
            }

            target = await WindowFinder
                .WaitForWindowAsync(_process, processName, config.WindowTitleMatch, self, before, TimeSpan.FromSeconds(30), ct)
                .ConfigureAwait(true);
        }
        else
        {
            _adoptedExisting = true;
        }

        if (target == IntPtr.Zero)
            return L.T("창을 찾지 못했습니다. 창 제목 일치 문자열을 지정해 보세요.", "Could not find the window. Try setting a window title match.");

        if (IsDisposed || !IsHandleCreated)
            return L.T("셀이 이미 해제되었습니다.", "The cell has already been released.");

        if (!Embed(target))
            return L.T("창을 셀에 넣지 못했습니다. 관리자 권한으로 실행되는 프로그램은 이 앱도 관리자 권한으로 실행해야 넣을 수 있습니다.",
                       "Could not place the window in the cell. A program running as administrator can only be embedded when this app runs as administrator too.");
        _guard.Start();
        return null;
    }

    /// <summary>Reparent and restyle <paramref name="hWnd"/>; false when Windows refused.</summary>
    private bool Embed(IntPtr hWnd)
    {
        _child = hWnd;
        _originalStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_STYLE);
        _originalExStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE);

        long style = (long)_originalStyle;
        if ((style & (NativeMethods.WS_MAXIMIZE | NativeMethods.WS_MINIMIZE)) != 0)
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);

        long newStyle = style;
        newStyle &= ~(NativeMethods.WS_POPUP | NativeMethods.WS_CAPTION | NativeMethods.WS_BORDER |
                      NativeMethods.WS_DLGFRAME | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU |
                      NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX |
                      NativeMethods.WS_MINIMIZE | NativeMethods.WS_MAXIMIZE);
        newStyle |= NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE;
        NativeMethods.SetWindowLongPtr(hWnd, NativeMethods.GWL_STYLE, (IntPtr)newStyle);

        long newEx = (long)_originalExStyle;
        newEx &= ~(NativeMethods.WS_EX_APPWINDOW | NativeMethods.WS_EX_DLGMODALFRAME |
                   NativeMethods.WS_EX_WINDOWEDGE | NativeMethods.WS_EX_CLIENTEDGE |
                   NativeMethods.WS_EX_STATICEDGE | NativeMethods.WS_EX_TOOLWINDOW);
        NativeMethods.SetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE, (IntPtr)newEx);

        NativeMethods.SetParent(hWnd, Handle);

        // A window of an elevated process refuses both the restyle and the reparent (UIPI), and
        // says so only through the return values. Unchecked, the cell reported success while the
        // program floated on the desktop and the watchdog nudged it forever.
        if (NativeMethods.GetParent(hWnd) != Handle)
        {
            NativeMethods.SetWindowLongPtr(hWnd, NativeMethods.GWL_STYLE, _originalStyle);
            NativeMethods.SetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE, _originalExStyle);
            _child = IntPtr.Zero;
            return false;
        }

        Fit(force: true);
        NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOWNA);
        return true;
    }

    /// <summary>Resize the hosted window to fill the cell.</summary>
    public void Fit(bool force = false)
    {
        if (!IsAttached) return;
        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return;

        // SetWindowPos on another process's window waits for it to answer; on a hung one that is
        // forever, and this runs from every relayout. Leave a hung window where it is.
        if (NativeMethods.IsHungAppWindow(_child)) return;

        if (!force && NativeMethods.GetWindowRect(_child, out var rect))
        {
            // Position too, not just size: an app that moves itself inside the cell keeps its
            // size and used to stay offset for good.
            var origin = PointToScreen(Point.Empty);
            if (rect.Width == size.Width && rect.Height == size.Height &&
                rect.Left == origin.X && rect.Top == origin.Y)
                return;
        }

        NativeMethods.SetWindowPos(
            _child, NativeMethods.HWND_TOP, 0, 0, size.Width, size.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_FRAMECHANGED);
    }

    public void FocusChild()
    {
        if (!IsAttached) return;
        NativeMethods.ForceFocus(_child);
    }

    private void Guard()
    {
        if (_released) return;
        if (_child == IntPtr.Zero) return;

        if (!NativeMethods.IsWindow(_child))
        {
            _guard.Stop();
            _child = IntPtr.Zero;
            ChildLost?.Invoke(this);
            return;
        }

        // Some apps re-assert their own geometry after startup; nudge them back into the cell.
        Fit();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Fit(force: true);
    }

    /// <summary>Detach the window. Either closes the app or restores it as a normal top-level window.</summary>
    public void Release(bool closeProcess)
    {
        if (_released) return;
        _released = true;
        _guard.Stop();

        var child = _child;
        _child = IntPtr.Zero;

        // Every call below waits for the hosted app to answer. Restarting a cell is exactly what
        // an operator does when that app has hung, so a hung window is not touched at all —
        // its process is ended below instead — rather than freezing the whole wall with it.
        bool alive = child != IntPtr.Zero && NativeMethods.IsWindow(child);
        bool hung = alive && NativeMethods.IsHungAppWindow(child);

        if (alive && !hung)
        {
            NativeMethods.SetParent(child, IntPtr.Zero);
            NativeMethods.SetWindowLongPtr(child, NativeMethods.GWL_STYLE, _originalStyle);
            NativeMethods.SetWindowLongPtr(child, NativeMethods.GWL_EXSTYLE, _originalExStyle);
            NativeMethods.SetWindowPos(child, NativeMethods.HWND_TOP, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOACTIVATE);

            // Adopted windows were never ours to close. Posted, not sent: SendMessage waits for
            // the app's WM_CLOSE handler, which may open a "save changes?" box and never return.
            if (closeProcess && !_adoptedExisting)
                NativeMethods.PostMessage(child, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            else
                NativeMethods.ShowWindow(child, NativeMethods.SW_SHOWNORMAL);
        }

        var process = _process;
        _process = null;
        if (process is null) return;

        if (!closeProcess || _adoptedExisting)
        {
            process.Dispose();
            return;
        }

        // Waiting for the exit used to happen right here, on the UI thread: 1.5s per program
        // cell, so switching away from a set with six of them froze the wall for nine seconds.
        // The wait now runs aside, and every cell's in parallel; shutdown collects them with
        // WaitForPendingCloses so nothing is left running when the wall exits.
        var closing = Task.Run(() =>
        {
            try
            {
                if (!process.WaitForExit(hung ? 0 : 1500) && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            finally
            {
                process.Dispose();
            }
        });
        lock (PendingCloses)
        {
            PendingCloses.RemoveAll(t => t.IsCompleted);
            PendingCloses.Add(closing);
        }
    }

    private static readonly List<Task> PendingCloses = new();

    /// <summary>Called on shutdown: let the programs being closed finish closing (or be ended).</summary>
    public static void WaitForPendingCloses(TimeSpan timeout)
    {
        Task[] pending;
        lock (PendingCloses) pending = PendingCloses.ToArray();
        if (pending.Length == 0) return;
        try { Task.WaitAll(pending, timeout); }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Release(closeProcess: false);
            _guard.Dispose();
        }
        base.Dispose(disposing);
    }
}
