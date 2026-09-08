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
    private readonly CheckBox _fitToCell = new() { Text = "셀 크기에 맞춰 전체 보기 (자동 배율)", AutoSize = true, Checked = true };
    private readonly NumericUpDown _zoom = new() { DecimalPlaces = 2, Minimum = 0.25M, Maximum = 4.0M, Increment = 0.05M, Value = 1.0M };
    private readonly CheckBox _preventLogout = new() { Text = "로그아웃 방지 (주기적으로 활동 신호 전송)", AutoSize = true };
    private readonly NumericUpDown _keepAliveSeconds = new() { Minimum = 10, Maximum = 3600, Increment = 10, Value = 60 };
    private readonly NumericUpDown _refreshMinutes = new() { Minimum = 0, Maximum = 1440, Increment = 5, Value = 0 };
    private readonly CheckBox _rememberLast = new() { Text = "마지막으로 열었던 주소로 시작 (끄면 항상 위 주소)", AutoSize = true };
    private readonly Label _lastUrl = new() { AutoSize = false, ForeColor = SystemColors.GrayText, AutoEllipsis = true };
    private readonly Button _resetUrl = new() { Text = "기본 주소로" };
    private string _rememberedUrl = string.Empty;
    /// <summary>The address as it was when the dialog opened, to tell an edit from a re-save.</summary>
    private readonly string _originalUrl;

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
        _originalUrl = config.Url ?? string.Empty;

        Text = $"셀 {cellIndex + 1} 설정";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9f);
        // Every coordinate below is authored at 96 DPI and scaled: the app runs PerMonitorV2, so
        // the font grows with the monitor's scaling and raw pixel bounds would clip the captions.
        ClientSize = new Size(S(560), S(569));

        int y = S(12);

        var kindBox = new GroupBox { Text = "종류", Bounds = new Rectangle(S(12), y, S(536), S(56)) };
        // Browser first: it is what nearly every cell on a wall is, and it is the kind whose
        // fields should already be on screen when the dialog opens. Measured widths rather than
        // fixed columns, so a long caption cannot cover the next radio's glyph.
        int rx = S(16);
        foreach (var rb in new[] { _rbBrowser, _rbProgram, _rbEmpty })
        {
            rb.Location = new Point(rx, S(24));
            rx += rb.PreferredSize.Width + S(20);
        }
        kindBox.Controls.AddRange(new Control[] { _rbBrowser, _rbProgram, _rbEmpty });
        Controls.Add(kindBox);
        y += S(68);

        Controls.Add(new Label { Text = "표시 이름", Bounds = new Rectangle(S(16), y + S(3), S(80), S(20)) });
        _title.Bounds = new Rectangle(S(100), y, S(448), S(23));
        Controls.Add(_title);
        y += S(34);

        // --- browser ---
        _browserBox.Bounds = new Rectangle(S(12), y, S(536), S(292));
        _browserBox.Controls.Add(new Label { Text = "주소", Bounds = new Rectangle(S(16), S(27), S(80), S(20)) });
        _url.Bounds = new Rectangle(S(100), S(24), S(420), S(23));
        _browserBox.Controls.Add(_url);
        _rememberLast.Location = new Point(S(100), S(53));
        _browserBox.Controls.Add(_rememberLast);
        _lastUrl.Bounds = new Rectangle(S(100), S(76), S(330), S(18));
        _browserBox.Controls.Add(_lastUrl);
        _resetUrl.Bounds = new Rectangle(S(436), S(72), S(84), S(25));
        _resetUrl.Click += (_, _) =>
        {
            // Forget where it wandered to; the next start goes back to the address above.
            _rememberedUrl = string.Empty;
            ShowRememberedUrl();
        };
        _browserBox.Controls.Add(_resetUrl);

        _browserBox.Controls.Add(new Label { Text = "프로필", Bounds = new Rectangle(S(16), S(109), S(80), S(20)) });
        _profile.Bounds = new Rectangle(S(100), S(106), S(180), S(23));
        _browserBox.Controls.Add(_profile);
        _browserBox.Controls.Add(new Label { Text = "(비우면 공용 세션)", Bounds = new Rectangle(S(288), S(109), S(140), S(20)), ForeColor = SystemColors.GrayText });
        _fitToCell.Location = new Point(S(100), S(139));
        _browserBox.Controls.Add(_fitToCell);
        _browserBox.Controls.Add(new Label { Text = "확대 배율", Bounds = new Rectangle(S(16), S(170), S(80), S(20)) });
        _zoom.Bounds = new Rectangle(S(100), S(167), S(80), S(23));
        _browserBox.Controls.Add(_zoom);
        _browserBox.Controls.Add(new Label
        {
            Text = "(자동 배율을 끈 경우에만 사용)",
            Bounds = new Rectangle(S(188), S(170), S(210), S(20)),
            ForeColor = SystemColors.GrayText,
        });
        _preventLogout.Location = new Point(S(100), S(199));
        _browserBox.Controls.Add(_preventLogout);
        _browserBox.Controls.Add(new Label { Text = "확인 주기", Bounds = new Rectangle(S(16), S(230), S(80), S(20)) });
        _keepAliveSeconds.Bounds = new Rectangle(S(100), S(227), S(74), S(23));
        _browserBox.Controls.Add(_keepAliveSeconds);
        _browserBox.Controls.Add(new Label
        {
            Text = "초 · 보기 모드에서만 동작",
            Bounds = new Rectangle(S(182), S(230), S(200), S(20)),
            ForeColor = SystemColors.GrayText,
        });
        _browserBox.Controls.Add(new Label { Text = "새로 고침", Bounds = new Rectangle(S(16), S(261), S(80), S(20)) });
        _refreshMinutes.Bounds = new Rectangle(S(100), S(258), S(74), S(23));
        _browserBox.Controls.Add(_refreshMinutes);
        _browserBox.Controls.Add(new Label
        {
            Text = "분마다 · 0이면 전체 설정을 따름",
            Bounds = new Rectangle(S(182), S(261), S(240), S(20)),
            ForeColor = SystemColors.GrayText,
        });
        Controls.Add(_browserBox);

        // --- program (same slot; only one is visible at a time) ---
        _programBox.Bounds = new Rectangle(S(12), y, S(536), S(230));
        _programBox.Controls.Add(new Label { Text = "실행 파일", Bounds = new Rectangle(S(16), S(27), S(80), S(20)) });
        _exe.Bounds = new Rectangle(S(100), S(24), S(340), S(23));
        _programBox.Controls.Add(_exe);
        var browse = new Button { Text = "찾아보기…", Bounds = new Rectangle(S(448), S(23), S(76), S(25)) };
        browse.Click += (_, _) => BrowseExecutable();
        _programBox.Controls.Add(browse);

        _programBox.Controls.Add(new Label { Text = "인수", Bounds = new Rectangle(S(16), S(60), S(80), S(20)) });
        _args.Bounds = new Rectangle(S(100), S(57), S(420), S(23));
        _programBox.Controls.Add(_args);

        _programBox.Controls.Add(new Label { Text = "작업 폴더", Bounds = new Rectangle(S(16), S(93), S(80), S(20)) });
        _workDir.Bounds = new Rectangle(S(100), S(90), S(420), S(23));
        _programBox.Controls.Add(_workDir);

        _programBox.Controls.Add(new Label { Text = "창 제목 포함", Bounds = new Rectangle(S(16), S(126), S(84), S(20)) });
        _titleMatch.Bounds = new Rectangle(S(100), S(123), S(420), S(23));
        _programBox.Controls.Add(_titleMatch);
        _programBox.Controls.Add(new Label
        {
            Text = "창을 여러 개 여는 앱에서 올바른 창을 고를 때 사용합니다.",
            Bounds = new Rectangle(S(100), S(148), S(420), S(18)),
            ForeColor = SystemColors.GrayText,
        });

        _attach.Location = new Point(S(100), S(172));
        _closeOnRelease.Location = new Point(S(100), S(196));
        _programBox.Controls.Add(_attach);
        _programBox.Controls.Add(_closeOnRelease);
        Controls.Add(_programBox);

        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Bounds = new Rectangle(S(376), S(470), S(84), S(30)) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(S(464), S(470), S(84), S(30)) };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        _rbEmpty.CheckedChanged += (_, _) => SyncVisibility();
        _rbBrowser.CheckedChanged += (_, _) => SyncVisibility();
        _rbProgram.CheckedChanged += (_, _) => SyncVisibility();
        _fitToCell.CheckedChanged += (_, _) => _zoom.Enabled = !_fitToCell.Checked;
        _preventLogout.CheckedChanged += (_, _) => _keepAliveSeconds.Enabled = _preventLogout.Checked;

        LoadFrom(config);
        SyncVisibility();
        FormClosing += OnFormClosing;
    }

    public CellConfig Result { get; private set; }

    /// <summary>Scale a coordinate authored at 96 DPI to this monitor.</summary>
    /// <summary>
    /// Show where the cell has drifted to, and offer to forget it. Without this the address box
    /// says one thing and the screen shows another, with no hint that the two differ.
    /// </summary>
    private void ShowRememberedUrl()
    {
        bool drifted = !string.IsNullOrWhiteSpace(_rememberedUrl)
            && !string.Equals(_rememberedUrl, _url.Text?.Trim(), StringComparison.OrdinalIgnoreCase);

        _lastUrl.Text = drifted ? $"현재 열려 있는 주소: {_rememberedUrl}" : string.Empty;
        _lastUrl.Visible = drifted;
        _resetUrl.Visible = drifted;
        _resetUrl.Enabled = drifted;
    }

    private int S(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);

    private void LoadFrom(CellConfig c)
    {
        // An unconfigured cell opens on the browser fields rather than on a blank dialog; picking
        // "비어 있음" is then an explicit choice, and "셀 비우기" in the cell menu still clears.
        _rbProgram.Checked = c.Kind == CellKind.Program;
        _rbEmpty.Checked = false;
        _rbBrowser.Checked = c.Kind != CellKind.Program;
        _title.Text = c.Title;
        _url.Text = c.Url;
        _rememberLast.Checked = c.RememberLastUrl;
        _rememberedUrl = c.LastUrl ?? string.Empty;
        ShowRememberedUrl();
        _profile.Text = c.ProfileName;
        _fitToCell.Checked = c.FitToCell;
        _zoom.Value = (decimal)Math.Clamp(c.ZoomFactor <= 0 ? 1.0 : c.ZoomFactor, 0.25, 4.0);
        _zoom.Enabled = !c.FitToCell;
        _preventLogout.Checked = c.PreventLogout;
        _keepAliveSeconds.Value = Math.Clamp(c.KeepAliveSeconds <= 0 ? 60 : c.KeepAliveSeconds, 10, 3600);
        _refreshMinutes.Value = Math.Clamp(c.RefreshMinutes, 0, 1440);
        _keepAliveSeconds.Enabled = c.PreventLogout;
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

        // Typing a new address is the clearest possible statement of where this cell belongs, so
        // whatever it had drifted to is dropped — otherwise, with resume on, the new address
        // would be saved and then ignored on the next start.
        var url = kind == CellKind.Browser ? _url.Text.Trim() : string.Empty;
        if (!string.Equals(url, _originalUrl.Trim(), StringComparison.Ordinal))
            _rememberedUrl = string.Empty;

        Result = new CellConfig
        {
            Kind = kind,
            Title = _title.Text.Trim(),
            Url = url,
            ProfileName = _profile.Text.Trim(),
            FitToCell = _fitToCell.Checked,
            ZoomFactor = (double)_zoom.Value,
            PreventLogout = _preventLogout.Checked,
            KeepAliveSeconds = (int)_keepAliveSeconds.Value,
            RememberLastUrl = _rememberLast.Checked,
            LastUrl = _rememberedUrl,
            RefreshMinutes = (int)_refreshMinutes.Value,
            ExecutablePath = _exe.Text.Trim(),
            Arguments = _args.Text.Trim(),
            WorkingDirectory = _workDir.Text.Trim(),
            WindowTitleMatch = _titleMatch.Text.Trim(),
            AttachToRunning = _attach.Checked,
            CloseOnRelease = _closeOnRelease.Checked,
        };
    }
}
