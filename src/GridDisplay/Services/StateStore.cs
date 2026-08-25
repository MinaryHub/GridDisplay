using System.Text.Json;
using System.Text.Json.Serialization;
using GridDisplay.Models;

namespace GridDisplay.Services;

/// <summary>
/// Loads/saves <see cref="AppState"/> to %APPDATA%\GridDisplay\state.json.
/// Writes are debounced and atomic: cells report every navigation, so naive saving would
/// hit the disk on each redirect and stutter the UI.
/// </summary>
public sealed class StateStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly System.Threading.Timer _debounce;
    private bool _disposed;

    public AppState State { get; private set; } = new();

    public static string RootDirectory => AppPaths.RoamingRoot;

    public static string StatePath => Path.Combine(RootDirectory, "state.json");

    /// <summary>
    /// WebView2 user-data folder. One folder keeps every browser cell of *this* copy in a single
    /// browser process; <see cref="AppPaths"/> keeps separate copies out of each other's, so one
    /// wall's browser cannot take the other two down with it.
    /// </summary>
    public static string WebViewDataDirectory => Path.Combine(AppPaths.LocalRoot, "WebView2");

    public StateStore()
    {
        _debounce = new System.Threading.Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Load()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                State = JsonSerializer.Deserialize<AppState>(json, JsonOptions) ?? new AppState();
            }
            else
            {
                State = new AppState();
            }
        }
        catch (Exception ex)
        {
            // A corrupt file must not block startup; keep it aside so the user can inspect it.
            TryBackupBrokenFile(ex);
            State = new AppState();
        }
        State.Normalize();
    }

    /// <summary>Queue a write ~1.5s out. Repeated calls collapse into one.</summary>
    public void SaveDeferred()
    {
        if (_disposed) return;
        try { _debounce.Change(1500, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    public void SaveNow()
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(RootDirectory);
                var json = JsonSerializer.Serialize(State, JsonOptions);
                var tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, StatePath, overwrite: true);
            }
        }
        catch
        {
            // Losing a save is survivable; crashing the display wall is not.
        }
    }

    private static void TryBackupBrokenFile(Exception ex)
    {
        try
        {
            var broken = StatePath + ".broken";
            File.Copy(StatePath, broken, overwrite: true);
            File.WriteAllText(broken + ".log", ex.ToString());
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Dispose();
        SaveNow();
    }
}
