using System.Diagnostics;

namespace GridDisplay.Interop;

/// <summary>
/// Locates the top-level window that should be pulled into a cell.
/// Launchers, single-instance apps and splash screens all mean "the process we started"
/// is often not "the process that owns the window", so several strategies are tried.
/// </summary>
internal static class WindowFinder
{
    public static IEnumerable<IntPtr> TopLevelWindows()
    {
        var result = new List<IntPtr>();
        NativeMethods.EnumWindows((h, _) =>
        {
            result.Add(h);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static bool IsUsableWindow(IntPtr hWnd)
    {
        if (!NativeMethods.IsWindow(hWnd) || !NativeMethods.IsWindowVisible(hWnd)) return false;
        if (!NativeMethods.GetWindowRect(hWnd, out var r)) return false;
        // Skip 0-sized helper windows and message-only shells.
        return r.Width > 80 && r.Height > 60;
    }

    public static IntPtr FindByTitle(string titleFragment, IntPtr exclude)
    {
        if (string.IsNullOrWhiteSpace(titleFragment)) return IntPtr.Zero;
        foreach (var h in TopLevelWindows())
        {
            if (h == exclude || !IsUsableWindow(h)) continue;
            var title = NativeMethods.GetWindowTitle(h);
            if (title.Contains(titleFragment, StringComparison.OrdinalIgnoreCase)) return h;
        }
        return IntPtr.Zero;
    }

    public static IntPtr FindByProcessName(string processName, string titleFragment, IntPtr exclude)
    {
        if (string.IsNullOrWhiteSpace(processName)) return IntPtr.Zero;
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(processName))
        {
            pids.Add((uint)p.Id);
            p.Dispose();
        }
        if (pids.Count == 0) return IntPtr.Zero;

        foreach (var h in TopLevelWindows())
        {
            if (h == exclude || !IsUsableWindow(h)) continue;
            NativeMethods.GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid)) continue;
            if (!string.IsNullOrWhiteSpace(titleFragment) &&
                !NativeMethods.GetWindowTitle(h).Contains(titleFragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return h;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Poll until the launched app shows a window. Returns <see cref="IntPtr.Zero"/> on timeout.
    /// Polling (rather than a shell hook) keeps this contained and cancellable.
    /// </summary>
    public static async Task<IntPtr> WaitForWindowAsync(
        Process? process,
        string processName,
        string titleFragment,
        IntPtr exclude,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (process is not null)
            {
                try
                {
                    process.Refresh();
                    if (!process.HasExited)
                    {
                        var h = process.MainWindowHandle;
                        if (h != IntPtr.Zero && h != exclude && IsUsableWindow(h))
                        {
                            if (string.IsNullOrWhiteSpace(titleFragment) ||
                                NativeMethods.GetWindowTitle(h).Contains(titleFragment, StringComparison.OrdinalIgnoreCase))
                            {
                                return h;
                            }
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process object no longer usable — fall through to name-based lookup.
                }
            }

            var byName = FindByProcessName(processName, titleFragment, exclude);
            if (byName != IntPtr.Zero) return byName;

            if (!string.IsNullOrWhiteSpace(titleFragment))
            {
                var byTitle = FindByTitle(titleFragment, exclude);
                if (byTitle != IntPtr.Zero) return byTitle;
            }

            await Task.Delay(150, ct).ConfigureAwait(true);
        }
        return IntPtr.Zero;
    }
}
