using GridDisplay.Services;

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
    /// <summary>
    /// The address this cell is configured to show. Set by the user and never written by the
    /// running page — a map that puts its coordinates in the address bar, or a site that bounces
    /// through a login, would otherwise erase the address that was typed here and leave no way
    /// back to it. Where the cell has wandered to is kept in <see cref="LastUrl"/>.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Where the cell actually was when it was last running, recorded as it navigates. Used to
    /// resume rather than to define — see <see cref="RememberLastUrl"/>.
    /// </summary>
    public string LastUrl { get; set; } = string.Empty;

    /// <summary>
    /// Reopen at <see cref="LastUrl"/> instead of <see cref="Url"/>. Off by default: a wall is
    /// configured once and expected to come up showing exactly the screens it was configured
    /// with, and a page that had bounced through a login or drifted to a sub-view made a restart
    /// the one thing that could not put it back. Turn it on for a cell someone browses from.
    /// </summary>
    public bool RememberLastUrl { get; set; }

    /// <summary>Where this cell should navigate when it starts.</summary>
    public string StartUrl =>
        RememberLastUrl && !string.IsNullOrWhiteSpace(LastUrl) ? LastUrl : Url;
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
    /// The user's own zoom (Ctrl+wheel, Ctrl +/-) on top of <see cref="FitToCell"/>, kept as a
    /// multiplier of the fit rather than as a factor: the fit changes every time the cell is
    /// zoomed to the monitor and back, and a multiplier survives that — and a restart — with the
    /// page laid out the same way. With fit off the user's zoom goes to <see cref="ZoomFactor"/>.
    /// </summary>
    public double ZoomScale { get; set; } = 1.0;

    /// <summary>
    /// Keep this cell's session alive while the wall sits idle. Off by default — it pokes the
    /// hosted page on a timer, which is only ever wanted where a site logs the user out.
    /// </summary>
    public bool PreventLogout { get; set; }
    /// <summary>Seconds between keep-alive pulses. Clamped to 10..3600 by <see cref="GridPreset.Normalize"/>.</summary>
    public int KeepAliveSeconds { get; set; } = 60;

    /// <summary>
    /// Stay signed in across a restart. Most intranet systems issue their login as a *session*
    /// cookie, which the browser drops when it closes, so every restart of the wall meant a login
    /// screen in every cell. This rewrites those cookies with an expiry so they reach the disk.
    /// Off by default: it keeps a credential on the machine, and it applies to the whole profile.
    /// Whether the server still honours the session after the restart is the server's call.
    /// </summary>
    public bool KeepSignedIn { get; set; }

    /// <summary>
    /// When the site sends the cell to a login page, go back to where it was once the login is
    /// done, instead of staying on whatever home page the site lands on afterwards.
    /// </summary>
    public bool ReturnAfterLogin { get; set; } = true;

    /// <summary>
    /// Keep the page running in the grid. Off by default: a grid cell shows a still picture of
    /// its page and the page sleeps, which is most of the CPU, GPU and memory a wall of
    /// dashboards spends. Zooming a cell always makes it live. Browser cells only.
    /// </summary>
    public bool LiveInGrid { get; set; }

    /// <summary>
    /// Reload this cell every N minutes; 0 leaves it to the wall-wide schedule. Here because one
    /// heavy page should not set the pace for the rest — a map that has to be refreshed every
    /// quarter hour is no reason to disturb twenty cells that are perfectly happy for a day.
    /// </summary>
    public int RefreshMinutes { get; set; }

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
                _ => L.T("비어 있음", "Empty"),
            };
        }
    }

    public CellConfig Clone() => (CellConfig)MemberwiseClone();
}

/// <summary>A saved grid arrangement — the "set" the user can recall.</summary>
public sealed class GridPreset
{
    /// <summary>
    /// Bumped when a stored cell setting needs correcting on load. Starts at 0 so a file written
    /// before this existed is recognised as old — the same reason
    /// <see cref="AppState.SettingsVersion"/> does it this way.
    /// </summary>
    public int SchemaVersion { get; set; }

