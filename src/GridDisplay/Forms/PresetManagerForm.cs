using GridDisplay.Models;

namespace GridDisplay.Forms;

/// <summary>Manage saved sets: create, duplicate, rename, resize the grid, delete, apply.</summary>
public sealed class PresetManagerForm : Form
{
    private readonly AppState _state;
    private readonly ListBox _list = new() { IntegralHeight = false };
    private readonly TextBox _name = new();
    private readonly NumericUpDown _cols = new() { Minimum = 1, Maximum = 12, Value = 3 };
    private readonly NumericUpDown _rows = new() { Minimum = 1, Maximum = 12, Value = 2 };
    private readonly Label _summary = new() { ForeColor = SystemColors.GrayText, AutoSize = false };

    /// <summary>
    /// Set while the form is writing to its own controls, so the resulting change events are
    /// recognised as echoes rather than user edits. Both directions must honour it — see
    /// <see cref="LoadSelected"/>.
    /// </summary>
    private bool _loading;

    /// <summary>
    /// A grid resize discards every cell outside the new bounds, and a NumericUpDown raises
    /// ValueChanged on each digit — typing "12" over "3" would pass through 1 column and destroy
    /// four cells on the way. Let the edits settle first, the same way MainForm does.
    /// </summary>
    private readonly System.Windows.Forms.Timer _resizeDebounce = new() { Interval = 600 };
    private GridPreset? _resizeTarget;
    private int _resizeCols, _resizeRows;

    public PresetManagerForm(AppState state)
    {
        _state = state;
        Text = "세트 관리";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9f);
        // Every coordinate below is authored at 96 DPI and scaled through S(): the app runs
        // PerMonitorV2, so the font grows with the monitor's scaling while raw pixel bounds do
        // not, and the captions clipped at 150%. Same approach as CellSettingsForm.
        ClientSize = new Size(S(620), S(380));

        _resizeDebounce.Tick += (_, _) => FlushResize();

        _list.Bounds = new Rectangle(S(12), S(12), S(240), S(320));
        _list.SelectedIndexChanged += (_, _) => LoadSelected();
        Controls.Add(_list);

        Controls.AddRange(new Control[]
        {
            Button("새로 만들기", S(264), S(12), (_, _) => AddPreset()),
            Button("복제", S(264), S(46), (_, _) => DuplicatePreset()),
            Button("삭제", S(264), S(80), (_, _) => DeletePreset()),
        });

        Controls.Add(new Label { Text = "이름", Bounds = new Rectangle(S(400), S(15), S(40), S(20)) });
        _name.Bounds = new Rectangle(S(444), S(12), S(164), S(23));
        _name.TextChanged += (_, _) => ApplyEdits();
        Controls.Add(_name);

        Controls.Add(new Label { Text = "열", Bounds = new Rectangle(S(400), S(49), S(40), S(20)) });
        _cols.Bounds = new Rectangle(S(444), S(46), S(60), S(23));
        _cols.ValueChanged += (_, _) => ApplyEdits();
        Controls.Add(_cols);

        Controls.Add(new Label { Text = "행", Bounds = new Rectangle(S(516), S(49), S(24), S(20)) });
        _rows.Bounds = new Rectangle(S(544), S(46), S(60), S(23));
        _rows.ValueChanged += (_, _) => ApplyEdits();
        Controls.Add(_rows);

        _summary.Bounds = new Rectangle(S(400), S(84), S(208), S(240));
        Controls.Add(_summary);

        var apply = new Button
        {
            Text = "이 세트 열기",
            DialogResult = DialogResult.OK,
            Bounds = new Rectangle(S(400), S(340), S(104), S(30)),
        };
        var close = new Button
        {
            Text = "닫기",
            DialogResult = DialogResult.Cancel,
            Bounds = new Rectangle(S(512), S(340), S(96), S(30)),
        };
        Controls.Add(apply);
        Controls.Add(close);
        AcceptButton = apply;
        CancelButton = close;

