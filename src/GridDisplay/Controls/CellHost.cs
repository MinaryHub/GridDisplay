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
    private readonly Label _placeholder;
    private WebView2? _web;
    private NativeAppHost? _native;
    private CancellationTokenSource? _cts;
    private bool _webReady;
    private bool _suspended;

    public CellHost(int index, CellConfig config)
    {
        Index = index;
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
    }

    public int Index { get; }

    public CellConfig Config { get; private set; }

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
                StatusText = $"[{Index + 1}] 비어 있음\n클릭하여 설정";
                break;
        }
    }

    private async Task StartBrowserAsync(CellConfig config, CancellationToken ct)
    {
        var view = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(24, 24, 27),
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = StateStore.WebViewDataDirectory,
                // Per-cell profiles let two cells stay signed in to the same site as different users.
                ProfileName = string.IsNullOrWhiteSpace(config.ProfileName) ? null : config.ProfileName,
            },
        };
        _web = view;
        Controls.Add(view);
        view.BringToFront();

        try
        {
            await view.EnsureCoreWebView2Async().ConfigureAwait(true);
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
            if (Config.Url == source) return;
            Config.Url = source;
            StateChanged?.Invoke(this);
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            LiveTitle = core.DocumentTitle;
            StateChanged?.Invoke(this);
        };

        core.ProcessFailed += (_, e) =>
        {
            StatusText = $"브라우저 프로세스 오류: {e.ProcessFailedKind}";
            _placeholder.BringToFront();
        };

        _webReady = true;
        try { view.ZoomFactor = config.ZoomFactor <= 0 ? 1.0 : config.ZoomFactor; } catch { }

        Navigate(config.Url);
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
    public void SetSuspended(bool suspend)
    {
        if (_web is null || !_webReady || suspend == _suspended) return;
        _suspended = suspend;
        try
        {
            if (suspend) _ = _web.CoreWebView2.TrySuspendAsync();
            else _web.CoreWebView2.Resume();
        }
        catch { }
    }

    public void Release()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _webReady = false;
        _suspended = false;
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
        if (disposing) Release();
        base.Dispose(disposing);
    }
}
