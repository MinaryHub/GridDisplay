namespace GridDisplay.Models;

public enum CellKind
{
    Empty = 0,
    Browser = 1,
    Program = 2,
}

/// <summary>What one grid cell hosts. Persisted verbatim to state.json.</summary>
public sealed class CellConfig
{
    public CellKind Kind { get; set; } = CellKind.Empty;

    /// <summary>User label. Empty means "derive from the running content".</summary>
    public string Title { get; set; } = string.Empty;

    // --- Browser ---
    /// <summary>Last known address. Rewritten whenever the cell navigates, so a restart resumes where the user left off.</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>WebView2 profile. Distinct names give cells independent cookies/logins.</summary>
    public string ProfileName { get; set; } = string.Empty;
    /// <summary>
    /// Scale the page down so the cell shows the same layout a fullscreen cell would. On by
    /// default: a cell holds a fraction of the monitor, so at 100% only the page's top-left
    /// corner is visible. <see cref="ZoomFactor"/> takes over when this is off.
    /// </summary>
    public bool FitToCell { get; set; } = true;
    /// <summary>Fixed zoom, used only when <see cref="FitToCell"/> is off.</summary>
    public double ZoomFactor { get; set; } = 1.0;

    /// <summary>
    /// Keep this cell's session alive while the wall sits idle. Off by default — it pokes the
    /// hosted page on a timer, which is only ever wanted where a site logs the user out.
    /// </summary>
    public bool PreventLogout { get; set; }
    /// <summary>Seconds between keep-alive pulses. Clamped to 10..3600 by <see cref="GridPreset.Normalize"/>.</summary>
    public int KeepAliveSeconds { get; set; } = 60;

    // --- Program ---
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    /// <summary>Optional substring used to pick the right top-level window when the app opens several.</summary>
    public string WindowTitleMatch { get; set; } = string.Empty;
    /// <summary>Adopt an already-running window instead of starting a new process.</summary>
    public bool AttachToRunning { get; set; }
    /// <summary>Close the hosted process when the cell is released (otherwise it is set free as a normal window).</summary>
    public bool CloseOnRelease { get; set; } = true;

    public bool IsConfigured => Kind != CellKind.Empty;

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title;
            return Kind switch
            {
                CellKind.Browser => string.IsNullOrWhiteSpace(Url) ? "Browser" : Url,
                CellKind.Program => string.IsNullOrWhiteSpace(ExecutablePath)
                    ? "Program"
                    : Path.GetFileNameWithoutExtension(ExecutablePath),
                _ => "비어 있음",
            };
        }
    }

    public CellConfig Clone() => (CellConfig)MemberwiseClone();
}

/// <summary>A saved grid arrangement — the "set" the user can recall.</summary>
public sealed class GridPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "새 세트";
    public int Columns { get; set; } = 3;
    public int Rows { get; set; } = 2;
    public List<CellConfig> Cells { get; set; } = new();

    public int CellCount => Math.Max(1, Columns) * Math.Max(1, Rows);

    /// <summary>Grow/shrink the cell list, keeping every cell at its original (row, column).</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 1, 12);
        rows = Math.Clamp(rows, 1, 12);
        var old = Cells;
        int oldCols = Math.Max(1, Columns), oldRows = Math.Max(1, Rows);
        var next = new List<CellConfig>(columns * rows);
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                int oldIndex = r * oldCols + c;
                bool inOldGrid = r < oldRows && c < oldCols && oldIndex < old.Count;
                next.Add(inOldGrid ? old[oldIndex] : new CellConfig());
            }
        }
        Columns = columns;
        Rows = rows;
        Cells = next;
    }

    public void Normalize()
    {
        Columns = Math.Clamp(Columns, 1, 12);
        Rows = Math.Clamp(Rows, 1, 12);
        Cells ??= new List<CellConfig>();
        while (Cells.Count < CellCount) Cells.Add(new CellConfig());
        if (Cells.Count > CellCount) Cells.RemoveRange(CellCount, Cells.Count - CellCount);
        foreach (var c in Cells)
            c.KeepAliveSeconds = c.KeepAliveSeconds <= 0 ? 60 : Math.Clamp(c.KeepAliveSeconds, 10, 3600);
    }

    public GridPreset Duplicate(string name)
    {
        var copy = new GridPreset
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Columns = Columns,
            Rows = Rows,
            Cells = Cells.Select(c => c.Clone()).ToList(),
        };
        return copy;
    }

    public static GridPreset CreateDefault()
    {
        var p = new GridPreset { Name = "기본 세트", Columns = 3, Rows = 2 };
        p.Normalize();
        return p;
    }
}

/// <summary>
/// App-wide preferences, kept in settings.json. Sets are NOT in here: each one lives in its own
/// file so that two copies of the app showing different sets never write over each other. See
/// <see cref="Services.SetStore"/>.
/// </summary>
public class AppState
{
    /// <summary>
    /// Bumped when a stored setting needs correcting on load. Starts at 0 so a file written
    /// before this existed is recognised as old — a new default value could not be told apart
    /// from an absent one.
    /// </summary>
    public int SettingsVersion { get; set; }

    private const int CurrentSettingsVersion = 3;