    private const int CurrentSchemaVersion = 1;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = L.T("새 세트", "New set");
    public int Columns { get; set; } = 3;
    public int Rows { get; set; } = 2;
    public List<CellConfig> Cells { get; set; } = new();

    /// <summary>
    /// Position in the set list, 1-based. Lives in the set's own file rather than settings.json
    /// for the same reason the sets are split up. 0 is a file written before sets could be
    /// reordered; those tie and fall back to name order, which is how they used to be listed.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Areas of the grid shown as one large cell, as in a spreadsheet. A merge belongs to grid
    /// positions, not to content: <see cref="Cells"/> keeps one entry per position, the top-left
    /// one is the cell that shows, and the rest are hidden but kept, so splitting the merge brings
    /// back whatever they held. A file written before merges existed simply has none.
    /// </summary>
    public List<CellMerge> Merges { get; set; } = new();

    /// <summary>
    /// Relative column widths, one per column, set by dragging the gap between columns. Empty
    /// means equal widths — the case for every file written before sizes could be dragged, and
    /// kept empty until the user drags, so an untouched set stays exactly as it was.
    /// </summary>
    public List<double> ColumnWeights { get; set; } = new();

    /// <summary>Relative row heights, as <see cref="ColumnWeights"/>.</summary>
    public List<double> RowWeights { get; set; } = new();

    public int CellCount => Math.Max(1, Columns) * Math.Max(1, Rows);

    /// <summary>
    /// Where each column starts, as a fraction of the width the columns share: Columns + 1
    /// values running from 0 to 1. Edges rather than widths, so laying out from them never
    /// leaves a rounding gap.
    /// </summary>
    public double[] ColumnEdges() => Edges(ColumnWeights, Math.Max(1, Columns));

    /// <summary>Row edges, as <see cref="ColumnEdges"/>.</summary>
    public double[] RowEdges() => Edges(RowWeights, Math.Max(1, Rows));

    private static double[] Edges(List<double> weights, int count)
    {
        bool usable = weights.Count == count && weights.All(w => double.IsFinite(w) && w > 0);
        double total = usable ? weights.Sum() : count;
        var edges = new double[count + 1];
        for (int i = 0; i < count; i++)
            edges[i + 1] = edges[i] + (usable ? weights[i] : 1.0) / total;
        edges[count] = 1.0;
        return edges;
    }

    /// <summary>
    /// The weights in use, made concrete — dragging needs numbers to change. Equal widths come
    /// out as all 1s.
    /// </summary>
    public List<double> ConcreteWeights(bool columns)
    {
        var edges = columns ? ColumnEdges() : RowEdges();
        int n = edges.Length - 1;
        return Enumerable.Range(0, n).Select(i => (edges[i + 1] - edges[i]) * n).ToList();
    }

    // ------------------------------------------------------------------ insert / delete

    /// <summary>
    /// Insert a column so it becomes column <paramref name="at"/>; what was there and to its
    /// right moves one over. Returns, for each new position, the position it came from (-1 for
    /// the new, empty cells), so the wall can move its running cells instead of restarting them.
    /// A merge the new column cuts through grows to take it in, as in a spreadsheet.
    /// </summary>
    public int[] InsertColumn(int at)
    {
        int cols = Columns, rows = Rows;
        at = Math.Clamp(at, 0, cols);
        foreach (var m in Merges)
        {
            if (m.Column >= at) m.Column++;
            else if (m.Right >= at) m.ColumnSpan++;
        }
        InsertWeight(ColumnWeights, cols, at);
        return Reshape(cols + 1, rows, (r, c) => c == at ? -1 : r * cols + (c > at ? c - 1 : c));
    }

    /// <summary>Insert a row so it becomes row <paramref name="at"/>. See <see cref="InsertColumn"/>.</summary>
    public int[] InsertRow(int at)
    {
        int cols = Columns, rows = Rows;
        at = Math.Clamp(at, 0, rows);
        foreach (var m in Merges)
        {
            if (m.Row >= at) m.Row++;
            else if (m.Bottom >= at) m.RowSpan++;
        }
        InsertWeight(RowWeights, rows, at);
        return Reshape(cols, rows + 1, (r, c) => r == at ? -1 : (r > at ? r - 1 : r) * cols + c);
    }