        Refill(_state.ActivePreset().Id);
    }

    /// <summary>The set the user chose to open, when the dialog returns OK.</summary>
    public GridPreset? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _state.Presets.Count
        ? _state.Presets[_list.SelectedIndex]
        : null;

    private Button Button(string text, int x, int y, EventHandler onClick)
    {
        var b = new Button { Text = text, Bounds = new Rectangle(x, y, S(120), S(28)) };
        b.Click += onClick;
        return b;
    }

    private static string RowLabel(GridPreset p) => $"{p.Name}  ({p.Columns}×{p.Rows})";

    private void Refill(string? selectId)
    {
        _loading = true;
        _list.Items.Clear();
        foreach (var p in _state.Presets) _list.Items.Add(RowLabel(p));
        int index = Math.Max(0, _state.Presets.FindIndex(p => p.Id == selectId));
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(index, _list.Items.Count - 1);
        _loading = false;
        LoadSelected();
    }

    /// <summary>
    /// Pull the selected set into the editor fields.
    ///
    /// The <see cref="_loading"/> guard is what makes renaming work: replacing a row's text in
    /// <see cref="ApplyEdits"/> makes the ListBox re-raise SelectedIndexChanged, which lands
    /// here. Without the guard that wrote <c>_name.Text</c> back on every keystroke, which reset
    /// the caret to the start and left Korean IME composition wedged mid-syllable.
    /// </summary>
    private void LoadSelected()
    {
        if (_loading) return;

        // Moving to another set commits whatever resize the previous one was still waiting on.
        FlushResize();

        var p = Selected;
        if (p is null) return;

        _loading = true;
        _name.Text = p.Name;
        _cols.Value = Math.Clamp(p.Columns, 1, 12);
        _rows.Value = Math.Clamp(p.Rows, 1, 12);
        _loading = false;

        ShowSummary(p);
    }

    private void ShowSummary(GridPreset p) =>
        _summary.Text = string.Join(
            Environment.NewLine,
            p.Cells.Select((c, i) => $"{i + 1}. {Truncate(c.DisplayName, 26)}"));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>
    /// Push the editor fields into the selected set. Deliberately one-way: it refreshes the row
    /// label and the cell summary but never the name or size inputs, because the user is typing
    /// in them.
    /// </summary>
    private void ApplyEdits()
    {
        if (_loading) return;

        var p = Selected;
        if (p is null) return;

        p.Name = string.IsNullOrWhiteSpace(_name.Text) ? p.Name : _name.Text.Trim();

        int cols = (int)_cols.Value, rows = (int)_rows.Value;
        if (cols != p.Columns || rows != p.Rows)
        {
            // Remember which set the pending resize belongs to: the user may pick another one
            // before the timer fires, and resizing whatever happens to be selected then would
            // reshape the wrong grid.
            _resizeTarget = p;
            _resizeCols = cols;
            _resizeRows = rows;
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        }
        else if (ReferenceEquals(_resizeTarget, p))
        {
            // Back at the original size before the timer fired — nothing left to apply.
            _resizeDebounce.Stop();
            _resizeTarget = null;
        }

        RefreshRow(p);
        ShowSummary(p);
    }

    /// <summary>Repaint one row's label without disturbing the selection or the editor fields.</summary>
    private void RefreshRow(GridPreset p)
    {
        int index = _state.Presets.IndexOf(p);
        if (index < 0 || index >= _list.Items.Count) return;

        int selected = _list.SelectedIndex;
        _loading = true;
        _list.Items[index] = RowLabel(p);
        // Rewriting an item can drop the selection; put it back before anything reads Selected.
        if (selected >= 0 && _list.SelectedIndex != selected) _list.SelectedIndex = selected;
        _loading = false;
    }

    /// <summary>
    /// Apply a resize that is still waiting on the debounce. Called before anything that reads
    /// or reorders the sets, and before the dialog closes, so a pending edit is never lost.
    /// </summary>
    private void FlushResize()
    {
        _resizeDebounce.Stop();

        var p = _resizeTarget;
        _resizeTarget = null;
        if (p is null) return;
        if (_resizeCols == p.Columns && _resizeRows == p.Rows) return;

        p.Resize(_resizeCols, _resizeRows);
        RefreshRow(p);
        if (ReferenceEquals(p, Selected)) ShowSummary(p);
    }

    private void AddPreset()
    {
        FlushResize();
        var p = GridPreset.CreateDefault();
        p.Name = $"세트 {_state.Presets.Count + 1}";
        _state.Presets.Add(p);
        Refill(p.Id);
    }

    private void DuplicatePreset()
    {
        FlushResize();
        var p = Selected;
        if (p is null) return;
        var copy = p.Duplicate(p.Name + " 사본");
        _state.Presets.Add(copy);
        Refill(copy.Id);
    }

    private void DeletePreset()
    {
        FlushResize();
        var p = Selected;
        if (p is null) return;
        if (_state.Presets.Count <= 1)
        {
            MessageBox.Show(this, "마지막 세트는 삭제할 수 없습니다.", "확인", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"'{p.Name}' 세트를 삭제할까요?", "삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            != DialogResult.Yes) return;

        _state.Presets.Remove(p);
        Refill(_state.Presets[0].Id);
    }

    /// <summary>A pending resize must survive the dialog closing, however it is dismissed.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        FlushResize();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _resizeDebounce.Dispose();
        base.Dispose(disposing);
    }

    private int S(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);
}
