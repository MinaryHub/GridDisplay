using GridDisplay.Models;
using GridDisplay.Services;

namespace GridDisplay.Forms;

/// <summary>Per-cell editor: choose empty / browser / program and its parameters.</summary>
public sealed class CellSettingsForm : Form
{
    /// <summary>
    /// Extra logical px for the caption column in English. "Window title contains" and
    /// "Working folder" do not fit the 80px column the Korean captions were laid out for, so
    /// everything right of that column, and the dialog itself, moves over by this much.
    /// </summary>
    private static readonly int Dx = L.Korean ? 0 : 60;

    private readonly RadioButton _rbEmpty = new() { Text = L.T("비어 있음", "Empty"), AutoSize = true };
    private readonly RadioButton _rbBrowser = new() { Text = L.T("브라우저 (WebView2)", "Browser (WebView2)"), AutoSize = true };
    private readonly RadioButton _rbProgram = new() { Text = L.T("프로그램", "Program"), AutoSize = true };

    private readonly TextBox _title = new();
    private readonly TextBox _url = new();
    private readonly TextBox _profile = new();
    private readonly CheckBox _fitToCell = new() { Text = L.T("셀 크기에 맞춰 전체 보기 (자동 배율)", "Fit page to cell (auto zoom)"), AutoSize = true, Checked = true };
    private readonly NumericUpDown _zoom = new() { DecimalPlaces = 2, Minimum = 0.25M, Maximum = 4.0M, Increment = 0.05M, Value = 1.0M };
    private readonly CheckBox _preventLogout = new() { Text = L.T("로그아웃 방지 (주기적으로 활동 신호 전송)", "Prevent sign-out (send activity signals periodically)"), AutoSize = true };
    private readonly NumericUpDown _keepAliveSeconds = new() { Minimum = 10, Maximum = 3600, Increment = 10, Value = 60 };
    private readonly NumericUpDown _refreshMinutes = new() { Minimum = 0, Maximum = 1440, Increment = 5, Value = 0 };
    private readonly CheckBox _keepSignedIn = new() { Text = L.T("다시 켜도 로그인 유지 (같은 프로필 전체에 적용)", "Stay signed in after restart (applies to the whole profile)"), AutoSize = true };
    private readonly CheckBox _liveInGrid = new() { Text = L.T("격자에서도 실시간으로 표시 (끄면 정지 화면 · 자원 절약)", "Live in the grid too (off: still picture, saves resources)"), AutoSize = true };
    private readonly CheckBox _returnAfterLogin = new() { Text = L.T("로그인 화면을 거친 뒤 원래 화면으로 돌아가기", "Return to the page after signing in"), AutoSize = true, Checked = true };
    private readonly CheckBox _rememberLast = new() { Text = L.T("마지막으로 열었던 주소로 시작 (끄면 항상 위 주소)", "Start at the last opened address (off: always the one above)"), AutoSize = true };
    private readonly Label _lastUrl = new() { AutoSize = false, ForeColor = SystemColors.GrayText, AutoEllipsis = true };
    private readonly Button _resetUrl = new() { Text = L.T("기본 주소로", "Use default") };
    private string _rememberedUrl = string.Empty;
    /// <summary>The address as it was when the dialog opened, to tell an edit from a re-save.</summary>
    private readonly string _originalUrl;
    private double _zoomScale;
    private readonly Label _userZoom = new() { AutoSize = false, ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleRight };
    private readonly Button _resetZoom = new() { Text = L.T("원래대로", "Reset") };

    private readonly TextBox _exe = new();
    private readonly TextBox _args = new();
    private readonly TextBox _workDir = new();
    private readonly TextBox _titleMatch = new();
    private readonly CheckBox _attach = new() { Text = L.T("이미 실행 중인 창을 가져오기", "Take over a window that is already running"), AutoSize = true };
    private readonly CheckBox _closeOnRelease = new() { Text = L.T("셀 해제 시 프로그램 종료", "Close the program when the cell is cleared"), AutoSize = true, Checked = true };

    private readonly GroupBox _browserBox = new() { Text = L.T("브라우저", "Browser") };
    private readonly GroupBox _programBox = new() { Text = L.T("프로그램", "Program") };

