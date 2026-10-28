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
                .WaitForWindowAsync(_process, processName, config.WindowTitleMatch, self, TimeSpan.FromSeconds(30), ct)
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

        Embed(target);
        _guard.Start();
        return null;
    }

    private void Embed(IntPtr hWnd)
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
        Fit(force: true);
        NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOWNA);
    }

    /// <summary>Resize the hosted window to fill the cell.</summary>
    public void Fit(bool force = false)
    {
        if (!IsAttached) return;
        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return;

        if (!force && NativeMethods.GetWindowRect(_child, out var rect))
        {
            var current = new Size(rect.Width, rect.Height);
            if (current == size) return;
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

        if (child != IntPtr.Zero && NativeMethods.IsWindow(child))
        {
            NativeMethods.SetParent(child, IntPtr.Zero);
            NativeMethods.SetWindowLongPtr(child, NativeMethods.GWL_STYLE, _originalStyle);
            NativeMethods.SetWindowLongPtr(child, NativeMethods.GWL_EXSTYLE, _originalExStyle);
            NativeMethods.SetWindowPos(child, NativeMethods.HWND_TOP, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOACTIVATE);

            // Adopted windows were never ours to close.
            if (closeProcess && !_adoptedExisting)
                NativeMethods.SendMessage(child, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            else
                NativeMethods.ShowWindow(child, NativeMethods.SW_SHOWNORMAL);
        }

        if (closeProcess && !_adoptedExisting && _process is not null)
        {
            try
            {
                if (!_process.WaitForExit(1500) && !_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { }
        }

        _process?.Dispose();
        _process = null;
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