    /// <summary>
    /// Delete column <paramref name="col"/> and whatever it held; columns to its right move
    /// over. A merge loses that column, and one left a single cell wide and tall stops being a
    /// merge (see <see cref="Normalize"/>). Returns the same map as <see cref="InsertColumn"/>.
    /// </summary>
    public int[] DeleteColumn(int col)
    {
        int cols = Columns, rows = Rows;
        foreach (var m in Merges)
        {
            if (m.Column > col) m.Column--;
            else if (m.Right >= col) m.ColumnSpan--;
        }
        Merges.RemoveAll(m => m.ColumnSpan < 1);
        if (ColumnWeights.Count == cols) ColumnWeights.RemoveAt(col);
        var map = Reshape(cols - 1, rows, (r, c) => r * cols + (c >= col ? c + 1 : c));
        Normalize();
        return map;
    }

    /// <summary>Delete row <paramref name="row"/>. See <see cref="DeleteColumn"/>.</summary>
    public int[] DeleteRow(int row)
    {
        int cols = Columns, rows = Rows;
        foreach (var m in Merges)
        {
            if (m.Row > row) m.Row--;
            else if (m.Bottom >= row) m.RowSpan--;
        }
        Merges.RemoveAll(m => m.RowSpan < 1);
        if (RowWeights.Count == rows) RowWeights.RemoveAt(row);
        var map = Reshape(cols, rows - 1, (r, c) => (r >= row ? r + 1 : r) * cols + c);
        Normalize();
        return map;
    }

    /// <summary>A new column or row starts as wide as its neighbour, so the others only shrink.</summary>
    private static void InsertWeight(List<double> weights, int count, int at)
    {
        if (weights.Count != count) return;   // equal sizes stay implicit
        weights.Insert(at, weights[Math.Min(at, count - 1)]);
    }

