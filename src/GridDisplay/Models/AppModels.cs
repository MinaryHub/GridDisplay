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

/// <summary>Everything persisted between runs.</summary>
public sealed class AppState
{
    public List<GridPreset> Presets { get; set; } = new();
    public string? ActivePresetId { get; set; }

    /// <summary>Occupy the whole monitor with no chrome at startup.</summary>
    public bool StartFullScreen { get; set; } = true;
    /// <summary>Stay above other windows while a cell is zoomed to the monitor.</summary>
    public bool TopMostInFullScreen { get; set; }
    /// <summary>Gap in pixels between cells; also the space the selection/hover outline is drawn in.</summary>
    public int CellGap { get; set; } = 8;
    /// <summary>Key that hands keyboard + mouse over to the hosted app (and takes it back).</summary>
    public Keys InputModeKey { get; set; } = Keys.F2;
    /// <summary>Let hidden browser cells release memory. Costs a few hundred ms when the cell is shown again.</summary>
    public bool SuspendHiddenCells { get; set; }

    public GridPreset ActivePreset()
    {
        var found = Presets.FirstOrDefault(p => p.Id == ActivePresetId);
        if (found is null)
        {
            found = Presets.FirstOrDefault();
            if (found is null)
            {
                found = GridPreset.CreateDefault();
                Presets.Add(found);
            }
            ActivePresetId = found.Id;
        }
        return found;
    }

    public void Normalize()
    {
        Presets ??= new List<GridPreset>();
        if (Presets.Count == 0) Presets.Add(GridPreset.CreateDefault());
        foreach (var p in Presets) p.Normalize();
        CellGap = Math.Clamp(CellGap, 0, 64);
        if (InputModeKey == Keys.None) InputModeKey = Keys.F2;
    }
}