    public CellSettingsForm(int cellIndex, CellConfig config)
    {
        Result = config.Clone();
        _originalUrl = config.Url ?? string.Empty;
        _zoomScale = config.ZoomScale;

        Text = L.T($"셀 {cellIndex + 1} 설정", $"Cell {cellIndex + 1} settings");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9f);
        // Every coordinate below is authored at 96 DPI and scaled: the app runs PerMonitorV2, so
        // the font grows with the monitor's scaling and raw pixel bounds would clip the captions.
        ClientSize = new Size(S(560 + Dx), S(642));

        int y = S(12);

        var kindBox = new GroupBox { Text = L.T("종류", "Type"), Bounds = new Rectangle(S(12), y, S(536 + Dx), S(56)) };
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

        Controls.Add(new Label { Text = L.T("표시 이름", "Display name"), Bounds = new Rectangle(S(16), y + S(3), S(80 + Dx), S(20)) });
        _title.Bounds = new Rectangle(S(100 + Dx), y, S(448), S(23));
        Controls.Add(_title);
        y += S(34);

        // --- browser ---
        // Four sections in the order a cell is set up: where it points, how it looks, how it
        // stays signed in, how often it reloads. Each starts with a heading and a rule.
        _browserBox.Bounds = new Rectangle(S(12), y, S(536 + Dx), S(472));

        AddSection(_browserBox, L.T("주소", "Address"), S(20));
        _browserBox.Controls.Add(new Label { Text = L.T("주소", "Address"), Bounds = new Rectangle(S(16), S(47), S(80 + Dx), S(20)) });
        _url.Bounds = new Rectangle(S(100 + Dx), S(44), S(420), S(23));
        _browserBox.Controls.Add(_url);
        _rememberLast.Location = new Point(S(100 + Dx), S(73));
        _browserBox.Controls.Add(_rememberLast);
        _lastUrl.Bounds = new Rectangle(S(100 + Dx), S(98), S(330), S(18));
        _browserBox.Controls.Add(_lastUrl);
        _resetUrl.Bounds = new Rectangle(S(436 + Dx), S(94), S(84), S(25));
        _resetUrl.Click += (_, _) =>
        {
            // Forget where it wandered to; the next start goes back to the address above.
            _rememberedUrl = string.Empty;
            ShowRememberedUrl();
        };
        _browserBox.Controls.Add(_resetUrl);

        AddSection(_browserBox, L.T("화면", "View"), S(128));
        _fitToCell.Location = new Point(S(100 + Dx), S(150));
        _browserBox.Controls.Add(_fitToCell);
        _userZoom.Bounds = new Rectangle(S(320 + Dx), S(152), S(110), S(20));
        _browserBox.Controls.Add(_userZoom);
        _resetZoom.Bounds = new Rectangle(S(436 + Dx), S(147), S(84), S(25));
        _resetZoom.Click += (_, _) =>
        {
            _zoomScale = 1.0;
            ShowUserZoom();
        };
        _browserBox.Controls.Add(_resetZoom);
        _browserBox.Controls.Add(new Label { Text = L.T("확대 배율", "Zoom"), Bounds = new Rectangle(S(16), S(181), S(80 + Dx), S(20)) });
        _zoom.Bounds = new Rectangle(S(100 + Dx), S(178), S(80), S(23));
        _browserBox.Controls.Add(_zoom);
        _browserBox.Controls.Add(new Label
        {
            Text = L.T("(자동 배율을 끈 경우에만 사용)", "(used only when auto zoom is off)"),
            Bounds = new Rectangle(S(188 + Dx), S(181), S(240), S(20)),
            ForeColor = SystemColors.GrayText,
        });
        // English needs more room for the hint than for the profile name, so the box gives way.
        int profileW = L.Korean ? 180 : 140;
        _browserBox.Controls.Add(new Label { Text = L.T("프로필", "Profile"), Bounds = new Rectangle(S(16), S(212), S(80 + Dx), S(20)) });
        _profile.Bounds = new Rectangle(S(100 + Dx), S(209), S(profileW), S(23));
        _browserBox.Controls.Add(_profile);
        int profileHintX = 100 + Dx + profileW + 8;
        _browserBox.Controls.Add(new Label
        {
            Text = L.T("비우면 공용 · 이름이 다르면 다른 계정", "Blank = shared · another name = another account"),
            Bounds = new Rectangle(S(profileHintX), S(212), S(520 + Dx - profileHintX), S(20)),
            ForeColor = SystemColors.GrayText,
        });

        _liveInGrid.Location = new Point(S(100 + Dx), S(238));
        _browserBox.Controls.Add(_liveInGrid);

