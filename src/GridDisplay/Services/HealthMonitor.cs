using System.Diagnostics;
using System.Text;
using GridDisplay.Interop;

namespace GridDisplay.Services;

/// <summary>
/// Writes a one-line resource snapshot on a slow timer.
///
/// A wall that has been up for three weeks either drifts or it does not, and "it feels slow
/// lately" is not something you can act on. Working set, handle count and GC heap over time turn
/// that into a trend you can read at a glance — and, just as usefully, rule out.
///
/// Deliberately boring: one line every half hour, a capped file, and every failure swallowed.
/// Diagnostics that can break the thing they observe are worse than none.
/// </summary>
public static class HealthMonitor
{
    private static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GridDisplay");

    private const long MaxLogBytes = 512 * 1024;

    private static readonly DateTime StartedUtc = DateTime.UtcNow;

    public static string LogPath => Path.Combine(LocalRoot, "health.log");

    /// <summary>How often <see cref="Sample"/> is worth calling. Long: this is a trend, not a graph.</summary>
    public static TimeSpan Interval => TimeSpan.FromMinutes(30);

    /// <summary>
    /// Record one snapshot. <paramref name="note"/> carries whatever the caller knows that the
    /// process itself does not — cell counts, the open set.
    /// </summary>
    public static void Sample(string note)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            self.Refresh();

            // Read the start time into a local first: touching StartedUtc is what runs the static
            // initialiser, and evaluating UtcNow before that made the very first sample negative.
            var started = StartedUtc;
            var uptime = DateTime.UtcNow - started;
            long working = self.WorkingSet64 / (1024 * 1024);
            long priv = self.PrivateMemorySize64 / (1024 * 1024);
            long managed = GC.GetTotalMemory(false) / (1024 * 1024);
            int handles = self.HandleCount;
            int threads = self.Threads.Count;

            // Browser cells live in WebView2's own processes, so this process's numbers alone
            // would hide the memory that actually matters. Counted machine-wide and labelled as
            // such: telling our renderers from another app's would need the command line of every
            // process, and when several walls share a machine the total is the number that
            // decides whether the machine starts thrashing anyway.
            int webview = 0;
            long webviewMb = 0;
            foreach (var p in Process.GetProcessesByName("msedgewebview2"))
            {
                using (p)
                {
                    webview++;
                    try { webviewMb += p.WorkingSet64 / (1024 * 1024); } catch { }
                }
            }

            // The symptom being chased is the whole desktop stalling, and that is usually memory
            // pressure rather than anything this process is doing on its own.
            string memory = "?";
            var status = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>(),
            };
            if (NativeMethods.GlobalMemoryStatusEx(ref status))
                memory = $"{status.ullAvailPhys / (1024 * 1024)}MB free / load {status.dwMemoryLoad}%";

            Write($"uptime {uptime.TotalHours:F1}h · working set {working}MB · private {priv}MB · managed heap {managed}MB · "
                + $"handles {handles} · threads {threads} · WebView2 (all) {webview} procs {webviewMb}MB · "
                + $"system memory {memory} · {note}");
        }
        catch
        {
        }
    }

    /// <summary>
    /// Record something that happened, as opposed to the periodic snapshot. Goes in the same file
    /// on purpose: when a cell dies, the next question is always what memory looked like around
    /// then, and interleaving the two makes that a matter of reading down the page.
    /// </summary>
    public static void Event(string message) => Write(message);

    private static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(LocalRoot);
            RollIfLarge();
            File.AppendAllText(
                LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] pid {Environment.ProcessId} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void RollIfLarge()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxLogBytes) return;
            var old = LogPath + ".1";
            if (File.Exists(old)) File.Delete(old);
            File.Move(LogPath, old);
        }
        catch { }
    }
}
