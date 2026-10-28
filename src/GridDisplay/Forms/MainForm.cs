using GridDisplay.Controls;
using GridDisplay.Interop;
using GridDisplay.Models;
using GridDisplay.Services;

namespace GridDisplay.Forms;

/// <summary>
/// The display wall itself.
///
/// Two interaction states:
///   view mode  — the grid owns the mouse. Clicking a cell zooms it to the whole monitor.
///                Hosted apps never see these clicks.
///   input mode — entered by zooming a cell. The mouse hook stands down and every keystroke
///                except Esc goes to the app; Esc leaves input mode and the zoom together.
///
/// Both states are enforced with low-level hooks rather than overlay windows, because the cells
/// host windows owned by other processes that would otherwise consume the input first.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color BackgroundColor = Color.FromArgb(14, 14, 16);
    private static readonly Color AccentColor = Color.FromArgb(70, 160, 255);

    private readonly SetStore _store = new();
    private readonly InputHook _hook = new();
    private readonly List<CellHost> _cells = new();
    private readonly UpdateService _updates;

    private readonly Label _banner = new();
    private readonly System.Windows.Forms.Timer _bannerTimer = new();
    private readonly System.Windows.Forms.Timer _updateTimer = new();
    private readonly System.Windows.Forms.Timer _maintenanceTimer = new();
    private DateTime _lastHealthUtc = DateTime.UtcNow;
    private DateTime _lastRecycleUtc = DateTime.UtcNow;
    private readonly ContextMenuStrip _menu = new();

    /// <summary>
    /// Kept as a field because its caption and enabled state depend on the cell the menu was
    /// opened over, which is only known when it opens.
    /// </summary>
    private ToolStripMenuItem _adoptUrlItem = null!;
    /// <summary>Shown only on a cell the user has zoomed with Ctrl+wheel.</summary>
    private ToolStripMenuItem _resetZoomItem = null!;

    private GridPreset _preset = null!;
    private CancellationTokenSource? _applyCts;

    // Written on the UI thread, read by the hook thread (and the reverse for the drag/hover
    // fields). All are int- or bool-sized, so volatile is enough to keep the two views in step —
    // see InputHook for why the callback runs on a thread of its own.
    private volatile int _zoomed = -1;
    private volatile int _selected;
    private volatile int _hover = -1;
    private volatile int _dragFrom = -1;
    private volatile int _dragOver = -1;
    private volatile bool _interactive;
    private volatile bool _closing;

    /// <summary>
    /// The window handle, cached for the hook thread. <c>Control.Handle</c> is a Control member
    /// and must not be touched from there — and going fullscreen changes FormBorderStyle, which
    /// makes WinForms destroy and recreate the handle underneath. Reading it off-thread across
    /// that recreation threw, the hook swallowed the exception, and every key stopped working
    /// the moment a cell was zoomed.
    /// </summary>
    private volatile IntPtr _formHandle;

    /// <summary>The cell menu is up. Set on the UI thread, read by the hook.</summary>
    private volatile bool _menuOpen;

    // Touched only by the hook thread, in press -> move -> release order.
    private bool _swallowRelease;
    private int _pressedCell = -1;
    private Point _pressPoint;
    private MouseButtons _pressButton;

    private int _menuCell = -1;
    private bool _fullScreen;

    private Rectangle _restoreBounds;
    private FormWindowState _restoreState = FormWindowState.Normal;
    private FormBorderStyle _restoreBorder = FormBorderStyle.Sizable;

    public MainForm()
    {
        _store.Load();

        _updates = new UpdateService(S);

        // The running version belongs in the title bar: a wall is usually read over
        // somebody's shoulder, and this is the one place it shows without opening a dialog.
        Text = $"Grid Display {AppVersion.Display}";
        BackColor = BackgroundColor;
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        ResizeRedraw = true;
        KeyPreview = true;

        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        Bounds = Rectangle.Inflate(screen.WorkingArea, -60, -60);

        BuildBanner();
        BuildMenu();
        BuildCloseZoom();

        _hook.OnKeyDown = OnGlobalKeyDown;
        _hook.OnMouseDown = OnGlobalMouseDown;
        _hook.OnMouseUp = OnGlobalMouseUp;
        _hook.OnMouseMove = OnGlobalMouseMove;

        Load += async (_, _) => await OnLoadedAsync().ConfigureAwait(true);
        Resize += (_, _) => Relayout();
    }

    private AppState S => _store.Settings;

    /// <summary>
    /// Which set to open. A wall that runs several copies names one per shortcut
    /// (<c>GridDisplay.exe --set "기업 관제"</c>); a single copy reopens whatever it had last.
    /// Naming it also stops that copy from writing the shared "last set" preference, which is
    /// the one thing three copies would otherwise still fight over.
    /// </summary>
    private GridPreset StartingSet()
    {
        var wanted = Program.RequestedSet;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var named = _store.Sets.FirstOrDefault(
                p => string.Equals(p.Name, wanted, StringComparison.CurrentCultureIgnoreCase));
            if (named is not null) { _setFromCommandLine = true; return named; }

            // Named a set that does not exist yet: make it rather than silently opening another.
            var created = _store.Add(new GridPreset { Name = wanted.Trim() });
            _setFromCommandLine = true;
            return created;
        }

        return _store.Sets.FirstOrDefault(p => p.Id == S.LastSetId) ?? _store.Sets[0];
    }

    private bool _setFromCommandLine;

    // ------------------------------------------------------------------ startup

    private async Task OnLoadedAsync()
    {
        // Whatever a previous update left behind, before anything else claims the disk.
        UpdateInstaller.CleanupStaging();

        // Before any cell exists: the first browser cell to start is the one that configures the
        // shared browser process, and it reads these.
        CellHost.BrowserArguments = S.BrowserArguments();
        CellHost.TrimBrowserMemory = S.BrowserMemorySaver;
        CellHost.StillRefreshMinutes = S.StillRefreshMinutes;

        _hook.Install();
        if (S.StartFullScreen) SetFullScreen(true);
        await LoadPresetAsync(StartingSet()).ConfigureAwait(true);
        StartUpdateWatch();
        StartMaintenance();
    }

    /// <summary>Swap the whole grid to <paramref name="preset"/> and bring its cells up.</summary>
    private async Task LoadPresetAsync(GridPreset preset)
    {
        _applyCts?.Cancel();
        _applyCts = new CancellationTokenSource();
        var ct = _applyCts.Token;

        preset.Normalize();
        _preset = preset;
        if (!_setFromCommandLine)
        {
            S.LastSetId = preset.Id;
            _store.SaveSettings();
        }

        if (_interactive) SetInteractive(false);
        _zoomed = -1;
        _hover = -1;

        SuspendLayout();
        foreach (var cell in _cells)
        {
            cell.StateChanged -= OnCellStateChanged;
            cell.UserZoomed -= OnCellUserZoom;
            cell.Release();
            Controls.Remove(cell);
            cell.Dispose();
        }
        _cells.Clear();

        for (int i = 0; i < preset.CellCount; i++)
        {
            var host = new CellHost(i, preset.Cells[i]);
            // Never reload the cell being read or typed into; it takes the next slot instead.
            host.RefreshAllowed = () => !_interactive && _zoomed != host.Index;
            host.StateChanged += OnCellStateChanged;
            host.UserZoomed += OnCellUserZoom;
            _cells.Add(host);
            Controls.Add(host);
        }
        ResumeLayout(false);

        _selected = Math.Clamp(_selected, 0, Math.Max(0, _cells.Count - 1));
        Relayout();
        Invalidate();
        UpdateTitle();
        _store.SaveSet(_preset);

        // Content comes up staggered: the window is already painted and clickable, and a
        // simultaneous launch of six browsers/processes would spike CPU and stall input.
        var pending = new List<Task>();
        for (int i = 0; i < _cells.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            pending.Add(_cells[i].ApplyAsync(preset.Cells[i]));
            try { await Task.Delay(120, ct).ConfigureAwait(true); }
            catch (OperationCanceledException) { break; }
        }

        try { await Task.WhenAll(pending).ConfigureAwait(true); }
        catch { /* per-cell failures are reported inside the cell */ }
    }

    private void OnCellStateChanged(CellHost cell)
    {
        // A browser reporting its address, not a user edit — let a redirect chain settle.
        _store.SaveSetSoon(_preset);
        if (cell.Index == _zoomed) UpdateTitle();
        // The name strip shows the page title and the zoom, both of which this reports.
        else InvalidateCellFrame(cell.Index);
    }

    private void OnCellUserZoom(CellHost cell)
    {
        var c = cell.Config;
        double factor = c.FitToCell ? c.ZoomScale : c.ZoomFactor;
        ShowBanner(L.T($"셀 {cell.Index + 1} 배율 {factor * 100:0}% · 다음에 켜도 유지됩니다 (우클릭 › 배율 원래대로)",
                       $"Cell {cell.Index + 1} zoom {factor * 100:0}% · kept after a restart (right-click › Reset zoom)"));
    }

    // ------------------------------------------------------------------ layout

    private Rectangle GridArea()
    {
        var r = ClientRectangle;
        return r;
    }

    /// <summary>
    /// Height of the name strip under each grid cell, 0 when zoomed or turned off. Height, never
    /// width: fit zoom is cell width over grid-area width, so only height is free. Settled by
    /// <see cref="Relayout"/>, which also shrinks it on a grid too dense to spare 24px.
    /// </summary>
    private int LabelStripHeight => _zoomed < 0 ? _labelStrip : 0;
    private int _labelStrip;

    /// <summary>
    /// The cell plus its name strip — the whole slot it owns. Frames, hit testing and
    /// invalidation use this, so a click on the strip counts as a click on the cell.
    /// </summary>
    private Rectangle CellSlot(int index)
    {
        var b = _cells[index].Bounds;
        return new Rectangle(b.Left, b.Top, b.Width, b.Height + LabelStripHeight);
    }

    private void Relayout()
    {
        if (_cells.Count == 0 || _preset is null) return;

        var area = GridArea();
        int gap = Math.Max(0, S.CellGap);

        SuspendLayout();
        if (_zoomed >= 0 && _zoomed < _cells.Count)
        {
            var zoomArea = area;

            for (int i = 0; i < _cells.Count; i++)
            {
                bool on = i == _zoomed;
                if (on) _cells[i].Bounds = zoomArea;
                if (_cells[i].Visible != on) _cells[i].Visible = on;
                if (S.SuspendHiddenCells) _cells[i].SetSuspended(!on);
            }
        }
        else
        {
            int cols = _preset.Columns, rows = _preset.Rows;
            int innerW = Math.Max(0, area.Width - gap * (cols + 1));
            int innerH = Math.Max(0, area.Height - gap * (rows + 1));
            _labelStrip = S.ShowCellLabels ? Math.Min(Scaled(24), innerH / rows / 3) : 0;

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    if (i >= _cells.Count) continue;
                    int left = area.Left + gap * (c + 1) + innerW * c / cols;
                    int right = area.Left + gap * (c + 1) + innerW * (c + 1) / cols;
                    int top = area.Top + gap * (r + 1) + innerH * r / rows;
                    int bottom = area.Top + gap * (r + 1) + innerH * (r + 1) / rows;
                    _cells[i].Bounds = Rectangle.FromLTRB(left, top, right, bottom - _labelStrip);
                    if (!_cells[i].Visible) _cells[i].Visible = true;
                    if (S.SuspendHiddenCells) _cells[i].SetSuspended(false);
                }
            }
        }

        // Fit zoom is cell width measured against the full grid area, so it has to be refreshed
        // wherever cells move: zooming a cell, toggling the toolbar, resizing the window.
        foreach (var cell in _cells) cell.SetFitReference(area.Width);

        PositionBanner();
        ResumeLayout(true);

        // Cells just moved, so the hook's cached copy is stale until this runs.
        PublishHitTestMap();
        ApplyPresentation();
    }

    /// <summary>
    /// Live or still for every cell. The zoomed cell is always live; in the grid only the cells
    /// set to stay live are, and the rest show a picture while their page sleeps. Cells hidden
    /// behind a zoom are left as they were — they are not on screen either way.
    /// </summary>
    private void ApplyPresentation()
    {
        for (int i = 0; i < _cells.Count; i++)
        {
            if (_zoomed >= 0)
            {
                if (i == _zoomed) _cells[i].SetLive(true);
                else _cells[i].SetHidden();
            }
            else
            {
                _cells[i].SetLive(_cells[i].Config.LiveInGrid);
            }
        }
    }

    /// <summary>Refresh the hook thread's copy every time WinForms hands us a new window.</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _formHandle = Handle;
        PublishHitTestMap();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_interactive || _zoomed >= 0 || _cells.Count == 0) return;

        using var idle = new Pen(Color.FromArgb(42, 42, 48), 1);
        using var selected = new Pen(Color.FromArgb(120, 120, 135), 2);
        using var hovered = new Pen(AccentColor, 3);
        // Drag feedback lives in the frames because the form cannot paint over child windows —
        // the gap between cells is the only canvas it owns.
        using var dropTarget = new Pen(Color.FromArgb(110, 210, 140), 3);
        using var dragSource = new Pen(Color.FromArgb(110, 210, 140), 2)
        {
            DashStyle = System.Drawing.Drawing2D.DashStyle.Dash,
        };

        for (int i = 0; i < _cells.Count; i++)
        {
            if (!_cells[i].Visible) continue;
            if (LabelStripHeight > 0) DrawCellLabel(e.Graphics, e.ClipRectangle, i);
            var pen = _dragFrom >= 0
                ? i == _dragOver && i != _dragFrom ? dropTarget : i == _dragFrom ? dragSource : idle
                : i == _hover ? hovered : i == _selected ? selected : idle;
            int inflate = (int)Math.Ceiling(pen.Width / 2f);
            var r = Rectangle.Inflate(CellSlot(i), inflate, inflate);
            e.Graphics.DrawRectangle(pen, r.Left, r.Top, r.Width - 1, r.Height - 1);
        }
    }

    private static readonly Color LabelStripColor = Color.FromArgb(28, 28, 33);
    private static readonly Color LabelTextColor = Color.FromArgb(230, 230, 235);
    private static readonly Color LabelDimColor = Color.FromArgb(154, 154, 166);
    private static readonly Color LabelOnColor = Color.FromArgb(63, 182, 139);

    /// <summary>
    /// Number, name and the settings worth seeing from across the room, painted into the strip
    /// under the cell. Painted rather than hosted in a Label: the form owns this canvas, so a
    /// hosted window cannot cover it the way it could a sibling control.
    /// </summary>
    private void DrawCellLabel(Graphics g, Rectangle clip, int index)
    {
        var cell = _cells[index];
        var b = cell.Bounds;
        var strip = new Rectangle(b.Left, b.Bottom, b.Width, LabelStripHeight);
        if (strip.Height <= 0 || !strip.IntersectsWith(clip)) return;

        using var fill = new SolidBrush(LabelStripColor);
        g.FillRectangle(fill, strip);

        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                      TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        int pad = Scaled(8), x = strip.Left + pad, right = strip.Right - pad;

        // Badges from the right, so the name gets whatever room is left over.
        foreach (var (text, color) in CellBadges(cell))
        {
            // Measured and drawn without ellipsis, plus slack: measuring and drawing round
            // differently, and with EndEllipsis a badge came out as "로…".
            const TextFormatFlags badgeFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                               TextFormatFlags.NoPadding;
            int w = TextRenderer.MeasureText(g, text, Font, Size.Empty, badgeFlags).Width + Scaled(4);
            int dot = Scaled(7);
            if (right - w - dot - Scaled(5) < x + Scaled(80)) break;
            right -= w;
            TextRenderer.DrawText(g, text, Font, new Rectangle(right, strip.Top, w, strip.Height), LabelDimColor, badgeFlags);
            right -= dot + Scaled(5);
            using var dotBrush = new SolidBrush(color);
            var smooth = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(dotBrush, right, strip.Top + (strip.Height - dot) / 2, dot, dot);
            g.SmoothingMode = smooth;
            right -= Scaled(12);
        }

        string number = (index + 1).ToString();
        int nw = TextRenderer.MeasureText(g, number, Font, Size.Empty, flags).Width;
        TextRenderer.DrawText(g, number, Font, new Rectangle(x, strip.Top, nw, strip.Height), LabelDimColor, flags);
        x += nw + Scaled(8);

        string name = cell.Config.IsConfigured ? cell.HeaderText : L.T("비어 있음", "Empty");
        if (right > x)
            TextRenderer.DrawText(g, name, Font, new Rectangle(x, strip.Top, right - x, strip.Height),
                cell.Config.IsConfigured ? LabelTextColor : LabelDimColor, flags);
    }

    private static IEnumerable<(string Text, Color Color)> CellBadges(CellHost cell)
    {
        var c = cell.Config;
        if (c.Kind != CellKind.Browser) yield break;
        if (c.LiveInGrid) yield return (L.T("실시간", "Live"), Color.FromArgb(240, 120, 110));
        if (cell.HasUserZoom) yield return (L.T($"배율 {c.ZoomScale * 100:0}%", $"Zoom {c.ZoomScale * 100:0}%"), AccentColor);
        if (c.RefreshMinutes > 0) yield return (L.T($"{c.RefreshMinutes}분", $"{c.RefreshMinutes} min"), LabelDimColor);
        if (c.PreventLogout) yield return (L.T("로그아웃 방지", "Prevent sign-out"), LabelOnColor);
        if (c.KeepSignedIn) yield return (L.T("로그인 유지", "Stay signed in"), LabelOnColor);
    }

    private void InvalidateCellFrame(int index)
    {
        if (index < 0 || index >= _cells.Count) return;
        int pad = Math.Max(4, S.CellGap);
        Invalidate(Rectangle.Inflate(CellSlot(index), pad, pad), false);
    }

    // ------------------------------------------------------------------ input routing

    /// <summary>
    /// Hand work back to the UI thread from the hook thread.
    ///
    /// The hook now runs on its own thread, so the window can be gone by the time a callback
    /// gets here — during shutdown especially. Swallowing that is right: the alternative is an
    /// exception on a thread whose stall would freeze input for the whole desktop.
    /// </summary>
    private void Post(Action action)
    {
        if (_closing) return;
        try
        {
            if (IsHandleCreated) BeginInvoke(action);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Publish what the hook needs to hit-test without touching a single Control.
    /// Called wherever the cells move or the mode changes.
    /// </summary>
    private void PublishHitTestMap()
    {
        if (!IsHandleCreated) return;
        _formHandle = Handle;

        var cells = new Rectangle[_cells.Count];
        for (int i = 0; i < _cells.Count; i++)
            cells[i] = _cells[i].Visible ? RectangleToScreen(CellSlot(i)) : Rectangle.Empty;

        _hook.SetMap(new HitTestMap
        {
            FormHandle = Handle,
            GridBounds = RectangleToScreen(GridArea()),
            Cells = cells,
            ClicksEnabled = !_closing && !_interactive && _cells.Count > 0,
            HoverEnabled = !_closing && !_interactive && _zoomed < 0 && _cells.Count > 0,
        });
    }

    private bool OnGlobalMouseDown(MouseButtons button, Point screen, int index)
    {
        if (_interactive || _closing) return false;
        if (index < 0) return false;

        // The menu never sees a click that lands on a cell — the hook takes it first — so it
        // would stay open. A click outside it closes it and does nothing else, as menus do.
        if (_menuOpen)
        {
            _swallowRelease = true;
            _pressedCell = -1;
            Post(() => _menu.Close(ToolStripDropDownCloseReason.AppClicked));
            return true;
        }

        _pressedCell = index;
        _pressPoint = screen;
        _pressButton = button;
        return true;
    }

    private bool OnGlobalMouseUp(MouseButtons button, Point screen, int index)
    {
        if (_interactive || _closing) return false;
        if (_swallowRelease)
        {
            // The release of the click that closed the menu.
            _swallowRelease = false;
            return true;
        }
        int pressed = _pressedCell;
        _pressedCell = -1;

        // Reset the drag synchronously: the hook can fire again before BeginInvoke runs.
        int from = _dragFrom, over = _dragOver;
        _dragFrom = -1;
        _dragOver = -1;
        if (from >= 0)
        {
            Post(() =>
            {
                InvalidateCellFrame(from);
                InvalidateCellFrame(over);
                if (index >= 0 && index != from) SwapCells(from, index);
            });
            return true;
        }

        if (index < 0) return false;

        // A release we never saw the press for is not ours to eat. Dragging the window by its
        // title bar is exactly that: the press lands on the caption, the cursor then travels
        // over the cells, and swallowing the release leaves Windows in its move loop with the
        // window stuck to the pointer.
        if (pressed < 0) return false;

        // Pressed on one cell and released on another: consume it, but do nothing.
        if (pressed != index) return true;

        // The hook must return promptly; do the real work back on the message loop.
        Post(() =>
        {
            if (button == MouseButtons.Right) ShowCellMenu(index, screen);
            else if (button == MouseButtons.Left) ActivateCell(index);
        });
        return true;
    }

    private void OnGlobalMouseMove(Point screen, int index)
    {
        // Throttling and the hit test both happen in the hook now; this only reacts.
        if (_interactive || _closing || _zoomed >= 0) return;

        // A left press that travels far enough becomes a cell drag. WinForms drag-and-drop is not
        // an option here: the hosted windows belong to other processes and consume the input
        // before any managed control sees it, which is why the hooks exist in the first place.
        if (_dragFrom < 0 && _pressedCell >= 0 && _pressButton == MouseButtons.Left &&
            _preset is not null && _pressedCell < _preset.Cells.Count &&
            _preset.Cells[_pressedCell].IsConfigured &&
            (Math.Abs(screen.X - _pressPoint.X) >= DragThreshold ||
             Math.Abs(screen.Y - _pressPoint.Y) >= DragThreshold))
        {
            _dragFrom = _pressedCell;
            int stale = _hover;
            _hover = -1;
            Post(() => InvalidateCellFrame(stale));
        }

        if (_dragFrom >= 0)
        {
            if (index == _dragOver) return;
            int previousTarget = _dragOver;
            _dragOver = index;
            int source = _dragFrom;
            Post(() =>
            {
                InvalidateCellFrame(previousTarget);
                InvalidateCellFrame(index);
                InvalidateCellFrame(source);
            });
            return;
        }

        if (index == _hover) return;
        int previous = _hover;
        _hover = index;
        Post(() =>
        {
            InvalidateCellFrame(previous);
            InvalidateCellFrame(_hover);
        });
    }

    private bool OnGlobalKeyDown(Keys key)
    {
        if (_closing) return false;
        // Cached: see _formHandle. Never Handle, which belongs to the UI thread.
        if (NativeMethods.GetForegroundWindow() != _formHandle) return false;

        if (_interactive)
        {
            // Esc is the one key kept back: it leaves input mode and the zoom together, straight
            // back to the grid. Everything else belongs to the hosted app.
            if (key == Keys.Escape)
            {
                Post(LeaveZoom);
                return true;
            }
            return false;
        }

        switch (key)
        {
            case Keys.Escape:
                if (_menuOpen)
                {
                    Post(() => _menu.Close(ToolStripDropDownCloseReason.Keyboard));
                    return true;
                }
                if (_dragFrom >= 0)
                {
                    int from = _dragFrom, over = _dragOver;
                    _dragFrom = -1;
                    _dragOver = -1;
                    _pressedCell = -1;
                    Post(() =>
                    {
                        InvalidateCellFrame(from);
                        InvalidateCellFrame(over);
                    });
                    return true;
                }
                if (_zoomed >= 0)
                {
                    int z = _zoomed;
                    Post(() => ToggleZoom(z));
                    return true;
                }
                return false;

            case Keys.Enter:
                Post(() => ActivateCell(_selected));
                return true;

            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
                if (_zoomed >= 0) return false;
                Post(() => MoveSelection(key));
                return true;

            case Keys.F11:
                Post(ToggleFullScreenByUser);
                return true;

        }

        if ((key & Keys.Control) == Keys.Control)
        {
            var code = key & Keys.KeyCode;
            if (code >= Keys.D1 && code <= Keys.D9)
            {
                int slot = code - Keys.D1;
                Post(() =>
                {
                    if (slot < _store.Sets.Count) _ = LoadPresetAsync(_store.Sets[slot]);
                });
                return true;
            }
            if (code == Keys.R)
            {
                Post(() => CellAt(_selected)?.Reload());
                return true;
            }
        }

        return false;
    }

    private CellHost? CellAt(int index) => index >= 0 && index < _cells.Count ? _cells[index] : null;

    private void MoveSelection(Keys key)
    {
        if (_cells.Count == 0) return;
        int cols = _preset.Columns, rows = _preset.Rows;
        int r = _selected / cols, c = _selected % cols;

        switch (key)
        {
            case Keys.Left: c--; break;
            case Keys.Right: c++; break;
            case Keys.Up: r--; break;
            case Keys.Down: r++; break;
        }
        c = Math.Clamp(c, 0, cols - 1);
        r = Math.Clamp(r, 0, rows - 1);

        int next = Math.Clamp(r * cols + c, 0, _cells.Count - 1);
        if (next == _selected) return;
        int previous = _selected;
        _selected = next;
        InvalidateCellFrame(previous);
        InvalidateCellFrame(_selected);
    }

    // ------------------------------------------------------------------ modes

    private void ActivateCell(int index)
    {
        var cell = CellAt(index);
        if (cell is null) return;

        int previous = _selected;
        _selected = index;
        InvalidateCellFrame(previous);
        InvalidateCellFrame(index);

        // An unconfigured cell has nothing to zoom into; go straight to its settings.
        if (!_preset.Cells[index].IsConfigured)
        {
            EditCell(index);
            return;
        }
        bool zoomingIn = _zoomed != index;
        ToggleZoom(index);

        // A cell is zoomed to be used, so hand it the input straight away.
        if (zoomingIn && _zoomed == index) SetInteractive(true);
    }

    /// <summary>Back to the grid from a zoomed cell, leaving input mode on the way.</summary>
    private void LeaveZoom()
    {
        if (_zoomed < 0)
        {
            if (_interactive) SetInteractive(false);
            return;
        }
        ToggleZoom(_zoomed);
        ShowBanner(L.T("격자 보기", "Grid view"));
    }

    private int DragThreshold => Math.Max(SystemInformation.DragSize.Width, Scaled(6));

    /// <summary>
    /// Exchange two cells' places. The hosts are swapped rather than their content: a reparented
    /// window or a live WebView2 only gets new bounds, so rearranging never reloads a cell — the
    /// same trade zooming already makes. Both lists move together to keep
    /// <c>_cells[i].Config</c> and <c>_preset.Cells[i]</c> the same object.
    /// </summary>
    private void SwapCells(int a, int b)
    {
        if (_preset is null || a == b) return;
        if (a < 0 || b < 0 || a >= _cells.Count || b >= _cells.Count) return;

        (_cells[a], _cells[b]) = (_cells[b], _cells[a]);
        (_preset.Cells[a], _preset.Cells[b]) = (_preset.Cells[b], _preset.Cells[a]);
        _cells[a].Index = a;
        _cells[b].Index = b;

        _selected = b;
        _hover = -1;
        _store.SaveSet(_preset);
        Relayout();
        Invalidate();
        UpdateTitle();
        ShowBanner(L.T($"셀 {a + 1} ↔ {b + 1} 자리 교환", $"Swapped cells {a + 1} ↔ {b + 1}"));
    }

    private void ToggleZoom(int index)
    {
        if (index < 0 || index >= _cells.Count) return;
        if (_interactive) SetInteractive(false);

        if (_zoomed == index)
        {
            _zoomed = -1;
            if (!S.StartFullScreen) SetFullScreen(false);
        }
        else
        {
            _zoomed = index;
            _selected = index;
            SetFullScreen(true);
        }

        _hover = -1;
        Relayout();
        Invalidate();
        UpdateTitle();

        if (_zoomed >= 0) _closeZoomTimer.Start();
        else UpdateCloseZoom();
    }

    private void SetInteractive(bool on)
    {
        if (on)
        {
            // Input mode is a fullscreen concept; asking for it from the grid zooms first.
            if (_zoomed < 0)
            {
                if (!_preset.Cells[Math.Clamp(_selected, 0, _preset.Cells.Count - 1)].IsConfigured) return;
                ToggleZoom(_selected);
            }
            if (_zoomed < 0) return;
        }

        _interactive = on;
        _hook.MouseCaptureEnabled = !on;

        Relayout();
        Invalidate();
        PublishHitTestMap();

        // Keep-alive is a view-mode concept: in input mode the user's own activity holds the
        // session open, and synthetic key events would land in whatever they are typing.
        foreach (var cell in _cells) cell.SetKeepAliveActive(!on);

        if (on)
        {
            CellAt(_zoomed)?.FocusContent();
            // Nothing is announced over a cell being worked in; clear whatever was showing.
            _bannerTimer.Stop();
            _banner.Visible = false;
        }
        else
        {
            // Focus currently sits in another process's window; a plain Focus() will not move it.
            Activate();
            NativeMethods.ForceFocus(Handle);
        }
        UpdateTitle();
    }

    /// <summary>
    /// F11 and the menu item both land here, and only here does the choice
    /// stick. <see cref="SetFullScreen"/> itself must stay silent: zooming a cell also goes
    /// fullscreen, and that is a view state, not a preference about how the app should start.
    /// </summary>
    private void ToggleFullScreenByUser()
    {
        SetFullScreen(!_fullScreen);
        S.StartFullScreen = _fullScreen;
        _store.SaveSettings();
        ShowBanner(_fullScreen
            ? L.T("전체 화면 — 다음 실행에도 이 상태로 시작합니다", "Full screen — the next start opens this way too")
            : L.T("창 모드 — 다음 실행에도 이 상태로 시작합니다", "Windowed — the next start opens this way too"));
    }

    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;

        if (on)
        {
            _restoreBounds = Bounds;
            _restoreState = WindowState;
            _restoreBorder = FormBorderStyle;

            var screen = Screen.FromControl(this);
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screen.Bounds;
            TopMost = S.TopMostInFullScreen;
        }
        else
        {
            TopMost = false;
            FormBorderStyle = _restoreBorder;
            WindowState = _restoreState;
            if (_restoreState == FormWindowState.Normal) Bounds = _restoreBounds;
        }

        _fullScreen = on;
        Relayout();
    }

    // ------------------------------------------------------------------ chrome

    private int Scaled(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);

    private void BuildBanner()
    {
        _banner.AutoSize = true;
        _banner.Visible = false;
        _banner.BackColor = Color.FromArgb(40, 40, 48);
        _banner.ForeColor = Color.White;
        _banner.Padding = new Padding(14, 8, 14, 8);
        _banner.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        Controls.Add(_banner);

        _bannerTimer.Interval = 2200;
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            _banner.Visible = false;
        };
    }

    private void ShowBanner(string text)
    {
        // Input mode shows the hosted app and nothing else over it.
        if (_interactive) return;
        _banner.Text = text;
        _banner.Visible = true;
        _banner.BringToFront();
        PositionBanner();
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    private void PositionBanner()
    {
        if (!_banner.Visible) return;
        var area = GridArea();
        _banner.Location = new Point(
            area.Left + Math.Max(0, (area.Width - _banner.Width) / 2),
            area.Top + 24);
    }

    private ToolStripLabel _menuHeader = null!;
    private ToolStripMenuItem _labelsItem = null!;
    private ToolStripMenuItem _liveItem = null!;

    /// <summary>
    /// The mouse's way out of a zoomed cell, as a browser's fullscreen shows one: a round ✕ at the
    /// top centre that appears only while the pointer is near the top edge, so it never sits over
    /// the page otherwise. A sibling control rather than painting — the form cannot paint over the
    /// hosted window, but a control brought to front sits above it.
    /// </summary>
    private readonly Button _closeZoom = new();
    private readonly ToolTip _closeZoomTip = new();
    /// <summary>
    /// Polls the pointer while a cell is zoomed. The hook cannot help: in input mode it stands
    /// down for the mouse, and it must not touch controls anyway.
    /// </summary>
    private readonly System.Windows.Forms.Timer _closeZoomTimer = new() { Interval = 150 };

    private void BuildCloseZoom()
    {
        int size = Scaled(48);
        _closeZoom.Text = "✕";
        _closeZoom.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
        _closeZoom.FlatStyle = FlatStyle.Flat;
        _closeZoom.FlatAppearance.BorderSize = 0;
        _closeZoom.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 70, 80);
        _closeZoom.BackColor = Color.FromArgb(40, 40, 48);
        _closeZoom.ForeColor = Color.White;
        _closeZoom.TabStop = false;
        _closeZoom.Visible = false;
        _closeZoom.Size = new Size(size, size);
        using (var round = new System.Drawing.Drawing2D.GraphicsPath())
        {
            round.AddEllipse(0, 0, size, size);
            _closeZoom.Region = new Region(round);
        }
        _closeZoom.Click += (_, _) => LeaveZoom();
        _closeZoomTip.SetToolTip(_closeZoom, L.T("격자로 돌아가기 (Esc)", "Back to grid (Esc)"));
        Controls.Add(_closeZoom);

        _closeZoomTimer.Tick += (_, _) => UpdateCloseZoom();
    }

    private void UpdateCloseZoom()
    {
        if (_zoomed < 0)
        {
            _closeZoomTimer.Stop();
            _closeZoom.Visible = false;
            return;
        }

        var area = GridArea();
        var pointer = PointToClient(Cursor.Position);
        var band = new Rectangle(area.Left, area.Top, area.Width, Scaled(90));
        bool show = band.Contains(pointer) && NativeMethods.GetForegroundWindow() == Handle;
        if (show)
        {
            _closeZoom.Location = new Point(area.Left + (area.Width - _closeZoom.Width) / 2, area.Top + Scaled(16));
            if (!_closeZoom.Visible)
            {
                _closeZoom.Visible = true;
                _closeZoom.BringToFront();
            }
        }
        else if (_closeZoom.Visible && !_closeZoom.Bounds.Contains(pointer))
        {
            _closeZoom.Visible = false;
        }
    }

    /// <summary>
    /// Three groups — this cell, the arrangement, the app — with the one destructive cell action
    /// kept apart at the bottom, so it is never the item next to the ones clicked every day.
    /// </summary>
    private void BuildMenu()
    {
        _menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false };
        _menu.BackColor = DarkMenuColors.Background;
        _menu.ForeColor = LabelTextColor;
        _menu.ShowImageMargin = false;
        // Room for the tick on "셀 이름표 표시".
        _menu.ShowCheckMargin = true;
        _menu.Opened += (_, _) => _menuOpen = true;
        _menu.Closed += (_, _) => _menuOpen = false;

        _menuHeader = new ToolStripLabel { ForeColor = LabelDimColor, Margin = new Padding(4, 4, 4, 2) };
        _menu.Items.Add(_menuHeader);
        _menu.Items.Add(L.T("셀 설정…", "Cell settings…"), null, (_, _) => EditCell(_menuCell));
        _menu.Items.Add(L.T("새로 고침", "Reload"), null, (_, _) => CellAt(_menuCell)?.Reload());
        _resetZoomItem = new ToolStripMenuItem(L.T("배율 원래대로", "Reset zoom"), null, (_, _) => CellAt(_menuCell)?.ResetZoom());
        _menu.Items.Add(_resetZoomItem);
        _liveItem = new ToolStripMenuItem(L.T("격자에서도 실시간 표시", "Live in grid"), null, (_, _) => ToggleLiveInGrid(_menuCell));
        _menu.Items.Add(_liveItem);
        _adoptUrlItem = new ToolStripMenuItem(L.T("현재 주소를 이 칸의 주소로", "Use current page as this cell's address"), null, (_, _) => AdoptCurrentUrl(_menuCell));
        _menu.Items.Add(_adoptUrlItem);
        _menu.Items.Add(L.T("셀 다시 시작", "Restart cell"), null, (_, _) => RestartCell(_menuCell));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(BuildSetMenu());
        _menu.Items.Add(BuildGridSizeMenu());
        _menu.Items.Add(L.T("세트 관리…", "Manage sets…"), null, (_, _) => ManagePresets());
        _menu.Items.Add(L.T("현재 배치를 새 세트로 저장…", "Save layout as new set…"), null, (_, _) => SaveAsNewPreset());
        _labelsItem = new ToolStripMenuItem(L.T("셀 이름표 표시", "Show cell labels"), null, (_, _) => ToggleCellLabels());
        _menu.Items.Add(_labelsItem);
        _menu.Items.Add(BuildLanguageMenu());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(L.T("전체 화면", "Full screen"), null, (_, _) => ToggleFullScreenByUser()) { ShortcutKeyDisplayString = "F11" });
        _menu.Items.Add(BuildShortcutMenu());
        _menu.Items.Add(L.T($"버전 및 업데이트…  (v{AppVersion.Display})", $"Version and updates…  (v{AppVersion.Display})"), null, (_, _) => ShowUpdateDialog());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(L.T("셀 비우기", "Clear cell"), null, (_, _) => ClearCell(_menuCell)) { ForeColor = Color.FromArgb(240, 154, 154) });
        _menu.Items.Add(L.T("종료", "Exit"), null, (_, _) => Close());

        foreach (ToolStripItem item in _menu.Items)
            if (item is ToolStripMenuItem m && m.ForeColor == SystemColors.ControlText) m.ForeColor = LabelTextColor;
    }

    /// <summary>
    /// Every set, the open one ticked, each with its Ctrl+N. Rebuilt on open — sets are added,
    /// renamed and reordered underneath this menu.
    /// </summary>
    private ToolStripMenuItem BuildSetMenu()
    {
        var item = new ToolStripMenuItem(L.T("세트 바꾸기", "Switch set")) { ForeColor = LabelTextColor };
        StyleDropDown(item);
        // A placeholder so the arrow shows before the first open fills it.
        item.DropDownItems.Add(new ToolStripMenuItem("…"));
        item.DropDownOpening += (_, _) =>
        {
            item.DropDownItems.Clear();
            var sets = _store.Sets.ToList();
            for (int i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                var entry = new ToolStripMenuItem($"{set.Name}  ({set.Columns}×{set.Rows})", null,
                    (_, _) => { if (set.Id != _preset.Id) _ = LoadPresetAsync(set); })
                {
                    Checked = set.Id == _preset?.Id,
                    ShortcutKeyDisplayString = i < 9 ? $"Ctrl+{i + 1}" : string.Empty,
                    ForeColor = LabelTextColor,
                };
                item.DropDownItems.Add(entry);
            }
        };
        return item;
    }

    /// <summary>The keys there are, for whoever has not learned them yet.</summary>
    private ToolStripMenuItem BuildShortcutMenu()
    {
        var item = new ToolStripMenuItem(L.T("단축키", "Shortcuts")) { ForeColor = LabelTextColor };
        StyleDropDown(item);
        foreach (var (key, what) in new[]
                 {
                     (L.T("클릭 · Enter", "Click · Enter"), L.T("크게 보고 바로 입력", "Zoom in and start typing")),
                     ("Esc", L.T("격자로 돌아가기 · 메뉴 닫기", "Back to grid · close menu")),
                     (L.T("방향키", "Arrow keys"), L.T("셀 고르기", "Select a cell")),
                     ("Ctrl+1~9", L.T("세트 바꾸기", "Switch set")),
                     ("Ctrl+R", L.T("선택한 셀 새로 고침", "Reload selected cell")),
                     ("F11", L.T("전체 화면", "Full screen")),
                     (L.T("우클릭", "Right-click"), L.T("이 메뉴", "This menu")),
                 })
        {
            item.DropDownItems.Add(new ToolStripMenuItem(what) { ShortcutKeyDisplayString = key, ForeColor = LabelTextColor });
        }
        return item;
    }

    private void StyleDropDown(ToolStripMenuItem item)
    {
        // A submenu draws with its own renderer, not its parent's.
        item.DropDown.Renderer = _menu.Renderer;
        item.DropDown.BackColor = DarkMenuColors.Background;
        if (item.DropDown is ToolStripDropDownMenu dd) { dd.ShowImageMargin = false; dd.ShowCheckMargin = true; }
    }

    /// <summary>
    /// Automatic, Korean or English. Captions are read once as controls are built, so the choice
    /// is saved and applies from the next start rather than switching half the UI now.
    /// </summary>
    private ToolStripMenuItem BuildLanguageMenu()
    {
        var item = new ToolStripMenuItem("언어 / Language") { ForeColor = LabelTextColor };
        StyleDropDown(item);
        foreach (var (code, caption) in new[]
                 {
                     ("", L.T("자동 (Windows 설정)", "Automatic (Windows setting)")),
                     ("ko", "한국어"),
                     ("en", "English"),
                 })
        {
            item.DropDownItems.Add(new ToolStripMenuItem(caption, null, (_, _) => SetLanguage(code))
            {
                Tag = code,
                ForeColor = LabelTextColor,
            });
        }

        // Ticks are refreshed on open, the same as the grid-size menu.
        item.DropDownOpening += (_, _) =>
        {
            string current = (S.Language ?? string.Empty).Trim().ToLowerInvariant();
            if (current is not ("ko" or "en")) current = string.Empty;
            foreach (ToolStripMenuItem entry in item.DropDownItems)
                entry.Checked = (string)entry.Tag! == current;
        };
        return item;
    }

    private void SetLanguage(string code)
    {
        S.Language = code;
        _store.SaveSettings();
        ShowBanner(L.T("다시 시작하면 적용됩니다", "Takes effect after a restart"));
    }

    private void ToggleCellLabels()
    {
        S.ShowCellLabels = !S.ShowCellLabels;
        _store.SaveSettings();
        Relayout();
        Invalidate();
    }

    /// <summary>The menu in the wall's own colours rather than the light system default.</summary>
    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public static readonly Color Background = Color.FromArgb(35, 35, 41);
        private static readonly Color Border = Color.FromArgb(52, 52, 60);
        private static readonly Color Selected = Color.FromArgb(52, 64, 90);

        public override Color ToolStripDropDownBackground => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Selected;
        public override Color MenuItemSelected => Selected;
        public override Color MenuItemSelectedGradientBegin => Selected;
        public override Color MenuItemSelectedGradientEnd => Selected;
        public override Color MenuItemPressedGradientBegin => Selected;
        public override Color MenuItemPressedGradientEnd => Selected;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Background;
        public override Color CheckBackground => Selected;
        public override Color CheckSelectedBackground => Selected;
        public override Color CheckPressedBackground => Selected;
    }

    /// <summary>
    /// Common shapes, so reshaping the wall does not require the set dialog. Arbitrary sizes up
    /// to 12×12 stay available there.
    /// </summary>
    private ToolStripMenuItem BuildGridSizeMenu()
    {
        var item = new ToolStripMenuItem(L.T("격자 크기", "Grid size"));
        foreach (var (cols, rows) in new[] { (1, 1), (2, 1), (2, 2), (3, 2), (3, 3), (4, 2), (4, 3), (4, 4) })
            item.DropDownItems.Add($"{cols} × {rows}", null, (_, _) => SetGridSize(cols, rows));

        StyleDropDown(item);
        foreach (ToolStripItem entry in item.DropDownItems) entry.ForeColor = LabelTextColor;

        // Ticks are refreshed on open — the open set changes underneath this menu.
        item.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem entry in item.DropDownItems)
                entry.Checked = entry.Text == $"{_preset?.Columns} × {_preset?.Rows}";
        };
        return item;
    }

    private void ShowCellMenu(int index, Point screen)
    {
        _menuCell = index;
        int previous = _selected;
        _selected = index;
        InvalidateCellFrame(previous);
        InvalidateCellFrame(index);
        UpdateAdoptUrlItem(index);
        _labelsItem.Checked = S.ShowCellLabels;
        var menuHost = CellAt(index);
        _liveItem.Available = menuHost?.Config.Kind == CellKind.Browser;
        _liveItem.Checked = menuHost?.Config.LiveInGrid == true;
        var host = CellAt(index);
        string name = host is null ? string.Empty : host.Config.IsConfigured ? host.HeaderText : L.T("비어 있음", "Empty");
        if (name.Length > 40) name = name[..40] + "…";
        _menuHeader.Text = L.T($"셀 {index + 1} · {name}", $"Cell {index + 1} · {name}");
        _resetZoomItem.Available = host?.HasUserZoom == true;
        if (host?.HasUserZoom == true)
            _resetZoomItem.Text = L.T($"배율 원래대로  (현재 {host.Config.ZoomScale * 100:0}%)", $"Reset zoom  (now {host.Config.ZoomScale * 100:0}%)");
        _menu.Show(screen);
    }

    // ------------------------------------------------------------------ commands

    /// <summary>
    /// Offer to adopt the address only when there is something to adopt: a browser cell that has
    /// wandered somewhere other than the address it was configured with. Showing the address in
    /// the caption saves opening the settings window to see what would be saved.
    /// </summary>
    private void UpdateAdoptUrlItem(int index)
    {
        var cell = index >= 0 && index < _preset.Cells.Count ? _preset.Cells[index] : null;
        bool drifted = cell is { Kind: CellKind.Browser }
            && !string.IsNullOrWhiteSpace(cell.LastUrl)
            && !string.Equals(cell.LastUrl, cell.Url, StringComparison.OrdinalIgnoreCase);

        _adoptUrlItem.Available = drifted;
        if (!drifted) return;

        var shown = cell!.LastUrl.Length > 60 ? cell.LastUrl[..60] + "…" : cell.LastUrl;
        _adoptUrlItem.Text = L.T($"현재 주소를 이 칸의 주소로  ({shown})", $"Use current page as this cell's address  ({shown})");
    }

    /// <summary>Make where the cell is now the address it is configured with.</summary>
    private void AdoptCurrentUrl(int index)
    {
        if (index < 0 || index >= _preset.Cells.Count) return;
        var cell = _preset.Cells[index];
        if (cell.Kind != CellKind.Browser || string.IsNullOrWhiteSpace(cell.LastUrl)) return;

        cell.Url = cell.LastUrl;
        _store.SaveSet(_preset);
        Invalidate();
        UpdateTitle();
        ShowBanner(L.T($"셀 {index + 1}의 주소를 현재 화면으로 바꿨습니다", $"Cell {index + 1} now uses the current page as its address"));
    }

    private async void EditCell(int index)
    {
        if (index < 0 || index >= _cells.Count) return;

        CellConfig updated;
        using (var dialog = new CellSettingsForm(index, _preset.Cells[index]))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            updated = dialog.Result;
        }

        _preset.Cells[index] = updated;
        _store.SaveSet(_preset);
        UpdateTitle();
        Invalidate();

        try
        {
            var applying = _cells[index].ApplyAsync(updated);
            // The new settings may have turned live-in-grid on or off.
            ApplyPresentation();
            await applying.ConfigureAwait(true);
        }
        catch { }
    }

    private void ToggleLiveInGrid(int index)
    {
        var cell = CellAt(index);
        if (cell is null || cell.Config.Kind != CellKind.Browser) return;
        cell.Config.LiveInGrid = !cell.Config.LiveInGrid;
        _store.SaveSet(_preset);
        ApplyPresentation();
        InvalidateCellFrame(index);
        ShowBanner(cell.Config.LiveInGrid
            ? L.T($"셀 {index + 1} 격자에서도 실시간으로 표시", $"Cell {index + 1} is live in the grid")
            : L.T($"셀 {index + 1} 격자에서는 정지 화면으로 표시", $"Cell {index + 1} shows a still in the grid"));
    }

    private async void RestartCell(int index)
    {
        if (index < 0 || index >= _cells.Count) return;
        try { await _cells[index].ApplyAsync(_preset.Cells[index]).ConfigureAwait(true); }
        catch { }
    }

    private async void ClearCell(int index)
    {
        if (index < 0 || index >= _cells.Count) return;
        var empty = new CellConfig();
        _preset.Cells[index] = empty;
        _store.SaveSet(_preset);
        try { await _cells[index].ApplyAsync(empty).ConfigureAwait(true); }
        catch { }
        Invalidate();
    }

    /// <summary>
    /// Reshape the open set's grid. Cells keep their (row, column), so growing preserves
    /// everything and shrinking discards only what falls outside the new bounds.
    /// </summary>
    private void SetGridSize(int cols, int rows)
    {
        if (_preset is null) return;
        cols = Math.Clamp(cols, 1, 12);
        rows = Math.Clamp(rows, 1, 12);
        if (cols == _preset.Columns && rows == _preset.Rows)
        {
            return;
        }

        int dropped = ConfiguredCellsOutside(cols, rows);
        if (dropped > 0 && MessageBox.Show(
                this,
                L.T($"격자를 {cols}×{rows}로 줄이면 설정된 셀 {dropped}개가 삭제됩니다.\n계속할까요?",
                    $"Shrinking the grid to {cols}×{rows} deletes {dropped} configured cell(s).\nContinue?"),
                L.T("격자 크기", "Grid size"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        _preset.Resize(cols, rows);
        _store.SaveSet(_preset);
        ShowBanner(L.T($"격자 {cols}×{rows}", $"Grid {cols}×{rows}"));
        _ = LoadPresetAsync(_preset);
    }

    /// <summary>Configured cells that a shrink to <paramref name="cols"/>×<paramref name="rows"/> would throw away.</summary>
    private int ConfiguredCellsOutside(int cols, int rows)
    {
        int oldCols = Math.Max(1, _preset.Columns), oldRows = Math.Max(1, _preset.Rows);
        int count = 0;
        for (int r = 0; r < oldRows; r++)
        {
            for (int c = 0; c < oldCols; c++)
            {
                if (r < rows && c < cols) continue;
                int i = r * oldCols + c;
                if (i < _preset.Cells.Count && _preset.Cells[i].IsConfigured) count++;
            }
        }
        return count;
    }

    private void ManagePresets()
    {
        // Remembered so an edit to the open set's dimensions is noticed even when the user
        // dismisses the dialog without pressing "open".
        var openId = _preset.Id;
        int openCols = _preset.Columns, openRows = _preset.Rows;

        GridPreset? chosen;
        DialogResult result;
        using (var dialog = new PresetManagerForm(_store))
        {
            result = dialog.ShowDialog(this);
            chosen = dialog.Selected;
        }

        _store.SaveSet(_preset);
        UpdateTitle();

        if (result == DialogResult.OK && chosen is not null && chosen.Id != openId)
        {
            _ = LoadPresetAsync(chosen);
            return;
        }

        var open = _store.Sets.FirstOrDefault(p => p.Id == openId);
        if (open is not null && (open.Columns != openCols || open.Rows != openRows))
            _ = LoadPresetAsync(open);
        else if (open is null)
            _ = LoadPresetAsync(StartingSet());
    }

    private void SaveAsNewPreset()
    {
        var name = Prompt(L.T("새 세트 이름", "New set name"), _preset.Name + L.T(" 사본", " copy"));
        if (string.IsNullOrWhiteSpace(name)) return;

        var copy = _store.Add(_preset.Duplicate(name.Trim()));
        ShowBanner(L.T($"'{copy.Name}' 세트로 저장했습니다", $"Saved as set '{copy.Name}'"));
    }

    private void UpdateTitle()
    {
        string mode = _interactive ? L.T("입력", "input") : _zoomed >= 0 ? L.T("확대", "zoom") : L.T("격자", "grid");
        string cell = _zoomed >= 0 && _zoomed < _cells.Count ? $" · {_cells[_zoomed].HeaderText}" : string.Empty;
        // Version stays in the title: a wall is usually read over somebody's shoulder, and
        // this is the one place it shows without opening a dialog.
        Text = $"Grid Display {AppVersion.Display} — {_preset?.Name} [{mode}]{cell}";
    }


    /// <summary>Minimal single-line text prompt; WinForms has no built-in one.</summary>
    private static string? Prompt(string caption, string initial)
    {
        using var form = new Form
        {
            Text = caption,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 108),
            MinimizeBox = false,
            MaximizeBox = false,
            Font = new Font("Segoe UI", 9f),
        };
        var box = new TextBox { Text = initial, Bounds = new Rectangle(12, 16, 336, 23) };
        var ok = new Button { Text = L.T("확인", "OK"), DialogResult = DialogResult.OK, Bounds = new Rectangle(176, 62, 84, 30) };
        var cancel = new Button { Text = L.T("취소", "Cancel"), DialogResult = DialogResult.Cancel, Bounds = new Rectangle(264, 62, 84, 30) };
        form.Controls.AddRange(new Control[] { box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? box.Text : null;
    }

    // ------------------------------------------------------------------ shutdown

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        PublishHitTestMap();
        _store.SaveSet(_preset);
        _store.SaveSettings();
        _maintenanceTimer.Stop();
        _applyCts?.Cancel();
        _hook.Dispose();

        foreach (var cell in _cells)
        {
            cell.StateChanged -= OnCellStateChanged;
            cell.UserZoomed -= OnCellUserZoom;
            cell.Release();
        }

        _store.SaveSet(_preset);
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------------ long-run upkeep

    /// <summary>
    /// One slow timer for everything that only matters after days of uptime. Five minutes is
    /// coarse enough to cost nothing and fine enough that the two jobs below never drift far
    /// from the schedule the user asked for.
    /// </summary>
    private void StartMaintenance()
    {
        _maintenanceTimer.Interval = (int)TimeSpan.FromMinutes(5).TotalMilliseconds;
        _maintenanceTimer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;

            if (now - _lastHealthUtc >= HealthMonitor.Interval)
            {
                _lastHealthUtc = now;
                HealthMonitor.Sample($"set '{_preset?.Name}' · {_cells.Count} cells" +
                    string.Empty);
            }

            int hours = S.CellRecycleHours;
            if (hours > 0 && now - _lastRecycleUtc >= TimeSpan.FromHours(hours))
            {
                // Not while anyone is looking closely: a zoomed or interactive cell would blink.
                // Leaving _lastRecycleUtc alone means the next tick tries again.
                if (!_interactive && _zoomed < 0) _ = RecycleBrowserCellsAsync();
            }
        };
        _maintenanceTimer.Start();

        HealthMonitor.Sample("startup");
    }

    /// <summary>
    /// Reload every browser cell, staggered. The address is already remembered, so this costs a
    /// redraw and buys back whatever the page accumulated since the last one.
    /// </summary>
    private async Task RecycleBrowserCellsAsync()
    {
        _lastRecycleUtc = DateTime.UtcNow;

        for (int i = 0; i < _cells.Count; i++)
        {
            if (_closing || _interactive || _zoomed >= 0) return;
            if (i >= _preset.Cells.Count || _preset.Cells[i].Kind != CellKind.Browser) continue;
            // A cell with its own interval refreshes on that; leave it alone.
            if (_preset.Cells[i].RefreshMinutes > 0) continue;

            try { _cells[i].Reload(); }
            catch { /* a cell that cannot reload reports it itself */ }

            // Same reason the cells start staggered: reloading six pages at once spikes CPU
            // and would stall input for everyone.
            try { await Task.Delay(2000).ConfigureAwait(true); }
            catch { return; }
        }

        HealthMonitor.Sample("browser cells recycled");
    }

    // ------------------------------------------------------------------ updates

    /// <summary>
    /// Watch for a new version in the background. Deliberately quiet: the first look waits a
    /// minute so it never competes with cells coming up, a found update only raises the banner,
    /// and nothing installs until the user presses a button in the dialog — the wall can be
    /// mid-presentation at any moment.
    /// </summary>
    private void StartUpdateWatch()
    {
        _updateTimer.Interval = 60_000;
        _updateTimer.Tick += async (_, _) => await PollForUpdateAsync().ConfigureAwait(true);
        _updateTimer.Start();
    }

    private async Task PollForUpdateAsync()
    {
        // Re-arm long after the first shot. Whether a check is due is UpdateService's decision;
        // this timer only wakes it often enough to notice the interval passing.
        _updateTimer.Interval = 30 * 60_000;
        if (_closing || !_updates.IsCheckDue()) return;
        try
        {
            var result = await _updates.CheckAsync().ConfigureAwait(true);
            if (_closing) return;
            if (result.Status is UpdateStatus.UpToDate or UpdateStatus.Available) _store.SaveSettings();
            if (result.HasUpdate) ShowBanner(L.T($"{result.Message} 우클릭 메뉴 › 버전 및 업데이트", $"{result.Message} Right-click menu › Version and updates"));
        }
        catch
        {
            // Same rule as a failing cell: an update check never takes the wall down with it.
        }
    }

    private void ShowUpdateDialog()
    {
        using var dialog = new UpdateForm(S, _updates);
        dialog.ShowDialog(this);
        if (dialog.SettingsChanged) _store.SaveSettings();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hook.Dispose();
            _bannerTimer.Dispose();
            _updateTimer.Dispose();
            _maintenanceTimer.Dispose();
            _menu.Dispose();
            _store.Dispose();
            _applyCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
