using GridDisplay.Interop;
using GridDisplay.Models;
using GridDisplay.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GridDisplay.Controls;

/// <summary>
/// One grid cell. Holds either an embedded WebView2 (browser cells) or a reparented native
/// window (program cells).
///
/// Browser cells deliberately use WebView2 rather than reparenting an external browser: only an
/// embedded engine reports its current address, which is what makes "remember the URL and come
/// back to it after a restart" possible at all. It also keeps in-page fullscreen (F11) bounded
/// by the control, i.e. by the cell.
/// </summary>
public sealed class CellHost : Panel
{
    /// <summary>WebView2 rejects factors outside this range.</summary>
    private const double MinZoom = 0.25;
    private const double MaxZoom = 4.0;

    /// <summary>
    /// One keep-alive pulse. The two logout mechanisms are independent and both need answering:
    /// synthetic activity events reset a page's own idle timer, and a credentialed no-store
    /// request stops the server-side session from expiring. Events go to <c>body</c> so they
    /// bubble on to <c>document</c> and <c>window</c> — idle detectors listen on all three.
    /// Deliberately no mousedown/click: those would fire the page's own buttons.
    /// </summary>
    private const string KeepAliveScript = """
        (function () {
          var t = document.body || document.documentElement;
          if (!t) return;
          t.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 1, clientY: 1 }));
          t.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, key: 'Shift' }));
          t.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true, key: 'Shift' }));
          try {
            fetch(location.href, { method: 'HEAD', cache: 'no-store', credentials: 'include' });
          } catch (e) { }
        })();
        """;

    private readonly Label _placeholder;
    private readonly System.Windows.Forms.Timer _keepAlive = new();
    private WebView2? _web;
    private NativeAppHost? _native;
    private CancellationTokenSource? _cts;
    private bool _webReady;
    private bool _suspended;
    private int _fitReference;
    private double _appliedZoom;
    private int _index;
    private bool _keepAliveActive = true;

    /// <summary>
    /// Recovery state for a dead render process. A wall runs for weeks, so a renderer will die
    /// eventually — out of memory, a driver reset, a page that faults. Backing off matters as
    /// much as retrying: a page that dies on load would otherwise reload forever, and a hundred
    /// crash-reload cycles a minute is worse than a cell that sits there saying what happened.
    /// </summary>
    /// <summary>
    /// This cell's own refresh clock, when it has one. Asked <see cref="RefreshAllowed"/> before
    /// it acts: reloading the cell somebody is reading, or typing into, would be rude.
    /// </summary>
    private readonly System.Windows.Forms.Timer _refreshTimer = new();

    /// <summary>Set by the wall. Null means "always fine".</summary>
    public Func<bool>? RefreshAllowed { get; set; }

    private readonly System.Windows.Forms.Timer _recoverTimer = new();
    private int _recoverAttempts;
    private DateTime _lastRecoverUtc = DateTime.MinValue;

    public CellHost(int index, CellConfig config)
    {
        _index = index;
        Config = config;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 24, 27);
        BorderStyle = BorderStyle.None;
        TabStop = false;