        AddSection(_browserBox, L.T("로그인", "Sign-in"), S(270));
        _keepSignedIn.Location = new Point(S(100 + Dx), S(292));
        _browserBox.Controls.Add(_keepSignedIn);
        _returnAfterLogin.Location = new Point(S(100 + Dx), S(316));
        _browserBox.Controls.Add(_returnAfterLogin);
        _preventLogout.Location = new Point(S(100 + Dx), S(340));
        _browserBox.Controls.Add(_preventLogout);
        _browserBox.Controls.Add(new Label { Text = L.T("확인 주기", "Check every"), Bounds = new Rectangle(S(16), S(369), S(80 + Dx), S(20)) });
        _keepAliveSeconds.Bounds = new Rectangle(S(100 + Dx), S(366), S(74), S(23));
        _browserBox.Controls.Add(_keepAliveSeconds);
        _browserBox.Controls.Add(new Label
        {
            Text = L.T("초 · 보기 모드에서만 동작", "seconds · only in view mode"),
            Bounds = new Rectangle(S(182 + Dx), S(369), S(240), S(20)),
            ForeColor = SystemColors.GrayText,
        });

        AddSection(_browserBox, L.T("새로 고침", "Reload"), S(402));
        _browserBox.Controls.Add(new Label { Text = L.T("주기", "Every"), Bounds = new Rectangle(S(16), S(431), S(80 + Dx), S(20)) });
        _refreshMinutes.Bounds = new Rectangle(S(100 + Dx), S(428), S(74), S(23));
        _browserBox.Controls.Add(_refreshMinutes);
        _browserBox.Controls.Add(new Label
        {
            Text = L.T("분마다 · 0이면 전체 설정을 따름", "minutes · 0 = use the global setting"),
            Bounds = new Rectangle(S(182 + Dx), S(431), S(300), S(20)),
            ForeColor = SystemColors.GrayText,
        });
        Controls.Add(_browserBox);

        // --- program (same slot; only one is visible at a time) ---
        _programBox.Bounds = new Rectangle(S(12), y, S(536 + Dx), S(230));
        _programBox.Controls.Add(new Label { Text = L.T("실행 파일", "Executable"), Bounds = new Rectangle(S(16), S(27), S(80 + Dx), S(20)) });
        _exe.Bounds = new Rectangle(S(100 + Dx), S(24), S(340), S(23));
        _programBox.Controls.Add(_exe);
        var browse = new Button { Text = L.T("찾아보기…", "Browse…"), Bounds = new Rectangle(S(448 + Dx), S(23), S(76), S(25)) };
        browse.Click += (_, _) => BrowseExecutable();
        _programBox.Controls.Add(browse);

        _programBox.Controls.Add(new Label { Text = L.T("인수", "Arguments"), Bounds = new Rectangle(S(16), S(60), S(80 + Dx), S(20)) });
        _args.Bounds = new Rectangle(S(100 + Dx), S(57), S(420), S(23));
        _programBox.Controls.Add(_args);

        _programBox.Controls.Add(new Label { Text = L.T("작업 폴더", "Working folder"), Bounds = new Rectangle(S(16), S(93), S(80 + Dx), S(20)) });
        _workDir.Bounds = new Rectangle(S(100 + Dx), S(90), S(420), S(23));
        _programBox.Controls.Add(_workDir);

        _programBox.Controls.Add(new Label { Text = L.T("창 제목 포함", "Window title contains"), Bounds = new Rectangle(S(16), S(126), S(84 + Dx), S(20)) });
        _titleMatch.Bounds = new Rectangle(S(100 + Dx), S(123), S(420), S(23));
        _programBox.Controls.Add(_titleMatch);
        _programBox.Controls.Add(new Label
        {
            Text = L.T("창을 여러 개 여는 앱에서 올바른 창을 고를 때 사용합니다.", "Picks the right window in apps that open several."),
            Bounds = new Rectangle(S(100 + Dx), S(148), S(420), S(18)),
            ForeColor = SystemColors.GrayText,
        });

        _attach.Location = new Point(S(100 + Dx), S(172));
        _closeOnRelease.Location = new Point(S(100 + Dx), S(196));
        _programBox.Controls.Add(_attach);
        _programBox.Controls.Add(_closeOnRelease);
        Controls.Add(_programBox);

