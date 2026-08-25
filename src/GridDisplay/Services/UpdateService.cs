using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using GridDisplay.Models;
using Windows.Management.Deployment;

namespace GridDisplay.Services;

public enum UpdateStatus
{
    /// <summary>Nothing to ask: the build has no package identity, so it cannot replace itself.</summary>
    NotSupported,
    /// <summary>Installed from Microsoft Store, which owns updating it. Nothing for the app to do.</summary>
    ManagedByStore,
    /// <summary>No feed URL, and the install source did not carry one either.</summary>
    NotConfigured,
    UpToDate,
    Available,
    Failed,
}

/// <summary>Outcome of one check. Immutable so it can be handed between threads and cached.</summary>
public sealed record UpdateCheckResult(UpdateStatus Status, Version? Latest, string? Source, string Message)
{
    public bool HasUpdate => Status == UpdateStatus.Available;

    /// <summary>
    /// Hex SHA-256 of the package, when the feed carries one. Only the unpackaged path uses it:
    /// MSIX verifies its own signature, but a zip replacing the install folder has nothing
    /// checking it but us.
    /// </summary>
    public string? Sha256 { get; init; }
}

public enum UpdateApplyOutcome
{
    /// <summary>Downloaded and registered; the new version starts with the next launch.</summary>
    StagedForNextLaunch,
    /// <summary>Windows is shutting this process down to finish the install.</summary>
    Restarting,
    /// <summary>Unpackaged build: the installer was handed to the shell for the user to run.</summary>
    HandedToUser,
    /// <summary>Store install: the Store app was opened on its updates page.</summary>
    OpenedStore,
    Failed,
}

public sealed record UpdateApplyResult(UpdateApplyOutcome Outcome, string Message);

