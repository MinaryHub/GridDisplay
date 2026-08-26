using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace GridDisplay.Services;

/// <summary>
/// Swaps the installed files for a staged build.
///
/// Windows will not let a running executable overwrite itself, so the new build does the work:
/// <see cref="Launch"/> starts the *staged* GridDisplay.exe with <see cref="ApplySwitch"/> and
/// quits; that second process waits for the first to exit, copies staging over the install
/// folder, starts the installed exe again and ends. Nothing but GridDisplay.exe is involved —
/// no batch file or PowerShell script for antivirus to quarantine, and failures are reported
/// through a log rather than a console nobody sees (the app is a WinExe).
///
/// The staged copy cannot delete the folder it is running from, so cleanup is deferred:
/// <see cref="CleanupStaging"/> runs at every startup and removes what it can.
/// </summary>
public static class UpdateInstaller
{
    private static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GridDisplay");

    public const string ApplySwitch = "--apply-update";
    public const string ExeName = "GridDisplay.exe";

    /// <summary>How long the applier waits for the old process before giving up.</summary>
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where packages are downloaded, unpacked and logged. Owned here rather than in
    /// <see cref="UpdateService"/> on purpose: the applier's whole job is to report what went
    /// wrong, so its log path must not depend on the static initialisation of a type that may
    /// itself be what failed.
    /// </summary>
    public static string UpdatesRoot => Path.Combine(LocalRoot, "Updates");

    private static string LogPath => Path.Combine(UpdatesRoot, "apply.log");

