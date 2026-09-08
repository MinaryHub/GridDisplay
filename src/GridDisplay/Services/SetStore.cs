using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GridDisplay.Models;

namespace GridDisplay.Services;

/// <summary>
/// Where sets and settings live on disk.
///
///   %APPDATA%\GridDisplay\settings.json   — app-wide preferences
///   %APPDATA%\GridDisplay\sets\*.json     — one file per set
///
/// One file per set is the whole point. The old layout kept every set in a single state.json,
/// and each running copy of the app wrote that file *whole* — so three walls with three sets
/// open would take turns overwriting one another's edits with whatever they happened to have
/// read at startup. Splitting the file means a copy only ever writes the set it has open, and
/// two copies showing different sets cannot collide at all.
///
/// Writes go straight to disk rather than through a delay. A wall that is power-cycled must not
/// lose the change that was made a second earlier, and a single set is small enough that saving
/// it on every edit costs nothing worth measuring.
/// </summary>
public sealed class SetStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly List<GridPreset> _sets = new();

    /// <summary>
    /// Browser cells report every navigation, and a redirect chain can fire several a second.
    /// Those coalesce; anything the user actually did is written immediately.
    /// </summary>
    private readonly System.Threading.Timer _urlDebounce;
    private GridPreset? _urlPending;

    private bool _disposed;

    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GridDisplay");

    public static string SetsDirectory { get; } = Path.Combine(RootDirectory, "sets");

    public static string SettingsPath { get; } = Path.Combine(RootDirectory, "settings.json");

    private static string LegacyStatePath { get; } = Path.Combine(RootDirectory, "state.json");

    /// <summary>Shared WebView2 user-data folder. One folder keeps every browser cell in a single browser process.</summary>
    public static string WebViewDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GridDisplay", "WebView2");

    public AppState Settings { get; private set; } = new();

    /// <summary>Every set on disk, in name order.</summary>
    public IReadOnlyList<GridPreset> Sets => _sets;

    public SetStore()
    {
        _urlDebounce = new System.Threading.Timer(_ => FlushUrls(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // ------------------------------------------------------------------ load

    public void Load()
    {
        Directory.CreateDirectory(SetsDirectory);
        MigrateLegacyStateIfPresent();

        // Write the file back whenever loading changed it — because it was missing, or because
        // Normalize corrected a setting on the way in. Leaving a migrated value only in memory
        // means the file still shows the old one, and the next person to read it concludes the
        // migration did not happen.
        bool rewrite = !File.Exists(SettingsPath);
        rewrite |= LoadSettings();
        if (rewrite) SaveSettings();

        LoadSets();

        if (_sets.Count == 0)
        {
            var first = GridPreset.CreateDefault();
            Add(first);
        }
    }

    /// <summary>Returns true when Normalize corrected something, so the caller can write it back.</summary>
    private bool LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                Settings = JsonSerializer.Deserialize<AppState>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppState();
        }
        catch
        {
            // A corrupt preferences file must not stop the wall coming up; defaults are fine.
            Settings = new AppState();
        }

        int before = Settings.SettingsVersion;
        Settings.Normalize();
        return Settings.SettingsVersion != before;
    }

    private void LoadSets()
    {
        _sets.Clear();
        _files.Clear();

        foreach (var path in Directory.EnumerateFiles(SetsDirectory, "*.json"))
        {
            try
            {
                var set = JsonSerializer.Deserialize<GridPreset>(File.ReadAllText(path), JsonOptions);
                if (set is null) continue;
                bool migrated = set.Normalize();
                if (string.IsNullOrWhiteSpace(set.Id)) set.Id = Guid.NewGuid().ToString("N");

                // Two files claiming one id would make saves ambiguous; the later one is a copy.
                if (_files.ContainsKey(set.Id)) set.Id = Guid.NewGuid().ToString("N");

                _sets.Add(set);
                _files[set.Id] = path;

                // Same reason the settings file is rewritten after a migration: a corrected
                // value that only lives in memory leaves the file still showing the old one.
                if (migrated) WriteAtomic(path, JsonSerializer.Serialize(set, JsonOptions));
            }
            catch
            {
                // One unreadable set must not hide the others.
            }
        }

        _sets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
    }

    /// <summary>
    /// Split a pre-split state.json into the new layout, once. The original is kept beside the
    /// new files rather than deleted — this runs unattended on a wall that is already in use.
    /// </summary>
    private void MigrateLegacyStateIfPresent()
    {
        try
        {
            if (!File.Exists(LegacyStatePath)) return;
            if (Directory.EnumerateFiles(SetsDirectory, "*.json").Any()) return;

            var legacy = JsonSerializer.Deserialize<LegacyState>(File.ReadAllText(LegacyStatePath), JsonOptions);
            if (legacy is null) return;

            if (legacy.Presets is not null)
            {
                foreach (var set in legacy.Presets)
                {
                    set.Normalize();
                    if (string.IsNullOrWhiteSpace(set.Id)) set.Id = Guid.NewGuid().ToString("N");
                    var path = ReserveFile(set);
                    WriteAtomic(path, JsonSerializer.Serialize(set, JsonOptions));
                }
            }

            WriteAtomic(SettingsPath, JsonSerializer.Serialize(legacy.ToSettings(), JsonOptions));
            File.Move(LegacyStatePath, LegacyStatePath + ".migrated", overwrite: true);
        }
        catch
        {
            // If this fails the app still starts, just with default settings and no sets.
        }
    }

    /// <summary>The old single-file shape, read once so its contents can be split up.</summary>
    private sealed class LegacyState : AppState
    {
        public List<GridPreset>? Presets { get; set; }
        public string? ActivePresetId { get; set; }

        public AppState ToSettings()
        {
            var s = new AppState
            {
                SettingsVersion = SettingsVersion,
                StartFullScreen = StartFullScreen,
                TopMostInFullScreen = TopMostInFullScreen,
                CellGap = CellGap,
                InputModeKey = InputModeKey,
                SuspendHiddenCells = SuspendHiddenCells,
                CellRecycleHours = CellRecycleHours,
                UpdateFeedUrl = UpdateFeedUrl,
                AutoCheckUpdates = AutoCheckUpdates,
                UpdateCheckHours = UpdateCheckHours,
                LastUpdateCheckUtc = LastUpdateCheckUtc,
                LastSetId = ActivePresetId ?? string.Empty,
            };
            s.Normalize();
            return s;
        }
    }

    // ------------------------------------------------------------------ save

    /// <summary>Write one set now. Used for everything the user did on purpose.</summary>
    public void SaveSet(GridPreset set)
    {
        if (_disposed || set is null) return;
        lock (_gate)
        {
            var path = _files.TryGetValue(set.Id, out var known) ? known : ReserveFile(set);
            path = RenameIfNameChanged(set, path);
            WriteAtomic(path, JsonSerializer.Serialize(set, JsonOptions));
        }
    }

    /// <summary>Write one set shortly. Used for browser addresses, which change on their own.</summary>
    public void SaveSetSoon(GridPreset set)
    {
        if (_disposed || set is null) return;
        _urlPending = set;
        try { _urlDebounce.Change(1000, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    private void FlushUrls()
    {
        var pending = _urlPending;
        _urlPending = null;
        if (pending is not null) SaveSet(pending);
    }

    public void SaveSettings()
    {
        if (_disposed) return;
        lock (_gate)
        {
            Settings.Normalize();
            WriteAtomic(SettingsPath, JsonSerializer.Serialize(Settings, JsonOptions));
        }
    }

    public GridPreset Add(GridPreset set)
    {
        set.Normalize();
        if (string.IsNullOrWhiteSpace(set.Id)) set.Id = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            _sets.Add(set);
            var path = ReserveFile(set);
            WriteAtomic(path, JsonSerializer.Serialize(set, JsonOptions));
        }
        return set;
    }

    public void Delete(GridPreset set)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(set.Id, out var path))
            {
                try { File.Delete(path); } catch { }
                _files.Remove(set.Id);
            }
            _sets.Remove(set);
        }
    }

    /// <summary>Re-read the folder, picking up sets another copy of the app created or removed.</summary>
    public void Refresh()
    {
        lock (_gate) LoadSets();
    }

    // ------------------------------------------------------------------ files

    /// <summary>
    /// Give a set a file named after it, so the folder can be read and backed up by hand. The id
    /// inside the file stays authoritative — the name is a convenience, not a key.
    /// </summary>
    private string ReserveFile(GridPreset set)
    {
        var baseName = SafeFileName(set.Name);
        var path = Path.Combine(SetsDirectory, baseName + ".json");
        int n = 2;
        while (File.Exists(path) || _files.ContainsValue(path))
            path = Path.Combine(SetsDirectory, $"{baseName} ({n++}).json");

        _files[set.Id] = path;
        return path;
    }

    private string RenameIfNameChanged(GridPreset set, string path)
    {
        var wanted = SafeFileName(set.Name);
        if (string.Equals(Path.GetFileNameWithoutExtension(path), wanted, StringComparison.OrdinalIgnoreCase))
            return path;

        var target = Path.Combine(SetsDirectory, wanted + ".json");
        if (File.Exists(target)) return path;   // someone else owns that name; keep ours

        try
        {
            if (File.Exists(path)) File.Move(path, target);
            _files[set.Id] = target;
            return target;
        }
        catch
        {
            return path;   // renaming is cosmetic; never lose the save over it
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "set";
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }

    /// <summary>
    /// Write through a temp file, forced to the platter, then swap it in. Flush(true) is the part
    /// that matters here: without it Windows can hold the bytes in cache for seconds, which is
    /// exactly long enough for a wall that freezes to come back with the previous contents.
    /// </summary>
    private static void WriteAtomic(string path, string contents)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            var bytes = new UTF8Encoding(false).GetBytes(contents);

            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Losing a save is survivable; taking the wall down over it is not.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FlushUrls();
        _urlDebounce.Dispose();
        SaveSettings();
    }
}
