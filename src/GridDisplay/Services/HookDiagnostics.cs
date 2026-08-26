using System.Diagnostics;
using System.Text;

namespace GridDisplay.Services;

/// <summary>
/// Times the low-level hook callbacks and writes a line when one runs long.
///
/// Why this exists: a slow hook callback does not slow this app down, it slows the whole desktop
/// down, because Windows holds every input event until each installed hook returns (or until
/// LowLevelHooksTimeout, 300ms by default, expires). That makes "the machine froze" and "our
/// callback was slow" impossible to tell apart by eye. This turns it into a number.
///
/// The cost of measuring is two timestamp reads per input event, and the disk is only touched
/// when something is actually slow — a wall that behaves writes nothing for weeks.
/// </summary>
public static class HookDiagnostics
{
    private static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GridDisplay");

    /// <summary>A callback slower than this is worth knowing about; typical is a few microseconds.</summary>
    private const double SlowMilliseconds = 15.0;

    /// <summary>One line per interval at most: a storm of slow events must not become a storm of writes.</summary>
    private static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(30);

    private const long MaxLogBytes = 512 * 1024;

    private static readonly object Gate = new();
    private static long _samples;
    private static long _slow;
    private static double _worstMs;
    private static string _worstWhat = string.Empty;
    private static DateTime _lastWriteUtc = DateTime.MinValue;

    public static string LogPath => Path.Combine(LocalRoot, "input-hook.log");

    public static long Timestamp() => Stopwatch.GetTimestamp();

    /// <summary>Record one callback. <paramref name="what"/> names the branch, for the log line.</summary>
    public static void Record(long startTimestamp, string what)
    {
        double ms = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

        lock (Gate)
        {
            _samples++;
            if (ms > _worstMs)
            {
                _worstMs = ms;
                _worstWhat = what;
            }
            if (ms < SlowMilliseconds) return;

            _slow++;
            var now = DateTime.UtcNow;
            if (now - _lastWriteUtc < WriteInterval) return;
            _lastWriteUtc = now;
        }

        Write($"느린 입력 후크 {ms:F1}ms ({what}) · 누적 느림 {_slow}회 / 표본 {_samples}회 · 최대 {_worstMs:F1}ms ({_worstWhat})");
    }

    /// <summary>Called on shutdown so a quiet run still leaves its worst case behind.</summary>
    public static void WriteSummary()
    {
        long samples, slow;
        double worst;
        string worstWhat;
        lock (Gate)
        {
            samples = _samples;
            slow = _slow;
            worst = _worstMs;
            worstWhat = _worstWhat;
        }

        if (samples == 0) return;
        Write($"종료 요약 · 표본 {samples}회 · 느림({SlowMilliseconds}ms 초과) {slow}회 · 최대 {worst:F1}ms ({worstWhat})");
    }

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
            // Diagnostics must never be the reason the wall stops.
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