    /// <summary>The folder the running build was installed into.</summary>
    public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Why an in-place update cannot run right now, or null when it can. Guards the two cases
    /// that would otherwise fail halfway: a dev-tree run under `dotnet run`, where there is no
    /// GridDisplay.exe to relaunch, and an install folder the user cannot write to.
    /// </summary>
    public static string? BlockedReason()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !Path.GetFileName(exe).Equals(ExeName, StringComparison.OrdinalIgnoreCase))
            return "개발 중 실행(dotnet run)에서는 업데이트를 적용할 수 없습니다.";

        if (!IsWritable(InstallDirectory))
            return $"설치 폴더에 쓸 권한이 없습니다.\n{InstallDirectory}";

        // Several walls commonly run from one install folder. The applier only waits for the
        // copy that launched it, so the others would still hold GridDisplay.dll open and the
        // update would fail halfway through. Say so before anything is downloaded.
        int siblings = SiblingInstances();
        if (siblings > 0)
            return $"같은 폴더에서 GridDisplay가 {siblings}개 더 실행 중입니다.\n"
                 + "업데이트하려면 나머지를 먼저 종료해 주세요.";

        return null;
    }

    /// <summary>Other GridDisplay processes running out of this same install folder.</summary>
    private static int SiblingInstances()
    {
        try
        {
            int mine = Environment.ProcessId;
            int count = 0;
            foreach (var process in Process.GetProcessesByName("GridDisplay"))
            {
                using (process)
                {
                    if (process.Id == mine) continue;
                    string? path = null;
                    // A process from another session can refuse MainModule; skip rather than fail.
                    try { path = process.MainModule?.FileName; } catch { continue; }
                    if (path is null) continue;
                    if (string.Equals(
                            Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar),
                            InstallDirectory,
                            StringComparison.OrdinalIgnoreCase))
                        count++;
                }
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Hand off to the staged build. The caller must close the form immediately afterwards so
    /// the hooks come down and state is saved before the copy starts.
    /// </summary>
    public static void Launch(string stagingDirectory)
    {
        var stagedExe = Path.Combine(stagingDirectory, ExeName);
        if (!File.Exists(stagedExe))
            throw new FileNotFoundException($"업데이트 파일을 찾을 수 없습니다.\n{stagedExe}");

        var info = new ProcessStartInfo
        {
            FileName = stagedExe,
            WorkingDirectory = stagingDirectory,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(ApplySwitch);
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        info.ArgumentList.Add(InstallDirectory);

        Process.Start(info);
    }

    /// <summary>
    /// The staged build's entry point when started by <see cref="Launch"/>. Runs headless —
    /// no form, no message loop — and must never leave the install folder half-written.
    /// </summary>
    public static void RunApply(string[] args)
    {
        try
        {
            if (args.Length < 3) { Log("적용 인자가 부족합니다."); return; }
            if (!int.TryParse(args[1], out int pid)) { Log($"잘못된 PID: {args[1]}"); return; }

            var target = args[2];
            var source = AppContext.BaseDirectory;

            if (!WaitForExit(pid))
            {
                // Still running means nothing was replaced and the user's wall is intact.
                // Bailing out is safer than forcing the copy against a live process.
                Log($"PID {pid} 이(가) 종료되지 않아 업데이트를 적용하지 않았습니다.");
                return;
            }

            CopyOver(source, target);

            var installedExe = Path.Combine(target, ExeName);
            Process.Start(new ProcessStartInfo
            {
                FileName = installedExe,
                WorkingDirectory = target,
                UseShellExecute = false,
            });

            Log($"업데이트 적용 완료: {source} -> {target}");
        }
        catch (Exception ex)
        {
            Log("업데이트 적용 실패: " + ex);
            // The old build is still installed and still launchable; say so rather than
            // leaving the user staring at an app that silently never came back.
            try
            {
                MessageBox.Show(
                    "업데이트를 적용하지 못했습니다. 이전 버전은 그대로 사용할 수 있습니다.\n\n"
                    + ex.Message + "\n\n기록: " + LogPath,
                    "Grid Display 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }
    }

    /// <summary>
    /// Fetch a package zip, verify it against <paramref name="expectedSha256"/> and unpack it.
    /// Returns the folder holding the new build, ready for <see cref="Launch"/>.
    ///
    /// Refuses a mismatched hash and a package without GridDisplay.exe at its root: an install
    /// folder must never be left unbootable, and this is the only thing standing between a
    /// tampered download and the running application.
    /// </summary>
    public static async Task<string> StageZipAsync(
        HttpClient http, string url, string expectedSha256, IProgress<int>? progress, CancellationToken ct)
    {
        var versionDir = Path.Combine(UpdatesRoot, "pending");
        var filesDir = Path.Combine(versionDir, "files");
        var zipPath = Path.Combine(versionDir, "package.zip");

        // A half-finished attempt from an earlier run must never be mistaken for a good package.
        TryDeleteDirectory(versionDir);
        Directory.CreateDirectory(versionDir);

        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1L;
            long read = 0;
            int lastPercent = -1;

            using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var target = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (progress is null || total <= 0) continue;

                // Whole percents only: every report is marshalled onto the message loop.
                int percent = (int)(read * 100 / total);
                if (percent == lastPercent) continue;
                lastPercent = percent;
                progress.Report(percent);
            }
        }

        string actual;
        using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));

        if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteDirectory(versionDir);
            throw new InvalidDataException(
                $"내려받은 파일이 손상되었습니다.\n기대: {expectedSha256.Trim()}\n실제: {actual}");
        }

        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, filesDir, overwriteFiles: true);
        try { File.Delete(zipPath); } catch { }

        if (!File.Exists(Path.Combine(filesDir, ExeName)))
        {
            TryDeleteDirectory(versionDir);
            throw new InvalidDataException($"업데이트 패키지에 {ExeName} 이(가) 없습니다.");
        }

        return filesDir;
    }

    /// <summary>Remove staging folders left behind by earlier updates. Best effort, by design.</summary>
    public static void CleanupStaging()
    {
        try
        {
            if (!Directory.Exists(UpdatesRoot)) return;

            // The applier is still exiting out of one of these when the new build starts, so a
            // delete can fail. Whatever survives is picked up by the next launch.
            foreach (var dir in Directory.GetDirectories(UpdatesRoot))
                TryDeleteDirectory(dir);
        }
        catch { }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    private static bool WaitForExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit((int)ExitTimeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            return true; // Already gone.
        }
    }

    /// <summary>
    /// Copy staging over the install folder. Extra files already in the target are left alone —
    /// a wall may have logs or shortcuts beside the binaries that an update has no business
    /// deleting.
    /// </summary>
    private static void CopyOver(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyWithRetry(file, destination);
        }
    }

    /// <summary>
    /// A file can stay locked for a moment after the old process dies — WebView2Loader.dll in
    /// particular outlives its host briefly. Retry before declaring the update failed.
    /// </summary>
    private static void CopyWithRetry(string source, string destination)
    {
        const int attempts = 5;
        for (int i = 1; ; i++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (i < attempts)
            {
                Thread.Sleep(400);
            }
            catch (UnauthorizedAccessException) when (i < attempts)
            {
                Thread.Sleep(400);
            }
        }
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".update-probe");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(UpdatesRoot);
            File.AppendAllText(
                LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch { }
    }
}