    /// <summary>Rebuild <see cref="Cells"/> at a new size from a (row, column) → old position map.</summary>
    private int[] Reshape(int cols, int rows, Func<int, int, int> oldOf)
    {
        var old = Cells;
        var map = new int[cols * rows];
        var next = new List<CellConfig>(cols * rows);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int from = oldOf(r, c);
                if (from >= old.Count) from = -1;
                map[r * cols + c] = from;
                next.Add(from >= 0 ? old[from] : new CellConfig());
            }
        Columns = cols;
        Rows = rows;
        Cells = next;
        return map;
    }

    /// <summary>The merge covering position <paramref name="index"/>, if any.</summary>
    public CellMerge? MergeAt(int index)
    {
        int cols = Math.Max(1, Columns);
        int r = index / cols, c = index % cols;
        return Merges.FirstOrDefault(m => m.Contains(r, c));
    }

    /// <summary>The position that shows for <paramref name="index"/>: itself, or its merge's top-left.</summary>
    public int OwnerOf(int index)
    {
        var m = MergeAt(index);
        return m is null ? index : m.Row * Math.Max(1, Columns) + m.Column;
    }

    /// <summary>Hidden under a merged cell.</summary>
    public bool IsCovered(int index) => OwnerOf(index) != index;

    /// <summary>
    /// Grow a block of positions until no merge straddles its edge, the way a spreadsheet
    /// selection snaps to whole merged areas. Inclusive row/column bounds.
    /// </summary>
    public (int Top, int Left, int Bottom, int Right) ExpandToMerges(int top, int left, int bottom, int right)
    {
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var m in Merges)
            {
                if (m.Row > bottom || m.Bottom < top || m.Column > right || m.Right < left) continue;
                if (m.Row < top) { top = m.Row; grew = true; }
                if (m.Column < left) { left = m.Column; grew = true; }
                if (m.Bottom > bottom) { bottom = m.Bottom; grew = true; }
                if (m.Right > right) { right = m.Right; grew = true; }
            }
        }
        return (top, left, bottom, right);
    }

    /// <summary>
    /// Make one cell of the block. Merges inside it are absorbed; the caller has already grown
    /// the block with <see cref="ExpandToMerges"/>, so none straddles the edge.
    /// </summary>
    public void Merge(int top, int left, int bottom, int right)
    {
        Merges.RemoveAll(m => m.Row >= top && m.Bottom <= bottom && m.Column >= left && m.Right <= right);
        if (bottom == top && right == left) return;
        Merges.Add(new CellMerge
        {
            Row = top,
            Column = left,
            RowSpan = bottom - top + 1,
            ColumnSpan = right - left + 1,
        });
    }

    /// <summary>Split the merge covering <paramref name="index"/>. Returns false when there was none.</summary>
    public bool Unmerge(int index)
    {
        var m = MergeAt(index);
        return m is not null && Merges.Remove(m);
    }

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
        // Positions keep their (row, column), so a merge still fits unless the shrink cut into it.
        Merges.RemoveAll(m => m.Bottom >= rows || m.Right >= columns);
        FitWeights(ColumnWeights, columns);
        FitWeights(RowWeights, rows);
    }

    /// <summary>Dragged sizes survive a resize: cut off with the columns, new ones as wide as the average.</summary>
    private static void FitWeights(List<double> weights, int count)
    {
        if (weights.Count == 0) return;
        double average = weights.Average();
        if (weights.Count > count) weights.RemoveRange(count, weights.Count - count);
        while (weights.Count < count) weights.Add(average);
    }

    /// <summary>Returns true when something was corrected, so the caller can write the file back.</summary>
    public bool Normalize()
    {
        int versionBefore = SchemaVersion;
        Columns = Math.Clamp(Columns, 1, 12);
        Rows = Math.Clamp(Rows, 1, 12);
        Cells ??= new List<CellConfig>();
        while (Cells.Count < CellCount) Cells.Add(new CellConfig());
        if (Cells.Count > CellCount) Cells.RemoveRange(CellCount, Cells.Count - CellCount);
        foreach (var c in Cells)
        {
            c.KeepAliveSeconds = c.KeepAliveSeconds <= 0 ? 60 : Math.Clamp(c.KeepAliveSeconds, 10, 3600);
            // 0 is "follow the wall"; a minute is the shortest that is not simply thrashing.
            if (c.RefreshMinutes != 0) c.RefreshMinutes = Math.Clamp(c.RefreshMinutes, 1, 24 * 60);
            c.ZoomScale = double.IsFinite(c.ZoomScale) && c.ZoomScale > 0 ? Math.Clamp(c.ZoomScale, 0.25, 4.0) : 1.0;

            // "Resume where it wandered to" used to be on for every cell, so a restart showed
            // the last page instead of the configured address. Files written back then cannot be
            // told apart from a deliberate choice, so the flag is cleared once, here.
            if (SchemaVersion < 1) c.RememberLastUrl = false;
        }

        // A merge that no longer fits the grid, covers a single position, or overlaps one kept
        // before it (a hand-edited file) is dropped; the cells it hid simply show again.
        Merges ??= new List<CellMerge>();
        var kept = new List<CellMerge>();
        foreach (var m in Merges)
        {
            if (m is null || m.Row < 0 || m.Column < 0 || m.RowSpan < 1 || m.ColumnSpan < 1) continue;
            if (m.Bottom >= Rows || m.Right >= Columns) continue;
            if (m.RowSpan * m.ColumnSpan < 2) continue;
            if (kept.Any(k => k.Overlaps(m))) continue;
            kept.Add(m);
        }
        Merges = kept;

        // Sizes that do not match the grid, or cannot be laid out, fall back to equal.
        ColumnWeights ??= new List<double>();
        RowWeights ??= new List<double>();
        if (ColumnWeights.Count != Columns || ColumnWeights.Any(w => !double.IsFinite(w) || w <= 0)) ColumnWeights.Clear();
        if (RowWeights.Count != Rows || RowWeights.Any(w => !double.IsFinite(w) || w <= 0)) RowWeights.Clear();

        SchemaVersion = Math.Max(SchemaVersion, CurrentSchemaVersion);
        return SchemaVersion != versionBefore;
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
            Merges = Merges.Select(m => m.Clone()).ToList(),
            ColumnWeights = ColumnWeights.ToList(),
            RowWeights = RowWeights.ToList(),
            // Carried over, or Normalize would read the copy as an old file and clear the flags.
            SchemaVersion = SchemaVersion,
        };
        return copy;
    }

    public static GridPreset CreateDefault()
    {
        var p = new GridPreset { Name = L.T("기본 세트", "Default set"), Columns = 3, Rows = 2 };
        p.Normalize();
        return p;
    }
}