/// <summary>
/// Version checking and self-update over MSIX.
///
/// Four sources of truth, tried in that order, because a wall can be deployed four ways:
///   0. installed from Microsoft Store — the Store updates it, and Store Policy 10.2 reserves that
///      job for the Store, so this class checks nothing and installs nothing. Leaving the
///      self-update path reachable in a Store build is a certification failure, not merely
///      redundant work;
///   1. an explicit feed URL in <see cref="AppState.UpdateFeedUrl"/> — an .appinstaller file or a
///      small update.json. Parsing the feed ourselves is what makes the version *number* known,
///      which the OS check below never reveals;
///   2. no URL but MSIX identity — ask Windows, which remembers the .appinstaller the package was
///      installed from. Zero configuration, but answers only "newer / not newer";
///   3. neither — the feature stays dormant and the UI says why.
///
/// Nothing here ever throws at the caller. A display wall that dies because a web request failed
/// is a worse outcome than a wall running last week's build, so every path returns a result
/// object carrying a message meant for the update dialog.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Application Id from the package manifest; keep in sync with packaging\AppxManifest.xml.</summary>
    private const string AppId = "GridDisplay";

    private static readonly HttpClient Http = CreateClient();

    private readonly AppState _state;

    public UpdateService(AppState state) => _state = state;

    /// <summary>Result of the last check, so the toolbar and menu can reflect it without re-fetching.</summary>
    public UpdateCheckResult? Last { get; private set; }

    /// <summary>True when enough time has passed for the periodic check to be worth running.</summary>
    public bool IsCheckDue()
    {
        // A Store install is updated by the Store, in the background, whether the app is running or
        // not. Polling would only raise a banner about something the user cannot act on here.
        if (AppVersion.IsStoreInstall) return false;
        if (!_state.AutoCheckUpdates) return false;
        var last = _state.LastUpdateCheckUtc;
        if (last is null) return true;
        int hours = Math.Clamp(_state.UpdateCheckHours, 1, 24 * 7);
        return DateTime.UtcNow - last.Value >= TimeSpan.FromHours(hours);
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var result = await RunCheckAsync(ct).ConfigureAwait(true);
        // Only a check that actually reached a source counts as one; otherwise a misconfigured
        // wall would stop retrying for a whole interval after a single network hiccup.
        if (result.Status is UpdateStatus.UpToDate or UpdateStatus.Available)
            _state.LastUpdateCheckUtc = DateTime.UtcNow;
        Last = result;
        return result;
    }

    private async Task<UpdateCheckResult> RunCheckAsync(CancellationToken ct)
    {
        if (AppVersion.IsStoreInstall) return ManagedByStore();

        var feed = (_state.UpdateFeedUrl ?? string.Empty).Trim();
        if (feed.Length > 0) return await CheckFeedAsync(feed, ct).ConfigureAwait(true);
        if (AppVersion.IsPackaged) return await CheckWithWindowsAsync().ConfigureAwait(true);
        return new UpdateCheckResult(UpdateStatus.NotConfigured, null, null,
            "업데이트 주소가 설정되지 않았습니다. .appinstaller 또는 update.json 주소를 입력하세요.");
    }

    // ------------------------------------------------------------------ Store

    /// <summary>
    /// Deep link to the Store's own "downloads and updates" page. Deliberately not a product page:
    /// that needs the Store ID, which only exists once the app has been published, whereas this URI
    /// is correct from the first submission onwards and lands the user exactly where the update is.
    /// </summary>
    private const string StoreUpdatesUri = "ms-windows-store://downloadsandupdates";

    private static UpdateCheckResult ManagedByStore() =>
        new(UpdateStatus.ManagedByStore, null, null,
            $"Microsoft Store가 자동으로 업데이트합니다 (현재 {AppVersion.Display}).");

    /// <summary>Open the Store on its updates page. The only update action a Store build offers.</summary>
    public static UpdateApplyResult OpenStoreUpdates()
    {
        try
        {
            Process.Start(new ProcessStartInfo(StoreUpdatesUri) { UseShellExecute = true });
            return new UpdateApplyResult(UpdateApplyOutcome.OpenedStore,
                "Microsoft Store를 열었습니다. '업데이트 가져오기'를 누르면 새 버전이 설치됩니다.");
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(UpdateApplyOutcome.Failed, $"Microsoft Store를 열 수 없습니다: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ feed

    private async Task<UpdateCheckResult> CheckFeedAsync(string feedUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri))
            return new UpdateCheckResult(UpdateStatus.Failed, null, null, $"업데이트 주소가 올바르지 않습니다: {feedUrl}");

        string body;
        try
        {
            body = await Http.GetStringAsync(uri, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateStatus.Failed, null, null, $"업데이트 정보를 가져올 수 없습니다: {ex.Message}");
        }

        try
        {
            var (latest, source, sha256) = body.TrimStart().StartsWith('<')
                ? ParseAppInstaller(body, uri)
                : ParseJsonFeed(body, uri);

            if (latest is null)
                return new UpdateCheckResult(UpdateStatus.Failed, null, null, "업데이트 정보에서 버전을 찾을 수 없습니다.");

            return Compare(latest, source) with { Sha256 = sha256 };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateStatus.Failed, null, null, $"업데이트 정보를 해석할 수 없습니다: {ex.Message}");
        }
    }

    /// <summary>
    /// Read version + package location out of an .appinstaller file. Elements are matched by local
    /// name, so a feed authored against any of the several AppInstaller XML namespaces still
    /// parses — that namespace changes with the Windows release the file was authored for.
    /// </summary>
    private static (Version? Latest, string Source, string? Sha256) ParseAppInstaller(string xml, Uri feedUri)
    {
        var root = XDocument.Parse(xml).Root ?? throw new InvalidDataException("빈 .appinstaller 파일입니다.");

        var main = root.Elements().FirstOrDefault(e => e.Name.LocalName is "MainPackage" or "MainBundle");
        var version = ParseVersion(main?.Attribute("Version")?.Value) ?? ParseVersion(root.Attribute("Version")?.Value);

        // Prefer the feed's own declared Uri: that is the address Windows has to be handed to
        // install from, and it stays right even when the file was fetched through a mirror.
        var declared = root.Attribute("Uri")?.Value;
        var source = string.IsNullOrWhiteSpace(declared) ? feedUri.ToString() : declared!;
        // An .appinstaller carries no hash: MSIX is signed, and Windows checks the signature.
        return (version, source, null);
    }

    private static (Version? Latest, string Source, string? Sha256) ParseJsonFeed(string json, Uri feedUri)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = ParseVersion(Text(root, "version") ?? Text(root, "latestVersion"));
        var source = Text(root, "appInstallerUrl") ?? Text(root, "url") ?? Text(root, "packageUrl") ?? feedUri.ToString();
        return (version, source, Text(root, "sha256"));

        static string? Text(JsonElement obj, string name) =>
            obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }

    private static UpdateCheckResult Compare(Version latest, string source)
    {
        if (Normalize(latest) <= Normalize(AppVersion.Current))
            return new UpdateCheckResult(UpdateStatus.UpToDate, latest, source,
                $"최신 버전입니다 (현재 {AppVersion.Display}).");

        return new UpdateCheckResult(UpdateStatus.Available, latest, source,
            $"새 버전 {Short(latest)}을(를) 설치할 수 있습니다 (현재 {AppVersion.Display}).");
    }

    // ------------------------------------------------------------------ Windows-side check

    private static async Task<UpdateCheckResult> CheckWithWindowsAsync()
    {
        try
        {
            var result = await Windows.ApplicationModel.Package.Current.CheckUpdateAvailabilityAsync();
            return result.Availability switch
            {
                Windows.ApplicationModel.PackageUpdateAvailability.Available or Windows.ApplicationModel.PackageUpdateAvailability.Required =>
                    new UpdateCheckResult(UpdateStatus.Available, null, null,
                        "새 버전이 있습니다. 설치 원본에서 업데이트할 수 있습니다."),
                Windows.ApplicationModel.PackageUpdateAvailability.NoUpdates =>
                    new UpdateCheckResult(UpdateStatus.UpToDate, null, null, $"최신 버전입니다 (현재 {AppVersion.Display})."),
                _ => new UpdateCheckResult(UpdateStatus.NotConfigured, null, null,
                    "설치 원본에 업데이트 정보가 없습니다. .appinstaller 주소를 입력하세요."),
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateStatus.Failed, null, null, $"업데이트 확인에 실패했습니다: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// Install the update. <paramref name="restartNow"/> picks between the only two honest options
    /// an MSIX self-update has: replace the package while nothing holds its files open — which
    /// means shutting this process down — or register it and let the next launch pick it up. A
    /// wall on a lobby screen usually wants the second, so that is what the dialog offers first.
    /// </summary>
    public async Task<UpdateApplyResult> ApplyAsync(UpdateCheckResult check, bool restartNow, IProgress<int>? progress = null)
    {
        // Store build: hand the job to the Store instead of staging a package ourselves. This is
        // the guard that keeps PackageManager out of a Store submission's reachable code paths.
        if (AppVersion.IsStoreInstall) return OpenStoreUpdates();

        var source = (check.Source ?? _state.UpdateFeedUrl ?? string.Empty).Trim();

        if (!AppVersion.IsPackaged)
        {
            if (source.Length == 0)
                return new UpdateApplyResult(UpdateApplyOutcome.Failed, "설치 파일 주소를 알 수 없습니다.");

            // A zip with a hash beside it is the Inno-installed shape, and that one this build can
            // finish by itself: download, verify, and let UpdateInstaller swap the install folder.
            // MSIX cannot be updated this way — its install folder is read-only — which is why the
            // two paths part here rather than earlier.
            if (source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(check.Sha256))
                return await ApplyUnpackagedZipAsync(source, check.Sha256!, progress).ConfigureAwait(true);

            // Anything else — an .appinstaller or a bare .msix — needs Windows to install it.
            // ms-appinstaller: is disabled by default on current Windows, so hand the plain URL to
            // the shell and let the browser download it.
            try
            {
                Process.Start(new ProcessStartInfo(source) { UseShellExecute = true });
                return new UpdateApplyResult(UpdateApplyOutcome.HandedToUser,
                    "설치 파일을 열었습니다. 내려받은 .appinstaller 또는 .msix 파일을 실행해 업데이트하세요.");
            }
            catch (Exception ex)
            {
                return new UpdateApplyResult(UpdateApplyOutcome.Failed, $"설치 파일을 열 수 없습니다: {ex.Message}");
            }
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
            return new UpdateApplyResult(UpdateApplyOutcome.Failed, "업데이트 주소가 올바르지 않습니다.");

        try
        {
            var manager = new PackageManager();
            var options = restartNow
                ? AddPackageByAppInstallerOptions.ForceTargetAppShutdown
                : AddPackageByAppInstallerOptions.None;

            // Queued before the call: with ForceTargetAppShutdown, Windows terminates this process
            // as part of the install, so nothing after the await is guaranteed to run.
            if (restartNow) ScheduleRelaunch();

            var operation = manager.AddPackageByAppInstallerFileAsync(uri, options, null);
            operation.Progress = (_, p) => progress?.Report((int)Math.Clamp(p.percentage, 0, 100));
            var result = await operation;

            if (result.ExtendedErrorCode is not null)
                return new UpdateApplyResult(UpdateApplyOutcome.Failed, DescribeFailure(result));

            return restartNow
                ? new UpdateApplyResult(UpdateApplyOutcome.Restarting, "업데이트를 설치했습니다. 앱이 다시 시작됩니다.")
                : new UpdateApplyResult(UpdateApplyOutcome.StagedForNextLaunch,
                    "업데이트를 설치했습니다. 다음 실행 때 새 버전으로 시작합니다.");
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(UpdateApplyOutcome.Failed, $"업데이트 설치에 실패했습니다: {ex.Message}");
        }
    }

    private static string DescribeFailure(DeploymentResult result)
    {
        uint hr = unchecked((uint)(result.ExtendedErrorCode?.HResult ?? 0));
        var detail = string.IsNullOrWhiteSpace(result.ErrorText) ? result.ExtendedErrorCode?.Message : result.ErrorText;
        // 0x80073D02 — the package's files are in use. The ordinary outcome of a self-update, and
        // the one case where the user needs to be told which button to press instead.
        if (hr == 0x80073D02)
            return "실행 중인 파일 때문에 지금 설치할 수 없습니다. '설치 후 다시 시작'을 사용하세요.";
        return $"업데이트 설치에 실패했습니다 (0x{hr:X8}): {detail}";
    }

    /// <summary>
    /// Bring the wall back after a shutdown-forcing install. Windows kills the process rather than
    /// restarting it, and an unattended screen that stays black until somebody walks over to it is
    /// the failure this avoids. A detached shell waits out the deployment and then launches the app
    /// by its AUMID — the packaged equivalent of running the exe.
    /// </summary>
    private static void ScheduleRelaunch()
    {
        if (string.IsNullOrEmpty(AppVersion.PackageFamilyName)) return;
        try
        {
            var aumid = $"{AppVersion.PackageFamilyName}!{AppId}";
            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                // ping, not timeout: timeout wants a console input handle and fails when detached.
                Arguments = $"/c ping -n 30 127.0.0.1 >nul & explorer.exe \"shell:appsFolder\\{aumid}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch
        {
            // Best effort. Failing to queue the relaunch must not abort the update itself.
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Update a copy that was installed from the Inno Setup package: fetch the zip, check it
    /// against the feed's hash, unpack it beside the install folder and hand over to
    /// <see cref="UpdateInstaller"/>, which waits for this process to die before swapping files.
    ///
    /// The hash check is not optional here. An MSIX is signed and Windows refuses a package that
    /// fails verification; a zip that overwrites GridDisplay.exe has nothing between it and the
    /// install folder but this comparison.
    /// </summary>
    private async Task<UpdateApplyResult> ApplyUnpackagedZipAsync(
        string url, string expectedSha256, IProgress<int>? progress)
    {
        var blocked = UpdateInstaller.BlockedReason();
        if (blocked is not null) return new UpdateApplyResult(UpdateApplyOutcome.Failed, blocked);

        string staging;
        try
        {
            staging = await UpdateInstaller
                .StageZipAsync(Http, url, expectedSha256, progress, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(UpdateApplyOutcome.Failed, $"업데이트를 내려받지 못했습니다: {ex.Message}");
        }

        try
        {
            UpdateInstaller.Launch(staging);
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(UpdateApplyOutcome.Failed, $"업데이트를 시작하지 못했습니다: {ex.Message}");
        }

        return new UpdateApplyResult(UpdateApplyOutcome.Restarting,
            "업데이트를 적용합니다. 프로그램이 잠시 후 다시 시작됩니다.");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"GridDisplay/{AppVersion.Display}");
        // Feeds live on intranet shares and static hosts that will happily serve a cached copy.
        client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return client;
    }

    public static Version? ParseVersion(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        // Accept "1.2", "1.2.3" and "1.2.3.4"; Version.TryParse rejects a bare "1".
        if (!text.Contains('.')) text += ".0";
        return Version.TryParse(text, out var v) ? v : null;
    }

    /// <summary>Pad to four parts, so 1.2.3 and 1.2.3.0 compare equal instead of -1 and 0.</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public static string Short(Version v) =>
        v.Revision > 0
            ? v.ToString(4)
            : string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}");
}