        _placeholder = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(140, 140, 150),
            BackColor = Color.FromArgb(24, 24, 27),
            Font = new Font("Segoe UI", 11f, FontStyle.Regular),
            Text = string.Empty,
        };
        Controls.Add(_placeholder);

        _keepAlive.Tick += (_, _) => PulseKeepAlive();

        _refreshTimer.Tick += (_, _) =>
        {
            // Not now: leave the timer running and take the next slot instead of skipping a
            // whole period, so a cell watched for an hour still refreshes when it is let go.
            if (RefreshAllowed is not null && !RefreshAllowed()) return;
            Services.HealthMonitor.Event($"셀 {Index + 1} 정기 새로 고침 · {Describe()}");
            Reload();
        };

        _recoverTimer.Tick += (_, _) =>
        {
            _recoverTimer.Stop();
            if (_web is null || !_webReady) return;
            try
            {
                StatusText = string.Empty;
                _web.CoreWebView2.Reload();
                Services.HealthMonitor.Event($"셀 {Index + 1} 다시 불러옴 ({_recoverAttempts}번째)");
            }
            catch
            {
                StatusText = "화면을 다시 불러오지 못했습니다.";
                _placeholder.BringToFront();
            }
        };
    }

    /// <summary>
    /// Position in the grid. Reassigned when cells are dragged into a new order — the hosts are
    /// swapped rather than their content, so the number on an empty cell has to follow.
    /// </summary>
    public int Index
    {
        get => _index;
        set
        {
            if (_index == value) return;
            _index = value;
            if (Config.Kind == CellKind.Empty) StatusText = EmptyText;
        }
    }

    public CellConfig Config { get; private set; }

    private string EmptyText => $"[{Index + 1}] 비어 있음\n클릭하여 설정";

    /// <summary>Raised when the hosted content changes something worth persisting (URL, title).</summary>
    public event Action<CellHost>? StateChanged;

    public string StatusText
    {
        get => _placeholder.Text;
        private set
        {
            _placeholder.Text = value;
            _placeholder.Visible = !string.IsNullOrEmpty(value);
        }
    }

    public string LiveTitle { get; private set; } = string.Empty;

    public string HeaderText => string.IsNullOrWhiteSpace(LiveTitle) ? Config.DisplayName : LiveTitle;

    /// <summary>Tear down whatever is hosted and load <paramref name="config"/> instead.</summary>
    public async Task ApplyAsync(CellConfig config)
    {
        Config = config;
        Release();

        // Restarting a cell by hand is the operator saying "try again"; the crash count from the
        // previous content should not count against the new one.
        _recoverTimer.Stop();
        _recoverAttempts = 0;
        _refreshTimer.Stop();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        switch (config.Kind)
        {
            case CellKind.Browser:
                StatusText = "브라우저 준비 중…";
                await StartBrowserAsync(config, ct).ConfigureAwait(true);
                break;

            case CellKind.Program:
                StatusText = "프로그램 시작 중…";
                await StartProgramAsync(config, ct).ConfigureAwait(true);
                break;

            default:
                StatusText = EmptyText;
                break;
        }
    }

    /// <summary>
    /// Chromium switches for the shared browser process, and whether to keep asking it to trim.
    /// Static because that is what they are: every browser cell in this process ends up in one
    /// browser, and only the cell that creates it gets a say in how it starts. Set once from the
    /// settings before any cell comes up — see AppState.BrowserArguments.
    /// </summary>
    public static string BrowserArguments { get; set; } = string.Empty;

    public static bool TrimBrowserMemory { get; set; }

    private async Task StartBrowserAsync(CellConfig config, CancellationToken ct)
    {
        var view = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(24, 24, 27),
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = SetStore.WebViewDataDirectory,
                // Per-cell profiles let two cells stay signed in to the same site as different users.
                ProfileName = string.IsNullOrWhiteSpace(config.ProfileName) ? null : config.ProfileName,
                AdditionalBrowserArguments = BrowserArguments,
            },
        };
        _web = view;
        Controls.Add(view);
        view.BringToFront();

        try
        {
            await view.EnsureCoreWebView2Async().ConfigureAwait(true);

            // Deliberately NOT set to Low here. This asks the browser to release what a WebView
            // is holding, which is right for one nobody is looking at and wrong for one that is
            // on screen: a map streaming tiles has them dropped as fast as they arrive, and the
            // page never finishes loading. It is applied from SetSuspended instead, where the
            // cell really is hidden.
            ApplyMemoryTarget(suspended: false);
        }
        catch (Exception ex)
        {
            StatusText = "WebView2 런타임을 초기화하지 못했습니다.\n" +
                         "Microsoft Edge WebView2 Runtime을 설치하세요.\n\n" + ex.Message;
            return;
        }

        if (ct.IsCancellationRequested || view.IsDisposed) return;

        var core = view.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        core.Settings.IsSwipeNavigationEnabled = false;

        // Popups stay inside the cell — nothing escapes the grid as a floating window.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (!string.IsNullOrEmpty(e.Uri)) core.Navigate(e.Uri);
        };

        core.SourceChanged += (_, _) =>
        {
            var source = core.Source;
            if (string.IsNullOrWhiteSpace(source) || source == "about:blank") return;
            // Records where the cell went; never touches the configured address.
            if (Config.LastUrl == source) return;
            Config.LastUrl = source;
            StateChanged?.Invoke(this);
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            LiveTitle = core.DocumentTitle;
            StateChanged?.Invoke(this);
        };

        core.ProcessFailed += (_, e) => OnProcessFailed(e.ProcessFailedKind);

        SyncRefreshTimer();

        _webReady = true;
        _appliedZoom = 0;
        ApplyZoom();
        SyncKeepAlive();

        Navigate(config.StartUrl);
        StatusText = string.Empty;
    }

    private async Task StartProgramAsync(CellConfig config, CancellationToken ct)
    {
        var host = new NativeAppHost { Dock = DockStyle.Fill };
        _native = host;
        Controls.Add(host);
        host.BringToFront();
        host.ChildLost += _ =>
        {
            StatusText = "프로그램 창이 종료되었습니다.\n우클릭 → 다시 시작";
            _placeholder.BringToFront();
        };

        string? error;
        try
        {
            error = await host.AttachAsync(config, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (error is not null)
        {
            StatusText = error;
            _placeholder.BringToFront();
        }
        else
        {
            StatusText = string.Empty;
            LiveTitle = NativeMethods.GetWindowTitle(host.ChildHandle);
            StateChanged?.Invoke(this);
        }
    }

    public void Navigate(string url)
    {
        if (_web is null || !_webReady) return;
        var target = NormalizeUrl(url);
        try { _web.CoreWebView2.Navigate(target); }
        catch { }
    }

    public static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "about:blank";
        url = url.Trim();
        if (url.Contains("://", StringComparison.Ordinal)) return url;
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return url;
        if (File.Exists(url)) return new Uri(url).AbsoluteUri;
        return "https://" + url;
    }

    /// <summary>
    /// Width the "fit to cell" zoom is measured against — the width this cell takes when zoomed to
    /// the whole grid area. A cell holding a third of that width then renders the identical page
    /// layout at a third of the scale, and a zoomed cell lands on exactly 1.0, so zooming in and
    /// back out changes only the scale and never reflows the page.
    /// </summary>
    public void SetFitReference(int referenceWidth)
    {
        _fitReference = referenceWidth;
        ApplyZoom();
    }

    private double TargetZoom()
    {
        if (!Config.FitToCell) return Config.ZoomFactor <= 0 ? 1.0 : Config.ZoomFactor;
        int width = ClientSize.Width;
        if (_fitReference <= 0 || width <= 0) return 1.0;
        return Math.Clamp((double)width / _fitReference, MinZoom, MaxZoom);
    }

    private void ApplyZoom()
    {
        if (_web is null || !_webReady) return;
        double target = TargetZoom();
        // Assigning ZoomFactor relays out the page, and every resize step calls in here, so push
        // only real changes. 0 is the "nothing applied yet" sentinel — no valid factor reaches it.
        if (Math.Abs(target - _appliedZoom) < 0.0005) return;
        _appliedZoom = target;
        try { _web.ZoomFactor = target; } catch { }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyZoom();
    }

    /// <summary>
    /// Keep-alive runs in view mode only. While the hosted app has the input (F2) the user is
    /// generating real activity anyway, and synthetic key events would collide with typing.
    /// </summary>
    public void SetKeepAliveActive(bool active)
    {
        if (_keepAliveActive == active) return;
        _keepAliveActive = active;
        SyncKeepAlive();
    }

    private void SyncKeepAlive()
    {
        bool on = _keepAliveActive && _webReady && _web is not null
                  && Config.Kind == CellKind.Browser && Config.PreventLogout;
        if (!on)
        {
            _keepAlive.Stop();
            return;
        }

        int interval = Math.Clamp(Config.KeepAliveSeconds <= 0 ? 60 : Config.KeepAliveSeconds, 10, 3600) * 1000;
        if (_keepAlive.Interval != interval) _keepAlive.Interval = interval;
        if (!_keepAlive.Enabled) _keepAlive.Start();
    }

    private void PulseKeepAlive()
    {
        if (_web is null || !_webReady || _suspended) return;
        try { _ = _web.CoreWebView2.ExecuteScriptAsync(KeepAliveScript); }
        catch { }
    }

    /// <summary>
    /// Bring the cell back after its render process died or stopped answering.
    ///
    /// Only the render process is recovered here. When the *browser* process goes the WebView2
    /// object is gone with it and reloading cannot help — that needs the cell rebuilt, which is
    /// the caller's business, so it is reported and left alone.
    /// </summary>
    private void OnProcessFailed(CoreWebView2ProcessFailedKind kind)
    {
        bool renderer = kind is CoreWebView2ProcessFailedKind.RenderProcessExited
                             or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive;

        // Logged before anything else: a cell that recovers leaves no trace on screen, so
        // without this line "it died twice last night" is unanswerable.
        Services.HealthMonitor.Event(
            $"셀 {Index + 1} 프로세스 종료 ({kind}) · {Describe()}");

        if (!renderer)
        {
            StatusText = $"브라우저 프로세스 오류: {kind}";
            _placeholder.BringToFront();
            return;
        }

        // A crash that arrives long after the last one is a fresh incident, not a loop.
        if (DateTime.UtcNow - _lastRecoverUtc > TimeSpan.FromMinutes(10)) _recoverAttempts = 0;
        _lastRecoverUtc = DateTime.UtcNow;
        _recoverAttempts++;

        const int maxAttempts = 5;
        if (_recoverAttempts > maxAttempts)
        {
            StatusText = $"화면을 되살리지 못했습니다 ({kind}). 셀 다시 시작을 눌러 주세요.";
            _placeholder.BringToFront();
            return;
        }

        // 5s, 10s, 20s, 40s, 80s — long enough that a page failing on load cannot spin.
        int delay = 5000 * (int)Math.Pow(2, _recoverAttempts - 1);
        StatusText = $"화면이 종료되어 {delay / 1000}초 뒤 다시 불러옵니다… ({_recoverAttempts}/{maxAttempts})";
        _placeholder.BringToFront();

        _recoverTimer.Stop();
        _recoverTimer.Interval = delay;
        _recoverTimer.Start();
    }

    /// <summary>Start or stop this cell's own refresh clock to match its configuration.</summary>
    private void SyncRefreshTimer()
    {
        _refreshTimer.Stop();
        int minutes = Config.Kind == CellKind.Browser ? Config.RefreshMinutes : 0;
        if (minutes <= 0) return;
        _refreshTimer.Interval = (int)TimeSpan.FromMinutes(minutes).TotalMilliseconds;
        _refreshTimer.Start();
    }

    /// <summary>What this cell is showing, short enough for a log line.</summary>
    private string Describe()
    {
        var url = Config.Url ?? string.Empty;
        if (url.Length > 120) url = url[..120] + "…";
        var title = string.IsNullOrWhiteSpace(Config.Title) ? string.Empty : $"'{Config.Title}' ";
        return title + url;
    }

    public void Reload()
    {
        if (_web is not null && _webReady)
        {
            try { _web.CoreWebView2.Reload(); } catch { }
        }
    }

    /// <summary>Hand keyboard focus to the hosted content so typing reaches the app.</summary>
    public void FocusContent()
    {
        if (_web is not null && _webReady)
        {
            try { _web.Focus(); } catch { }
            return;
        }
        _native?.FocusChild();
    }

    /// <summary>
    /// Free the browser's memory while the cell is off-screen. Optional because resuming costs
    /// a visible beat, which is the wrong trade for a wall that is switched between constantly.
    /// </summary>
    /// <summary>
    /// Tell the browser how hard to trim this cell. Low belongs to a hidden cell only — see the
    /// note where the WebView is created.
    /// </summary>
    private void ApplyMemoryTarget(bool suspended)
    {
        if (_web is null) return;
        try
        {
            _web.CoreWebView2.MemoryUsageTargetLevel = TrimBrowserMemory && suspended
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch
        {
            // Older runtime: the browser-wide switches still apply.
        }
    }

    public void SetSuspended(bool suspend)
    {
        // A cell asked to stay signed in must stay running — suspending it is exactly the idleness
        // the keep-alive exists to prevent.
        if (suspend && Config.PreventLogout) return;
        if (_web is null || !_webReady || suspend == _suspended) return;
        _suspended = suspend;
        try
        {
            if (suspend) _ = _web.CoreWebView2.TrySuspendAsync();
            else _web.CoreWebView2.Resume();
            ApplyMemoryTarget(suspend);
        }
        catch { }
    }

    public void Release()
    {
        _keepAlive.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _webReady = false;
        _suspended = false;
        _appliedZoom = 0;
        LiveTitle = string.Empty;

        if (_native is not null)
        {
            _native.Release(Config.CloseOnRelease);
            Controls.Remove(_native);
            _native.Dispose();
            _native = null;
        }

        if (_web is not null)
        {
            Controls.Remove(_web);
            _web.Dispose();
            _web = null;
        }

        StatusText = string.Empty;
        _placeholder.BringToFront();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Release();
            _keepAlive.Dispose();
            _recoverTimer.Dispose();
            _refreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