    /// <summary>
    /// Set to open when no set is named on the command line. Only meaningful for a single copy;
    /// a wall that runs several passes --set to each one instead.
    /// </summary>
    public string LastSetId { get; set; } = string.Empty;

    /// <summary>
    /// Occupy the whole monitor with no chrome at startup. Off by default so a first run opens
    /// as an ordinary window: with no title bar there is no visible way to move, resize or close
    /// the app, which is the wrong first impression. F11 still toggles it, and the choice sticks.
    /// </summary>
    public bool StartFullScreen { get; set; }
    /// <summary>Stay above other windows while a cell is zoomed to the monitor.</summary>
    public bool TopMostInFullScreen { get; set; }
    /// <summary>Gap in pixels between cells; also the space the selection/hover outline is drawn in.</summary>
    public int CellGap { get; set; } = 8;
    /// <summary>Key that hands keyboard + mouse over to the hosted app (and takes it back).</summary>
    public Keys InputModeKey { get; set; } = Keys.F2;
    /// <summary>Let hidden browser cells release memory. Costs a few hundred ms when the cell is shown again.</summary>
    public bool SuspendHiddenCells { get; set; }

    /// <summary>
    /// Reload browser cells every N hours; 0 turns it off. A dashboard left open for weeks keeps
    /// growing whether or not this app leaks — the page's own scripts and DOM see to that — and a
    /// reload is the one cure that does not involve restarting the wall. Skipped while a cell is
    /// zoomed or taking input, so it never interrupts anyone.
    /// </summary>
    public int CellRecycleHours { get; set; } = 6;

    /// <summary>
    /// Ceiling on the JavaScript heap of each page, in MB; 0 removes it, and 0 is the default.
    ///
    /// It was tried at 256 and had to be withdrawn: pages whose *live* data grows past the
    /// ceiling do not collect their way back under it — V8 runs out and Windows kills the render
    /// process, which shows up as a dead cell. A ceiling only helps a page that accumulates
    /// garbage, and these accumulate data they still hold. Left here because it is the right
    /// tool for a page that does churn, but it has to be chosen deliberately, per site.
    /// </summary>
    public int BrowserHeapLimitMB { get; set; }

    /// <summary>
    /// Run the browser lean: Chromium's low-memory heuristics plus a standing request to trim.
    /// A wall is the case these were written for — the pages are watched, never touched, so
    /// trading a little responsiveness for memory is free.
    /// </summary>
    public bool BrowserMemorySaver { get; set; } = true;

    /// <summary>
    /// Where to look for a new version: an .appinstaller file or a small update.json. Empty means
    /// "ask Windows", which is enough when the app itself was installed from an .appinstaller.
    /// </summary>
    public string UpdateFeedUrl { get; set; } = string.Empty;
    /// <summary>Check once at startup and then every <see cref="UpdateCheckHours"/> hours.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>Hours between automatic checks. Clamped to 1..168 by <see cref="Normalize"/>.</summary>
    public int UpdateCheckHours { get; set; } = 24;
    /// <summary>Last successful check, so a restarted wall does not re-check straight away.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// Chromium switches for the shared browser process, built from the memory settings.
    ///
    /// These reach the browser only through whichever cell creates it first: every cell in this
    /// process shares one user-data folder and therefore one browser. Two copies of the app
    /// asking for different switches against that folder is an error, so every copy has to
    /// compute the same string — which is why this is derived from settings and nothing else.
    /// </summary>
    public string BrowserArguments()
    {
        var parts = new List<string>();
        if (BrowserMemorySaver) parts.Add("--enable-low-end-device-mode");
        if (BrowserHeapLimitMB > 0) parts.Add($"--js-flags=--max-old-space-size={BrowserHeapLimitMB}");
        return string.Join(' ', parts);
    }

    public void Normalize()
    {
        if (SettingsVersion < 3 && BrowserHeapLimitMB == 256)
        {
            // 1.0.2 shipped this on by default and it killed render processes on heavy pages.
            BrowserHeapLimitMB = 0;
        }

        if (SettingsVersion < 2 && CellRecycleHours == 24)
        {
            // 24 hours was the old default and it never fired on a wall that froze inside 13.
            // Only the untouched default is lowered; a deliberate 24 elsewhere is left alone.
            CellRecycleHours = 6;
        }
        SettingsVersion = Math.Max(SettingsVersion, 0);

        if (SettingsVersion < 1)
        {
            // Walls set up before this ran fullscreen from the first frame, which leaves no title
            // bar and so no visible way to move, resize or close the app. Hand the window back
            // once; F11 still switches, and now the choice is remembered.
            StartFullScreen = false;
            SettingsVersion = 1;
        }
        SettingsVersion = Math.Max(SettingsVersion, CurrentSettingsVersion);

        CellGap = Math.Clamp(CellGap, 0, 64);
        CellRecycleHours = Math.Clamp(CellRecycleHours, 0, 24 * 30);
        // 0 is "no ceiling"; anything else has to be big enough for a page to actually run in.
        if (BrowserHeapLimitMB != 0) BrowserHeapLimitMB = Math.Clamp(BrowserHeapLimitMB, 64, 4096);
        UpdateCheckHours = Math.Clamp(UpdateCheckHours <= 0 ? 24 : UpdateCheckHours, 1, 24 * 7);
        if (InputModeKey == Keys.None) InputModeKey = Keys.F2;
    }
}
