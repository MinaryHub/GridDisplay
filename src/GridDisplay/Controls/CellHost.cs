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
    private bool _navigating;
    private bool _navUserInitiated;

    /// <summary>
    /// A page counts as a login page when it shows a password field. Crude, and deliberately so:
    /// it needs no per-site setup, and every intranet form login this was written for has one.
    /// </summary>
    private const string LoginPageProbe = "!!document.querySelector('input[type=password]')";

    /// <summary>Last page reached that was not a login page — where to go back to.</summary>
    private string _goodUrl = string.Empty;
    /// <summary>Set when the site sent the cell to a login page; cleared once it has gone back.</summary>
    private string? _returnTo;

    /// <summary>Converted session cookies are given this long; Chromium caps expiry at 400 days.</summary>
    private static readonly TimeSpan SignedInLifetime = TimeSpan.FromDays(400);
    private readonly System.Windows.Forms.Timer _cookieTimer = new() { Interval = 5 * 60 * 1000 };
    private int _index;
    private bool _keepAliveActive = true;

    /// <summary>
    /// Still-image presentation. A grid of live dashboards spends CPU, GPU and memory redrawing
    /// pages nobody is reading closely, so a cell in the grid shows a captured frame and its page
    /// is put to sleep; it wakes only when zoomed, or every <see cref="StillRefreshMinutes"/> to
    /// take a fresh picture. <see cref="CellConfig.LiveInGrid"/> opts a cell out.
    /// </summary>
    private readonly PictureBox _still = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.StretchImage,
        BackColor = Color.FromArgb(24, 24, 27),
        Visible = false,
    };
    private readonly System.Windows.Forms.Timer _stillTimer = new();
    /// <summary>The wall wants a still picture here (it is in the grid and not live).</summary>
    private bool _stillWanted;
    /// <summary>The page is hidden behind the picture right now.</summary>
    private bool _frozen;
    /// <summary>Bumped on every presentation change, so a capture that finishes late is dropped.</summary>
    private int _stillGen;
    private bool _firstLoadDone;
    private TaskCompletionSource<bool>? _loadWaiter;

    /// <summary>Minutes between fresh pictures of a still cell; 0 leaves the first one in place.</summary>
    public static int StillRefreshMinutes { get; set; } = 5;

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
        Controls.Add(_still);

        _stillTimer.Tick += (_, _) =>
        {
            if (!_stillWanted || !Visible) return;
            if (RefreshAllowed is not null && !RefreshAllowed()) return;
            _ = RefreshStillAsync(reload: true, settleMs: 4000);
        };

        _keepAlive.Tick += (_, _) => PulseKeepAlive();
        _cookieTimer.Tick += (_, _) => _ = PersistSessionCookiesAsync();

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

    private string EmptyText => $"{Index + 1}\n\n＋ 이 칸에 화면 추가\n클릭하여 웹 주소나 프로그램을 넣을 수 있습니다";

    /// <summary>Raised when the hosted content changes something worth persisting (URL, title).</summary>
    public event Action<CellHost>? StateChanged;

    /// <summary>Raised when the user zooms the page themselves, so the wall can say it was kept.</summary>
    public event Action<CellHost>? UserZoomed;

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

        view.ZoomFactorChanged += (_, _) => OnZoomFactorChanged();
        core.NavigationStarting += (_, e) =>
        {
            _navigating = true;
            _navUserInitiated = e.IsUserInitiated;
        };
        core.NavigationCompleted += (_, e) =>
        {
            _navigating = false;
            _appliedZoom = 0;
            ApplyZoom();
            _loadWaiter?.TrySetResult(e.IsSuccess);
            if (!_firstLoadDone)
            {
                // The first picture can only be taken once there is a page to take it of.
                _firstLoadDone = true;
                // Also when hidden behind a zoom: RefreshStillAsync puts an unseen page to sleep.
                if (_stillWanted || !Visible) _ = RefreshStillAsync(reload: false, settleMs: 4000);
            }
            if (!e.IsSuccess) return;
            _ = CheckLoginAsync(_navUserInitiated);
            _ = PersistSessionCookiesAsync();
        };

        SyncRefreshTimer();

        _webReady = true;
        _appliedZoom = 0;
        ApplyZoom();
        SyncKeepAlive();
        if (config.KeepSignedIn) _cookieTimer.Start();
        if (_stillWanted) StartStillTimer();

        _goodUrl = NormalizeUrl(config.StartUrl);
        _returnTo = null;
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

    /// <summary>The bare fit-to-cell factor, or 0 while the cell has no size to measure yet.</summary>
    private double FitZoom()
    {
        int width = ClientSize.Width;
        if (_fitReference <= 0 || width <= 0) return 0;
        return (double)width / _fitReference;
    }

    private double TargetZoom()
    {
        if (!Config.FitToCell) return Math.Clamp(Config.ZoomFactor <= 0 ? 1.0 : Config.ZoomFactor, MinZoom, MaxZoom);
        double fit = FitZoom();
        double scale = Config.ZoomScale <= 0 ? 1.0 : Config.ZoomScale;
        return Math.Clamp((fit <= 0 ? 1.0 : fit) * scale, MinZoom, MaxZoom);
    }

    /// <summary>
    /// The page's zoom moved. Our own assignments land here too and are recognised by matching
    /// what was last applied; anything else is the user (Ctrl+wheel, Ctrl +/-), and is recorded
    /// so the next start comes up at the same size.
    ///
    /// Ignored mid-navigation: WebView2 treats a user zoom as belonging to the current page and
    /// puts the app's factor back when a new one loads, and that revert must not be mistaken for
    /// the user zooming back out. NavigationCompleted then re-asserts the recorded zoom.
    /// </summary>
    private void OnZoomFactorChanged()
    {
        if (_web is null || !_webReady || _navigating) return;
        double actual;
        try { actual = _web.ZoomFactor; } catch { return; }
        if (Math.Abs(actual - _appliedZoom) < 0.0005) return;

        if (Config.FitToCell)
        {
            double fit = FitZoom();
            if (fit <= 0) return;
            Config.ZoomScale = Math.Clamp(actual / fit, MinZoom, MaxZoom);
        }
        else
        {
            Config.ZoomFactor = Math.Clamp(actual, MinZoom, MaxZoom);
        }

        // Re-assigned so the factor is the app's own and outlives the page it was made on.
        _appliedZoom = 0;
        BeginInvoke(ApplyZoom);
        StateChanged?.Invoke(this);
        UserZoomed?.Invoke(this);
    }

    /// <summary>Whether the user has zoomed this cell away from where its settings put it.</summary>
    public bool HasUserZoom => Config.Kind == CellKind.Browser && Config.FitToCell
                               && Math.Abs(Config.ZoomScale - 1.0) > 0.001;

    /// <summary>Drop the user's zoom and go back to the plain fit.</summary>
    public void ResetZoom()
    {
        Config.ZoomScale = 1.0;
        _appliedZoom = 0;
        ApplyZoom();
        StateChanged?.Invoke(this);
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
    /// Keep-alive runs in view mode only. While the hosted app has the input the user is
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

    /// <summary>
    /// Bring the cell back to where it was after the site sent it through a login page.
    ///
    /// Only a login page the cell was *sent* to arms the return — a redirect from the server, a
    /// page script, a reload of an expired session. One the user clicked through to is theirs,
    /// and yanking them back from it would be wrong. The return fires once per interruption, so a
    /// login that keeps failing costs a login screen, never a loop.
    /// </summary>
    private async Task CheckLoginAsync(bool userInitiated)
    {
        if (_web is null || !_webReady || !Config.ReturnAfterLogin) return;
        var web = _web;

        bool loginPage = await IsLoginPageAsync(web).ConfigureAwait(true);
        // A page that builds its form from script may not have it yet; look once more.
        if (!loginPage && _returnTo is null)
        {
            await Task.Delay(1500).ConfigureAwait(true);
            loginPage = await IsLoginPageAsync(web).ConfigureAwait(true);
        }
        if (web != _web || !_webReady || _navigating) return;

        string url;
        try { url = web.CoreWebView2.Source; } catch { return; }

        if (loginPage)
        {
            if (_returnTo is null && !userInitiated && !SameAddress(_goodUrl, url))
            {
                _returnTo = _goodUrl;
                Services.HealthMonitor.Event($"셀 {Index + 1} 로그인 화면으로 이동됨 · 로그인 후 복귀 예정");
            }
            return;
        }

        if (_returnTo is { } target)
        {
            _returnTo = null;
            if (!SameAddress(target, url))
            {
                Services.HealthMonitor.Event($"셀 {Index + 1} 로그인 완료 · 원래 화면으로 복귀");
                Navigate(target);
                return;
            }
        }
        _goodUrl = url;
    }

    private static async Task<bool> IsLoginPageAsync(WebView2 web)
    {
        try { return await web.CoreWebView2.ExecuteScriptAsync(LoginPageProbe).ConfigureAwait(true) == "true"; }
        catch { return false; }
    }

    private static bool SameAddress(string a, string b) =>
        string.Equals(NormalizeUrl(a).TrimEnd('/'), NormalizeUrl(b).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Give the profile's session cookies an expiry so they are written to disk and outlive the
    /// browser. Intranet logins are nearly always session cookies, which is the whole reason a
    /// restart lands every cell on a login screen. A converted cookie is no longer a session one,
    /// so each is touched once; one the server re-issues comes back as a session cookie and is
    /// simply converted again. Logging out still works — the server deletes the cookie outright.
    /// </summary>
    private async Task PersistSessionCookiesAsync()
    {
        if (_web is null || !_webReady || !Config.KeepSignedIn) return;
        try
        {
            var manager = _web.CoreWebView2.CookieManager;
            var cookies = await manager.GetCookiesAsync(null).ConfigureAwait(true);
            var until = DateTime.Now + SignedInLifetime;
            foreach (var cookie in cookies)
            {
                if (!cookie.IsSession) continue;
                cookie.Expires = until;
                manager.AddOrUpdateCookie(cookie);
            }
        }
        catch
        {
            // The cell may have been released underneath; the next pass tries again.
        }
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
        // Asleep behind a zoom: leave it. It is refreshed when it is back in view.
        if (_frozen && !Visible) return;
        // A still cell reloads behind its picture and then takes a new one.
        if (_stillWanted && _webReady)
        {
            _ = RefreshStillAsync(reload: true, settleMs: 4000);
            return;
        }
        if (_web is not null && _webReady)
        {
            try { _web.CoreWebView2.Reload(); } catch { }
        }
    }

    /// <summary>
    /// Live or still, as the wall decides: live when zoomed or when the cell is set to stay live,
    /// still otherwise. Only browser cells take part — another process's window cannot be put
    /// to sleep from here, so program cells are always live.
    /// </summary>
    public void SetLive(bool live)
    {
        if (Config.Kind != CellKind.Browser) return;
        if (live)
        {
            if (!_stillWanted && !_frozen) return;
            _stillWanted = false;
            _stillGen++;
            _stillTimer.Stop();
            Thaw();
            return;
        }

        // Relayout calls in here on every resize step; once pictured there is nothing to do. A
        // cell put to sleep behind a zoom before it was ever pictured still needs its picture.
        if (_stillWanted && _frozen && _still.Image is not null) return;
        _stillWanted = true;
        StartStillTimer();
        // Coming back from a zoom: the page has to settle at its new size and zoom before the
        // picture is taken. Before the first load there is nothing to take; that waits for it.
        if (_firstLoadDone) _ = RefreshStillAsync(reload: false, settleMs: 800);
    }

    /// <summary>
    /// Another cell is zoomed and this one is out of sight: sleep, whatever the grid setting.
    /// Nothing here is on screen, so even a live-in-grid cell has no reason to run. No picture
    /// is taken — a hidden page paints nothing — and the grid takes one on the way back if needed.
    /// </summary>
    public void SetHidden()
    {
        if (Config.Kind != CellKind.Browser || _web is null || !_webReady) return;
        _stillGen++;
        if (!_frozen) Freeze();
    }

    private void StartStillTimer()
    {
        if (StillRefreshMinutes <= 0 || _stillTimer.Enabled) return;
        _stillTimer.Interval = (int)TimeSpan.FromMinutes(StillRefreshMinutes).TotalMilliseconds;
        _stillTimer.Start();
    }

    /// <summary>Whether this cell is showing a picture rather than the running page.</summary>
    public bool IsStill => _frozen;

    /// <summary>
    /// Wake the page behind the current picture, optionally reload it, let it settle, take a new
    /// picture and put the page back to sleep. If the capture fails the page is left running —
    /// a live cell is better than a blank one.
    /// </summary>
    private async Task RefreshStillAsync(bool reload, int settleMs)
    {
        if (_web is null || !_webReady) return;
        int gen = ++_stillGen;
        var web = _web;

        // Out of sight (another cell is zoomed): waking it would only run a page nobody sees.
        if (!Visible)
        {
            if (!_frozen) Freeze();
            return;
        }

        // The page has to be on screen to paint; the old picture stays over it meanwhile.
        if (_still.Image is not null)
        {
            _still.Visible = true;
            _still.BringToFront();
            if (_placeholder.Visible) _placeholder.BringToFront();
        }
        Wake();

        try
        {
            if (reload)
            {
                _loadWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                try { web.CoreWebView2.Reload(); } catch { return; }
                await Task.WhenAny(_loadWaiter.Task, Task.Delay(30000)).ConfigureAwait(true);
            }
            await Task.Delay(settleMs).ConfigureAwait(true);
            if (gen != _stillGen || !_stillWanted || web != _web) return;
            // Hidden while it settled: a hidden page paints nothing, and a blank picture would
            // stay up until the next refresh. Back to sleep; the grid pictures it on return.
            if (!Visible)
            {
                Freeze();
                return;
            }

            using var stream = new MemoryStream();
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream)
                .ConfigureAwait(true);
            if (gen != _stillGen || !_stillWanted || web != _web) return;

            stream.Position = 0;
            Bitmap frame;
            using (var decoded = Image.FromStream(stream)) frame = new Bitmap(decoded);
            var old = _still.Image;
            _still.Image = frame;
            old?.Dispose();
            Freeze();
        }
        catch
        {
            // Left running: see the summary.
        }
    }

    private void Freeze()
    {
        if (_web is null) return;
        _still.Visible = true;
        _still.BringToFront();
        if (_placeholder.Visible) _placeholder.BringToFront();
        _web.Visible = false;
        _frozen = true;

        // A keep-alive cell must keep running — its timer is what holds the session — so it is
        // only hidden, which already stops it painting. Everything else sleeps outright.
        if (Config.PreventLogout || _suspended) return;
        try
        {
            _suspended = true;
            _ = _web.CoreWebView2.TrySuspendAsync();
            ApplyMemoryTarget(suspended: true);
        }
        catch { }
    }

    /// <summary>Bring the page back and show it, live.</summary>
    private void Thaw()
    {
        Wake();
        _frozen = false;
        _still.Visible = false;
        if (_web is not null) _web.BringToFront();
        if (_placeholder.Visible) _placeholder.BringToFront();
    }

    private void Wake()
    {
        if (_web is null) return;
        _web.Visible = true;
        if (!_suspended) return;
        _suspended = false;
        try
        {
            _web.CoreWebView2.Resume();
            ApplyMemoryTarget(suspended: false);
        }
        catch { }
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
        // A still cell manages its own sleep; resuming it here would leave it running unseen.
        if (_stillWanted) return;
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
        _cookieTimer.Stop();
        _returnTo = null;
        _stillTimer.Stop();
        _stillGen++;
        // _stillWanted is the wall's wish, not the content's state, so it outlives a restart of
        // the cell: the new page is pictured once it has loaded.
        _frozen = false;
        _firstLoadDone = false;
        _loadWaiter?.TrySetResult(false);
        _loadWaiter = null;
        _still.Visible = false;
        _still.Image?.Dispose();
        _still.Image = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _webReady = false;
        _suspended = false;
        _appliedZoom = 0;
        _navigating = false;
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
            _cookieTimer.Dispose();
            _stillTimer.Dispose();
            _recoverTimer.Dispose();
            _refreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
