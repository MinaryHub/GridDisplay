using GridDisplay.Models;
using GridDisplay.Services;

namespace GridDisplay.Forms;

/// <summary>
/// Version info and the manual update path: which build is running, where updates come from, and
/// the two ways to take one. Edits are written straight into the shared <see cref="AppState"/> —
/// the caller owns saving, the same aliasing the rest of the app relies on.
///
/// A Store install gets a different dialog: no feed address, no install buttons, one button that
/// opens the Store. Not cosmetic — <see cref="AppVersion.IsStoreInstall"/> also disarms
/// <see cref="UpdateService"/>, and the dialog has to stop offering what the service refuses.
/// </summary>
public sealed class UpdateForm : Form
{
    private readonly AppState _state;
    private readonly UpdateService _updates;
    private readonly bool _storeManaged = AppVersion.IsStoreInstall;

    private readonly TextBox _feedUrl = new();
    private readonly CheckBox _autoCheck = new() { Text = "자동으로 확인", AutoSize = true };
    private readonly NumericUpDown _hours = new() { Minimum = 1, Maximum = 168, Increment = 1, Value = 24 };
    private readonly Label _lastChecked = new() { AutoSize = true, ForeColor = Color.FromArgb(90, 90, 100) };
    private readonly Button _check = new() { Text = "지금 확인" };
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new() { Visible = false };
    private readonly Button _installLater = new() { Text = "설치 (다음 실행 시 적용)", Enabled = false };
    private readonly Button _installNow = new() { Text = "설치 후 다시 시작", Enabled = false };
    private readonly Button _close = new() { Text = "닫기" };

    private UpdateCheckResult? _found;
    private bool _busy;

    public UpdateForm(AppState state, UpdateService updates)
    {
        _state = state;
        _updates = updates;

        Text = "버전 및 업데이트";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9f);
        // Authored at 96 DPI and scaled through S(...), like CellSettingsForm: the app runs
        // PerMonitorV2, so the font follows the monitor's scaling while raw pixels would clip.
        ClientSize = new Size(S(540), S(430));

        int y = S(12);

        // --- what is running ---
        var versionBox = new GroupBox { Text = "현재 버전", Bounds = new Rectangle(S(12), y, S(516), S(104)) };
        versionBox.Controls.Add(Caption("버전", S(16), S(26)));
        versionBox.Controls.Add(Value(AppVersion.Display, S(110), S(26), S(390)));
        versionBox.Controls.Add(Caption("설치 형식", S(16), S(52)));
        versionBox.Controls.Add(Value(AppVersion.InstallKindText, S(110), S(52), S(390)));
        versionBox.Controls.Add(Caption("패키지", S(16), S(78)));
        versionBox.Controls.Add(Value(
            string.IsNullOrEmpty(AppVersion.PackageFamilyName) ? "-" : AppVersion.PackageFamilyName,
            S(110), S(78), S(390)));
        Controls.Add(versionBox);
        y += S(116);

        // --- where updates come from ---
        var feedBox = new GroupBox { Text = "업데이트 원본", Bounds = new Rectangle(S(12), y, S(516), S(126)) };
        if (_storeManaged) FillStoreFeedBox(feedBox); else FillSelfManagedFeedBox(feedBox);
        Controls.Add(feedBox);
        y += S(138);

        // --- check + result ---
        // No "check now" on a Store build: the Store already checked, and a button whose only
        // possible answer is "the Store handles it" is worse than no button.
        _check.Bounds = new Rectangle(S(12), y, S(110), S(28));
        _check.Click += async (_, _) => await RunCheckAsync().ConfigureAwait(true);
        _check.Visible = !_storeManaged;
        Controls.Add(_check);

        _status.Bounds = _storeManaged
            ? new Rectangle(S(12), y, S(516), S(46))
            : new Rectangle(S(132), y, S(396), S(46));
        Controls.Add(_status);
        y += S(54);

        _progress.Bounds = new Rectangle(S(12), y, S(516), S(12));
        Controls.Add(_progress);
        y += S(24);

