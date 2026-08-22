using GridDisplay.Controls;
using GridDisplay.Interop;
using GridDisplay.Models;
using GridDisplay.Services;

namespace GridDisplay.Forms;

/// <summary>
/// The display wall itself.
///
/// Two interaction states:
///   view mode  — the grid owns the mouse. Clicking a cell zooms it to the whole monitor,
///                clicking again returns to the grid. Hosted apps never see these clicks.
///   input mode — entered from a zoomed cell with the configured key (default F2). The mouse
///                hook stands down and every keystroke except the release key goes to the app.
///
/// Both states are enforced with low-level hooks rather than overlay windows, because the cells
/// host windows owned by other processes that would otherwise consume the input first.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color BackgroundColor = Color.FromArgb(14, 14, 16);
    private static readonly Color AccentColor = Color.FromArgb(70, 160, 255);

    private readonly StateStore _store = new();
    private readonly InputHook _hook = new();
    private readonly List<CellHost> _cells = new();

    private readonly Panel _bar = new();
    private readonly ComboBox _presetCombo = new();
    private readonly Label _banner = new();
    private readonly System.Windows.Forms.Timer _bannerTimer = new();
    private readonly ContextMenuStrip _menu = new();

    private GridPreset _preset = null!;
    private CancellationTokenSource? _applyCts;

    private int _zoomed = -1;
    private int _selected;
    private int _hover = -1;
    private int _pressedCell = -1;
    private int _menuCell = -1;
    private bool _interactive;
    private bool _fullScreen;
    private bool _barVisible;
    private bool _closing;
    private bool _suppressCombo;
    private long _lastHoverTick;

    private Rectangle _restoreBounds;
    private FormWindowState _restoreState = FormWindowState.Normal;
    private FormBorderStyle _restoreBorder = FormBorderStyle.Sizable;

    public MainForm()
    {
        _store.Load();

        Text = "Grid Display";
        BackColor = BackgroundColor;
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        ResizeRedraw = true;
        KeyPreview = true;

        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        Bounds = Rectangle.Inflate(screen.WorkingArea, -60, -60);

        BuildBar();
        BuildBanner();
        BuildMenu();

        _hook.OnKeyDown = OnGlobalKeyDown;
        _hook.OnMouseDown = OnGlobalMouseDown;
        _hook.OnMouseUp = OnGlobalMouseUp;
        _hook.OnMouseMove = OnGlobalMouseMove;

        Load += async (_, _) => await OnLoadedAsync().ConfigureAwait(true);
        Resize += (_, _) => Relayout();
    }

    private AppState S => _store.State;

    // ------------------------------------------------------------------ startup

    private async Task OnLoadedAsync()
    {
        _hook.Install();
        if (S.StartFullScreen) SetFullScreen(true);
        await LoadPresetAsync(S.ActivePreset()).ConfigureAwait(true);
    }

    /// <summary>Swap the whole grid to <paramref name="preset"/> and bring its cells up.</summary>
    private async Task LoadPresetAsync(GridPreset preset)
    {
        _applyCts?.Cancel();
        _applyCts = new CancellationTokenSource();
        var ct = _applyCts.Token;

        preset.Normalize();
        _preset = preset;
        S.ActivePresetId = preset.Id;

        if (_interactive) SetInteractive(false);
        _zoomed = -1;
        _hover = -1;

        SuspendLayout();
        foreach (var cell in _cells)
        {
            cell.StateChanged -= OnCellStateChanged;
            cell.Release();
            Controls.Remove(cell);
            cell.Dispose();
        }
        _cells.Clear();

        for (int i = 0; i < preset.CellCount; i++)
        {
            var host = new CellHost(i, preset.Cells[i]);
            host.StateChanged += OnCellStateChanged;
            _cells.Add(host);
            Controls.Add(host);
        }
        ResumeLayout(false);

        _selected = Math.Clamp(_selected, 0, Math.Max(0, _cells.Count - 1));
        Relayout();
        Invalidate();
        UpdatePresetCombo();
        UpdateTitle();
        _store.SaveDeferred();

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
        _store.SaveDeferred();
        if (cell.Index == _zoomed) UpdateTitle();
    }

    // ------------------------------------------------------------------ layout

    private Rectangle GridArea()
    {
        var r = ClientRectangle;
        if (_barVisible) r = new Rectangle(r.Left, r.Top + _bar.Height, r.Width, r.Height - _bar.Height);
        return r;
    }

    private void Relayout()
    {
        if (_cells.Count == 0 || _preset is null) return;

        var area = GridArea();
        int gap = Math.Max(0, S.CellGap);

        SuspendLayout();
        if (_zoomed >= 0 && _zoomed < _cells.Count)
        {
            for (int i = 0; i < _cells.Count; i++)
            {
                bool on = i == _zoomed;
                if (on) _cells[i].Bounds = area;
                if (_cells[i].Visible != on) _cells[i].Visible = on;
                if (S.SuspendHiddenCells) _cells[i].SetSuspended(!on);
            }
        }
        else
        {
            int cols = _preset.Columns, rows = _preset.Rows;
            int innerW = Math.Max(0, area.Width - gap * (cols + 1));
            int innerH = Math.Max(0, area.Height - gap * (rows + 1));

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
                    _cells[i].Bounds = Rectangle.FromLTRB(left, top, right, bottom);
                    if (!_cells[i].Visible) _cells[i].Visible = true;
                    if (S.SuspendHiddenCells) _cells[i].SetSuspended(false);
                }
            }
        }

        _bar.Bounds = new Rectangle(0, 0, ClientSize.Width, _bar.Height);
        if (_barVisible) _bar.BringToFront();
        PositionBanner();
        ResumeLayout(true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_zoomed >= 0 || _cells.Count == 0) return;

        using var idle = new Pen(Color.FromArgb(42, 42, 48), 1);
        using var selected = new Pen(Color.FromArgb(120, 120, 135), 2);
        using var hovered = new Pen(AccentColor, 3);

        for (int i = 0; i < _cells.Count; i++)
        {
            if (!_cells[i].Visible) continue;
            var pen = i == _hover ? hovered : i == _selected ? selected : idle;
            int inflate = (int)Math.Ceiling(pen.Width / 2f);
            var r = Rectangle.Inflate(_cells[i].Bounds, inflate, inflate);
            e.Graphics.DrawRectangle(pen, r.Left, r.Top, r.Width - 1, r.Height - 1);
        }
    }

    private void InvalidateCellFrame(int index)
    {
        if (index < 0 || index >= _cells.Count) return;
        int pad = Math.Max(4, S.CellGap);
        Invalidate(Rectangle.Inflate(_cells[index].Bounds, pad, pad), false);
    }

    // ------------------------------------------------------------------ input routing

    private int CellIndexAtScreen(Point screen)
    {
        var pt = new NativeMethods.POINT { X = screen.X, Y = screen.Y };
        var window = NativeMethods.WindowFromPoint(pt);
        if (window == IntPtr.Zero) return -1;
        // Dialogs, context menus and other apps live under a different root — leave them alone.
        if (NativeMethods.GetAncestor(window, NativeMethods.GA_ROOT) != Handle) return -1;

        var client = PointToClient(screen);
        for (int i = 0; i < _cells.Count; i++)
            if (_cells[i].Visible && _cells[i].Bounds.Contains(client)) return i;
        return -1;
    }

    private bool OnGlobalMouseDown(MouseButtons button, Point screen)
    {
        if (_interactive || _closing) return false;
        int index = CellIndexAtScreen(screen);
        if (index < 0) return false;
        _pressedCell = index;
        return true;
    }

    private bool OnGlobalMouseUp(MouseButtons button, Point screen)
    {
        if (_interactive || _closing) return false;
        int index = CellIndexAtScreen(screen);
        int pressed = _pressedCell;
        _pressedCell = -1;
        if (index < 0) return false;
        if (pressed != index) return true;

        // The hook must return promptly; do the real work back on the message loop.
        BeginInvoke(() =>
        {
            if (button == MouseButtons.Right) ShowCellMenu(index, screen);
            else if (button == MouseButtons.Left) ActivateCell(index);
        });
        return true;
    }

    private void OnGlobalMouseMove(Point screen)
    {
        if (_interactive || _closing || _zoomed >= 0) return;

        long now = Environment.TickCount64;
        if (now - _lastHoverTick < 30) return;
        _lastHoverTick = now;

        int index = CellIndexAtScreen(screen);
        if (index == _hover) return;
        int previous = _hover;
        _hover = index;
        BeginInvoke(() =>
        {
            InvalidateCellFrame(previous);
            InvalidateCellFrame(_hover);
        });
    }

    private bool OnGlobalKeyDown(Keys key)
    {
        if (_closing) return false;
        if (NativeMethods.GetForegroundWindow() != Handle) return false;

        var releaseKey = S.InputModeKey;

        if (_interactive)
        {
            // Everything except the release key belongs to the hosted app — including Esc,
            // which plenty of apps need.
            if (key == releaseKey)
            {
                BeginInvoke(() => SetInteractive(false));
                return true;
            }
            return false;
        }

        if (key == releaseKey)
        {
            BeginInvoke(() => SetInteractive(true));
            return true;
        }

        switch (key)
        {
            case Keys.Escape:
                if (_zoomed >= 0)
                {
                    int z = _zoomed;
                    BeginInvoke(() => ToggleZoom(z));
                    return true;
                }
                return false;

            case Keys.Enter:
                BeginInvoke(() => ActivateCell(_selected));
                return true;

            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
                if (_zoomed >= 0) return false;
                BeginInvoke(() => MoveSelection(key));
                return true;

            case Keys.F11:
                BeginInvoke(() => SetFullScreen(!_fullScreen));
                return true;

            case Keys.F1:
                BeginInvoke(ToggleBar);
                return true;
        }

        if ((key & Keys.Control) == Keys.Control)
        {
            var code = key & Keys.KeyCode;
            if (code >= Keys.D1 && code <= Keys.D9)
            {
                int slot = code - Keys.D1;
                BeginInvoke(() =>
                {
                    if (slot < S.Presets.Count) _ = LoadPresetAsync(S.Presets[slot]);
                });
                return true;
            }
            if (code == Keys.R)
            {
                BeginInvoke(() => CellAt(_selected)?.Reload());
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
        ToggleZoom(index);
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

        if (on)
        {
            CellAt(_zoomed)?.FocusContent();
            ShowBanner($"입력 모드 — {KeyName(S.InputModeKey)} 키로 해제");
        }
        else
        {
            // Focus currently sits in another process's window; a plain Focus() will not move it.
            Activate();
            NativeMethods.ForceFocus(Handle);
            ShowBanner("보기 모드 — 클릭하면 격자로 돌아갑니다");
        }
        UpdateTitle();
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

    private void BuildBar()
    {
        _bar.Height = 40;
        _bar.BackColor = Color.FromArgb(28, 28, 33);
        _bar.Visible = false;

        _presetCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _presetCombo.Bounds = new Rectangle(10, 8, 220, 24);
        _presetCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressCombo) return;
            int i = _presetCombo.SelectedIndex;
            if (i >= 0 && i < S.Presets.Count && S.Presets[i].Id != _preset.Id)
                _ = LoadPresetAsync(S.Presets[i]);
        };
        _bar.Controls.Add(_presetCombo);

        _bar.Controls.Add(BarButton("세트 관리…", 240, (_, _) => ManagePresets()));
        _bar.Controls.Add(BarButton("셀 설정…", 348, (_, _) => EditCell(_selected)));
        _bar.Controls.Add(BarButton("전체 화면", 440, (_, _) => SetFullScreen(!_fullScreen)));

        _bar.Controls.Add(new Label
        {
            Text = "F1 도구모음 · F2 입력 모드 · F11 전체 화면 · Ctrl+1~9 세트 · 우클릭 메뉴",
            ForeColor = Color.FromArgb(150, 150, 160),
            AutoSize = true,
            Location = new Point(548, 13),
        });

        Controls.Add(_bar);
    }

    private static Button BarButton(string text, int x, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            Bounds = new Rectangle(x, 7, 100, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(48, 48, 56),
            ForeColor = Color.White,
            TabStop = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += onClick;
        return b;
    }

    private void ToggleBar()
    {
        _barVisible = !_barVisible;
        _bar.Visible = _barVisible;
        if (_barVisible) _bar.BringToFront();
        Relayout();
        Invalidate();
    }

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

    private void BuildMenu()
    {
        _menu.Items.Add("셀 설정…", null, (_, _) => EditCell(_menuCell));
        _menu.Items.Add("새로 고침", null, (_, _) => CellAt(_menuCell)?.Reload());
        _menu.Items.Add("셀 다시 시작", null, (_, _) => RestartCell(_menuCell));
        _menu.Items.Add("셀 비우기", null, (_, _) => ClearCell(_menuCell));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("세트 관리…", null, (_, _) => ManagePresets());
        _menu.Items.Add("현재 배치를 새 세트로 저장…", null, (_, _) => SaveAsNewPreset());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("도구 모음 표시/숨김 (F1)", null, (_, _) => ToggleBar());
        _menu.Items.Add("전체 화면 (F11)", null, (_, _) => SetFullScreen(!_fullScreen));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("종료", null, (_, _) => Close());
    }

    private void ShowCellMenu(int index, Point screen)
    {
        _menuCell = index;
        int previous = _selected;
        _selected = index;
        InvalidateCellFrame(previous);
        InvalidateCellFrame(index);
        _menu.Show(screen);
    }

    // ------------------------------------------------------------------ commands

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
        _store.SaveDeferred();
        UpdateTitle();
        Invalidate();

        try { await _cells[index].ApplyAsync(updated).ConfigureAwait(true); }
        catch { }
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
        _store.SaveDeferred();
        try { await _cells[index].ApplyAsync(empty).ConfigureAwait(true); }
        catch { }
        Invalidate();
    }

    private void ManagePresets()
    {
        // Remembered so an edit to the open set's dimensions is noticed even when the user
        // dismisses the dialog without pressing "open".
        var openId = _preset.Id;
        int openCols = _preset.Columns, openRows = _preset.Rows;

        GridPreset? chosen;
        DialogResult result;
        using (var dialog = new PresetManagerForm(S))
        {
            result = dialog.ShowDialog(this);
            chosen = dialog.Selected;
        }

        _store.SaveDeferred();
        UpdatePresetCombo();
        UpdateTitle();

        if (result == DialogResult.OK && chosen is not null && chosen.Id != openId)
        {
            _ = LoadPresetAsync(chosen);
            return;
        }

        var open = S.Presets.FirstOrDefault(p => p.Id == openId);
        if (open is not null && (open.Columns != openCols || open.Rows != openRows))
            _ = LoadPresetAsync(open);
        else if (open is null)
            _ = LoadPresetAsync(S.ActivePreset());
    }

    private void SaveAsNewPreset()
    {
        var name = Prompt("새 세트 이름", _preset.Name + " 사본");
        if (string.IsNullOrWhiteSpace(name)) return;

        var copy = _preset.Duplicate(name.Trim());
        S.Presets.Add(copy);
        _store.SaveDeferred();
        UpdatePresetCombo();
        ShowBanner($"'{copy.Name}' 세트로 저장했습니다");
    }

    private void UpdatePresetCombo()
    {
        _suppressCombo = true;
        _presetCombo.Items.Clear();
        foreach (var p in S.Presets) _presetCombo.Items.Add($"{p.Name}  ({p.Columns}×{p.Rows})");
        int index = S.Presets.FindIndex(p => p.Id == _preset?.Id);
        if (index >= 0) _presetCombo.SelectedIndex = index;
        _suppressCombo = false;
    }

    private void UpdateTitle()
    {
        string mode = _interactive ? "입력" : _zoomed >= 0 ? "확대" : "격자";
        string cell = _zoomed >= 0 && _zoomed < _cells.Count ? $" · {_cells[_zoomed].HeaderText}" : string.Empty;
        Text = $"Grid Display — {_preset?.Name} [{mode}]{cell}";
    }

    private static string KeyName(Keys key) => key.ToString();

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
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Bounds = new Rectangle(176, 62, 84, 30) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(264, 62, 84, 30) };
        form.Controls.AddRange(new Control[] { box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? box.Text : null;
    }

    // ------------------------------------------------------------------ shutdown

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _applyCts?.Cancel();
        _hook.Dispose();

        foreach (var cell in _cells)
        {
            cell.StateChanged -= OnCellStateChanged;
            cell.Release();
        }

        _store.SaveNow();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hook.Dispose();
            _bannerTimer.Dispose();
            _menu.Dispose();
            _store.Dispose();
            _applyCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
