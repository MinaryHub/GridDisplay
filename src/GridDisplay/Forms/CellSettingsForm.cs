using GridDisplay.Models;

namespace GridDisplay.Forms;

/// <summary>Per-cell editor: choose empty / browser / program and its parameters.</summary>
public sealed class CellSettingsForm : Form
{
    private readonly RadioButton _rbEmpty = new() { Text = "비어 있음", AutoSize = true };
    private readonly RadioButton _rbBrowser = new() { Text = "브라우저 (WebView2)", AutoSize = true };
    private readonly RadioButton _rbProgram = new() { Text = "프로그램", AutoSize = true };

    private readonly TextBox _title = new();
    private readonly TextBox _url = new();
    private readonly TextBox _profile = new();
    private readonly NumericUpDown _zoom = new() { DecimalPlaces = 2, Minimum = 0.25M, Maximum = 4.0M, Increment = 0.05M, Value = 1.0M };

    private readonly TextBox _exe = new();
    private readonly TextBox _args = new();
    private readonly TextBox _workDir = new();
    private readonly TextBox _titleMatch = new();
    private readonly CheckBox _attach = new() { Text = "이미 실행 중인 창을 가져오기", AutoSize = true };
    private readonly CheckBox _closeOnRelease = new() { Text = "셀 해제 시 프로그램 종료", AutoSize = true, Checked = true };

    private readonly GroupBox _browserBox = new() { Text = "브라우저" };
    private readonly GroupBox _programBox = new() { Text = "프로그램" };

    public CellSettingsForm(int cellIndex, CellConfig config)
    {
        Result = config.Clone();

        Text = $"셀 {cellIndex + 1} 설정";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(560, 520);
        Font = new Font("Segoe UI", 9f);

        int y = 12;

        var kindBox = new GroupBox { Text = "종류", Bounds = new Rectangle(12, y, 536, 56) };
        _rbEmpty.Location = new Point(16, 24);
        _rbBrowser.Location = new Point(120, 24);
        _rbProgram.Location = new Point(300, 24);
        kindBox.Controls.AddRange(new Control[] { _rbEmpty, _rbBrowser, _rbProgram });
        Controls.Add(kindBox);
        y += 68;

        Controls.Add(new Label { Text = "표시 이름", Bounds = new Rectangle(16, y + 3, 80, 20) });
        _title.Bounds = new Rectangle(100, y, 448, 23);
        Controls.Add(_title);
        y += 34;

        // --- browser ---
        _browserBox.Bounds = new Rectangle(12, y, 536, 130);
        _browserBox.Controls.Add(new Label { Text = "주소", Bounds = new Rectangle(16, 27, 80, 20) });
        _url.Bounds = new Rectangle(100, 24, 420, 23);
        _browserBox.Controls.Add(_url);
        _browserBox.Controls.Add(new Label { Text = "프로필", Bounds = new Rectangle(16, 60, 80, 20) });
        _profile.Bounds = new Rectangle(100, 57, 180, 23);
        _browserBox.Controls.Add(_profile);
        _browserBox.Controls.Add(new Label { Text = "(비우면 공용 세션)", Bounds = new Rectangle(288, 60, 130, 20), ForeColor = SystemColors.GrayText });
        _browserBox.Controls.Add(new Label { Text = "확대 배율", Bounds = new Rectangle(16, 93, 80, 20) });
        _zoom.Bounds = new Rectangle(100, 90, 80, 23);
        _browserBox.Controls.Add(_zoom);
        Controls.Add(_browserBox);

        // --- program (same slot; only one is visible at a time) ---
        _programBox.Bounds = new Rectangle(12, y, 536, 230);
        _programBox.Controls.Add(new Label { Text = "실행 파일", Bounds = new Rectangle(16, 27, 80, 20) });
        _exe.Bounds = new Rectangle(100, 24, 340, 23);
        _programBox.Controls.Add(_exe);
        var browse = new Button { Text = "찾아보기…", Bounds = new Rectangle(448, 23, 72, 25) };
        browse.Click += (_, _) => BrowseExecutable();
        _programBox.Controls.Add(browse);

        _programBox.Controls.Add(new Label { Text = "인수", Bounds = new Rectangle(16, 60, 80, 20) });
        _args.Bounds = new Rectangle(100, 57, 420, 23);
        _programBox.Controls.Add(_args);

        _programBox.Controls.Add(new Label { Text = "작업 폴더", Bounds = new Rectangle(16, 93, 80, 20) });
        _workDir.Bounds = new Rectangle(100, 90, 420, 23);
        _programBox.Controls.Add(_workDir);

        _programBox.Controls.Add(new Label { Text = "창 제목 포함", Bounds = new Rectangle(16, 126, 80, 20) });
        _titleMatch.Bounds = new Rectangle(100, 123, 420, 23);
        _programBox.Controls.Add(_titleMatch);
        _programBox.Controls.Add(new Label
        {
            Text = "창을 여러 개 여는 앱에서 올바른 창을 고를 때 사용합니다.",
            Bounds = new Rectangle(100, 148, 420, 18),
            ForeColor = SystemColors.GrayText,
        });

        _attach.Location = new Point(100, 172);
        _closeOnRelease.Location = new Point(100, 196);
        _programBox.Controls.Add(_attach);
        _programBox.Controls.Add(_closeOnRelease);
        Controls.Add(_programBox);

        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Bounds = new Rectangle(376, 470, 84, 30) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(464, 470, 84, 30) };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        _rbEmpty.CheckedChanged += (_, _) => SyncVisibility();
        _rbBrowser.CheckedChanged += (_, _) => SyncVisibility();
        _rbProgram.CheckedChanged += (_, _) => SyncVisibility();