        var ok = new Button { Text = L.T("확인", "OK"), DialogResult = DialogResult.OK, Bounds = new Rectangle(S(376 + Dx), S(600), S(84), S(30)) };
        var cancel = new Button { Text = L.T("취소", "Cancel"), DialogResult = DialogResult.Cancel, Bounds = new Rectangle(S(464 + Dx), S(600), S(84), S(30)) };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        _rbEmpty.CheckedChanged += (_, _) => SyncVisibility();
        _rbBrowser.CheckedChanged += (_, _) => SyncVisibility();
        _rbProgram.CheckedChanged += (_, _) => SyncVisibility();
        _fitToCell.CheckedChanged += (_, _) =>
        {
            _zoom.Enabled = !_fitToCell.Checked;
            ShowUserZoom();
        };
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

        _lastUrl.Text = drifted ? L.T($"현재 열려 있는 주소: {_rememberedUrl}", $"Currently open: {_rememberedUrl}") : string.Empty;
        _lastUrl.Visible = drifted;
        _resetUrl.Visible = drifted;
        _resetUrl.Enabled = drifted;
    }

    /// <summary>A bold section heading with a rule beside it, inside a group box.</summary>
    private void AddSection(Control parent, string text, int top)
    {
        var heading = new Label { Text = text, AutoSize = true, Location = new Point(S(16), top), Font = new Font(Font, FontStyle.Bold) };
        parent.Controls.Add(heading);
        int x = S(16) + heading.PreferredSize.Width + S(8);
        parent.Controls.Add(new Label
        {
            BorderStyle = BorderStyle.Fixed3D,
            Bounds = new Rectangle(x, top + heading.PreferredSize.Height / 2, S(520 + Dx) - x, 2),
        });
    }

    /// <summary>
    /// The zoom set with Ctrl+wheel on top of the fit. Not editable here — it is set by zooming
    /// the page — but visible, and resettable, so the dialog is not silent about it.
    /// </summary>
    private void ShowUserZoom()
    {
        bool shown = _fitToCell.Checked && Math.Abs(_zoomScale - 1.0) > 0.001;
        _userZoom.Text = shown ? L.T($"Ctrl+휠 {_zoomScale * 100:0}%", $"Ctrl+wheel {_zoomScale * 100:0}%") : string.Empty;
        _userZoom.Visible = shown;
        _resetZoom.Visible = shown;
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
        ShowUserZoom();
        _preventLogout.Checked = c.PreventLogout;
        _keepAliveSeconds.Value = Math.Clamp(c.KeepAliveSeconds <= 0 ? 60 : c.KeepAliveSeconds, 10, 3600);
        _refreshMinutes.Value = Math.Clamp(c.RefreshMinutes, 0, 1440);
        _keepSignedIn.Checked = c.KeepSignedIn;
        _returnAfterLogin.Checked = c.ReturnAfterLogin;
        _liveInGrid.Checked = c.LiveInGrid;
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
            Title = L.T("실행 파일 선택", "Select executable"),
            Filter = L.T("프로그램 (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|모든 파일 (*.*)|*.*",
                         "Programs (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|All files (*.*)|*.*"),
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
            MessageBox.Show(this, L.T("주소를 입력하세요.", "Enter an address."), L.T("확인", "GridDisplay"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
        }
        if (kind == CellKind.Program && string.IsNullOrWhiteSpace(_exe.Text) && !_attach.Checked)
        {
            MessageBox.Show(this,
                L.T("실행 파일을 지정하거나 '실행 중인 창 가져오기'를 켜세요.",
                    "Choose an executable, or turn on 'Take over a window that is already running'."),
                L.T("확인", "GridDisplay"),
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
            // Not on the form; carried so saving the dialog does not undo the user's Ctrl+wheel.
            ZoomScale = _zoomScale,
            PreventLogout = _preventLogout.Checked,
            KeepAliveSeconds = (int)_keepAliveSeconds.Value,
            RememberLastUrl = _rememberLast.Checked,
            LastUrl = _rememberedUrl,
            RefreshMinutes = (int)_refreshMinutes.Value,
            KeepSignedIn = _keepSignedIn.Checked,
            ReturnAfterLogin = _returnAfterLogin.Checked,
            LiveInGrid = _liveInGrid.Checked,
            ExecutablePath = _exe.Text.Trim(),
            Arguments = _args.Text.Trim(),
            WorkingDirectory = _workDir.Text.Trim(),
            WindowTitleMatch = _titleMatch.Text.Trim(),
            AttachToRunning = _attach.Checked,
            CloseOnRelease = _closeOnRelease.Checked,
        };
    }
}