/// <summary>A block of grid positions shown as one cell. Zero-based; spans count positions.</summary>
public sealed class CellMerge
{
    public int Row { get; set; }
    public int Column { get; set; }
    public int RowSpan { get; set; } = 1;
    public int ColumnSpan { get; set; } = 1;

    [System.Text.Json.Serialization.JsonIgnore] public int Bottom => Row + RowSpan - 1;
    [System.Text.Json.Serialization.JsonIgnore] public int Right => Column + ColumnSpan - 1;

    public bool Contains(int row, int column) =>
        row >= Row && row <= Bottom && column >= Column && column <= Right;

    public bool Overlaps(CellMerge other) =>
        Row <= other.Bottom && other.Row <= Bottom && Column <= other.Right && other.Column <= Right;

    public CellMerge Clone() => (CellMerge)MemberwiseClone();
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

    private const int CurrentSettingsVersion = 4;

    /// <summary>
    /// Set to open when no set is named on the command line. Only meaningful for a single copy;
    /// a wall that runs several passes --set to each one instead.
    /// </summary>
    public string LastSetId { get; set; } = string.Empty;

    /// <summary>
    /// UI language: "ko", "en", or empty to follow Windows. Read once at startup by
    /// <see cref="Services.L.UseSavedSetting"/>, so a change applies on the next start.
    /// </summary>
    public string Language { get; set; } = string.Empty;

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
    /// <summary>
    /// A thin strip under each cell in the grid with its number, name and what it is set to do
    /// (stay signed in, refresh, zoom). Taken from the cell's height, never its width, so fit
    /// zoom is unaffected; not drawn on a zoomed cell.
    /// </summary>
    public bool ShowCellLabels { get; set; } = true;
    /// <summary>
    /// Minutes between fresh pictures of a still grid cell (reload, settle, capture, sleep);
    /// 0 keeps the first picture until the cell is zoomed or refreshed by hand.
    /// </summary>
    public int StillRefreshMinutes { get; set; } = 5;
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
    /// Run the browser under Chromium's low-memory heuristics, and trim hidden cells harder.
    ///
    /// Off by default, having been on and withdrawn: low-end-device mode shrinks caches and
    /// disables code caching, which a page that streams a lot — a live map above all — feels as
    /// loading that keeps stopping. Worth turning on where memory is tighter than the pages are
    /// demanding, which is the opposite of the case it was added for.
    /// </summary>
    public bool BrowserMemorySaver { get; set; }

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
        if (SettingsVersion < 4 && BrowserMemorySaver)
        {
            // 1.0.2 through 1.0.5 shipped this on; it interrupts loading on streaming pages.
            BrowserMemorySaver = false;
        }

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
        StillRefreshMinutes = Math.Clamp(StillRefreshMinutes, 0, 24 * 60);
        // 0 is "no ceiling"; anything else has to be big enough for a page to actually run in.
        if (BrowserHeapLimitMB != 0) BrowserHeapLimitMB = Math.Clamp(BrowserHeapLimitMB, 64, 4096);
        UpdateCheckHours = Math.Clamp(UpdateCheckHours <= 0 ? 24 : UpdateCheckHours, 1, 24 * 7);
    }
}
