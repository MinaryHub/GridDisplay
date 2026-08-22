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
    private bool _loading;

    public PresetManagerForm(AppState state)
    {
        _state = state;
        Text = "세트 관리";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(620, 380);
        Font = new Font("Segoe UI", 9f);

        _list.Bounds = new Rectangle(12, 12, 240, 320);
        _list.SelectedIndexChanged += (_, _) => LoadSelected();
        Controls.Add(_list);

        var newBtn = Button("새로 만들기", 264, 12, (_, _) => AddPreset());
        var dupBtn = Button("복제", 264, 46, (_, _) => DuplicatePreset());
        var delBtn = Button("삭제", 264, 80, (_, _) => DeletePreset());
        Controls.AddRange(new Control[] { newBtn, dupBtn, delBtn });

        Controls.Add(new Label { Text = "이름", Bounds = new Rectangle(400, 15, 40, 20) });
        _name.Bounds = new Rectangle(444, 12, 164, 23);
        _name.TextChanged += (_, _) => ApplyEdits();
        Controls.Add(_name);

        Controls.Add(new Label { Text = "열", Bounds = new Rectangle(400, 49, 40, 20) });
        _cols.Bounds = new Rectangle(444, 46, 60, 23);
        _cols.ValueChanged += (_, _) => ApplyEdits();
        Controls.Add(_cols);

        Controls.Add(new Label { Text = "행", Bounds = new Rectangle(516, 49, 24, 20) });
        _rows.Bounds = new Rectangle(544, 46, 60, 23);
        _rows.ValueChanged += (_, _) => ApplyEdits();
        Controls.Add(_rows);

        _summary.Bounds = new Rectangle(400, 84, 208, 240);
        Controls.Add(_summary);

        var apply = new Button { Text = "이 세트 열기", DialogResult = DialogResult.OK, Bounds = new Rectangle(400, 340, 104, 30) };
        var close = new Button { Text = "닫기", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(512, 340, 96, 30) };
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

    private static Button Button(string text, int x, int y, EventHandler onClick)
    {
        var b = new Button { Text = text, Bounds = new Rectangle(x, y, 120, 28) };
        b.Click += onClick;
        return b;
    }

    private void Refill(string? selectId)
    {
        _loading = true;
        _list.Items.Clear();
        foreach (var p in _state.Presets)
            _list.Items.Add($"{p.Name}  ({p.Columns}×{p.Rows})");
        int index = Math.Max(0, _state.Presets.FindIndex(p => p.Id == selectId));
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(index, _list.Items.Count - 1);
        _loading = false;
        LoadSelected();
    }

    private void LoadSelected()
    {
        var p = Selected;
        if (p is null) return;
        _loading = true;
        _name.Text = p.Name;
        _cols.Value = Math.Clamp(p.Columns, 1, 12);
        _rows.Value = Math.Clamp(p.Rows, 1, 12);
        _loading = false;

        var lines = p.Cells.Select((c, i) => $"{i + 1}. {Truncate(c.DisplayName, 26)}");
        _summary.Text = string.Join(Environment.NewLine, lines);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void ApplyEdits()
    {
        if (_loading) return;
        var p = Selected;
        if (p is null) return;

        p.Name = string.IsNullOrWhiteSpace(_name.Text) ? p.Name : _name.Text.Trim();
        int cols = (int)_cols.Value, rows = (int)_rows.Value;
        if (cols != p.Columns || rows != p.Rows) p.Resize(cols, rows);

        int index = _list.SelectedIndex;
        _loading = true;
        _list.Items[index] = $"{p.Name}  ({p.Columns}×{p.Rows})";
        _loading = false;
        LoadSelected();
    }

    private void AddPreset()
    {
        var p = GridPreset.CreateDefault();
        p.Name = $"세트 {_state.Presets.Count + 1}";
        _state.Presets.Add(p);
        Refill(p.Id);
    }

    private void DuplicatePreset()
    {
        var p = Selected;
        if (p is null) return;
        var copy = p.Duplicate(p.Name + " 사본");
        _state.Presets.Add(copy);
        Refill(copy.Id);
    }

    private void DeletePreset()
    {
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
}