        LoadFrom(config);
        SyncVisibility();
        FormClosing += OnFormClosing;
    }

    public CellConfig Result { get; private set; }

    private void LoadFrom(CellConfig c)
    {
        _rbEmpty.Checked = c.Kind == CellKind.Empty;
        _rbBrowser.Checked = c.Kind == CellKind.Browser;
        _rbProgram.Checked = c.Kind == CellKind.Program;
        _title.Text = c.Title;
        _url.Text = c.Url;
        _profile.Text = c.ProfileName;
        _zoom.Value = (decimal)Math.Clamp(c.ZoomFactor <= 0 ? 1.0 : c.ZoomFactor, 0.25, 4.0);
        _exe.Text = c.ExecutablePath;
        _args.Text = c.Arguments;
        _workDir.Text = c.WorkingDirectory;
        _titleMatch.Text = c.WindowTitleMatch;
        _attach.Checked = c.AttachToRunning;
        _closeOnRelease.Checked = c.CloseOnRelease;
    }

    private void SyncVisibility()
    {
        _browserBox.Visible = _rbBrowser.Checked;
        _programBox.Visible = _rbProgram.Checked;
    }

    private void BrowseExecutable()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "실행 파일 선택",
            Filter = "프로그램 (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _exe.Text = dlg.FileName;
        if (string.IsNullOrWhiteSpace(_title.Text))
            _title.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK) return;

        var kind = _rbBrowser.Checked ? CellKind.Browser : _rbProgram.Checked ? CellKind.Program : CellKind.Empty;

        if (kind == CellKind.Browser && string.IsNullOrWhiteSpace(_url.Text))
        {
            MessageBox.Show(this, "주소를 입력하세요.", "확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
        }
        if (kind == CellKind.Program && string.IsNullOrWhiteSpace(_exe.Text) && !_attach.Checked)
        {
            MessageBox.Show(this, "실행 파일을 지정하거나 '실행 중인 창 가져오기'를 켜세요.", "확인",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
        }

        Result = new CellConfig
        {
            Kind = kind,
            Title = _title.Text.Trim(),
            Url = kind == CellKind.Browser ? _url.Text.Trim() : string.Empty,
            ProfileName = _profile.Text.Trim(),
            ZoomFactor = (double)_zoom.Value,
            ExecutablePath = _exe.Text.Trim(),
            Arguments = _args.Text.Trim(),
            WorkingDirectory = _workDir.Text.Trim(),
            WindowTitleMatch = _titleMatch.Text.Trim(),
            AttachToRunning = _attach.Checked,
            CloseOnRelease = _closeOnRelease.Checked,
        };
    }
}