        // The one action a Store build offers reuses this button: same slot, different job.
        _installLater.Bounds = new Rectangle(S(12), y, S(180), S(30));
        if (_storeManaged)
        {
            _installLater.Text = "Microsoft Store 열기";
            _installLater.Enabled = true;
            _installLater.Click += (_, _) => ShowApplyResult(UpdateService.OpenStoreUpdates());
        }
        else
        {
            _installLater.Click += async (_, _) => await ApplyAsync(restartNow: false).ConfigureAwait(true);
        }
        Controls.Add(_installLater);

        _installNow.Bounds = new Rectangle(S(200), y, S(150), S(30));
        _installNow.Click += async (_, _) => await ApplyAsync(restartNow: true).ConfigureAwait(true);
        _installNow.Visible = !_storeManaged;
        Controls.Add(_installNow);

        _close.Bounds = new Rectangle(S(428), y, S(100), S(30));
        _close.Click += (_, _) => Close();
        Controls.Add(_close);
        CancelButton = _close;

        LoadState();
        ShowResult(_storeManaged ? null : _updates.Last);
    }

    private void FillStoreFeedBox(GroupBox box) => box.Controls.Add(new Label
    {
        Text = "Microsoft Store가 이 앱의 업데이트를 관리합니다.\n\n" +
               "새 버전은 Store가 백그라운드에서 내려받아 설치하므로 주소를 지정하거나\n" +
               "직접 설치할 필요가 없습니다. 지금 바로 확인하려면 아래에서 Store를 여세요.",
        Bounds = new Rectangle(S(16), S(28), S(484), S(80)),
    });

    private void FillSelfManagedFeedBox(GroupBox feedBox)
    {
        feedBox.Controls.Add(Caption("주소", S(16), S(27)));
        _feedUrl.Bounds = new Rectangle(S(110), S(24), S(390), S(23));
        _feedUrl.PlaceholderText = "https://example/griddisplay/GridDisplay.appinstaller";
        feedBox.Controls.Add(_feedUrl);
        feedBox.Controls.Add(new Label
        {
            Text = ".appinstaller 파일 또는 {\"version\":\"1.2.3\",\"url\":\"…\"} 형식의 update.json 주소",
            Bounds = new Rectangle(S(110), S(50), S(390), S(18)),
            ForeColor = Color.FromArgb(90, 90, 100),
        });

        _autoCheck.Location = new Point(S(112), S(78));
        feedBox.Controls.Add(_autoCheck);
        _hours.Bounds = new Rectangle(_autoCheck.Location.X + _autoCheck.PreferredSize.Width + S(12), S(76), S(58), S(23));
        feedBox.Controls.Add(_hours);
        var hoursSuffix = new Label
        {
            Text = "시간마다",
            AutoSize = true,
            Location = new Point(_hours.Right + S(6), S(79)),
        };
        feedBox.Controls.Add(hoursSuffix);
        _lastChecked.Location = new Point(hoursSuffix.Right + S(16), S(79));
        feedBox.Controls.Add(_lastChecked);
    }

    private void LoadState()
    {
        _feedUrl.Text = _state.UpdateFeedUrl;
        _autoCheck.Checked = _state.AutoCheckUpdates;
        _hours.Value = Math.Clamp(_state.UpdateCheckHours <= 0 ? 24 : _state.UpdateCheckHours, 1, 168);
        _lastChecked.Text = _state.LastUpdateCheckUtc is { } utc
            ? $"마지막 확인 {utc.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "확인한 적 없음";
    }

    /// <summary>Push the edits back before every action, so a check uses the address just typed.</summary>
    private void CommitState()
    {
        // Nothing to commit on a Store build — those controls were never shown, so writing them
        // back would only mark the state dirty and provoke a pointless save.
        if (_storeManaged) return;
        _state.UpdateFeedUrl = _feedUrl.Text.Trim();
        _state.AutoCheckUpdates = _autoCheck.Checked;
        _state.UpdateCheckHours = (int)_hours.Value;
        SettingsChanged = true;
    }

    /// <summary>True once the dialog touched <see cref="AppState"/>; the caller turns it into a save.</summary>
    public bool SettingsChanged { get; private set; }

    private async Task RunCheckAsync()
    {
        if (_busy) return;
        CommitState();
        SetBusy(true, "확인 중…");
        try
        {
            var result = await _updates.CheckAsync().ConfigureAwait(true);
            ShowResult(result);
            LoadState();
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async Task ApplyAsync(bool restartNow)
    {
        if (_busy || _found is null) return;
        CommitState();
        SetBusy(true, restartNow ? "설치 중… 완료되면 앱이 다시 시작됩니다." : "설치 중…");
        _progress.Visible = true;
        _progress.Value = 0;
        var progress = new Progress<int>(p => _progress.Value = Math.Clamp(p, 0, 100));
        try
        {
            ShowApplyResult(await _updates.ApplyAsync(_found, restartNow, progress).ConfigureAwait(true));
        }
        finally
        {
            _progress.Visible = false;
            SetBusy(false, null);
        }
    }

    /// <summary>
    /// Report an apply attempt. Anything but a failure retires the buttons: the work is either done
    /// or now somebody else's (the shell's, or the Store's), and pressing again would repeat it.
    /// </summary>
    private void ShowApplyResult(UpdateApplyResult result)
    {
        _status.ForeColor = result.Outcome == UpdateApplyOutcome.Failed ? Color.FromArgb(180, 40, 40) : ForeColor;
        _status.Text = result.Message;
        if (result.Outcome == UpdateApplyOutcome.Failed) return;
        // Opening the Store is repeatable and costs nothing, so that one button stays live.
        if (_storeManaged) return;
        _installLater.Enabled = false;
        _installNow.Enabled = false;
        _found = null;
    }

    private void ShowResult(UpdateCheckResult? result)
    {
        if (_storeManaged)
        {
            // The button is already labelled and enabled for the Store; only the caption is left.
            _status.ForeColor = ForeColor;
            _status.Text = $"Microsoft Store가 자동으로 업데이트합니다 (현재 {AppVersion.Display}).";
            return;
        }

        _found = result is { Status: UpdateStatus.Available } ? result : null;
        // An unpackaged build can still find and download an update, it just cannot install it
        // itself — so the buttons stay live and say what they will do in the result message.
        _installLater.Enabled = _found is not null && AppVersion.IsPackaged;
        _installNow.Enabled = _found is not null && AppVersion.IsPackaged;
        _installLater.Text = AppVersion.IsPackaged ? "설치 (다음 실행 시 적용)" : "설치 파일 열기";
        if (!AppVersion.IsPackaged) _installLater.Enabled = _found is not null;

        if (result is null)
        {
            _status.ForeColor = ForeColor;
            _status.Text = AppVersion.IsPackaged
                ? "'지금 확인'을 눌러 새 버전을 확인하세요."
                : "MSIX 패키지로 설치하면 앱이 스스로 업데이트할 수 있습니다.";
            return;
        }

        _status.ForeColor = result.Status switch
        {
            UpdateStatus.Available => Color.FromArgb(20, 110, 40),
            UpdateStatus.Failed => Color.FromArgb(180, 40, 40),
            _ => ForeColor,
        };
        _status.Text = result.Message;
    }

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        _check.Enabled = !busy;
        _feedUrl.Enabled = !busy;
        UseWaitCursor = busy;
        if (busy)
        {
            _installLater.Enabled = false;
            _installNow.Enabled = false;
            if (message is not null)
            {
                _status.ForeColor = ForeColor;
                _status.Text = message;
            }
        }
        else if (_found is not null)
        {
            _installLater.Enabled = true;
            _installNow.Enabled = AppVersion.IsPackaged;
        }
    }

    private static Label Caption(string text, int x, int y) => new()
    {
        Text = text,
        AutoSize = true,
        Location = new Point(x, y + 3),
        ForeColor = Color.FromArgb(90, 90, 100),
    };

    private static Label Value(string text, int x, int y, int width) => new()
    {
        Text = text,
        Bounds = new Rectangle(x, y, width, 20),
        AutoEllipsis = true,
    };

    private int S(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);
}
